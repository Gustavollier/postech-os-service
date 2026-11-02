using Dapper;
using Microsoft.Data.SqlClient;
using PosTech.Contratos;
using PosTech.Mensageria;
using PosTech.Persistencia;
using PosTech.Saga;
using Testcontainers.MsSql;

namespace PosTech.Os.Testes;

/// <summary>
/// As regras de negócio da ordem, contra um SQL Server de verdade.
///
/// Tudo aqui é regra que o usuário percebe: em que ordem a recepção vê a fila,
/// o que o cliente consegue enxergar, e o que o histórico conta. São as que
/// mais custam caro quando quebram em silêncio.
/// </summary>
public sealed class RegrasDaOrdemTests : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    private string _conexao = null!;
    private OrdemRepositorio _ordens = null!;
    private SagaRepositorio _sagas = null!;
    private OutboxRepositorio _outbox = null!;

    public async Task InitializeAsync()
    {
        await _sql.StartAsync();
        _conexao = _sql.GetConnectionString();

        var raiz = AppContext.BaseDirectory;
        while (raiz is not null && !Directory.Exists(Path.Combine(raiz, "src", "PosTech.Persistencia")))
            raiz = Directory.GetParent(raiz)?.FullName;

        await using var conexao = new SqlConnection(_conexao);
        await conexao.OpenAsync();

        foreach (var arquivo in new[] { "schema-os.sql", "schema-mensageria.sql" })
        {
            var script = await File.ReadAllTextAsync(Path.Combine(raiz!, "src", "PosTech.Persistencia", arquivo));

            foreach (var lote in script.Split("\nGO", StringSplitOptions.RemoveEmptyEntries))
                if (!string.IsNullOrWhiteSpace(lote))
                    await conexao.ExecuteAsync(lote);
        }

        _ordens = new OrdemRepositorio(_conexao);
        _sagas = new SagaRepositorio(_conexao);
        _outbox = new OutboxRepositorio(_conexao);
    }

    public async Task DisposeAsync() => await _sql.DisposeAsync();

    private async Task<int> AbrirAsync(int idCliente = 10, DateTime? quando = null, string correlationId = "corr-1")
    {
        var agora = quando ?? DateTime.UtcNow;

        return await _ordens.AbrirAsync(
            idCliente, idVeiculo: 20, idFuncionario: 30,
            nomeCliente: $"Cliente {idCliente}", descricaoVeiculo: "Gol AAA-1234",
            montarEvento: id => Outbox.Enfileirar(
                new OrdemAberta(id, idCliente, $"Cliente {idCliente}", "c@oficina.test", 20, "Gol AAA-1234", 30, agora),
                correlationId, "os-service", agora),
            agora);
    }

    /// <summary>Leva a saga até um passo, aplicando as decisões de verdade.</summary>
    private async Task LevarAteAsync(int idOrdem, EStatusOrdem status)
    {
        var caminho = new (EStatusOrdem Alvo, Func<EstadoDaSaga, DateTime, Decisao> Passo)[]
        {
            (EStatusOrdem.EmDiagnostico, (e, a) => OrquestradorDaSaga.Avancar(e, new DiagnosticoConcluido(
                idOrdem, 30, 10, "Ana Prado", "ana@oficina.test",
                [new ItemDaOrdem("Troca", ETipoItem.MaoDeObra, 1, 100m)], 100m, a), a)),
            (EStatusOrdem.AguardandoAprovacao, (e, a) => OrquestradorDaSaga.Avancar(e, new OrcamentoGerado(
                idOrdem, $"orc-{idOrdem}", 100m, a), a)),
            (EStatusOrdem.AguardandoAprovacao, (e, a) => OrquestradorDaSaga.Avancar(e, new OrcamentoAprovado(
                idOrdem, $"orc-{idOrdem}", 100m, a), a)),
            (EStatusOrdem.EmExecucao, (e, a) => OrquestradorDaSaga.Avancar(e, new PagamentoConfirmado(
                idOrdem, $"pag-{idOrdem}", 100m, "mp-1", a), a)),
            (EStatusOrdem.Finalizada, (e, a) => OrquestradorDaSaga.Avancar(e, new ExecucaoFinalizada(idOrdem, 30, a), a)),
        };

        foreach (var (alvo, passo) in caminho)
        {
            var estado = await _sagas.ObterAsync(idOrdem);
            var antes = estado!.StatusDaOrdem;
            var decisao = passo(estado, DateTime.UtcNow);

            if (decisao.Mudou)
                await _ordens.AplicarDecisaoAsync(decisao.Estado, antes, null, []);

            if (decisao.Estado.StatusDaOrdem == status && alvo == status)
                return;
        }
    }

    // -----------------------------------------------------------------------
    // Abertura
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Abrir_cria_ordem_saga_historico_e_evento_de_uma_vez()
    {
        var id = await AbrirAsync();

        var ordem = await _ordens.ObterAsync(id);
        var saga = await _sagas.ObterAsync(id);
        var historico = await _ordens.HistoricoAsync(id);
        var pendentes = await _outbox.ObterPendentesAsync(10);

        Assert.NotNull(ordem);
        Assert.Equal(EStatusOrdem.Recebida, ordem.Status);
        Assert.Equal("Cliente 10", ordem.NomeCliente);

        Assert.NotNull(saga);
        Assert.Equal(EPassoDaSaga.AguardandoDiagnostico, saga.Passo);

        var primeira = Assert.Single(historico);
        Assert.Null(primeira.StatusAnterior);
        Assert.Equal(EStatusOrdem.Recebida, primeira.StatusNovo);

        Assert.Single(pendentes, e => e.Tipo == "OrdemAberta");
    }

    [Fact]
    public async Task Se_o_evento_nao_couber_a_ordem_inteira_volta_atras()
    {
        // A promessa da transação: ou nasce tudo, ou não nasce nada. Uma ordem
        // sem o evento seria invisível para os outros serviços para sempre.
        var antes = (await _ordens.ListarAsync(null)).Count;

        await Assert.ThrowsAnyAsync<Exception>(() => _ordens.AbrirAsync(
            idCliente: 99, idVeiculo: 20, idFuncionario: 30,
            nomeCliente: "Teste", descricaoVeiculo: "Gol",
            montarEvento: id =>
            {
                var evento = Outbox.Enfileirar(
                    new OrdemAberta(id, 99, "Teste", "t@t.test", 20, "Gol", 30, DateTime.UtcNow),
                    "corr-x", "os-service");
                evento.Tipo = new string('x', 500); // a coluna é NVARCHAR(100)
                return evento;
            },
            DateTime.UtcNow));

        Assert.Equal(antes, (await _ordens.ListarAsync(null)).Count);
        Assert.DoesNotContain(await _ordens.ListarAsync(99), o => o.IdCliente == 99);
    }

    // -----------------------------------------------------------------------
    // Listagem: a ordem em que a recepção vê a fila
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_fila_da_recepcao_prioriza_execucao_e_deixa_recebidas_por_ultimo()
    {
        var emExecucao = await AbrirAsync();
        await LevarAteAsync(emExecucao, EStatusOrdem.EmExecucao);

        var aguardando = await AbrirAsync();
        await LevarAteAsync(aguardando, EStatusOrdem.AguardandoAprovacao);

        var emDiagnostico = await AbrirAsync();
        await LevarAteAsync(emDiagnostico, EStatusOrdem.EmDiagnostico);

        var recebida = await AbrirAsync();

        var fila = await _ordens.ListarAsync(null);
        var ids = fila.Select(o => o.Id).ToList();

        // Em execução primeiro: é o carro que já está na bancada e precisa
        // sair. Recebida por último: ainda nem foi olhado.
        Assert.Equal(
            [emExecucao, aguardando, emDiagnostico, recebida],
            ids.Where(i => new[] { emExecucao, aguardando, emDiagnostico, recebida }.Contains(i)));
    }

    [Fact]
    public async Task Dentro_do_mesmo_status_as_mais_antigas_vem_na_frente()
    {
        var ontem = DateTime.UtcNow.AddDays(-1);
        var antiga = await AbrirAsync(quando: ontem);
        var nova = await AbrirAsync();

        var fila = (await _ordens.ListarAsync(null)).Select(o => o.Id).ToList();

        Assert.True(
            fila.IndexOf(antiga) < fila.IndexOf(nova),
            "A ordem mais antiga tem que aparecer antes da mais nova dentro do mesmo status.");
    }

    [Fact]
    public async Task Filtrar_por_cliente_mostra_so_o_que_e_dele()
    {
        // É por aqui que a checagem de posse limita o cliente. Vazar ordem de
        // outro cliente foi uma das falhas fechadas na Fase 3.
        var daAna = await AbrirAsync(idCliente: 10);
        var doBruno = await AbrirAsync(idCliente: 11);

        var deAna = await _ordens.ListarAsync(10);

        Assert.Contains(deAna, o => o.Id == daAna);
        Assert.DoesNotContain(deAna, o => o.Id == doBruno);
        Assert.All(deAna, o => Assert.Equal(10, o.IdCliente));
    }

    [Fact]
    public async Task Sem_filtro_a_equipe_ve_todas()
    {
        var daAna = await AbrirAsync(idCliente: 10);
        var doBruno = await AbrirAsync(idCliente: 11);

        var todas = await _ordens.ListarAsync(null);

        Assert.Contains(todas, o => o.Id == daAna);
        Assert.Contains(todas, o => o.Id == doBruno);
    }

    // -----------------------------------------------------------------------
    // Histórico
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Cada_transicao_vira_uma_linha_de_historico_em_ordem()
    {
        var id = await AbrirAsync();
        await LevarAteAsync(id, EStatusOrdem.Finalizada);

        var historico = await _ordens.HistoricoAsync(id);

        Assert.Equal(
            [EStatusOrdem.Recebida, EStatusOrdem.EmDiagnostico, EStatusOrdem.AguardandoAprovacao,
             EStatusOrdem.EmExecucao, EStatusOrdem.Finalizada],
            historico.Select(h => h.StatusNovo));

        // O "de onde veio" encadeia com o "para onde foi" da linha anterior.
        for (var i = 1; i < historico.Count; i++)
            Assert.Equal(historico[i - 1].StatusNovo, historico[i].StatusAnterior);
    }

    [Fact]
    public async Task Transicao_da_saga_que_nao_muda_o_status_nao_polui_o_historico()
    {
        // Aprovar o orçamento move a saga de AguardandoAprovacao para
        // AguardandoPagamento, mas para o cliente nada mudou: ele continua
        // aguardando. Uma linha aqui seria ruído na tela dele.
        var id = await AbrirAsync();
        await LevarAteAsync(id, EStatusOrdem.AguardandoAprovacao);

        var antes = (await _ordens.HistoricoAsync(id)).Count;

        var estado = await _sagas.ObterAsync(id);
        var decisao = OrquestradorDaSaga.Avancar(
            estado!, new OrcamentoAprovado(id, $"orc-{id}", 100m, DateTime.UtcNow), DateTime.UtcNow);

        await _ordens.AplicarDecisaoAsync(decisao.Estado, EStatusOrdem.AguardandoAprovacao, null, []);

        Assert.True(decisao.Mudou, "A saga tem que ter avançado.");
        Assert.Equal(EPassoDaSaga.AguardandoPagamento, (await _sagas.ObterAsync(id))!.Passo);
        Assert.Equal(antes, (await _ordens.HistoricoAsync(id)).Count);
    }

    [Fact]
    public async Task O_motivo_da_compensacao_fica_gravado_no_historico()
    {
        // É o que o cliente lê na tela quando a ordem dele volta atrás. Sem o
        // motivo, o status muda sozinho e ninguém sabe por quê.
        var id = await AbrirAsync();
        await LevarAteAsync(id, EStatusOrdem.AguardandoAprovacao);

        var estado = await _sagas.ObterAsync(id);
        var decisao = OrquestradorDaSaga.Avancar(
            estado!, new OrcamentoRejeitado(id, $"orc-{id}", "Cliente achou caro", DateTime.UtcNow), DateTime.UtcNow);

        await _ordens.AplicarDecisaoAsync(
            decisao.Estado, EStatusOrdem.AguardandoAprovacao, decisao.Estado.MotivoDaCompensacao, []);

        var saga = await _sagas.ObterAsync(id);
        Assert.Equal(EPassoDaSaga.Cancelada, saga!.Passo);
        Assert.Equal("Cliente achou caro", saga.MotivoDaCompensacao);
    }

    [Fact]
    public async Task A_ordem_registra_quando_foi_atualizada_pela_ultima_vez()
    {
        var id = await AbrirAsync();
        Assert.Null((await _ordens.ObterAsync(id))!.AtualizadoEm);

        await LevarAteAsync(id, EStatusOrdem.EmDiagnostico);

        var ordem = await _ordens.ObterAsync(id);
        Assert.NotNull(ordem!.AtualizadoEm);
        Assert.True(ordem.AtualizadoEm >= ordem.CriadoEm);
    }

    // -----------------------------------------------------------------------
    // Leituras que a API expõe
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Ordem_inexistente_devolve_nulo_em_vez_de_estourar()
    {
        Assert.Null(await _ordens.ObterAsync(999_999));
        Assert.Empty(await _ordens.HistoricoAsync(999_999));
        Assert.Null(await _sagas.ObterAsync(999_999));
    }

    [Fact]
    public async Task O_status_lido_do_banco_e_o_enum_certo_e_nao_um_numero_solto()
    {
        // Esta é a leitura que estourava: TINYINT no banco, enum no record
        // posicional, e o Dapper não casava o construtor.
        var id = await AbrirAsync();
        await LevarAteAsync(id, EStatusOrdem.EmExecucao);

        var ordem = await _ordens.ObterAsync(id);

        Assert.Equal(EStatusOrdem.EmExecucao, ordem!.Status);
        Assert.Equal("EmExecucao", ordem.Status.ToString());
    }
}
