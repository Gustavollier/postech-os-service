# postech-os-service

Microsserviço de **Ordem de Serviço** do Tech Challenge Fase 4 (FIAP 13SOAT).

Dono da ordem, do status e do histórico — e **orquestrador da Saga** que
coordena o fluxo entre os quatro serviços.

## O que este serviço faz

| Responsabilidade | Detalhe |
| --- | --- |
| Abrir ordem de serviço | Grava a ordem, o estado inicial da saga e o evento `OrdemAberta` numa transação só |
| Status e histórico | Toda transição vira linha em `StatusHistorico`, com o motivo |
| Orquestrar a saga | Decide o próximo passo e dispara a compensação quando algo falha |
| Compensar por timeout | Varre sagas paradas esperando a execução além do prazo |

Banco: **Azure SQL** (`os-db`). Mensageria: **RabbitMQ**.

## A Saga

Estratégia: **orquestrada**, com o orquestrador aqui dentro. A justificativa
completa está em [`docs/ADR-0006-saga-orquestrada.md`](docs/ADR-0006-saga-orquestrada.md).

Em resumo: coreografia é mais idiomática, mas orquestração dá um lugar único
onde o estado da saga aparece e onde a compensação tem dono explícito. E o
`OrdemServicoDomainService` da Fase 2 já era uma máquina de estados — o
orquestrador é essa máquina coordenando passos que passaram a morar fora do
processo.

```
1. Abrir a OS              OS Service   -> OrdemAberta
2. Diagnóstico             Execução     -> DiagnosticoConcluido (leva os itens)
3. Orçamento               Billing      -> OrcamentoGerado
4. Aprovação e cobrança    Billing      -> OrcamentoAprovado
5. Pagamento confirmado    Billing      -> PagamentoConfirmado
6. Execução e fechamento   Execução     -> ExecucaoFinalizada
```

### Compensações

| Falha | O que acontece |
| --- | --- |
| Orçamento rejeitado | Cancela o orçamento e a ordem |
| Pagamento recusado | **Volta um passo**, não cancela: o cliente pode tentar outro meio e o orçamento continua valendo |
| Execução não assume no prazo | Estorna o pagamento e cancela a ordem |
| Falha ao publicar | Nenhuma: o evento fica no outbox e é reenviado |

A regra que organiza todas: **compensar só o que de fato aconteceu**. Cancelar
antes de existir orçamento não publica `OrcamentoCancelado`; estornar o que
nunca foi pago inventaria movimento financeiro.

## Por que outbox

Gravar no banco e publicar no broker são recursos diferentes e não cabem numa
transação só. Publicar antes de gravar inventa um evento sobre uma ordem que
pode não existir; gravar antes de publicar perde o evento se o processo morrer
no meio.

Com outbox, gravar a ordem e enfileirar o evento é **uma transação local**, e um
processo à parte entrega depois. O publicador sobe com *publisher confirmations*
ligadas: sem elas, o `BasicPublish` devolve assim que o byte sai do processo e o
outbox marcaria como publicado um evento que o broker nunca aceitou.

Do outro lado, `EventoProcessado` garante idempotência: o RabbitMQ entrega ao
menos uma vez, e sem o registro isso viraria orçamento e cobrança duplicados.

## Rotas

| Método | Rota | O quê |
| --- | --- | --- |
| `POST` | `/api/v1/ordens-servico` | Abre a ordem |
| `GET` | `/api/v1/ordens-servico` | Lista, na prioridade da recepção. `?idCliente=` filtra |
| `GET` | `/api/v1/ordens-servico/{id}` | Detalhe |
| `GET` | `/api/v1/ordens-servico/{id}/status` | Status e histórico com motivos |
| `GET` | `/api/v1/ordens-servico/{id}/saga` | Estado da saga — é aqui que se mostra `Compensando` e o porquê |
| `GET` | `/health` · `/health/ready` | Liveness e readiness |

Swagger em `/swagger`.

## Rodar local

```bash
# Infraestrutura (RabbitMQ, SQL Server, Mongo)
docker compose -f docker-compose.yml up -d

# API
export SqlConnectionString="Server=localhost;Database=os-db;User Id=sa;Password=...;TrustServerCertificate=true"
export RabbitMqConnectionString="amqp://postech:senha@localhost:5672/"
dotnet run --project src/PosTech.Os.Api
```

O schema (`schema-os.sql` e `schema-mensageria.sql`) é aplicado pelo próprio
serviço na partida. Os scripts são idempotentes e vão embutidos no assembly,
então ambiente novo não tem passo manual.

## Testes

```bash
dotnet test
```

**96 testes, 94% de cobertura de linha**, sendo 5 deles cenários BDD. Os de integração sobem RabbitMQ e SQL
Server de verdade via Testcontainers — precisa de Docker. Dublê não prova
topologia nem atomicidade de transação, que é justamente o que mais importa
aqui.

Três grupos, em ordem de quanto cada um pega:

- **`SagaPontaAPontaTests`** — os quatro serviços conversando por um broker de
  verdade, com as compensações todas. Achou três defeitos de concorrência que
  nenhum teste de unidade pegaria.
- **`ApiDoOsServiceTests`** — a API no ar, com SQL Server e RabbitMQ. Esta
  camada esteve com cobertura **zero** por um tempo, e não por ser trivial: é
  onde o corpo da requisição vira evento no outbox. Foi um defeito aqui — o
  `OrdemResumida` como record posicional, que o Dapper não conseguia materializar
  de um enum vindo de `TINYINT` — que fazia o `GET /{id}` devolver 500 com 50
  testes verdes ao lado.
- **regras e contratos** — as funções puras da saga e o formato do fio.

### BDD: a saga escrita como a oficina fala

O fluxo completo da ordem está especificado em Gherkin, em português, com
[Reqnroll](https://reqnroll.net), em [`tests/PosTech.Os.Testes/Bdd`](tests/PosTech.Os.Testes/Bdd):

```gherkin
Cenário: Pagamento recusado volta um passo em vez de cancelar a ordem
  Dado que o diagnóstico foi concluído e o orçamento gerado
  Quando o cliente aprovar o orçamento
  E o adquirente recusar o pagamento com o motivo "Cartão sem limite"
  Então a saga está no passo "AguardandoAprovacao"
  E o Execução não recebe o evento "OrdemCancelada"
  Quando o cliente aprovar o orçamento
  E o Billing confirmar o pagamento
  Então a saga está no passo "AguardandoExecucao"
```

| Funcionalidade | Cenários |
| --- | --- |
| [Saga da ordem de serviço](tests/PosTech.Os.Testes/Bdd/SagaDaOrdemDeServico.feature) | caminho feliz da abertura ao fechamento; orçamento rejeitado; pagamento recusado; evento entregue duas vezes |
| [Compensação por prazo](tests/PosTech.Os.Testes/Bdd/CompensacaoPorPrazo.feature) | a execução não assume no prazo, a saga cancela, pede o estorno e só fecha quando ele confirma |

Os cenários rodam contra o OS Service de verdade, com SQL Server e RabbitMQ.
O Execução e o Billing são simulados publicando no broker os eventos que
publicariam, e cada passo confere o que o orquestrador mandou para as filas
deles e o estado na rota `/saga`. Nenhum passo chama o orquestrador por
método, e nenhum mexe no banco.

O cenário de prazo roda com o prazo de execução em três segundos, só naquela
funcionalidade (tag `@prazo-curto`). Para isso, o prazo e o intervalo da
varredura passaram a aceitar fração de minuto. Em produção continuam 60 e 5.

A pipeline **reprova abaixo de 80% de cobertura**. Na Fase 3 a cobertura era
medida e ignorada; aqui ela é um gate — e já reprovou de verdade, quando a
cobertura caiu para 76%.

## Configuração

| Variável | O quê |
| --- | --- |
| `SqlConnectionString` | Conexão com o `os-db`. Obrigatória |
| `RabbitMqConnectionString` | Conexão AMQP. Obrigatória |
| `Saga__PrazoDeExecucaoEmMinutos` | Quanto esperar a execução antes de compensar. Padrão 60 |
| `Saga__IntervaloDaVarreduraEmMinutos` | De quanto em quanto tempo varrer. Padrão 5 |

As duas primeiras são obrigatórias e o serviço **se recusa a subir sem elas** —
falhar na partida é melhor que descobrir na primeira requisição.

## Documentação

- [ADR-0006 — Saga orquestrada](docs/ADR-0006-saga-orquestrada.md)
- [ADR-0007 — Um banco por serviço](docs/ADR-0007-banco-por-servico.md)
- [ADR-0008 — CQRS só como separação de código](docs/ADR-0008-cqrs-sem-infraestrutura-nova.md)
