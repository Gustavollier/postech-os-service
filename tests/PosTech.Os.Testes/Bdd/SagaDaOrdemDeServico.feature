# language: pt

@saga
Funcionalidade: Saga da ordem de serviço
  Para que uma ordem de serviço atravesse os quatro serviços sem transação distribuída
  Como oficina
  Quero que o OS Service orquestre o fluxo pela fila
  E que, quando algo falhar, desfaça só o que de fato aconteceu

  O OS Service roda de verdade, com SQL Server e RabbitMQ. O Execução e o Billing
  são simulados publicando no broker exatamente os eventos que publicariam, e a
  reação do orquestrador é conferida nas filas deles e na rota /saga.

  Contexto:
    Dado que a recepção abriu uma ordem de serviço para "Ana Prado" com o veículo "VW Gol ABC1D23"
    Então o Execução recebe o evento "OrdemAberta"
    E a saga está no passo "AguardandoDiagnostico"

  Cenário: Caminho feliz, da abertura ao fechamento
    Quando o Execução concluir o diagnóstico no valor de 198,30
    Então a saga está no passo "AguardandoOrcamento"
    Quando o Billing gerar o orçamento
    Então a saga está no passo "AguardandoAprovacao"
    E a ordem está com o status "AguardandoAprovacao"
    Quando o cliente aprovar o orçamento
    E o Billing confirmar o pagamento
    Então a saga está no passo "AguardandoExecucao"
    E a ordem está com o status "EmExecucao"
    Quando o Execução finalizar o serviço
    Então a saga está no passo "Concluida"
    E a ordem está com o status "Finalizada"
    E o histórico da ordem passou por "Recebida, EmDiagnostico, AguardandoAprovacao, EmExecucao, Finalizada"

  Cenário: Cliente rejeita o orçamento e a saga desfaz o que foi feito
    Dado que o diagnóstico foi concluído e o orçamento gerado
    Quando o cliente rejeitar o orçamento com o motivo "Achou caro"
    Então o Billing recebe o evento "OrcamentoCancelado"
    E o Execução recebe o evento "OrdemCancelada"
    E a saga está no passo "Cancelada"
    E o motivo da compensação é "Achou caro"

  Cenário: Pagamento recusado volta um passo em vez de cancelar a ordem
    Dado que o diagnóstico foi concluído e o orçamento gerado
    Quando o cliente aprovar o orçamento
    E o adquirente recusar o pagamento com o motivo "Cartão sem limite"
    Então a saga está no passo "AguardandoAprovacao"
    E o Execução não recebe o evento "OrdemCancelada"
    Quando o cliente aprovar o orçamento
    E o Billing confirmar o pagamento
    Então a saga está no passo "AguardandoExecucao"

  Cenário: Evento entregue duas vezes não faz a saga andar duas vezes
    Quando o Execução concluir o diagnóstico no valor de 198,30
    E o mesmo diagnóstico for entregue de novo
    Então a saga está no passo "AguardandoOrcamento"
    E o histórico da ordem tem 1 transição para "EmDiagnostico"
