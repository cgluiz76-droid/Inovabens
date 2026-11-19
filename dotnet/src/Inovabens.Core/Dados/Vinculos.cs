using System.Collections.Concurrent;
using System.Data.Common;
using Inovabens.Core.Comum;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Inovabens.Core.Dados;

/// <summary>
/// Integridade nas exclusões: antes de excluir um registro (imóvel, pessoa, lead…), percorre TODAS as chaves estrangeiras
/// que apontam para a tabela dele, lidas do próprio catálogo do Postgres. Assim uma tabela nova com vínculo já entra na
/// checagem sem mudar código, e a exclusão lógica (excluido_em) não deixa registros "órfãos" apontando para algo apagado.
/// Não contam como vínculo:
///  - linhas que também estão excluídas (coluna excluido_em preenchida);
///  - linhas de ligação cujo outro lado já foi excluído (ex.: interesse de um lead excluído);
///  - tabelas de composição informadas pelo chamador (ex.: fotos e características do próprio imóvel), que vão junto;
///  - registros técnicos (auditoria, alertas, log do WhatsApp).
/// </summary>
public static class Vinculos
{
    /// <summary>Registros técnicos: nunca impedem a exclusão.</summary>
    private static readonly HashSet<string> Tecnicas = ["audit_log", "alertas", "whatsapp_mensagens"];

    /// <summary>Nomes amigáveis para a mensagem. Chave "tabela.coluna" vale antes de "tabela".</summary>
    private static readonly Dictionary<string, string> Rotulos = new()
    {
        ["imovel_proprietarios"] = "Proprietários",
        ["imovel_envolvidos"] = "Envolvidos",
        ["receitas"] = "Receitas",
        ["receitas.contraparte_id"] = "Receitas (contraparte)",
        ["despesas"] = "Despesas",
        ["despesa_lancamentos"] = "Despesas (lançamentos)",
        ["lancamentos_financeiros"] = "Receitas/despesas (lançamentos financeiros)",
        ["financeiro_comprovantes"] = "Comprovantes financeiros",
        ["aquisicoes"] = "Aquisição",
        ["fechamentos_investimento"] = "Investimento",
        ["fechamentos_investimento.comprador_id"] = "Investimento (comprador)",
        ["compromissos"] = "Agenda (compromissos)",
        ["compromisso_participantes"] = "Agenda (participante de compromisso)",
        ["memorandos"] = "Memorandos",
        ["regularizacoes"] = "Regularizações",
        ["regularizacoes.profissional_id"] = "Regularizações (profissional)",
        ["leads"] = "Leads do CRM",
        ["leads.imovel_sugerido_id"] = "Leads do CRM (imóvel sugerido)",
        ["lead_imoveis_interesse"] = "Leads do CRM (imóvel de interesse)",
        ["propostas"] = "Propostas",
        ["proposta_itens.imovel_permuta_id"] = "Propostas (imóvel dado em permuta)",
        ["processos"] = "Processos",
        ["posts_redes"] = "Posts de redes sociais",
    };

    /// <summary>Nomes das partes que vão junto na exclusão (composição do próprio registro).</summary>
    private static readonly Dictionary<string, string> RotulosComposicao = new()
    {
        ["imovel_midias"] = "Fotos, vídeos e documentos do imóvel",
        ["imovel_caracteristicas"] = "Características",
        ["pessoa_papeis"] = "Perfis da pessoa",
        ["lead_historico_estagio"] = "Histórico de etapas do funil",
        ["lead_interacoes"] = "Ações da linha do tempo",
        ["lead_imoveis_interesse"] = "Imóveis de interesse",
    };

    private sealed record Chave(string Tabela, string Coluna, bool Logica, IReadOnlyList<(string Coluna, string Alvo)> OutrosLogicos);

    /// <summary>Catálogo por tabela-alvo; o esquema só muda num deploy (a app reinicia), então vale guardar.</summary>
    private static readonly ConcurrentDictionary<string, IReadOnlyList<Chave>> Cache = new();

    /// <summary>
    /// Lança <see cref="RegraNegocioException"/> com "Não é possível excluir {nome} - Remova o vínculo com A / B / C"
    /// quando houver qualquer vínculo ativo.
    /// </summary>
    public static async Task ExigirSemVinculosAsync(InovabensDb db, string tabela, Guid id, string nome, params string[] composicao)
    {
        var lista = await ListarAsync(db, tabela, id, composicao);
        if (lista.Count > 0)
            throw new RegraNegocioException("_", $"Não é possível excluir {nome} - Remova o vínculo com {string.Join(" / ", lista)}.");
    }

    /// <summary>
    /// Prévia da exclusão para a tela de confirmação: o que vai junto (o próprio registro e as partes dele, com
    /// quantidade) e o que impede a exclusão (vínculos ativos). Com bloqueios a tela nem oferece o botão de excluir.
    /// </summary>
    public static async Task<PreviaExclusao> PreviaAsync(InovabensDb db, string tabela, Guid id, string descricao, params string[] composicao)
    {
        var bloqueios = await ListarAsync(db, tabela, id, composicao);
        var itens = new List<string> { descricao };
        var con = db.Database.GetDbConnection();
        var abriu = con.State != System.Data.ConnectionState.Open;
        if (abriu) await con.OpenAsync();
        try
        {
            foreach (var k in Cache[tabela].Where(k => composicao.Contains(k.Tabela)))
            {
                await using var cmd = con.CreateCommand();
                cmd.CommandText = $"""SELECT count(*) FROM "{k.Tabela}" WHERE "{k.Coluna}" = @id""";
                cmd.Parameters.Add(new NpgsqlParameter("id", id));
                var n = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                if (n > 0) itens.Add($"{RotulosComposicao.GetValueOrDefault(k.Tabela, k.Tabela)} ({n})");
            }
        }
        finally
        {
            if (abriu) await con.CloseAsync();
        }
        return new PreviaExclusao(itens, bloqueios);
    }

    /// <summary>
    /// Regra do sistema para exclusões: a senha do próprio usuário confirma que a exclusão não é acidental, e
    /// nenhum vínculo ativo pode sobrar. Vale para quem chama o serviço, não só para a tela.
    /// </summary>
    public static async Task ConfirmarExclusaoAsync(InovabensDb db, UsuarioAtual u, string? senha, string tabela, Guid id, string nome, params string[] composicao)
    {
        var hash = await db.Usuarios.Where(x => x.Id == u.Id).Select(x => x.SenhaHash).SingleOrDefaultAsync();
        if (string.IsNullOrEmpty(senha) || hash is null || !Servicos.Senhas.Confere(hash, senha))
            throw new RegraNegocioException("senha", "Senha incorreta. Digite a sua senha de login para confirmar a exclusão.");
        await ExigirSemVinculosAsync(db, tabela, id, nome, composicao);
    }

    /// <summary>Vínculos ativos do registro, já com nome amigável e quantidade (ex.: "Proprietários (2)").</summary>
    public static async Task<IReadOnlyList<string>> ListarAsync(InovabensDb db, string tabela, Guid id, params string[] composicao)
    {
        var con = db.Database.GetDbConnection();
        var abriu = con.State != System.Data.ConnectionState.Open;
        if (abriu) await con.OpenAsync();
        try
        {
            var chaves = Cache.TryGetValue(tabela, out var c) ? c : Cache[tabela] = await LerCatalogo(con, tabela);
            var achados = new Dictionary<string, long>();
            foreach (var k in chaves)
            {
                if (Tecnicas.Contains(k.Tabela) || composicao.Contains(k.Tabela)) continue;
                var n = await Contar(con, k, id);
                if (n == 0) continue;
                var rotulo = Rotulos.GetValueOrDefault($"{k.Tabela}.{k.Coluna}") ?? Rotulos.GetValueOrDefault(k.Tabela)
                    ?? char.ToUpperInvariant(k.Tabela[0]) + k.Tabela[1..].Replace('_', ' ');
                achados[rotulo] = achados.GetValueOrDefault(rotulo) + n;
            }
            return achados.Select(a => $"{a.Key} ({a.Value})").ToList();
        }
        finally
        {
            if (abriu) await con.CloseAsync();
        }
    }

    private static async Task<long> Contar(DbConnection con, Chave k, Guid id)
    {
        // identificadores vêm do catálogo do Postgres (não do usuário); o id vai como parâmetro
        var sql = $"""SELECT count(*) FROM "{k.Tabela}" t WHERE t."{k.Coluna}" = @id""";
        if (k.Logica) sql += """ AND t."excluido_em" IS NULL""";
        else
            // tabela de ligação: não conta se o outro lado já foi excluído (ex.: proprietário de um imóvel excluído)
            foreach (var (col, alvo) in k.OutrosLogicos)
                sql += $"""
                     AND (t."{col}" IS NULL OR EXISTS (SELECT 1 FROM "{alvo}" x WHERE x."id" = t."{col}" AND x."excluido_em" IS NULL))
                    """;
        await using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new NpgsqlParameter("id", id));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<IReadOnlyList<Chave>> LerCatalogo(DbConnection con, string tabela)
    {
        // todas as FKs de uma coluna só: (tabela que aponta, coluna, alvo, se a tabela que aponta tem excluido_em, se o alvo tem)
        const string sql = """
            SELECT r.relname, a.attname, alvo.relname,
                   EXISTS (SELECT 1 FROM pg_attribute x WHERE x.attrelid = c.conrelid AND x.attname = 'excluido_em' AND NOT x.attisdropped),
                   EXISTS (SELECT 1 FROM pg_attribute x WHERE x.attrelid = c.confrelid AND x.attname = 'excluido_em' AND NOT x.attisdropped)
            FROM pg_constraint c
            JOIN pg_class r ON r.oid = c.conrelid
            JOIN pg_class alvo ON alvo.oid = c.confrelid
            JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = c.conkey[1]
            JOIN pg_namespace n ON n.oid = r.relnamespace AND n.nspname = current_schema()
            WHERE c.contype = 'f' AND array_length(c.conkey, 1) = 1
            ORDER BY r.relname, a.attname
            """;
        var fks = new List<(string Tabela, string Coluna, string Alvo, bool Logica, bool AlvoLogico)>();
        await using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = sql;
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                fks.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetBoolean(3), rd.GetBoolean(4)));
        }
        return fks.Where(f => f.Alvo == tabela && f.Tabela != tabela)
            .Select(f => new Chave(f.Tabela, f.Coluna, f.Logica,
                fks.Where(o => o.Tabela == f.Tabela && o.Coluna != f.Coluna && o.AlvoLogico).Select(o => (o.Coluna, o.Alvo)).ToList()))
            .ToList();
    }
}

/// <summary>O que a exclusão leva junto (primeiro item = o próprio registro) e o que a impede.</summary>
public sealed record PreviaExclusao(IReadOnlyList<string> VaiJunto, IReadOnlyList<string> Bloqueios)
{
    public bool Bloqueada => Bloqueios.Count > 0;
}
