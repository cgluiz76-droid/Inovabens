using ClosedXML.Excel;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Financeiro;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Relatorios;

public sealed record Arquivo(byte[] Conteudo, string Nome, string Tipo);

/// <summary>
/// Ficha do Imóvel e Ficha Financeira em PDF (QuestPDF) e Excel (ClosedXML) — port de relatorios.service.ts.
/// Toda exportação fica registrada na auditoria (ação EXPORTAR).
/// </summary>
public sealed class RelatoriosService(
    IDbContextFactory<InovabensDb> fabrica, ImoveisService imoveis, AquisicaoService aquisicao, DespesasService despesas,
    RetornoService retorno, IArmazenamento storage, AuditService audit)
{
    public const string TipoPdf = "application/pdf";
    public const string TipoXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string Moeda = "\"R$\" #,##0.00";
    private static readonly string Marca = "#1E3A8A", Suave = "#64748B", Linha = "#E2E8F0", Zebra = "#F8FAFC";

    // Licença Community do QuestPDF: gratuita para empresas com receita bruta anual abaixo de US$ 1 milhão.
    static RelatoriosService() => QuestPDF.Settings.License = LicenseType.Community;

    private static string Pct(double? v) => v is null ? "—" : (v.Value * 100).ToString("0.00", Texto.PtBr) + "%";
    private static string Endereco(Imovel i) => string.Join(" · ", new[]
    {
        string.Join(", ", new[] { i.Logradouro, i.Numero }.Where(x => !string.IsNullOrWhiteSpace(x))), i.Complemento, i.Bairro,
        string.Join("/", new[] { i.Cidade, i.Uf }.Where(x => !string.IsNullOrWhiteSpace(x))), i.Cep,
    }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } s ? s : "—";

    private async Task Auditar(UsuarioAtual u, Guid id, object o)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        audit.Registrar(db, u.Id, "imoveis", id, "EXPORTAR", depois: o);
        await db.SaveChangesAsync();
    }

    /* ============================ Ficha do Imóvel ============================ */

    private sealed record ContatoPessoa(Guid Id, string? CpfCnpj, string? Contato);

    private async Task<(ImovelDetalhe Im, IReadOnlyList<RegularizacaoDetalhe> Regs, Dictionary<Guid, ContatoPessoa> Pessoas)> DadosFicha(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.ImoveisExportar);
        var im = await imoveis.DetalheAsync(u, id);
        var regs = await imoveis.ListarRegularizacoesAsync(u, id);
        var ids = im.Proprietarios.Select(p => p.PessoaId).Concat(im.Envolvidos.Select(e => e.PessoaId)).Distinct().ToList();
        await using var db = await fabrica.CreateDbContextAsync();
        var pessoas = await db.Pessoas.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new ContatoPessoa(p.Id, p.CpfCnpj, p.Telefone ?? p.Email)).ToDictionaryAsync(p => p.Id);
        return (im, regs, pessoas);
    }

    public async Task<Arquivo> FichaImovelPdfAsync(UsuarioAtual u, Guid id)
    {
        var (im, regs, pessoas) = await DadosFicha(u, id);
        var d = im.Dados;
        var capa = im.Midias.FirstOrDefault(m => m.Midia.Capa && m.Midia.Tipo == TipoMidia.FOTO) ?? im.Midias.FirstOrDefault(m => m.Midia.Tipo == TipoMidia.FOTO);
        Image? foto = null;
        if (await storage.BaixarAsync(capa?.Midia.ArquivoChave) is { Dados: var bytes })
        {
            try { foto = Image.FromBinaryData(bytes); } catch { /* formato não suportado: segue sem foto */ }
        }

        var pdf = NovoPdf("Ficha do Imóvel", $"{d.Codigo} · {d.Titulo}", $"Ficha do Imóvel {d.Codigo} · gerada em {Texto.DataHora(DateTime.UtcNow)}", col =>
        {
            if (foto is not null) col.Item().AlignRight().Width(155).Height(105).Image(foto).FitArea();
            Secao(col, "Identificação");
            Campos(col, 3, ("Código", d.Codigo), ("Situação", Texto.Rotulo(d.Situacao)), ("Finalidade", Texto.Rotulo(d.Finalidade)),
                ("Tipo", string.Join(" / ", new[] { d.Tipo, d.Subtipo }.Where(x => !string.IsNullOrWhiteSpace(x)))),
                ("Responsável", im.CorretorNome), ("Divulgar no site", d.DivulgarSite ? "Sim" : "Não"));
            Secao(col, "Localização");
            Campos(col, 1, ("Endereço", Endereco(d)), ("Endereço público no site", d.EnderecoPublico ? "Sim" : "Não"));
            Secao(col, "Dimensões e composição");
            Campos(col, 4, ("Área total (m²)", d.AreaTotalM2?.ToString(Texto.PtBr)), ("Área construída (m²)", d.AreaConstruidaM2?.ToString(Texto.PtBr)),
                ("Área útil (m²)", d.AreaUtilM2?.ToString(Texto.PtBr)), ("Quartos", d.Quartos?.ToString()), ("Suítes", d.Suites?.ToString()),
                ("Banheiros", d.Banheiros?.ToString()), ("Vagas", d.Vagas?.ToString()));
            Secao(col, "Valores de divulgação");
            Campos(col, 2, ("Venda", Texto.Moeda(d.PrecoVenda)), ("Locação", Texto.Moeda(d.PrecoLocacao)));
            Secao(col, "Descrição pública");
            Paragrafo(col, d.DescricaoPublica);
            Secao(col, "Características");
            Tabela(col, [("Característica", 2), ("Valor", 2), ("Divulgação", 1)],
                [.. im.Caracteristicas.Select(c => new[] { c.Chave, c.Valor ?? "Sim", c.Publico ? "Pública" : "Interna" })]);
            Secao(col, "Dados internos (não divulgar)");
            Campos(col, 3, ("Matrícula", d.Matricula), ("Cartório", d.Cartorio), ("Inscrição municipal", d.InscricaoMunicipal));
            Paragrafo(col, d.DescricaoInterna);
            Secao(col, "Proprietários");
            Tabela(col, [("Nome", 3), ("CPF/CNPJ", 1.6f), ("Contato", 1.8f), ("%", 0.8f)],
                [.. im.Proprietarios.Select(p => new[] { p.PessoaNome, pessoas.GetValueOrDefault(p.PessoaId)?.CpfCnpj ?? "—",
                    pessoas.GetValueOrDefault(p.PessoaId)?.Contato ?? "—", p.Percentual.ToString("0.00", Texto.PtBr) })], alinharUltimaDireita: true);
            Secao(col, "Envolvidos (fiadores, parceiros, prestadores, terceiros)");
            Tabela(col, [("Nome", 3), ("Papel", 1.4f), ("Contato", 2.8f)],
                [.. im.Envolvidos.Select(e => new[] { e.PessoaNome, e.PapelNome, pessoas.GetValueOrDefault(e.PessoaId)?.Contato ?? "—" })]);
            Secao(col, "Regularização");
            Tabela(col, [("Tipo", 1.6f), ("Status", 1.2f), ("Profissional", 1.9f), ("Prazo", 0.9f), ("Custo est.", 1.4f)],
                [.. regs.Select(r => new[] { Texto.Rotulo(r.Dados.Tipo), Texto.Rotulo(r.Dados.Status), r.ProfissionalNome ?? "—",
                    Texto.Data(r.Dados.PrazoPrevisto), Texto.Moeda(r.Dados.CustoEstimado) })], alinharUltimaDireita: true);
            Secao(col, "Mídias");
            Paragrafo(col, $"{im.Midias.Count(m => m.Midia.Tipo == TipoMidia.FOTO)} foto(s) · {im.Midias.Count(m => m.Midia.Tipo != TipoMidia.FOTO)} outro(s) arquivo(s) (vídeos, plantas, documentos).");
        });
        await Auditar(u, id, new { relatorio = "ficha-imovel", formato = "pdf" });
        return new Arquivo(pdf, $"ficha-{d.Codigo}.pdf", TipoPdf);
    }

    public async Task<Arquivo> FichaImovelXlsxAsync(UsuarioAtual u, Guid id)
    {
        var (im, regs, pessoas) = await DadosFicha(u, id);
        var d = im.Dados;
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Ficha do Imóvel");
        ws.ShowGridLines = false;
        ws.Column(1).Width = 28; ws.Column(2).Width = 60; ws.Column(3).Width = 28; ws.Column(4).Width = 40;
        ws.Range("A1:D1").Merge().Value = $"FICHA DO IMÓVEL — {d.Codigo}";
        ws.Cell("A1").Style.Font.SetBold().Font.SetFontSize(14).Font.SetFontColor(XLColor.White).Fill.SetBackgroundColor(XLColor.FromHtml(Marca));
        var r = 2;
        void Titulo(string t)
        {
            r++;
            var c = ws.Cell(r, 1);
            c.Value = t.ToUpperInvariant();
            c.Style.Font.SetBold().Font.SetFontColor(XLColor.FromHtml(Marca));
            ws.Range(r, 1, r, 4).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        }
        IXLRow Linha(string a, XLCellValue b, string c = "", XLCellValue? dd = null)
        {
            r++;
            ws.Cell(r, 1).Value = a; ws.Cell(r, 2).Value = b; ws.Cell(r, 3).Value = c; ws.Cell(r, 4).Value = dd ?? "";
            ws.Cell(r, 1).Style.Font.Bold = true; ws.Cell(r, 3).Style.Font.Bold = true;
            return ws.Row(r);
        }
        Titulo("Identificação");
        Linha("Título", d.Titulo, "Código", d.Codigo);
        Linha("Situação", Texto.Rotulo(d.Situacao), "Finalidade", Texto.Rotulo(d.Finalidade));
        Linha("Tipo", string.Join(" / ", new[] { d.Tipo, d.Subtipo }.Where(x => !string.IsNullOrWhiteSpace(x))), "Responsável", im.CorretorNome ?? "—");
        Linha("Divulgar no site", d.DivulgarSite ? "Sim" : "Não");
        Titulo("Localização");
        Linha("Endereço", Endereco(d));
        Linha("Latitude/Longitude", d.Latitude is { } la && d.Longitude is { } lo ? $"{la.ToString(Texto.PtBr)}, {lo.ToString(Texto.PtBr)}" : "—");
        Titulo("Dimensões");
        Linha("Área total (m²)", V(d.AreaTotalM2), "Quartos", V(d.Quartos));
        Linha("Área construída (m²)", V(d.AreaConstruidaM2), "Suítes", V(d.Suites));
        Linha("Área útil (m²)", V(d.AreaUtilM2), "Banheiros", V(d.Banheiros));
        Linha("", "", "Vagas", V(d.Vagas));
        Titulo("Valores de divulgação");
        var v = Linha("Venda", V(d.PrecoVenda), "Locação", V(d.PrecoLocacao));
        foreach (var c in new[] { 2, 4 }) { v.Cell(c).Style.NumberFormat.Format = Moeda; v.Cell(c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left; }
        Titulo("Descrição pública");
        r++; ws.Range(r, 1, r, 4).Merge().Value = d.DescricaoPublica ?? "—";
        ws.Row(r).Height = 80; ws.Range(r, 1, r, 4).Style.Alignment.SetWrapText().Alignment.SetVertical(XLAlignmentVerticalValues.Top);
        Titulo("Dados internos");
        Linha("Matrícula", d.Matricula ?? "—", "Cartório", d.Cartorio ?? "—");
        Linha("Inscrição municipal", d.InscricaoMunicipal ?? "—");
        r++; ws.Range(r, 1, r, 4).Merge().Value = d.DescricaoInterna ?? "—";
        ws.Row(r).Height = 60; ws.Range(r, 1, r, 4).Style.Alignment.SetWrapText().Alignment.SetVertical(XLAlignmentVerticalValues.Top);

        Aba(wb, "Características", [("Característica", 32), ("Valor", 40), ("Divulgação", 14)],
            im.Caracteristicas.Select(c => new XLCellValue[] { c.Chave, c.Valor ?? "Sim", c.Publico ? "Pública" : "Interna" }));
        Aba(wb, "Pessoas", [("Vínculo", 16), ("Nome", 40), ("CPF/CNPJ", 20), ("Contato", 30), ("%", 8)],
            im.Proprietarios.Select(p => new XLCellValue[] { "Proprietário", p.PessoaNome, pessoas.GetValueOrDefault(p.PessoaId)?.CpfCnpj ?? "",
                    pessoas.GetValueOrDefault(p.PessoaId)?.Contato ?? "", p.Percentual })
                .Concat(im.Envolvidos.Select(e => new XLCellValue[] { e.PapelNome, e.PessoaNome, "", pessoas.GetValueOrDefault(e.PessoaId)?.Contato ?? "", "" })));
        var sr = Aba(wb, "Regularização", [("Tipo", 22), ("Status", 16), ("Profissional", 28), ("Prazo", 12), ("Custo estimado", 18), ("Descrição", 50)],
            regs.Select(x => new XLCellValue[] { Texto.Rotulo(x.Dados.Tipo), Texto.Rotulo(x.Dados.Status), x.ProfissionalNome ?? "",
                Texto.Data(x.Dados.PrazoPrevisto), V(x.Dados.CustoEstimado), x.Dados.Descricao ?? "" }));
        sr.Column(5).Style.NumberFormat.Format = Moeda;

        await Auditar(u, id, new { relatorio = "ficha-imovel", formato = "xlsx" });
        return new Arquivo(Salvar(wb), $"ficha-{d.Codigo}.xlsx", TipoXlsx);
    }

    /* =========================== Ficha Financeira =========================== */

    private async Task<(ImovelDetalhe Im, ResultadoRetorno Ret, IReadOnlyList<LancamentoHistorico> Lanc, AquisicaoDetalhe? Aq)> DadosFinanceiros(UsuarioAtual u, Guid id, CodigoIndice indice)
    {
        u.Exigir(P.FinanceiroExportar);
        var im = await imoveis.DetalheAsync(u, id);
        var ret = await retorno.CalcularAsync(u, id, indice);
        var lanc = await despesas.HistoricoAsync(u, id);
        var aq = await aquisicao.ObterAsync(u, id);
        return (im, ret, lanc, aq);
    }

    private static string StatusTexto(StatusLancamento? s) => s switch
    {
        StatusLancamento.PAGO => "Pago", StatusLancamento.NAO_PAGO => "Não pago", StatusLancamento.NAO_SE_APLICA => "Não se aplica", _ => "A marcar",
    };

    public async Task<Arquivo> FichaFinanceiraPdfAsync(UsuarioAtual u, Guid id, CodigoIndice indice)
    {
        var (im, ret, lanc, aq) = await DadosFinanceiros(u, id, indice);
        var d = im.Dados;
        var pdf = NovoPdf("Ficha Financeira", $"{d.Codigo} · {d.Titulo}", $"Ficha Financeira {d.Codigo} · gerada em {Texto.DataHora(DateTime.UtcNow)}", col =>
        {
            Secao(col, "Resumo do investimento");
            Campos(col, 3,
                ("Custo de aquisição", Texto.Moeda(ret.CustoAquisicao)), ("Custo total investido", Texto.Moeda(ret.CustoTotal)),
                ("Passivo pendente (não pago)", Texto.Moeda(ret.PassivoPendente)), ("Receita líquida", Texto.Moeda(ret.ReceitaLiquida)),
                ("Resultado", Texto.Moeda(ret.Resultado)), ($"Custo corrigido ({ret.Indice})", Texto.Moeda(ret.CustoCorrigido)),
                ("ROI nominal", Pct(ret.Roi)), ("ROI real (acima do índice)", Pct(ret.RoiReal)), ("TIR anualizada", Pct(ret.TirAnual)),
                ("MOIC", ret.Moic is { } m ? m.ToString("0.00", Texto.PtBr) + "×" : "—"), ("Prazo (dias)", ret.PrazoDias?.ToString()),
                ("Data de corte", Texto.Data(ret.DataFinal)));
            foreach (var a in ret.Avisos) col.Item().Text($"• {a}").Italic().FontSize(8.5f).FontColor(Suave);
            Secao(col, "Aquisição (CAPEX)");
            if (aq is null) Paragrafo(col, "Aquisição não cadastrada.");
            else
            {
                var x = aq.Dados;
                Campos(col, 3, ("Data", Texto.Data(x.DataAquisicao)), ("Valor pago", Texto.Moeda(x.ValorPago)), ("Impostos", Texto.Moeda(x.Impostos)),
                    ("Taxas", Texto.Moeda(x.Taxas)), ("Despesas diversas", Texto.Moeda(x.DespesasDiversas)), ("TOTAL DE AQUISIÇÃO", Texto.Moeda(x.TotalAquisicao)));
                Tabela(col, [("Tipo", 1), ("Descrição", 3.3f), ("Data", 1), ("Valor", 2)],
                    [.. aq.Itens.Select(i => new[] { Texto.Rotulo(i.Tipo), i.Descricao, Texto.Data(i.DataPagamento), Texto.Moeda(i.Valor) })],
                    "Sem itens detalhados.", alinharUltimaDireita: true);
            }
            Secao(col, "Despesas e status de pagamento (OPEX)");
            Tabela(col, [("Competência", 0.9f), ("Categoria", 1.3f), ("Descrição", 1.7f), ("Venc.", 0.85f), ("Valor", 1), ("Status", 0.85f), ("Justificativa", 1.3f)],
                [.. lanc.Select(l => new[] { l.Dados.Competencia.ToString("MM/yyyy"), l.Categoria, l.Descricao, Texto.Data(l.Dados.Vencimento),
                    Texto.Moeda(l.Dados.Valor), StatusTexto(l.Dados.Status), l.Dados.Justificativa ?? "" })]);
            Secao(col, "Fluxo de caixa");
            Tabela(col, [("Data", 0.9f), ("Tipo", 0.8f), ("Descrição", 4), ("Valor", 1.9f)],
                [.. ret.Fluxo.Select(f => new[] { Texto.Data(f.Data), f.Tipo == TipoMovimento.SAIDA ? "Saída" : "Entrada", f.Descricao,
                    (f.Tipo == TipoMovimento.SAIDA ? "-" : "") + Texto.Moeda(f.Valor) })], alinharUltimaDireita: true);
        });
        await Auditar(u, id, new { relatorio = "ficha-financeira", formato = "pdf", indice = indice.ToString() });
        return new Arquivo(pdf, $"ficha-financeira-{d.Codigo}.pdf", TipoPdf);
    }

    public async Task<Arquivo> FichaFinanceiraXlsxAsync(UsuarioAtual u, Guid id, CodigoIndice indice)
    {
        var (im, ret, lanc, aq) = await DadosFinanceiros(u, id, indice);
        var d = im.Dados;
        using var wb = new XLWorkbook();

        var linhasResumo = new List<(string K, XLCellValue V, string? Fmt)>
        {
            ("Imóvel", $"{d.Codigo} — {d.Titulo}", null), ("Data de corte", Texto.Data(ret.DataFinal), null), ("Índice de correção", ret.Indice.ToString(), null),
            ("Custo de aquisição", ret.CustoAquisicao, Moeda), ("Custo total investido", ret.CustoTotal, Moeda),
            ("Passivo pendente (não pago)", ret.PassivoPendente, Moeda), ("Custo corrigido", V(ret.CustoCorrigido), Moeda),
            ("Receita líquida", ret.ReceitaLiquida, Moeda), ("Resultado", ret.Resultado, Moeda),
            ("ROI nominal", V(ret.Roi), "0.00%"), ("ROI real", V(ret.RoiReal), "0.00%"), ("TIR anualizada", V(ret.TirAnual), "0.00%"),
            ("MOIC", V(ret.Moic), "0.00\"×\""), ("Prazo (dias)", V(ret.PrazoDias), null),
        };
        linhasResumo.AddRange(ret.Avisos.Select(a => ("Aviso", (XLCellValue)a, (string?)null)));
        var rs = Aba(wb, "Resumo", [("Indicador", 38), ("Valor", 24)], linhasResumo.Select(x => new[] { x.K, x.V }));
        for (var i = 0; i < linhasResumo.Count; i++)
        {
            var c = rs.Cell(i + 2, 2);
            if (linhasResumo[i].Fmt is { } f) c.Style.NumberFormat.Format = f;
            c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }

        var aba = Aba(wb, "Aquisição", [("Tipo", 14), ("Descrição", 44), ("Data", 12), ("Valor", 18)],
            aq is null ? [] : aq.Itens.Select(i => new XLCellValue[] { Texto.Rotulo(i.Tipo), i.Descricao, Texto.Data(i.DataPagamento), i.Valor }));
        if (aq is not null)
        {
            var r = aba.LastRowUsed()!.RowNumber() + 1;
            foreach (var (k, v) in new[] { ("Valor pago", aq.Dados.ValorPago), ("Impostos", aq.Dados.Impostos), ("Taxas", aq.Dados.Taxas), ("Despesas diversas", aq.Dados.DespesasDiversas) })
            {
                r++;
                aba.Cell(r, 2).Value = k; aba.Cell(r, 4).Value = v;
            }
            r++;
            aba.Cell(r, 2).Value = "TOTAL DE AQUISIÇÃO"; aba.Cell(r, 4).Value = aq.Dados.TotalAquisicao;
            aba.Row(r).Style.Font.Bold = true;
            aba.Column(4).Style.NumberFormat.Format = Moeda;
        }

        var desp = Aba(wb, "Despesas", [("Competência", 13), ("Categoria", 22), ("Descrição", 34), ("Vencimento", 12), ("Valor", 16), ("Status", 14), ("Pago em", 12), ("Justificativa", 50)],
            lanc.Select(l => new XLCellValue[] { l.Dados.Competencia.ToString("MM/yyyy"), l.Categoria, l.Descricao, Texto.Data(l.Dados.Vencimento),
                l.Dados.Valor, StatusTexto(l.Dados.Status), l.Dados.DataPagamento is { } p ? Texto.Data(p) : "", l.Dados.Justificativa ?? "" }));
        desp.Column(5).Style.NumberFormat.Format = Moeda;
        desp.Range(1, 1, Math.Max(1, desp.LastRowUsed()!.RowNumber()), 8).SetAutoFilter();

        var fl = Aba(wb, "Fluxo de caixa", [("Data", 12), ("Tipo", 10), ("Descrição", 60), ("Valor", 18), ($"Valor corrigido ({ret.Indice})", 24)],
            ret.Fluxo.Select(f => new XLCellValue[] { Texto.Data(f.Data), f.Tipo == TipoMovimento.SAIDA ? "Saída" : "Entrada", f.Descricao, f.Valor, V(f.ValorCorrigido) }));
        fl.Column(4).Style.NumberFormat.Format = Moeda;
        fl.Column(5).Style.NumberFormat.Format = Moeda;

        await Auditar(u, id, new { relatorio = "ficha-financeira", formato = "xlsx", indice = indice.ToString() });
        return new Arquivo(Salvar(wb), $"ficha-financeira-{d.Codigo}.xlsx", TipoXlsx);
    }

    /* ------------------------------ Excel: apoio ------------------------------ */

    private static XLCellValue V(decimal? v) => v is { } x ? x : Blank.Value;
    private static XLCellValue V(double? v) => v is { } x ? x : Blank.Value;
    private static XLCellValue V(int? v) => v is { } x ? x : Blank.Value;

    private static IXLWorksheet Aba(XLWorkbook wb, string nome, (string Titulo, double Largura)[] colunas, IEnumerable<XLCellValue[]> linhas)
    {
        var ws = wb.Worksheets.Add(nome);
        for (var c = 0; c < colunas.Length; c++)
        {
            ws.Cell(1, c + 1).Value = colunas[c].Titulo;
            ws.Column(c + 1).Width = colunas[c].Largura;
        }
        var cab = ws.Range(1, 1, 1, colunas.Length).Style;
        cab.Font.SetBold().Font.SetFontColor(XLColor.White).Fill.SetBackgroundColor(XLColor.FromHtml(Marca));
        var r = 1;
        foreach (var l in linhas)
        {
            r++;
            for (var c = 0; c < l.Length; c++) ws.Cell(r, c + 1).Value = l[c];
        }
        ws.SheetView.FreezeRows(1);
        return ws;
    }

    private static byte[] Salvar(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /* ------------------------------- PDF: apoio ------------------------------- */

    private static byte[] NovoPdf(string titulo, string subtitulo, string rodape, Action<ColumnDescriptor> conteudo) =>
        Document.Create(doc => doc.Page(p =>
        {
            p.Size(PageSizes.A4);
            p.MarginHorizontal(40);
            p.MarginVertical(30);
            p.DefaultTextStyle(t => t.FontSize(10).FontColor("#0F172A"));
            p.Header().Background(Marca).Padding(14).Row(r =>
            {
                r.RelativeItem().Column(c =>
                {
                    c.Item().Text(titulo).FontSize(18).Bold().FontColor(Colors.White);
                    c.Item().Text(subtitulo).FontSize(10).FontColor(Colors.White);
                });
                r.ConstantItem(150).AlignRight().Text("INOVABENS").FontSize(13).Bold().FontColor(Colors.White);
            });
            p.Content().PaddingTop(12).Column(c =>
            {
                c.Spacing(4);
                conteudo(c);
            });
            p.Footer().AlignCenter().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(8).FontColor(Suave));
                t.Span($"{rodape}  ·  Página ");
                t.CurrentPageNumber();
                t.Span(" de ");
                t.TotalPages();
            });
        })).WithMetadata(new DocumentMetadata { Title = titulo, Author = "Inovabens" }).GeneratePdf();

    private static void Secao(ColumnDescriptor col, string titulo) =>
        col.Item().PaddingTop(10).BorderBottom(1).BorderColor(Linha).PaddingBottom(2)
            .Text(titulo.ToUpperInvariant()).FontSize(11).Bold().FontColor(Marca);

    /// <summary>Grade de "rótulo / valor" em N colunas.</summary>
    private static void Campos(ColumnDescriptor col, int colunas, params (string Rotulo, string? Valor)[] pares)
    {
        foreach (var grupo in pares.Chunk(colunas))
            col.Item().PaddingBottom(4).Row(r =>
            {
                foreach (var (rotulo, valor) in grupo)
                    r.RelativeItem().PaddingRight(8).Column(c =>
                    {
                        c.Item().Text(rotulo.ToUpperInvariant()).FontSize(7.5f).FontColor(Suave);
                        c.Item().Text(string.IsNullOrWhiteSpace(valor) ? "—" : valor).Bold();
                    });
                for (var i = grupo.Length; i < colunas; i++) r.RelativeItem();
            });
    }

    private static void Paragrafo(ColumnDescriptor col, string? texto) =>
        col.Item().Text(string.IsNullOrWhiteSpace(texto) ? "—" : texto.Trim()).Justify();

    private static void Tabela(ColumnDescriptor col, (string Titulo, float Peso)[] colunas, IReadOnlyList<string[]> linhas,
        string vazio = "Nenhum registro.", bool alinharUltimaDireita = false)
    {
        if (linhas.Count == 0)
        {
            Paragrafo(col, vazio);
            return;
        }
        col.Item().Table(t =>
        {
            t.ColumnsDefinition(c => { foreach (var (_, peso) in colunas) c.RelativeColumn(peso); });
            t.Header(h =>
            {
                for (var i = 0; i < colunas.Length; i++)
                {
                    var cel = h.Cell().Background(Marca).Padding(3);
                    (alinharUltimaDireita && i == colunas.Length - 1 ? cel.AlignRight() : cel)
                        .Text(colunas[i].Titulo).FontSize(8).Bold().FontColor(Colors.White);
                }
            });
            for (var l = 0; l < linhas.Count; l++)
                for (var i = 0; i < colunas.Length; i++)
                {
                    var cel = t.Cell().Background(l % 2 == 1 ? Zebra : Colors.White).Padding(3);
                    (alinharUltimaDireita && i == colunas.Length - 1 ? cel.AlignRight() : cel)
                        .Text(i < linhas[l].Length ? linhas[l][i] : "").FontSize(8.5f);
                }
        });
    }
}
