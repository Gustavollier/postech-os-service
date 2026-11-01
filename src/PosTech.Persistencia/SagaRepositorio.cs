using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using PosTech.Saga;

namespace PosTech.Persistencia;

/// <summary>
/// Estado da saga em Azure SQL, dentro de <c>os-db</c>, ao lado da ordem.
///
/// Fica no mesmo banco de propósito: a saga e a ordem mudam juntas, e separá-las
/// criaria exatamente a transação distribuída que o padrão existe para evitar
/// (ADR-0006).
/// </summary>
public interface ISagaRepositorio
{
    Task<EstadoDaSaga?> ObterAsync(int idOrdem, CancellationToken cancellationToken = default);

    Task CriarAsync(EstadoDaSaga estado, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grava o estado novo e enfileira os eventos de compensação na mesma
    /// transação. É a razão de este método existir em vez de dois: se a saga
    /// avançasse e os eventos ficassem para trás, a compensação nunca sairia.
    /// </summary>
    Task SalvarComEventosAsync(
        EstadoDaSaga estado,
        IReadOnlyList<Mensageria.EventoOutbox> eventos,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sagas paradas em <see cref="EPassoDaSaga.AguardandoExecucao"/> além do
    /// prazo. Ninguém publica o fato de algo não ter acontecido, então a
    /// compensação por timeout precisa de um varredor que pergunte.
    /// </summary>
    Task<IReadOnlyList<EstadoDaSaga>> ObterParadasAsync(
        EPassoDaSaga passo, TimeSpan prazo, int limite, CancellationToken cancellationToken = default);
}

public sealed class SagaRepositorio : ISagaRepositorio
{
    private const string OBTER = @"
        SELECT IdOrdem, Passo, StatusDaOrdem, IdOrcamento, IdPagamento,
               ReferenciaExterna, Valor, MotivoDaCompensacao, AtualizadoEm
        FROM SagaOrdemServico
        WHERE IdOrdem = @IdOrdem";

    private const string CRIAR = @"
        IF NOT EXISTS (SELECT 1 FROM SagaOrdemServico WHERE IdOrdem = @IdOrdem)
            INSERT INTO SagaOrdemServico
                (IdOrdem, Passo, StatusDaOrdem, Valor, AtualizadoEm)
            VALUES
                (@IdOrdem, @Passo, @StatusDaOrdem, @Valor, @AtualizadoEm);";

    private const string SALVAR = @"
        UPDATE SagaOrdemServico
        SET Passo = @Passo,
            StatusDaOrdem = @StatusDaOrdem,
            IdOrcamento = @IdOrcamento,
            IdPagamento = @IdPagamento,
            ReferenciaExterna = @ReferenciaExterna,
            Valor = @Valor,
            MotivoDaCompensacao = @MotivoDaCompensacao,
            AtualizadoEm = @AtualizadoEm
        WHERE IdOrdem = @IdOrdem";

    private const string OBTER_PARADAS = @"
        SELECT TOP (@Limite)
               IdOrdem, Passo, StatusDaOrdem, IdOrcamento, IdPagamento,
               ReferenciaExterna, Valor, MotivoDaCompensacao, AtualizadoEm
        FROM SagaOrdemServico
        WHERE Passo = @Passo AND AtualizadoEm <= @Limite_Tempo
        ORDER BY AtualizadoEm";

    private readonly string _connectionString;

    public SagaRepositorio(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task<EstadoDaSaga?> ObterAsync(int idOrdem, CancellationToken cancellationToken = default)
    {
        await using var conexao = new SqlConnection(_connectionString);

        var comando = new CommandDefinition(OBTER, new { IdOrdem = idOrdem }, cancellationToken: cancellationToken);
        var linha = await conexao.QueryFirstOrDefaultAsync<LinhaDaSaga>(comando).ConfigureAwait(false);

        return linha?.ParaEstado();
    }

    public async Task CriarAsync(EstadoDaSaga estado, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(estado);

        await using var conexao = new SqlConnection(_connectionString);

        var comando = new CommandDefinition(
            CRIAR,
            new
            {
                estado.IdOrdem,
                Passo = (byte)estado.Passo,
                StatusDaOrdem = (byte)estado.StatusDaOrdem,
                estado.Valor,
                estado.AtualizadoEm,
            },
            cancellationToken: cancellationToken);

        await conexao.ExecuteAsync(comando).ConfigureAwait(false);
    }

    public async Task SalvarComEventosAsync(
        EstadoDaSaga estado,
        IReadOnlyList<Mensageria.EventoOutbox> eventos,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(estado);
        ArgumentNullException.ThrowIfNull(eventos);

        await using var conexao = new SqlConnection(_connectionString);
        await conexao.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transacao = (SqlTransaction)await conexao.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var comando = new CommandDefinition(
                SALVAR,
                new
                {
                    estado.IdOrdem,
                    Passo = (byte)estado.Passo,
                    StatusDaOrdem = (byte)estado.StatusDaOrdem,
                    estado.IdOrcamento,
                    estado.IdPagamento,
                    estado.ReferenciaExterna,
                    estado.Valor,
                    estado.MotivoDaCompensacao,
                    estado.AtualizadoEm,
                },
                transaction: transacao,
                cancellationToken: cancellationToken);

            await conexao.ExecuteAsync(comando).ConfigureAwait(false);

            foreach (var evento in eventos)
            {
                await OutboxRepositorio.InserirAsync(conexao, transacao, evento, cancellationToken).ConfigureAwait(false);
            }

            await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transacao.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<EstadoDaSaga>> ObterParadasAsync(
        EPassoDaSaga passo, TimeSpan prazo, int limite, CancellationToken cancellationToken = default)
    {
        await using var conexao = new SqlConnection(_connectionString);

        var comando = new CommandDefinition(
            OBTER_PARADAS,
            new { Passo = (byte)passo, Limite_Tempo = DateTime.UtcNow.Subtract(prazo), Limite = limite },
            cancellationToken: cancellationToken);

        var linhas = await conexao.QueryAsync<LinhaDaSaga>(comando).ConfigureAwait(false);

        return linhas.Select(l => l.ParaEstado()).ToList();
    }

    /// <summary>
    /// Dapper não popula <c>required init</c>, então a linha vem para este
    /// intermediário e vira estado por construção.
    /// </summary>
    private sealed class LinhaDaSaga
    {
        public int IdOrdem { get; set; }
        public byte Passo { get; set; }
        public byte StatusDaOrdem { get; set; }
        public string? IdOrcamento { get; set; }
        public string? IdPagamento { get; set; }
        public string? ReferenciaExterna { get; set; }
        public decimal? Valor { get; set; }
        public string? MotivoDaCompensacao { get; set; }
        public DateTime AtualizadoEm { get; set; }

        public EstadoDaSaga ParaEstado() => new()
        {
            IdOrdem = IdOrdem,
            Passo = (EPassoDaSaga)Passo,
            StatusDaOrdem = (EStatusOrdem)StatusDaOrdem,
            IdOrcamento = IdOrcamento,
            IdPagamento = IdPagamento,
            ReferenciaExterna = ReferenciaExterna,
            Valor = Valor,
            MotivoDaCompensacao = MotivoDaCompensacao,
            AtualizadoEm = AtualizadoEm,
        };
    }
}
