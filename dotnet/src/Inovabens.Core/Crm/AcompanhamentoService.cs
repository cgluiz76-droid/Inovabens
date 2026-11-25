using System.Globalization;
using ClosedXML.Excel;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Relatorios;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Crm;

/// <summary>Filtros do painel de acompanhamento do CRM (os mesmos valem para a tela e para o Excel).</summary>
public sealed class FiltroAcompanhamento
{
    public Guid? ImovelId { get; set; }
    public Guid? ResponsavelId { get; set; }
    /// <summary>Só a fila aberta (leads sem responsável).</summary>
    public bool SemResponsavel { get; set; }
    /// <summary>Parte do nome, telefone ou e-mail da pessoa.</summary>
    public string? Pessoa { get; set; }
    public Funil? Funil { get; set; }
    public Guid? EstagioId { get; set; }
    /// <summary>Aberto, ganho ou perdido (tipo do estágio atual). Vazio = todos.</summary>
    public TipoEstagio? Status { get; set; }
    /// <summary>Período de entrada do lead (datas de Brasília, inclusivas).</summary>
    public DateOnly? De { get; set; }
    public DateOnly? Ate { get; set; }
    public string? Origem { get; set; }
    /// <summary>Lead aberto parado na mesma etapa há pelo menos N dias.</summary>
    public int? ParadoDias { get; set; }
    public bool SemProximoPasso { get; set; }
    public bool AtividadeVencida { get; set; }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Query string (sem "?") com os filtros preenchidos: link da tela e do Excel.</summary>
    public string ParaConsulta()
    {
        var p = new List<string>();
        void Add(string k, object? v) { if (v is not null && v.ToString() is { Length: > 0 } s) p.Add($"{k}={Uri.EscapeDataString(s)}"); }
        Add("imovel", ImovelId);
        Add("responsavel", ResponsavelId);
        if (SemResponsavel) Add("fila", "1");
        Add("pessoa", Texto.Limpo(Pessoa));
        Add("funil", Funil);
        Add("estagio", EstagioId);
        Add("status", Status);
        Add("de", De?.ToString("yyyy-MM-dd", Inv));
        Add("ate", Ate?.ToString("yyyy-MM-dd", Inv));
        Add("origem", Texto.Limpo(Origem));
        Add("parado", ParadoDias);
        if (SemProximoPasso) Add("sempasso", "1");
        if (AtividadeVencida) Add("vencida", "1");
        return string.Join("&", p);
    }

    /// <summary>Lê os filtros da query string (valores inválidos são ignorados).</summary>
    public static FiltroAcompanhamento Ler(Func<string, string?> valor)
    {
        static Guid? G(string? s) => Guid.TryParse(s, out var g) ? g : null;
        static DateOnly? D(string? s) => DateOnly.TryParseExact(s, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d) ? d : null;
        return new FiltroAcompanhamento
        {
            ImovelId = G(valor("imovel")),
            ResponsavelId = G(valor("responsavel")),
            SemResponsavel = valor("fila") == "1",
            Pessoa = Texto.Limpo(valor("pessoa")),
            Funil = Enum.TryParse<Funil>(valor("funil"), true, out var f) ? f : null,
            EstagioId = G(valor("estagio")),
            Status = Enum.TryParse<TipoEstagio>(valor("status"), true, out var t) ? t : null,
            De = D(valor("de")),
            Ate = D(valor("ate")),
            Origem = Texto.Limpo(valor("origem")),
            ParadoDias = int.TryParse(valor("parado"), out var n) && n > 0 ? n : null,
            SemProximoPasso = valor("sempasso") == "1",
            AtividadeVencida = valor("vencida") == "1",
        };
    }
}

/// <summary>Uma linha do painel: o lead num imóvel (lead com dois imóveis aparece nos dois; sem imóvel, no grupo "Sem imóvel").</summary>
public sealed record LinhaAcompanhamento(
    Guid LeadId, Guid? ImovelId, string? ImovelCodigo, string? ImovelTitulo, bool SoSugerido,
    string Pessoa, string? Telefone, Funil Funil, string Estagio, string? EstagioCor, TipoEstagio Tipo, int DiasNaEtapa,
    Guid? ResponsavelId, string? Responsavel, string? Origem, DateTime? ProximoEm, string? ProximoTitulo,
    int AtividadesVencidas, decimal? ValorProposta, StatusProposta? StatusProposta, DateTime CriadoEm)
{
    public SituacaoProximoPasso Situacao => LeadCartao.SituacaoDe(ProximoEm);
}

/// <summary>Etapa do funil: leads hoje, quantos já passaram por ela, conversão para a seguinte e tempo médio.</summary>
public sealed record EtapaIndicador(Funil Funil, string Nome, string? Cor, TipoEstagio Tipo, int Atuais, int Passaram,
    decimal? ConversaoProxima, decimal? DiasMedios);

public sealed record UsuarioIndicador(Guid? Id, string Nome, int Abertos, int AtividadesVencidas, int SemProximoPasso, int Ganhos, int Perdidos);

public sealed record Acompanhamento(IReadOnlyList<LinhaAcompanhamento> Linhas, IReadOnlyList<EtapaIndicador> Etapas,
    IReadOnlyList<UsuarioIndicador> Usuarios, int Leads)
{
    /// <summary>Linhas agrupadas por imóvel (os sem imóvel por último).</summary>
    public IEnumerable<IGrouping<Guid?, LinhaAcompanhamento>> PorImovel =>
        Linhas.GroupBy(l => l.ImovelId).OrderBy(g => g.Key is null).ThenBy(g => g.First().ImovelCodigo);
}

/// <summary>
/// Painel de acompanhamento do CRM: com quem está cada imóvel e em que etapa, com filtros e indicadores.
/// Só leitura. O Corretor vê só os leads dele (mesmo escopo do funil, sem a fila aberta).
/// </summary>
public sealed class AcompanhamentoService(IDbContextFactory<InovabensDb> fabrica, AuditService audit)
{
    public async Task<Acompanhamento> ConsultarAsync(UsuarioAtual u, FiltroAcompanhamento f)
    {
        u.Exigir(P.CrmVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var agora = DateTime.UtcNow;

        var q = db.Leads.AsNoTracking().Where(l => l.ExcluidoEm == null).VisiveisPara(u);
        if (f.SemResponsavel) q = q.Where(l => l.ResponsavelId == null);
        else if (f.ResponsavelId is { } r) q = q.Where(l => l.ResponsavelId == r);
        if (f.Funil is { } funil) q = q.Where(l => l.Estagio!.Funil == funil);
        if (f.EstagioId is { } estagioId) q = q.Where(l => l.EstagioId == estagioId);
        if (f.Status is { } st) q = q.Where(l => l.Estagio!.Tipo == st);
        if (f.Origem is { } origem) q = q.Where(l => l.Origem == origem);
        if (f.De is { } de) { var d = DatasBR.ParaUtc(de.ToDateTime(TimeOnly.MinValue)); q = q.Where(l => l.CriadoEm >= d); }
        if (f.Ate is { } ate) { var a = DatasBR.ParaUtc(ate.AddDays(1).ToDateTime(TimeOnly.MinValue)); q = q.Where(l => l.CriadoEm < a); }
        if (f.ImovelId is { } imovel)
            q = q.Where(l => l.ImovelSugeridoId == imovel || db.LeadImoveisInteresse.Any(i => i.LeadId == l.Id && i.ImovelId == imovel));
        if (Texto.Limpo(f.Pessoa) is { } pessoa)
        {
            var like = Texto.LikeBusca(pessoa);
            var digitos = new string(pessoa.Where(char.IsAsciiDigit).ToArray());
            var likeFone = digitos.Length >= 4 ? $"%{digitos}%" : null;
            q = q.Where(l => EF.Functions.Like(FuncoesBusca.Normalizar(l.Pessoa!.NomeRazao), like, @"\") || EF.Functions.Like(FuncoesBusca.Normalizar(l.Pessoa.Email), like, @"\")
                             || (likeFone != null && (EF.Functions.Like(l.Pessoa.Telefone ?? "", likeFone) || EF.Functions.Like(l.Pessoa.Whatsapp ?? "", likeFone))));
        }

        var leads = await q
            .Select(l => new
            {
                l.Id, Nome = l.Pessoa!.NomeRazao, Telefone = l.Pessoa.Whatsapp ?? l.Pessoa.Telefone, l.Estagio!.Funil, l.EstagioId,
                Estagio = l.Estagio.Nome, l.Estagio.Cor, l.Estagio.Tipo, l.ResponsavelId,
                Responsavel = l.Responsavel != null ? l.Responsavel.Nome : null, l.Origem, l.CriadoEm, l.ProximaAcaoEm, l.ImovelSugeridoId,
                NaFase = db.LeadHistoricoEstagios.Where(h => h.LeadId == l.Id && h.EstagioNovoId == l.EstagioId).Max(h => (DateTime?)h.CriadoEm),
                Proximo = db.Compromissos.Where(c => c.LeadId == l.Id && c.Status == StatusCompromisso.PENDENTE && c.ExcluidoEm == null)
                    .OrderBy(c => c.InicioEm).Select(c => new { c.InicioEm, c.Titulo }).FirstOrDefault(),
                Vencidas = db.Compromissos.Count(c => c.LeadId == l.Id && c.Status == StatusCompromisso.PENDENTE && c.ExcluidoEm == null && c.InicioEm < agora),
                Proposta = db.Propostas.Where(p => p.LeadId == l.Id && (p.Status == StatusProposta.EM_ANALISE || p.Status == StatusProposta.ACEITA))
                    .OrderByDescending(p => p.Status == StatusProposta.ACEITA).ThenByDescending(p => p.CriadoEm)
                    .Select(p => new { p.ValorTotal, p.Status, p.ImovelId }).FirstOrDefault(),
                Imoveis = (from i in db.LeadImoveisInteresse
                           where i.LeadId == l.Id
                           join imv in db.Imoveis on i.ImovelId equals imv.Id
                           where imv.ExcluidoEm == null
                           select new { imv.Id, imv.Codigo, imv.Titulo }).ToList(),
                Sugerido = db.Imoveis.Where(i => i.Id == l.ImovelSugeridoId && i.ExcluidoEm == null)
                    .Select(i => new { i.Id, i.Codigo, i.Titulo }).FirstOrDefault(),
            })
            .ToListAsync();

        var hoje = DatasBR.Hoje().DayNumber;
        int Dias(DateTime utc) => Math.Max(0, hoje - DateOnly.FromDateTime(DatasBR.DeUtc(utc)).DayNumber);

        var filtrados = leads.Select(l => new
        {
            Lead = l,
            Dias = Dias(l.NaFase ?? l.CriadoEm),
            ProximoEm = l.Proximo?.InicioEm ?? l.ProximaAcaoEm,
        })
        .Where(x => f.ParadoDias is not { } n || (x.Lead.Tipo == TipoEstagio.ABERTO && x.Dias >= n))
        .Where(x => !f.SemProximoPasso || (x.Lead.Tipo == TipoEstagio.ABERTO && x.ProximoEm is null))
        .Where(x => !f.AtividadeVencida || x.Lead.Vencidas > 0)
        .ToList();

        var linhas = new List<LinhaAcompanhamento>();
        foreach (var x in filtrados)
        {
            var l = x.Lead;
            var imoveis = l.Imoveis.Select(i => (Id: (Guid?)i.Id, Codigo: (string?)i.Codigo, Titulo: (string?)i.Titulo, So: false)).ToList();
            if (l.Sugerido is { } s && imoveis.All(i => i.Id != s.Id)) imoveis.Add((s.Id, s.Codigo, s.Titulo, true));
            if (imoveis.Count == 0) imoveis.Add((null, null, null, false));
            foreach (var im in imoveis.Where(i => f.ImovelId is null || i.Id == f.ImovelId))
            {
                var proposta = l.Proposta is { } p && (p.ImovelId == im.Id || im.Id is null) ? p : null;
                linhas.Add(new LinhaAcompanhamento(l.Id, im.Id, im.Codigo, im.Titulo, im.So, l.Nome, l.Telefone, l.Funil, l.Estagio, l.Cor, l.Tipo,
                    x.Dias, l.ResponsavelId, l.Responsavel, l.Origem, x.ProximoEm, l.Proximo?.Titulo, l.Vencidas,
                    proposta?.ValorTotal, proposta?.Status, l.CriadoEm));
            }
        }
        linhas = [.. linhas.OrderBy(l => l.ImovelId is null).ThenBy(l => l.ImovelCodigo).ThenBy(l => l.Funil).ThenBy(l => l.Pessoa)];

        // --- indicadores (sobre os leads filtrados, cada lead uma vez) ---
        var ids = filtrados.Select(x => x.Lead.Id).ToList();
        var deLeads = Funis.DeLeads.ToList();
        var estagios = await db.FunilEstagios.AsNoTracking().Where(e => e.Ativo && deLeads.Contains(e.Funil) && (f.Funil == null || e.Funil == f.Funil))
            .OrderBy(e => e.Funil).ThenBy(e => e.Ordem).ToListAsync();
        var historico = ids.Count == 0 ? [] : await db.LeadHistoricoEstagios.AsNoTracking().Where(h => ids.Contains(h.LeadId))
            .OrderBy(h => h.CriadoEm).Select(h => new { h.LeadId, h.EstagioNovoId, h.CriadoEm }).ToListAsync();
        var atual = filtrados.ToDictionary(x => x.Lead.Id, x => x.Lead.EstagioId);

        var passaram = historico.GroupBy(h => h.EstagioNovoId).ToDictionary(g => g.Key, g => g.Select(h => h.LeadId).ToHashSet());
        foreach (var (lead, est) in atual)
        {
            if (!passaram.TryGetValue(est, out var set)) passaram[est] = set = [];
            set.Add(lead);
        }

        // tempo em cada passagem: da entrada até a entrada seguinte (ou até agora, se ainda está lá)
        var duracoes = new Dictionary<Guid, List<double>>();
        foreach (var g in historico.GroupBy(h => h.LeadId))
        {
            var passos = g.ToList();
            for (var i = 0; i < passos.Count; i++)
            {
                var fim = i + 1 < passos.Count ? passos[i + 1].CriadoEm : agora;
                if (i + 1 == passos.Count && atual.GetValueOrDefault(g.Key) != passos[i].EstagioNovoId) continue;
                if (!duracoes.TryGetValue(passos[i].EstagioNovoId, out var lista)) duracoes[passos[i].EstagioNovoId] = lista = [];
                lista.Add((fim - passos[i].CriadoEm).TotalDays);
            }
        }

        var etapas = new List<EtapaIndicador>();
        foreach (var porFunil in estagios.GroupBy(e => e.Funil))
        {
            var lista = porFunil.ToList();
            for (var i = 0; i < lista.Count; i++)
            {
                var e = lista[i];
                var n = passaram.GetValueOrDefault(e.Id)?.Count ?? 0;
                var proxima = e.Tipo == TipoEstagio.ABERTO ? lista.Skip(i + 1).FirstOrDefault(x => x.Tipo != TipoEstagio.PERDIDO) : null;
                decimal? conv = proxima is not null && n > 0
                    ? Math.Round(100m * (passaram.GetValueOrDefault(proxima.Id)?.Count(l => passaram[e.Id].Contains(l)) ?? 0) / n, 1) : null;
                decimal? dias = duracoes.TryGetValue(e.Id, out var d) && d.Count > 0 && e.Tipo == TipoEstagio.ABERTO ? Math.Round((decimal)d.Average(), 1) : null;
                etapas.Add(new EtapaIndicador(e.Funil, e.Nome, e.Cor, e.Tipo, atual.Values.Count(v => v == e.Id), n, conv, dias));
            }
        }

        var usuarios = filtrados.GroupBy(x => x.Lead.ResponsavelId)
            .Select(g => new UsuarioIndicador(g.Key, g.First().Lead.Responsavel ?? "Fila aberta (sem responsável)",
                g.Count(x => x.Lead.Tipo == TipoEstagio.ABERTO),
                g.Sum(x => x.Lead.Vencidas),
                g.Count(x => x.Lead.Tipo == TipoEstagio.ABERTO && x.ProximoEm is null),
                g.Count(x => x.Lead.Tipo == TipoEstagio.GANHO),
                g.Count(x => x.Lead.Tipo == TipoEstagio.PERDIDO)))
            .OrderByDescending(x => x.Ganhos).ThenByDescending(x => x.Abertos).ThenBy(x => x.Nome)
            .ToList();

        return new Acompanhamento(linhas, etapas, usuarios, filtrados.Count);
    }

    /// <summary>Origens usadas nos leads (lista do filtro).</summary>
    public async Task<IReadOnlyList<string>> OrigensAsync(UsuarioAtual u)
    {
        u.Exigir(P.CrmVer);
        await using var db = await fabrica.CreateDbContextAsync();
        return await db.Leads.AsNoTracking().Where(l => l.ExcluidoEm == null && l.Origem != null).VisiveisPara(u)
            .Select(l => l.Origem!).Distinct().OrderBy(o => o).ToListAsync();
    }

    /// <summary>Planilha com as mesmas linhas e indicadores da tela.</summary>
    public async Task<Arquivo> ExcelAsync(UsuarioAtual u, FiltroAcompanhamento f)
    {
        var a = await ConsultarAsync(u, f);
        using var wb = new XLWorkbook();
        static XLCellValue T(string? s) => s is null ? Blank.Value : s;
        static XLCellValue N(decimal? v) => v is { } x ? x : Blank.Value;
        static XLCellValue Dt(DateTime? utc) => utc is { } d ? DatasBR.DeUtc(d) : Blank.Value;

        var ws = RelatoriosService.Aba(wb, "Acompanhamento",
        [
            ("Imóvel", 12), ("Título do imóvel", 36), ("Pessoa", 28), ("Telefone", 16), ("Funil", 10), ("Etapa", 20), ("Dias na etapa", 10),
            ("Responsável", 22), ("Origem", 14), ("Próximo passo", 30), ("Quando", 17), ("Atividades vencidas", 11),
            ("Proposta", 14), ("Situação da proposta", 14), ("Entrada do lead", 17), ("Só sugerido", 9),
        ],
        a.Linhas.Select(l => new XLCellValue[]
        {
            T(l.ImovelCodigo ?? "Sem imóvel"), T(l.ImovelTitulo), l.Pessoa, T(l.Telefone), Funis.Nome(l.Funil), l.Estagio,
            l.DiasNaEtapa, T(l.Responsavel ?? "Fila aberta"), T(l.Origem), T(l.ProximoTitulo), Dt(l.ProximoEm), l.AtividadesVencidas,
            N(l.ValorProposta), T(l.StatusProposta?.ToString()), Dt(l.CriadoEm), l.SoSugerido ? "Sim" : "",
        }));
        ws.Column(11).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
        ws.Column(15).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
        ws.Column(13).Style.NumberFormat.Format = "#,##0.00";
        if (a.Linhas.Count > 0) ws.Range(1, 1, a.Linhas.Count + 1, 16).SetAutoFilter();

        RelatoriosService.Aba(wb, "Etapas",
            [("Funil", 10), ("Etapa", 22), ("Leads hoje", 11), ("Já passaram", 11), ("Conversão p/ próxima (%)", 14), ("Tempo médio (dias)", 13)],
            a.Etapas.Select(e => new XLCellValue[]
            {
                Funis.Nome(e.Funil), e.Nome, e.Atuais, e.Passaram, N(e.ConversaoProxima), N(e.DiasMedios),
            }));
        RelatoriosService.Aba(wb, "Usuários",
            [("Responsável", 28), ("Abertos", 10), ("Atividades vencidas", 12), ("Sem próximo passo", 12), ("Ganhos", 10), ("Perdidos", 10)],
            a.Usuarios.Select(x => new XLCellValue[] { x.Nome, x.Abertos, x.AtividadesVencidas, x.SemProximoPasso, x.Ganhos, x.Perdidos }));

        await using (var db = await fabrica.CreateDbContextAsync())
        {
            audit.Registrar(db, u.Id, "leads", null, "EXPORTAR", depois: new { relatorio = "acompanhamento", filtros = f.ParaConsulta() });
            await db.SaveChangesAsync();
        }
        return new Arquivo(RelatoriosService.Salvar(wb), $"acompanhamento-crm-{DatasBR.Hoje():yyyy-MM-dd}.xlsx", RelatoriosService.TipoXlsx);
    }
}
