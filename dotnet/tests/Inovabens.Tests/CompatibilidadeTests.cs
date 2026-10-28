using System.Text.RegularExpressions;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Isopoh.Cryptography.Argon2;
using Microsoft.EntityFrameworkCore;

namespace Inovabens.Tests;

/// <summary>Garante que a versão .NET conversa com o banco e os dados gravados pela versão Node.</summary>
public class CompatibilidadeTests
{
    // Gerados pela própria API Node (@node-rs/argon2 e common/crypto.ts) para garantir compatibilidade.
    private const string HashNode = "$argon2id$v=19$m=19456,t=2,p=1$8ZtngF2K14z1KyE/Zy/kZA$+FtujNAA6SfMMXtwVL9H8f91mWsxFJqLpcumZ6JQg2A";
    private const string ChaveHex = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
    private const string CifradoNode = "3dw6hQENKORUK1PbjnhEjWwKixP0H9bI/AKF6/mqBp06sohf4B3DiZu7rGQ=";

    [Fact]
    public void Senha_gravada_pelo_Node_e_aceita()
    {
        Assert.True(Argon2.Verify(HashNode, "Trocar@123456"));
        Assert.False(Argon2.Verify(HashNode, "senha-errada"));
    }

    [Fact]
    public void Segredo_TOTP_cifrado_pelo_Node_e_decifrado()
    {
        Assert.Equal("JBSWY3DPEHPK3PXP", Cripto.Decifrar(CifradoNode, ChaveHex));
        Assert.Equal("abc", Cripto.Decifrar(Cripto.Cifrar("abc", ChaveHex), ChaveHex));
    }

    [Theory]
    [InlineData("postgres://inovabens:inovabens@localhost:5432/inovabens", "Host=localhost;Port=5432;Database=inovabens;Username=inovabens;Password=inovabens")]
    [InlineData("postgresql://u:p%40ss@db.exemplo.com/banco?sslmode=require", "Host=db.exemplo.com;Port=5432;Database=banco;Username=u;Password=p@ss;SSL Mode=Require")]
    [InlineData("Host=x;Database=y", "Host=x;Database=y")]
    public void DATABASE_URL_do_Node_vira_conexao_Npgsql(string url, string esperado) =>
        Assert.Equal(esperado, InovabensDb.NormalizarConexao(url));

    /// <summary>
    /// Toda tabela/coluna mapeada no EF existe nos scripts de migração (Inovabens.Core/Dados/Migracoes).
    /// Pega erro de digitação de coluna sem precisar de banco rodando.
    /// </summary>
    [Fact]
    public void Mapeamento_EF_bate_com_as_migrations_SQL()
    {
        var sql = string.Concat(Migrador.Scripts().Select(s => s.Sql.ReplaceLineEndings("\n")));
        var tabelas = Regex.Matches(sql, @"CREATE TABLE ""(\w+)"" \((.*?)\n\);", RegexOptions.Singleline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        // colunas acrescentadas depois (scripts só do .NET, como 0003_*)
        foreach (Match m in Regex.Matches(sql, @"ALTER TABLE ""?(\w+)""? ADD COLUMN (?:IF NOT EXISTS )?""?(\w+)""?", RegexOptions.IgnoreCase))
            tabelas[m.Groups[1].Value] += $"\n\"{m.Groups[2].Value}\" ";

        var opcoes = new DbContextOptionsBuilder<InovabensDb>();
        InovabensDb.ConfigurarNpgsql(opcoes, "Host=localhost;Database=x");
        using var db = new InovabensDb(opcoes.Options);

        var faltando = new List<string>();
        foreach (var e in db.Model.GetEntityTypes())
        {
            var tabela = e.GetTableName()!;
            if (!tabelas.TryGetValue(tabela, out var corpo)) { faltando.Add($"tabela {tabela}"); continue; }
            foreach (var p in e.GetProperties())
                if (!corpo.Contains($"\"{p.GetColumnName()}\" ")) faltando.Add($"{tabela}.{p.GetColumnName()}");
        }
        Assert.Empty(faltando);
    }

    /// <summary>
    /// Enquanto a versão Node existir, os scripts herdados precisam ser idênticos aos de apps/api/drizzle:
    /// os dois lados aplicam e reconhecem as mesmas migrações no mesmo banco.
    /// </summary>
    [Fact]
    public void Scripts_herdados_iguais_aos_do_Drizzle()
    {
        var pasta = PastaDrizzle();
        if (pasta is null) return; // versão Node já removida
        foreach (var arquivo in Directory.GetFiles(pasta, "*.sql"))
        {
            var nome = Path.GetFileNameWithoutExtension(arquivo);
            var script = Migrador.Scripts().SingleOrDefault(s => s.Nome == nome);
            Assert.True(script is not null, $"{nome}.sql existe no Drizzle mas não em Dados/Migracoes");
            Assert.Equal(File.ReadAllText(arquivo).ReplaceLineEndings("\n"), script.Sql.ReplaceLineEndings("\n"));
        }
    }

    private static string? PastaDrizzle()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "apps", "api", "drizzle");
            if (Directory.Exists(p)) return p;
        }
        return null;
    }
}
