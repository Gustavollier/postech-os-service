using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using PosTech.Contratos;
using Reqnroll;

namespace PosTech.Os.Testes.Bdd;

/// <summary>
/// Os passos dos cenários da saga.
///
/// Cada passo faz o que um ator de verdade faria: a recepção chama a API do OS
/// Service; o Execução e o Billing publicam eventos no broker. Nenhum passo
/// chama o orquestrador por método, e nenhum passo mexe no banco: o estado é
/// lido pela mesma rota <c>/saga</c> que aparece no vídeo.
/// </summary>
[Binding]
public sealed class PassosDaSaga(FeatureContext funcionalidade)
{
    private const string Execucao = "execucao-service";
    private const string Billing = "billing-service";

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private readonly AmbienteDaSaga _ambiente = funcionalidade.Get<AmbienteDaSaga>();

    // O estado de um cenário: a ordem que ele abriu e os ids que os serviços
    // simulados geraram para ela.
    private readonly string _correlationId = $"bdd-{Guid.NewGuid():N}";
    private int _idOrdem;
    private decimal _valor = 198.30m;
    private DiagnosticoConcluido? _ultimoDiagnostico;

    private string IdOrcamento => $"orc-{_idOrdem}";

    private string IdPagamento => $"pag-{_idOrdem}";

    private string ReferenciaExterna => $"mp-{_idOrdem}";

    // -----------------------------------------------------------------------
    // Recepção
    // -----------------------------------------------------------------------

    [Given("que a recepção abriu uma ordem de serviço para {string} com o veículo {string}")]
    public async Task AbrirOrdem(string nome, string veiculo)
    {
        var resposta = await _ambiente.Cliente(_correlationId).PostAsJsonAsync("/api/v1/ordens-servico", new
        {
            idCliente = 10, idVeiculo = 20, idFuncionario = 30,
            nomeCliente = nome, descricaoVeiculo = veiculo, emailCliente = "cliente@oficina.test",
        });

        resposta.EnsureSuccessStatusCode();

        _idOrdem = (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    // -----------------------------------------------------------------------
    // Execução, simulado
    // -----------------------------------------------------------------------

    [When("o Execução concluir o diagnóstico no valor de {decimal}")]
    public async Task ConcluirDiagnostico(decimal valor)
    {
        _valor = valor;

        _ultimoDiagnostico = new DiagnosticoConcluido(
            _idOrdem, 30, 10, "Ana Prado", "cliente@oficina.test",
            [new ItemDaOrdem("Troca de óleo e filtro", ETipoItem.MaoDeObra, 1, valor)],
            valor, DateTime.UtcNow);

        await _ambiente.PublicarComoAsync(_ultimoDiagnostico, Execucao, _correlationId);
    }

    [When("o mesmo diagnóstico for entregue de novo")]
    public async Task DiagnosticoRepetido()
    {
        // Entrega repetida é comportamento normal do RabbitMQ. Uma mensagem
        // nova com o mesmo conteúdo é o pior caso: o registro de idempotência
        // não a reconhece, e quem segura é a regra do próprio orquestrador.
        await _ambiente.PublicarComoAsync(_ultimoDiagnostico!, Execucao, _correlationId);
        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    [When("o Execução finalizar o serviço")]
    public Task FinalizarServico() =>
        _ambiente.PublicarComoAsync(new ExecucaoFinalizada(_idOrdem, 30, DateTime.UtcNow), Execucao, _correlationId);

    [When("o prazo da execução passar sem o Execução assumir a ordem")]
    public static Task PrazoPassa() =>
        // O prazo da funcionalidade é de três segundos e a varredura roda a
        // cada meio segundo. Não há o que publicar: é justamente a ausência
        // de evento que o varredor detecta.
        Task.Delay(TimeSpan.FromSeconds(4));

    // -----------------------------------------------------------------------
    // Billing e cliente, simulados
    // -----------------------------------------------------------------------

    [When("o Billing gerar o orçamento")]
    public Task GerarOrcamento() =>
        _ambiente.PublicarComoAsync(new OrcamentoGerado(_idOrdem, IdOrcamento, _valor, DateTime.UtcNow), Billing, _correlationId);

    [When("o cliente aprovar o orçamento")]
    public Task AprovarOrcamento() =>
        _ambiente.PublicarComoAsync(new OrcamentoAprovado(_idOrdem, IdOrcamento, _valor, DateTime.UtcNow), Billing, _correlationId);

    [When("o cliente rejeitar o orçamento com o motivo {string}")]
    public Task RejeitarOrcamento(string motivo) =>
        _ambiente.PublicarComoAsync(new OrcamentoRejeitado(_idOrdem, IdOrcamento, motivo, DateTime.UtcNow), Billing, _correlationId);

    [When("o Billing confirmar o pagamento")]
    public Task ConfirmarPagamento() =>
        _ambiente.PublicarComoAsync(
            new PagamentoConfirmado(_idOrdem, IdPagamento, _valor, ReferenciaExterna, DateTime.UtcNow), Billing, _correlationId);

    [When("o adquirente recusar o pagamento com o motivo {string}")]
    public async Task RecusarPagamento(string motivo)
    {
        // A recusa só vale com a saga esperando pagamento; publicar antes de
        // a aprovação ser processada faria o orquestrador, corretamente,
        // ignorar a recusa.
        await SagaNoPasso("AguardandoPagamento");

        await _ambiente.PublicarComoAsync(new PagamentoRecusado(_idOrdem, IdPagamento, motivo, DateTime.UtcNow), Billing, _correlationId);
    }

    [When("o Billing estornar o pagamento")]
    public Task EstornarPagamento() =>
        _ambiente.PublicarComoAsync(
            new PagamentoEstornado(_idOrdem, IdPagamento, ReferenciaExterna, _valor, "Execução não assumiu", DateTime.UtcNow),
            Billing, _correlationId);

    // -----------------------------------------------------------------------
    // Atalhos de cenário: levam a saga a um passo conhecido.
    // -----------------------------------------------------------------------

    [Given("que o diagnóstico foi concluído e o orçamento gerado")]
    public async Task DiagnosticoEOrcamento()
    {
        await ConcluirDiagnostico(198.30m);
        await SagaNoPasso("AguardandoOrcamento");
        await GerarOrcamento();
        await SagaNoPasso("AguardandoAprovacao");
    }

    [Given("que o cliente aprovou e o pagamento foi confirmado")]
    public async Task AprovadoEPago()
    {
        await AprovarOrcamento();
        await SagaNoPasso("AguardandoPagamento");
        await ConfirmarPagamento();
        await SagaNoPasso("AguardandoExecucao");
    }

    // -----------------------------------------------------------------------
    // Verificações
    // -----------------------------------------------------------------------

    [Then("a saga está no passo {string}")]
    public async Task SagaNoPasso(string passo)
    {
        await InfraDeEspera.AguardarAsync(
            async () => (await SagaAsync()).GetProperty("passo").GetString() == passo,
            $"a saga da ordem {_idOrdem} chegar em {passo}");
    }

    [Then("a ordem está com o status {string}")]
    public async Task OrdemNoStatus(string status)
    {
        await InfraDeEspera.AguardarAsync(
            async () => (await OrdemAsync()).GetProperty("status").GetString() == status,
            $"a ordem {_idOrdem} ficar {status}");
    }

    [Then("o histórico da ordem passou por {string}")]
    public async Task HistoricoPassouPor(string sequencia)
    {
        var esperado = sequencia.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var historico = await HistoricoAsync();

        Assert.Equal(esperado, historico);
    }

    [Then("o histórico da ordem tem {int} transição para {string}")]
    public async Task TransicoesPara(int quantidade, string status)
    {
        Assert.Equal(quantidade, (await HistoricoAsync()).Count(s => s == status));
    }

    [Then("o motivo da compensação é {string}")]
    public async Task MotivoE(string motivo)
    {
        Assert.Equal(motivo, (await SagaAsync()).GetProperty("motivoDaCompensacao").GetString());
    }

    [Then("o motivo da compensação contém {string}")]
    public async Task MotivoContem(string trecho)
    {
        Assert.Contains(trecho, (await SagaAsync()).GetProperty("motivoDaCompensacao").GetString() ?? string.Empty);
    }

    [Then("o Execução recebe o evento {string}")]
    public async Task ExecucaoRecebe(string tipo)
    {
        var envelope = await _ambiente.FilaDoExecucao.EsperarAsync(tipo, _correlationId);
        Assert.Equal(_correlationId, envelope.CorrelationId);
    }

    [Then("o Billing recebe o evento {string}")]
    public async Task BillingRecebe(string tipo)
    {
        var envelope = await _ambiente.FilaDoBilling.EsperarAsync(tipo, _correlationId);
        Assert.Equal(_correlationId, envelope.CorrelationId);
    }

    [Then("o Billing recebe o evento {string} com o rastro da compensação por prazo")]
    public Task BillingRecebeDoVarredor(string tipo) => EsperarDoVarredorAsync(_ambiente.FilaDoBilling, tipo);

    [Then("o Execução recebe o evento {string} com o rastro da compensação por prazo")]
    public Task ExecucaoRecebeDoVarredor(string tipo) => EsperarDoVarredorAsync(_ambiente.FilaDoExecucao, tipo);

    private Task EsperarDoVarredorAsync(EspiaoDaFila fila, string tipo)
    {
        var prefixo = $"timeout-{_idOrdem}-";

        return InfraDeEspera.AguardarAsync(
            () => Task.FromResult(fila.Recebidos.Any(e => e.Tipo == tipo && e.CorrelationId.StartsWith(prefixo, StringComparison.Ordinal))),
            $"o evento {tipo} da compensação por prazo da ordem {_idOrdem}");
    }

    [Then("o Execução não recebe o evento {string}")]
    public async Task ExecucaoNaoRecebe(string tipo)
    {
        // Ausência só se prova esperando: tempo de sobra para o outbox
        // publicar, se fosse publicar.
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.DoesNotContain(_ambiente.FilaDoExecucao.Recebidos,
            e => e.Tipo == tipo && e.CorrelationId == _correlationId);
    }

    // -----------------------------------------------------------------------

    private async Task<JsonElement> SagaAsync() =>
        await _ambiente.Cliente(_correlationId).GetFromJsonAsync<JsonElement>($"/api/v1/ordens-servico/{_idOrdem}/saga");

    private async Task<JsonElement> OrdemAsync() =>
        await _ambiente.Cliente(_correlationId).GetFromJsonAsync<JsonElement>($"/api/v1/ordens-servico/{_idOrdem}");

    private async Task<List<string>> HistoricoAsync()
    {
        var status = await _ambiente.Cliente(_correlationId)
            .GetFromJsonAsync<JsonElement>($"/api/v1/ordens-servico/{_idOrdem}/status");

        return status.GetProperty("historico").EnumerateArray()
            .Select(t => t.GetProperty("para").GetString()!)
            .ToList();
    }
}

/// <summary>
/// Espera uma condição assíncrona. Fluxo por fila não é verificável logo
/// depois de publicar: o consumidor está noutra thread e o outbox publica em
/// laço.
/// </summary>
internal static class InfraDeEspera
{
    public static async Task AguardarAsync(Func<Task<bool>> condicao, string oQue, int segundos = 30)
    {
        var limite = DateTime.UtcNow.AddSeconds(segundos);

        while (DateTime.UtcNow < limite)
        {
            try
            {
                if (await condicao())
                    return;
            }
            catch (HttpRequestException)
            {
                // A rota pode responder 404 enquanto a saga ainda não existe.
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Esperei {segundos}s por: {oQue}. Não aconteceu.");
    }
}
