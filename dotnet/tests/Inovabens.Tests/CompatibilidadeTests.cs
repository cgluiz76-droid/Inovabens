using System.Text.RegularExpressions;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Isopoh.Cryptography.Argon2;
using Microsoft.EntityFrameworkCore;

namespace Inovabens.Tests;

/// <summary>Garante que a versão .NET lê o banco e os dados gravados pela antiga versão Node (senhas, segredos 2FA).</summary>
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
}

public class ConexaoTests
{
    [Theory]
    // Render / docker-compose
    [InlineData("postgres://inovabens:inovabens@db:5432/inovabens", "db", 5432, "inovabens", "inovabens", "inovabens")]
    [InlineData("postgresql://u%40x:s%3Ah@dpg-abc.oregon-postgres.render.com/banco?sslmode=require", "dpg-abc.oregon-postgres.render.com", 5432, "banco", "u@x", "s:h")]
    // Cloud SQL pelo socket do Cloud Run: host vazio na URL e o caminho do socket no parâmetro host
    [InlineData("postgres://inovabens:abc123@/inovabens?host=/cloudsql/proj:southamerica-east1:inovabens-db", "/cloudsql/proj:southamerica-east1:inovabens-db", 5432, "inovabens", "inovabens", "abc123")]
    public void Url_do_banco_vira_conexao_do_Npgsql(string url, string host, int porta, string banco, string usuario, string senha)
    {
        var b = new Npgsql.NpgsqlConnectionStringBuilder(InovabensDb.NormalizarConexao(url));
        Assert.Equal((host, porta, banco, usuario, senha), (b.Host, b.Port, b.Database, b.Username, b.Password));
    }

    [Fact]
    public void Sslmode_da_url_e_respeitado_e_conexao_no_formato_do_Npgsql_passa_direto()
    {
        Assert.Equal(Npgsql.SslMode.Require, new Npgsql.NpgsqlConnectionStringBuilder(InovabensDb.NormalizarConexao("postgres://u:p@h/d?sslmode=require")).SslMode);
        Assert.Equal("Host=x;Database=y", InovabensDb.NormalizarConexao("Host=x;Database=y"));
    }
}
