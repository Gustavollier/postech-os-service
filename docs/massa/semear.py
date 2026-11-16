#!/usr/bin/env python3
"""Semeia a massa de testes da oficina pelo gateway, como um usuário faria.

Cria equipe, clientes, veículos e peças, e leva sete ordens a pontos
diferentes da saga — de "aguardando diagnóstico" a "concluída" e "cancelada" —
para o painel abrir já com o que mostrar e cada cenário de demonstração ter uma
ordem pronta esperando.

Nada aqui escreve no banco: tudo passa pelo APIM, com o token de quem faria a
ação (o gerente cadastra e opera; o cliente aprova o próprio orçamento). Então
rodar este script também é um teste do ambiente.

Parte de uma base vazia, só com o gerente. Rodar duas vezes duplica as ordens.

    MASSA_CPF=11144477735 MASSA_SENHA='...' python3 docs/massa/semear.py

MASSA_SENHA é a senha do gerente e vira também a senha dos funcionários
criados. MASSA_GATEWAY troca o ambiente (padrão: o APIM da entrega).
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request

GATEWAY = os.environ.get("MASSA_GATEWAY", "https://pos-tech-fiap-apim.azure-api.net")
CPF_GERENTE = os.environ.get("MASSA_CPF")
SENHA = os.environ.get("MASSA_SENHA")
if not CPF_GERENTE or not SENHA:
    sys.exit("Defina MASSA_CPF e MASSA_SENHA (o gerente do ambiente).")


def chamar(metodo, caminho, corpo=None, token=None, aceitar=()):
    req = urllib.request.Request(
        GATEWAY + caminho,
        method=metodo,
        data=json.dumps(corpo).encode() if corpo is not None else None,
        headers={"Content-Type": "application/json", **({"Authorization": f"Bearer {token}"} if token else {})},
    )
    try:
        with urllib.request.urlopen(req, timeout=40) as r:
            bruto = r.read()
            return r.status, json.loads(bruto) if bruto else None
    except urllib.error.HTTPError as e:
        if e.code in aceitar:
            return e.code, None
        sys.exit(f"{metodo} {caminho} -> HTTP {e.code}: {e.read()[:300].decode(errors='replace')}")


def esperar(descricao, ler, pronto, segundos=60):
    """Os passos da saga andam por evento: lê até a condição valer."""
    fim = time.time() + segundos
    while time.time() < fim:
        valor = ler()
        if valor is not None and pronto(valor):
            return valor
        time.sleep(1.5)
    sys.exit(f"Não aconteceu a tempo: {descricao}")


def cpf(base9):
    d = [int(c) for c in base9]
    for _ in range(2):
        soma = sum(n * (len(d) + 1 - i) for i, n in enumerate(d))
        d.append((soma * 10) % 11 % 10)
    return "".join(map(str, d))


def cnpj(base12):
    d = [int(c) for c in base12]
    for pesos in ([5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2], [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]):
        resto = sum(n * p for n, p in zip(d, pesos)) % 11
        d.append(0 if resto < 2 else 11 - resto)
    return "".join(map(str, d))


# --------------------------------------------------------------------------
# A massa
# --------------------------------------------------------------------------

EQUIPE = [
    ("Carla Mendes", "Recepcionista", cpf("234567890"), "11988880001", 45.00),
    ("Bruno Lima", "Mecanico", cpf("345678901"), "11988880002", 85.50),
    ("Diego Rocha", "Eletricista", cpf("456789012"), "11988880003", 92.00),
    ("Elisa Nunes", "Estoquista", cpf("567890123"), "11988880004", 52.00),
]

# (apelido, nome, documento, telefone, e-mail, [(marca, modelo, placa, cor, ano, km)])
CLIENTES = [
    ("ana", "Ana Prado", {"cpf": "52998224725"}, "11988887777", "cliente@oficina.test",
     [("VW", "Gol", "ABC1D23", "Prata", 2022, 42000)]),
    ("rafael", "Rafael Souza", {"cpf": cpf("678901234")}, "11977771111", "rafael.souza@oficina.test",
     [("Fiat", "Argo", "RFA2B34", "Branco", 2023, 18500)]),
    ("marina", "Marina Costa", {"cpf": cpf("789012345")}, "11977772222", "marina.costa@oficina.test",
     [("Honda", "Civic", "MRC3C45", "Cinza", 2021, 56300), ("Honda", "Fit", "MRC4D56", "Vermelho", 2019, 78900)]),
    ("joao", "João Pereira", {"cpf": cpf("890123456")}, "11977773333", "joao.pereira@oficina.test",
     [("Toyota", "Corolla", "JPR5E67", "Preto", 2020, 64200)]),
    ("luiza", "Luiza Almeida", {"cpf": cpf("901234567")}, "11977774444", "luiza.almeida@oficina.test",
     [("Chevrolet", "Onix", "LZA6F78", "Azul", 2024, 9800)]),
    ("frota", "Transportes Andrade LTDA", {"cnpj": cnpj("112223330001")}, "1133334444", "frota@andrade.test",
     [("Fiat", "Fiorino", "TAD7G89", "Branco", 2022, 121000), ("VW", "Saveiro", "TAD8H90", "Branco", 2021, 98700)]),
    ("pedro", "Pedro Santos", {"cpf": cpf("123450987")}, "11977775555", "pedro.santos@oficina.test",
     [("Jeep", "Renegade", "PDS9J01", "Verde", 2023, 27400)]),
]

# (apelido, nome, marca, código, preço, estoque)
PECAS = [
    ("filtro", "Filtro de óleo", "Bosch", "FO-0986", 38.90, 20),
    ("oleo", "Óleo 5W30 sintético 1L", "Mobil", "OL-5W30", 45.00, 40),
    ("pastilha", "Pastilha de freio dianteira", "Cobreq", "PF-N1047", 129.90, 12),
    ("disco", "Disco de freio ventilado", "Fremax", "DF-BD4405", 219.00, 8),
    ("vela", "Vela de ignição", "NGK", "VI-BKR6E", 32.50, 24),
    ("correia", "Correia dentada", "Gates", "CD-5578XS", 189.90, 6),
    ("bateria", "Bateria 60Ah", "Moura", "BT-M60GD", 549.00, 4),
    ("amortecedor", "Amortecedor dianteiro", "Monroe", "AM-G8103", 389.00, 1),
    ("palheta", "Palheta do limpador", "Bosch", "PL-AF24", 59.90, 0),
]

MO = "MaoDeObra"
# (cliente, índice do veículo, responsável, até onde levar, itens de mão de obra, itens de peça)
ORDENS = [
    ("ana", 0, "Bruno Lima", "concluida",
     [("Troca de óleo e filtro", 1, 120.50)], [("filtro", 1), ("oleo", 4)]),
    ("rafael", 0, "Bruno Lima", "em_execucao",
     [("Revisão dos freios dianteiros", 1, 180.00)], [("pastilha", 1), ("disco", 2)]),
    ("marina", 0, "Diego Rocha", "aguardando_aprovacao",
     [("Diagnóstico elétrico e troca de bateria", 1, 150.00)], [("bateria", 1)]),
    ("joao", 0, "Bruno Lima", "aguardando_aprovacao",
     [("Troca da correia dentada", 1, 420.00)], [("correia", 1), ("vela", 4)]),
    ("luiza", 0, "Bruno Lima", "aguardando_diagnostico", [], []),
    ("frota", 0, "Diego Rocha", "cancelada",
     [("Revisão geral de 120 mil km", 1, 890.00)], [("oleo", 5), ("filtro", 1)]),
    ("marina", 1, "Bruno Lima", "aguardando_diagnostico", [], []),
]
MOTIVO_DA_REJEICAO = "Cliente vai fazer a revisão na concessionária"


def main():
    print(f"Gateway: {GATEWAY}")
    _, login = chamar("POST", "/identidade/api/v1/autenticacao/login", {"cpf": CPF_GERENTE, "senha": SENHA})
    gerente = login["accessToken"]

    equipe = {"Gerente da Oficina": login["funcionarioId"]}
    for nome, cargo, doc, contato, valor_hora in EQUIPE:
        _, f = chamar("POST", "/identidade/api/v1/funcionarios", {
            "nome": nome, "contato": contato, "cpf": doc, "cargo": cargo, "valorHora": valor_hora, "senha": SENHA,
        }, gerente)
        equipe[nome] = f["id"]
        print(f"  funcionário {f['id']}: {nome} ({cargo})")

    clientes = {}
    for apelido, nome, documento, telefone, email, veiculos in CLIENTES:
        _, c = chamar("POST", "/identidade/api/v1/clientes", {
            **documento, "nomeCompleto": nome, "telefone": telefone, "email": email,
        }, gerente)
        frota = []
        for marca, modelo, placa, cor, ano, km in veiculos:
            _, v = chamar("POST", "/identidade/api/v1/veiculos", {
                "clienteId": c["id"], "marca": marca, "modelo": modelo, "placa": placa, "cor": cor,
                "anoModelo": ano, "anoFabricacao": ano - 1, "kmEntrada": km,
            }, gerente)
            frota.append(v)
        clientes[apelido] = {"id": c["id"], "nome": nome, "email": email, "documento": documento, "veiculos": frota}
        print(f"  cliente {c['id']}: {nome}, {len(frota)} veículo(s)")

    pecas = {}
    for apelido, nome, marca, codigo, preco, estoque in PECAS:
        _, p = chamar("POST", "/execucao/api/v1/pecas", {
            "nome": nome, "marca": marca, "codigo": codigo, "preco": preco, "quantidadeEmEstoque": estoque,
        }, gerente)
        pecas[apelido] = p["id"]
    print(f"  {len(pecas)} peças no catálogo")

    resumo = []
    for dono, indice, responsavel, alvo, mao_de_obra, itens_de_peca in ORDENS:
        cliente = clientes[dono]
        veiculo = cliente["veiculos"][indice]
        _, aberta = chamar("POST", "/os/api/v1/ordens-servico", {
            "idCliente": cliente["id"], "idVeiculo": veiculo["id"], "idFuncionario": equipe[responsavel],
            "nomeCliente": cliente["nome"], "descricaoVeiculo": veiculo["descricao"], "emailCliente": cliente["email"],
        }, gerente)
        ordem = aberta["id"]
        saga = lambda: chamar("GET", f"/os/api/v1/ordens-servico/{ordem}/saga", token=gerente, aceitar=(404,))[1]
        chegou = lambda passo, segundos=60: esperar(
            f"ordem {ordem} em {passo}", saga, lambda s: s["passo"] == passo, segundos)

        # A ordem chega no Execução pela fila; só então o diagnóstico é aceito.
        esperar(f"ordem {ordem} na oficina",
                lambda: chamar("GET", f"/execucao/api/v1/execucao/ordens/{ordem}", token=gerente, aceitar=(404,))[1],
                lambda o: True)

        if alvo != "aguardando_diagnostico":
            itens = [{"descricao": d, "tipo": MO, "quantidade": q, "valorUnitario": v} for d, q, v in mao_de_obra]
            itens += [{"tipo": "Peca", "idPeca": pecas[p], "quantidade": q} for p, q in itens_de_peca]
            chamar("POST", f"/execucao/api/v1/execucao/ordens/{ordem}/diagnostico", {"itens": itens}, gerente)
            chegou("AguardandoAprovacao")

        if alvo == "cancelada":
            chamar("POST", f"/billing/api/v1/orcamentos/os/{ordem}/rejeitar", {"motivo": MOTIVO_DA_REJEICAO}, gerente)
            chegou("Cancelada")

        if alvo in ("em_execucao", "concluida"):
            # Quem aprova é o cliente, com o token dele — o da Auth Function.
            doc_do_cliente = cliente["documento"]["cpf"]
            _, sessao = chamar("POST", "/auth", {"cpf": doc_do_cliente})
            chamar("POST", f"/billing/api/v1/orcamentos/os/{ordem}/aprovar", token=sessao["access_token"])
            # O Mercado Pago aprova o Pix de teste e chama o webhook sozinho. Em
            # geral leva segundos, mas o sandbox às vezes demora perto de dois
            # minutos — daí a espera longa só neste passo.
            chegou("AguardandoExecucao", 300)

        if alvo == "concluida":
            chamar("POST", f"/execucao/api/v1/execucao/ordens/{ordem}/finalizar", token=gerente)
            chegou("Concluida")

        _, orcamento = chamar("GET", f"/billing/api/v1/orcamentos/os/{ordem}", token=gerente, aceitar=(404,))
        final = saga()
        resumo.append({
            "ordem": ordem, "cliente": cliente["nome"], "veiculo": veiculo["descricao"], "responsavel": responsavel,
            "passo": final["passo"], "status": final["statusDaOrdem"],
            "valor": orcamento["valor"] if orcamento else None,
            "pagamento": orcamento["statusDoPagamento"] if orcamento else None,
        })
        print(f"  ordem {ordem}: {cliente['nome']} / {veiculo['descricao']} -> {final['passo']}")

    _, estoque = chamar("GET", "/execucao/api/v1/pecas", token=gerente)
    json.dump({
        "equipe": equipe,
        "funcionarios": [{"nome": n, "cargo": c, "cpf": d} for n, c, d, _, _ in EQUIPE],
        "clientes": clientes, "ordens": resumo,
        "pecas": [{k: p[k] for k in ("nome", "marca", "codigo", "preco", "quantidadeEmEstoque", "quantidadeReservada", "disponivel")} for p in estoque],
    }, open(os.environ.get("MASSA_SAIDA", "massa.json"), "w"), ensure_ascii=False, indent=2)
    print("Pronto.")


if __name__ == "__main__":
    main()
