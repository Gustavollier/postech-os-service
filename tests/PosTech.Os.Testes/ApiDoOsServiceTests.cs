using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using PosTech.Contratos;
using PosTech.Mensageria;
using PosTech.Os.Api.Controllers;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;

namespace PosTech.Os.Testes;

/// <summary>
/// A API do OS Service no ar, com SQL Server e RabbitMQ de verdade.
///
/// Sobe uma vez para a coleção inteira: o xUnit instancia a classe de teste uma
/// vez por <c>[Fact]</c>, e um SQL Server por teste seria minutos de espera.
/// </summary>
public sealed class AmbienteDoOsService : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4").Build();

    public WebApplicationFactory<Program> Fabrica { get; private set; } = null!;

    public string SqlConnectionString { get; private set; } = string.Empty;

    public string RabbitConnectionString { get; private set; } = string.Empty;

    public HttpClient Cliente(string? correlationId = null)
    {
        var cliente = Fabrica.CreateClient();

        if (correlationId is { Length: > 0 })
            cliente.DefaultRequestHeaders.Add("X-Correlation-ID", correlationId);

        return cliente;
    }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_sql.StartAsync(), _rabbit.StartAsync());

        SqlConnectionString = _sql.GetConnectionString();
        RabbitConnectionString = _rabbit.GetConnectionString();

        await AplicarSchemasAsync();

        Fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("SqlConnectionString", SqlConnectionString);
            builder.UseSetting("RabbitMqConnectionString", RabbitConnectionString);

            // Varredura de timeout bem rápida: é o que torna o cenário de
            // compensação por prazo observável num teste em vez de em uma hora.
            builder.UseSetting("Saga__PrazoDeExecucaoEmMinutos", "60");
            builder.UseSetting("Saga__IntervaloDaVarreduraEmMinutos", "5");
        });

        // É o CreateClient que constrói o grafo de dependências e inicia os
        // serviços de fundo. Sem isto, nada aqui provaria que o host sobe.
        _ = Fabrica.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await Fabrica.DisposeAsync();
        await Task.WhenAll(_sql.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }

    /// <summary>Roda os dois scripts de schema, como a pipeline faz no deploy.</summary>
    private async Task AplicarSchemasAsync()
    {
        await using var conexao = new SqlConnection(SqlConnectionString);
        await conexao.OpenAsync();

        foreach (var arquivo in new[] { "schema-os.sql", "schema-mensageria.sql" })
        {
            var script = await File.ReadAllTextAsync(Path.Combine(PastaDosSchemas(), arquivo));

            // SqlClient não entende GO: é separador do sqlcmd, não do T-SQL.
            foreach (var lote in script.Split("\nGO", StringSplitOptions.RemoveEmptyEntries))
            {
                if (!string.IsNullOrWhiteSpace(lote))
                    await conexao.ExecuteAsync(lote);
            }
        }
    }

    private static string PastaDosSchemas()
    {
        var pasta = AppContext.BaseDirectory;

        while (pasta is not null && !Directory.Exists(Path.Combine(pasta, "src", "PosTech.Persistencia")))
            pasta = Directory.GetParent(pasta)?.FullName;

        return Path.Combine(pasta!, "src", "PosTech.Persistencia");
    }

    public async Task<T?> ConsultarAsync<T>(string sql, object? parametros = null)
    {
        await using var conexao = new SqlConnection(SqlConnectionString);

        return await conexao.QueryFirstOrDefaultAsync<T>(sql, parametros);
    }
}

[CollectionDefinition("os-api")]
public sealed class ColecaoDaApiDoOs : ICollectionFixture<AmbienteDoOsService>;

/// <summary>
/// As rotas HTTP do OS Service.
///
/// Esta camada estava com cobertura **zero** até aqui, e não por ser trivial: é
/// onde o corpo da requisição vira evento no outbox, e onde um mapeamento errado
/// de enum para TINYINT já derrubou uma rota antes — o <c>OrdemResumida</c> como
/// record posicional, que fazia o GET devolver 500 sem nenhum teste notar.
/// </summary>
[Collection("os-api")]
public class ApiDoOsServiceTests
{
    private readonly AmbienteDoOsService _ambiente;

    public ApiDoOsServiceTests(AmbienteDoOsService ambiente) => _ambiente = ambiente;

    private static AbrirOrdemRequest UmaOrdem(string nome = "Ana Prado") => new(
        IdCliente: 10, IdVeiculo: 20, IdFuncionario: 30,
        NomeCliente: nome, DescricaoVeiculo: "Gol AAA-1234", EmailCliente: "ana@oficina.test");

    private async Task<int> AbrirAsync(string correlationId = "corr-api", string nome = "Ana Prado")
    {
        var resposta = await _ambiente.Cliente(correlationId)
            .PostAsJsonAsync("/api/v1/ordens-servico", UmaOrdem(nome));

        resposta.EnsureSuccessStatusCode();

        var criada = await resposta.Content.ReadFromJsonAsync<OrdemCriada>();

        return criada!.Id;
    }

    // -----------------------------------------------------------------------
    // Abrir
    // -----------------------------------------------------------------------

    [Fact]
    public async Task O_servico_sobe_com_todas_as_dependencias_resolvidas()
    {
        // Montar o grafo de dependências é comportamento do serviço. No Billing,
        // um registro que faltava no contêiner derrubava o host na partida e os
        // 35 testes de unidade passavam alheios.
        var resposta = await _ambiente.Cliente().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task O_readiness_responde_separado_do_liveness()
    {
        var resposta = await _ambiente.Cliente().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task Abrir_devolve_201_com_o_Location_da_ordem_criada()
    {
        var resposta = await _ambiente.Cliente()
            .PostAsJsonAsync("/api/v1/ordens-servico", UmaOrdem());

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);

        // 201 sem Location é 201 pela metade: quem criou não sabe onde ler.
        Assert.NotNull(resposta.Headers.Location);

        var criada = await resposta.Content.ReadFromJsonAsync<OrdemCriada>();
        Assert.True(criada!.Id > 0);
    }

    [Fact]
    public async Task Abrir_grava_a_ordem_a_saga_e_o_evento_na_mesma_transacao()
    {
        var id = await AbrirAsync("corr-transacao");

        // Os três de uma vez: é a promessa em que o padrão outbox inteiro se
        // apoia, e aqui ela é verificada pelo caminho que o usuário usa.
        var ordens = await _ambiente.ConsultarAsync<int>(
            "SELECT COUNT(1) FROM OrdemServico WHERE Id = @id", new { id });

        var sagas = await _ambiente.ConsultarAsync<int>(
            "SELECT COUNT(1) FROM SagaOrdemServico WHERE IdOrdem = @id", new { id });

        var historico = await _ambiente.ConsultarAsync<int>(
            "SELECT COUNT(1) FROM StatusHistorico WHERE IdOrdem = @id", new { id });

        Assert.Equal(1, ordens);
        Assert.Equal(1, sagas);
        Assert.Equal(1, historico);
    }

    [Fact]
    public async Task O_evento_sai_com_o_correlationId_que_veio_no_cabecalho()
    {
        // O APIM cria o X-Correlation-ID na borda. Ignorá-lo aqui faria o rastro
        // do fluxo distribuído começar neste serviço em vez de na requisição do
        // cliente — e aí não há como seguir uma ordem pelos quatro serviços.
        await AbrirAsync("corr-do-gateway", "Bruno Dias");

        var correlacao = await _ambiente.ConsultarAsync<string>(
            "SELECT TOP 1 CorrelationId FROM EventoOutbox WHERE Tipo = 'OrdemAberta' ORDER BY Id DESC");

        Assert.Equal("corr-do-gateway", correlacao);
    }

    [Theory]
    [InlineData(0, 20, 30)]
    [InlineData(10, 0, 30)]
    [InlineData(10, 20, 0)]
    [InlineData(-1, 20, 30)]
    public async Task Abrir_sem_cliente_veiculo_ou_funcionario_devolve_400(int cliente, int veiculo, int funcionario)
    {
        var resposta = await _ambiente.Cliente().PostAsJsonAsync("/api/v1/ordens-servico", new AbrirOrdemRequest(
            IdCliente: cliente, IdVeiculo: veiculo, IdFuncionario: funcionario,
            NomeCliente: "Ana", DescricaoVeiculo: "Gol", EmailCliente: "a@a.test"));

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Consultar
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Obter_devolve_a_ordem_com_o_status_por_nome()
    {
        var id = await AbrirAsync();

        var resposta = await _ambiente.Cliente().GetAsync($"/api/v1/ordens-servico/{id}");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var ordem = await resposta.Content.ReadFromJsonAsync<OrdemLida>();

        Assert.NotNull(ordem);
        Assert.Equal(id, ordem.Id);
        Assert.Equal(10, ordem.IdCliente);
        Assert.Equal("Ana Prado", ordem.NomeCliente);

        // Por nome, e não número: na Fase 3 a API serializava enum por nome e o
        // front lia como número, e isso custou três bugs.
        Assert.False(int.TryParse(ordem.Status, out _));
    }

    [Fact]
    public async Task Obter_uma_ordem_que_nao_existe_devolve_404()
    {
        var resposta = await _ambiente.Cliente().GetAsync("/api/v1/ordens-servico/999999");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task Listar_devolve_as_ordens_e_o_filtro_por_cliente_funciona()
    {
        await AbrirAsync(nome: "Carla Souza");

        var cliente = _ambiente.Cliente();

        var todas = await cliente.GetFromJsonAsync<List<OrdemLida>>("/api/v1/ordens-servico");
        Assert.NotNull(todas);
        Assert.NotEmpty(todas);

        var doCliente = await cliente.GetFromJsonAsync<List<OrdemLida>>("/api/v1/ordens-servico?idCliente=10");
        Assert.NotNull(doCliente);
        Assert.All(doCliente, o => Assert.Equal(10, o.IdCliente));

        // O filtro é o que limita o que um cliente enxerga. Se ele for ignorado,
        // a rota passa a vazar ordem de terceiro — e continua devolvendo 200.
        var deOutro = await cliente.GetFromJsonAsync<List<OrdemLida>>("/api/v1/ordens-servico?idCliente=987654");
        Assert.NotNull(deOutro);
        Assert.Empty(deOutro);
    }

    [Fact]
    public async Task Status_devolve_o_historico_com_os_motivos()
    {
        var id = await AbrirAsync();

        var resposta = await _ambiente.Cliente().GetAsync($"/api/v1/ordens-servico/{id}/status");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var status = await resposta.Content.ReadFromJsonAsync<StatusLido>();

        Assert.NotNull(status);
        Assert.Equal(id, status.Id);
        Assert.NotEmpty(status.Historico);

        // A primeira transição não tem "de": a ordem não vinha de status nenhum.
        Assert.Null(status.Historico[0].De);
        Assert.False(string.IsNullOrWhiteSpace(status.Historico[0].Para));
    }

    [Fact]
    public async Task Status_de_ordem_inexistente_devolve_404()
    {
        var resposta = await _ambiente.Cliente().GetAsync("/api/v1/ordens-servico/999999/status");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task A_rota_da_saga_mostra_o_passo_em_que_a_ordem_esta()
    {
        // Esta rota existe porque orquestração só se paga se o estado for
        // visível. É ela que, no vídeo, mostra a saga em Compensando e o motivo.
        var id = await AbrirAsync();

        var resposta = await _ambiente.Cliente().GetAsync($"/api/v1/ordens-servico/{id}/saga");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var saga = await resposta.Content.ReadFromJsonAsync<SagaLida>();

        Assert.NotNull(saga);
        Assert.Equal(id, saga.IdOrdem);
        Assert.Equal("AguardandoDiagnostico", saga.Passo);
        Assert.True(saga.EmAndamento);
        Assert.Null(saga.MotivoDaCompensacao);
    }

    [Fact]
    public async Task A_rota_da_saga_para_ordem_inexistente_devolve_404()
    {
        var resposta = await _ambiente.Cliente().GetAsync("/api/v1/ordens-servico/999999/saga");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Caminho completo pela API
    // -----------------------------------------------------------------------

    [Fact]
    public async Task O_outbox_entrega_o_OrdemAberta_que_a_rota_enfileirou()
    {
        // Fecha o laço entre a requisição HTTP e o barramento: a rota grava, o
        // serviço de fundo publica, e o evento chega na fila do Execução. É o
        // publicador de fundo do processo real, não um empurrão do teste.
        await using var espiao = await EspiaoDaFila.LigarAsync(
            _ambiente.RabbitConnectionString, Topologia.FilaExecucao, "OrdemAberta");

        await AbrirAsync("corr-ponta-a-ponta", "Diego Alves");

        var evento = await espiao.EsperarAsync("OrdemAberta", "corr-ponta-a-ponta");

        var aberta = evento.Ler<OrdemAberta>();

        Assert.NotNull(aberta);
        Assert.Equal("Diego Alves", aberta.NomeCliente);
        Assert.Equal("ana@oficina.test", aberta.EmailCliente);
    }

    [Fact]
    public async Task O_swagger_responde_porque_e_entregavel_da_fase()
    {
        var resposta = await _ambiente.Cliente().GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Formas de leitura das respostas
    // -----------------------------------------------------------------------

    private sealed record OrdemCriada(int Id);

    private sealed record OrdemLida(
        int Id, int IdCliente, int IdVeiculo, int IdFuncionario,
        string Status, string? NomeCliente, string? DescricaoVeiculo);

    private sealed record StatusLido(int Id, string Status, IReadOnlyList<TransicaoLida> Historico);

    private sealed record TransicaoLida(string? De, string Para, string? Motivo, DateTime OcorridoEm);

    private sealed record SagaLida(
        int IdOrdem, string Passo, string StatusDaOrdem, string? IdOrcamento,
        decimal? Valor, string? MotivoDaCompensacao, bool EmAndamento);
}

/// <summary>
/// Escuta uma fila para provar que o evento saiu de verdade pelo broker, e não
/// só foi gravado na tabela do outbox.
/// </summary>
public sealed class EspiaoDaFila : IAsyncDisposable
{
    private readonly ConexaoRabbitMq _conexao;
    private readonly ConsumidorDeEventos _consumidor;
    private readonly List<EnvelopeRecebido> _recebidos = [];
    private readonly object _trava = new();

    private EspiaoDaFila(ConexaoRabbitMq conexao, ConsumidorDeEventos consumidor)
    {
        _conexao = conexao;
        _consumidor = consumidor;
    }

    public static async Task<EspiaoDaFila> LigarAsync(string connectionString, string fila, params string[] tipos)
    {
        var conexao = new ConexaoRabbitMq(connectionString, "espiao");

        await conexao.DeclararTopologiaAsync();

        EspiaoDaFila? espiao = null;

        var tratadores = tipos.ToDictionary(
            tipo => tipo,
            _ => (TratadorDeEvento)((envelope, _) =>
            {
                espiao!.Guardar(envelope);
                return Task.CompletedTask;
            }));

        var consumidor = new ConsumidorDeEventos(
            conexao, fila, "espiao", new IdempotenciaEmMemoria(), tratadores);

        espiao = new EspiaoDaFila(conexao, consumidor);

        await consumidor.IniciarAsync();

        return espiao;
    }

    private void Guardar(EnvelopeRecebido envelope)
    {
        lock (_trava)
        {
            _recebidos.Add(envelope);
        }
    }

    public IReadOnlyList<EnvelopeRecebido> Recebidos
    {
        get
        {
            lock (_trava)
            {
                return [.. _recebidos];
            }
        }
    }

    /// <summary>
    /// Espera pelo tipo **e** pelo correlationId. Sem o segundo filtro, o
    /// primeiro evento daquele tipo deixado por outro teste satisfaz a espera, e
    /// a asserção passa por acidente.
    /// </summary>
    public async Task<EnvelopeRecebido> EsperarAsync(string tipo, string correlationId, int segundos = 45)
    {
        var limite = DateTime.UtcNow.AddSeconds(segundos);

        while (DateTime.UtcNow < limite)
        {
            if (Recebidos.FirstOrDefault(e => e.Tipo == tipo && e.CorrelationId == correlationId) is { } achado)
                return achado;

            await Task.Delay(250);
        }

        throw new TimeoutException($"Esperei {segundos}s pelo evento {tipo} de {correlationId}. Não chegou.");
    }

    public async ValueTask DisposeAsync()
    {
        await _consumidor.DisposeAsync();
        await _conexao.DisposeAsync();
    }
}
