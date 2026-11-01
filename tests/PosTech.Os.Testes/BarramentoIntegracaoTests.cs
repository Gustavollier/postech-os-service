using System.Text;
using PosTech.Contratos;
using PosTech.Mensageria;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace PosTech.Os.Testes;

/// <summary>
/// O evento atravessando um RabbitMQ de verdade.
///
/// É o critério de pronto da Etapa 1. Dublê não serve aqui: o que precisa ser
/// provado é a topologia — se o binding casa com a routing key, se a mensagem
/// sobrevive persistente, se a dead letter recebe o que falhou. Nada disso
/// aparece em teste com dublê, e tudo isso quebra silenciosamente em produção.
/// </summary>
public sealed class BarramentoIntegracaoTests : IAsyncLifetime
{
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management").Build();

    private ConexaoRabbitMq _conexao = null!;

    public async Task InitializeAsync()
    {
        await _rabbit.StartAsync();

        _conexao = new ConexaoRabbitMq(_rabbit.GetConnectionString(), "testes-fase4");
        await _conexao.DeclararTopologiaAsync();
    }

    public async Task DisposeAsync()
    {
        await _conexao.DisposeAsync();
        await _rabbit.DisposeAsync();
    }

    private static OrdemAberta UmaOrdem(int id = 1) => new(
        IdOrdem: id, IdCliente: 10, NomeCliente: "Ana Prado", EmailCliente: "ana@oficina.test",
        IdVeiculo: 20, DescricaoVeiculo: "Gol AAA-1234", IdFuncionario: 30,
        AbertaEm: new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task OrdemAberta_publicada_pelo_OS_chega_no_Execucao()
    {
        var recebido = new TaskCompletionSource<EnvelopeRecebido>(TaskCreationOptions.RunContinuationsAsynchronously);

        var consumidor = new ConsumidorDeEventos(
            _conexao,
            Topologia.FilaExecucao,
            nomeDoConsumidor: "execucao-service",
            new IdempotenciaEmMemoria(),
            new Dictionary<string, TratadorDeEvento>
            {
                ["OrdemAberta"] = (envelope, _) =>
                {
                    recebido.TrySetResult(envelope);
                    return Task.CompletedTask;
                },
            });

        await consumidor.IniciarAsync();

        // O caminho real: o caso de uso enfileira no outbox e o processador entrega.
        var repositorio = new OutboxEmMemoria();
        repositorio.Gravar(Outbox.Enfileirar(UmaOrdem(42), "corr-abc", "os-service"));

        await using var publicador = new PublicadorRabbitMq(_conexao);
        var processador = new ProcessadorDoOutbox(repositorio, publicador);

        var publicados = await processador.ProcessarLoteAsync();

        Assert.Equal(1, publicados);
        Assert.Equal(EStatusOutbox.Publicado, repositorio.Linhas[0].Status);

        var envelope = await Esperar(recebido.Task);

        Assert.Equal("OrdemAberta", envelope.Tipo);
        Assert.Equal(RoutingKeys.OrdemAberta, envelope.RoutingKey);

        // O correlationId sobreviveu à travessia da fila. É isso que mantém o
        // trace inteiro quando o fluxo sai do HTTP e entra na mensageria.
        Assert.Equal("corr-abc", envelope.CorrelationId);
        Assert.Equal(repositorio.Linhas[0].MensagemId, envelope.MensagemId);

        var dados = envelope.Ler<OrdemAberta>();
        Assert.NotNull(dados);
        Assert.Equal(42, dados.IdOrdem);
        Assert.Equal("Ana Prado", dados.NomeCliente);
    }

    [Fact]
    public async Task A_mesma_mensagem_entregue_duas_vezes_so_e_processada_uma()
    {
        var processadas = 0;
        var idempotencia = new IdempotenciaEmMemoria();
        var segundaEntrega = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var consumidor = new ConsumidorDeEventos(
            _conexao,
            Topologia.FilaExecucao,
            nomeDoConsumidor: "execucao-service",
            idempotencia,
            new Dictionary<string, TratadorDeEvento>
            {
                ["OrdemAberta"] = (_, _) =>
                {
                    Interlocked.Increment(ref processadas);
                    return Task.CompletedTask;
                },
            });

        await consumidor.IniciarAsync();

        var linha = Outbox.Enfileirar(UmaOrdem(7), "corr-dup", "os-service");
        var repositorio = new OutboxEmMemoria();
        repositorio.Gravar(linha);

        await using var publicador = new PublicadorRabbitMq(_conexao);

        // Publica a MESMA linha duas vezes, que é exatamente o que acontece
        // quando o processo morre entre a confirmação do broker e a marcação
        // no outbox.
        await publicador.PublicarAsync(linha);
        await publicador.PublicarAsync(linha);

        await EsperarAte(() => consumidor.DescartadosPorRepeticao >= 1, segundaEntrega);

        Assert.Equal(1, processadas);
        Assert.Equal(1, consumidor.DescartadosPorRepeticao);
    }

    [Fact]
    public async Task Mensagem_que_falha_sempre_termina_na_dead_letter()
    {
        var consumidor = new ConsumidorDeEventos(
            _conexao,
            Topologia.FilaExecucao,
            nomeDoConsumidor: "execucao-service",
            new IdempotenciaEmMemoria(),
            new Dictionary<string, TratadorDeEvento>
            {
                ["OrdemAberta"] = (_, _) => throw new InvalidOperationException("falha de propósito"),
            });

        await consumidor.IniciarAsync();

        var linha = Outbox.Enfileirar(UmaOrdem(13), "corr-dlq", "os-service");
        await using var publicador = new PublicadorRabbitMq(_conexao);
        await publicador.PublicarAsync(linha);

        var canal = await _conexao.ObterCanalAsync();
        var dlq = Topologia.DeadLetterDe(Topologia.FilaExecucao);

        BasicGetResult? naDeadLetter = null;
        for (var tentativa = 0; tentativa < 60 && naDeadLetter is null; tentativa++)
        {
            await Task.Delay(250);
            naDeadLetter = await canal.BasicGetAsync(dlq, autoAck: true);
        }

        Assert.NotNull(naDeadLetter);
        Assert.Equal(1, consumidor.EnviadosParaDeadLetter);

        var json = Encoding.UTF8.GetString(naDeadLetter.Body.Span);
        var envelope = SerializacaoDeEvento.Desserializar<OrdemAberta>(json);

        Assert.NotNull(envelope);
        Assert.Equal(13, envelope.Dados.IdOrdem);
        Assert.Equal("corr-dlq", envelope.CorrelationId);
    }

    private static async Task<T> Esperar<T>(Task<T> tarefa)
    {
        var concluida = await Task.WhenAny(tarefa, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.True(concluida == tarefa, "O evento não chegou no consumidor dentro de 20 segundos.");

        return await tarefa;
    }

    private static async Task EsperarAte(Func<bool> condicao, TaskCompletionSource _)
    {
        for (var tentativa = 0; tentativa < 80; tentativa++)
        {
            if (condicao())
                return;

            await Task.Delay(250);
        }

        Assert.True(condicao(), "A condição esperada não aconteceu dentro de 20 segundos.");
    }
}
