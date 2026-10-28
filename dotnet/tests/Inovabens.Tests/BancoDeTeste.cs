using Inovabens.Core;
using Inovabens.Core.Dados;
using Inovabens.Core.Servicos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Inovabens.Tests;

/// <summary>Os testes de integração compartilham o mesmo banco: rodam em sequência, nunca em paralelo.</summary>
[CollectionDefinition(Nome, DisableParallelization = true)]
public sealed class BancoDeTeste : ICollectionFixture<BancoFixture>
{
    public const string Nome = "Banco de teste";
}

/// <summary>
/// Banco dos testes de integração. Por padrão sobe um PostgreSQL 16 descartável no Docker (Testcontainers);
/// com INOVABENS_TEST_DB definida, usa esse banco. Nos dois casos aplica as migrações e os dados-base pelo
/// próprio .NET e cria os usuários de teste. Sem Docker e sem INOVABENS_TEST_DB os testes FALHAM (antes eles
/// passavam sem executar nada); para rodar só os de unidade: dotnet test --filter "Categoria!=Integracao&Categoria!=E2E".
/// </summary>
public sealed class BancoFixture : IAsyncLifetime
{
    public const string Senha = "Trocar@123456";

    /// <summary>URL do banco em uso; definida antes do primeiro teste da coleção.</summary>
    public static string Url { get; private set; } = "";

    private PostgreSqlContainer? _container;

    public async Task InitializeAsync()
    {
        var url = Environment.GetEnvironmentVariable("INOVABENS_TEST_DB");
        if (string.IsNullOrWhiteSpace(url))
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("inovabens").WithUsername("inovabens").WithPassword("inovabens")
                .Build();
            try { await _container.StartAsync(); }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    "Testes de integração precisam do Docker em execução (ou de INOVABENS_TEST_DB apontando para um banco de TESTE). " +
                    "Para rodar só os testes de unidade: dotnet test --filter \"Categoria!=Integracao&Categoria!=E2E\".", e);
            }
            url = _container.GetConnectionString();
        }
        Url = url;

        var op = InovabensOpcoes.De(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DATABASE_URL"] = url }).Build(), producao: false);
        await new Migrador(op, NullLogger<Migrador>.Instance).AplicarAsync();
        await new DadosIniciais(op, NullLogger<DadosIniciais>.Instance).GarantirAsync();
        await CriarCorretorDeTeste(InovabensDb.NormalizarConexao(url));
    }

    /// <summary>Corretora com escopo próprio; o perfil Corretor ganha financeiro:ver/editar, como os testes esperam.</summary>
    private static async Task CriarCorretorDeTeste(string conexao)
    {
        await using var con = new NpgsqlConnection(conexao);
        await con.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO perfil_permissoes (perfil_id, permissao_id)
            SELECT pf.id, pm.id FROM perfis pf JOIN permissoes pm ON pm.codigo IN ('financeiro:ver', 'financeiro:editar')
            WHERE pf.nome = 'Corretor'
            ON CONFLICT DO NOTHING;
            INSERT INTO usuarios (nome, email, senha_hash, perfil_id)
            SELECT 'Carla Corretora', 'carla@inovabens.com.br', @h, id FROM perfis WHERE nome = 'Corretor'
              AND NOT EXISTS (SELECT 1 FROM usuarios WHERE lower(email) = 'carla@inovabens.com.br');
            """, con);
        cmd.Parameters.AddWithValue("h", Senhas.Hash(Senha));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}
