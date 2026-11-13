using Inovabens.Core.Agenda;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Painel;

public sealed record ContagemSituacao(SituacaoImovel Situacao, int Total);

public sealed record ContagemEstagio(string Estagio, string? Cor, int Total);

public sealed record ResumoPainel(
    IReadOnlyList<ContagemSituacao> ImoveisPorSituacao, IReadOnlyList<ContagemEstagio> LeadsPorEstagio,
    int AlertasAbertos, int? LancamentosNaoPagos, IReadOnlyList<CompromissoDetalhe> ProximosCompromissos,
    int? PropostasEmAnalise = null, int? LeadsNovosSemana = null);

/// <summary>Resumo da página inicial respeitando escopo e permissões (port de dashboard.module.ts).</summary>
public sealed class PainelService(IDbContextFactory<InovabensDb> fabrica, AgendaService agenda, AlertasService alertas)
{
    public async Task<ResumoPainel> ResumoAsync(UsuarioAtual u)
    {
        await using var db = await fabrica.CreateDbContextAsync();

        IReadOnlyList<ContagemSituacao> imoveis = [];
        if (u.Pode(P.ImoveisVer))
        {
            var q = db.Imoveis.Where(i => i.ExcluidoEm == null).VisiveisPara(u);
            imoveis = await q.GroupBy(i => i.Situacao).Select(g => new ContagemSituacao(g.Key, g.Count())).ToListAsync();
        }

        IReadOnlyList<ContagemEstagio> leads = [];
        int? propostas = null, novosSemana = null;
        if (u.Pode(P.CrmVer))
        {
            var ql = db.Leads.Where(l => l.ExcluidoEm == null).VisiveisPara(u);
            leads = await db.FunilEstagios.Where(e => e.Funil == Funil.VENDA && e.Ativo).OrderBy(e => e.Ordem)
                .Select(e => new ContagemEstagio(e.Nome, e.Cor, ql.Count(l => l.EstagioId == e.Id)))
                .ToListAsync();
            propostas = await db.Propostas.CountAsync(p => p.Status == StatusProposta.EM_ANALISE && ql.Any(l => l.Id == p.LeadId));
            var semana = DateTime.UtcNow.AddDays(-7);
            novosSemana = await ql.CountAsync(l => l.CriadoEm >= semana);
        }

        int? naoPagos = u.Pode(P.FinanceiroVer)
            ? await db.DespesaLancamentos.CountAsync(l => l.Despesa!.ExcluidoEm == null && l.Status == StatusLancamento.NAO_PAGO)
            : null;

        IReadOnlyList<CompromissoDetalhe> proximos = u.Pode(P.AgendaVer)
            ? [.. (await agenda.ListarAsync(u, new FiltroAgenda(De: DateTime.UtcNow, Status: StatusCompromisso.PENDENTE))).Take(5)]
            : [];

        return new ResumoPainel(imoveis, leads, await alertas.ContagemAsync(u), naoPagos, proximos, propostas, novosSemana);
    }
}
