using System.Security.Cryptography;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Relatorios;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Locacao;

public sealed record PortalLink(Guid Id, TipoPortal Tipo, Guid? PessoaId, string Nome, string Link, string? LinkWhatsapp, int Acessos, DateTime? UltimoAcesso);

public sealed record PortalBoleto(decimal Valor, DateOnly Vencimento, string? LinhaDigitavel, string? PixCopiaCola, string? UrlPdf);
public sealed record PortalRecibo(Guid Id, int Numero, DateOnly Data, decimal Total, string? Forma);
public sealed record PortalFatura(Guid Id, DateOnly Competencia, DateOnly Vencimento, StatusFaturaLocacao Status, decimal Total, decimal Saldo,
    bool Rescisao, PortalBoleto? Boleto, IReadOnlyList<PortalRecibo> Recibos);
public sealed record PortalContrato(Guid Id, string Codigo, string Imovel, string Endereco, StatusContratoLocacao Status, string? Inquilino,
    decimal Aluguel, short DiaVencimento);
public sealed record PortalRepasse(DateOnly Previsto, string Contrato, decimal Bruto, decimal Liquido, bool Pago);

/// <summary>O que o portal mostra. Inquilino: faturas, boletos, recibos e quitação. Proprietário: contratos, repasses, extratos e informe.</summary>
public sealed record PortalDados(TipoPortal Tipo, string Nome, IReadOnlyList<PortalContrato> Contratos, IReadOnlyList<PortalFatura> Faturas,
    IReadOnlyList<PortalRepasse> Repasses, IReadOnlyList<int> Anos, IReadOnlyList<DateOnly> Meses, string? LinkChamado);

/// <summary>
/// Locação, fase 7: portal do inquilino (por contrato) e do proprietário (por pessoa) por link secreto, sem senha.
/// Revogar o link corta o acesso; um novo link gera outro endereço. Os documentos são os mesmos da tela interna,
/// gerados em nome do administrador principal depois de conferir que pertencem a quem abriu o link.
/// </summary>
public sealed class PortalService(
    IDbContextFactory<InovabensDb> fabrica, AuditService audit, InovabensOpcoes op, LocacaoService locacao, LocacaoRelatoriosService relatorios)
{
    private static string NovoToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();

    private string Url(string caminho) => $"{op.WebUrl.TrimEnd('/')}/{caminho}";

    /* ------------------------------- tela interna ------------------------------- */

    /// <summary>Links ativos do contrato: o do inquilino e os dos locadores.</summary>
    public async Task<IReadOnlyList<PortalLink>> LinksAsync(UsuarioAtual u, Guid contratoId)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var partes = await Partes(db, u, contratoId);
        var locadores = partes.Where(p => p.Papel == PapelContratoLocacao.LOCADOR).Select(p => p.PessoaId).ToList();
        var acessos = await db.PortalAcessos.AsNoTracking()
            .Where(a => a.RevogadoEm == null && ((a.Tipo == TipoPortal.INQUILINO && a.ContratoId == contratoId)
                || (a.Tipo == TipoPortal.PROPRIETARIO && a.PessoaId != null && locadores.Contains(a.PessoaId.Value))))
            .OrderBy(a => a.CriadoEm).ToListAsync();
        return [.. acessos.Select(a => Link(a, partes))];
    }

    /// <summary>Link do portal (cria se não houver). Sem <paramref name="pessoaId"/>: o do inquilino; com: o do locador.</summary>
    public async Task<PortalLink> GerarLinkAsync(UsuarioAtual u, Guid contratoId, Guid? pessoaId = null)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var partes = await Partes(db, u, contratoId);
        PortalAcesso? a;
        if (pessoaId is { } pid)
        {
            if (!partes.Any(p => p.PessoaId == pid && p.Papel == PapelContratoLocacao.LOCADOR))
                throw new RegraNegocioException("_", "Esta pessoa não é locadora do contrato.");
            a = await db.PortalAcessos.FirstOrDefaultAsync(x => x.Tipo == TipoPortal.PROPRIETARIO && x.PessoaId == pid && x.RevogadoEm == null);
            a ??= new PortalAcesso { Tipo = TipoPortal.PROPRIETARIO, PessoaId = pid };
        }
        else
        {
            if (!partes.Any(p => p.Papel == PapelContratoLocacao.LOCATARIO)) throw new RegraNegocioException("_", "O contrato não tem locatário.");
            a = await db.PortalAcessos.FirstOrDefaultAsync(x => x.Tipo == TipoPortal.INQUILINO && x.ContratoId == contratoId && x.RevogadoEm == null);
            a ??= new PortalAcesso { Tipo = TipoPortal.INQUILINO, ContratoId = contratoId };
        }
        if (a.Token == "")
        {
            a.Token = NovoToken();
            a.CriadoPor = u.Id;
            db.PortalAcessos.Add(a);
            audit.Registrar(db, u.Id, "portal_acessos", a.Id, "CRIAR", depois: new { a.Tipo, a.ContratoId, a.PessoaId });
            await db.SaveChangesAsync();
        }
        return Link(a, partes);
    }

    public async Task RevogarAsync(UsuarioAtual u, Guid contratoId, Guid acessoId)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var partes = await Partes(db, u, contratoId);
        var locadores = partes.Where(p => p.Papel == PapelContratoLocacao.LOCADOR).Select(p => p.PessoaId).ToList();
        var a = await db.PortalAcessos.SingleOrDefaultAsync(x => x.Id == acessoId && x.RevogadoEm == null
                && (x.ContratoId == contratoId || (x.PessoaId != null && locadores.Contains(x.PessoaId.Value))))
            ?? throw new NaoEncontradoException("Link não encontrado.");
        a.RevogadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "portal_acessos", a.Id, "REVOGAR");
        await db.SaveChangesAsync();
    }

    private sealed record Parte(Guid PessoaId, PapelContratoLocacao Papel, string Nome, string? Fone);

    private static async Task<List<Parte>> Partes(InovabensDb db, UsuarioAtual u, Guid contratoId)
    {
        if (!await db.ContratosLocacao.AnyAsync(c => c.Id == contratoId && db.Imoveis.VisiveisPara(u).Any(i => i.Id == c.ImovelId)))
            throw new NaoEncontradoException("Contrato não encontrado.");
        return await db.ContratoLocacaoPartes.AsNoTracking().Where(p => p.ContratoId == contratoId)
            .Select(p => new Parte(p.PessoaId, p.Papel, p.Pessoa!.NomeRazao, p.Pessoa.Whatsapp ?? p.Pessoa.Telefone)).ToListAsync();
    }

    private PortalLink Link(PortalAcesso a, List<Parte> partes)
    {
        var p = a.Tipo == TipoPortal.INQUILINO
            ? partes.FirstOrDefault(x => x.Papel == PapelContratoLocacao.LOCATARIO)
            : partes.FirstOrDefault(x => x.PessoaId == a.PessoaId);
        var link = Url($"portal/{a.Token}");
        var texto = $"Olá{(p is null ? "" : ", " + ManutencaoService.PrimeiroNome(p.Nome))}! Aqui é da Inova Bens. " + (a.Tipo == TipoPortal.INQUILINO
            ? $"Pelo seu portal você vê as faturas do aluguel, paga por boleto ou Pix, baixa recibos e pede reparos: {link}"
            : $"Pelo seu portal você acompanha os repasses e baixa extratos e o informe de rendimentos: {link}");
        return new PortalLink(a.Id, a.Tipo, a.PessoaId, p?.Nome ?? "—", link, ManutencaoService.Whatsapp(p?.Fone, texto), a.Acessos, a.UltimoAcesso);
    }

    /* --------------------------------- público --------------------------------- */

    private static async Task<PortalAcesso?> Acesso(InovabensDb db, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;
        return await db.PortalAcessos.AsNoTracking().SingleOrDefaultAsync(a => a.Token == token && a.RevogadoEm == null, ct);
    }

    /// <summary>Dados do portal (null: link inválido ou revogado). Conta o acesso.</summary>
    public async Task<PortalDados?> PortalAsync(string token, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var a = await Acesso(db, token, ct);
        if (a is null) return null;
        await db.PortalAcessos.Where(x => x.Id == a.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Acessos, x => x.Acessos + 1).SetProperty(x => x.UltimoAcesso, DateTime.UtcNow), ct);
        return a.Tipo == TipoPortal.INQUILINO ? await Inquilino(db, a, ct) : await Proprietario(db, a, ct);
    }

    private async Task<PortalDados?> Inquilino(InovabensDb db, PortalAcesso a, CancellationToken ct)
    {
        var c = await db.ContratosLocacao.SingleOrDefaultAsync(x => x.Id == a.ContratoId, ct);
        if (c is null || c.Status is StatusContratoLocacao.RASCUNHO or StatusContratoLocacao.CANCELADO) return null;
        var nome = await db.ContratoLocacaoPartes.AsNoTracking().Where(p => p.ContratoId == c.Id && p.Papel == PapelContratoLocacao.LOCATARIO)
            .Select(p => p.Pessoa!.NomeRazao).FirstOrDefaultAsync(ct) ?? "";
        var contratos = await Contratos(db, [c.Id], ct);

        // últimos 2 anos e tudo o que estiver em aberto
        var desde = DatasBR.Hoje().AddYears(-2);
        var faturas = await db.FaturasLocacao.AsNoTracking().Include(f => f.Itens).Include(f => f.Pagamentos)
            .Where(f => f.ContratoId == c.Id && f.Status != StatusFaturaLocacao.CANCELADA && (f.Status == StatusFaturaLocacao.ABERTA || f.Competencia >= desde))
            .OrderByDescending(f => f.Competencia).ToListAsync(ct);
        var ids = faturas.Select(f => f.Id).ToList();
        var boletos = (await db.FaturaBoletos.AsNoTracking().Where(b => b.FaturaId != null && ids.Contains(b.FaturaId.Value) && b.CanceladoEm == null
                && b.Status != "PAID" && b.Status != "CANCELLED").OrderByDescending(b => b.CriadoEm).ToListAsync(ct))
            .GroupBy(b => b.FaturaId!.Value).ToDictionary(g => g.Key, g => g.First());
        var lista = faturas.Select(f =>
        {
            var pagos = f.Pagamentos.Where(p => p.EstornadoEm == null).ToList();
            var saldo = f.Status == StatusFaturaLocacao.ABERTA ? Math.Max(0, f.Itens.Sum(i => i.Valor) - pagos.Sum(p => p.Principal)) : 0;
            var bol = f.Status == StatusFaturaLocacao.ABERTA && boletos.TryGetValue(f.Id, out var b)
                ? new PortalBoleto(b.Valor, b.Vencimento, b.LinhaDigitavel, b.PixCopiaCola, b.UrlPdf) : null;
            return new PortalFatura(f.Id, f.Competencia, f.Vencimento, f.Status, f.Itens.Sum(i => i.Valor), saldo, f.Rescisao, bol,
                [.. pagos.OrderBy(p => p.ReciboNumero).Select(p => new PortalRecibo(p.Id, p.ReciboNumero, p.Data, p.Total, p.Forma))]);
        }).ToList();

        // quitação anual: anos encerrados com faturas
        var anoAtual = DatasBR.Hoje().Year;
        var anos = await db.FaturasLocacao.Where(f => f.ContratoId == c.Id && f.Status != StatusFaturaLocacao.CANCELADA && f.Competencia.Year < anoAtual)
            .Select(f => f.Competencia.Year).Distinct().OrderByDescending(y => y).ToListAsync(ct);

        // link de chamado de manutenção (o mesmo da tela do contrato; criado aqui se ainda não existir)
        if (c.TokenChamado is null && c.Status == StatusContratoLocacao.ATIVO)
        {
            c.TokenChamado = NovoToken();
            await db.SaveChangesAsync(ct);
        }
        var chamado = c.Status == StatusContratoLocacao.ATIVO ? Url($"chamado/{c.TokenChamado}") : null;
        return new PortalDados(TipoPortal.INQUILINO, nome, contratos, lista, [], anos, [], chamado);
    }

    private async Task<PortalDados?> Proprietario(InovabensDb db, PortalAcesso a, CancellationToken ct)
    {
        var pid = a.PessoaId!.Value;
        var nome = await db.Pessoas.AsNoTracking().Where(p => p.Id == pid).Select(p => p.NomeRazao).SingleOrDefaultAsync(ct);
        if (nome is null) return null;
        var ids = await ContratosDoLocador(db, pid).Select(c => c.Id).ToListAsync(ct);
        var contratos = await Contratos(db, ids, ct);
        var codigos = contratos.ToDictionary(c => c.Id, c => c.Codigo);
        var repasses = await db.RepassesLocacao.AsNoTracking().Where(r => r.PessoaId == pid && ids.Contains(r.ContratoId))
            .OrderByDescending(r => r.PrevistoPara).Take(24)
            .Select(r => new
            {
                r.PrevistoPara, r.ContratoId, r.Bruto, r.Liquido,
                Pago = r.PagoEm != null || db.LancamentosEmpresa.Any(l => l.Id == r.LancamentoEmpresaId && l.ExcluidoEm == null && l.DataPagamento != null),
            }).ToListAsync(ct);
        var hoje = DatasBR.Hoje();
        var anos = await db.FaturaLocacaoPagamentos.Where(p => p.EstornadoEm == null && db.FaturasLocacao.Any(f => f.Id == p.FaturaId && ids.Contains(f.ContratoId)))
            .Select(p => p.Data.Year).Distinct().OrderByDescending(y => y).ToListAsync(ct);
        var mes = new DateOnly(hoje.Year, hoje.Month, 1);
        return new PortalDados(TipoPortal.PROPRIETARIO, nome, contratos, [],
            [.. repasses.Select(r => new PortalRepasse(r.PrevistoPara, codigos.GetValueOrDefault(r.ContratoId, "—"), r.Bruto, r.Liquido, r.Pago))],
            anos, [.. Enumerable.Range(0, 12).Select(i => mes.AddMonths(-i))], null);
    }

    private static IQueryable<ContratoLocacao> ContratosDoLocador(InovabensDb db, Guid pessoaId) =>
        db.ContratosLocacao.Where(c => (c.Status == StatusContratoLocacao.ATIVO || c.Status == StatusContratoLocacao.ENCERRADO)
            && db.ContratoLocacaoPartes.Any(p => p.ContratoId == c.Id && p.PessoaId == pessoaId && p.Papel == PapelContratoLocacao.LOCADOR));

    private static async Task<List<PortalContrato>> Contratos(InovabensDb db, List<Guid> ids, CancellationToken ct)
    {
        var cs = await db.ContratosLocacao.AsNoTracking().Where(c => ids.Contains(c.Id)).OrderByDescending(c => c.Status == StatusContratoLocacao.ATIVO)
            .ThenByDescending(c => c.Inicio).ToListAsync(ct);
        var imovelIds = cs.Select(c => c.ImovelId).ToList();
        var imoveis = await db.Imoveis.AsNoTracking().Where(i => imovelIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var inquilinos = await db.ContratoLocacaoPartes.AsNoTracking().Where(p => ids.Contains(p.ContratoId) && p.Papel == PapelContratoLocacao.LOCATARIO)
            .Select(p => new { p.ContratoId, p.Pessoa!.NomeRazao }).ToListAsync(ct);
        return [.. cs.Select(c =>
        {
            var im = imoveis[c.ImovelId];
            return new PortalContrato(c.Id, c.Codigo, im.Codigo, ManutencaoService.Endereco(im), c.Status,
                inquilinos.FirstOrDefault(x => x.ContratoId == c.Id)?.NomeRazao, c.ValorAluguel, c.DiaVencimento);
        })];
    }

    /* ------------------------------- documentos ------------------------------- */

    /// <summary>Recibo de um recebimento: do contrato do inquilino ou de um contrato do proprietário.</summary>
    public async Task<Arquivo?> ReciboAsync(string token, Guid pagamentoId, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var a = await Acesso(db, token, ct);
        if (a is null) return null;
        var contratoId = await db.FaturaLocacaoPagamentos.Where(p => p.Id == pagamentoId && p.EstornadoEm == null)
            .Select(p => db.FaturasLocacao.Where(f => f.Id == p.FaturaId).Select(f => (Guid?)f.ContratoId).Single()).SingleOrDefaultAsync(ct);
        if (contratoId is null || !await Pertence(db, a, contratoId.Value, ct)) return null;
        return await Gerar(db, u => locacao.ReciboPdfAsync(u, pagamentoId), ct);
    }

    /// <summary>2ª via da fatura em aberto (só o inquilino).</summary>
    public async Task<Arquivo?> SegundaViaAsync(string token, Guid faturaId, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var a = await Acesso(db, token, ct);
        if (a is not { Tipo: TipoPortal.INQUILINO }
            || !await db.FaturasLocacao.AnyAsync(f => f.Id == faturaId && f.ContratoId == a.ContratoId && f.Status == StatusFaturaLocacao.ABERTA, ct))
            return null;
        return await Gerar(db, u => relatorios.SegundaViaPdfAsync(u, faturaId), ct);
    }

    /// <summary>Declaração anual de quitação (só o inquilino).</summary>
    public async Task<Arquivo?> QuitacaoAsync(string token, int ano, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var a = await Acesso(db, token, ct);
        if (a is not { Tipo: TipoPortal.INQUILINO } || ano < 2000 || ano >= DatasBR.Hoje().Year) return null;
        return await Gerar(db, u => relatorios.QuitacaoAnualPdfAsync(u, a.ContratoId!.Value, ano), ct);
    }

    /// <summary>Extrato mensal do proprietário.</summary>
    public async Task<Arquivo?> ExtratoAsync(string token, DateOnly mes, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var a = await Acesso(db, token, ct);
        if (a is not { Tipo: TipoPortal.PROPRIETARIO }) return null;
        return await Gerar(db, u => locacao.ExtratoProprietarioPdfAsync(u, a.PessoaId!.Value, new DateOnly(mes.Year, mes.Month, 1)), ct);
    }

    /// <summary>Informe de rendimentos (IR) do proprietário.</summary>
    public async Task<Arquivo?> InformeAsync(string token, int ano, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var a = await Acesso(db, token, ct);
        if (a is not { Tipo: TipoPortal.PROPRIETARIO } || ano < 2000 || ano > DatasBR.Hoje().Year) return null;
        return await Gerar(db, u => relatorios.InformeRendimentosPdfAsync(u, a.PessoaId!.Value, ano), ct);
    }

    private static async Task<bool> Pertence(InovabensDb db, PortalAcesso a, Guid contratoId, CancellationToken ct) =>
        a.Tipo == TipoPortal.INQUILINO ? a.ContratoId == contratoId : await ContratosDoLocador(db, a.PessoaId!.Value).AnyAsync(c => c.Id == contratoId, ct);

    /// <summary>Gera o documento como o administrador principal; regra de negócio (sem dados) vira "não encontrado".</summary>
    private async Task<Arquivo?> Gerar(InovabensDb db, Func<UsuarioAtual, Task<Arquivo>> gerar, CancellationToken ct)
    {
        var robo = await CobrancaBancariaService.RoboAsync(db, op, ct);
        if (robo is null) return null;
        try { return await gerar(robo); }
        catch (Exception e) when (e is RegraNegocioException or NaoEncontradoException) { return null; }
    }
}
