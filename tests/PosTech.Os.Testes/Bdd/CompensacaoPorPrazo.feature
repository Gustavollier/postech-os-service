# language: pt

@saga @prazo-curto
Funcionalidade: Compensação por prazo de execução
  Para não deixar dinheiro do cliente preso numa ordem que ninguém assumiu
  Como oficina
  Quero que, passado o prazo, a saga estorne o pagamento e cancele a ordem

  Ninguém publica o fato de algo não ter acontecido: é o varredor do OS Service
  que encontra a saga parada. Aqui o prazo é de segundos, configurado só para
  esta funcionalidade; em produção é uma hora.

  A compensação não nasce de requisição nenhuma, então ganha um rastro próprio,
  "timeout-{ordem}-{id}", em vez de reaproveitar o de um fluxo que parou há uma
  hora. É por ele que se segue a compensação no Datadog.

  Cenário: A execução não assume no prazo e o pagamento é estornado
    Dado que a recepção abriu uma ordem de serviço para "Bruno Dias" com o veículo "Fiat Uno XYZ9A87"
    E que o diagnóstico foi concluído e o orçamento gerado
    E que o cliente aprovou e o pagamento foi confirmado
    Quando o prazo da execução passar sem o Execução assumir a ordem
    Então o Billing recebe o evento "OrcamentoCancelado" com o rastro da compensação por prazo
    E o Billing recebe o evento "OrdemCancelada" com o rastro da compensação por prazo
    E o Execução recebe o evento "OrdemCancelada" com o rastro da compensação por prazo
    E a saga está no passo "Compensando"
    E o motivo da compensação contém "prazo"
    Quando o Billing estornar o pagamento
    Então a saga está no passo "Cancelada"
