using Inovabens.Core.Agenda;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Microsoft.EntityFrameworkCore;

namespace Inovabens.Core.Crm;

/// <summary>
/// Prazo por etapa: lead ou processo do imóvel parado na etapa além de <see cref="FunilEstagio.PrazoDias"/> gera um alerta
/// "etapa parada" para o responsável (processo sem responsável: o corretor responsável do imóvel; lead na fila: ninguém,
/// a fila já avisa os corretores). O alerta é resolvido quando o cartão muda de etapa, sai do funil ou o prazo aumenta.
/// </summary>
public sealed class PrazosService(IDbContextFactory<InovabensDb> fabrica)
{
    private sealed record Atrasado(Guid? LeadId, Guid? ProcessoId, Guid UsuarioId, string Mensagem);

    public async Task<ResultadoVerificacao> VerificarAsync(CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var hoje = DatasBR.Hoje().DayNumber;
        int Dias(DateTime desde) => hoje - DateOnly.FromDateTime(DatasBR.DeUtc(desde)).DayNumber;

        var leads = await db.Leads.AsNoTracking()
            .Where(l => l.ExcluidoEm == null && l.ResponsavelId != null && l.Estagio!.Ativo && l.Estagio.Tipo == TipoEstagio.ABERTO && l.Estagio.PrazoDias != null)
            .Select(l => new
            {
                l.Id, Responsavel = l.ResponsavelId!.Value, Nome = l.Pessoa!.NomeRazao, Etapa = l.Estagio!.Nome, l.Estagio.Funil, Prazo = l.Estagio.PrazoDias!.Value,
                Desde = db.LeadHistoricoEstagios.Where(h => h.LeadId == l.Id && h.EstagioNovoId == l.EstagioId).Max(h => (DateTime?)h.CriadoEm) ?? l.CriadoEm,
            })
            .ToListAsync(ct);
        var processos = await db.Processos.AsNoTracking()
            .Where(p => p.ExcluidoEm == null && p.Imovel!.ExcluidoEm == null && p.Estagio!.Ativo && p.Estagio.Tipo == TipoEstagio.ABERTO && p.Estagio.PrazoDias != null
                        && (p.ResponsavelId != null || p.Imovel.CorretorResponsavelId != null))
            .Select(p => new
            {
                p.Id, Responsavel = p.ResponsavelId ?? p.Imovel!.CorretorResponsavelId!.Value, p.Imovel!.Codigo, Etapa = p.Estagio!.Nome, p.Funil,
                Prazo = p.Estagio.PrazoDias!.Value,
                Desde = db.ProcessoHistoricos.Where(h => h.ProcessoId == p.Id && h.EstagioNovoId == p.EstagioId).Max(h => (DateTime?)h.CriadoEm) ?? p.CriadoEm,
            })
            .ToListAsync(ct);

        var atrasados = leads.Where(l => Dias(l.Desde) > l.Prazo)
            .Select(l => new Atrasado(l.Id, null, l.Responsavel,
                $"O lead {l.Nome} está há {Dias(l.Desde)} dias na etapa \"{l.Etapa}\" do funil {Funis.Nome(l.Funil)} (prazo: {l.Prazo} dias)."))
            .Concat(processos.Where(p => Dias(p.Desde) > p.Prazo)
                .Select(p => new Atrasado(null, p.Id, p.Responsavel,
                    $"O imóvel {p.Codigo} está há {Dias(p.Desde)} dias na etapa \"{p.Etapa}\" do funil {Funis.Nome(p.Funil)} (prazo: {p.Prazo} dias).")))
            .ToList();

        var abertos = await db.Alertas.Where(a => a.Tipo == TipoAlerta.ETAPA_PARADA && a.ResolvidoEm == null).ToListAsync(ct);
        var chaves = atrasados.Select(a => (a.LeadId, a.ProcessoId, a.UsuarioId)).ToHashSet();
        var resolvidos = 0;
        foreach (var a in abertos.Where(a => !chaves.Contains((a.LeadId, a.ProcessoId, a.UsuarioId))))
        {
            a.ResolvidoEm = DateTime.UtcNow;
            resolvidos++;
        }
        var existentes = abertos.Where(a => a.ResolvidoEm == null).Select(a => (a.LeadId, a.ProcessoId, a.UsuarioId)).ToHashSet();
        var novos = atrasados.Where(a => !existentes.Contains((a.LeadId, a.ProcessoId, a.UsuarioId))).ToList();
        foreach (var n in novos)
            db.Alertas.Add(new Alerta
            {
                Id = Guid.NewGuid(), Tipo = TipoAlerta.ETAPA_PARADA, UsuarioId = n.UsuarioId, LeadId = n.LeadId, ProcessoId = n.ProcessoId, Mensagem = n.Mensagem,
            });
        await db.SaveChangesAsync(ct);
        return new ResultadoVerificacao(novos.Count, resolvidos);
    }

    /// <summary>O cartão mudou de etapa: encerra o alerta de etapa parada (quem chama está na transação).</summary>
    internal static Task ResolverAsync(InovabensDb db, Guid? leadId = null, Guid? processoId = null) =>
        db.Alertas.Where(a => a.Tipo == TipoAlerta.ETAPA_PARADA && a.ResolvidoEm == null
                              && ((leadId != null && a.LeadId == leadId) || (processoId != null && a.ProcessoId == processoId)))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ResolvidoEm, DateTime.UtcNow));
}
