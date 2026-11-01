using PosTech.Contratos;
using PosTech.Mensageria;
using PosTech.Persistencia;
using PosTech.Saga;

namespace PosTech.Os.Api.Aplicacao;

/// <summary>
/// A cola entre o barramento e a máquina da saga.
///
/// Faz três coisas e nada mais: carrega o estado, passa o evento ao
/// orquestrador, persiste o que ele decidiu. A regra inteira está no
/// <see cref="OrquestradorDaSaga"/>, que é função pura — aqui só mora o I/O.
/// </summary>
public sealed class ManipuladorDaSaga
{
    public const string Origem = "os-service";

    private readonly ISagaRepositorio _sagas;
    private readonly IOrdemRepositorio _ordens;
    private readonly ILogger<ManipuladorDaSaga> _log;
    private readonly Func<DateTime> _agora;

    public ManipuladorDaSaga(
        ISagaRepositorio sagas,
        IOrdemRepositorio ordens,
        ILogger<ManipuladorDaSaga> log,
        Func<DateTime>? agora = null)
    {
        _sagas = sagas;
        _ordens = ordens;
        _log = log;
        _agora = agora ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Trata um evento vindo da fila. Devolve <c>false</c> quando não havia
    /// saga para aquela ordem, o que é um aviso e não um erro: pode ser evento
    /// de uma ordem que este serviço não conhece.
    /// </summary>
    public async Task<bool> TratarAsync(EnvelopeRecebido envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var evento = Desembrulhar(envelope);

        if (evento is null)
        {
            _log.LogWarning("Evento {Tipo} chegou na fila do OS Service e não é tratado aqui.", envelope.Tipo);
            return true;
        }

        var idOrdem = IdDaOrdem(evento);
        var estado = await _sagas.ObterAsync(idOrdem, cancellationToken).ConfigureAwait(false);

        if (estado is null)
        {
            _log.LogWarning("Evento {Tipo} para a ordem {IdOrdem}, que não tem saga neste serviço.", envelope.Tipo, idOrdem);
            return false;
        }

        var statusAntes = estado.StatusDaOrdem;
        var decisao = OrquestradorDaSaga.Avancar(estado, evento, _agora());

        if (!decisao.Mudou)
        {
            // Evento fora de ordem ou repetido. Acontece o tempo todo numa fila
            // e não é erro: a mensagem é confirmada e segue a vida.
            _log.LogInformation(
                "Evento {Tipo} não moveu a saga da ordem {IdOrdem}, que está em {Passo}.",
                envelope.Tipo, idOrdem, estado.Passo);
            return true;
        }

        var eventos = decisao.EventosAPublicar
            .Select(e => Enfileirar(e, envelope.CorrelationId))
            .ToList();

        await _ordens.AplicarDecisaoAsync(
            decisao.Estado, statusAntes, decisao.Estado.MotivoDaCompensacao, eventos, cancellationToken)
            .ConfigureAwait(false);

        _log.LogInformation(
            "Ordem {IdOrdem}: {Tipo} levou a saga para {Passo}, status {Status}.",
            idOrdem, envelope.Tipo, decisao.Estado.Passo, decisao.Estado.StatusDaOrdem);

        return true;
    }

    /// <summary>
    /// Compensa as sagas que ficaram esperando a execução além do prazo.
    ///
    /// Roda em varredura porque ninguém publica o fato de algo não ter
    /// acontecido. É o único caminho da saga que não começa num evento.
    /// </summary>
    public async Task<int> CompensarParadasAsync(TimeSpan prazo, CancellationToken cancellationToken = default)
    {
        var paradas = await _sagas
            .ObterParadasAsync(EPassoDaSaga.AguardandoExecucao, prazo, limite: 50, cancellationToken)
            .ConfigureAwait(false);

        var compensadas = 0;

        foreach (var estado in paradas)
        {
            var statusAntes = estado.StatusDaOrdem;
            var decisao = OrquestradorDaSaga.ExecucaoNaoAssumiu(estado, _agora());

            if (!decisao.Mudou)
                continue;

            // Correlation novo: esta compensação não veio de requisição nenhuma,
            // nasceu aqui. Inventar um id mantém o rastro, em vez de reaproveitar
            // o de um fluxo que terminou faz horas.
            var correlationId = $"timeout-{estado.IdOrdem}-{Guid.NewGuid():N}";

            var eventos = decisao.EventosAPublicar.Select(e => Enfileirar(e, correlationId)).ToList();

            await _ordens.AplicarDecisaoAsync(
                decisao.Estado, statusAntes, decisao.Estado.MotivoDaCompensacao, eventos, cancellationToken)
                .ConfigureAwait(false);

            _log.LogWarning(
                "Ordem {IdOrdem} compensada por timeout: a execução não assumiu em {Prazo}.",
                estado.IdOrdem, prazo);

            compensadas++;
        }

        return compensadas;
    }

    private static object? Desembrulhar(EnvelopeRecebido envelope) => envelope.Tipo switch
    {
        "DiagnosticoConcluido" => envelope.Ler<DiagnosticoConcluido>(),
        "OrcamentoGerado" => envelope.Ler<OrcamentoGerado>(),
        "OrcamentoAprovado" => envelope.Ler<OrcamentoAprovado>(),
        "OrcamentoRejeitado" => envelope.Ler<OrcamentoRejeitado>(),
        "PagamentoConfirmado" => envelope.Ler<PagamentoConfirmado>(),
        "PagamentoRecusado" => envelope.Ler<PagamentoRecusado>(),
        "PagamentoEstornado" => envelope.Ler<PagamentoEstornado>(),
        "ExecucaoFinalizada" => envelope.Ler<ExecucaoFinalizada>(),
        _ => null,
    };

    private static int IdDaOrdem(object evento) => evento switch
    {
        DiagnosticoConcluido e => e.IdOrdem,
        OrcamentoGerado e => e.IdOrdem,
        OrcamentoAprovado e => e.IdOrdem,
        OrcamentoRejeitado e => e.IdOrdem,
        PagamentoConfirmado e => e.IdOrdem,
        PagamentoRecusado e => e.IdOrdem,
        PagamentoEstornado e => e.IdOrdem,
        ExecucaoFinalizada e => e.IdOrdem,
        _ => throw new InvalidOperationException($"Evento {evento.GetType().Name} sem id de ordem."),
    };

    private static EventoOutbox Enfileirar(object evento, string correlationId) => evento switch
    {
        OrcamentoCancelado e => Outbox.Enfileirar(e, correlationId, Origem),
        OrdemCancelada e => Outbox.Enfileirar(e, correlationId, Origem),
        _ => throw new InvalidOperationException(
            $"O orquestrador pediu para publicar {evento.GetType().Name}, que não está mapeado."),
    };
}
