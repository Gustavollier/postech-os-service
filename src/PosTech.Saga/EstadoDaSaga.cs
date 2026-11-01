namespace PosTech.Saga;

/// <summary>
/// Onde a ordem está no fluxo distribuído.
///
/// É diferente do status da OS, e a diferença importa. O status é o que o
/// cliente vê na tela; o passo da saga é o que o orquestrador precisa saber
/// para decidir o próximo movimento. Uma ordem pode estar "Aguardando
/// Aprovação" para o cliente enquanto a saga está em <see cref="AguardandoPagamento"/>,
/// e misturar os dois é como sagas viram bugs difíceis.
/// </summary>
public enum EPassoDaSaga
{
    AguardandoDiagnostico = 0,
    AguardandoOrcamento = 1,
    AguardandoAprovacao = 2,
    AguardandoPagamento = 3,
    AguardandoExecucao = 4,
    Concluida = 5,

    /// <summary>Desfazendo os passos já dados. Estado transitório.</summary>
    Compensando = 6,

    /// <summary>Compensação terminada. Estado final do caminho triste.</summary>
    Cancelada = 7,
}

/// <summary>
/// Status da ordem, o mesmo enum da Fase 2. Fica repetido aqui porque o
/// orquestrador não pode depender do assembly do monólito.
/// </summary>
public enum EStatusOrdem
{
    Recebida = 0,
    EmDiagnostico = 1,
    AguardandoAprovacao = 2,
    EmExecucao = 3,
    Finalizada = 4,
    Entregue = 5,
}

/// <summary>
/// O que o orquestrador guarda sobre uma ordem em voo.
///
/// Mora em <c>os-db</c>, ao lado da ordem, e não num serviço de saga separado:
/// a saga e a ordem mudam juntas, e separá-las criaria exatamente a transação
/// distribuída que o padrão existe para evitar.
/// </summary>
public sealed class EstadoDaSaga
{
    public required int IdOrdem { get; init; }

    public EPassoDaSaga Passo { get; set; } = EPassoDaSaga.AguardandoDiagnostico;

    public EStatusOrdem StatusDaOrdem { get; set; } = EStatusOrdem.Recebida;

    public string? IdOrcamento { get; set; }

    public string? IdPagamento { get; set; }

    /// <summary>Id da cobrança no Mercado Pago. Sem ele não dá para estornar.</summary>
    public string? ReferenciaExterna { get; set; }

    public decimal? Valor { get; set; }

    /// <summary>Por que a saga foi compensada. Fica visível na tela da ordem.</summary>
    public string? MotivoDaCompensacao { get; set; }

    public DateTime AtualizadoEm { get; set; }

    public bool EstaEmAndamento =>
        Passo is not (EPassoDaSaga.Concluida or EPassoDaSaga.Cancelada);
}

/// <summary>
/// O que o orquestrador decidiu ao ver um evento.
///
/// Devolver a decisão em vez de executá-la é o que torna a máquina testável sem
/// banco e sem broker: o teste alimenta um evento e confere o que sai.
/// </summary>
public sealed record Decisao(
    EstadoDaSaga Estado,
    IReadOnlyList<object> EventosAPublicar,
    bool Mudou)
{
    public static Decisao Ignorar(EstadoDaSaga estado) => new(estado, [], Mudou: false);
}
