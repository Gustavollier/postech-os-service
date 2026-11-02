using PosTech.Contratos;
using PosTech.Saga;

namespace PosTech.Os.Testes;

/// <summary>
/// A máquina da saga, exercitada sem banco e sem broker.
///
/// É o teste que o vídeo precisa mostrar funcionando. Cada compensação tem o
/// seu caso, e o caso mais importante é o que prova que a saga **não** estorna
/// um pagamento que nunca aconteceu.
/// </summary>
public class OrquestradorDaSagaTests
{
    private static readonly DateTime Agora = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private static EstadoDaSaga Nova(int idOrdem = 1) => new() { IdOrdem = idOrdem };

    private static DiagnosticoConcluido UmDiagnostico(decimal total = 198.30m) => new(
        IdOrdem: 1, IdFuncionarioResponsavel: 7,
        IdCliente: 10, NomeCliente: "Ana Prado", EmailCliente: "ana@oficina.test",
        Itens: [new ItemDaOrdem("Troca de óleo", ETipoItem.MaoDeObra, 1, total)],
        ValorTotal: total, ConcluidoEm: Agora);

    // -----------------------------------------------------------------------
    // Caminho feliz
    // -----------------------------------------------------------------------

    [Fact]
    public void Fluxo_completo_leva_a_ordem_de_recebida_ate_finalizada()
    {
        var estado = Nova();

        var passos = new object[]
        {
            UmDiagnostico(),
            new OrcamentoGerado(1, "orc-1", 198.30m, Agora),
            new OrcamentoAprovado(1, "orc-1", 198.30m, Agora),
            new PagamentoConfirmado(1, "pag-1", 198.30m, "mp-9988", Agora),
            new ExecucaoFinalizada(1, 7, Agora),
        };

        foreach (var evento in passos)
        {
            var decisao = OrquestradorDaSaga.Avancar(estado, evento, Agora);
            Assert.True(decisao.Mudou, $"O evento {evento.GetType().Name} deveria ter movido a saga.");
            estado = decisao.Estado;
        }

        Assert.Equal(EPassoDaSaga.Concluida, estado.Passo);
        Assert.Equal(EStatusOrdem.Finalizada, estado.StatusDaOrdem);
        Assert.Equal("orc-1", estado.IdOrcamento);
        Assert.Equal("pag-1", estado.IdPagamento);
        Assert.Equal("mp-9988", estado.ReferenciaExterna);
        Assert.Null(estado.MotivoDaCompensacao);
    }

    [Fact]
    public void Aprovar_o_orcamento_nao_coloca_a_ordem_em_execucao()
    {
        // Aprovar sem pagar não libera a oficina para trabalhar. Para o cliente
        // a ordem segue aguardando; só a confirmação do pagamento muda isso.
        var estado = Nova();
        estado = OrquestradorDaSaga.Avancar(estado, UmDiagnostico(), Agora).Estado;
        estado = OrquestradorDaSaga.Avancar(estado, new OrcamentoGerado(1, "orc-1", 100m, Agora), Agora).Estado;

        var decisao = OrquestradorDaSaga.Avancar(estado, new OrcamentoAprovado(1, "orc-1", 100m, Agora), Agora);

        Assert.Equal(EPassoDaSaga.AguardandoPagamento, decisao.Estado.Passo);
        Assert.Equal(EStatusOrdem.AguardandoAprovacao, decisao.Estado.StatusDaOrdem);
    }

    // -----------------------------------------------------------------------
    // Eventos fora de ordem e repetidos
    // -----------------------------------------------------------------------

    [Fact]
    public void Evento_fora_de_ordem_e_ignorado_sem_quebrar_a_saga()
    {
        // Numa fila, um evento atrasado chega depois do que veio a seguir.
        // Ignorar em silêncio é o comportamento certo; aplicar seria corromper
        // o estado.
        var estado = Nova();

        var decisao = OrquestradorDaSaga.Avancar(
            estado, new PagamentoConfirmado(1, "pag-1", 100m, "mp-1", Agora), Agora);

        Assert.False(decisao.Mudou);
        Assert.Equal(EPassoDaSaga.AguardandoDiagnostico, decisao.Estado.Passo);
        Assert.Null(decisao.Estado.IdPagamento);
    }

    [Fact]
    public void O_mesmo_evento_duas_vezes_so_move_a_saga_uma_vez()
    {
        var estado = Nova();
        var diagnostico = UmDiagnostico();

        var primeira = OrquestradorDaSaga.Avancar(estado, diagnostico, Agora);
        var segunda = OrquestradorDaSaga.Avancar(primeira.Estado, diagnostico, Agora);

        Assert.True(primeira.Mudou);
        Assert.False(segunda.Mudou);
        Assert.Equal(EPassoDaSaga.AguardandoOrcamento, segunda.Estado.Passo);
    }

    [Fact]
    public void Saga_terminada_nao_reage_mais_a_nada()
    {
        var estado = Nova();
        estado.Passo = EPassoDaSaga.Concluida;

        var decisao = OrquestradorDaSaga.Avancar(estado, UmDiagnostico(), Agora);

        Assert.False(decisao.Mudou);
        Assert.Equal(EPassoDaSaga.Concluida, decisao.Estado.Passo);
    }

    // -----------------------------------------------------------------------
    // Compensações
    // -----------------------------------------------------------------------

    [Fact]
    public void Pagamento_recusado_devolve_a_ordem_para_aprovacao_sem_cancelar()
    {
        // O cliente pode tentar outro meio de pagamento. Cancelar a ordem na
        // primeira recusa seria perder trabalho de diagnóstico já feito.
        var estado = AteAguardandoPagamento();

        var decisao = OrquestradorDaSaga.Avancar(
            estado, new PagamentoRecusado(1, "pag-1", "Cartão sem limite", Agora), Agora);

        Assert.True(decisao.Mudou);
        Assert.Equal(EPassoDaSaga.AguardandoAprovacao, decisao.Estado.Passo);
        Assert.Equal(EStatusOrdem.AguardandoAprovacao, decisao.Estado.StatusDaOrdem);
        Assert.Equal("Cartão sem limite", decisao.Estado.MotivoDaCompensacao);
        Assert.Empty(decisao.EventosAPublicar);

        // E o orçamento continua de pé para uma nova tentativa.
        Assert.Equal("orc-1", decisao.Estado.IdOrcamento);
    }

    [Fact]
    public void Orcamento_rejeitado_cancela_a_ordem_e_o_orcamento()
    {
        var estado = AteAguardandoAprovacao();

        var decisao = OrquestradorDaSaga.Avancar(
            estado, new OrcamentoRejeitado(1, "orc-1", "Cliente não aprovou", Agora), Agora);

        Assert.Equal(EPassoDaSaga.Cancelada, decisao.Estado.Passo);
        Assert.Equal("Cliente não aprovou", decisao.Estado.MotivoDaCompensacao);

        var cancelado = Assert.IsType<OrcamentoCancelado>(decisao.EventosAPublicar[0]);
        Assert.Equal("orc-1", cancelado.IdOrcamento);
        Assert.IsType<OrdemCancelada>(decisao.EventosAPublicar[1]);
    }

    [Fact]
    public void Execucao_que_nao_assume_estorna_o_pagamento_e_cancela()
    {
        var estado = AteAguardandoExecucao();

        var decisao = OrquestradorDaSaga.ExecucaoNaoAssumiu(estado, Agora);

        // Fica Compensando, e não Cancelada: o estorno ainda não voltou, e
        // marcar Cancelada agora faria o orquestrador ignorar o
        // PagamentoEstornado que chega depois.
        Assert.Equal(EPassoDaSaga.Compensando, decisao.Estado.Passo);
        Assert.Contains(decisao.EventosAPublicar, e => e is OrcamentoCancelado);
        Assert.Contains(decisao.EventosAPublicar, e => e is OrdemCancelada);

        var fim = OrquestradorDaSaga.Avancar(
            decisao.Estado,
            new PagamentoEstornado(1, "pag-1", "mp-9988", 198.30m, "estorno", Agora),
            Agora);

        Assert.Equal(EPassoDaSaga.Cancelada, fim.Estado.Passo);
    }

    [Fact]
    public void Cancelar_antes_do_orcamento_nao_publica_cancelamento_de_orcamento()
    {
        // A regra que organiza toda a compensação: desfazer só o que de fato
        // aconteceu. Cancelar um orçamento que nunca existiu confunde quem lê o
        // histórico da ordem.
        var estado = Nova();

        var decisao = OrquestradorDaSaga.CancelarPorPedido(estado, "Cliente desistiu", Agora);

        Assert.Equal(EPassoDaSaga.Cancelada, decisao.Estado.Passo);
        Assert.Single(decisao.EventosAPublicar);
        Assert.IsType<OrdemCancelada>(decisao.EventosAPublicar[0]);
    }

    [Fact]
    public void Cancelar_sem_pagamento_nao_inventa_estorno()
    {
        var estado = AteAguardandoAprovacao();

        var decisao = OrquestradorDaSaga.CancelarPorPedido(estado, "Cliente desistiu", Agora);

        // Sem pagamento não há o que esperar: a saga já morre cancelada, em vez
        // de ficar presa em Compensando esperando um estorno que nunca vem.
        Assert.Equal(EPassoDaSaga.Cancelada, decisao.Estado.Passo);
        Assert.DoesNotContain(decisao.EventosAPublicar, e => e is PagamentoEstornado);
    }

    [Fact]
    public void Estorno_que_chega_sem_a_saga_estar_compensando_e_ignorado()
    {
        var estado = AteAguardandoExecucao();

        var decisao = OrquestradorDaSaga.Avancar(
            estado, new PagamentoEstornado(1, "pag-1", "mp-1", 100m, "qualquer", Agora), Agora);

        Assert.False(decisao.Mudou);
        Assert.Equal(EPassoDaSaga.AguardandoExecucao, decisao.Estado.Passo);
    }

    // -----------------------------------------------------------------------

    private static EstadoDaSaga AteAguardandoAprovacao()
    {
        var estado = Nova();
        estado = OrquestradorDaSaga.Avancar(estado, UmDiagnostico(), Agora).Estado;
        return OrquestradorDaSaga.Avancar(estado, new OrcamentoGerado(1, "orc-1", 198.30m, Agora), Agora).Estado;
    }

    private static EstadoDaSaga AteAguardandoPagamento()
    {
        var estado = AteAguardandoAprovacao();
        return OrquestradorDaSaga.Avancar(estado, new OrcamentoAprovado(1, "orc-1", 198.30m, Agora), Agora).Estado;
    }

    private static EstadoDaSaga AteAguardandoExecucao()
    {
        var estado = AteAguardandoPagamento();
        return OrquestradorDaSaga.Avancar(
            estado, new PagamentoConfirmado(1, "pag-1", 198.30m, "mp-9988", Agora), Agora).Estado;
    }
}
