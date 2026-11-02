using System.Reflection;
using Dapper;
using Microsoft.Data.SqlClient;

namespace PosTech.Persistencia;

/// <summary>
/// Aplica o schema do os-db na partida do serviço.
///
/// Os dois scripts são idempotentes, então rodar em toda réplica, a cada
/// subida, é seguro. Isto substitui o "rodar o sqlcmd à mão" do README: na
/// Fase 4 o banco é recriado do zero perto da gravação, e um passo manual
/// esquecido seria o serviço subindo contra um banco sem tabela nenhuma.
///
/// O schema é do serviço, e não da infraestrutura: o postech-infra-db só cria
/// o banco e o usuário os_app, com permissão de criar as próprias tabelas.
/// </summary>
public static class InstaladorDoSchema
{
    /// <summary>A ordem importa: o schema da mensageria não depende do da ordem, mas é o que o README sempre mandou rodar.</summary>
    public static readonly string[] Scripts = ["schema-os.sql", "schema-mensageria.sql"];

    public static string Ler(string arquivo)
    {
        var nome = $"PosTech.Persistencia.{arquivo}";

        using var recurso = Assembly.GetExecutingAssembly().GetManifestResourceStream(nome)
            ?? throw new InvalidOperationException($"{arquivo} não está embutido no assembly.");

        using var leitor = new StreamReader(recurso);
        return leitor.ReadToEnd();
    }

    public static async Task AplicarAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var conexao = new SqlConnection(connectionString);
        await conexao.OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (var arquivo in Scripts)
        {
            // GO é separador do sqlcmd, não do T-SQL: o SqlClient não o entende.
            var lotes = Ler(arquivo)
                .Replace("\r\n", "\n")
                .Split("\nGO", StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.Split('\n').Any(linha => linha.Trim().Length > 0 && !linha.TrimStart().StartsWith("--")));

            foreach (var lote in lotes)
                await conexao.ExecuteAsync(new CommandDefinition(lote, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }
}
