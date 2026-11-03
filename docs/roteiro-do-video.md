# Roteiro do vídeo — Fase 4 (até 15 minutos)

O enunciado pede quatro coisas no vídeo. Cada bloco abaixo diz qual delas cobre.

| Exigência | Onde |
| --- | --- |
| Fluxo completo de uma OS passando pelos microsserviços | Bloco 3 |
| Execução do Saga Pattern e tratamento de falhas | Bloco 4 |
| Deploy automatizado de pelo menos um microsserviço, com validação de testes | Bloco 5 |
| Monitoramento e rastreamento dos fluxos distribuídos | Bloco 6 (e em pontos dos blocos 3 e 4) |

---

## Antes de gravar (checklist do dia)

Com o ambiente no ar (`postech-infra-db` e `postech-infra-k8s` aplicados, os
quatro serviços com deploy feito):

- [ ] Os quatro pods rodando: `kubectl -n postechallenge get pods`
- [ ] Primeiro funcionário criado (pasta **0** da coleção do Postman, por port-forward)
- [ ] Variável `gateway` da coleção apontando para o APIM (`terraform output apim_gateway_url`)
- [ ] **Webhook do Mercado Pago configurado** na aplicação de teste: *Suas integrações → Configuração de webhooks*, URL `https://<apim>.azure-api.net/billing/api/v1/pagamentos/webhook`, tópico **Order**
- [ ] Rodar a coleção inteira uma vez (pastas 1 a 4) e ver tudo verde — se algo falhar, é melhor descobrir agora
- [ ] Abrir as abas, já logadas: Postman, GitHub Actions do `postech-execucao-service`, SonarCloud (organização `postech4`), painel do CloudAMQP, MongoDB Atlas (Data Explorer), Datadog (APM e Logs)
- [ ] Deixar pronta uma mudança pequena no `postech-execucao-service` para o bloco 5 (ver lá)
- [ ] Limpar a variável `correlationId` da coleção, para o vídeo usar um rastro novo e fácil de achar no Datadog

---

## Bloco 1 — Abertura (0:00–1:00)

- Quem somos (nomes e RMs).
- O problema: a oficina cresceu, virou várias filiais, e o monólito da Fase 3
  precisa virar serviços independentes, com consistência entre criação da OS,
  orçamento, pagamento e execução — sem transação distribuída.
- O que vamos mostrar: arquitetura, o fluxo de uma OS de ponta a ponta, a
  saga desfazendo o que falhou, um deploy automatizado e o rastreamento.

## Bloco 2 — Arquitetura (1:00–3:00)

Tela: `docs/arquitetura.md` (diagrama de componentes).

- **Quatro serviços**, cada um com repositório, pipeline e banco próprios:
  Identidade, OS, Execução, Billing.
- **Divisão:** cada serviço é dono de um pedaço do negócio que muda por motivos
  próprios. A Identidade é consultada, não coordenada; a OS orquestra; a
  Execução cuida da oficina e do estoque; o Billing, do dinheiro.
- **Bancos:** SQL onde a consistência é transacional (OS: ordem, saga e outbox
  na mesma transação; Identidade: CPF e placa únicos por índice). MongoDB onde
  o agregado é lido inteiro (orçamento, ordem na oficina com os itens).
  **Nenhum serviço acessa o banco de outro** — e não é só disciplina: cada um
  tem usuário próprio, que não enxerga o banco vizinho.
- **Comunicação:** cliente → APIM → serviço, por REST. Serviço → serviço, só por
  evento no RabbitMQ. Se um serviço cai, o evento espera na fila.
- **Saga orquestrada**, com o orquestrador dentro do OS Service. Por quê: um
  lugar só onde o estado da saga aparece e onde a compensação tem dono.

## Bloco 3 — Fluxo completo de uma OS (3:00–7:30)

Tela principal: Postman, pasta **3. Fluxo completo da OS**, request a request
(não pelo Runner — narrando). Tela de apoio: rota `/saga` e os painéis.

1. **Login do funcionário** (pasta 1) — mostrar o JWT; dizer que o APIM valida
   o token em toda rota menos login e health.
2. **Abrir a OS** — mostrar o `X-Correlation-ID` no cabeçalho. Dizer: a ordem, o
   estado da saga e o evento `OrdemAberta` gravam numa transação só (outbox).
3. **Saga: aguardando diagnóstico** — primeira aparição da rota `/saga`.
4. **Fila da oficina** — a OS chegou ao Execução **pelo RabbitMQ**, sem ninguém
   chamar o serviço. Mostrar no CloudAMQP a fila `execucao.eventos`.
5. **Diagnóstico** — o mecânico lança mão de obra e peça; o preço da peça sai do
   catálogo e a peça é **reservada**. Mostrar no Atlas o documento da peça com a
   reserva dentro.
6. **Orçamento montado pelo Billing** — o `DiagnosticoConcluido` levou os itens e
   o cliente; o Billing montou o orçamento sem perguntar nada a ninguém.
7. **Cliente aprova** — o Billing cria a cobrança **Pix no Mercado Pago**
   (mostrar a `referenciaExterna` `ORD...` e a `urlDePagamento`).
8. **Pagamento** — no sandbox, o Mercado Pago aprova o Pix sozinho e chama o
   **webhook**. Dizer: o Billing não acredita no webhook; ele consulta o pedido
   no Mercado Pago antes de confirmar. Mostrar o pedido em *Suas integrações →
   Pagamentos de teste*.
   - *Plano B se o webhook demorar:* rodar o request **"Mercado Pago notifica
     o pagamento (webhook)"** — ele chama o webhook com o id real do pedido, e o
     Billing consulta o Mercado Pago de verdade. Não é simulação.
9. **Saga: aguardando execução** → **Mecânico finaliza** (peças baixadas) →
   **Saga concluída**.
10. **Status e histórico** — `Recebida → EmDiagnostico → AguardandoAprovacao →
    EmExecucao → Finalizada`.

## Bloco 4 — Saga e tratamento de falhas (7:30–10:00)

1. **Ao vivo:** pasta **4. Compensação** no Postman.
   - Abrir outra OS, diagnosticar com peça, **cliente rejeita**.
   - Saga **Cancelada**, com o motivo. Ordem cancelada na oficina. **A reserva da
     peça voltou** — mostrar a peça no Atlas sem a reserva.
   - Dizer a regra: compensar só o que de fato aconteceu. Não houve pagamento,
     então não há estorno.
2. **As outras falhas, pelos testes BDD** (tela: `tests/PosTech.Os.Testes/Bdd`):
   - Mostrar a funcionalidade em português (Gherkin) e rodar
     `dotnet test --filter "FullyQualifiedName~Bdd"` — os cinco cenários verdes.
   - **Pagamento recusado volta um passo**, não cancela: o cliente tenta outro
     meio e o orçamento continua valendo.
   - **Execução que não assume no prazo:** o varredor cancela, o Billing estorna,
     e a saga fica em *Compensando* até o estorno confirmar.
3. **Garantias que seguram tudo isso** (falar, 30 s): outbox (evento nunca se
   perde entre o banco e o broker), idempotência (entrega repetida não duplica
   orçamento nem cobrança), dead letter (mensagem que não vai dar certo fica
   visível).

## Bloco 5 — Deploy automatizado com validação de testes (10:00–12:30)

Serviço: **Execução** (o pipeline mais rápido).

1. Fazer uma mudança pequena e visível — por exemplo, um texto de log ou a
   descrição de uma rota no Swagger — e dar push na `main` (ou abrir e mergear
   o PR, se a `main` estiver protegida, o que é melhor ainda para mostrar).
2. GitHub Actions: mostrar os jobs rodando.
   - **Build, testes e cobertura:** 120 testes com MongoDB e RabbitMQ reais, e o
     **gate de 80%** que reprova a pipeline abaixo disso.
   - **SonarCloud:** quality gate. Abrir o painel do projeto (cobertura, bugs,
     vulnerabilidades).
   - **Imagem e deploy no AKS:** imagem com a tag do commit no ACR,
     `kubectl set image`, `rollout status`.
3. `kubectl -n postechallenge get pods` — o pod novo subindo, o velho saindo.
4. Chamar `/execucao/health` pelo gateway — no ar.

## Bloco 6 — Monitoramento e rastreamento (12:30–14:30)

1. **Logs por correlação:** no Datadog Logs, filtrar pelo `correlationId` do
   bloco 3. Aparecem os quatro serviços, na ordem da saga — é o mesmo id que
   nasceu no APIM e viajou em todo evento.
2. **APM:** abrir um trace de uma requisição do bloco 3 e mostrar os serviços
   envolvidos e a latência de cada um.
3. **Dashboard:** latência, erros e réplicas por serviço.
4. Fechar com o rastro da compensação por prazo: ela ganha um id próprio
   (`timeout-{ordem}-...`), porque não nasceu de requisição nenhuma.

## Bloco 7 — Encerramento (14:30–15:00)

- O que entregamos: quatro serviços independentes, saga com compensação,
  pipelines com gate de cobertura e qualidade, deploy automatizado, rastreio.
- Links no PDF.

---

## Riscos e o que fazer

| Risco | O que fazer |
| --- | --- |
| Webhook do Mercado Pago não chega | Plano B do passo 8 do bloco 3: o request que chama o webhook com o id real |
| Um passo do Postman responde antes do evento chegar | Os passos assíncronos já repetem sozinhos; se ainda assim falhar, rodar de novo |
| Pipeline do bloco 5 demora | Gravar o bloco 5 separado e editar; o Execução leva uns 5 minutos |
| Trace do Datadog não liga os serviços pela fila | Mostrar os logs pelo `correlationId` (bloco 6, item 1), que não depende disso |
| Algo quebra durante a gravação | Gravar bloco a bloco; cada bloco começa de um estado conhecido |
