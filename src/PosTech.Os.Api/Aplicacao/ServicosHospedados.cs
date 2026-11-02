using PosTech.Contratos;
using PosTech.Mensageria;

namespace PosTech.Os.Api.Aplicacao;

/// <summary>
/// Entrega o que está no outbox, em laço.
///
/// Fica separado da API de propósito: a requisição HTTP termina assim que a
/// transação comita, sem esperar o broker. Se a publicação fosse no caminho da
/// requisição, um RabbitMQ lento viraria uma API lenta — e um RabbitMQ fora do
/// ar viraria uma API fora do ar.
/// </summary>
public sealed class PublicadorDoOutbox : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IntervaloAposFalha = TimeSpan.FromSeconds(15);

    private readonly ProcessadorDoOutbox _processador;
    private readonly ILogger<PublicadorDoOutbox> _log;

    public PublicadorDoOutbox(ProcessadorDoOutbox processador, ILogger<PublicadorDoOutbox> log)
    {
        _processador = processador;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var espera = Intervalo;

            try
            {
                var publicados = await _processador.ProcessarLoteAsync(cancellationToken: stoppingToken);

                if (publicados > 0)
                    _log.LogInformation("{Quantidade} eventos publicados.", publicados);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception excecao)
            {
                // O processador já trata falha por evento e agenda o retry; se
                // estourou aqui, o problema é mais embaixo — conexão de banco,
                // normalmente. Esperar mais evita encher o log de erro idêntico.
                _log.LogError(excecao, "Falha no laço do outbox.");
                espera = IntervaloAposFalha;
            }

            try
            {
                await Task.Delay(espera, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

/// <summary>
/// Liga o consumidor da fila do OS Service no manipulador da saga.
/// </summary>
public sealed class ConsumidorDaSaga : BackgroundService
{
    private readonly ConexaoRabbitMq _conexao;
    private readonly IServiceScopeFactory _escopos;
    private readonly IRegistroDeIdempotencia _idempotencia;
    private readonly ILogger<ConsumidorDaSaga> _log;

    public ConsumidorDaSaga(
        ConexaoRabbitMq conexao,
        IServiceScopeFactory escopos,
        IRegistroDeIdempotencia idempotencia,
        ILogger<ConsumidorDaSaga> log)
    {
        _conexao = conexao;
        _escopos = escopos;
        _idempotencia = idempotencia;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Declarar na subida de todo serviço é seguro porque é idempotente, e
        // evita a ordem de inicialização virar requisito: nenhum serviço
        // precisa estar no ar antes do outro.
        await _conexao.DeclararTopologiaAsync(stoppingToken);

        var tratadores = new[]
        {
            "DiagnosticoConcluido", "OrcamentoGerado", "OrcamentoAprovado", "OrcamentoRejeitado",
            "PagamentoConfirmado", "PagamentoRecusado", "PagamentoEstornado", "ExecucaoFinalizada",
        }.ToDictionary(tipo => tipo, _ => (TratadorDeEvento)TratarAsync);

        var consumidor = new ConsumidorDeEventos(
            _conexao, Topologia.FilaOrdemServico, ManipuladorDaSaga.Origem, _idempotencia, tratadores);

        await consumidor.IniciarAsync(stoppingToken);

        _log.LogInformation("Consumindo {Fila}.", Topologia.FilaOrdemServico);

        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private async Task TratarAsync(EnvelopeRecebido envelope, CancellationToken cancellationToken)
    {
        // Escopo por mensagem: o manipulador depende de repositórios, que são
        // scoped. Reusar o escopo da aplicação vazaria conexão entre mensagens.
        using var escopo = _escopos.CreateScope();

        var manipulador = escopo.ServiceProvider.GetRequiredService<ManipuladorDaSaga>();

        using var _ = _log.BeginScope(new Dictionary<string, object>
        {
            ["correlationId"] = envelope.CorrelationId,
        });

        await manipulador.TratarAsync(envelope, cancellationToken);
    }
}

/// <summary>
/// Varre sagas paradas e dispara a compensação por timeout.
///
/// É o único caminho da saga que não começa num evento: ninguém publica o fato
/// de a execução não ter assumido a ordem.
/// </summary>
public sealed class VarredorDeTimeout : BackgroundService
{
    private readonly IServiceScopeFactory _escopos;
    private readonly ILogger<VarredorDeTimeout> _log;
    private readonly TimeSpan _prazo;
    private readonly TimeSpan _intervalo;

    public VarredorDeTimeout(IServiceScopeFactory escopos, IConfiguration configuracao, ILogger<VarredorDeTimeout> log)
    {
        _escopos = escopos;
        _log = log;

        // Curto em demonstração, para a compensação caber no vídeo; longo em
        // operação real, onde um reparo leva dias.
        // Lidos como double, e não int: fração de minuto é o que permite ao
        // cenário BDD de compensação por prazo rodar em segundos em vez de em
        // uma hora. Em produção os valores continuam inteiros.
        _prazo = TimeSpan.FromMinutes(configuracao.GetValue("Saga:PrazoDeExecucaoEmMinutos", 60.0));
        _intervalo = TimeSpan.FromMinutes(configuracao.GetValue("Saga:IntervaloDaVarreduraEmMinutos", 5.0));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var escopo = _escopos.CreateScope();
                var manipulador = escopo.ServiceProvider.GetRequiredService<ManipuladorDaSaga>();

                var compensadas = await manipulador.CompensarParadasAsync(_prazo, stoppingToken);

                if (compensadas > 0)
                    _log.LogWarning("{Quantidade} sagas compensadas por timeout.", compensadas);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception excecao)
            {
                _log.LogError(excecao, "Falha na varredura de timeout.");
            }

            try
            {
                await Task.Delay(_intervalo, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
