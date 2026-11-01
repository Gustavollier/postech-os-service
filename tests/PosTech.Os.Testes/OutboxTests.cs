using PosTech.Contratos;
using PosTech.Mensageria;

namespace PosTech.Os.Testes;

public class OutboxTests
{
    private static OrdemAberta UmaOrdem() => new(
        IdOrdem: 1, IdCliente: 10, NomeCliente: "Ana", EmailCliente: "ana@oficina.test",
        IdVeiculo: 20, DescricaoVeiculo: "Gol AAA-1234", IdFuncionario: 30, AbertaEm: DateTime.UtcNow);

    [Fact]
    public void Enfileirar_monta_a_linha_pronta_para_gravar_na_transacao()
    {
        var linha = Outbox.Enfileirar(UmaOrdem(), "corr-1", "os-service");

        Assert.Equal("OrdemAberta", linha.Tipo);
        Assert.Equal(RoutingKeys.OrdemAberta, linha.RoutingKey);
        Assert.Equal("corr-1", linha.CorrelationId);
        Assert.Equal(EStatusOutbox.Pendente, linha.Status);
        Assert.Equal(0, linha.Tentativas);
        Assert.Contains("\"tipo\":\"OrdemAberta\"", linha.Payload);
    }

    [Fact]
    public void O_id_da_mensagem_na_linha_e_o_mesmo_de_dentro_do_envelope()
    {
        // Se estes dois divergissem, o consumidor deduplicaria por um id e o
        // publicador registraria outro, e a idempotência não valeria nada.
        var linha = Outbox.Enfileirar(UmaOrdem(), "corr-1", "os-service");

        var envelope = SerializacaoDeEvento.Desserializar<OrdemAberta>(linha.Payload);

        Assert.NotNull(envelope);
        Assert.Equal(linha.MensagemId, envelope.MensagemId);
    }

    [Fact]
    public async Task Publicacao_bem_sucedida_marca_a_linha_e_nao_tenta_de_novo()
    {
        var repositorio = new OutboxEmMemoria();
        var publicador = new PublicadorFalso();
        repositorio.Gravar(Outbox.Enfileirar(UmaOrdem(), "corr-1", "os-service"));

        var processador = new ProcessadorDoOutbox(repositorio, publicador);

        var publicados = await processador.ProcessarLoteAsync();

        Assert.Equal(1, publicados);
        Assert.Single(publicador.Publicados);
        Assert.Equal(EStatusOutbox.Publicado, repositorio.Linhas[0].Status);
        Assert.NotNull(repositorio.Linhas[0].PublicadoEm);

        // Segunda volta: já publicado, nada a fazer.
        Assert.Equal(0, await processador.ProcessarLoteAsync());
        Assert.Equal(1, publicador.Chamadas);
    }

    [Fact]
    public async Task Falha_mantem_pendente_e_agenda_a_proxima_tentativa()
    {
        var repositorio = new OutboxEmMemoria();
        var publicador = new PublicadorFalso { FalharNasPrimeiras = 1 };
        repositorio.Gravar(Outbox.Enfileirar(UmaOrdem(), "corr-1", "os-service"));

        var agora = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var processador = new ProcessadorDoOutbox(repositorio, publicador, () => agora);

        var publicados = await processador.ProcessarLoteAsync();

        var linha = repositorio.Linhas[0];
        Assert.Equal(0, publicados);
        Assert.Equal(EStatusOutbox.Pendente, linha.Status);
        Assert.Equal(1, linha.Tentativas);
        Assert.Equal(agora.Add(ProcessadorDoOutbox.Backoff(1)), linha.ProximaTentativaEm);
        Assert.Contains("broker indisponível", linha.UltimoErro);
    }

    [Fact]
    public async Task Depois_do_limite_de_tentativas_a_linha_para_de_ser_tentada()
    {
        var repositorio = new OutboxEmMemoria();
        var publicador = new PublicadorFalso { FalharNasPrimeiras = int.MaxValue };
        var linha = repositorio.Gravar(Outbox.Enfileirar(UmaOrdem(), "corr-1", "os-service"));
        linha.Tentativas = ProcessadorDoOutbox.MaximoDeTentativas - 1;

        var processador = new ProcessadorDoOutbox(repositorio, publicador);

        await processador.ProcessarLoteAsync();

        // Falhou, mas continua na tabela: visível para alguém olhar, que é a
        // razão de o outbox ser tabela e não fila em memória.
        Assert.Equal(EStatusOutbox.Falhou, linha.Status);
        Assert.Equal(ProcessadorDoOutbox.MaximoDeTentativas, linha.Tentativas);
        Assert.Single(repositorio.Linhas);
    }

    [Fact]
    public async Task Uma_falha_no_meio_do_lote_nao_impede_os_outros()
    {
        var repositorio = new OutboxEmMemoria();
        var publicador = new PublicadorFalso { FalharNasPrimeiras = 1 };
        repositorio.Gravar(Outbox.Enfileirar(UmaOrdem(), "corr-1", "os-service"));
        repositorio.Gravar(Outbox.Enfileirar(UmaOrdem(), "corr-2", "os-service"));
        repositorio.Gravar(Outbox.Enfileirar(UmaOrdem(), "corr-3", "os-service"));

        var processador = new ProcessadorDoOutbox(repositorio, publicador);

        var publicados = await processador.ProcessarLoteAsync();

        Assert.Equal(2, publicados);
        Assert.Equal(EStatusOutbox.Pendente, repositorio.Linhas[0].Status);
        Assert.Equal(EStatusOutbox.Publicado, repositorio.Linhas[1].Status);
        Assert.Equal(EStatusOutbox.Publicado, repositorio.Linhas[2].Status);
    }

    [Fact]
    public async Task Linha_agendada_para_o_futuro_nao_entra_no_lote()
    {
        var repositorio = new OutboxEmMemoria();
        var publicador = new PublicadorFalso();
        var linha = repositorio.Gravar(Outbox.Enfileirar(UmaOrdem(), "corr-1", "os-service"));
        linha.ProximaTentativaEm = DateTime.UtcNow.AddMinutes(5);

        var processador = new ProcessadorDoOutbox(repositorio, publicador);

        Assert.Equal(0, await processador.ProcessarLoteAsync());
        Assert.Empty(publicador.Publicados);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 8)]
    [InlineData(8, 256)]
    [InlineData(20, 300)]
    public void Backoff_cresce_e_para_no_teto_de_cinco_minutos(int tentativas, int segundosEsperados)
    {
        Assert.Equal(TimeSpan.FromSeconds(segundosEsperados), ProcessadorDoOutbox.Backoff(tentativas));
    }
}
