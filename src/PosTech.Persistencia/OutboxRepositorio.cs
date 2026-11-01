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
    /// A leitura do lote usa UPDLOCK e READPAST de propósito.
    ///
    /// Com duas ou três réplicas do serviço no AKS, todas rodam o processador do
    /// outbox ao mesmo tempo. Sem isso, as três leem as mesmas linhas e publicam
    /// o mesmo evento três vezes — o consumidor deduplicaria, mas o tráfego e o
    /// log triplicariam sem motivo. UPDLOCK trava as linhas lidas; READPAST faz
    /// as outras réplicas pularem o que já está travado em vez de esperar.
    /// </summary>
    private const string OBTER_PENDENTES = @"
        SELECT TOP (@Limite)
               Id, MensagemId, Tipo, RoutingKey, Payload, CorrelationId,
               Status, Tentativas, CriadoEm, PublicadoEm, ProximaTentativaEm, UltimoErro
        FROM EventoOutbox WITH (UPDLOCK, READPAST)
        WHERE Status = 0 AND ProximaTentativaEm <= SYSUTCDATETIME()
        ORDER BY Id";

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

    public async Task<IReadOnlyList<EventoOutbox>> ObterPendentesAsync(int limite, CancellationToken cancellationToken = default)
    {
        await using var conexao = new SqlConnection(_connectionString);
        await conexao.OpenAsync(cancellationToken).ConfigureAwait(false);

        // A transação fecha junto com o using: os locks de UPDLOCK só valem
        // enquanto ela estiver aberta, e segurá-los durante a publicação
        // deixaria a tabela travada o tempo da rede.
        await using var transacao = (SqlTransaction)await conexao.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var comando = new CommandDefinition(
            OBTER_PENDENTES, new { Limite = limite }, transaction: transacao, cancellationToken: cancellationToken);

        var linhas = (await conexao.QueryAsync<EventoOutbox>(comando).ConfigureAwait(false)).ToList();

        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);

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
