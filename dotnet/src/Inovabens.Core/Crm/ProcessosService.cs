using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Crm;

/// <summary>Cartão de um imóvel num funil de processo (Administrativo, Projetos).</summary>
/// <param name="ProximoEm">Próxima atividade pendente da agenda ligada ao imóvel (UTC).</param>
public sealed record CartaoProcesso(
    Guid Id, Guid ImovelId, string Codigo, string Titulo, string? Cidade, Guid? ResponsavelId, string? ResponsavelNome,
    DateOnly? Prazo, string? Observacoes, int Ordem, DateTime NaFaseDesde, DateTime? ProximoEm, string? ProximoTitulo)
{
    public int DiasNaFase => Math.Max(0, DatasBR.Hoje().DayNumber - DateOnly.FromDateTime(DatasBR.DeUtc(NaFaseDesde)).DayNumber);

    public bool PrazoVencido => Prazo is { } p && p < DatasBR.Hoje();

    public SituacaoProximoPasso Situacao => LeadCartao.SituacaoDe(ProximoEm);
}

public sealed record ColunaProcesso(FunilEstagio Estagio, IReadOnlyList<CartaoProcesso> Cartoes);

/// <summary>Resumo de um processo do imóvel (ficha do imóvel e painel de acompanhamento).</summary>
public sealed record ProcessoResumo(Guid Id, Guid ImovelId, Funil Funil, string Estagio, string? Cor, TipoEstagio Tipo,
    string? Responsavel, DateOnly? Prazo, int DiasNaFase);

public sealed class ProcessoForm
{
    public Funil Funil { get; set; } = Funil.ADMINISTRATIVO;
    [Required(ErrorMessage = "Selecione o imóvel.")] public Guid? ImovelId { get; set; }
    /// <summary>Etapa inicial (só na criação); vazio = a primeira etapa do funil.</summary>
    public Guid? EstagioId { get; set; }
    public Guid? ResponsavelId { get; set; }
    public DateOnly? Prazo { get; set; }
    [StringLength(4000, ErrorMessage = "No máximo 4000 caracteres.")] public string? Observacoes { get; set; }
}

/// <summary>
/// Funis de processos do imóvel (Administrativo/Documentação e Projetos/Engenharia). Cada imóvel tem no máximo um
/// cartão em cada funil. Perfil com escopo próprio vê os processos de que é responsável e os dos seus imóveis.
/// No funil Projetos, mudar a etapa também atualiza a "etapa da estratégia" e o status do projeto no imóvel.
/// </summary>
public sealed class ProcessosService(IDbContextFactory<InovabensDb> fabrica, AuditService audit)
{
    private static IQueryable<Processo> Visiveis(InovabensDb db, UsuarioAtual u)
    {
        var q = db.Processos.Where(p => p.ExcluidoEm == null && p.Imovel!.ExcluidoEm == null);
        return u.EscopoProprio ? q.Where(p => p.ResponsavelId == u.Id || p.Imovel!.CorretorResponsavelId == u.Id) : q;
    }

    private static void ExigirFunil(Funil f)
    {
        if (!Funis.EhProcesso(f)) throw new RegraNegocioException("Funil", $"O funil {Funis.Nome(f)} é de leads, não de processos.");
    }

    public async Task<IReadOnlyList<ColunaProcesso>> QuadroAsync(UsuarioAtual u, Funil funil, Guid? responsavelId = null,
        Guid? imovelId = null, bool semResponsavel = false)
    {
        u.Exigir(P.ProcessosVer);
        ExigirFunil(funil);
        await using var db = await fabrica.CreateDbContextAsync();
        var estagios = await db.FunilEstagios.AsNoTracking().Where(e => e.Funil == funil && e.Ativo).OrderBy(e => e.Ordem).ToListAsync();
        var q = Visiveis(db, u).AsNoTracking().Where(p => p.Funil == funil);
        if (semResponsavel) q = q.Where(p => p.ResponsavelId == null);
        else if (responsavelId is { } r) q = q.Where(p => p.ResponsavelId == r);
        if (imovelId is { } im) q = q.Where(p => p.ImovelId == im);
        var linhas = await q.OrderBy(p => p.Ordem).ThenBy(p => p.Imovel!.Codigo)
            .Select(p => new
            {
                p.Id, p.EstagioId, p.ImovelId, p.Imovel!.Codigo, p.Imovel.Titulo, p.Imovel.Cidade, p.ResponsavelId,
                Responsavel = p.Responsavel != null ? p.Responsavel.Nome : null, p.Prazo, p.Observacoes, p.Ordem, p.CriadoEm,
                NaFase = db.ProcessoHistoricos.Where(h => h.ProcessoId == p.Id && h.EstagioNovoId == p.EstagioId).Max(h => (DateTime?)h.CriadoEm),
                Proximo = db.Compromissos.Where(c => c.ImovelId == p.ImovelId && c.Status == StatusCompromisso.PENDENTE && c.ExcluidoEm == null)
                    .OrderBy(c => c.InicioEm).Select(c => new { c.InicioEm, c.Titulo }).FirstOrDefault(),
            })
            .ToListAsync();
        return [.. estagios.Select(e => new ColunaProcesso(e, [.. linhas.Where(l => l.EstagioId == e.Id)
            .Select(l => new CartaoProcesso(l.Id, l.ImovelId, l.Codigo, l.Titulo, l.Cidade, l.ResponsavelId, l.Responsavel, l.Prazo,
                l.Observacoes, l.Ordem, l.NaFase ?? l.CriadoEm, l.Proximo?.InicioEm, l.Proximo?.Titulo))]))];
    }

    /// <summary>Processos (abertos e encerrados) dos imóveis informados; null = todos os visíveis.</summary>
    public async Task<IReadOnlyList<ProcessoResumo>> DosImoveisAsync(UsuarioAtual u, IReadOnlyCollection<Guid>? imoveis)
    {
        if (!u.Pode(P.ProcessosVer)) return [];
        await using var db = await fabrica.CreateDbContextAsync();
        var q = Visiveis(db, u).AsNoTracking();
        if (imoveis is not null) q = q.Where(p => imoveis.Contains(p.ImovelId));
        var linhas = await q.OrderBy(p => p.Funil)
            .Select(p => new
            {
                p.Id, p.ImovelId, p.Funil, Estagio = p.Estagio!.Nome, p.Estagio.Cor, p.Estagio.Tipo,
                Responsavel = p.Responsavel != null ? p.Responsavel.Nome : null, p.Prazo, p.CriadoEm,
                NaFase = db.ProcessoHistoricos.Where(h => h.ProcessoId == p.Id && h.EstagioNovoId == p.EstagioId).Max(h => (DateTime?)h.CriadoEm),
            })
            .ToListAsync();
        var hoje = DatasBR.Hoje().DayNumber;
        return [.. linhas.Select(l => new ProcessoResumo(l.Id, l.ImovelId, l.Funil, l.Estagio, l.Cor, l.Tipo, l.Responsavel, l.Prazo,
            Math.Max(0, hoje - DateOnly.FromDateTime(DatasBR.DeUtc(l.NaFase ?? l.CriadoEm)).DayNumber)))];
    }

    public async Task<Processo> ObterAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.ProcessosVer);
        await using var db = await fabrica.CreateDbContextAsync();
        return await Visiveis(db, u).AsNoTracking().SingleOrDefaultAsync(p => p.Id == id) ?? throw new NaoEncontradoException("Processo não encontrado.");
    }

    public async Task<Guid> CriarAsync(UsuarioAtual u, ProcessoForm form)
    {
        u.Exigir(P.ProcessosEditar);
        Validacao.Validar(form);
        ExigirFunil(form.Funil);
        await using var db = await fabrica.CreateDbContextAsync();
        var imovel = await Acesso.ImovelComAcesso(db, u, form.ImovelId!.Value);
        if (await db.Processos.AnyAsync(p => p.ImovelId == imovel.Id && p.Funil == form.Funil && p.ExcluidoEm == null))
            throw new RegraNegocioException(nameof(ProcessoForm.ImovelId), $"O imóvel {imovel.Codigo} já está no funil {Funis.Nome(form.Funil)}.");
        var estagio = form.EstagioId is { } eid
            ? await EstagioDoFunil(db, eid, form.Funil)
            : await db.FunilEstagios.Where(e => e.Funil == form.Funil && e.Ativo && e.Tipo == TipoEstagio.ABERTO).OrderBy(e => e.Ordem).FirstOrDefaultAsync()
              ?? throw new RegraNegocioException("EstagioId", $"O funil {Funis.Nome(form.Funil)} não tem etapas ativas.");
        var p = new Processo
        {
            Id = Guid.NewGuid(), Funil = form.Funil, ImovelId = imovel.Id, EstagioId = estagio.Id,
            ResponsavelId = Acesso.ResponsavelAoCriar(u, form.ResponsavelId), Prazo = form.Prazo,
            Observacoes = Texto.Limpo(form.Observacoes), CriadoPor = u.Id,
            Ordem = (await db.Processos.Where(x => x.EstagioId == estagio.Id).MinAsync(x => (int?)x.Ordem) ?? 1) - 1,
        };
        db.Processos.Add(p);
        db.ProcessoHistoricos.Add(new ProcessoHistorico { ProcessoId = p.Id, EstagioNovoId = estagio.Id, UsuarioId = u.Id });
        if (p.Funil == Funil.PROJETOS) AtualizarProjeto(imovel, estagio);
        audit.Registrar(db, u.Id, "processos", p.Id, "CRIAR", depois: p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    /// <summary>Responsável, prazo e observações (a etapa muda só por <see cref="MoverAsync"/>).</summary>
    public async Task AtualizarAsync(UsuarioAtual u, Guid id, ProcessoForm form)
    {
        u.Exigir(P.ProcessosEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var p = await Visiveis(db, u).SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Processo não encontrado.");
        var antes = p.Copia();
        if (Acesso.NovoResponsavel(u, form.ResponsavelId) is { } r) p.ResponsavelId = r;
        p.Prazo = form.Prazo;
        p.Observacoes = Texto.Limpo(form.Observacoes);
        p.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "processos", id, "ATUALIZAR", antes, p);
        await db.SaveChangesAsync();
    }

    /// <summary>Muda a etapa (com histórico) ou só a posição na coluna (<paramref name="ordem"/> = antes de qual cartão).</summary>
    public async Task MoverAsync(UsuarioAtual u, Guid id, Guid estagioId, int? ordem = null)
    {
        u.Exigir(P.ProcessosEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var p = await Visiveis(db, u).SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Processo não encontrado.");
        var novo = await EstagioDoFunil(db, estagioId, p.Funil);
        await using var tx = await db.Database.BeginTransactionAsync();
        var prox = (await db.Processos.Where(x => x.EstagioId == novo.Id).MaxAsync(x => (int?)x.Ordem) ?? -1) + 1;
        if (ordem is { } pos)
            await db.Processos.Where(x => x.EstagioId == novo.Id && x.Id != id && x.Ordem >= pos)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Ordem, x => x.Ordem + 1));
        var anterior = p.EstagioId;
        p.Ordem = ordem ?? (anterior == novo.Id ? p.Ordem : prox);
        if (anterior != novo.Id)
        {
            p.EstagioId = novo.Id;
            db.ProcessoHistoricos.Add(new ProcessoHistorico { ProcessoId = id, EstagioAnteriorId = anterior, EstagioNovoId = novo.Id, UsuarioId = u.Id });
            if (p.Funil == Funil.PROJETOS)
                AtualizarProjeto(await db.Imoveis.SingleAsync(i => i.Id == p.ImovelId), novo);
            audit.Registrar(db, u.Id, "processos", id, "MOVER", depois: new { estagio_anterior = anterior, estagio_novo = novo.Id });
        }
        p.AtualizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync();
        if (anterior != novo.Id) await PrazosService.ResolverAsync(db, processoId: id);
        await tx.CommitAsync();
    }

    public async Task ExcluirAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.ProcessosEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var p = await Visiveis(db, u).SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Processo não encontrado.");
        var antes = p.Copia();
        p.ExcluidoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "processos", id, "EXCLUIR", antes);
        await db.SaveChangesAsync();
    }

    /// <summary>Histórico de etapas do processo (mais recente primeiro).</summary>
    public async Task<IReadOnlyList<HistoricoEstagio>> HistoricoAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.ProcessosVer);
        await using var db = await fabrica.CreateDbContextAsync();
        if (!await Visiveis(db, u).AnyAsync(p => p.Id == id)) throw new NaoEncontradoException("Processo não encontrado.");
        return await db.ProcessoHistoricos.AsNoTracking().Where(h => h.ProcessoId == id).OrderByDescending(h => h.CriadoEm)
            .Select(h => new HistoricoEstagio(h.Id, h.CriadoEm,
                db.FunilEstagios.Where(e => e.Id == h.EstagioAnteriorId).Select(e => e.Nome).FirstOrDefault(),
                db.FunilEstagios.Where(e => e.Id == h.EstagioNovoId).Select(e => e.Nome).First(),
                db.Usuarios.Where(x => x.Id == h.UsuarioId).Select(x => x.Nome).FirstOrDefault()))
            .ToListAsync();
    }

    private static async Task<FunilEstagio> EstagioDoFunil(InovabensDb db, Guid estagioId, Funil funil)
    {
        var e = await db.FunilEstagios.SingleOrDefaultAsync(x => x.Id == estagioId);
        if (e is null || !e.Ativo) throw new RegraNegocioException("EstagioId", "Etapa inexistente ou inativa");
        if (e.Funil != funil) throw new RegraNegocioException("EstagioId", $"Esta etapa pertence ao funil {Funis.Nome(e.Funil)}");
        return e;
    }

    /// <summary>Funil Projetos: a etapa do cartão é a "etapa da estratégia" do imóvel (como no Notion).</summary>
    internal static void AtualizarProjeto(Imovel i, FunilEstagio e)
    {
        i.ProjetoEtapa = e.Nome.Length > 40 ? e.Nome[..40] : e.Nome;
        i.ProjetoStatus = e.Tipo switch
        {
            TipoEstagio.GANHO => "Concluído",
            TipoEstagio.PERDIDO => i.ProjetoStatus,
            _ when e.Ordem <= 1 => "Não iniciada",
            _ => "Em andamento",
        };
        i.AtualizadoEm = DateTime.UtcNow;
    }
}
