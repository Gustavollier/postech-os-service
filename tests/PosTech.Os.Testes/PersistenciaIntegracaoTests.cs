using Dapper;
using Microsoft.Data.SqlClient;
using PosTech.Contratos;
using PosTech.Mensageria;
using PosTech.Persistencia;
using PosTech.Saga;
using Testcontainers.MsSql;

namespace PosTech.Os.Testes;

/// <summary>
/// O outbox, a idempotência e a saga contra um SQL Server de verdade.
///
/// O teste que mais importa aqui é o de atomicidade. O padrão outbox inteiro se
/// apoia numa promessa — gravar o estado e enfileirar o evento acontecem na
/// mesma transação — e essa promessa não se verifica com dublê: ou o banco faz
/// rollback dos dois, ou não faz.
/// </summary>
public sealed class PersistenciaIntegracaoTests : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    private string _conexao = null!;

    public async Task InitializeAsync()
    {
        await _sql.StartAsync();
        _conexao = _sql.GetConnectionString();

        var script = await File.ReadAllTextAsync(CaminhoDoSchema());

        await using var conexao = new SqlConnection(_conexao);
        await conexao.OpenAsync();

        // SqlClient não entende GO: é separador do sqlcmd, não do T-SQL.
        foreach (var lote in script.Split("\nGO", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!string.IsNullOrWhiteSpace(lote))
                await conexao.ExecuteAsync(lote);
        }
    }

    public async Task DisposeAsync() => await _sql.DisposeAsync();

    private static string CaminhoDoSchema()
    {
        var pasta = AppContext.BaseDirectory;

        while (pasta is not null && !Directory.Exists(Path.Combine(pasta, "src", "PosTech.Persistencia")))
            pasta = Directory.GetParent(pasta)?.FullName;

        return Path.Combine(pasta!, "src", "PosTech.Persistencia", "schema-mensageria.sql");
    }

    private static OrdemAberta UmaOrdem(int id) => new(
        IdOrdem: id, IdCliente: 10, NomeCliente: "Ana Prado", EmailCliente: "ana@oficina.test",
        IdVeiculo: 20, DescricaoVeiculo: "Gol AAA-1234", IdFuncionario: 30, AbertaEm: DateTime.UtcNow);

    // -----------------------------------------------------------------------
    // Outbox
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Evento_gravado_aparece_como_pendente_e_some_depois_de_publicado()
    {
        var repositorio = new OutboxRepositorio(_conexao);
        var evento = Outbox.Enfileirar(UmaOrdem(1), "corr-1", "os-service");

        await using (var conexao = new SqlConnection(_conexao))
        {
            await conexao.OpenAsync();
            await using var transacao = (SqlTransaction)await conexao.BeginTransactionAsync();
            evento.Id = await OutboxRepositorio.InserirAsync(conexao, transacao, evento);
            await transacao.CommitAsync();
        }

        var pendentes = await repositorio.ObterPendentesAsync(10);
        var gravado = Assert.Single(pendentes, l => l.MensagemId == evento.MensagemId);

        Assert.Equal("OrdemAberta", gravado.Tipo);
        Assert.Equal(RoutingKeys.OrdemAberta, gravado.RoutingKey);
        Assert.Equal("corr-1", gravado.CorrelationId);
        Assert.Equal(EStatusOutbox.Pendente, gravado.Status);

        await repositorio.MarcarPublicadoAsync(gravado.MensagemId, DateTime.UtcNow);

        Assert.DoesNotContain(await repositorio.ObterPendentesAsync(10), l => l.Id == gravado.Id);
    }

    [Fact]
    public async Task Evento_agendado_para_o_futuro_nao_entra_no_lote()
    {
        var repositorio = new OutboxRepositorio(_conexao);
        var evento = Outbox.Enfileirar(UmaOrdem(2), "corr-2", "os-service");
        evento.ProximaTentativaEm = DateTime.UtcNow.AddMinutes(10);

        await using (var conexao = new SqlConnection(_conexao))
        {
            await conexao.OpenAsync();
            await using var transacao = (SqlTransaction)await conexao.BeginTransactionAsync();
            await OutboxRepositorio.InserirAsync(conexao, transacao, evento);
            await transacao.CommitAsync();
        }

        Assert.DoesNotContain(await repositorio.ObterPendentesAsync(50), l => l.MensagemId == evento.MensagemId);
    }

    // -----------------------------------------------------------------------
    // Idempotência
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Registrar_o_mesmo_evento_duas_vezes_nao_estoura()
    {
        // Duas réplicas podem passar pela checagem juntas e gravar as duas. Uma
        // violação de chave aqui mandaria para a dead letter uma mensagem que
        // foi processada com sucesso, que é o pior resultado possível.
        var repositorio = new IdempotenciaRepositorio(_conexao);
        var id = Guid.NewGuid();

        Assert.False(await repositorio.JaProcessadoAsync(id, "execucao-service"));

        await repositorio.RegistrarAsync(id, "OrdemAberta", "execucao-service", DateTime.UtcNow);
        await repositorio.RegistrarAsync(id, "OrdemAberta", "execucao-service", DateTime.UtcNow);

        Assert.True(await repositorio.JaProcessadoAsync(id, "execucao-service"));
    }

    [Fact]
    public async Task Servicos_diferentes_processam_a_mesma_mensagem_legitimamente()
    {
        var repositorio = new IdempotenciaRepositorio(_conexao);
        var id = Guid.NewGuid();

        await repositorio.RegistrarAsync(id, "OrdemCancelada", "billing-service", DateTime.UtcNow);

        Assert.True(await repositorio.JaProcessadoAsync(id, "billing-service"));
        Assert.False(await repositorio.JaProcessadoAsync(id, "execucao-service"));
    }

    // -----------------------------------------------------------------------
    // Saga
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Estado_da_saga_sobrevive_a_ida_e_volta_do_banco()
    {
        var repositorio = new SagaRepositorio(_conexao);
        var estado = new EstadoDaSaga { IdOrdem = 100, AtualizadoEm = DateTime.UtcNow };

        await repositorio.CriarAsync(estado);

        estado.Passo = EPassoDaSaga.AguardandoPagamento;
        estado.StatusDaOrdem = EStatusOrdem.AguardandoAprovacao;
        estado.IdOrcamento = "orc-100";
        estado.Valor = 198.30m;
        estado.AtualizadoEm = DateTime.UtcNow;

        await repositorio.SalvarComEventosAsync(estado, []);

        var lido = await repositorio.ObterAsync(100);

        Assert.NotNull(lido);
        Assert.Equal(EPassoDaSaga.AguardandoPagamento, lido.Passo);
        Assert.Equal(EStatusOrdem.AguardandoAprovacao, lido.StatusDaOrdem);
        Assert.Equal("orc-100", lido.IdOrcamento);
        Assert.Equal(198.30m, lido.Valor);
    }

    [Fact]
    public async Task Salvar_a_saga_e_enfileirar_a_compensacao_acontece_numa_transacao_so()
    {
        var saga = new SagaRepositorio(_conexao);
        var outbox = new OutboxRepositorio(_conexao);

        var estado = new EstadoDaSaga { IdOrdem = 200, AtualizadoEm = DateTime.UtcNow };
        await saga.CriarAsync(estado);

        estado = OrquestradorDaSaga.Avancar(estado, new DiagnosticoConcluido(
            200, 7, [new ItemDaOrdem("Troca", ETipoItem.MaoDeObra, 1, 100m)], 100m, DateTime.UtcNow), DateTime.UtcNow).Estado;

        estado = OrquestradorDaSaga.Avancar(estado, new OrcamentoGerado(200, "orc-200", 100m, DateTime.UtcNow), DateTime.UtcNow).Estado;

        var decisao = OrquestradorDaSaga.CancelarPorPedido(estado, "Cliente desistiu", DateTime.UtcNow);

        var eventos = decisao.EventosAPublicar
            .Select(e => EnfileirarDinamico(e, "corr-200"))
            .ToList();

        await saga.SalvarComEventosAsync(decisao.Estado, eventos);

        var lido = await saga.ObterAsync(200);
        var pendentes = await outbox.ObterPendentesAsync(50);

        Assert.Equal(EPassoDaSaga.Cancelada, lido!.Passo);
        Assert.Equal("Cliente desistiu", lido.MotivoDaCompensacao);

        // Os dois eventos de compensação saíram junto com a mudança de estado.
        Assert.Contains(pendentes, l => l.Tipo == "OrcamentoCancelado" && l.CorrelationId == "corr-200");
        Assert.Contains(pendentes, l => l.Tipo == "OrdemCancelada" && l.CorrelationId == "corr-200");
    }

    [Fact]
    public async Task Falha_no_meio_desfaz_o_estado_e_os_eventos_juntos()
    {
        // A promessa do outbox: ou os dois acontecem, ou nenhum. Aqui o segundo
        // evento tem payload maior que a coluna aguenta, o insert estoura e o
        // UPDATE da saga, já executado na mesma transação, tem que voltar atrás.
        var saga = new SagaRepositorio(_conexao);
        var outbox = new OutboxRepositorio(_conexao);

        var estado = new EstadoDaSaga { IdOrdem = 300, AtualizadoEm = DateTime.UtcNow };
        await saga.CriarAsync(estado);

        var bom = Outbox.Enfileirar(new OrdemCancelada(300, "ok", DateTime.UtcNow), "corr-300", "os-service");
        var ruim = Outbox.Enfileirar(new OrdemCancelada(300, "estoura", DateTime.UtcNow), "corr-300", "os-service");
        ruim.Tipo = new string('x', 500); // a coluna Tipo é NVARCHAR(100)

        estado.Passo = EPassoDaSaga.Cancelada;
        estado.MotivoDaCompensacao = "não deveria persistir";

        await Assert.ThrowsAnyAsync<Exception>(
            () => saga.SalvarComEventosAsync(estado, [bom, ruim]));

        var lido = await saga.ObterAsync(300);
        var pendentes = await outbox.ObterPendentesAsync(50);

        // Nem o estado novo, nem o evento que tinha dado certo antes do erro.
        Assert.Equal(EPassoDaSaga.AguardandoDiagnostico, lido!.Passo);
        Assert.Null(lido.MotivoDaCompensacao);
        Assert.DoesNotContain(pendentes, l => l.MensagemId == bom.MensagemId);
    }

    [Fact]
    public async Task Varredor_acha_a_saga_parada_alem_do_prazo()
    {
        var repositorio = new SagaRepositorio(_conexao);

        var parada = new EstadoDaSaga
        {
            IdOrdem = 400,
            Passo = EPassoDaSaga.AguardandoExecucao,
            AtualizadoEm = DateTime.UtcNow.AddHours(-3),
        };
        await repositorio.CriarAsync(parada);
        await repositorio.SalvarComEventosAsync(parada, []);

        var recente = new EstadoDaSaga
        {
            IdOrdem = 401,
            Passo = EPassoDaSaga.AguardandoExecucao,
            AtualizadoEm = DateTime.UtcNow,
        };
        await repositorio.CriarAsync(recente);
        await repositorio.SalvarComEventosAsync(recente, []);

        var paradas = await repositorio.ObterParadasAsync(EPassoDaSaga.AguardandoExecucao, TimeSpan.FromHours(1), 10);

        Assert.Contains(paradas, s => s.IdOrdem == 400);
        Assert.DoesNotContain(paradas, s => s.IdOrdem == 401);
    }

    /// <summary>
    /// O orquestrador devolve <c>object</c>, e <c>Outbox.Enfileirar</c> é
    /// genérico. Em produção quem chama conhece o tipo; aqui o teste resolve em
    /// runtime para poder passar a lista inteira de uma vez.
    /// </summary>
    private static EventoOutbox EnfileirarDinamico(object evento, string correlationId)
    {
        var metodo = typeof(Outbox)
            .GetMethod(nameof(Outbox.Enfileirar))!
            .MakeGenericMethod(evento.GetType());

        return (EventoOutbox)metodo.Invoke(null, [evento, correlationId, "os-service", null])!;
    }
}
