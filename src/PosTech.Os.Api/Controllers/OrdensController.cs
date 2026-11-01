using Microsoft.AspNetCore.Mvc;
using PosTech.Contratos;
using PosTech.Mensageria;
using PosTech.Os.Api.Aplicacao;
using PosTech.Persistencia;
using PosTech.Saga;

namespace PosTech.Os.Api.Controllers;

public sealed record AbrirOrdemRequest(
    int IdCliente,
    int IdVeiculo,
    int IdFuncionario,
    string NomeCliente,
    string DescricaoVeiculo,
    string EmailCliente);

public sealed record OrdemResponse(
    int Id,
    int IdCliente,
    int IdVeiculo,
    int IdFuncionario,
    string Status,
    string? NomeCliente,
    string? DescricaoVeiculo,
    DateTime CriadoEm,
    DateTime? AtualizadoEm);

[ApiController]
[Route("api/v1/ordens-servico")]
public sealed class OrdensController : ControllerBase
{
    private readonly IOrdemRepositorio _ordens;
    private readonly ISagaRepositorio _sagas;

    public OrdensController(IOrdemRepositorio ordens, ISagaRepositorio sagas)
    {
        _ordens = ordens;
        _sagas = sagas;
    }

    /// <summary>
    /// Abre a ordem. A ordem, o estado inicial da saga e o evento
    /// <c>OrdemAberta</c> entram numa transação só; a publicação fica com o
    /// processo do outbox, então esta resposta não espera o broker.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Abrir([FromBody] AbrirOrdemRequest corpo, CancellationToken cancellationToken)
    {
        if (corpo is null)
            return BadRequest(new { erro = "Corpo da requisição é obrigatório." });

        if (corpo.IdCliente <= 0 || corpo.IdVeiculo <= 0 || corpo.IdFuncionario <= 0)
            return BadRequest(new { erro = "Cliente, veículo e funcionário são obrigatórios." });

        var correlationId = CorrelationId();
        var agora = DateTime.UtcNow;

        var id = await _ordens.AbrirAsync(
            corpo.IdCliente, corpo.IdVeiculo, corpo.IdFuncionario,
            corpo.NomeCliente, corpo.DescricaoVeiculo,
            montarEvento: idOrdem => Outbox.Enfileirar(
                new OrdemAberta(
                    idOrdem, corpo.IdCliente, corpo.NomeCliente, corpo.EmailCliente,
                    corpo.IdVeiculo, corpo.DescricaoVeiculo, corpo.IdFuncionario, agora),
                correlationId,
                ManipuladorDaSaga.Origem,
                agora),
            agora,
            cancellationToken);

        return CreatedAtAction(nameof(Obter), new { id }, new { id });
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obter(int id, CancellationToken cancellationToken)
    {
        var ordem = await _ordens.ObterAsync(id, cancellationToken);

        return ordem is null ? NotFound() : Ok(Mapear(ordem));
    }

    /// <summary>
    /// Lista ordenada pela prioridade da recepção. Com <c>idCliente</c>, filtra
    /// — é por aqui que a checagem de posse limita o que o cliente enxerga.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Listar([FromQuery] int? idCliente, CancellationToken cancellationToken)
    {
        var ordens = await _ordens.ListarAsync(idCliente, cancellationToken);

        return Ok(ordens.Select(Mapear));
    }

    [HttpGet("{id:int}/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Status(int id, CancellationToken cancellationToken)
    {
        var ordem = await _ordens.ObterAsync(id, cancellationToken);

        if (ordem is null)
            return NotFound();

        var historico = await _ordens.HistoricoAsync(id, cancellationToken);

        return Ok(new
        {
            id = ordem.Id,
            status = ordem.Status.ToString(),
            historico = historico.Select(h => new
            {
                de = h.StatusAnterior?.ToString(),
                para = h.StatusNovo.ToString(),
                motivo = h.Motivo,
                ocorridoEm = h.OcorridoEm,
            }),
        });
    }

    /// <summary>
    /// O estado da saga, para a demonstração e para o diagnóstico.
    ///
    /// Esta rota existe porque a orquestração só vale a pena se o estado for
    /// visível: é aqui que se mostra a saga em <c>Compensando</c> e o motivo
    /// que a levou até lá.
    /// </summary>
    [HttpGet("{id:int}/saga")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Saga(int id, CancellationToken cancellationToken)
    {
        var estado = await _sagas.ObterAsync(id, cancellationToken);

        if (estado is null)
            return NotFound();

        return Ok(new
        {
            idOrdem = estado.IdOrdem,
            passo = estado.Passo.ToString(),
            statusDaOrdem = estado.StatusDaOrdem.ToString(),
            idOrcamento = estado.IdOrcamento,
            idPagamento = estado.IdPagamento,
            referenciaExterna = estado.ReferenciaExterna,
            valor = estado.Valor,
            motivoDaCompensacao = estado.MotivoDaCompensacao,
            emAndamento = estado.EstaEmAndamento,
            atualizadoEm = estado.AtualizadoEm,
        });
    }

    private static OrdemResponse Mapear(OrdemResumida o) => new(
        o.Id, o.IdCliente, o.IdVeiculo, o.IdFuncionario,
        o.Status.ToString(), o.NomeCliente, o.DescricaoVeiculo, o.CriadoEm, o.AtualizadoEm);

    /// <summary>
    /// O X-Correlation-ID que o gateway criou na borda. Sem ele o rastro do
    /// fluxo distribuído começa aqui em vez de na requisição do cliente.
    /// </summary>
    private string CorrelationId() =>
        Request.Headers.TryGetValue("X-Correlation-ID", out var valor) && !string.IsNullOrWhiteSpace(valor)
            ? valor.ToString()
            : Guid.NewGuid().ToString();
}
