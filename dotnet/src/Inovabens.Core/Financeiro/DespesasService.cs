using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Financeiro;

/// <summary>Categorias, despesas recorrentes (OPEX) e checklist mensal. Port de despesas.service.ts.</summary>
public sealed class DespesasService(IDbContextFactory<InovabensDb> fabrica, AuditService audit)
{
    /* ---------------------------- Categorias ---------------------------- */

    public async Task<IReadOnlyList<DespesaCategoria>> CategoriasAsync(bool incluirInativas = false)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        return await db.DespesaCategorias.AsNoTracking()
            .Where(c => incluirInativas || c.Ativo)
            .OrderBy(c => c.Nome)
            .ToListAsync();
    }

    public async Task<DespesaCategoria> CriarCategoriaAsync(UsuarioAtual u, CategoriaForm form)
    {
        u.Exigir(P.AdminConfig);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = new DespesaCategoria { Nome = form.Nome.Trim(), Ativo = form.Ativo };
        db.DespesaCategorias.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    public async Task AtualizarCategoriaAsync(UsuarioAtual u, Guid id, CategoriaForm form)
    {
        u.Exigir(P.AdminConfig);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await db.DespesaCategorias.SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Categoria não encontrada.");
        c.Nome = form.Nome.Trim();
        c.Ativo = form.Ativo;
        await db.SaveChangesAsync();
    }

    /* ----------------------------- Despesas ----------------------------- */

    public async Task<IReadOnlyList<DespesaDetalhe>> ListarAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.FinanceiroVer);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        return await db.Despesas.AsNoTracking()
            .Where(d => d.ImovelId == imovelId && d.ExcluidoEm == null)
            .OrderBy(d => d.Categoria!.Nome).ThenBy(d => d.Descricao)
            .Select(d => new DespesaDetalhe(d, d.Categoria!.Nome))
            .ToListAsync();
    }

    public async Task<Guid> CriarAsync(UsuarioAtual u, Guid imovelId, DespesaForm form)
    {
        u.Exigir(P.FinanceiroEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var d = new Despesa { ImovelId = imovelId };
        form.AplicarEm(d);
        db.Despesas.Add(d);
        audit.Registrar(db, u.Id, "despesas", d.Id, "CRIAR", depois: d);
        await db.SaveChangesAsync();
        return d.Id;
    }

    public async Task AtualizarAsync(UsuarioAtual u, Guid id, DespesaForm form)
    {
        u.Exigir(P.FinanceiroEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var d = await DespesaComAcesso(db, u, id);
        var antes = d.Copia();
        form.AplicarEm(d);
        audit.Registrar(db, u.Id, "despesas", id, "ATUALIZAR", antes, d);
        await db.SaveChangesAsync();
    }

    /// <summary>Exclusão lógica; a despesa também deixa de gerar lançamentos.</summary>
    public async Task ExcluirAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.FinanceiroEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var d = await DespesaComAcesso(db, u, id);
        audit.Registrar(db, u.Id, "despesas", id, "EXCLUIR", antes: d.Copia());
        d.ExcluidoEm = DateTime.UtcNow;
        d.Ativa = false;
        await db.SaveChangesAsync();
    }

    private static async Task<Despesa> DespesaComAcesso(InovabensDb db, UsuarioAtual u, Guid id)
    {
        var d = await db.Despesas.SingleOrDefaultAsync(x => x.Id == id && x.ExcluidoEm == null)
            ?? throw new NaoEncontradoException("Despesa não encontrada.");
        await Acesso.ImovelComAcesso(db, u, d.ImovelId, rastrear: false);
        return d;
    }

    /* ------------------- Competências (checklist mensal) ------------------- */

    /// <summary>
    /// Gera (idempotente) os lançamentos do mês para as despesas ativas: MENSAL todo mês; ANUAL no mês de
    /// <c>inicio_em</c>; UNICA no mês de <c>inicio_em</c>. Vencimento no dia previsto (limitado ao último dia
    /// do mês). Status nasce em branco ("a marcar"). Devolve quantos lançamentos foram criados.
    /// </summary>
    public async Task<int> GerarCompetenciaAsync(DateOnly mes, Guid? imovelId = null)
    {
        var competencia = new DateOnly(mes.Year, mes.Month, 1);
        await using var db = await fabrica.CreateDbContextAsync();
        return await db.Database.ExecuteSqlRawAsync("""
            insert into despesa_lancamentos (despesa_id, competencia, vencimento, valor)
            select d.id, @mes,
                   make_date(extract(year from @mes)::int, extract(month from @mes)::int,
                     least(coalesce(d.dia_vencimento, extract(day from d.inicio_em)::int),
                           extract(day from (date_trunc('month', @mes) + interval '1 month' - interval '1 day'))::int)),
                   d.valor_previsto
              from despesas d
             where d.ativa and d.excluido_em is null
               and (@imovel is null or d.imovel_id = @imovel)
               and d.inicio_em <= (date_trunc('month', @mes) + interval '1 month' - interval '1 day')::date
               and (d.fim_em is null or d.fim_em >= @mes)
               and (d.recorrencia = 'MENSAL'
                    or (d.recorrencia = 'ANUAL' and extract(month from d.inicio_em) = extract(month from @mes))
                    or (d.recorrencia = 'UNICA' and date_trunc('month', d.inicio_em) = date_trunc('month', @mes)))
            on conflict (despesa_id, competencia) do nothing
            """,
            new NpgsqlParameter("mes", NpgsqlDbType.Date) { Value = competencia },
            new NpgsqlParameter("imovel", NpgsqlDbType.Uuid) { Value = (object?)imovelId ?? DBNull.Value });
    }

    public async Task<Checklist> ChecklistAsync(UsuarioAtual u, Guid imovelId, DateOnly mes)
    {
        u.Exigir(P.FinanceiroVer);
        var competencia = new DateOnly(mes.Year, mes.Month, 1);
        await using (var db0 = await fabrica.CreateDbContextAsync())
            await Acesso.ImovelComAcesso(db0, u, imovelId, rastrear: false);
        await GerarCompetenciaAsync(competencia, imovelId);

        await using var db = await fabrica.CreateDbContextAsync();
        var itens = await db.DespesaLancamentos.AsNoTracking()
            .Where(l => l.Despesa!.ImovelId == imovelId && l.Despesa.ExcluidoEm == null && l.Competencia == competencia)
            .OrderBy(l => l.Despesa!.Categoria!.Nome).ThenBy(l => l.Despesa!.Descricao)
            .Select(l => new ChecklistItem(l.DespesaId, l.Id, l.Despesa!.Descricao, l.Despesa.Categoria!.Nome, l.Despesa.Recorrencia,
                l.Vencimento, l.Valor, l.Status, l.DataPagamento, l.Justificativa))
            .ToListAsync();
        int Conta(StatusLancamento? s) => itens.Count(i => i.Status == s);
        return new Checklist(competencia,
            new ChecklistResumo(Conta(StatusLancamento.PAGO), Conta(StatusLancamento.NAO_PAGO), Conta(StatusLancamento.NAO_SE_APLICA), Conta(null)),
            itens);
    }

    public async Task<IReadOnlyList<LancamentoHistorico>> HistoricoAsync(UsuarioAtual u, Guid imovelId, DateOnly? de = null, DateOnly? ate = null)
    {
        u.Exigir(P.FinanceiroVer);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var q = db.DespesaLancamentos.AsNoTracking().Where(l => l.Despesa!.ImovelId == imovelId && l.Despesa.ExcluidoEm == null);
        if (de is { } d1) { var c = new DateOnly(d1.Year, d1.Month, 1); q = q.Where(l => l.Competencia >= c); }
        if (ate is { } d2) { var c = new DateOnly(d2.Year, d2.Month, 1); q = q.Where(l => l.Competencia <= c); }
        return await q.OrderByDescending(l => l.Competencia).ThenBy(l => l.Despesa!.Categoria!.Nome)
            .Select(l => new LancamentoHistorico(l, l.Despesa!.Descricao, l.Despesa.Categoria!.Nome))
            .ToListAsync();
    }

    /// <summary>
    /// Marca o lançamento. REGRA DE NEGÓCIO: Não Pago exige justificativa (validada aqui e pela
    /// constraint ck_justificativa_nao_pago no banco). Pago/Não se aplica resolvem o alerta da despesa.
    /// </summary>
    public async Task AtualizarStatusAsync(UsuarioAtual u, Guid lancamentoId, StatusLancamentoEntrada e)
    {
        u.Exigir(P.FinanceiroEditar);
        Validacao.Validar(e);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await db.DespesaLancamentos.Include(x => x.Despesa).SingleOrDefaultAsync(x => x.Id == lancamentoId)
            ?? throw new NaoEncontradoException("Lançamento não encontrado.");
        await Acesso.ImovelComAcesso(db, u, l.Despesa!.ImovelId, rastrear: false);

        var antes = l.Copia();
        e.AplicarEm(l, u.Id, DatasBR.Hoje());

        await using var tx = await db.Database.BeginTransactionAsync();
        if (e.ResolveAlerta)
            await db.Alertas.Where(a => a.LancamentoId == lancamentoId && a.ResolvidoEm == null)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ResolvidoEm, DateTime.UtcNow));
        audit.Registrar(db, u.Id, "despesa_lancamentos", lancamentoId, "ATUALIZAR", antes, l);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}
