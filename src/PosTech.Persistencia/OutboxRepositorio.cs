using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using PosTech.Mensageria;

namespace PosTech.Persistencia;

/// <summary>
/// Outbox em Azure SQL, com Dapper. Mesmo estilo das queries da Fase 2: SQL
/// escrito à mão, numa constante, longe da regra de negócio.
/// </summary>
public sealed class OutboxRepositorio : IRepositorioDeOutbox
{
    /// <summary>
    /// Reserva o lote e devolve o que reservou, numa operação só.
    ///
    /// Com duas ou três réplicas no AKS, todas rodam o processador ao mesmo
    /// tempo e sem reserva as três publicariam o mesmo evento. O consumidor
    /// deduplicaria, mas o tráfego e o log triplicariam à toa.
    ///
    /// A reserva é um UPDATE com OUTPUT, e não um SELECT com UPDLOCK: o lock de
    /// leitura morre no commit da transação, que acontece ANTES da publicação —
    /// ele não protegeria nada. Empurrar ProximaTentativaEm para a frente é uma
    /// trava que sobrevive ao commit, e que se solta sozinha: se o processo
    /// morrer no meio da publicação, o prazo vence e outra réplica reprocessa.
    /// </summary>
    private const string RESERVAR_PENDENTES = @"
        UPDATE TOP (@Limite) EventoOutbox WITH (READPAST)
        SET ProximaTentativaEm = DATEADD(SECOND, @ReservaEmSegundos, SYSUTCDATETIME())
        OUTPUT inserted.Id, inserted.MensagemId, inserted.Tipo, inserted.RoutingKey,
               inserted.Payload, inserted.CorrelationId, inserted.Status,
               inserted.Tentativas, inserted.CriadoEm, inserted.PublicadoEm,
               inserted.ProximaTentativaEm, inserted.UltimoErro
        WHERE Status = 0 AND ProximaTentativaEm <= SYSUTCDATETIME()";

    private const string INSERIR = @"
        INSERT INTO EventoOutbox
            (MensagemId, Tipo, RoutingKey, Payload, CorrelationId, Status, Tentativas, CriadoEm, ProximaTentativaEm)
        VALUES
            (@MensagemId, @Tipo, @RoutingKey, @Payload, @CorrelationId, @Status, @Tentativas, @CriadoEm, @ProximaTentativaEm);
        SELECT CAST(SCOPE_IDENTITY() AS BIGINT);";

    private const string MARCAR_PUBLICADO = @"
        UPDATE EventoOutbox
        SET Status = 1, PublicadoEm = @PublicadoEm
        WHERE MensagemId = @MensagemId AND Status = 0";

    private const string REGISTRAR_FALHA = @"
        UPDATE EventoOutbox
        SET Tentativas = @Tentativas,
            ProximaTentativaEm = @ProximaTentativaEm,
            UltimoErro = @UltimoErro,
            Status = @Status
        WHERE MensagemId = @MensagemId";

    private readonly string _connectionString;

    public OutboxRepositorio(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    /// <summary>
    /// Grava o evento. A conexão e a transação vêm de fora porque é o caso de
    /// uso que manda: a linha do outbox tem que entrar na MESMA transação da
    /// regra de negócio, senão o padrão não vale nada.
    /// </summary>
    public static async Task<long> InserirAsync(
        IDbConnection conexao, IDbTransaction transacao, EventoOutbox evento, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conexao);
        ArgumentNullException.ThrowIfNull(evento);

        var comando = new CommandDefinition(
            INSERIR,
            new
            {
                evento.MensagemId,
                evento.Tipo,
                evento.RoutingKey,
                evento.Payload,
                evento.CorrelationId,
                Status = (byte)evento.Status,
                evento.Tentativas,
                evento.CriadoEm,
                evento.ProximaTentativaEm,
            },
            transaction: transacao,
            cancellationToken: cancellationToken);

        return await conexao.ExecuteScalarAsync<long>(comando).ConfigureAwait(false);
    }

    /// <summary>
    /// Quanto tempo uma réplica tem para publicar o que reservou antes de o
    /// evento voltar para a fila. Generoso o bastante para um broker lento,
    /// curto o bastante para a recuperação não demorar quando um pod morre.
    /// </summary>
    public const int ReservaEmSegundos = 30;

    public async Task<IReadOnlyList<EventoOutbox>> ObterPendentesAsync(int limite, CancellationToken cancellationToken = default)
    {
        await using var conexao = new SqlConnection(_connectionString);

        var comando = new CommandDefinition(
            RESERVAR_PENDENTES,
            new { Limite = limite, ReservaEmSegundos },
            cancellationToken: cancellationToken);

        var linhas = (await conexao.QueryAsync<EventoOutbox>(comando).ConfigureAwait(false)).ToList();

        return linhas;
    }

    public async Task MarcarPublicadoAsync(Guid mensagemId, DateTime publicadoEm, CancellationToken cancellationToken = default)
    {
        await using var conexao = new SqlConnection(_connectionString);

        var comando = new CommandDefinition(
            MARCAR_PUBLICADO, new { MensagemId = mensagemId, PublicadoEm = publicadoEm }, cancellationToken: cancellationToken);

        await conexao.ExecuteAsync(comando).ConfigureAwait(false);
    }

    public async Task RegistrarFalhaAsync(
        Guid mensagemId, int tentativas, DateTime proximaTentativaEm, string erro, EStatusOutbox status,
        CancellationToken cancellationToken = default)
    {
        await using var conexao = new SqlConnection(_connectionString);

        var comando = new CommandDefinition(
            REGISTRAR_FALHA,
            new { MensagemId = mensagemId, Tentativas = tentativas, ProximaTentativaEm = proximaTentativaEm, UltimoErro = erro, Status = (byte)status },
            cancellationToken: cancellationToken);

        await conexao.ExecuteAsync(comando).ConfigureAwait(false);
    }
}

/// <summary>
/// Registro de idempotência em Azure SQL.
/// </summary>
public sealed class IdempotenciaRepositorio : IRegistroDeIdempotencia
{
    private const string JA_PROCESSADO = @"
        SELECT COUNT(1) FROM EventoProcessado
        WHERE MensagemId = @MensagemId AND Consumidor = @Consumidor";

    /// <summary>
    /// A inserção ignora a duplicata em vez de estourar.
    ///
    /// Duas réplicas podem processar a mesma mensagem ao mesmo tempo, passar
    /// pela checagem juntas e tentar gravar as duas. Uma violação de chave aqui
    /// faria o consumidor mandar para a dead letter uma mensagem que foi
    /// processada com sucesso — o pior resultado possível.
    /// </summary>
    private const string REGISTRAR = @"
        IF NOT EXISTS (SELECT 1 FROM EventoProcessado WHERE MensagemId = @MensagemId AND Consumidor = @Consumidor)
            INSERT INTO EventoProcessado (MensagemId, Consumidor, Tipo, ProcessadoEm)
            VALUES (@MensagemId, @Consumidor, @Tipo, @ProcessadoEm);";

    private readonly string _connectionString;

    public IdempotenciaRepositorio(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task<bool> JaProcessadoAsync(Guid mensagemId, string consumidor, CancellationToken cancellationToken = default)
    {
        await using var conexao = new SqlConnection(_connectionString);

        var comando = new CommandDefinition(
            JA_PROCESSADO, new { MensagemId = mensagemId, Consumidor = consumidor }, cancellationToken: cancellationToken);

        return await conexao.ExecuteScalarAsync<int>(comando).ConfigureAwait(false) > 0;
    }

    public async Task RegistrarAsync(Guid mensagemId, string tipo, string consumidor, DateTime quando, CancellationToken cancellationToken = default)
    {
        await using var conexao = new SqlConnection(_connectionString);

        var comando = new CommandDefinition(
            REGISTRAR,
            new { MensagemId = mensagemId, Consumidor = consumidor, Tipo = tipo, ProcessadoEm = quando },
            cancellationToken: cancellationToken);

        await conexao.ExecuteAsync(comando).ConfigureAwait(false);
    }
}
