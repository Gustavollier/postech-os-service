# Massa de testes

Os dados que estão no ambiente da entrega, para gravar o vídeo e para os testes dos colaboradores. Foram criados em 16/11/2026 pelo script [`massa/semear.py`](massa/semear.py), sobre a base limpa — tudo pelo gateway, com o token de quem faria cada ação.

- **Painel:** https://gustavollier.github.io/postech-front/
- **Gateway:** `https://pos-tech-fiap-apim.azure-api.net`

## Como entrar

**Equipe:** aba *Sou da equipe*, CPF e senha. **A senha é a mesma para todos os funcionários desta massa:** a do gerente, que está na variável `senhaFuncionario` da coleção do Postman (`docs/postman`). Ela não está escrita aqui de propósito.

**Cliente:** aba *Sou cliente*, só o CPF. Cliente pessoa jurídica não entra pelo painel: o login de cliente é por CPF.

## Equipe

| Id | Nome | Cargo | CPF | O que consegue fazer |
|---|---|---|---|---|
| 1 | Gerente da Oficina | Gerente | `111.444.777-35` | Tudo, inclusive editar e desativar cadastros e gerir a equipe |
| 2 | Carla Mendes | Recepcionista | `234.567.890-92` | Operar a oficina e cadastrar; não edita nem desativa |
| 3 | Bruno Lima | Mecânico | `345.678.901-75` | Operar a oficina e cadastrar; não edita nem desativa |
| 4 | Diego Rocha | Eletricista | `456.789.012-49` | Operar a oficina e cadastrar; não edita nem desativa |
| 5 | Elisa Nunes | Estoquista | `567.890.123-03` | Operar a oficina e cadastrar; não edita nem desativa |

## Clientes e veículos

| Id | Cliente | Documento (login) | E-mail | Veículos (id · descrição) |
|---|---|---|---|---|
| 1 | Ana Prado | CPF `529.982.247-25` | cliente@oficina.test | 1 · VW Gol ABC1D23 |
| 2 | Rafael Souza | CPF `678.901.234-69` | rafael.souza@oficina.test | 2 · Fiat Argo RFA2B34 |
| 3 | Marina Costa | CPF `789.012.345-05` | marina.costa@oficina.test | 3 · Honda Civic MRC3C45<br>4 · Honda Fit MRC4D56 |
| 4 | João Pereira | CPF `890.123.456-42` | joao.pereira@oficina.test | 5 · Toyota Corolla JPR5E67 |
| 5 | Luiza Almeida | CPF `901.234.567-70` | luiza.almeida@oficina.test | 6 · Chevrolet Onix LZA6F78 |
| 6 | Transportes Andrade LTDA | CNPJ `11.222.333/0001-81` (sem login) | frota@andrade.test | 7 · Fiat Fiorino TAD7G89<br>8 · VW Saveiro TAD8H90 |
| 7 | Pedro Santos | CPF `123.450.987-32` | pedro.santos@oficina.test | 9 · Jeep Renegade PDS9J01 |

**Pedro Santos não tem ordem nenhuma:** é o cliente livre para abrir uma ordem do zero ao vivo. Ana Prado é a cliente que a coleção do Postman usa.

## Peças

| Peça | Marca | Código | Preço | Em estoque | Reservadas | Livres |
|---|---|---|---|---|---|---|
| Amortecedor dianteiro | Monroe | `AM-G8103` | R$ 389,00 | 1 | 0 | 1 |
| Bateria 60Ah | Moura | `BT-M60GD` | R$ 549,00 | 4 | 1 | 3 |
| Correia dentada | Gates | `CD-5578XS` | R$ 189,90 | 6 | 1 | 5 |
| Disco de freio ventilado | Fremax | `DF-BD4405` | R$ 219,00 | 8 | 2 | 6 |
| Filtro de óleo | Bosch | `FO-0986` | R$ 38,90 | 19 | 0 | 19 |
| Palheta do limpador | Bosch | `PL-AF24` | R$ 59,90 | 0 | 0 | 0 |
| Pastilha de freio dianteira | Cobreq | `PF-N1047` | R$ 129,90 | 12 | 1 | 11 |
| Vela de ignição | NGK | `VI-BKR6E` | R$ 32,50 | 24 | 4 | 20 |
| Óleo 5W30 sintético 1L | Mobil | `OL-5W30` | R$ 45,00 | 36 | 0 | 36 |

As reservas são da ordem 2 (paga, esperando o mecânico finalizar) e das ordens 3 e 4 (orçamento esperando resposta). Duas peças estão no catálogo para os testes de falta de estoque: **Amortecedor dianteiro** (1 unidade) e **Palheta do limpador** (zero).

## Ordens de serviço

| Ordem | Cliente | Veículo | Responsável | Onde está | Orçamento | Pagamento | Serve para |
|---|---|---|---|---|---|---|---|
| #1 | Ana Prado | VW Gol ABC1D23 | Bruno Lima | Concluída | R$ 339,40 | Confirmado | Histórico completo de uma ordem que deu certo; estoque já baixado |
| #2 | Rafael Souza | Fiat Argo RFA2B34 | Bruno Lima | Em execução (paga) | R$ 747,90 | Confirmado | **Finalizar a execução** e ver a saga concluir |
| #3 | Marina Costa | Honda Civic MRC3C45 | Diego Rocha | Aguardando aprovação do cliente | R$ 699,00 | Sem cobrança | **Cliente aprova e paga** (entrar com o CPF da Marina) |
| #4 | João Pereira | Toyota Corolla JPR5E67 | Bruno Lima | Aguardando aprovação do cliente | R$ 739,90 | Sem cobrança | **Cliente rejeita** → compensação (entrar com o CPF do João, ou rejeitar pela equipe) |
| #5 | Luiza Almeida | Chevrolet Onix LZA6F78 | Bruno Lima | Aguardando diagnóstico | — | — | **Registrar diagnóstico** com mão de obra e peça |
| #6 | Transportes Andrade LTDA | Fiat Fiorino TAD7G89 | Diego Rocha | Cancelada (orçamento rejeitado) | R$ 1153,90 | Sem cobrança | Exemplo pronto de saga compensada, com o motivo |
| #7 | Marina Costa | Honda Fit MRC4D56 | Bruno Lima | Aguardando diagnóstico | — | — | Reserva: segunda ordem da Marina, para repetir o diagnóstico ou mostrar que o cliente vê as duas dele |

## Cenários prontos

Cada um já tem uma ordem esperando. Feito uma vez, a ordem muda de estado — para repetir, use a ordem reserva (#7), abra uma nova para o Pedro, ou rode a massa de novo.

| Quero mostrar | Entrar como | Fazer | Resultado esperado |
|---|---|---|---|
| Diagnóstico e orçamento automático | Equipe (qualquer um) | Ordem **#5** → *Registrar diagnóstico*: uma mão de obra e uma peça do estoque | Em segundos o orçamento aparece sozinho e a peça fica reservada |
| Cliente aprova e paga | Cliente `789.012.345-05` (Marina) | Ordem **#3** → *Aprovar e pagar* | Link do Pix; depois *Pagamento confirmado* e a trilha vai para Execução |
| Finalizar o serviço | Equipe | Ordem **#2** → *Finalizar execução* | Saga *Concluída*; disco e pastilha saem do estoque de vez |
| Compensação por rejeição | Cliente `890.123.456-42` (João) | Ordem **#4** → *Rejeitar*, com um motivo | Saga *Cancelada* com o motivo; a correia e as velas voltam a ficar livres |
| Ordem nova do zero | Equipe | *Ordens* → *+ Nova ordem* → cliente **Pedro Santos** | Ordem #8 em *Recebida* |
| Cliente só vê o que é dele | Cliente `789.012.345-05` (Marina) | Abrir *Ordens de serviço* | Só as ordens #3 e #7 |
| Painel com um pouco de tudo | Gerente | Tela *Painel* | Sete ordens distribuídas por status, inclusive uma cancelada |
| Fluxo inteiro em um minuto | Gerente | Tela *Demonstração* → *Iniciar demonstração* | Cria cliente e ordem próprios e vai até *Concluída* |

## Dados para os testes negativos

| Teste | Como provocar | Resposta esperada |
|---|---|---|
| CPF inválido no cadastro | Novo cliente com CPF `111.111.111-11` | 400, documento inválido |
| CPF duplicado | Novo cliente com `529.982.247-25` (Ana) | 409 |
| Placa duplicada | Novo veículo com a placa `ABC1D23` | 409 |
| Estoque insuficiente | Diagnóstico pedindo **2** de *Amortecedor dianteiro* (há 1) ou 1 de *Palheta do limpador* (há 0) | O painel avisa antes de enviar; direto na API, 409 e nenhuma peça fica reservada |
| Login de cliente inexistente | Entrar como cliente com um CPF válido não cadastrado, por exemplo `390.533.447-05` | 404 |
| Senha errada | Entrar como equipe com a senha errada | 401; depois de 5 erros seguidos o login bloqueia — **não façam isso com o gerente** |
| Cliente tentando ver ordem alheia | Token da Marina em `GET /os/api/v1/ordens-servico/1` | 404 |
| Cliente tentando ver o cadastro | Token de cliente em `GET /identidade/api/v1/clientes` | 403 |
| Sem token | Qualquer rota de negócio sem `Authorization` | 401 |
| Mecânico tentando o que é do gerente | Entrar como Bruno Lima e abrir *Clientes* ou *Equipe* | Os botões *Editar* e *Desativar* não aparecem; direto na API, 403 |

## Sobre o pagamento

O Pix é criado de verdade no ambiente de teste do Mercado Pago, que aprova sozinho e chama o webhook. **Normalmente leva poucos segundos, mas já levou perto de dois minutos.** Se demorar na gravação, esperem ou cortem na edição; o plano B é o request *“Mercado Pago notifica o pagamento (webhook)”* da coleção do Postman, que só funciona depois de o Mercado Pago ter aprovado.

## Recriar a massa

O script parte de uma base **vazia, só com o gerente**. Rodar em cima da massa existente falha nos cadastros (CPF e placa já usados).

```bash
MASSA_CPF=11144477735 MASSA_SENHA='<senha do gerente>' python3 docs/massa/semear.py
```

Esvaziar a base é apagar as linhas das tabelas do `os-db` e do `identidade-db` (mantendo o funcionário 1) e os documentos do `billing-db` e do `execucao-db`. Não há rota para isso de propósito; foi feito direto nos bancos, de dentro do cluster.
