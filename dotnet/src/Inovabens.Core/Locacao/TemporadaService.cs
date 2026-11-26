using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Financeiro;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Relatorios;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Locacao;

/// <summary>
/// Locação, fase 8: temporada por diária (Lei 8.245, arts. 48 a 50). Reservas com calendário por imóvel, preço pela
/// diária (fim de semana à parte), limpeza e caução; check-in/out; recebimentos no financeiro; acerto ao fechar a
/// reserva (comissão, repasse ao proprietário, limpeza e devolução da caução). Calendário trocado com Airbnb/Booking
/// por iCal e pedido de reserva pelo site.
/// Administração de terceiros: o que o hóspede paga entra como dinheiro de terceiros; no fechamento a comissão passa
/// a receita e o repasse, a limpeza e a devolução da caução ficam a pagar. Imóvel próprio: receita no financeiro do imóvel.
/// </summary>
public sealed class TemporadaService(
    IDbContextFactory<InovabensDb> fabrica, AuditService audit, InovabensOpcoes op, ILogger<TemporadaService> log)
{
    public const string CategoriaHospedagem = "Hospedagem de temporada (terceiros)";
    public const string CategoriaComissao = "Comissão de temporada";
    public const string CategoriaComissaoRetida = "Comissão de temporada retida";
    public const string CategoriaRepasse = "Repasse de temporada ao proprietário";
    public const string CategoriaLimpeza = "Limpeza de temporada";
    public const string CategoriaCaucao = "Devolução de caução de temporada";
    public const short DiasRepasse = 5;

    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private static string NovoToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
    private string Url(string caminho) => $"{op.WebUrl.TrimEnd('/')}/{caminho}";

    /* ------------------------------- imóveis ------------------------------- */

    public async Task<IReadOnlyList<TemporadaImovelLinha>> ImoveisAsync(UsuarioAtual u)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var hoje = DatasBR.Hoje();
        var cfgs = await db.TemporadaImoveis.AsNoTracking().Include(t => t.Imovel)
            .Where(t => db.Imoveis.VisiveisPara(u).Any(i => i.Id == t.ImovelId)).OrderBy(t => t.Imovel!.Codigo).ToListAsync();
        var ids = cfgs.Select(c => c.ImovelId).ToList();
        var cals = await db.TemporadaCalendarios.AsNoTracking().Where(c => ids.Contains(c.ImovelId)).OrderBy(c => c.CriadoEm).ToListAsync();
        var futuras = await db.TemporadaReservas.Where(r => ids.Contains(r.ImovelId) && r.Saida >= hoje
                && r.Status != StatusReserva.CANCELADA && r.Status != StatusReserva.PEDIDO)
            .GroupBy(r => r.ImovelId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        return [.. cfgs.Select(c => new TemporadaImovelLinha(c.ImovelId, c.Imovel!.Codigo, c.Imovel.Titulo, c.Ativo, c.Modalidade, c.Diaria,
            c.ComissaoPct, Url($"temporada/ical/{c.IcalToken}.ics"),
            [.. cals.Where(x => x.ImovelId == c.ImovelId).Select(x => new CalendarioLinha(x.Id, x.Origem, x.Url, x.UltimaSincronizacao, x.Erro))],
            futuras.GetValueOrDefault(c.ImovelId)))];
    }

    /// <summary>Configuração do imóvel; sem configuração ainda, uma sugestão (diária = aluguel/30).</summary>
    public async Task<TemporadaConfigForm> ConfigAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var im = await db.Imoveis.VisiveisPara(u).AsNoTracking().Where(i => i.Id == imovelId)
            .Select(i => new { i.PrecoLocacao }).SingleOrDefaultAsync() ?? throw new NaoEncontradoException("Imóvel não encontrado.");
        var c = await db.TemporadaImoveis.AsNoTracking().SingleOrDefaultAsync(x => x.ImovelId == imovelId);
        if (c is null) return new TemporadaConfigForm { Diaria = im.PrecoLocacao is > 0 ? decimal.Round(im.PrecoLocacao.Value / 30, 0) : null };
        var fax = c.FaxineiraId is { } f ? await db.Pessoas.Where(p => p.Id == f).Select(p => p.NomeRazao).SingleOrDefaultAsync() : null;
        return new TemporadaConfigForm
        {
            Ativo = c.Ativo, Modalidade = c.Modalidade, Diaria = c.Diaria, DiariaFimSemana = c.DiariaFimSemana, MinimoNoites = c.MinimoNoites,
            HospedesMax = c.HospedesMax, TaxaLimpeza = c.TaxaLimpeza, Caucao = c.Caucao, ComissaoPct = c.ComissaoPct, Checkin = c.Checkin,
            Checkout = c.Checkout, Regras = c.Regras, FaxineiraId = c.FaxineiraId, FaxineiraNome = fax,
        };
    }

    public async Task SalvarConfigAsync(UsuarioAtual u, Guid imovelId, TemporadaConfigForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        if (!await db.Imoveis.VisiveisPara(u).AnyAsync(i => i.Id == imovelId)) throw new NaoEncontradoException("Imóvel não encontrado.");
        if (form.FaxineiraId is { } fid && !await db.Pessoas.AnyAsync(p => p.Id == fid)) throw new RegraNegocioException(nameof(form.FaxineiraId), "Pessoa inválida.");
        var c = await db.TemporadaImoveis.SingleOrDefaultAsync(x => x.ImovelId == imovelId);
        var novo = c is null;
        var antes = novo ? null : new { c!.Ativo, c.Diaria, c.DiariaFimSemana, c.ComissaoPct, c.TaxaLimpeza, c.Caucao, c.Modalidade };
        c ??= new TemporadaImovel { ImovelId = imovelId, IcalToken = NovoToken() };
        c.Ativo = form.Ativo;
        c.Modalidade = form.Modalidade;
        c.Diaria = form.Diaria!.Value;
        c.DiariaFimSemana = form.DiariaFimSemana;
        c.MinimoNoites = form.MinimoNoites;
        c.HospedesMax = form.HospedesMax;
        c.TaxaLimpeza = form.TaxaLimpeza;
        c.Caucao = form.Caucao;
        c.ComissaoPct = form.ComissaoPct;
        c.Checkin = form.Checkin;
        c.Checkout = form.Checkout;
        c.Regras = Texto.Limpo(form.Regras);
        c.FaxineiraId = form.FaxineiraId;
        c.AtualizadoPor = u.Id;
        c.AtualizadoEm = DateTime.UtcNow;
        if (novo) db.TemporadaImoveis.Add(c);
        audit.Registrar(db, u.Id, "temporada_imoveis", imovelId, novo ? "CRIAR" : "ATUALIZAR", antes,
            new { c.Ativo, c.Diaria, c.DiariaFimSemana, c.ComissaoPct, c.TaxaLimpeza, c.Caucao, c.Modalidade });
        await db.SaveChangesAsync();
    }

    /// <summary>Troca o link do calendário exportado (o antigo para de funcionar; recadastrar nas plataformas).</summary>
    public async Task NovoLinkIcalAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await Config(db, u, imovelId);
        c.IcalToken = NovoToken();
        audit.Registrar(db, u.Id, "temporada_imoveis", imovelId, "NOVO_LINK_ICAL");
        await db.SaveChangesAsync();
    }

    public async Task AdicionarCalendarioAsync(UsuarioAtual u, Guid imovelId, OrigemCalendario origem, string url)
    {
        u.Exigir(P.LocacaoEditar);
        url = (url ?? "").Trim();
        if (!UrlCalendarioValida(url))
            throw new RegraNegocioException("url", "Cole o link de exportação do calendário (https://..., termina em .ics ou ical).");
        await using var db = await fabrica.CreateDbContextAsync();
        await Config(db, u, imovelId);
        var cal = new TemporadaCalendario { ImovelId = imovelId, Origem = origem, Url = url };
        db.TemporadaCalendarios.Add(cal);
        audit.Registrar(db, u.Id, "temporada_calendarios", cal.Id, "CRIAR", depois: new { origem, imovelId });
        await db.SaveChangesAsync();
    }

    public async Task RemoverCalendarioAsync(UsuarioAtual u, Guid calendarioId)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var cal = await db.TemporadaCalendarios.SingleOrDefaultAsync(c => c.Id == calendarioId && db.Imoveis.VisiveisPara(u).Any(i => i.Id == c.ImovelId))
            ?? throw new NaoEncontradoException("Calendário não encontrado.");
        db.TemporadaCalendarios.Remove(cal);
        audit.Registrar(db, u.Id, "temporada_calendarios", cal.Id, "EXCLUIR");
        await db.SaveChangesAsync();
    }

    /// <summary>Só https para endereço público (não aceita IP nem localhost: evita o servidor consultar a rede interna).</summary>
    internal static bool UrlCalendarioValida(string url) =>
        url.Length <= 1000 && Uri.TryCreate(url, UriKind.Absolute, out var x) && x.Scheme == Uri.UriSchemeHttps && x.HostNameType == UriHostNameType.Dns
        && !x.IsLoopback && x.Host.Contains('.') && !x.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        && !x.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);

    private static async Task<TemporadaImovel> Config(InovabensDb db, UsuarioAtual u, Guid imovelId) =>
        await db.TemporadaImoveis.SingleOrDefaultAsync(c => c.ImovelId == imovelId && db.Imoveis.VisiveisPara(u).Any(i => i.Id == c.ImovelId))
        ?? throw new NaoEncontradoException("Imóvel sem configuração de temporada.");

    /* ------------------------------- preço ------------------------------- */

    /// <summary>Diárias da estadia: noites de sexta e sábado pela diária de fim de semana, se houver.</summary>
    public static decimal CalcularDiarias(TemporadaImovel c, DateOnly entrada, DateOnly saida)
    {
        decimal total = 0;
        for (var d = entrada; d < saida; d = d.AddDays(1))
            total += c.DiariaFimSemana is { } fs && d.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday ? fs : c.Diaria;
        return total;
    }

    /// <summary>Reservas que ocupam as datas (PEDIDO e CANCELADA não ocupam). Saída de uma no dia da entrada da outra não conflita.</summary>
    private static IQueryable<TemporadaReserva> Conflitos(InovabensDb db, Guid imovelId, DateOnly entrada, DateOnly saida, Guid? exceto) =>
        db.TemporadaReservas.Where(r => r.ImovelId == imovelId && r.Id != exceto && r.Status != StatusReserva.PEDIDO && r.Status != StatusReserva.CANCELADA
            && r.Entrada < saida && r.Saida > entrada);

    public async Task<Cotacao> CotarAsync(Guid imovelId, DateOnly entrada, DateOnly saida, Guid? exceto = null, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await db.TemporadaImoveis.AsNoTracking().SingleOrDefaultAsync(x => x.ImovelId == imovelId, ct)
            ?? throw new NaoEncontradoException("Imóvel sem configuração de temporada.");
        return await Cotar(db, c, entrada, saida, exceto, ct);
    }

    private static async Task<Cotacao> Cotar(InovabensDb db, TemporadaImovel c, DateOnly entrada, DateOnly saida, Guid? exceto, CancellationToken ct)
    {
        var noites = saida.DayNumber - entrada.DayNumber;
        if (noites <= 0) return new Cotacao(0, 0, 0, 0, false, "A saída precisa ser depois da entrada.");
        string? motivo = null;
        if (noites > 90) motivo = "Temporada é de até 90 dias.";
        else if (noites < c.MinimoNoites) motivo = $"Mínimo de {c.MinimoNoites} noite(s).";
        else if (await Conflitos(db, c.ImovelId, entrada, saida, exceto).Select(r => r.Numero).FirstOrDefaultAsync(ct) is > 0 and var n)
            motivo = $"Datas ocupadas ({TemporadaReserva.CodigoDe(n)}).";
        return new Cotacao(noites, CalcularDiarias(c, entrada, saida), c.TaxaLimpeza, c.Caucao, motivo is null, motivo);
    }

    /* ------------------------------- reservas ------------------------------- */

    public async Task<IReadOnlyList<ReservaLinha>> ReservasAsync(UsuarioAtual u, Guid? imovelId = null, DateOnly? de = null, DateOnly? ate = null,
        bool incluirCanceladas = false)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var q = db.TemporadaReservas.AsNoTracking().Where(r => db.Imoveis.VisiveisPara(u).Any(i => i.Id == r.ImovelId));
        if (imovelId is { } im) q = q.Where(r => r.ImovelId == im);
        if (de is { } d) q = q.Where(r => r.Saida >= d);
        if (ate is { } a) q = q.Where(r => r.Entrada <= a);
        if (!incluirCanceladas) q = q.Where(r => r.Status != StatusReserva.CANCELADA);
        var linhas = await q.OrderBy(r => r.Entrada).Take(500)
            .Select(r => new
            {
                r.Id, r.Numero, r.ImovelId, Imovel = db.Imoveis.Where(i => i.Id == r.ImovelId).Select(i => i.Codigo).First(), r.HospedeNome,
                r.Entrada, r.Saida, r.Status, r.Origem, AReceber = r.Diarias - r.Desconto + r.Limpeza + r.Caucao - r.TaxaPlataforma,
                Recebido = db.TemporadaPagamentos.Where(p => p.ReservaId == r.Id && p.EstornadoEm == null).Sum(p => (decimal?)p.Valor) ?? 0,
            }).ToListAsync();
        return [.. linhas.Select(r => new ReservaLinha(r.Id, TemporadaReserva.CodigoDe(r.Numero), r.ImovelId, r.Imovel, r.HospedeNome, r.Entrada, r.Saida,
            r.Status, r.Origem, r.AReceber, r.Recebido))];
    }

    public async Task<ReservaDetalhe> ObterAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var r = await Reserva(db, u, id, rastrear: false);
        return await Detalhe(db, r);
    }

    private static async Task<ReservaDetalhe> Detalhe(InovabensDb db, TemporadaReserva r)
    {
        var c = await db.TemporadaImoveis.AsNoTracking().Include(x => x.Imovel).SingleAsync(x => x.ImovelId == r.ImovelId);
        var pags = await db.TemporadaPagamentos.AsNoTracking().Where(p => p.ReservaId == r.Id).OrderBy(p => p.Data).ThenBy(p => p.RegistradoEm)
            .Select(p => new PagamentoReservaLinha(p.Id, p.Data, p.Valor, p.Forma,
                db.ContasBancarias.Where(x => x.Id == p.ContaBancariaId).Select(x => x.Nome).FirstOrDefault(), p.EstornadoEm != null)).ToListAsync();
        var fax = c.FaxineiraId is { } f ? await db.Pessoas.Where(p => p.Id == f).Select(p => p.NomeRazao).SingleOrDefaultAsync() : null;
        return new ReservaDetalhe(r, c.Imovel!.Codigo, c.Imovel.Titulo, ManutencaoService.Endereco(c.Imovel), c, pags, await Acerto(db, r, c), fax);
    }

    private static async Task<AcertoReserva> Acerto(InovabensDb db, TemporadaReserva r, TemporadaImovel c)
    {
        var baseDiarias = r.Diarias - r.Desconto;
        var comissao = c.Modalidade == ModalidadeLocacao.ADMINISTRACAO ? decimal.Round(baseDiarias * r.ComissaoPct / 100, 2) : 0;
        var retida = Math.Min(r.CaucaoRetida, r.Caucao);
        var repasse = c.Modalidade == ModalidadeLocacao.ADMINISTRACAO ? Math.Max(0, baseDiarias - comissao - r.TaxaPlataforma + retida) : 0;
        var hoje = DatasBR.Hoje();
        var donos = c.Modalidade == ModalidadeLocacao.ADMINISTRACAO
            ? await db.ImovelProprietarios.AsNoTracking().Where(p => p.ImovelId == r.ImovelId && (p.FimEm == null || p.FimEm >= hoje))
                .OrderByDescending(p => p.Percentual).Select(p => new { p.PessoaId, p.Percentual, p.Pessoa!.NomeRazao }).ToListAsync()
            : [];
        var soma = donos.Sum(d => d.Percentual);
        var partes = new List<RepasseTemporada>();
        var resto = repasse;
        for (var i = 0; i < donos.Count; i++)
        {
            // o último fica com o arredondamento
            var v = i == donos.Count - 1 ? resto : decimal.Round(repasse * donos[i].Percentual / (soma == 0 ? 1 : soma), 2);
            partes.Add(new RepasseTemporada(donos[i].PessoaId, donos[i].NomeRazao, donos[i].Percentual, v));
            resto -= v;
        }
        return new AcertoReserva(baseDiarias, comissao, r.TaxaPlataforma, r.Limpeza, retida, r.Caucao - retida, partes);
    }

    public async Task<Guid> CriarAsync(UsuarioAtual u, ReservaForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var c = await Config(db, u, form.ImovelId ?? Guid.Empty);
        await Travar(db, c.ImovelId);
        var r = new TemporadaReserva { ImovelId = c.ImovelId, CriadoPor = u.Id, Status = StatusReserva.CONFIRMADA };
        await Aplicar(db, r, c, form);
        db.TemporadaReservas.Add(r);
        audit.Registrar(db, u.Id, "temporada_reservas", r.Id, "CRIAR", depois: r);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return r.Id;
    }

    public async Task AtualizarAsync(UsuarioAtual u, Guid id, ReservaForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var r = await Reserva(db, u, id);
        if (r.Status is StatusReserva.FECHADA or StatusReserva.CANCELADA) throw new RegraNegocioException("_", "Reserva fechada ou cancelada não muda.");
        var c = await db.TemporadaImoveis.SingleAsync(x => x.ImovelId == r.ImovelId);
        await Travar(db, c.ImovelId);
        var antes = r.Copia();
        await Aplicar(db, r, c, form);
        r.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "temporada_reservas", r.Id, "ATUALIZAR", antes, r);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>Uma gravação de reserva por imóvel por vez: a conferência de datas não deixa duas reservas no mesmo dia.</summary>
    private static Task Travar(InovabensDb db, Guid imovelId) =>
        db.Database.ExecuteSqlRawAsync("select 1 from temporada_imoveis where imovel_id = {0} for update", imovelId);

    private static async Task Aplicar(InovabensDb db, TemporadaReserva r, TemporadaImovel c, ReservaForm f)
    {
        var entrada = f.Entrada!.Value;
        var saida = f.Saida!.Value;
        if (f.Hospedes > c.HospedesMax) throw new RegraNegocioException(nameof(f.Hospedes), $"O imóvel recebe até {c.HospedesMax} hóspede(s).");
        if (r.Status != StatusReserva.PEDIDO && await Conflitos(db, c.ImovelId, entrada, saida, r.Id).Select(x => x.Numero).FirstOrDefaultAsync() is > 0 and var n)
            throw new RegraNegocioException(nameof(f.Entrada), $"Datas ocupadas pela reserva {TemporadaReserva.CodigoDe(n)}.");
        if (f.HospedeId is { } hid && !await db.Pessoas.AnyAsync(p => p.Id == hid)) throw new RegraNegocioException(nameof(f.HospedeId), "Pessoa inválida.");
        var plataforma = f.Origem is OrigemReserva.AIRBNB or OrigemReserva.BOOKING;
        r.Origem = f.Origem;
        r.HospedeId = f.HospedeId;
        r.HospedeNome = f.HospedeNome.Trim();
        r.HospedeTelefone = Texto.Limpo(f.HospedeTelefone);
        r.HospedeEmail = Texto.Limpo(f.HospedeEmail);
        r.HospedeDocumento = Texto.Limpo(f.HospedeDocumento) is { } doc ? Texto.Digitos(doc) : null;
        r.Hospedes = f.Hospedes;
        r.Entrada = entrada;
        r.Saida = saida;
        r.Diarias = f.Diarias ?? CalcularDiarias(c, entrada, saida);
        r.Desconto = Math.Min(f.Desconto, r.Diarias);
        // na plataforma a limpeza e a caução vêm do que ela informa (padrão zero)
        r.Limpeza = f.Limpeza ?? (plataforma ? 0 : c.TaxaLimpeza);
        r.Caucao = f.Caucao ?? (plataforma ? 0 : c.Caucao);
        r.TaxaPlataforma = f.TaxaPlataforma;
        r.ComissaoPct = f.ComissaoPct ?? c.ComissaoPct;
        r.Observacoes = Texto.Limpo(f.Observacoes);
    }

    public static ReservaForm Formulario(TemporadaReserva r) => new()
    {
        ImovelId = r.ImovelId, Origem = r.Origem, HospedeId = r.HospedeId, HospedeNome = r.HospedeNome, HospedeTelefone = r.HospedeTelefone,
        HospedeEmail = r.HospedeEmail, HospedeDocumento = r.HospedeDocumento, Hospedes = r.Hospedes, Entrada = r.Entrada, Saida = r.Saida,
        Diarias = r.Diarias, Desconto = r.Desconto, Limpeza = r.Limpeza, Caucao = r.Caucao, TaxaPlataforma = r.TaxaPlataforma,
        ComissaoPct = r.ComissaoPct, Observacoes = r.Observacoes,
    };

    /// <summary>Pedido do site vira reserva: confere de novo as datas (podem ter sido ocupadas depois do pedido).</summary>
    public async Task ConfirmarAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var r = await Reserva(db, u, id);
        if (r.Status != StatusReserva.PEDIDO) throw new RegraNegocioException("_", "Só pedido de reserva é confirmado.");
        await Travar(db, r.ImovelId);
        if (await Conflitos(db, r.ImovelId, r.Entrada, r.Saida, r.Id).Select(x => x.Numero).FirstOrDefaultAsync() is > 0 and var n)
            throw new RegraNegocioException("_", $"Datas ocupadas pela reserva {TemporadaReserva.CodigoDe(n)}.");
        await Mudar(db, u, r, StatusReserva.CONFIRMADA, "CONFIRMAR");
        await tx.CommitAsync();
    }

    public async Task CheckinAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var r = await Reserva(db, u, id);
        if (r.Status != StatusReserva.CONFIRMADA) throw new RegraNegocioException("_", "Check-in só de reserva confirmada.");
        r.CheckinEm = DateTime.UtcNow;
        await Mudar(db, u, r, StatusReserva.EM_CURSO, "CHECKIN");
    }

    /// <summary>Check-out: registra quanto da caução fica retido (danos, multa de regras); o resto volta ao hóspede no fechamento.</summary>
    public async Task CheckoutAsync(UsuarioAtual u, Guid id, decimal caucaoRetida, string? observacao)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var r = await Reserva(db, u, id);
        if (r.Status is not (StatusReserva.CONFIRMADA or StatusReserva.EM_CURSO)) throw new RegraNegocioException("_", "Check-out só de reserva confirmada ou em curso.");
        if (caucaoRetida < 0 || caucaoRetida > r.Caucao) throw new RegraNegocioException("caucao", $"A caução é de {Texto.Moeda(r.Caucao)}.");
        r.CaucaoRetida = caucaoRetida;
        r.CheckinEm ??= DateTime.UtcNow;
        r.CheckoutEm = DateTime.UtcNow;
        if (Texto.Limpo(observacao) is { } obs) r.Observacoes = string.Join("\n", new[] { r.Observacoes, $"Check-out: {obs}" }.Where(x => x != null));
        await Mudar(db, u, r, StatusReserva.FINALIZADA, "CHECKOUT");
    }

    /// <summary>Cancela. Com recebimento ativo, estorne antes (ou devolva o que foi pago e registre).</summary>
    public async Task CancelarAsync(UsuarioAtual u, Guid id, string? motivo)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var r = await Reserva(db, u, id);
        if (r.Status is StatusReserva.FECHADA or StatusReserva.CANCELADA or StatusReserva.FINALIZADA)
            throw new RegraNegocioException("_", "Esta reserva não pode mais ser cancelada.");
        if (await db.TemporadaPagamentos.AnyAsync(p => p.ReservaId == r.Id && p.EstornadoEm == null))
            throw new RegraNegocioException("_", "A reserva tem recebimento: estorne antes de cancelar (a devolução ao hóspede é feita no financeiro).");
        r.CanceladaEm = DateTime.UtcNow;
        r.MotivoCancelamento = Texto.Limpo(motivo);
        await Mudar(db, u, r, StatusReserva.CANCELADA, "CANCELAR");
    }

    private async Task Mudar(InovabensDb db, UsuarioAtual u, TemporadaReserva r, StatusReserva novo, string acao)
    {
        var antes = r.Status;
        r.Status = novo;
        r.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "temporada_reservas", r.Id, acao, new { Status = antes }, new { r.Status, r.CaucaoRetida, r.MotivoCancelamento });
        await db.SaveChangesAsync();
    }

    private static async Task<TemporadaReserva> Reserva(InovabensDb db, UsuarioAtual u, Guid id, bool rastrear = true)
    {
        var q = rastrear ? db.TemporadaReservas : db.TemporadaReservas.AsNoTracking();
        return await q.SingleOrDefaultAsync(r => r.Id == id && db.Imoveis.VisiveisPara(u).Any(i => i.Id == r.ImovelId))
            ?? throw new NaoEncontradoException("Reserva não encontrada.");
    }

    /* ------------------------------- dinheiro ------------------------------- */

    /// <summary>Recebimento do hóspede (sinal, saldo ou caução). Administração: dinheiro de terceiros; próprio: receita do imóvel.</summary>
    public async Task<Guid> ReceberAsync(UsuarioAtual u, Guid id, RecebimentoReservaForm form)
    {
        u.Exigir(P.LocacaoReceber);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var r = await Reserva(db, u, id);
        await db.Database.ExecuteSqlRawAsync("select 1 from temporada_reservas where id = {0} for update", r.Id);
        if (r.Status is StatusReserva.CANCELADA or StatusReserva.FECHADA) throw new RegraNegocioException("_", "Reserva cancelada ou fechada.");
        var c = await db.TemporadaImoveis.AsNoTracking().Include(x => x.Imovel).SingleAsync(x => x.ImovelId == r.ImovelId);
        var recebido = await db.TemporadaPagamentos.Where(p => p.ReservaId == r.Id && p.EstornadoEm == null).SumAsync(p => (decimal?)p.Valor) ?? 0;
        var saldo = r.Total - r.TaxaPlataforma - recebido;
        var data = form.Data!.Value;
        var valor = form.Valor!.Value;
        if (valor > saldo) throw new RegraNegocioException(nameof(form.Valor), $"O saldo da reserva é {Texto.Moeda(Math.Max(0, saldo))}.");
        if (data > DatasBR.Hoje()) throw new RegraNegocioException(nameof(form.Data), "A data do recebimento não pode ser futura.");
        if (c.Modalidade == ModalidadeLocacao.ADMINISTRACAO && form.ContaBancariaId is null)
            throw new RegraNegocioException(nameof(form.ContaBancariaId), "Informe a conta da imobiliária onde o dinheiro entrou.");
        if (form.ContaBancariaId is { } cid && !await db.ContasBancarias.AnyAsync(x => x.Id == cid && x.Ativa && x.Tipo != TipoContaBancaria.CARTAO_CREDITO))
            throw new RegraNegocioException(nameof(form.ContaBancariaId), "Conta bancária inválida.");
        await MesesFechados.Garantir(db, data);

        var pg = new TemporadaPagamento
        {
            ReservaId = r.Id, Data = data, Valor = valor, Forma = Texto.Limpo(form.Forma), ContaBancariaId = form.ContaBancariaId,
            Observacao = Texto.Limpo(form.Observacao), RegistradoPor = u.Id,
        };
        var referencia = $"{r.Codigo} · {c.Imovel!.Codigo} · {r.Entrada:dd/MM} a {r.Saida:dd/MM/yyyy}";
        if (c.Modalidade == ModalidadeLocacao.ADMINISTRACAO)
        {
            var l = new LancamentoEmpresa
            {
                Descricao = $"Hospedagem — {referencia}", Valor = valor, Vencimento = data, DataPagamento = data, Competencia = CalculoLocacao.MesDe(data),
                CategoriaId = await LocacaoService.Categoria(db, CategoriaHospedagem, TipoCategoriaEmpresa.ENTRADA, GrupoCategoriaEmpresa.TERCEIROS),
                ContaId = form.ContaBancariaId, PessoaId = r.HospedeId, PessoaNome = r.HospedeNome, ImovelId = r.ImovelId,
                Documento = "Recibo de temporada", CriadoPor = u.Id, AtualizadoPor = u.Id,
            };
            db.LancamentosEmpresa.Add(l);
            audit.Registrar(db, u.Id, "lancamentos_empresa", l.Id, "CRIAR", depois: l);
            pg.LancamentoEmpresaId = l.Id;
        }
        else
        {
            var contaId = await db.ContasOrcamentarias.Where(x => x.Nome == LocacaoService.ContaAluguelImovel && x.Tipo == TipoConta.RECEITA)
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync()
                ?? throw new RegraNegocioException("_", $"Crie a conta de receita \"{LocacaoService.ContaAluguelImovel}\" no financeiro dos imóveis.");
            var l = new LancamentoFinanceiro
            {
                ImovelId = r.ImovelId, Situacao = SituacaoLancamento.LIQUIDADO, Data = data, DataLiquidacao = data, Valor = valor,
                Descricao = $"Temporada {referencia}", ContaId = contaId, PessoaId = r.HospedeId, PessoaNome = r.HospedeNome,
                ContaBancariaId = form.ContaBancariaId, Competencia = CalculoLocacao.MesDe(r.Entrada), CriadoPor = u.Id, AtualizadoPor = u.Id,
            };
            l.Numero = await LancamentosFinanceirosService.ProximoNumero(db, r.ImovelId);
            db.LancamentosFinanceiros.Add(l);
            audit.Registrar(db, u.Id, "lancamentos_financeiros", l.Id, "CRIAR", depois: l);
            pg.LancamentoImovelId = l.Id;
        }
        db.TemporadaPagamentos.Add(pg);
        audit.Registrar(db, u.Id, "temporada_reservas", r.Id, "RECEBER", depois: new { pg.Id, pg.Valor, pg.Data, pg.Forma });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return pg.Id;
    }

    public async Task EstornarAsync(UsuarioAtual u, Guid pagamentoId)
    {
        u.Exigir(P.LocacaoReceber);
        await using var db = await fabrica.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var pg = await db.TemporadaPagamentos.SingleOrDefaultAsync(p => p.Id == pagamentoId && p.EstornadoEm == null)
            ?? throw new NaoEncontradoException("Recebimento não encontrado.");
        var r = await Reserva(db, u, pg.ReservaId, rastrear: false);
        if (r.Status == StatusReserva.FECHADA) throw new RegraNegocioException("_", "Reserva fechada: desfaça o fechamento antes.");
        if (pg.LancamentoEmpresaId is { } le && await db.LancamentosEmpresa.SingleOrDefaultAsync(l => l.Id == le && l.ExcluidoEm == null) is { } l)
        {
            if (l.ExtratoItemId is not null) throw new RegraNegocioException("_", "Este recebimento já foi conciliado com o extrato: desfaça a conciliação antes.");
            await MesesFechados.Garantir(db, l.DataPagamento, l.Competencia);
            var antes = l.Copia();
            l.ExcluidoEm = DateTime.UtcNow;
            l.AtualizadoPor = u.Id;
            audit.Registrar(db, u.Id, "lancamentos_empresa", l.Id, "EXCLUIR", antes, l);
        }
        if (pg.LancamentoImovelId is { } li && await db.LancamentosFinanceiros.SingleOrDefaultAsync(x => x.Id == li && x.ExcluidoEm == null) is { } lf)
        {
            if (lf.ExtratoItemId is not null) throw new RegraNegocioException("_", "Este recebimento já foi conciliado com o extrato: desfaça a conciliação antes.");
            var antes = lf.Copia();
            lf.ExcluidoEm = DateTime.UtcNow;
            lf.AtualizadoPor = u.Id;
            audit.Registrar(db, u.Id, "lancamentos_financeiros", lf.Id, "EXCLUIR", antes, lf);
        }
        pg.EstornadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "temporada_reservas", r.Id, "ESTORNAR", depois: new { pg.Id, pg.Valor });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>
    /// Fecha a reserva depois do check-out e com tudo recebido. Administração: a comissão passa de dinheiro de terceiros
    /// para receita; repasse a cada proprietário, limpeza à faxineira e devolução da caução ficam a pagar.
    /// </summary>
    public async Task FecharAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.LocacaoReceber);
        await using var db = await fabrica.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var r = await Reserva(db, u, id);
        await db.Database.ExecuteSqlRawAsync("select 1 from temporada_reservas where id = {0} for update", r.Id);
        if (r.Status != StatusReserva.FINALIZADA) throw new RegraNegocioException("_", "Feche a reserva depois do check-out.");
        var d = await Detalhe(db, r);
        if (d.Saldo > 0) throw new RegraNegocioException("_", $"Falta receber {Texto.Moeda(d.Saldo)}.");
        var c = d.Config;
        var hoje = DatasBR.Hoje();
        if (c.Modalidade == ModalidadeLocacao.ADMINISTRACAO)
        {
            await MesesFechados.Garantir(db, hoje);
            var a = d.Acerto;
            if (a.Repasse > 0 && a.Repasses.Count == 0) throw new RegraNegocioException("_", "Cadastre os proprietários do imóvel para o repasse.");
            if (a.Limpeza > 0 && c.FaxineiraId is null) throw new RegraNegocioException("_", "Informe quem faz a limpeza na configuração do imóvel.");
            var conta = await db.TemporadaPagamentos.Where(p => p.ReservaId == r.Id && p.EstornadoEm == null).OrderByDescending(p => p.Data)
                .Select(p => p.ContaBancariaId).FirstOrDefaultAsync();
            var referencia = $"{r.Codigo} · {d.ImovelCodigo} · {r.Entrada:dd/MM} a {r.Saida:dd/MM/yyyy}";
            var previsto = hoje.AddDays(DiasRepasse);
            LancamentoEmpresa Lanc(string categoria, Guid catId, decimal valor, DateOnly venc, DateOnly? pagoEm, Guid? pessoa, string? nome) => new()
            {
                Descricao = $"{categoria} — {referencia}", Valor = valor, Vencimento = venc, DataPagamento = pagoEm, Competencia = CalculoLocacao.MesDe(venc),
                CategoriaId = catId, ContaId = conta, PessoaId = pessoa, PessoaNome = nome, ImovelId = r.ImovelId,
                Documento = "Acerto de temporada", CriadoPor = u.Id, AtualizadoPor = u.Id,
            };
            var lancs = new List<LancamentoEmpresa>();
            if (a.Comissao > 0)
            {
                // a comissão estava em dinheiro de terceiros: sai de lá e entra como receita (mesma conta, saldo não muda)
                lancs.Add(Lanc(CategoriaComissaoRetida, await LocacaoService.Categoria(db, CategoriaComissaoRetida, TipoCategoriaEmpresa.SAIDA, GrupoCategoriaEmpresa.TERCEIROS),
                    -a.Comissao, hoje, hoje, null, null));
                lancs.Add(Lanc(CategoriaComissao, await LocacaoService.Categoria(db, CategoriaComissao, TipoCategoriaEmpresa.ENTRADA, GrupoCategoriaEmpresa.RECEITA),
                    a.Comissao, hoje, hoje, null, null));
            }
            var catRepasse = await LocacaoService.Categoria(db, CategoriaRepasse, TipoCategoriaEmpresa.SAIDA, GrupoCategoriaEmpresa.TERCEIROS);
            foreach (var p in a.Repasses.Where(p => p.Valor > 0))
                lancs.Add(Lanc(CategoriaRepasse, catRepasse, -p.Valor, previsto, null, p.PessoaId, p.Nome));
            if (a.Limpeza > 0)
                lancs.Add(Lanc(CategoriaLimpeza, await LocacaoService.Categoria(db, CategoriaLimpeza, TipoCategoriaEmpresa.SAIDA, GrupoCategoriaEmpresa.TERCEIROS),
                    -a.Limpeza, previsto, null, c.FaxineiraId, d.Faxineira));
            if (a.CaucaoDevolver > 0)
                lancs.Add(Lanc(CategoriaCaucao, await LocacaoService.Categoria(db, CategoriaCaucao, TipoCategoriaEmpresa.SAIDA, GrupoCategoriaEmpresa.TERCEIROS),
                    -a.CaucaoDevolver, hoje.AddDays(2), null, r.HospedeId, r.HospedeNome));
            db.LancamentosEmpresa.AddRange(lancs);
            foreach (var l in lancs) audit.Registrar(db, u.Id, "lancamentos_empresa", l.Id, "CRIAR", depois: l);
            r.LancamentosEmpresaIds = [.. lancs.Select(l => l.Id)];
        }
        r.FechadaEm = DateTime.UtcNow;
        await Mudar(db, u, r, StatusReserva.FECHADA, "FECHAR");
        await tx.CommitAsync();
    }

    /// <summary>Desfaz o fechamento (lançamentos do acerto excluídos), se nada dele foi pago nem conciliado.</summary>
    public async Task ReabrirAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.LocacaoReceber);
        await using var db = await fabrica.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var r = await Reserva(db, u, id);
        if (r.Status != StatusReserva.FECHADA) throw new RegraNegocioException("_", "A reserva não está fechada.");
        var lancs = await db.LancamentosEmpresa.Where(l => r.LancamentosEmpresaIds.Contains(l.Id) && l.ExcluidoEm == null).ToListAsync();
        if (lancs.Any(l => l.Valor < 0 && l.DataPagamento is not null && !l.Descricao.StartsWith(CategoriaComissaoRetida)))
            throw new RegraNegocioException("_", "Repasse, limpeza ou caução do acerto já foi pago: desfaça o pagamento antes.");
        if (lancs.Any(l => l.ExtratoItemId is not null)) throw new RegraNegocioException("_", "Lançamento do acerto conciliado: desfaça a conciliação antes.");
        foreach (var l in lancs)
        {
            await MesesFechados.Garantir(db, l.DataPagamento, l.Competencia);
            var antes = l.Copia();
            l.ExcluidoEm = DateTime.UtcNow;
            l.AtualizadoPor = u.Id;
            audit.Registrar(db, u.Id, "lancamentos_empresa", l.Id, "EXCLUIR", antes, l);
        }
        r.LancamentosEmpresaIds = [];
        r.FechadaEm = null;
        await Mudar(db, u, r, StatusReserva.FINALIZADA, "REABRIR");
        await tx.CommitAsync();
    }

    /* ------------------------------- documentos ------------------------------- */

    /// <summary>Contrato de locação para temporada (Lei 8.245, arts. 48 a 50), pronto para assinar.</summary>
    public async Task<Arquivo> ContratoPdfAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var r = await Reserva(db, u, id, rastrear: false);
        var d = await Detalhe(db, r);
        var c = d.Config;
        var locadores = c.Modalidade == ModalidadeLocacao.ADMINISTRACAO && d.Acerto.Repasses.Count > 0
            ? string.Join("; ", d.Acerto.Repasses.Select(p => p.Nome)) + ", representado(s) pela Inova Bens (administradora)"
            : "Inova Bens";
        var pdf = RelatoriosService.NovoPdf("Contrato de locação para temporada", $"{r.Codigo} · {d.ImovelCodigo}",
            $"Inova Bens · emitido em {DatasBR.Agora():dd/MM/yyyy HH:mm}", col =>
            {
                RelatoriosService.Campos(col, 1, ("Locador(es)", locadores));
                RelatoriosService.Campos(col, 2, ("Locatário", r.HospedeNome + (r.HospedeDocumento is { } doc ? $" ({Formatos.Documento(doc)})" : "")),
                    ("Contato", string.Join(" · ", new[] { r.HospedeTelefone, r.HospedeEmail }.Where(x => x != null)) is { Length: > 0 } ct ? ct : "—"));
                RelatoriosService.Campos(col, 1, ("Imóvel", $"{d.ImovelCodigo} · {d.Endereco}"));
                RelatoriosService.Campos(col, 3, ("Entrada", $"{Texto.Data(r.Entrada)} a partir das {c.Checkin}"),
                    ("Saída", $"{Texto.Data(r.Saida)} até as {c.Checkout}"), ("Hóspedes", r.Hospedes.ToString()));
                RelatoriosService.Secao(col, "Valores");
                RelatoriosService.Tabela(col, [("Item", 3), ("Valor", 1)],
                [
                    [$"Diárias ({r.Noites} noite(s))", Texto.Moeda(r.Diarias)],
                    .. r.Desconto > 0 ? new[] { new[] { "Desconto", "− " + Texto.Moeda(r.Desconto) } } : [],
                    .. r.Limpeza > 0 ? new[] { new[] { "Taxa de limpeza", Texto.Moeda(r.Limpeza) } } : [],
                    .. r.Caucao > 0 ? new[] { new[] { "Caução (devolvida na saída, descontados danos)", Texto.Moeda(r.Caucao) } } : [],
                    ["Total", Texto.Moeda(r.Total)],
                ], "—", alinharUltimaDireita: true);
                RelatoriosService.Secao(col, "Cláusulas");
                RelatoriosService.Paragrafo(col,
                    $"1. Locação para temporada (Lei 8.245/91, arts. 48 a 50), para residência temporária do LOCATÁRIO, por {r.Noites} noite(s), "
                    + "sem prorrogação automática. Findo o prazo, o imóvel deve ser desocupado no horário de saída.\n"
                    + "2. O aluguel e os encargos podem ser recebidos antecipadamente, de uma só vez (art. 49). A reserva se confirma com o pagamento combinado.\n"
                    + $"3. O imóvel é entregue mobiliado e em condições de uso; o número máximo de ocupantes é {c.HospedesMax}.\n"
                    + "4. O LOCATÁRIO responde por danos ao imóvel, móveis e utensílios; a caução cobre esses danos e o saldo é devolvido após a vistoria de saída.\n"
                    + "5. É proibido sublocar, ceder ou emprestar o imóvel. Respeitar o regulamento do condomínio, o silêncio e a vizinhança.\n"
                    + "6. Cancelamento pelo LOCATÁRIO: conforme combinado na reserva; na falta de acordo, a imobiliária pode reter o sinal pago.\n"
                    + "7. Fica eleito o foro da comarca do imóvel.");
                if (c.Regras is { } regras)
                {
                    RelatoriosService.Secao(col, "Regras da casa");
                    RelatoriosService.Paragrafo(col, regras);
                }
                RelatoriosService.Paragrafo(col, $"\n\n_____________________________________________\nLocatário: {r.HospedeNome}"
                    + $"\n\n_____________________________________________\nLocador / Inova Bens · {Texto.Data(DatasBR.Hoje())}");
            });
        audit.Registrar(db, u.Id, "temporada_reservas", r.Id, "EXPORTAR", depois: new { documento = "contrato-temporada", formato = "pdf" });
        await db.SaveChangesAsync();
        return new Arquivo(pdf, $"contrato-temporada-{r.Codigo}.pdf", RelatoriosService.TipoPdf);
    }

    /// <summary>Acerto da reserva para o proprietário: diárias, comissão, taxa da plataforma e repasse.</summary>
    public async Task<Arquivo> AcertoPdfAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var r = await Reserva(db, u, id, rastrear: false);
        var d = await Detalhe(db, r);
        var a = d.Acerto;
        var pdf = RelatoriosService.NovoPdf("Acerto de temporada", $"{r.Codigo} · {d.ImovelCodigo}",
            $"Inova Bens · emitido em {DatasBR.Agora():dd/MM/yyyy HH:mm}", col =>
            {
                RelatoriosService.Campos(col, 3, ("Hóspede", r.HospedeNome), ("Período", $"{Texto.Data(r.Entrada)} a {Texto.Data(r.Saida)}"),
                    ("Origem", Texto.Rotulo(r.Origem)));
                RelatoriosService.Secao(col, "Cálculo");
                RelatoriosService.Tabela(col, [("Item", 3), ("Valor", 1)],
                [
                    [$"Diárias ({r.Noites} noite(s))", Texto.Moeda(r.Diarias)],
                    ["Desconto", "− " + Texto.Moeda(r.Desconto)],
                    [$"Comissão da administração ({r.ComissaoPct:0.##}%)", "− " + Texto.Moeda(a.Comissao)],
                    ["Taxa da plataforma", "− " + Texto.Moeda(a.TaxaPlataforma)],
                    ["Caução retida (danos)", Texto.Moeda(a.CaucaoRetida)],
                    ["Repasse", Texto.Moeda(a.Repasse)],
                ], "—", alinharUltimaDireita: true);
                if (a.Repasses.Count > 0)
                {
                    RelatoriosService.Secao(col, "Repasse por proprietário");
                    RelatoriosService.Tabela(col, [("Proprietário", 3), ("%", 1), ("Valor", 1)],
                        [.. a.Repasses.Select(p => new[] { p.Nome, p.Percentual.ToString("0.##"), Texto.Moeda(p.Valor) })], "—", colunasDireita: 1);
                }
                RelatoriosService.Paragrafo(col, "Taxa de limpeza (paga à equipe de limpeza) e caução devolvida ao hóspede não entram no repasse.");
            });
        audit.Registrar(db, u.Id, "temporada_reservas", r.Id, "EXPORTAR", depois: new { documento = "acerto-temporada", formato = "pdf" });
        await db.SaveChangesAsync();
        return new Arquivo(pdf, $"acerto-{r.Codigo}.pdf", RelatoriosService.TipoPdf);
    }

    /* ------------------------------- site ------------------------------- */

    /// <summary>Imóvel de temporada ativo e publicado no site (null: não está).</summary>
    public async Task<TemporadaPublica?> PublicaAsync(Guid imovelId, ImovelDetalhe detalhe, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await db.TemporadaImoveis.AsNoTracking().SingleOrDefaultAsync(x => x.ImovelId == imovelId && x.Ativo, ct);
        if (c is null) return null;
        var hoje = DatasBR.Hoje();
        var ate = hoje.AddMonths(6);
        var ocupadas = await Conflitos(db, imovelId, hoje, ate, null).Select(r => new { r.Entrada, r.Saida }).ToListAsync(ct);
        var dias = new HashSet<DateOnly>();
        foreach (var o in ocupadas)
            for (var d = o.Entrada < hoje ? hoje : o.Entrada; d < o.Saida; d = d.AddDays(1)) dias.Add(d);
        var im = detalhe.Dados;
        return new TemporadaPublica(imovelId, im.Codigo, im.Titulo, im.Cidade, c.Diaria, c.DiariaFimSemana, c.MinimoNoites, c.HospedesMax,
            c.TaxaLimpeza, c.Caucao, c.Checkin, c.Checkout, c.Regras, dias);
    }

    public async Task<bool> AtivoAsync(Guid imovelId, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        return await db.TemporadaImoveis.AnyAsync(x => x.ImovelId == imovelId && x.Ativo, ct);
    }

    /// <summary>Pedido do visitante: vira reserva PEDIDO (não bloqueia datas) e avisa a equipe no sino. Devolve o código.</summary>
    public async Task<string?> PedidoAsync(Guid imovelId, PedidoReservaForm form, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(form.Site)) return "ok"; // robô: finge que deu certo
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await db.TemporadaImoveis.AsNoTracking().Include(x => x.Imovel).SingleOrDefaultAsync(x => x.ImovelId == imovelId && x.Ativo, ct);
        if (c is null) return null;
        var hoje = DatasBR.Hoje();
        var entrada = form.Entrada!.Value;
        var saida = form.Saida!.Value;
        if (entrada <= hoje) throw new RegraNegocioException(nameof(form.Entrada), "Escolha uma entrada a partir de amanhã.");
        if (entrada > hoje.AddMonths(18)) throw new RegraNegocioException(nameof(form.Entrada), "Reservas com até 18 meses de antecedência.");
        if (form.Hospedes > c.HospedesMax) throw new RegraNegocioException(nameof(form.Hospedes), $"O imóvel recebe até {c.HospedesMax} hóspede(s).");
        var cot = await Cotar(db, c, entrada, saida, null, ct);
        if (!cot.Disponivel) throw new RegraNegocioException(nameof(form.Entrada), cot.Motivo!.StartsWith("Datas ocupadas") ? "Essas datas não estão disponíveis." : cot.Motivo);
        // poucos pedidos abertos por telefone: evita encher a agenda com repetição
        var tel = Texto.Digitos(form.Telefone);
        if (await db.TemporadaReservas.CountAsync(r => r.Status == StatusReserva.PEDIDO && r.HospedeTelefone == tel && r.CriadoEm > DateTime.UtcNow.AddDays(-1), ct) >= 3)
            throw new RegraNegocioException("_", "Recebemos seus pedidos. Aguarde nosso contato.");
        var r = new TemporadaReserva
        {
            ImovelId = imovelId, Origem = OrigemReserva.SITE, Status = StatusReserva.PEDIDO, HospedeNome = form.Nome.Trim(), HospedeTelefone = tel,
            HospedeEmail = Texto.Limpo(form.Email), Hospedes = form.Hospedes, Entrada = entrada, Saida = saida, Diarias = cot.Diarias,
            Limpeza = c.TaxaLimpeza, Caucao = c.Caucao, ComissaoPct = c.ComissaoPct, Observacoes = Texto.Limpo(form.Mensagem),
        };
        db.TemporadaReservas.Add(r);
        audit.Registrar(db, null, "temporada_reservas", r.Id, "PEDIDO_SITE", depois: new { r.Entrada, r.Saida, r.Hospedes });
        await db.SaveChangesAsync(ct);
        await AlertarEquipe(db, r.Id, $"Pedido de reserva pelo site: {c.Imovel!.Codigo}, {entrada:dd/MM} a {saida:dd/MM/yyyy}, {r.HospedeNome}", ct);
        await db.SaveChangesAsync(ct);
        return TemporadaReserva.CodigoDe(await db.TemporadaReservas.Where(x => x.Id == r.Id).Select(x => x.Numero).SingleAsync(ct));
    }

    private static async Task AlertarEquipe(InovabensDb db, Guid reservaId, string mensagem, CancellationToken ct)
    {
        var usuarios = await db.Usuarios.Where(x => x.Ativo && x.ExcluidoEm == null
                && db.PerfilPermissoes.Any(pp => pp.PerfilId == x.PerfilId && pp.Permissao!.Codigo == P.LocacaoEditar))
            .Select(x => x.Id).ToListAsync(ct);
        foreach (var uid in usuarios)
            db.Alertas.Add(new Alerta { Id = Guid.NewGuid(), Tipo = TipoAlerta.TEMPORADA, UsuarioId = uid, TemporadaReservaId = reservaId, Mensagem = mensagem });
    }

    /* ------------------------------- iCal ------------------------------- */

    /// <summary>Calendário do imóvel para o Airbnb/Booking (datas ocupadas, sem dados do hóspede). Null: link inválido.</summary>
    public async Task<string?> IcalAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await db.TemporadaImoveis.AsNoTracking().SingleOrDefaultAsync(x => x.IcalToken == token, ct);
        if (c is null) return null;
        var desde = DatasBR.Hoje().AddDays(-30);
        var reservas = await db.TemporadaReservas.AsNoTracking().Where(r => r.ImovelId == c.ImovelId && r.Saida >= desde
            && r.Status != StatusReserva.PEDIDO && r.Status != StatusReserva.CANCELADA).OrderBy(r => r.Entrada).ToListAsync(ct);
        var sb = new StringBuilder();
        void L(string s) => sb.Append(s).Append("\r\n");
        L("BEGIN:VCALENDAR");
        L("VERSION:2.0");
        L("PRODID:-//Inova Bens//Temporada//PT-BR");
        L("CALSCALE:GREGORIAN");
        L("METHOD:PUBLISH");
        var agora = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        foreach (var r in reservas)
        {
            L("BEGIN:VEVENT");
            L($"UID:{r.Id}@inovabens");
            L($"DTSTAMP:{agora}");
            L($"DTSTART;VALUE=DATE:{r.Entrada:yyyyMMdd}");
            L($"DTEND;VALUE=DATE:{r.Saida:yyyyMMdd}");
            L("SUMMARY:Reservado");
            L("END:VEVENT");
        }
        L("END:VCALENDAR");
        return sb.ToString();
    }

    public sealed record EventoIcal(string Uid, DateOnly Inicio, DateOnly Fim, string? Resumo);

    /// <summary>Lê os eventos de dia inteiro de um .ics (linhas dobradas desfeitas; datas com ou sem hora).</summary>
    internal static List<EventoIcal> LerIcal(string texto)
    {
        var linhas = new List<string>();
        foreach (var bruta in texto.Replace("\r\n", "\n").Split('\n'))
        {
            if ((bruta.StartsWith(' ') || bruta.StartsWith('\t')) && linhas.Count > 0) linhas[^1] += bruta[1..];
            else linhas.Add(bruta);
        }
        var eventos = new List<EventoIcal>();
        string? uid = null, resumo = null;
        DateOnly? ini = null, fim = null;
        var dentro = false;
        foreach (var l in linhas)
        {
            if (l == "BEGIN:VEVENT") { dentro = true; uid = resumo = null; ini = fim = null; continue; }
            if (l == "END:VEVENT")
            {
                if (dentro && uid is not null && ini is { } i)
                {
                    var f = fim is { } x && x > i ? x : i.AddDays(1);
                    eventos.Add(new EventoIcal(uid.Length > 300 ? uid[..300] : uid, i, f, resumo));
                }
                dentro = false;
                continue;
            }
            if (!dentro) continue;
            var dois = l.IndexOf(':');
            if (dois < 0) continue;
            var nome = l[..dois].Split(';')[0].ToUpperInvariant();
            var valor = l[(dois + 1)..].Trim();
            switch (nome)
            {
                case "UID": uid = valor; break;
                case "SUMMARY": resumo = valor.Replace("\\,", ",").Replace("\\n", " "); break;
                case "DTSTART": ini = Data(valor); break;
                case "DTEND": fim = Data(valor); break;
            }
        }
        return eventos;

        static DateOnly? Data(string v) =>
            v.Length >= 8 && DateOnly.TryParseExact(v[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>Rotina: lê os calendários das plataformas de todos os imóveis ativos.</summary>
    public async Task<int> SincronizarTodosAsync(CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var ids = await db.TemporadaCalendarios.Where(c => db.TemporadaImoveis.Any(t => t.ImovelId == c.ImovelId && t.Ativo)).Select(c => c.Id).ToListAsync(ct);
        var n = 0;
        foreach (var id in ids)
        {
            try { n += await SincronizarAsync(id, null, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Calendário de temporada {Id}", id); }
        }
        return n;
    }

    /// <summary>Sincronização manual (botão na tela).</summary>
    public async Task<int> SincronizarAsync(UsuarioAtual u, Guid calendarioId, CancellationToken ct = default)
    {
        u.Exigir(P.LocacaoEditar);
        await using (var db = await fabrica.CreateDbContextAsync())
            if (!await db.TemporadaCalendarios.AnyAsync(c => c.Id == calendarioId && db.Imoveis.VisiveisPara(u).Any(i => i.Id == c.ImovelId), ct))
                throw new NaoEncontradoException("Calendário não encontrado.");
        return await SincronizarAsync(calendarioId, u.Id, ct);
    }

    /// <summary>Busca o .ics e aplica: evento novo vira reserva (valores a lançar), mudança de data atualiza, evento sumido cancela.</summary>
    internal async Task<int> SincronizarAsync(Guid calendarioId, Guid? usuario, CancellationToken ct, string? conteudo = null)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var cal = await db.TemporadaCalendarios.SingleAsync(c => c.Id == calendarioId, ct);
        try { conteudo ??= await Baixar(cal.Url, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            cal.Erro = ("Falha ao ler o calendário: " + e.Message) is var m && m.Length > 500 ? m[..500] : m;
            await db.SaveChangesAsync(ct);
            return 0;
        }
        var c = await db.TemporadaImoveis.Include(x => x.Imovel).SingleAsync(x => x.ImovelId == cal.ImovelId, ct);
        var hoje = DatasBR.Hoje();
        var eventos = LerIcal(conteudo).Where(e => e.Fim > hoje && e.Fim.DayNumber - e.Inicio.DayNumber <= 90)
            .GroupBy(e => e.Uid).Select(g => g.First()).ToList();
        var existentes = await db.TemporadaReservas.Where(r => r.CalendarioId == cal.Id).ToListAsync(ct);
        var origem = cal.Origem switch { OrigemCalendario.AIRBNB => OrigemReserva.AIRBNB, OrigemCalendario.BOOKING => OrigemReserva.BOOKING, _ => OrigemReserva.OUTRA };
        var nome = cal.Origem switch { OrigemCalendario.AIRBNB => "Airbnb", OrigemCalendario.BOOKING => "Booking", _ => "outra plataforma" };
        var mudancas = 0;
        foreach (var e in eventos)
        {
            var r = existentes.FirstOrDefault(x => x.ExternoUid == e.Uid);
            if (r is null)
            {
                r = new TemporadaReserva
                {
                    ImovelId = c.ImovelId, Origem = origem, Status = StatusReserva.CONFIRMADA, CalendarioId = cal.Id, ExternoUid = e.Uid,
                    HospedeNome = $"Hóspede {nome}", Entrada = e.Inicio, Saida = e.Fim, ComissaoPct = c.ComissaoPct, CriadoPor = usuario,
                    Observacoes = $"Importada do calendário {nome}{(e.Resumo is { } s ? $" ({s})" : "")}. Lance os valores informados pela plataforma.",
                };
                db.TemporadaReservas.Add(r);
                audit.Registrar(db, usuario, "temporada_reservas", r.Id, "IMPORTAR_ICAL", depois: new { r.Entrada, r.Saida, nome });
                if (await Conflitos(db, c.ImovelId, e.Inicio, e.Fim, r.Id).Select(x => x.Numero).FirstOrDefaultAsync(ct) is > 0 and var n)
                    await AlertarEquipe(db, r.Id, $"Conflito de datas no {c.Imovel!.Codigo}: reserva do {nome} ({e.Inicio:dd/MM} a {e.Fim:dd/MM}) bate com {TemporadaReserva.CodigoDe(n)}", ct);
                else await AlertarEquipe(db, r.Id, $"Nova reserva do {nome} no {c.Imovel!.Codigo}: {e.Inicio:dd/MM} a {e.Fim:dd/MM/yyyy}", ct);
                mudancas++;
            }
            else if (r.Status is StatusReserva.CONFIRMADA or StatusReserva.CANCELADA && (r.Entrada != e.Inicio || r.Saida != e.Fim || r.Status == StatusReserva.CANCELADA))
            {
                var antes = new { r.Entrada, r.Saida, r.Status };
                r.Entrada = e.Inicio;
                r.Saida = e.Fim;
                if (r.Status == StatusReserva.CANCELADA) { r.Status = StatusReserva.CONFIRMADA; r.CanceladaEm = null; r.MotivoCancelamento = null; }
                r.AtualizadoEm = DateTime.UtcNow;
                audit.Registrar(db, usuario, "temporada_reservas", r.Id, "ATUALIZAR_ICAL", antes, new { r.Entrada, r.Saida, r.Status });
                mudancas++;
            }
        }
        // sumiu do calendário (cancelada na plataforma) e ainda não começou: cancela, se não houver recebimento
        var uids = eventos.Select(e => e.Uid).ToHashSet();
        foreach (var r in existentes.Where(x => x.Status == StatusReserva.CONFIRMADA && x.Entrada > hoje && !uids.Contains(x.ExternoUid!)))
        {
            if (await db.TemporadaPagamentos.AnyAsync(p => p.ReservaId == r.Id && p.EstornadoEm == null, ct)) continue;
            r.Status = StatusReserva.CANCELADA;
            r.CanceladaEm = DateTime.UtcNow;
            r.MotivoCancelamento = $"Removida do calendário {nome}.";
            audit.Registrar(db, usuario, "temporada_reservas", r.Id, "CANCELAR_ICAL");
            await AlertarEquipe(db, r.Id, $"Reserva {r.Codigo} do {nome} cancelada na plataforma ({c.Imovel!.Codigo}, {r.Entrada:dd/MM})", ct);
            mudancas++;
        }
        cal.UltimaSincronizacao = DateTime.UtcNow;
        cal.Erro = null;
        await db.SaveChangesAsync(ct);
        return mudancas;
    }

    private static async Task<string> Baixar(string url, CancellationToken ct)
    {
        if (!UrlCalendarioValida(url)) throw new InvalidDataException("endereço não aceito.");
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[2_000_001];
        var lidos = 0;
        int n;
        while ((n = await s.ReadAsync(buffer.AsMemory(lidos, buffer.Length - lidos), ct)) > 0)
        {
            lidos += n;
            if (lidos >= buffer.Length) throw new InvalidDataException("calendário maior que 2 MB.");
        }
        var texto = Encoding.UTF8.GetString(buffer, 0, lidos);
        if (!texto.Contains("BEGIN:VCALENDAR")) throw new InvalidDataException("o endereço não devolveu um calendário (.ics).");
        return texto;
    }
}
