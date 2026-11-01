using System.Text;
using PosTech.Contratos;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace PosTech.Mensageria;

/// <summary>
/// Registro do que este serviço já processou.
///
/// O par do outbox do outro lado da fila. O RabbitMQ entrega ao menos uma vez,
/// então a mesma mensagem chega de novo sempre que um ack se perde. Sem este
/// registro, "ao menos uma vez" vira orçamento duplicado e cobrança duplicada.
/// </summary>
public interface IRegistroDeIdempotencia
{
    Task<bool> JaProcessadoAsync(Guid mensagemId, string consumidor, CancellationToken cancellationToken = default);

    Task RegistrarAsync(Guid mensagemId, string tipo, string consumidor, DateTime quando, CancellationToken cancellationToken = default);
}

/// <summary>
/// O que fazer com um evento recebido. O tipo vem do envelope, não da classe
/// C#, pelo mesmo motivo do atributo: renomear classe não pode quebrar a fila.
/// </summary>
public delegate Task TratadorDeEvento(EnvelopeRecebido envelope, CancellationToken cancellationToken);

/// <summary>
/// Um evento que chegou, ainda com o corpo em JSON.
///
/// A desserialização para o tipo concreto fica com o tratador, porque é ele que
/// sabe qual versão do contrato espera.
/// </summary>
public sealed record EnvelopeRecebido(
    Guid MensagemId,
    string Tipo,
    string RoutingKey,
    string CorrelationId,
    string Json)
{
    public T? Ler<T>() where T : notnull =>
        SerializacaoDeEvento.Desserializar<T>(Json) is { } envelope ? envelope.Dados : default;
}

/// <summary>
/// Consome uma fila, com idempotência, retry em memória e dead letter.
///
/// O retry é curto e em memória de propósito. Falha de rede e deadlock de banco
/// passam em segundos; o que não passa em três tentativas não vai passar na
/// quarta, e insistir só atrasa a fila inteira atrás de uma mensagem envenenada.
/// Depois disso a mensagem vai para a dead letter, onde fica visível para
/// alguém olhar em vez de sumir.
/// </summary>
public sealed class ConsumidorDeEventos : IAsyncDisposable
{
    public const int TentativasEmMemoria = 3;

    private readonly ConexaoRabbitMq _conexao;
    private readonly string _nomeDaFila;
    private readonly string _nomeDoConsumidor;
    private readonly IRegistroDeIdempotencia _idempotencia;
    private readonly IReadOnlyDictionary<string, TratadorDeEvento> _tratadores;
    private readonly Func<DateTime> _agora;

    private IChannel? _canal;

    public ConsumidorDeEventos(
        ConexaoRabbitMq conexao,
        string nomeDaFila,
        string nomeDoConsumidor,
        IRegistroDeIdempotencia idempotencia,
        IReadOnlyDictionary<string, TratadorDeEvento> tratadores,
        Func<DateTime>? agora = null)
    {
        _conexao = conexao ?? throw new ArgumentNullException(nameof(conexao));
        _idempotencia = idempotencia ?? throw new ArgumentNullException(nameof(idempotencia));
        _tratadores = tratadores ?? throw new ArgumentNullException(nameof(tratadores));

        if (string.IsNullOrWhiteSpace(nomeDaFila))
            throw new ArgumentException("Nome da fila é obrigatório.", nameof(nomeDaFila));

        if (string.IsNullOrWhiteSpace(nomeDoConsumidor))
            throw new ArgumentException("Nome do consumidor é obrigatório.", nameof(nomeDoConsumidor));

        _nomeDaFila = nomeDaFila;
        _nomeDoConsumidor = nomeDoConsumidor;
        _agora = agora ?? (() => DateTime.UtcNow);
    }

    /// <summary>Eventos que chegaram e foram descartados por repetição. Útil em teste e em métrica.</summary>
    public int DescartadosPorRepeticao { get; private set; }

    /// <summary>Eventos mandados para a dead letter.</summary>
    public int EnviadosParaDeadLetter { get; private set; }

    public async Task IniciarAsync(CancellationToken cancellationToken = default)
    {
        _canal = await _conexao.ObterCanalAsync(cancellationToken).ConfigureAwait(false);

        // Um por vez. Com prefetch alto, uma réplica puxa a fila inteira para a
        // memória e as outras ficam ociosas; pior, uma mensagem lenta segura
        // todas as que vieram atrás dela no mesmo lote.
        await _canal.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken)
            .ConfigureAwait(false);

        var consumidor = new AsyncEventingBasicConsumer(_canal);
        consumidor.ReceivedAsync += AoReceberAsync;

        await _canal.BasicConsumeAsync(
            queue: _nomeDaFila,
            autoAck: false,
            consumer: consumidor,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task AoReceberAsync(object remetente, BasicDeliverEventArgs argumentos)
    {
        var canal = _canal!;
        var json = Encoding.UTF8.GetString(argumentos.Body.Span);

        if (!Guid.TryParse(argumentos.BasicProperties.MessageId, out var mensagemId))
        {
            // Mensagem sem id não dá para deduplicar, então não dá para
            // processar com segurança. Vai direto para a dead letter.
            EnviadosParaDeadLetter++;
            await canal.BasicNackAsync(argumentos.DeliveryTag, multiple: false, requeue: false).ConfigureAwait(false);
            return;
        }

        var tipo = argumentos.BasicProperties.Type ?? string.Empty;
        var correlationId = argumentos.BasicProperties.CorrelationId ?? string.Empty;

        var envelope = new EnvelopeRecebido(mensagemId, tipo, argumentos.RoutingKey, correlationId, json);

        if (await _idempotencia.JaProcessadoAsync(mensagemId, _nomeDoConsumidor).ConfigureAwait(false))
        {
            DescartadosPorRepeticao++;
            await canal.BasicAckAsync(argumentos.DeliveryTag, multiple: false).ConfigureAwait(false);
            return;
        }

        if (!_tratadores.TryGetValue(tipo, out var tratador))
        {
            // A fila recebeu algo que este serviço não trata. É erro de binding,
            // não mensagem envenenada: ack e segue, senão a fila trava.
            await canal.BasicAckAsync(argumentos.DeliveryTag, multiple: false).ConfigureAwait(false);
            return;
        }

        for (var tentativa = 1; tentativa <= TentativasEmMemoria; tentativa++)
        {
            try
            {
                await tratador(envelope, CancellationToken.None).ConfigureAwait(false);

                await _idempotencia
                    .RegistrarAsync(mensagemId, tipo, _nomeDoConsumidor, _agora())
                    .ConfigureAwait(false);

                await canal.BasicAckAsync(argumentos.DeliveryTag, multiple: false).ConfigureAwait(false);
                return;
            }
            catch (Exception) when (tentativa < TentativasEmMemoria)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * tentativa)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                EnviadosParaDeadLetter++;

                // requeue: false com x-dead-letter-exchange na fila manda para a
                // DLQ. Com requeue: true a mesma mensagem voltaria para a frente
                // da fila e giraria para sempre.
                await canal.BasicNackAsync(argumentos.DeliveryTag, multiple: false, requeue: false)
                    .ConfigureAwait(false);
                return;
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
