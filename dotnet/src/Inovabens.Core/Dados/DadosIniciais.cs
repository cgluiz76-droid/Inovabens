using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Inovabens.Core.Dados;

/// <summary>
/// Dados-base idempotentes (port de apps/api/src/db/seed.ts): permissões, perfis padrão (só ACRESCENTA
/// permissões, nunca remove as customizações do administrador), categorias de despesa, estágios dos dois
/// funis e o administrador inicial, criado somente se ainda não houver nenhum usuário.
/// </summary>
public sealed class DadosIniciais(InovabensOpcoes op, ILogger<DadosIniciais> log)
{
    public const string SenhaPadrao = "Trocar@123456";

    public async Task GarantirAsync(CancellationToken ct = default)
    {
        await using var con = new NpgsqlConnection(InovabensDb.NormalizarConexao(op.DatabaseUrl));
        await con.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct);

        foreach (var codigo in Permissoes.Todas)
            await Exec(con, tx, "INSERT INTO permissoes (codigo, descricao) VALUES (@c, @c) ON CONFLICT DO NOTHING", ct, ("c", codigo));

        foreach (var p in PadroesIniciais.Perfis)
        {
            await Exec(con, tx, "INSERT INTO perfis (nome, descricao, escopo_proprio) VALUES (@n, @d, @e) ON CONFLICT DO NOTHING", ct,
                ("n", p.Nome), ("d", p.Descricao), ("e", p.EscopoProprio));
            await Exec(con, tx, """
                INSERT INTO perfil_permissoes (perfil_id, permissao_id)
                SELECT pf.id, pm.id FROM perfis pf JOIN permissoes pm ON pm.codigo = ANY(@cs) WHERE pf.nome = @n
                ON CONFLICT DO NOTHING
                """, ct, ("n", p.Nome), ("cs", p.Permissoes.ToArray()));
        }

        foreach (var nome in PadroesIniciais.CategoriasDespesa)
            await Exec(con, tx, "INSERT INTO despesa_categorias (nome) VALUES (@n) ON CONFLICT DO NOTHING", ct, ("n", nome));

        foreach (var funil in Enum.GetValues<Funil>())
            for (var i = 0; i < PadroesIniciais.Estagios.Count; i++)
            {
                var e = PadroesIniciais.Estagios[i];
                await Exec(con, tx, """
                    INSERT INTO funil_estagios (funil, nome, tipo, cor, ordem)
                    VALUES (@f::funil, @n, @t::tipo_estagio, @c, @o) ON CONFLICT DO NOTHING
                    """, ct, ("f", funil.ToString()), ("n", e.Nome), ("t", e.Tipo.ToString()), ("c", e.Cor), ("o", i + 1));
            }

        await CriarAdministrador(con, tx, ct);
        await tx.CommitAsync(ct);
    }

    private async Task CriarAdministrador(NpgsqlConnection con, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using (var c = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM usuarios)", con, tx))
            if (await c.ExecuteScalarAsync(ct) is true) return;

        if (op.Producao && op.SeedAdminSenha == SenhaPadrao)
            throw new InvalidOperationException("Defina SEED_ADMIN_PASSWORD (não use a senha padrão em produção).");

        await Exec(con, tx, """
            INSERT INTO usuarios (nome, email, senha_hash, perfil_id)
            SELECT 'Administrador', @e, @h, id FROM perfis WHERE nome = 'Administrador'
            """, ct, ("e", op.SeedAdminEmail.ToLowerInvariant()), ("h", Senhas.Hash(op.SeedAdminSenha)));
        log.LogInformation("Administrador inicial criado: {Email}", op.SeedAdminEmail);
    }

    private static async Task Exec(NpgsqlConnection con, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Nome, object Valor)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, con, tx);
        foreach (var (nome, valor) in ps) cmd.Parameters.AddWithValue(nome, valor);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
