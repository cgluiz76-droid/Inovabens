using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Inovabens.Core.Dados;

/// <summary>
/// Aplica os scripts de <c>Dados/Migracoes/*.sql</c> (recursos embutidos) em ordem de nome, cada um na sua
/// transação, e registra o que já rodou em <c>schema_migracoes</c>. Um advisory lock impede que duas
/// instâncias subindo juntas apliquem o mesmo script.
/// <para>
/// Convivência com a versão Node: os três primeiros scripts são os mesmos de apps/api/drizzle. Num banco
/// criado pelo Node, eles são reconhecidos pelo diário do Drizzle e só registrados aqui. Num banco criado
/// pelo .NET, o diário do Drizzle também é preenchido, para o <c>migrate</c> do Node não tentar recriar as tabelas.
/// </para>
/// </summary>
public sealed class Migrador(InovabensOpcoes op, ILogger<Migrador> log)
{
    private const long ChaveLock = 0x1A0BE45_0001;

    /// <summary>Scripts herdados do Drizzle e o "when" de cada um em apps/api/drizzle/meta/_journal.json.</summary>
    private static readonly Dictionary<string, long> Drizzle = new()
    {
        ["0000_init"] = 1793046596452,
        ["0001_regras_banco"] = 1793046607639,
        ["0002_auth_tokens"] = 1793102256590,
    };

    public sealed record Script(string Nome, string Sql)
    {
        /// <summary>SHA-256 do conteúdo com quebras de linha normalizadas (o checkout no Windows pode trocar LF por CRLF).</summary>
        public string Hash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sql.ReplaceLineEndings("\n"))));
    }

    public static IReadOnlyList<Script> Scripts()
    {
        var asm = typeof(Migrador).Assembly;
        const string prefixo = "Inovabens.Core.Dados.Migracoes.";
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefixo) && n.EndsWith(".sql"))
            .Order(StringComparer.Ordinal)
            .Select(n => new Script(n[prefixo.Length..^4], Ler(asm, n)))
            .ToList();
    }

    /// <summary>Aplica os scripts pendentes e devolve os nomes aplicados nesta execução.</summary>
    public async Task<IReadOnlyList<string>> AplicarAsync(CancellationToken ct = default)
    {
        await using var con = new NpgsqlConnection(InovabensDb.NormalizarConexao(op.DatabaseUrl));
        await con.OpenAsync(ct);
        await Executar(con, null, $"SELECT pg_advisory_lock({ChaveLock})", ct);
        try
        {
            await Executar(con, null, """
                CREATE TABLE IF NOT EXISTS schema_migracoes (
                    nome text PRIMARY KEY,
                    hash text NOT NULL,
                    aplicada_em timestamptz NOT NULL DEFAULT now()
                )
                """, ct);

            var scripts = Scripts();
            var aplicadas = await Aplicadas(con, ct);
            if (aplicadas.Count == 0) aplicadas = await AdotarDrizzle(con, scripts, ct);

            foreach (var (nome, hash) in aplicadas)
                if (scripts.FirstOrDefault(s => s.Nome == nome) is { } s && s.Hash != hash)
                    log.LogWarning("O script {Script} foi alterado depois de aplicado; a alteração não será executada. Crie um novo script.", nome);

            var novas = new List<string>();
            foreach (var s in scripts.Where(s => !aplicadas.ContainsKey(s.Nome)))
            {
                await using var tx = await con.BeginTransactionAsync(ct);
                await Executar(con, tx, s.Sql, ct);
                await Registrar(con, tx, s, ct);
                if (Drizzle.TryGetValue(s.Nome, out var quando)) await RegistrarNoDrizzle(con, tx, s, quando, ct);
                await tx.CommitAsync(ct);
                log.LogInformation("Migração aplicada: {Script}", s.Nome);
                novas.Add(s.Nome);
            }
            return novas;
        }
        finally
        {
            await Executar(con, null, $"SELECT pg_advisory_unlock({ChaveLock})", CancellationToken.None);
        }
    }

    /// <summary>Banco criado pelo Node: marca como aplicados os scripts que o Drizzle já rodou.</summary>
    private async Task<Dictionary<string, string>> AdotarDrizzle(NpgsqlConnection con, IReadOnlyList<Script> scripts, CancellationToken ct)
    {
        await using (var existe = new NpgsqlCommand("SELECT to_regclass('drizzle.__drizzle_migrations') IS NOT NULL", con))
            if (await existe.ExecuteScalarAsync(ct) is not true) return [];
        await using var cmd = new NpgsqlCommand("SELECT max(created_at) FROM drizzle.__drizzle_migrations", con);
        if (await cmd.ExecuteScalarAsync(ct) is not long ultima) return [];

        await using var tx = await con.BeginTransactionAsync(ct);
        var adotadas = new Dictionary<string, string>();
        foreach (var s in scripts.Where(s => Drizzle.TryGetValue(s.Nome, out var q) && q <= ultima))
        {
            await Registrar(con, tx, s, ct);
            adotadas[s.Nome] = s.Hash;
        }
        await tx.CommitAsync(ct);
        if (adotadas.Count > 0)
            log.LogInformation("Banco criado pela versão Node: {Qtd} migração(ões) do Drizzle reconhecidas", adotadas.Count);
        return adotadas;
    }

    /// <summary>Mesma tabela que o migrator do Drizzle cria; ele só compara created_at com o "when" do diário.</summary>
    private static async Task RegistrarNoDrizzle(NpgsqlConnection con, NpgsqlTransaction tx, Script s, long quando, CancellationToken ct)
    {
        await Executar(con, tx, """
            CREATE SCHEMA IF NOT EXISTS drizzle;
            CREATE TABLE IF NOT EXISTS drizzle.__drizzle_migrations (id SERIAL PRIMARY KEY, hash text NOT NULL, created_at bigint)
            """, ct);
        await using var cmd = new NpgsqlCommand("INSERT INTO drizzle.__drizzle_migrations (hash, created_at) VALUES (@h, @q)", con, tx);
        cmd.Parameters.AddWithValue("h", s.Hash);
        cmd.Parameters.AddWithValue("q", quando);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task Registrar(NpgsqlConnection con, NpgsqlTransaction tx, Script s, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("INSERT INTO schema_migracoes (nome, hash) VALUES (@n, @h)", con, tx);
        cmd.Parameters.AddWithValue("n", s.Nome);
        cmd.Parameters.AddWithValue("h", s.Hash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Dictionary<string, string>> Aplicadas(NpgsqlConnection con, CancellationToken ct)
    {
        var r = new Dictionary<string, string>();
        await using var cmd = new NpgsqlCommand("SELECT nome, hash FROM schema_migracoes", con);
        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct)) r[leitor.GetString(0)] = leitor.GetString(1);
        return r;
    }

    private static async Task Executar(NpgsqlConnection con, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, con, tx) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Ler(Assembly asm, string recurso)
    {
        using var s = asm.GetManifestResourceStream(recurso)!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }
}
