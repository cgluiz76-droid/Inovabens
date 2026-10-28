using Inovabens.Core;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Inovabens.Tests;

/// <summary>Migrações e dados-base aplicados pelo .NET, inclusive em banco criado pela versão Node.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class MigradorTests
{
    [Fact]
    public async Task Segunda_execucao_nao_aplica_nada_e_nao_duplica_dados()
    {
        var op = Opcoes(BancoFixture.Url);
        var antes = await Contagens(BancoFixture.Url);

        Assert.Empty(await new Migrador(op, NullLogger<Migrador>.Instance).AplicarAsync());
        await new DadosIniciais(op, NullLogger<DadosIniciais>.Instance).GarantirAsync();

        Assert.Equal(antes, await Contagens(BancoFixture.Url));
        Assert.Equal(Migrador.Scripts().Count, await Escalar<long>(BancoFixture.Url, "SELECT count(*) FROM schema_migracoes"));
    }

    [Fact]
    public async Task Banco_novo_fica_reconhecivel_pelo_migrate_do_Node()
    {
        // o Drizzle só roda scripts com "when" maior que o created_at mais recente do seu diário
        Assert.Equal(1793102256590L, await Escalar<long>(BancoFixture.Url, "SELECT max(created_at) FROM drizzle.__drizzle_migrations"));
    }

    [Fact]
    public async Task Banco_criado_pelo_Node_e_adotado_sem_reaplicar_scripts()
    {
        await using var banco = await BancoTemporario.CriarAsync();
        await using (var con = new NpgsqlConnection(banco.Conexao))
        {
            // o que o `pnpm db:migrate` deixa no banco: as tabelas e o diário do Drizzle
            await con.OpenAsync();
            foreach (var s in Migrador.Scripts().Where(s => string.CompareOrdinal(s.Nome, "0002_auth_tokens") <= 0))
                await new NpgsqlCommand(s.Sql, con).ExecuteNonQueryAsync();
            await new NpgsqlCommand("""
                CREATE SCHEMA drizzle;
                CREATE TABLE drizzle.__drizzle_migrations (id SERIAL PRIMARY KEY, hash text NOT NULL, created_at bigint);
                INSERT INTO drizzle.__drizzle_migrations (hash, created_at)
                VALUES ('a', 1793046596452), ('b', 1793046607639), ('c', 1793102256590);
                """, con).ExecuteNonQueryAsync();
        }

        var aplicadas = await new Migrador(Opcoes(banco.Conexao), NullLogger<Migrador>.Instance).AplicarAsync();

        Assert.DoesNotContain(aplicadas, n => string.CompareOrdinal(n, "0002_auth_tokens") <= 0);
        Assert.Equal(Migrador.Scripts().Count, await Escalar<long>(banco.Conexao, "SELECT count(*) FROM schema_migracoes"));
        Assert.Equal(3L, await Escalar<long>(banco.Conexao, "SELECT count(*) FROM drizzle.__drizzle_migrations"));
    }

    [Fact]
    public async Task Dados_base_criam_perfis_categorias_estagios_e_administrador()
    {
        Assert.Equal(Permissoes.Todas.Count, await Escalar<long>(BancoFixture.Url, """
            SELECT count(*) FROM perfil_permissoes pp JOIN perfis p ON p.id = pp.perfil_id WHERE p.nome = 'Administrador'
            """));
        Assert.Equal(PadroesIniciais.CategoriasDespesa.Count,
            await Escalar<long>(BancoFixture.Url, $"SELECT count(*) FROM despesa_categorias WHERE nome = ANY(ARRAY['{string.Join("','", PadroesIniciais.CategoriasDespesa)}'])"));
        Assert.Equal(PadroesIniciais.Estagios.Count * 2,
            await Escalar<long>(BancoFixture.Url, $"SELECT count(*) FROM funil_estagios WHERE nome = ANY(ARRAY['{string.Join("','", PadroesIniciais.Estagios.Select(e => e.Nome))}'])"));
        Assert.True(await Escalar<bool>(BancoFixture.Url, "SELECT EXISTS (SELECT 1 FROM usuarios WHERE email = 'admin@inovabens.com.br')"));
    }

    [Fact]
    public async Task Producao_recusa_criar_administrador_com_a_senha_padrao()
    {
        await using var banco = await BancoTemporario.CriarAsync();
        var op = InovabensOpcoes.De(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = banco.Conexao,
            ["TOKEN_ENC_KEY"] = new string('a', 64),
            ["JWT_REFRESH_SECRET"] = new string('b', 40),
        }).Build(), producao: true);
        await new Migrador(op, NullLogger<Migrador>.Instance).AplicarAsync();

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => new DadosIniciais(op, NullLogger<DadosIniciais>.Instance).GarantirAsync());
        Assert.Contains("SEED_ADMIN_PASSWORD", erro.Message);
        Assert.Equal(0L, await Escalar<long>(banco.Conexao, "SELECT count(*) FROM usuarios"));
    }

    private static InovabensOpcoes Opcoes(string url) => InovabensOpcoes.De(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DATABASE_URL"] = url }).Build(), producao: false);

    private static async Task<(long, long, long, long, long)> Contagens(string url) => (
        await Escalar<long>(url, "SELECT count(*) FROM permissoes"),
        await Escalar<long>(url, "SELECT count(*) FROM perfil_permissoes"),
        await Escalar<long>(url, "SELECT count(*) FROM despesa_categorias"),
        await Escalar<long>(url, "SELECT count(*) FROM funil_estagios"),
        await Escalar<long>(url, "SELECT count(*) FROM usuarios"));

    private static async Task<T> Escalar<T>(string url, string sql)
    {
        await using var con = new NpgsqlConnection(InovabensDb.NormalizarConexao(url));
        await con.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, con);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Banco vazio no mesmo servidor do banco de teste, apagado ao final.</summary>
    private sealed class BancoTemporario : IAsyncDisposable
    {
        private readonly string _admin;
        private readonly string _nome;
        public string Conexao { get; }

        private BancoTemporario(string admin, string nome, string conexao) => (_admin, _nome, Conexao) = (admin, nome, conexao);

        public static async Task<BancoTemporario> CriarAsync()
        {
            var admin = InovabensDb.NormalizarConexao(BancoFixture.Url);
            var nome = "node_" + Guid.NewGuid().ToString("N")[..10];
            await using (var con = new NpgsqlConnection(admin))
            {
                await con.OpenAsync();
                await new NpgsqlCommand($"CREATE DATABASE {nome}", con).ExecuteNonQueryAsync();
            }
            return new BancoTemporario(admin, nome, new NpgsqlConnectionStringBuilder(admin) { Database = nome, Pooling = false }.ConnectionString);
        }

        public async ValueTask DisposeAsync()
        {
            await using var con = new NpgsqlConnection(_admin);
            await con.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE IF EXISTS {_nome} WITH (FORCE)", con).ExecuteNonQueryAsync();
        }
    }
}
