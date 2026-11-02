using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using PosTech.Contratos;
using PosTech.Mensageria;
using Reqnroll;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;

namespace PosTech.Os.Testes.Bdd;

/// <summary>
/// O ambiente de uma funcionalidade BDD: o OS Service no ar com SQL Server e
/// RabbitMQ de verdade, e espiões nas filas do Execução e do Billing para ver
/// o que o orquestrador manda para eles.
///
/// Sobe uma vez por funcionalidade, e não por cenário, pelo mesmo motivo das
/// fixtures xUnit do projeto: um SQL Server por cenário seria minutos. Os
/// cenários não colidem porque cada um tem a sua ordem e o seu correlationId.
///
/// A funcionalidade marcada com <c>@prazo-curto</c> ganha um prazo de execução
/// de segundos. As outras usam o padrão de produção, uma hora — senão o
/// varredor cancelaria as ordens do caminho feliz no meio do cenário.
/// </summary>
public sealed class AmbienteDaSaga : IAsyncDisposable
{
    public const string TagPrazoCurto = "prazo-curto";

    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4").Build();

    public WebApplicationFactory<Program> Fabrica { get; private set; } = null!;

    public string RabbitConnectionString { get; private set; } = string.Empty;

    public EspiaoDaFila FilaDoExecucao { get; private set; } = null!;

    public EspiaoDaFila FilaDoBilling { get; private set; } = null!;

    public HttpClient Cliente(string correlationId)
    {
        var cliente = Fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add("X-Correlation-ID", correlationId);
        return cliente;
    }

    public static async Task<AmbienteDaSaga> SubirAsync(bool prazoCurto)
    {
        var ambiente = new AmbienteDaSaga();
        await ambiente.IniciarAsync(prazoCurto);
        return ambiente;
    }

    private async Task IniciarAsync(bool prazoCurto)
    {
        await Task.WhenAll(_sql.StartAsync(), _rabbit.StartAsync());

        RabbitConnectionString = _rabbit.GetConnectionString();
        await AplicarSchemasAsync(_sql.GetConnectionString());

        Fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("SqlConnectionString", _sql.GetConnectionString());
            builder.UseSetting("RabbitMqConnectionString", RabbitConnectionString);

            if (prazoCurto)
            {
                // Três segundos de prazo, varredura a cada meio segundo.
                builder.UseSetting("Saga:PrazoDeExecucaoEmMinutos", "0.05");
                builder.UseSetting("Saga:IntervaloDaVarreduraEmMinutos", "0.008");
            }
        });

        _ = Fabrica.CreateClient();

        // Os espiões ficam no lugar do Execução e do Billing: consomem as filas
        // deles com a mesma topologia de produção.
        FilaDoExecucao = await EspiaoDaFila.LigarAsync(
            RabbitConnectionString, Topologia.FilaExecucao, "OrdemAberta", "PagamentoConfirmado", "OrdemCancelada");

        FilaDoBilling = await EspiaoDaFila.LigarAsync(
            RabbitConnectionString, Topologia.FilaBilling, "DiagnosticoConcluido", "OrcamentoCancelado", "OrdemCancelada");
    }

    /// <summary>
    /// Publica no exchange como outro serviço publicaria: mesmo envelope, mesma
    /// routing key, mesma origem.
    /// </summary>
    public async Task PublicarComoAsync<T>(T evento, string origem, string correlationId)
        where T : notnull
    {
        await using var conexao = new ConexaoRabbitMq(RabbitConnectionString, origem);
        await conexao.DeclararTopologiaAsync();

        var publicador = new PublicadorRabbitMq(conexao);
        await publicador.PublicarAsync(Outbox.Enfileirar(evento, correlationId, origem));
    }

    private static async Task AplicarSchemasAsync(string connectionString)
    {
        var pasta = AppContext.BaseDirectory;

        while (pasta is not null && !Directory.Exists(Path.Combine(pasta, "src", "PosTech.Persistencia")))
            pasta = Directory.GetParent(pasta)?.FullName;

        await using var conexao = new SqlConnection(connectionString);
        await conexao.OpenAsync();

        foreach (var arquivo in new[] { "schema-os.sql", "schema-mensageria.sql" })
        {
            var script = await File.ReadAllTextAsync(Path.Combine(pasta!, "src", "PosTech.Persistencia", arquivo));

            foreach (var lote in script.Split("\nGO", StringSplitOptions.RemoveEmptyEntries))
            {
                if (!string.IsNullOrWhiteSpace(lote))
                    await conexao.ExecuteAsync(lote);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await FilaDoExecucao.DisposeAsync();
        await FilaDoBilling.DisposeAsync();
        await Fabrica.DisposeAsync();
        await Task.WhenAll(_sql.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }
}

/// <summary>Sobe e derruba o ambiente em volta de cada funcionalidade.</summary>
[Binding]
public sealed class GanchosDaSaga
{
    [BeforeFeature("saga")]
    public static async Task SubirAsync(FeatureContext funcionalidade)
    {
        var prazoCurto = funcionalidade.FeatureInfo.Tags.Contains(AmbienteDaSaga.TagPrazoCurto);

        funcionalidade.Set(await AmbienteDaSaga.SubirAsync(prazoCurto));
    }

    [AfterFeature("saga")]
    public static async Task DerrubarAsync(FeatureContext funcionalidade)
    {
        if (funcionalidade.TryGetValue(out AmbienteDaSaga ambiente))
            await ambiente.DisposeAsync();
    }
}
