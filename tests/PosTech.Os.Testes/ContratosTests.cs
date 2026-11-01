using System.Reflection;
using PosTech.Contratos;

namespace PosTech.Os.Testes;

/// <summary>
/// Guarda-corpos do conjunto de contratos.
///
/// A maioria destes testes não exercita lógica: eles vigiam o catálogo. Um
/// evento novo sem atributo, uma routing key repetida ou um erro de digitação na
/// topologia são defeitos que só aparecem em runtime, no dia da gravação, com a
/// mensagem sumindo sem erro nenhum. Aqui eles quebram o build.
/// </summary>
public class ContratosTests
{
    private static readonly Type[] Eventos = typeof(OrdemAberta).Assembly
        .GetTypes()
        .Where(t => t.GetCustomAttribute<EventoDeIntegracaoAttribute>() is not null)
        .ToArray();

    [Fact]
    public void Todo_evento_declarado_tem_atributo_de_integracao()
    {
        // Os records de evento vivem todos em Eventos.cs e terminam em fato no
        // passado. Se algum aparecer sem o atributo, ele não pode ser publicado,
        // e o erro só apareceria na primeira tentativa de envio.
        var semAtributo = typeof(OrdemAberta).Assembly
            .GetTypes()
            .Where(t => t.IsClass && t.Namespace == "PosTech.Contratos")
            .Where(t => t.Name.EndsWith("Aberta") || t.Name.EndsWith("Gerado")
                     || t.Name.EndsWith("Aprovado") || t.Name.EndsWith("Confirmado")
                     || t.Name.EndsWith("Finalizada") || t.Name.EndsWith("Concluido")
                     || t.Name.EndsWith("Rejeitado") || t.Name.EndsWith("Recusado")
                     || t.Name.EndsWith("Cancelado") || t.Name.EndsWith("Cancelada")
                     || t.Name.EndsWith("Estornado"))
            .Where(t => t.GetCustomAttribute<EventoDeIntegracaoAttribute>() is null)
            .Select(t => t.Name)
            .ToArray();

        Assert.Empty(semAtributo);
    }

    [Fact]
    public void Nenhum_nome_de_evento_se_repete()
    {
        var repetidos = Eventos
            .Select(t => t.GetCustomAttribute<EventoDeIntegracaoAttribute>()!.Nome)
            .GroupBy(nome => nome)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.Empty(repetidos);
    }

    [Fact]
    public void Nenhuma_routing_key_se_repete()
    {
        var repetidas = Eventos
            .Select(t => t.GetCustomAttribute<EventoDeIntegracaoAttribute>()!.RoutingKey)
            .GroupBy(chave => chave)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.Empty(repetidas);
    }

    [Fact]
    public void Toda_routing_key_segue_o_formato_contexto_ponto_fato()
    {
        foreach (var tipo in Eventos)
        {
            var chave = tipo.GetCustomAttribute<EventoDeIntegracaoAttribute>()!.RoutingKey;

            Assert.Matches("^[a-z]+\\.[a-z]+$", chave);
        }
    }

    [Fact]
    public void Toda_assinatura_da_topologia_aponta_para_uma_routing_key_que_existe()
    {
        // Este é o teste que paga por si. Uma letra errada numa assinatura não dá
        // erro: a fila simplesmente nunca recebe nada, e o serviço fica mudo.
        var declaradas = Eventos
            .Select(t => t.GetCustomAttribute<EventoDeIntegracaoAttribute>()!.RoutingKey)
            .ToHashSet();

        var orfas = Topologia.Assinaturas
            .SelectMany(par => par.Value.Select(chave => (Fila: par.Key, Chave: chave)))
            .Where(a => !declaradas.Contains(a.Chave))
            .Select(a => $"{a.Fila} assina {a.Chave}, que não existe")
            .ToArray();

        Assert.Empty(orfas);
    }

    [Fact]
    public void Todo_evento_do_caminho_feliz_e_consumido_por_alguem()
    {
        // Evento publicado que ninguém escuta é trabalho jogado fora, e
        // normalmente significa que um consumidor foi esquecido.
        var assinadas = Topologia.Assinaturas.SelectMany(p => p.Value).ToHashSet();

        var ninguemEscuta = Eventos
            .Select(t => t.GetCustomAttribute<EventoDeIntegracaoAttribute>()!)
            .Where(a => !assinadas.Contains(a.RoutingKey))
            .Select(a => a.Nome)
            .ToArray();

        Assert.Empty(ninguemEscuta);
    }

    [Fact]
    public void Envelope_tira_nome_e_versao_do_atributo_e_nao_do_nome_da_classe()
    {
        var evento = new OrdemAberta(1, 2, "Ana", "ana@oficina.test", 3, "Gol AAA-1234", 4, DateTime.UtcNow);

        var envelope = Envelope<OrdemAberta>.Criar(evento, "corr-1", "os-service");

        Assert.Equal("OrdemAberta", envelope.Tipo);
        Assert.Equal(1, envelope.Versao);
        Assert.Equal("corr-1", envelope.CorrelationId);
        Assert.Equal("os-service", envelope.Origem);
        Assert.NotEqual(Guid.Empty, envelope.MensagemId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Envelope_recusa_correlation_id_vazio(string? correlationId)
    {
        var evento = new OrdemCancelada(1, "teste", DateTime.UtcNow);

        Assert.Throws<ArgumentException>(
            () => Envelope<OrdemCancelada>.Criar(evento, correlationId!, "os-service"));
    }

    [Fact]
    public void Envelope_recusa_origem_vazia()
    {
        var evento = new OrdemCancelada(1, "teste", DateTime.UtcNow);

        Assert.Throws<ArgumentException>(
            () => Envelope<OrdemCancelada>.Criar(evento, "corr-1", "  "));
    }

    [Fact]
    public void Cada_mensagem_recebe_um_id_proprio()
    {
        var evento = new OrdemCancelada(1, "teste", DateTime.UtcNow);

        var primeiro = Envelope<OrdemCancelada>.Criar(evento, "corr-1", "os-service");
        var segundo = Envelope<OrdemCancelada>.Criar(evento, "corr-1", "os-service");

        Assert.NotEqual(primeiro.MensagemId, segundo.MensagemId);
    }

    [Fact]
    public void Serializacao_do_envelope_volta_igual()
    {
        var evento = new DiagnosticoConcluido(
            IdOrdem: 42,
            IdFuncionarioResponsavel: 7,
            Itens:
            [
                new ItemDaOrdem("Troca de óleo", ETipoItem.MaoDeObra, 1, 120.50m),
                new ItemDaOrdem("Filtro de óleo", ETipoItem.Peca, 2, 38.90m),
            ],
            ValorTotal: 198.30m,
            ConcluidoEm: new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc));

        var envelope = Envelope<DiagnosticoConcluido>.Criar(evento, "corr-99", "execucao-service");

        var json = SerializacaoDeEvento.Serializar(envelope);
        var volta = SerializacaoDeEvento.Desserializar<DiagnosticoConcluido>(json);

        Assert.NotNull(volta);
        Assert.Equal(envelope.MensagemId, volta.MensagemId);
        Assert.Equal("DiagnosticoConcluido", volta.Tipo);
        Assert.Equal("corr-99", volta.CorrelationId);
        Assert.Equal(2, volta.Dados.Itens.Count);
        Assert.Equal(198.30m, volta.Dados.ValorTotal);
        Assert.Equal(ETipoItem.Peca, volta.Dados.Itens[1].Tipo);
    }

    [Fact]
    public void Serializar_um_tipo_sem_atributo_falha_cedo()
    {
        // Falhar na criação do envelope é muito melhor que publicar uma mensagem
        // com tipo vazio e descobrir no consumidor.
        Assert.Throws<InvalidOperationException>(
            () => Envelope<string>.Criar("qualquer coisa", "corr-1", "os-service"));
    }

    [Fact]
    public void Dead_letter_segue_o_nome_da_fila()
    {
        Assert.Equal("billing.eventos.dlq", Topologia.DeadLetterDe(Topologia.FilaBilling));
    }
}

/// <summary>
/// O formato do fio, fixado.
///
/// Serialização é contrato público: mudar o encoder ou o conversor de enum
/// muda o que o outro serviço recebe, e isso não pode acontecer por acidente
/// numa troca de default do .NET. Este teste quebra se mudar.
/// </summary>
public class FormatoDoFioTests
{
    [Fact]
    public void Acentuacao_vai_legivel_e_enum_vai_por_nome()
    {
        var evento = new DiagnosticoConcluido(
            IdOrdem: 1, IdFuncionarioResponsavel: 7,
            Itens: [new ItemDaOrdem("Troca de óleo", ETipoItem.MaoDeObra, 1, 120.50m)],
            ValorTotal: 120.50m,
            ConcluidoEm: new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc));

        var json = SerializacaoDeEvento.Serializar(
            Envelope<DiagnosticoConcluido>.Criar(evento, "corr-1", "execucao-service"));

        Assert.Contains("\"descricao\":\"Troca de óleo\"", json);
        Assert.Contains("\"tipo\":\"MaoDeObra\"", json);
        Assert.DoesNotContain("\\u00F3", json);
    }

    [Fact]
    public void Enum_por_nome_volta_igual_na_desserializacao()
    {
        var evento = new DiagnosticoConcluido(
            1, 7, [new ItemDaOrdem("Pastilha", ETipoItem.Peca, 4, 89.90m)], 359.60m, DateTime.UtcNow);

        var json = SerializacaoDeEvento.Serializar(
            Envelope<DiagnosticoConcluido>.Criar(evento, "corr-1", "execucao-service"));

        var volta = SerializacaoDeEvento.Desserializar<DiagnosticoConcluido>(json);

        Assert.Equal(ETipoItem.Peca, volta!.Dados.Itens[0].Tipo);
        Assert.Equal(359.60m, volta.Dados.ValorTotal);
    }
}
