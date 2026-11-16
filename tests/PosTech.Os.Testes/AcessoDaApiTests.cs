using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PosTech.Os.Api.Controllers;

namespace PosTech.Os.Testes;

/// <summary>
/// Quem pode o quê nas rotas do OS Service.
///
/// O gateway só confere se o token é válido. Sem a checagem daqui, um cliente
/// autenticado listava as ordens de todos os clientes e lia a saga de qualquer
/// uma pelo número — foi assim que o defeito apareceu, com o painel no ar.
/// </summary>
[Collection("os-api")]
public class AcessoDaApiTests
{
    private readonly AmbienteDoOsService _ambiente;

    public AcessoDaApiTests(AmbienteDoOsService ambiente) => _ambiente = ambiente;

    private HttpClient Como(string? token) => _ambiente.Cliente().Como(token);

    private async Task<int> AbrirParaAsync(int idCliente)
    {
        var resposta = await _ambiente.Cliente().PostAsJsonAsync("/api/v1/ordens-servico", new AbrirOrdemRequest(
            IdCliente: idCliente, IdVeiculo: 20, IdFuncionario: 30,
            NomeCliente: $"Cliente {idCliente}", DescricaoVeiculo: "Gol AAA-1234", EmailCliente: "c@oficina.test"));

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    [Theory]
    [InlineData("/api/v1/ordens-servico")]
    [InlineData("/api/v1/ordens-servico/1")]
    [InlineData("/api/v1/ordens-servico/1/status")]
    [InlineData("/api/v1/ordens-servico/1/saga")]
    public async Task Sem_token_nenhuma_rota_de_negocio_responde(string rota)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Como(null).GetAsync(rota)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Como(TokenDeTeste.Vencido()).GetAsync(rota)).StatusCode);
    }

    [Fact]
    public async Task O_health_continua_publico()
    {
        Assert.Equal(HttpStatusCode.OK, (await Como(null).GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Cliente_nao_abre_ordem_e_qualquer_cargo_da_equipe_abre()
    {
        var corpo = new AbrirOrdemRequest(4101, 20, 30, "Ana", "Gol AAA-1234", "ana@oficina.test");

        var comoCliente = await Como(TokenDeTeste.Cliente(4101)).PostAsJsonAsync("/api/v1/ordens-servico", corpo);
        var comoMecanico = await Como(TokenDeTeste.Mecanico()).PostAsJsonAsync("/api/v1/ordens-servico", corpo);

        Assert.Equal(HttpStatusCode.Forbidden, comoCliente.StatusCode);
        Assert.Equal(HttpStatusCode.Created, comoMecanico.StatusCode);
    }

    [Fact]
    public async Task A_listagem_do_cliente_so_traz_as_ordens_dele_mesmo_pedindo_as_de_outro()
    {
        var minha = await AbrirParaAsync(4201);
        var alheia = await AbrirParaAsync(4202);
        var http = Como(TokenDeTeste.Cliente(4201));

        // Sem filtro, e pedindo explicitamente o cliente errado: o resultado é
        // o mesmo, porque o filtro vem do token e não da query.
        foreach (var rota in new[] { "/api/v1/ordens-servico", "/api/v1/ordens-servico?idCliente=4202" })
        {
            var ordens = await http.GetFromJsonAsync<List<OrdemResponse>>(rota);

            Assert.Contains(ordens!, o => o.Id == minha);
            Assert.DoesNotContain(ordens!, o => o.Id == alheia);
            Assert.All(ordens!, o => Assert.Equal(4201, o.IdCliente));
        }
    }

    [Fact]
    public async Task A_equipe_lista_as_ordens_de_todos_os_clientes()
    {
        var umaOrdem = await AbrirParaAsync(4301);
        var outraOrdem = await AbrirParaAsync(4302);

        var ordens = await Como(TokenDeTeste.Mecanico()).GetFromJsonAsync<List<OrdemResponse>>("/api/v1/ordens-servico");

        Assert.Contains(ordens!, o => o.Id == umaOrdem);
        Assert.Contains(ordens!, o => o.Id == outraOrdem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/status")]
    [InlineData("/saga")]
    public async Task Ordem_de_outro_cliente_responde_como_se_nao_existisse(string sufixo)
    {
        var id = await AbrirParaAsync(4401);
        var rota = $"/api/v1/ordens-servico/{id}{sufixo}";

        Assert.Equal(HttpStatusCode.OK, (await Como(TokenDeTeste.Cliente(4401)).GetAsync(rota)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Como(TokenDeTeste.Mecanico()).GetAsync(rota)).StatusCode);

        // 404 e não 403: a resposta não confirma que a ordem existe.
        Assert.Equal(HttpStatusCode.NotFound, (await Como(TokenDeTeste.Cliente(4402)).GetAsync(rota)).StatusCode);
    }
}
