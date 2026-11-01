using Dapper;
using Microsoft.Data.SqlClient;
using PosTech.Mensageria;
using PosTech.Saga;

namespace PosTech.Persistencia;

public sealed record OrdemResumida(
    int Id,
    int IdCliente,
    int IdVeiculo,
    int IdFuncionario,
    EStatusOrdem Status,
    string? NomeCliente,
    string? DescricaoVeiculo,
    DateTime CriadoEm,
    DateTime? AtualizadoEm);

public sealed record MudancaDeStatus(
    EStatusOrdem? StatusAnterior,
    EStatusOrdem StatusNovo,
    string? Motivo,
    DateTime OcorridoEm);

/// <summary>
/// As linhas como o banco devolve, antes de virarem domínio.
///
/// Dapper materializa record posicional casando o construtor por tipo, e
/// TINYINT chega como <see cref="byte"/> — não como o enum. Sem este
/// intermediário, toda leitura de ordem estoura em runtime com
/// "a parameterless default constructor ... is required". É o mesmo padrão
/// que <c>SagaRepositorio</c> já usa.
/// </summary>
internal sealed class LinhaDeOrdem
{
    public int Id { get; set; }
    public int IdCliente { get; set; }
    public int IdVeiculo { get; set; }
    public int IdFuncionario { get; set; }
    public byte Status { get; set; }
    public string? NomeCliente { get; set; }
    public string? DescricaoVeiculo { get; set; }
    public DateTime CriadoEm { get; set; }
    public DateTime? AtualizadoEm { get; set; }

    public OrdemResumida Para() => new(
        Id, IdCliente, IdVeiculo, IdFuncionario, (EStatusOrdem)Status,
        NomeCliente, DescricaoVeiculo, CriadoEm, AtualizadoEm);
}

internal sealed class LinhaDeHistorico
{
    public byte? StatusAnterior { get; set; }
    public byte StatusNovo { get; set; }
    public string? Motivo { get; set; }
    public DateTime OcorridoEm { get; set; }

    public MudancaDeStatus Para() => new(
        (EStatusOrdem?)StatusAnterior, (EStatusOrdem)StatusNovo, Motivo, OcorridoEm);
}

public interface IOrdemRepositorio
{
    /// <summary>
    /// Cria a ordem, o estado inicial da saga e o evento <c>OrdemAberta</c>
    /// numa transação só.
    ///
    /// Os três juntos, e não em três chamadas: se a ordem nascesse sem saga, o
    /// orquestrador nunca a veria; se nascesse sem o evento, nenhum outro
    /// serviço ficaria sabendo dela. Devolve o id gerado.
    /// </summary>
    Task<int> AbrirAsync(
        int idCliente, int idVeiculo, int idFuncionario,
        string nomeCliente, string descricaoVeiculo,
        Func<int, EventoOutbox> montarEvento,
        DateTime agora,
        CancellationToken cancellationToken = default);

    Task<OrdemResumida?> ObterAsync(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrdemResumida>> ListarAsync(
        int? idCliente, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MudancaDeStatus>> HistoricoAsync(int idOrdem, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aplica a decisão da saga: muda o status da ordem, registra o histórico,
    /// grava o estado da saga e enfileira os eventos. Tudo numa transação.
    /// </summary>
    Task AplicarDecisaoAsync(
        EstadoDaSaga estado,
        EStatusOrdem? statusAnterior,
        string? motivo,
        IReadOnlyList<EventoOutbox> eventos,
        CancellationToken cancellationToken = default);
}

public sealed class OrdemRepositorio : IOrdemRepositorio
{
    private const string ABRIR = @"
        INSERT INTO OrdemServico
            (IdCliente, IdVeiculo, IdFuncionario, Status, NomeCliente, DescricaoVeiculo, CriadoEm)
        VALUES
            (@IdCliente, @IdVeiculo, @IdFuncionario, 0, @NomeCliente, @DescricaoVeiculo, @Agora);
        SELECT CAST(SCOPE_IDENTITY() AS INT);";

    private const string CRIAR_SAGA = @"
        INSERT INTO SagaOrdemServico (IdOrdem, Passo, StatusDaOrdem, AtualizadoEm)
        VALUES (@IdOrdem, 0, 0, @Agora);";

    private const string REGISTRAR_HISTORICO = @"
        INSERT INTO StatusHistorico (IdOrdem, StatusAnterior, StatusNovo, Motivo, OcorridoEm)
        VALUES (@IdOrdem, @StatusAnterior, @StatusNovo, @Motivo, @OcorridoEm);";

    private const string ATUALIZAR_STATUS = @"
        UPDATE OrdemServico SET Status = @Status, AtualizadoEm = @Agora WHERE Id = @IdOrdem;";

    private const string SALVAR_SAGA = @"
        UPDATE SagaOrdemServico
        SET Passo = @Passo, StatusDaOrdem = @StatusDaOrdem, IdOrcamento = @IdOrcamento,
            IdPagamento = @IdPagamento, ReferenciaExterna = @ReferenciaExterna, Valor = @Valor,
            MotivoDaCompensacao = @MotivoDaCompensacao, AtualizadoEm = @AtualizadoEm
        WHERE IdOrdem = @IdOrdem;";

    private const string OBTER = @"
        SELECT Id, IdCliente, IdVeiculo, IdFuncionario, Status, NomeCliente, DescricaoVeiculo, CriadoEm, AtualizadoEm
        FROM OrdemServico WHERE Id = @Id";

    private const string LISTAR = @"
        SELECT Id, IdCliente, IdVeiculo, IdFuncionario, Status, NomeCliente, DescricaoVeiculo, CriadoEm, AtualizadoEm
        FROM OrdemServico
        WHERE (@IdCliente IS NULL OR IdCliente = @IdCliente)
        -- Prioridade da recepção, igual à Fase 2: em execução primeiro,
        -- entregues por último, e dentro de cada grupo as mais antigas na frente.
        ORDER BY CASE Status WHEN 3 THEN 0 WHEN 2 THEN 1 WHEN 1 THEN 2 WHEN 0 THEN 3 ELSE 4 END, CriadoEm";

    private const string HISTORICO = @"
        SELECT StatusAnterior, StatusNovo, Motivo, OcorridoEm
        FROM StatusHistorico WHERE IdOrdem = @IdOrdem ORDER BY OcorridoEm, Id";

    private readonly string _connectionString;

    public OrdemRepositorio(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task<int> AbrirAsync(
        int idCliente, int idVeiculo, int idFuncionario,
        string nomeCliente, string descricaoVeiculo,
        Func<int, EventoOutbox> montarEvento,
        DateTime agora,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(montarEvento);

        await using var conexao = new SqlConnection(_connectionString);
        await conexao.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transacao = (SqlTransaction)await conexao.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var id = await conexao.ExecuteScalarAsync<int>(new CommandDefinition(
                ABRIR,
                new { IdCliente = idCliente, IdVeiculo = idVeiculo, IdFuncionario = idFuncionario, NomeCliente = nomeCliente, DescricaoVeiculo = descricaoVeiculo, Agora = agora },
                transacao, cancellationToken: cancellationToken)).ConfigureAwait(false);

            await conexao.ExecuteAsync(new CommandDefinition(
                CRIAR_SAGA, new { IdOrdem = id, Agora = agora }, transacao, cancellationToken: cancellationToken)).ConfigureAwait(false);

            await conexao.ExecuteAsync(new CommandDefinition(
                REGISTRAR_HISTORICO,
                new { IdOrdem = id, StatusAnterior = (byte?)null, StatusNovo = (byte)EStatusOrdem.Recebida, Motivo = (string?)"Ordem aberta", OcorridoEm = agora },
                transacao, cancellationToken: cancellationToken)).ConfigureAwait(false);

            // O evento só pode ser montado depois do INSERT, porque leva o id.
            await OutboxRepositorio.InserirAsync(conexao, transacao, montarEvento(id), cancellationToken).ConfigureAwait(false);

            await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);

            return id;
        }
        catch
        {
            await transacao.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task AplicarDecisaoAsync(
        EstadoDaSaga estado,
        EStatusOrdem? statusAnterior,
        string? motivo,
        IReadOnlyList<EventoOutbox> eventos,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(estado);
        ArgumentNullException.ThrowIfNull(eventos);

        await using var conexao = new SqlConnection(_connectionString);
        await conexao.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transacao = (SqlTransaction)await conexao.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await conexao.ExecuteAsync(new CommandDefinition(
                SALVAR_SAGA,
                new
                {
                    estado.IdOrdem, Passo = (byte)estado.Passo, StatusDaOrdem = (byte)estado.StatusDaOrdem,
                    estado.IdOrcamento, estado.IdPagamento, estado.ReferenciaExterna, estado.Valor,
                    estado.MotivoDaCompensacao, estado.AtualizadoEm,
                },
                transacao, cancellationToken: cancellationToken)).ConfigureAwait(false);

            // Histórico só quando o status muda de verdade. Uma transição da
            // saga que não mexe no que o cliente vê não vira linha na tela dele.
            if (statusAnterior != estado.StatusDaOrdem)
            {
                await conexao.ExecuteAsync(new CommandDefinition(
                    ATUALIZAR_STATUS,
                    new { estado.IdOrdem, Status = (byte)estado.StatusDaOrdem, Agora = estado.AtualizadoEm },
                    transacao, cancellationToken: cancellationToken)).ConfigureAwait(false);

                await conexao.ExecuteAsync(new CommandDefinition(
                    REGISTRAR_HISTORICO,
                    new
                    {
                        estado.IdOrdem,
                        StatusAnterior = (byte?)statusAnterior,
                        StatusNovo = (byte)estado.StatusDaOrdem,
                        Motivo = motivo,
                        OcorridoEm = estado.AtualizadoEm,
                    },
                    transacao, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            foreach (var evento in eventos)
                await OutboxRepositorio.InserirAsync(conexao, transacao, evento, cancellationToken).ConfigureAwait(false);

            await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transacao.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<OrdemResumida?> ObterAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var conexao = new SqlConnection(_connectionString);

        var linha = await conexao.QueryFirstOrDefaultAsync<LinhaDeOrdem>(
            new CommandDefinition(OBTER, new { Id = id }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return linha?.Para();
    }

    public async Task<IReadOnlyList<OrdemResumida>> ListarAsync(int? idCliente, CancellationToken cancellationToken = default)
    {
        await using var conexao = new SqlConnection(_connectionString);

        var linhas = await conexao.QueryAsync<LinhaDeOrdem>(
            new CommandDefinition(LISTAR, new { IdCliente = idCliente }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return linhas.Select(l => l.Para()).ToList();
    }

    public async Task<IReadOnlyList<MudancaDeStatus>> HistoricoAsync(int idOrdem, CancellationToken cancellationToken = default)
    {
        await using var conexao = new SqlConnection(_connectionString);

        var linhas = await conexao.QueryAsync<LinhaDeHistorico>(
            new CommandDefinition(HISTORICO, new { IdOrdem = idOrdem }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return linhas.Select(l => l.Para()).ToList();
    }
}
