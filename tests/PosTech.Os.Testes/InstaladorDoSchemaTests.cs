using Dapper;
using Microsoft.Data.SqlClient;
using PosTech.Persistencia;

namespace PosTech.Os.Testes;

/// <summary>
/// O schema aplicado pelo próprio serviço na partida.
///
/// Na Fase 4 o os-db é recriado do zero perto da gravação: o serviço precisa
/// subir contra um banco vazio e deixá-lo pronto, e subir de novo — toda
/// réplica, todo restart — sem quebrar o que já existe.
/// </summary>
[Collection("os-api")]
public class InstaladorDoSchemaTests(AmbienteDoOsService ambiente)
{
    private async Task<string> BancoVazioAsync()
    {
        var nome = $"os_vazio_{Guid.NewGuid():N}";

        await using (var mestre = new SqlConnection(ambiente.SqlConnectionString))
            await mestre.ExecuteAsync($"CREATE DATABASE [{nome}]");

        return new SqlConnectionStringBuilder(ambiente.SqlConnectionString) { InitialCatalog = nome }.ConnectionString;
    }

    [Fact]
    public async Task Num_banco_vazio_cria_todas_as_tabelas_do_servico()
    {
        var conexao = await BancoVazioAsync();

        await InstaladorDoSchema.AplicarAsync(conexao);

        await using var banco = new SqlConnection(conexao);
        var tabelas = (await banco.QueryAsync<string>("SELECT name FROM sys.tables")).ToHashSet();

        Assert.Contains("OrdemServico", tabelas);
        Assert.Contains("SagaOrdemServico", tabelas);
        Assert.Contains("EventoOutbox", tabelas);
        Assert.Contains("EventoProcessado", tabelas);
    }

    [Fact]
    public async Task Aplicar_de_novo_nao_quebra_nem_apaga_dado()
    {
        var conexao = await BancoVazioAsync();

        await InstaladorDoSchema.AplicarAsync(conexao);

        await using var banco = new SqlConnection(conexao);
        var antes = await banco.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables");

        await InstaladorDoSchema.AplicarAsync(conexao);
        await InstaladorDoSchema.AplicarAsync(conexao);

        Assert.Equal(antes, await banco.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables"));
    }

    [Fact]
    public void Os_scripts_viajam_dentro_do_assembly()
    {
        // No contêiner não há pasta src/: se o script não estiver embutido,
        // o serviço não sobe.
        foreach (var arquivo in InstaladorDoSchema.Scripts)
            Assert.Contains("CREATE TABLE", InstaladorDoSchema.Ler(arquivo));
    }
}
