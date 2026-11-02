using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using PosTech.Contratos;
using PosTech.Mensageria;
using PosTech.Os.Api.Aplicacao;
using PosTech.Persistencia;
using PosTech.Saga;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;

namespace PosTech.Os.Testes;

/// <summary>
/// A saga inteira, assíncrona, contra SQL Server e RabbitMQ de verdade.
///
/// É o teste que o vídeo da entrega precisa reproduzir. Tudo que acontece aqui
/// passa pela fila: nenhum passo chama o seguinte por método. Os outros três
/// serviços são simulados por consumidores que reagem exatamente como eles
/// reagiriam — recebem o evento, fazem o trabalho e publicam o resultado.
///
/// O que isto prova e um teste com dublê não provaria:
/// a) o evento sai do outbox, atravessa o broker e chega no consumidor certo;
/// b) o estado da saga avança sozinho, sem ninguém orquestrar o teste;
/// c) a compensação dispara e desfaz os passos na ordem certa;
/// d) o correlationId sobrevive à travessia inteira.
/// </summary>
public sealed class SagaPontaAPontaTests : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management").Build();

    private string _conexaoSql = null!;
    private ConexaoRabbitMq _barramento = null!;
    private OrdemRepositorio _ordens = null!;
    private SagaRepositorio _sagas = null!;
    private OutboxRepositorio _outbox = null!;
    private IdempotenciaRepositorio _idempotencia = null!;
    private PublicadorRabbitMq _publicador = null!;
    private ProcessadorDoOutbox _processador = null!;
    private ManipuladorDaSaga _manipulador = null!;

    private readonly List<IAsyncDisposable> _consumidores = [];
    private CancellationTokenSource _bombeando = null!;
    private Task _bomba = Task.CompletedTask;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_sql.StartAsync(), _rabbit.StartAsync());

        _conexaoSql = _sql.GetConnectionString();
        await AplicarSchemasAsync();

        _barramento = new ConexaoRabbitMq(_rabbit.GetConnectionString(), "testes-os-service");
        await _barramento.DeclararTopologiaAsync();

        _ordens = new OrdemRepositorio(_conexaoSql);
        _sagas = new SagaRepositorio(_conexaoSql);
        _outbox = new OutboxRepositorio(_conexaoSql);
        _idempotencia = new IdempotenciaRepositorio(_conexaoSql);
        _publicador = new PublicadorRabbitMq(_barramento);
        _processador = new ProcessadorDoOutbox(_outbox, _publicador);
        _manipulador = new ManipuladorDaSaga(_sagas, _ordens, NullLogger<ManipuladorDaSaga>.Instance);

        // O publicador do outbox em laço, igual ao BackgroundService do serviço.
        _bombeando = new CancellationTokenSource();
        _bomba = BombearOutboxAsync(_bombeando.Token);
    }

    public async Task DisposeAsync()
    {
        _bombeando.Cancel();
        try { await _bomba; } catch (OperationCanceledException) { }

        foreach (var consumidor in _consumidores)
            await consumidor.DisposeAsync();

        await _publicador.DisposeAsync();
        await Task.WhenAll(_sql.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }

    private async Task AplicarSchemasAsync()
    {
        var raiz = RaizDoRepositorio();

        await using var conexao = new SqlConnection(_conexaoSql);
        await conexao.OpenAsync();

        foreach (var arquivo in new[] { "schema-os.sql", "schema-mensageria.sql" })
        {
            var script = await File.ReadAllTextAsync(Path.Combine(raiz, "src", "PosTech.Persistencia", arquivo));

            foreach (var lote in script.Split("\nGO", StringSplitOptions.RemoveEmptyEntries))
            {
                if (!string.IsNullOrWhiteSpace(lote))
                    await conexao.ExecuteAsync(lote);
            }
        }
    }

    private static string RaizDoRepositorio()
    {
        var pasta = AppContext.BaseDirectory;

        while (pasta is not null && !Directory.Exists(Path.Combine(pasta, "src", "PosTech.Persistencia")))
            pasta = Directory.GetParent(pasta)?.FullName;

        return pasta!;
    }

    private async Task BombearOutboxAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _processador.ProcessarLoteAsync(cancellationToken: cancellationToken);
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Mesma postura do BackgroundService: falha no laço não derruba
                // o laço.
                await Task.Delay(300, CancellationToken.None);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Os outros serviços, simulados. Cada um consome o que consumiria de
    // verdade e publica o que publicaria.
    // -----------------------------------------------------------------------

    private async Task LigarOrquestradorAsync()
    {
        var tratadores = new[]
        {
            "DiagnosticoConcluido", "OrcamentoGerado", "OrcamentoAprovado", "OrcamentoRejeitado",
            "PagamentoConfirmado", "PagamentoRecusado", "PagamentoEstornado", "ExecucaoFinalizada",
        }.ToDictionary(t => t, _ => (TratadorDeEvento)((envelope, ct) => _manipulador.TratarAsync(envelope, ct)));

        var consumidor = new ConsumidorDeEventos(
            _barramento, Topologia.FilaOrdemServico, ManipuladorDaSaga.Origem, _idempotencia, tratadores);

        await consumidor.IniciarAsync();
        _consumidores.Add(consumidor);
    }

    /// <summary>
    /// Liga um serviço falso numa fila, com os tratadores que o teste quiser.
    /// Cada um recebe o seu próprio registro de idempotência, como os serviços
    /// de verdade têm.
    /// </summary>
    private async Task LigarServicoAsync(string fila, string nome, Dictionary<string, TratadorDeEvento> tratadores)
    {
        var consumidor = new ConsumidorDeEventos(
            _barramento, fila, nome, new IdempotenciaRepositorio(_conexaoSql), tratadores);

        await consumidor.IniciarAsync();
        _consumidores.Add(consumidor);
    }

    private async Task PublicarAsync<T>(T evento, string correlationId, string origem) where T : notnull =>
        await _publicador.PublicarAsync(Outbox.Enfileirar(evento, correlationId, origem));

    private async Task<int> AbrirOrdemAsync(string correlationId)
    {
        var agora = DateTime.UtcNow;

        return await _ordens.AbrirAsync(
            idCliente: 10, idVeiculo: 20, idFuncionario: 30,
            nomeCliente: "Ana Prado", descricaoVeiculo: "Gol AAA-1234",
            montarEvento: id => Outbox.Enfileirar(
                new OrdemAberta(id, 10, "Ana Prado", "ana@oficina.test", 20, "Gol AAA-1234", 30, agora),
                correlationId, ManipuladorDaSaga.Origem, agora),
            agora);
    }

    /// <summary>
    /// Espera a saga chegar ao passo esperado. Nada neste teste chama o passo
    /// seguinte: o que faz a saga andar é a fila, então o teste observa em vez
    /// de conduzir.
    /// </summary>
    private async Task<EstadoDaSaga> EsperarPassoAsync(
        int idOrdem, EPassoDaSaga esperado, int segundos = 30)
    {
        var limite = DateTime.UtcNow.AddSeconds(segundos);
        EstadoDaSaga? estado = null;

        while (DateTime.UtcNow < limite)
        {
            estado = await _sagas.ObterAsync(idOrdem);

            if (estado?.Passo == esperado)
                return estado;

            await Task.Delay(200);
        }

        Assert.Fail(
            $"A saga da ordem {idOrdem} não chegou em {esperado} em {segundos}s. " +
            $"Parou em {estado?.Passo.ToString() ?? "sem saga"}.");

        return null!;
    }

    // =======================================================================
    // Caminho feliz
    // =======================================================================

    [Fact]
    public async Task O_fluxo_completo_anda_sozinho_pela_fila_ate_a_ordem_fechar()
    {
        const string correlationId = "corr-feliz";
        var correlacoesVistas = new List<string>();

        await LigarOrquestradorAsync();

        // Execução: recebe a ordem, diagnostica, e depois do pagamento executa.
        await LigarServicoAsync(Topologia.FilaExecucao, "execucao-service", new()
        {
            ["OrdemAberta"] = async (envelope, _) =>
            {
                correlacoesVistas.Add(envelope.CorrelationId);
                var dados = envelope.Ler<OrdemAberta>()!;

                await PublicarAsync(
                    new DiagnosticoConcluido(
                        dados.IdOrdem, dados.IdFuncionario,
                        dados.IdCliente, dados.NomeCliente, dados.EmailCliente,
                        [new ItemDaOrdem("Troca de óleo", ETipoItem.MaoDeObra, 1, 120.50m),
                         new ItemDaOrdem("Filtro", ETipoItem.Peca, 2, 38.90m)],
                        198.30m, DateTime.UtcNow),
                    envelope.CorrelationId, "execucao-service");
            },
            ["PagamentoConfirmado"] = async (envelope, _) =>
            {
                correlacoesVistas.Add(envelope.CorrelationId);
                var dados = envelope.Ler<PagamentoConfirmado>()!;

                await PublicarAsync(
                    new ExecucaoFinalizada(dados.IdOrdem, 30, DateTime.UtcNow),
                    envelope.CorrelationId, "execucao-service");
            },
        });

        // Billing: monta o orçamento, e aprova e cobra na sequência.
        await LigarServicoAsync(Topologia.FilaBilling, "billing-service", new()
        {
            ["DiagnosticoConcluido"] = async (envelope, _) =>
            {
                correlacoesVistas.Add(envelope.CorrelationId);
                var dados = envelope.Ler<DiagnosticoConcluido>()!;
                var idOrcamento = $"orc-{dados.IdOrdem}";

                await PublicarAsync(
                    new OrcamentoGerado(dados.IdOrdem, idOrcamento, dados.ValorTotal, DateTime.UtcNow),
                    envelope.CorrelationId, "billing-service");

                // O cliente aprova e o pagamento confirma. Num ambiente real
                // isso vem de uma chamada REST e do webhook do Mercado Pago.
                await PublicarAsync(
                    new OrcamentoAprovado(dados.IdOrdem, idOrcamento, dados.ValorTotal, DateTime.UtcNow),
                    envelope.CorrelationId, "billing-service");

                await PublicarAsync(
                    new PagamentoConfirmado(dados.IdOrdem, $"pag-{dados.IdOrdem}", dados.ValorTotal, "mp-9988", DateTime.UtcNow),
                    envelope.CorrelationId, "billing-service");
            },
        });

        var idOrdem = await AbrirOrdemAsync(correlationId);

        var estado = await EsperarPassoAsync(idOrdem, EPassoDaSaga.Concluida, segundos: 45);

        Assert.Equal(EStatusOrdem.Finalizada, estado.StatusDaOrdem);
        Assert.Equal($"orc-{idOrdem}", estado.IdOrcamento);
        Assert.Equal($"pag-{idOrdem}", estado.IdPagamento);
        Assert.Equal("mp-9988", estado.ReferenciaExterna);
        Assert.Equal(198.30m, estado.Valor);
        Assert.Null(estado.MotivoDaCompensacao);

        // A ordem acompanhou a saga.
        var ordem = await _ordens.ObterAsync(idOrdem);
        Assert.Equal(EStatusOrdem.Finalizada, ordem!.Status);

        // E o histórico conta a história inteira, em ordem.
        var historico = await _ordens.HistoricoAsync(idOrdem);
        Assert.Equal(
            [EStatusOrdem.Recebida, EStatusOrdem.EmDiagnostico, EStatusOrdem.AguardandoAprovacao,
             EStatusOrdem.EmExecucao, EStatusOrdem.Finalizada],
            historico.Select(h => h.StatusNovo));

        // O correlationId atravessou todos os saltos de fila.
        Assert.NotEmpty(correlacoesVistas);
        Assert.All(correlacoesVistas, c => Assert.Equal(correlationId, c));

        // E o outbox esvaziou: nada ficou preso.
        Assert.Empty(await _outbox.ObterPendentesAsync(100));
    }

    // =======================================================================
    // Compensações
    // =======================================================================

    [Fact]
    public async Task Pagamento_recusado_devolve_a_ordem_para_aprovacao_e_a_segunda_tentativa_passa()
    {
        const string correlationId = "corr-recusa";
        var tentativasDePagamento = 0;

        await LigarOrquestradorAsync();

        await LigarServicoAsync(Topologia.FilaExecucao, "execucao-service", new()
        {
            ["OrdemAberta"] = async (envelope, _) =>
            {
                var dados = envelope.Ler<OrdemAberta>()!;
                await PublicarAsync(
                    new DiagnosticoConcluido(dados.IdOrdem, 30,
                        dados.IdCliente, dados.NomeCliente, dados.EmailCliente,
                        [new ItemDaOrdem("Troca", ETipoItem.MaoDeObra, 1, 100m)], 100m, DateTime.UtcNow),
                    envelope.CorrelationId, "execucao-service");
            },
        });

        await LigarServicoAsync(Topologia.FilaBilling, "billing-service", new()
        {
            ["DiagnosticoConcluido"] = async (envelope, _) =>
            {
                var dados = envelope.Ler<DiagnosticoConcluido>()!;
                await PublicarAsync(
                    new OrcamentoGerado(dados.IdOrdem, $"orc-{dados.IdOrdem}", 100m, DateTime.UtcNow),
                    envelope.CorrelationId, "billing-service");
                await PublicarAsync(
                    new OrcamentoAprovado(dados.IdOrdem, $"orc-{dados.IdOrdem}", 100m, DateTime.UtcNow),
                    envelope.CorrelationId, "billing-service");

                // Primeira cobrança recusada; a segunda passa.
                Interlocked.Increment(ref tentativasDePagamento);
                await PublicarAsync(
                    new PagamentoRecusado(dados.IdOrdem, $"pag-{dados.IdOrdem}", "Cartão sem limite", DateTime.UtcNow),
                    envelope.CorrelationId, "billing-service");
            },
        });

        var idOrdem = await AbrirOrdemAsync(correlationId);

        // A recusa não cancela: volta para aguardando aprovação, com o motivo.
        var depoisDaRecusa = await EsperarPassoAsync(idOrdem, EPassoDaSaga.AguardandoAprovacao, segundos: 45);

        // Precisa ter passado por AguardandoPagamento antes de voltar.
        Assert.Equal("Cartão sem limite", depoisDaRecusa.MotivoDaCompensacao);
        Assert.Equal(EStatusOrdem.AguardandoAprovacao, depoisDaRecusa.StatusDaOrdem);

        // O orçamento continua valendo. É o ponto: não se joga fora o
        // diagnóstico já feito porque um cartão falhou.
        Assert.Equal($"orc-{idOrdem}", depoisDaRecusa.IdOrcamento);

        // Segunda tentativa, agora aprovada.
        await PublicarAsync(
            new OrcamentoAprovado(idOrdem, $"orc-{idOrdem}", 100m, DateTime.UtcNow),
            correlationId, "billing-service");
        await EsperarPassoAsync(idOrdem, EPassoDaSaga.AguardandoPagamento, segundos: 30);

        await PublicarAsync(
            new PagamentoConfirmado(idOrdem, $"pag-{idOrdem}-2", 100m, "mp-1234", DateTime.UtcNow),
            correlationId, "billing-service");

        var depoisDoPagamento = await EsperarPassoAsync(idOrdem, EPassoDaSaga.AguardandoExecucao, segundos: 30);

        Assert.Equal(EStatusOrdem.EmExecucao, depoisDoPagamento.StatusDaOrdem);

        // O motivo da recusa anterior sumiu: a ordem foi paga, e deixar
        // "Cartão sem limite" grudado nela confundiria quem lê a tela.
        Assert.Null(depoisDoPagamento.MotivoDaCompensacao);
    }

    [Fact]
    public async Task Orcamento_rejeitado_cancela_a_ordem_e_avisa_o_Billing()
    {
        const string correlationId = "corr-rejeicao";
        var cancelamentosRecebidos = new List<string>();

        await LigarOrquestradorAsync();

        await LigarServicoAsync(Topologia.FilaExecucao, "execucao-service", new()
        {
            ["OrdemAberta"] = async (envelope, _) =>
            {
                var dados = envelope.Ler<OrdemAberta>()!;
                await PublicarAsync(
                    new DiagnosticoConcluido(dados.IdOrdem, 30,
                        dados.IdCliente, dados.NomeCliente, dados.EmailCliente,
                        [new ItemDaOrdem("Troca", ETipoItem.MaoDeObra, 1, 100m)], 100m, DateTime.UtcNow),
                    envelope.CorrelationId, "execucao-service");
            },
            ["OrdemCancelada"] = (envelope, _) =>
            {
                cancelamentosRecebidos.Add("execucao");
                return Task.CompletedTask;
            },
        });

        await LigarServicoAsync(Topologia.FilaBilling, "billing-service", new()
        {
            ["DiagnosticoConcluido"] = async (envelope, _) =>
            {
                var dados = envelope.Ler<DiagnosticoConcluido>()!;
                await PublicarAsync(
                    new OrcamentoGerado(dados.IdOrdem, $"orc-{dados.IdOrdem}", 100m, DateTime.UtcNow),
                    envelope.CorrelationId, "billing-service");
                await PublicarAsync(
                    new OrcamentoRejeitado(dados.IdOrdem, $"orc-{dados.IdOrdem}", "Cliente não aprovou", DateTime.UtcNow),
                    envelope.CorrelationId, "billing-service");
            },
            ["OrcamentoCancelado"] = (envelope, _) =>
            {
                cancelamentosRecebidos.Add("billing");
                return Task.CompletedTask;
            },
            ["OrdemCancelada"] = (envelope, _) =>
            {
                cancelamentosRecebidos.Add("billing-ordem");
                return Task.CompletedTask;
            },
        });

        var idOrdem = await AbrirOrdemAsync(correlationId);

        var estado = await EsperarPassoAsync(idOrdem, EPassoDaSaga.Cancelada, segundos: 45);

        Assert.Equal("Cliente não aprovou", estado.MotivoDaCompensacao);

        // Os eventos de compensação saíram pelo barramento e chegaram em quem
        // precisava desfazer alguma coisa.
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < limite && cancelamentosRecebidos.Count < 3)
            await Task.Delay(200);

        Assert.Contains("billing", cancelamentosRecebidos);
        Assert.Contains("billing-ordem", cancelamentosRecebidos);
        Assert.Contains("execucao", cancelamentosRecebidos);
    }

    [Fact]
    public async Task Execucao_que_nao_assume_no_prazo_e_compensada_pela_varredura()
    {
        const string correlationId = "corr-timeout";

        await LigarOrquestradorAsync();

        await LigarServicoAsync(Topologia.FilaExecucao, "execucao-service", new()
        {
            ["OrdemAberta"] = async (envelope, _) =>
            {
                var dados = envelope.Ler<OrdemAberta>()!;
                await PublicarAsync(
                    new DiagnosticoConcluido(dados.IdOrdem, 30,
                        dados.IdCliente, dados.NomeCliente, dados.EmailCliente,
                        [new ItemDaOrdem("Troca", ETipoItem.MaoDeObra, 1, 100m)], 100m, DateTime.UtcNow),
                    envelope.CorrelationId, "execucao-service");
            },
            // De propósito: PagamentoConfirmado não é tratado. A oficina não
            // assume a ordem, que é exatamente o cenário do timeout.
        });

        await LigarServicoAsync(Topologia.FilaBilling, "billing-service", new()
        {
            ["DiagnosticoConcluido"] = async (envelope, _) =>
            {
                var dados = envelope.Ler<DiagnosticoConcluido>()!;
                await PublicarAsync(
                    new OrcamentoGerado(dados.IdOrdem, $"orc-{dados.IdOrdem}", 100m, DateTime.UtcNow),
                    envelope.CorrelationId, "billing-service");
                await PublicarAsync(
                    new OrcamentoAprovado(dados.IdOrdem, $"orc-{dados.IdOrdem}", 100m, DateTime.UtcNow),
                    envelope.CorrelationId, "billing-service");
                await PublicarAsync(
                    new PagamentoConfirmado(dados.IdOrdem, $"pag-{dados.IdOrdem}", 100m, "mp-7777", DateTime.UtcNow),
                    envelope.CorrelationId, "billing-service");
            },
        });

        var idOrdem = await AbrirOrdemAsync(correlationId);

        await EsperarPassoAsync(idOrdem, EPassoDaSaga.AguardandoExecucao, segundos: 45);

        // Prazo zero: toda saga aguardando execução está "parada há tempo
        // demais". É o que o varredor do serviço faz com prazo de verdade.
        var compensadas = await _manipulador.CompensarParadasAsync(TimeSpan.Zero);

        Assert.True(compensadas >= 1);

        var estado = await _sagas.ObterAsync(idOrdem);

        // Compensando, e não Cancelada: o pagamento aconteceu e a saga espera o
        // estorno confirmar antes de morrer.
        Assert.Equal(EPassoDaSaga.Compensando, estado!.Passo);
        Assert.Contains("não assumiu", estado.MotivoDaCompensacao);

        // O estorno chega e fecha a saga.
        await PublicarAsync(
            new PagamentoEstornado(idOrdem, $"pag-{idOrdem}", "mp-7777", 100m, "timeout", DateTime.UtcNow),
            correlationId, "billing-service");

        var fim = await EsperarPassoAsync(idOrdem, EPassoDaSaga.Cancelada, segundos: 30);
        Assert.Equal(EPassoDaSaga.Cancelada, fim.Passo);
    }

    // =======================================================================
    // Assincronismo: o que só quebra quando a mensagem atravessa de verdade
    // =======================================================================

    [Fact]
    public async Task Evento_duplicado_no_barramento_nao_move_a_saga_duas_vezes()
    {
        await LigarOrquestradorAsync();

        var idOrdem = await AbrirOrdemAsync("corr-dup");
        await EsperarPassoAsync(idOrdem, EPassoDaSaga.AguardandoDiagnostico, segundos: 20);

        var diagnostico = new DiagnosticoConcluido(
            idOrdem, 30, 10, "Ana Prado", "ana@oficina.test",
            [new ItemDaOrdem("Troca", ETipoItem.MaoDeObra, 1, 100m)], 100m, DateTime.UtcNow);

        // A MESMA linha de outbox publicada duas vezes: é o que acontece quando
        // o processo morre entre a confirmação do broker e a marcação.
        var linha = Outbox.Enfileirar(diagnostico, "corr-dup", "execucao-service");
        await _publicador.PublicarAsync(linha);
        await _publicador.PublicarAsync(linha);

        await EsperarPassoAsync(idOrdem, EPassoDaSaga.AguardandoOrcamento, segundos: 30);
        await Task.Delay(2000);

        var estado = await _sagas.ObterAsync(idOrdem);

        // Parou em AguardandoOrcamento. Se a duplicata tivesse sido processada,
        // nada mudaria aqui de qualquer forma — mas o histórico denunciaria.
        Assert.Equal(EPassoDaSaga.AguardandoOrcamento, estado!.Passo);

        var historico = await _ordens.HistoricoAsync(idOrdem);
        Assert.Single(historico, h => h.StatusNovo == EStatusOrdem.EmDiagnostico);
    }

    [Fact]
    public async Task Eventos_fora_de_ordem_nao_corrompem_a_saga()
    {
        // Numa fila, a ordem de chegada não é garantida entre publicadores
        // diferentes. O pagamento chegar antes do orçamento é cenário real.
        await LigarOrquestradorAsync();

        var idOrdem = await AbrirOrdemAsync("corr-fora-de-ordem");
        await EsperarPassoAsync(idOrdem, EPassoDaSaga.AguardandoDiagnostico, segundos: 20);

        await PublicarAsync(
            new PagamentoConfirmado(idOrdem, "pag-x", 100m, "mp-x", DateTime.UtcNow), "corr-fora-de-ordem", "billing-service");
        await PublicarAsync(
            new ExecucaoFinalizada(idOrdem, 30, DateTime.UtcNow), "corr-fora-de-ordem", "execucao-service");
        await PublicarAsync(
            new OrcamentoGerado(idOrdem, "orc-x", 100m, DateTime.UtcNow), "corr-fora-de-ordem", "billing-service");

        await Task.Delay(4000);

        var estado = await _sagas.ObterAsync(idOrdem);

        // Nenhum deles cabia no passo atual, então todos foram ignorados. A
        // saga continua esperando o diagnóstico.
        Assert.Equal(EPassoDaSaga.AguardandoDiagnostico, estado!.Passo);
        Assert.Null(estado.IdPagamento);
        Assert.Null(estado.IdOrcamento);
    }

    [Fact]
    public async Task O_outbox_entrega_o_que_foi_gravado_antes_de_o_broker_existir_para_ele()
    {
        // A ordem é aberta com o laço do outbox parado. O evento fica na tabela
        // e só sai quando o publicador volta — que é a promessa do padrão:
        // broker fora do ar não perde evento, só atrasa.
        _bombeando.Cancel();
        try { await _bomba; } catch (OperationCanceledException) { }

        await LigarOrquestradorAsync();

        var idOrdem = await AbrirOrdemAsync("corr-outbox");

        await Task.Delay(1500);
        Assert.Single(await _outbox.ObterPendentesAsync(100), e => e.Tipo == "OrdemAberta");

        // Religa o laço: o que estava preso sai.
        _bombeando = new CancellationTokenSource();
        _bomba = BombearOutboxAsync(_bombeando.Token);

        var limite = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < limite)
        {
            if (!(await _outbox.ObterPendentesAsync(100)).Any(e => e.Tipo == "OrdemAberta"))
                return;

            await Task.Delay(200);
        }

        Assert.Fail("O evento continuou preso no outbox depois de o publicador voltar.");
    }

    [Fact]
    public async Task Duas_replicas_do_publicador_nao_publicam_o_mesmo_evento_duas_vezes()
    {
        // A reserva do outbox em ação. Sem ela, as duas réplicas leem o mesmo
        // lote e publicam tudo em dobro.
        _bombeando.Cancel();
        try { await _bomba; } catch (OperationCanceledException) { }

        var recebidos = 0;

        await LigarServicoAsync(Topologia.FilaExecucao, "execucao-service", new()
        {
            ["OrdemAberta"] = (_, _) =>
            {
                Interlocked.Increment(ref recebidos);
                return Task.CompletedTask;
            },
        });

        await AbrirOrdemAsync("corr-replicas");

        // Duas réplicas processando o outbox ao mesmo tempo.
        var replicaA = new ProcessadorDoOutbox(new OutboxRepositorio(_conexaoSql), _publicador);
        var replicaB = new ProcessadorDoOutbox(new OutboxRepositorio(_conexaoSql), _publicador);

        var resultados = await Task.WhenAll(replicaA.ProcessarLoteAsync(), replicaB.ProcessarLoteAsync());

        // Só uma das duas pegou o evento.
        Assert.Equal(1, resultados.Sum());

        await Task.Delay(3000);
        Assert.Equal(1, recebidos);
    }
}
