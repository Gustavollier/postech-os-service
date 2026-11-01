using PosTech.Contratos;

namespace PosTech.Saga;

/// <summary>
/// A máquina da saga da ordem de serviço.
///
/// Função pura: recebe o estado atual e um evento, devolve o estado novo e o
/// que publicar. Não toca banco, não toca broker. É de propósito — a lógica que
/// mais importa na Fase 4 é também a mais difícil de testar quando está
/// misturada com I/O, e aqui ela se testa alimentando eventos e conferindo o
/// que sai.
///
/// Promove o <c>OrdemServicoDomainService</c> da Fase 2: as mesmas transições
/// sequenciais, agora coordenando passos que moram fora do processo. Ver
/// ADR-0006.
/// </summary>
public static class OrquestradorDaSaga
{
    public static Decisao Avancar(EstadoDaSaga estado, object evento, DateTime agora)
    {
        ArgumentNullException.ThrowIfNull(estado);
        ArgumentNullException.ThrowIfNull(evento);

        // Saga terminada não reage mais a nada. Um evento atrasado que chega
        // depois do fim é normal numa fila, e tratá-lo reabriria uma ordem já
        // fechada.
        if (!estado.EstaEmAndamento)
            return Decisao.Ignorar(estado);

        return evento switch
        {
            DiagnosticoConcluido e => Diagnostico(estado, e, agora),
            OrcamentoGerado e => Orcamento(estado, e, agora),
            OrcamentoAprovado e => Aprovacao(estado, e, agora),
            OrcamentoRejeitado e => Rejeicao(estado, e, agora),
            PagamentoConfirmado e => Pagamento(estado, e, agora),
            PagamentoRecusado e => Recusa(estado, e, agora),
            ExecucaoFinalizada e => Execucao(estado, e, agora),
            PagamentoEstornado e => Estorno(estado, e, agora),
            _ => Decisao.Ignorar(estado),
        };
    }

    // -----------------------------------------------------------------------
    // Caminho feliz
    // -----------------------------------------------------------------------

    private static Decisao Diagnostico(EstadoDaSaga estado, DiagnosticoConcluido evento, DateTime agora)
    {
        if (estado.Passo != EPassoDaSaga.AguardandoDiagnostico)
            return Decisao.Ignorar(estado);

        estado.Passo = EPassoDaSaga.AguardandoOrcamento;
        estado.StatusDaOrdem = EStatusOrdem.EmDiagnostico;
        estado.Valor = evento.ValorTotal;
        estado.AtualizadoEm = agora;

        return new Decisao(estado, [], Mudou: true);
    }

    private static Decisao Orcamento(EstadoDaSaga estado, OrcamentoGerado evento, DateTime agora)
    {
        if (estado.Passo != EPassoDaSaga.AguardandoOrcamento)
            return Decisao.Ignorar(estado);

        estado.Passo = EPassoDaSaga.AguardandoAprovacao;
        estado.StatusDaOrdem = EStatusOrdem.AguardandoAprovacao;
        estado.IdOrcamento = evento.IdOrcamento;
        estado.Valor = evento.Valor;
        estado.AtualizadoEm = agora;

        return new Decisao(estado, [], Mudou: true);
    }

    private static Decisao Aprovacao(EstadoDaSaga estado, OrcamentoAprovado evento, DateTime agora)
    {
        if (estado.Passo != EPassoDaSaga.AguardandoAprovacao)
            return Decisao.Ignorar(estado);

        estado.Passo = EPassoDaSaga.AguardandoPagamento;
        estado.IdOrcamento = evento.IdOrcamento;
        estado.Valor = evento.Valor;
        estado.AtualizadoEm = agora;

        // O status não muda: para o cliente a ordem segue aguardando, porque
        // aprovar sem pagar ainda não libera a oficina para trabalhar.
        return new Decisao(estado, [], Mudou: true);
    }

    private static Decisao Pagamento(EstadoDaSaga estado, PagamentoConfirmado evento, DateTime agora)
    {
        if (estado.Passo != EPassoDaSaga.AguardandoPagamento)
            return Decisao.Ignorar(estado);

        estado.Passo = EPassoDaSaga.AguardandoExecucao;
        estado.StatusDaOrdem = EStatusOrdem.EmExecucao;
        estado.IdPagamento = evento.IdPagamento;
        estado.ReferenciaExterna = evento.ReferenciaExterna;
        estado.Valor = evento.Valor;
        estado.AtualizadoEm = agora;

        return new Decisao(estado, [], Mudou: true);
    }

    private static Decisao Execucao(EstadoDaSaga estado, ExecucaoFinalizada evento, DateTime agora)
    {
        if (estado.Passo != EPassoDaSaga.AguardandoExecucao)
            return Decisao.Ignorar(estado);

        estado.Passo = EPassoDaSaga.Concluida;
        estado.StatusDaOrdem = EStatusOrdem.Finalizada;
        estado.AtualizadoEm = agora;

        return new Decisao(estado, [], Mudou: true);
    }

    // -----------------------------------------------------------------------
    // Compensações
    //
    // A regra que organiza tudo abaixo: compensar só o que de fato aconteceu.
    // Estornar um pagamento que nunca foi feito é pior que não compensar nada,
    // porque inventa movimento financeiro.
    // -----------------------------------------------------------------------

    private static Decisao Rejeicao(EstadoDaSaga estado, OrcamentoRejeitado evento, DateTime agora)
    {
        if (estado.Passo != EPassoDaSaga.AguardandoAprovacao)
            return Decisao.Ignorar(estado);

        return Compensar(estado, evento.Motivo, agora);
    }

    private static Decisao Recusa(EstadoDaSaga estado, PagamentoRecusado evento, DateTime agora)
    {
        if (estado.Passo != EPassoDaSaga.AguardandoPagamento)
            return Decisao.Ignorar(estado);

        // Recusa de pagamento não cancela a ordem. O cliente pode tentar outro
        // meio, então a saga volta um passo em vez de morrer: o orçamento
        // continua válido e a ordem volta a aguardar aprovação.
        estado.Passo = EPassoDaSaga.AguardandoAprovacao;
        estado.StatusDaOrdem = EStatusOrdem.AguardandoAprovacao;
        estado.IdPagamento = evento.IdPagamento;
        estado.MotivoDaCompensacao = evento.Motivo;
        estado.AtualizadoEm = agora;

        return new Decisao(estado, [], Mudou: true);
    }

    private static Decisao Estorno(EstadoDaSaga estado, PagamentoEstornado evento, DateTime agora)
    {
        if (estado.Passo != EPassoDaSaga.Compensando)
            return Decisao.Ignorar(estado);

        estado.Passo = EPassoDaSaga.Cancelada;
        estado.AtualizadoEm = agora;

        return new Decisao(estado, [], Mudou: true);
    }

    /// <summary>
    /// A execução não assumiu a ordem dentro do prazo. Disparado por um
    /// varredor que olha sagas paradas, não por evento: ninguém publica o fato
    /// de algo não ter acontecido.
    /// </summary>
    public static Decisao ExecucaoNaoAssumiu(EstadoDaSaga estado, DateTime agora)
    {
        ArgumentNullException.ThrowIfNull(estado);

        if (estado.Passo != EPassoDaSaga.AguardandoExecucao)
            return Decisao.Ignorar(estado);

        return Compensar(estado, "A execução não assumiu a ordem dentro do prazo.", agora);
    }

    /// <summary>
    /// Cancelamento pedido pela recepção. Entra pelo mesmo caminho de
    /// compensação para não existirem dois jeitos de desfazer a mesma coisa.
    /// </summary>
    public static Decisao CancelarPorPedido(EstadoDaSaga estado, string motivo, DateTime agora)
    {
        ArgumentNullException.ThrowIfNull(estado);

        if (!estado.EstaEmAndamento)
            return Decisao.Ignorar(estado);

        return Compensar(estado, motivo, agora);
    }

    /// <summary>
    /// Desfaz o que já foi feito, na ordem inversa, publicando só o que cabe
    /// ao passo alcançado.
    /// </summary>
    private static Decisao Compensar(EstadoDaSaga estado, string motivo, DateTime agora)
    {
        var houvePagamento = estado.IdPagamento is not null && estado.ReferenciaExterna is not null;
        var houveOrcamento = estado.IdOrcamento is not null;

        var publicar = new List<object>();

        if (houveOrcamento)
        {
            publicar.Add(new OrcamentoCancelado(
                estado.IdOrdem, estado.IdOrcamento!, motivo, agora));
        }

        publicar.Add(new OrdemCancelada(estado.IdOrdem, motivo, agora));

        estado.MotivoDaCompensacao = motivo;
        estado.AtualizadoEm = agora;

        // Com pagamento feito, a saga fica Compensando até o estorno confirmar.
        // Sem pagamento, não há o que esperar e ela já morre cancelada. Marcar
        // Cancelada cedo demais faria o orquestrador ignorar o
        // PagamentoEstornado que chega depois.
        estado.Passo = houvePagamento ? EPassoDaSaga.Compensando : EPassoDaSaga.Cancelada;

        return new Decisao(estado, publicar, Mudou: true);
    }
}
