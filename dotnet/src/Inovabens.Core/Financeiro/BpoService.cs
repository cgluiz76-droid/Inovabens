using System.Text;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Relatorios;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Financeiro;

/// <summary>Dados do "cartão" de um lançamento para o responsável financeiro (BPO) do imóvel.</summary>
public sealed record CartaoBpo(
    Guid LancamentoId, string? ResponsavelNome, string? ResponsavelWhatsapp, string? ResponsavelEmail,
    bool Receita, string Numero, string Situacao, decimal Valor, string Descricao, string Conta, string Empreendimento,
    string? Observacao, DateOnly Vencimento, DateOnly? Liquidacao, string? Pessoa, IReadOnlyList<ComprovanteDetalhe> Anexos,
    DateTime? EnviadoEm, string? EnviadoPor)
{
    public string Tipo => Receita ? "Receita" : "Despesa";

    /// <summary>Valor sem sinal (o tipo já diz se entra ou sai).</summary>
    public string ValorTexto => Texto.Moeda(Math.Abs(Valor));

    public bool TemResponsavel => ResponsavelNome is not null;

    /// <summary>Texto pronto para colar no WhatsApp (*negrito* e _itálico_ do próprio WhatsApp).</summary>
    public string TextoWhatsapp
    {
        get
        {
            var t = new StringBuilder();
            t.Append("*").Append(Tipo).Append(" — ").Append(Numero).Append("*\n");
            t.Append("*Valor:* ").Append(ValorTexto).Append('\n');
            t.Append("*Descrição:* ").Append(Descricao).Append('\n');
            t.Append("*Empreendimento:* ").Append(Empreendimento).Append('\n');
            if (Observacao is not null) t.Append("*Observação:* ").Append(Observacao).Append('\n');
            t.Append("_Vencimento ").Append(Texto.Data(Vencimento));
            if (Liquidacao is { } d) t.Append(" · ").Append(Receita ? "recebido" : "pago").Append(" em ").Append(Texto.Data(d));
            t.Append("_");
            if (Anexos.Count > 0) t.Append("\n📎 ").Append(Anexos.Count).Append(Anexos.Count == 1 ? " anexo" : " anexos").Append(" em seguida");
            return t.ToString();
        }
    }

    /// <summary>Conversa no WhatsApp do responsável já com o texto (null sem WhatsApp válido).</summary>
    public string? LinkWhatsapp =>
        Texto.LinkWhatsapp(ResponsavelWhatsapp) is { } link ? $"{link}?text={Uri.EscapeDataString(TextoWhatsapp)}" : null;
}

/// <summary>
/// Envio dos lançamentos ao responsável financeiro (BPO) do imóvel. Enquanto a API da Meta não está configurada o envio é
/// manual: o usuário abre o cartão, copia o texto (ou abre a conversa) e manda o PDF único (cartão + comprovantes); depois
/// marca "Enviado ao BPO", que grava quando e quem (auditado). O envio automático, quando existir, grava os mesmos campos.
/// </summary>
public sealed class BpoService(IDbContextFactory<InovabensDb> fabrica, IArmazenamento armazenamento, AuditService audit)
{
    // o PDF do cartão pode ser o primeiro gerado desde a subida: garante a licença sem depender do RelatoriosService
    static BpoService() => QuestPDF.Settings.License = LicenseType.Community;

    /// <summary>O imóvel tem responsável financeiro cadastrado (grupo Financeiro/BPO)?</summary>
    public async Task<bool> TemResponsavelAsync(UsuarioAtual u, Guid imovelId)
    {
        if (!u.Pode(P.FinanceiroEditar)) return false;
        await using var db = await fabrica.CreateDbContextAsync();
        return (await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false)).BpoNome is not null;
    }

    public async Task<CartaoBpo> CartaoAsync(UsuarioAtual u, Guid lancamentoId)
    {
        // mesmo nível dos comprovantes: quem envia ao BPO precisa ver os anexos
        u.Exigir(P.FinanceiroEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await db.LancamentosFinanceiros.AsNoTracking().Include(x => x.Conta)
            .SingleOrDefaultAsync(x => x.Id == lancamentoId && x.ExcluidoEm == null) ?? throw new NaoEncontradoException("Lançamento não encontrado.");
        var im = await Acesso.ImovelComAcesso(db, u, l.ImovelId, rastrear: false);
        var anexos = await db.ComprovantesFinanceiros.AsNoTracking().Where(c => c.LancamentoId == lancamentoId).OrderBy(c => c.CriadoEm)
            .Select(c => new ComprovanteDetalhe(c.Id, c.NomeOriginal, c.TipoConteudo, c.Tamanho, c.CriadoEm))
            .ToListAsync();
        var enviadoPor = l.BpoEnviadoPor is { } quem ? await db.Usuarios.Where(x => x.Id == quem).Select(x => x.Nome).FirstOrDefaultAsync() : null;
        var nome = RegrasImovel.NomeSemCodigo(im.Codigo, im.NomeDrive) ?? im.Titulo;
        return new CartaoBpo(
            l.Id, im.BpoNome, im.BpoWhatsapp, im.BpoEmail,
            l.Conta?.Tipo == TipoConta.RECEITA || (l.Conta is null && l.Valor > 0),
            LancamentosFinanceirosService.FormatarNumero(im.Codigo, l.Numero), LancamentosFinanceirosService.NomeSituacao(l.Situacao),
            l.Valor, l.Descricao, l.Conta?.Nome ?? "", $"{im.Codigo} · {nome}", l.Observacao, l.Data, l.DataLiquidacao, l.PessoaNome,
            anexos, l.BpoEnviadoEm, enviadoPor);
    }

    /// <summary>Marca (ou desmarca) o envio ao BPO: grava quando e quem, com auditoria.</summary>
    public async Task MarcarEnviadoAsync(UsuarioAtual u, Guid lancamentoId, bool enviado)
    {
        u.Exigir(P.FinanceiroEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await db.LancamentosFinanceiros.SingleOrDefaultAsync(x => x.Id == lancamentoId && x.ExcluidoEm == null)
            ?? throw new NaoEncontradoException("Lançamento não encontrado.");
        await Acesso.ImovelComAcesso(db, u, l.ImovelId, rastrear: false);
        if (l.Situacao == SituacaoLancamento.PREVISTO)
            throw new RegraNegocioException("bpo", "Lançamento previsto ainda não existe: confirme-o antes de enviar ao BPO.");
        if (enviado == l.BpoEnviadoEm is not null) return;
        var antes = new { l.BpoEnviadoEm, l.BpoEnviadoPor };
        l.BpoEnviadoEm = enviado ? DateTime.UtcNow : null;
        l.BpoEnviadoPor = enviado ? u.Id : null;
        audit.Registrar(db, u.Id, "lancamentos_financeiros", l.Id, enviado ? "BPO_ENVIADO" : "BPO_DESMARCADO",
            antes, new { l.BpoEnviadoEm, l.BpoEnviadoPor });
        await db.SaveChangesAsync();
    }

    /* ------------------------------- PDF único ------------------------------- */

    // Uma folha A4 (pontos, 1/72"): dados compactos no topo e os anexos encaixados no resto da folha
    private const float Margem = 20, Espaco = 6, AlturaUtil = 841.89f - 2 * Margem;
    /// <summary>Acima disso as miniaturas ficariam ilegíveis: as páginas excedentes são só citadas no cartão.</summary>
    internal const int MaximoPaginasAnexo = 6;

    /// <summary>
    /// PDF do BPO numa única página A4: os dados do lançamento em letra pequena no topo e, abaixo, os comprovantes
    /// encaixados (imagens e páginas dos PDFs, convertidas em imagem). Com mais de um, ficam lado a lado em grade.
    /// Anexo que não dá para incorporar (outro formato, PDF protegido/corrompido) fica listado para baixar à parte.
    /// </summary>
    public async Task<Arquivo> PdfAsync(UsuarioAtual u, Guid lancamentoId, CancellationToken ct = default)
    {
        var c = await CartaoAsync(u, lancamentoId);
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var chaves = await db.ComprovantesFinanceiros.AsNoTracking().Where(x => x.LancamentoId == lancamentoId)
            .ToDictionaryAsync(x => x.Id, x => x.ArquivoChave, ct);

        var paginas = new List<(string Rotulo, byte[] Imagem)>();
        var fora = new List<(string Nome, string Motivo)>();
        var excedentes = 0;
        foreach (var a in c.Anexos)
        {
            var conteudo = await armazenamento.BaixarAsync(chaves.GetValueOrDefault(a.Id), ct);
            if (conteudo is null) { fora.Add((a.Nome, "não encontrado no Google Drive")); continue; }
            if (EhPdf(a.Nome, a.Tipo))
            {
                var imagens = PaginasDoPdf(conteudo.Dados);
                if (imagens is null) { fora.Add((a.Nome, "PDF protegido ou corrompido")); continue; }
                for (var i = 0; i < imagens.Count; i++)
                {
                    if (paginas.Count >= MaximoPaginasAnexo) { excedentes++; continue; }
                    paginas.Add((imagens.Count == 1 ? a.Nome : $"{a.Nome} (pág. {i + 1}/{imagens.Count})", imagens[i]));
                }
            }
            else if (!ImagemLegivel(conteudo.Dados)) fora.Add((a.Nome, "formato que não entra no PDF"));
            else if (paginas.Count >= MaximoPaginasAnexo) excedentes++;
            else paginas.Add((a.Nome, conteudo.Dados));
        }

        return new Arquivo(CartaoPdf(c, paginas, fora, excedentes), $"bpo-{Seguro(c.Numero)}.pdf", RelatoriosService.TipoPdf);
    }

    private static bool EhPdf(string nome, string tipo) =>
        tipo.Contains("pdf", StringComparison.OrdinalIgnoreCase) || nome.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    private static bool ImagemLegivel(byte[] dados)
    {
        try { Image.FromBinaryData(dados).Dispose(); return true; }
        catch (Exception) { return false; }
    }

    // o PDFium (Docnet) não é seguro para chamadas simultâneas
    private static readonly Lock TravaPdfium = new();

    /// <summary>
    /// Cada página do PDF como JPEG (~144 dpi, fundo branco), para caber na mesma folha do cartão.
    /// Só as primeiras <see cref="MaximoPaginasAnexo"/> são convertidas. null = PDF protegido ou ilegível.
    /// </summary>
    internal static IReadOnlyList<byte[]>? PaginasDoPdf(byte[] pdf)
    {
        try
        {
            lock (TravaPdfium)
            {
                using var doc = Docnet.Core.DocLib.Instance.GetDocReader(pdf, new Docnet.Core.Models.PageDimensions(2.0));
                var total = doc.GetPageCount();
                if (total == 0) return null;
                var saida = new List<byte[]>();
                for (var i = 0; i < Math.Min(total, MaximoPaginasAnexo); i++)
                {
                    using var pg = doc.GetPageReader(i);
                    int w = pg.GetPageWidth(), h = pg.GetPageHeight();
                    var bgra = pg.GetImage(); // BGRA com transparência onde a página é "vazia"
                    using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
                    System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bmp.GetPixels(), bgra.Length);
                    using var branco = new SKBitmap(w, h);
                    using (var tela = new SKCanvas(branco))
                    {
                        tela.Clear(SKColors.White);
                        using var fonte = SKImage.FromBitmap(bmp);
                        tela.DrawImage(fonte, 0, 0, SKSamplingOptions.Default);
                    }
                    using var img = SKImage.FromBitmap(branco);
                    saida.Add(img.Encode(SKEncodedImageFormat.Jpeg, 82).ToArray());
                }
                // páginas além do limite contam como excedentes (só o número delas importa)
                for (var i = MaximoPaginasAnexo; i < total; i++) saida.Add([]);
                return saida;
            }
        }
        catch (Exception) { return null; }
    }

    private static string Seguro(string s) => string.Concat(s.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '-'));

    private const string Azul = "#1E3A8A", Cinza = "#64748B", Linha = "#E2E8F0";

    /// <summary>Folha única A4: faixa de título, dados em grade compacta (altura fixa) e grade de anexos abaixo.</summary>
    private static byte[] CartaoPdf(CartaoBpo c, IReadOnlyList<(string Rotulo, byte[] Imagem)> paginas,
        IReadOnlyList<(string Nome, string Motivo)> fora, int excedentes)
    {
        var validas = paginas.Where(p => p.Imagem.Length > 0).ToList();
        var notas = new List<string>();
        if (c.Anexos.Count == 0) notas.Add("Sem anexos.");
        notas.AddRange(fora.Select(f => $"{f.Nome}: não incorporado ({f.Motivo}) — baixe pelo sistema."));
        if (excedentes > 0) notas.Add($"Mais {excedentes} página(s) de anexo não couberam nesta folha — baixe pelo sistema.");

        // altura dos dados estimada pelo conteúdo (o que passar é reduzido): o resto da folha fica para os anexos
        var linhasObs = Math.Max(1, (int)Math.Ceiling((c.Observacao?.Length ?? 0) / 120.0));
        var alturaDados = Math.Clamp(28 + 4 * 21 + (c.Pessoa is null ? 0 : 21) + (linhasObs - 1) * 10 + notas.Count * 10 + 12f, 120f, 300f);
        var alturaAnexos = AlturaUtil - alturaDados - Espaco;
        return Document.Create(doc => doc.Page(p =>
        {
            p.Size(PageSizes.A4);
            p.Margin(Margem);
            p.DefaultTextStyle(t => t.FontSize(8).FontColor("#0F172A").LineHeight(1.1f));
            p.Content().Column(col =>
            {
                col.Spacing(Espaco);
                // dados: altura fixa (o que passar disso é reduzido) para a área dos anexos ser sempre a mesma
                col.Item().Height(alturaDados).ScaleToFit().Column(d =>
                {
                    d.Spacing(3);
                    d.Item().Background(Azul).PaddingVertical(5).PaddingHorizontal(8).Row(r =>
                    {
                        r.RelativeItem().Text(t =>
                        {
                            t.Span($"{c.Tipo} {c.Numero}").FontSize(12).Bold().FontColor(Colors.White);
                            t.Span($"   {c.Empreendimento}").FontSize(8.5f).FontColor(Colors.White);
                        });
                        r.ConstantItem(70).AlignRight().AlignMiddle().Text("INOVABENS").FontSize(9).Bold().FontColor(Colors.White);
                    });
                    Par(d, ("Responsável financeiro", c.ResponsavelNome), ("Valor", $"{c.ValorTexto} ({c.Tipo.ToLowerInvariant()})"));
                    Par(d, ("Descrição", c.Descricao), ("Empreendimento", c.Empreendimento));
                    Par(d, ("Observação", c.Observacao));
                    Par(d, ("Vencimento", Texto.Data(c.Vencimento)),
                        (c.Receita ? "Recebido em" : "Pago em", c.Liquidacao is { } dl ? Texto.Data(dl) : null),
                        ("Situação", c.Situacao), ("Conta", c.Conta));
                    if (c.Pessoa is not null) Par(d, (c.Receita ? "Recebido de" : "Pago a", c.Pessoa));
                    foreach (var n in notas) d.Item().Text(n).FontSize(7).Italic().FontColor(Cinza);
                    d.Item().Text($"Cartão BPO · gerado em {DatasBR.Agora():dd/MM/yyyy HH:mm}").FontSize(6.5f).FontColor(Cinza);
                });

                if (validas.Count == 0) return;
                // grade dos anexos: 1 = área toda; 2 = lado a lado; 3-4 = 2x2; 5-6 = 3x2
                var colunas = validas.Count == 1 ? 1 : validas.Count <= 4 ? 2 : 3;
                var linhas = (int)Math.Ceiling(validas.Count / (double)colunas);
                var alturaCelula = (alturaAnexos - 4 - (linhas - 1) * 4) / linhas;
                col.Item().BorderTop(0.5f).BorderColor(Linha).PaddingTop(2).Column(g =>
                {
                    g.Spacing(4);
                    foreach (var linha in validas.Chunk(colunas))
                        g.Item().Height(alturaCelula - 2).Row(r =>
                        {
                            r.Spacing(4);
                            foreach (var (rotulo, imagem) in linha)
                                r.RelativeItem().Column(cel =>
                                {
                                    cel.Item().Text(rotulo).FontSize(6.5f).FontColor(Cinza).ClampLines(1);
                                    cel.Item().Extend().AlignCenter().AlignMiddle().Image(imagem).FitArea();
                                });
                            for (var i = linha.Length; i < colunas; i++) r.RelativeItem();
                        });
                });
            });
        })).WithMetadata(new DocumentMetadata { Title = $"BPO {c.Numero}", Author = "Inovabens" }).GeneratePdf();
    }

    /// <summary>Linha de "rótulo / valor" compacta (rótulo miúdo em cima, valor em negrito).</summary>
    private static void Par(ColumnDescriptor col, params (string Rotulo, string? Valor)[] pares) =>
        col.Item().PaddingHorizontal(2).Row(r =>
        {
            foreach (var (rotulo, valor) in pares)
                r.RelativeItem().PaddingRight(6).Column(cc =>
                {
                    cc.Item().Text(rotulo.ToUpperInvariant()).FontSize(6).FontColor(Cinza);
                    cc.Item().Text(string.IsNullOrWhiteSpace(valor) ? "—" : valor.Trim()).FontSize(8.5f).Bold();
                });
        });
}
