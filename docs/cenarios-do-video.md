# Cenários do vídeo — o que mostrar e o que falar

Complemento do [`roteiro-do-video.md`](roteiro-do-video.md). Os dados prontos para cada cenário — CPFs,
ordens e peças — estão em [`massa-de-testes.md`](massa-de-testes.md). O roteiro diz a
ordem e o tempo de cada bloco; este arquivo é a cola de quem está gravando:
para cada cenário, **o que abrir**, **o que fazer na tela** e **uma fala
sugerida**. As falas são ponto de partida — digam com as palavras de vocês.

O vídeo pode ser gravado de duas formas, e as duas estão cobertas aqui:

- **pelo painel** (`https://gustavollier.github.io/postech-front/`), que é mais
  visual e tem a tela **Demonstração** feita para isso;
- **pelo Postman** (`docs/postman`), que mostra as chamadas cruas.

Sugestão: fluxo principal pelo painel, e o Postman só onde ajuda a mostrar
uma resposta HTTP (o 401, o 404 da autorização).

---

## Onde está cada coisa

Abram tudo isto antes de gravar, cada um numa aba, já logado.

| O quê | Onde abrir | Nome para procurar |
| --- | --- | --- |
| Painel da oficina | https://gustavollier.github.io/postech-front/ | — |
| Gateway | Portal Azure → API Management | `pos-tech-fiap-apim` |
| Cluster | Portal Azure → Kubernetes services | `postech-aks`, namespace `postechallenge` |
| Banco SQL | Portal Azure → SQL databases | servidor `postech-sql-13soat-cac`, bancos `os-db` e `identidade-db` |
| Cofre de segredos | Portal Azure → Key vaults | `postech-kv-13soat` |
| Registro de imagens | Portal Azure → Container registries | `postechacr13soat` |
| Auth Function | Portal Azure → Container Apps | `postech-auth-fn-13soat` |
| MongoDB | MongoDB Atlas → projeto `postech-fase4` → *Browse Collections* | bancos `billing-db` e `execucao-db` |
| RabbitMQ | CloudAMQP → *RabbitMQ Manager* → *Queues* | `os.eventos`, `execucao.eventos`, `billing.eventos` |
| Mercado Pago | Mercado Pago Developers → `postech-oficina` | *Configuração de webhooks* e os pedidos de teste |
| Pipelines | GitHub → repositório → *Actions* | um por serviço |
| Qualidade | SonarCloud → organização `postech4` | quatro projetos |
| Observabilidade | Datadog → Dashboards / Logs / APM | dashboard "PosTechChallenge — Oficina Mecânica (Fase 4)" |

Todo recurso da Azure está no resource group `pos-tech-fiap`, região
`canadacentral`.

---

## Cenário 0 — Tour pela infraestrutura (1 a 2 minutos)

Objetivo: provar que é tudo real e que cada serviço tem o próprio banco.
Passem rápido, uma frase por tela.

| Tela | O que mostrar | Fala sugerida |
| --- | --- | --- |
| Portal Azure, resource group `pos-tech-fiap` | A lista de recursos | "Tudo o que roda na Azure está neste resource group, e foi criado por Terraform: o gateway, o cluster Kubernetes, o SQL, o cofre de segredos e o registro de imagens." |
| API Management `pos-tech-fiap-apim` → *APIs* | As cinco APIs: os, billing, execucao, identidade, auth | "Esta é a única porta de entrada. O API Management valida o token, aplica limite de requisições e roteia pelo primeiro trecho do caminho para o serviço certo. O cluster só aceita tráfego vindo daqui." |
| AKS `postech-aks` → *Workloads* (ou `kubectl -n postechallenge get pods`) | Os quatro pods | "Aqui estão os quatro microsserviços rodando no Kubernetes, cada um com o próprio deployment e autoescala." |
| SQL `postech-sql-13soat-cac` | Os bancos `os-db` e `identidade-db` | "Este é o nosso SQL na Azure. São dois bancos separados: um do serviço de Ordem de Serviço, outro do Identidade. Cada serviço tem um usuário que só enxerga o próprio banco." |
| MongoDB Atlas → *Browse Collections* | `billing-db.orcamentos` e `execucao-db.ordens` / `pecas` | "E este é o nosso MongoDB, no Atlas. O Billing guarda os orçamentos aqui, e a Execução guarda as ordens na oficina e o estoque de peças. Também são dois bancos, com um usuário para cada." |
| CloudAMQP → *Queues* | As três filas | "Os serviços não se chamam por HTTP. Eles conversam por eventos neste RabbitMQ — uma fila por serviço. Se um serviço cair, a mensagem espera aqui." |
| Key Vault `postech-kv-13soat` → *Secrets* | Só os nomes dos segredos | "Nenhuma senha está no código nem na pipeline. Connection strings, a chave do JWT e o token do Mercado Pago ficam neste cofre e chegam aos pods como Secret do Kubernetes." |

> Cuidado: no Key Vault e no CloudAMQP, **não abram o valor** de nenhum segredo
> nem a tela com a URL de conexão. Mostrem só a lista de nomes.

---

## Cenário 1 — Caminho feliz, de ponta a ponta

Cobre a exigência *"fluxo completo de uma OS passando pelos microsserviços"*.

**Preparação:** logado no painel como gerente. Para não cadastrar nada ao vivo, usem a massa: ordem #5 para o diagnóstico, #3 para o cliente aprovar (Marina), #2 para finalizar.

| # | O que fazer | O que aparece | Fala sugerida |
| --- | --- | --- | --- |
| 1 | Tela **Clientes** → *+ Novo cliente*, depois *+ Veículo* | Cliente e veículo criados | "Começo cadastrando o cliente e o veículo. Isso é o serviço de Identidade, gravando no SQL." |
| 2 | Tela **Peças** | Uma peça com estoque | "O estoque é do serviço de Execução, no MongoDB." |
| 3 | Tela **Ordens de serviço** → *+ Nova ordem* | Ordem nova no topo, status *Recebida* | "Abrir a ordem é no serviço de OS, que é o orquestrador da saga. A ordem, o estado da saga e o evento de abertura são gravados numa transação só — é o padrão outbox, então o evento nunca se perde." |
| 4 | Abrir a ordem | Trilha da saga em *Diagnóstico* | "Esta trilha é o estado da saga, lido do orquestrador. A ordem já chegou na oficina — e chegou pela fila, ninguém chamou o serviço de Execução." |
| 5 | *Registrar diagnóstico*: uma mão de obra e uma **peça do estoque** | Aviso verde; em segundos o orçamento aparece sozinho | "O mecânico lança o serviço e as peças. A peça fica reservada no estoque para esta ordem. E reparem: eu não pedi orçamento a ninguém. O evento do diagnóstico foi para a fila e o Billing montou o orçamento." |
| 6 | (opcional) Tela **Peças** | "2 reservadas · 8 livres" | "A reserva aparece aqui: está na prateleira, mas não pode ser prometida para outra ordem." |
| 7 | Sair e entrar como **cliente**, pelo CPF | Só a ordem dele | "Agora sou o cliente. Ele entra só com o CPF, por uma Azure Function, e enxerga apenas as próprias ordens." |
| 8 | Abrir a ordem → *Aprovar e pagar* | Botão *Pagar com Pix no Mercado Pago* | "Ao aprovar, o Billing cria uma cobrança Pix de verdade no Mercado Pago, no ambiente de teste." |
| 9 | Esperar alguns segundos | *✓ Pagamento confirmado*, trilha em *Execução* | "O Mercado Pago aprovou o Pix de teste e chamou o nosso webhook. O Billing não confia no webhook: ele consulta o pedido no Mercado Pago antes de confirmar. Com o pagamento confirmado, a saga andou sozinha." |
| 10 | Sair, entrar como gerente, abrir a ordem → *Finalizar execução* | Trilha em *Concluída* | "O mecânico finaliza, as peças reservadas saem do estoque de vez e a saga fecha." |
| 11 | Mostrar o **Histórico de status** e a tela **Peças** | Recebida → … → Finalizada; estoque menor | "Aqui está o caminho inteiro da ordem, e o estoque já com a baixa." |

**Versão curta (1 minuto):** tela **Demonstração** → *Iniciar demonstração* →
clicar *Executar* em cada passo conforme acende. A tela mostra de qual serviço
é cada chamada, o estado da saga ao vivo e um único *Correlation ID* para o
fluxo todo — guardem esse id para o cenário 6.

**O Pix de teste nem sempre é imediato.** O Mercado Pago costuma aprovar em poucos segundos, mas já levou perto de dois minutos. Se demorar, esperem e cortem na edição: a tela atualiza sozinha quando o webhook chega.

**Se quiserem forçar depois de ~2 min:** no Postman, pasta 3, request
*"Mercado Pago notifica o pagamento (webhook)"*. Ele entrega a notificação com
o id real do pedido; o Billing consulta o Mercado Pago de verdade.

---

## Cenário 2 — Compensação: o cliente rejeita o orçamento

Cobre *"execução do Saga Pattern e tratamento de falhas"*.

| # | O que fazer | O que aparece | Fala sugerida |
| --- | --- | --- | --- |
| 1 | Nova ordem, diagnóstico **com peça** | Orçamento pendente, peça reservada | "Mesmo começo. Agora vamos ver o que acontece quando dá errado." |
| 2 | *Rejeitar pelo cliente* (ou como cliente, *Rejeitar*), com um motivo | Saga **Cancelada**, quadro *Compensação* com o motivo | "O cliente recusou. A saga entra em compensação: cada serviço desfaz o que tinha feito." |
| 3 | Tela **Peças** | A reserva sumiu | "A peça que estava reservada voltou a ficar livre." |
| 4 | Tela **Ordens** | A ordem com o selo *Cancelada* | "E a ordem está cancelada. Um detalhe importante: a saga só compensa o que de fato aconteceu. Não houve pagamento, então não há estorno a fazer." |

---

## Cenário 3 — As outras falhas, pelos testes BDD

As falhas que não dá para provocar ao vivo com segurança estão escritas como
especificação. Tela: `tests/PosTech.Os.Testes/Bdd/SagaDaOrdemDeServico.feature`.

```bash
dotnet test --filter "FullyQualifiedName~Bdd"
```

| Cenário no Gherkin | Fala sugerida |
| --- | --- |
| Pagamento recusado volta um passo em vez de cancelar a ordem | "Se o Mercado Pago recusa o pagamento, a ordem não é cancelada. A saga volta um passo e o cliente pode tentar de novo — o orçamento continua valendo." |
| A execução não assume no prazo e o pagamento é estornado | "Se o pagamento foi feito mas a oficina não assume o serviço no prazo, um varredor cancela, o Billing estorna, e a saga fica em *Compensando* até o estorno confirmar." |
| Evento entregue duas vezes não faz a saga andar duas vezes | "Fila entrega mensagem repetida às vezes. Todos os consumidores são idempotentes: a mesma mensagem duas vezes não gera dois orçamentos nem duas cobranças." |

Fala de fechamento: "Estes cenários rodam contra SQL Server e RabbitMQ de
verdade, em contêineres, a cada push."

---

## Cenário 4 — Segurança: cada perfil só alcança o que é seu

Não é exigência explícita, mas é rápido e mostra maturidade. Use o Postman.

| # | O que fazer | Resposta | Fala sugerida |
| --- | --- | --- | --- |
| 1 | `GET {{gateway}}/os/api/v1/ordens-servico` **sem token** | 401 | "Sem token, o gateway barra." |
| 2 | Login do cliente por CPF (pasta 2) e, com o token dele, `GET /os/api/v1/ordens-servico` | 200, só as ordens dele | "Com o token de um cliente, a listagem só traz as ordens dele — o filtro sai do token, não do que ele pede." |
| 3 | Com o mesmo token, `GET /os/api/v1/ordens-servico/{id de outro cliente}` | 404 | "A ordem de outro cliente responde como se não existisse." |
| 4 | Com o mesmo token, `GET /identidade/api/v1/clientes` | 403 | "E o cadastro de clientes, que tem CPF e telefone, é só da equipe." |

Fala de fechamento: "O gateway autentica; cada serviço decide o que o perfil
pode. E o cluster só aceita conexão vinda do gateway."

---

## Cenário 5 — Deploy automatizado com validação de testes

Cobre *"deploy automatizado de pelo menos um microsserviço, com validação de
testes"*. Serviço: **Execução** (pipeline mais rápido, uns 5 minutos).

| # | O que fazer | Fala sugerida |
| --- | --- | --- |
| 1 | Abrir um PR pequeno no `postech-execucao-service` (um texto de log, por exemplo) | "Vou fazer uma mudança pequena e abrir um pull request." |
| 2 | Aba *Checks* do PR: **Build, testes e cobertura** | "A pipeline sobe MongoDB e RabbitMQ de verdade e roda os 141 testes. Abaixo de 80% de cobertura, ela reprova." |
| 3 | **SonarCloud** → abrir o projeto | "Depois o SonarCloud analisa qualidade, bugs e vulnerabilidades. O quality gate também bloqueia o merge." |
| 4 | Mergear o PR → *Actions* → **Imagem e deploy no AKS** | "Com o merge, a pipeline gera a imagem com a tag do commit, publica no registro e atualiza o deployment no Kubernetes." |
| 5 | `kubectl -n postechallenge get pods -w` | "O pod novo sobe, passa no health check, e só então o antigo sai. Sem indisponibilidade." |
| 6 | `GET {{gateway}}/execucao/health` | "E o serviço está no ar com a versão nova." |

Dica: gravem este cenário separado e editem — a espera da pipeline não
precisa ir inteira para o vídeo.

---

## Cenário 6 — Monitoramento e rastreamento distribuído

Cobre *"monitoramento e rastreamento dos fluxos distribuídos"*.

| # | Tela | O que fazer | Fala sugerida |
| --- | --- | --- | --- |
| 1 | Painel, botão **Atividade** (canto inferior direito) | Abrir e mostrar as chamadas com o *Correlation ID* | "Toda chamada sai do navegador com um identificador de correlação. O gateway o repassa, cada serviço o coloca nos logs, e ele viaja dentro de cada evento na fila." |
| 2 | Datadog → **Logs** | Buscar pelo Correlation ID do cenário 1 (o da tela Demonstração) | "Buscando por esse id, aparecem os quatro serviços, na ordem em que a saga aconteceu. É uma ordem de serviço inteira contada pelos logs." |
| 3 | Datadog → **APM** → um trace | Abrir uma requisição do fluxo | "Aqui o trace da requisição, com o tempo gasto em cada trecho." |
| 4 | Datadog → dashboard **PosTechChallenge — Oficina Mecânica (Fase 4)** | Mostrar os widgets da saga, latência e Kubernetes | "E o painel operacional: ordens por passo da saga, eventos por tipo, compensações por prazo, latência e consumo dos pods." |
| 5 | Datadog → **Monitors** | Os alertas `[PosTech] …` | "Há alertas para falha no processamento de ordens, latência alta e indisponibilidade do gateway." |

Se o trace do APM não ligar os serviços através da fila, não insistam: o
item 2 (logs pelo Correlation ID) prova o rastreamento e não depende disso.

---

## Antes de apertar o REC

- [ ] `kubectl -n postechallenge get pods` — quatro pods *Running*
- [ ] `https://pos-tech-fiap-apim.azure-api.net/os/health` responde 200
- [ ] Login como gerente e como cliente funcionando no painel
- [ ] Uma rodada da tela **Demonstração** até *Concluída*, para aquecer
- [ ] Coleção do Postman rodada inteira, tudo verde
- [ ] Datadog recebendo logs (buscar pelo Correlation ID da rodada de aquecimento)
- [ ] PR do cenário 5 já preparado
- [ ] Notificações do sistema e abas pessoais fechadas; nenhum segredo visível

## Frases que resumem a arquitetura

Para a abertura ou o encerramento:

- "Quatro serviços, cada um com repositório, pipeline e banco próprios."
- "SQL onde a consistência é transacional; MongoDB onde o agregado é lido inteiro."
- "Por HTTP só entra quem vem de fora. Entre serviços, só evento."
- "Saga orquestrada: um lugar só onde o estado aparece e onde a compensação tem dono."
- "Outbox para o evento não se perder, idempotência para a repetição não duplicar, dead letter para o que deu errado ficar visível."
