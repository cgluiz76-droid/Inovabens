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
    // a junção de PDFs (DocumentOperation) também exige a licença, mesmo antes de qualquer relatório ter sido gerado
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

    /// <summary>
    /// PDF único para o BPO: página do cartão, cada imagem anexada como página e os PDFs anexados juntados ao final.
    /// Anexo que não dá para incorporar (outro formato, PDF protegido/corrompido) fica listado no cartão para baixar à parte.
    /// </summary>
    public async Task<Arquivo> PdfAsync(UsuarioAtual u, Guid lancamentoId, CancellationToken ct = default)
    {
        var c = await CartaoAsync(u, lancamentoId);
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var chaves = await db.ComprovantesFinanceiros.AsNoTracking().Where(x => x.LancamentoId == lancamentoId)
            .ToDictionaryAsync(x => x.Id, x => x.ArquivoChave, ct);

        var pasta = Directory.CreateTempSubdirectory("bpo-");
        try
        {
            var imagens = new List<(string Nome, byte[] Dados)>();
            var pdfs = new List<string>();
            var fora = new List<(string Nome, string Motivo)>();
            foreach (var a in c.Anexos)
            {
                var conteudo = await armazenamento.BaixarAsync(chaves.GetValueOrDefault(a.Id), ct);
                if (conteudo is null) { fora.Add((a.Nome, "não encontrado no Google Drive")); continue; }
                if (EhPdf(a.Nome, a.Tipo))
                {
                    var caminho = Path.Combine(pasta.FullName, $"anexo-{pdfs.Count + 1}.pdf");
                    await File.WriteAllBytesAsync(caminho, conteudo.Dados, ct);
                    if (PdfLegivel(caminho)) pdfs.Add(caminho);
                    else fora.Add((a.Nome, "PDF protegido ou corrompido"));
                }
                else if (ImagemLegivel(conteudo.Dados)) imagens.Add((a.Nome, conteudo.Dados));
                else fora.Add((a.Nome, "formato que não entra no PDF"));
            }

            var cartao = CartaoPdf(c, imagens, pdfs.Count, fora);
            var nomeArquivo = $"bpo-{Seguro(c.Numero)}.pdf";
            if (pdfs.Count == 0) return new Arquivo(cartao, nomeArquivo, RelatoriosService.TipoPdf);
            return new Arquivo(Juntar(cartao, pdfs), nomeArquivo, RelatoriosService.TipoPdf);
        }
        finally
        {
            try { pasta.Delete(recursive: true); } catch (IOException) { /* temporário: o sistema limpa depois */ }
        }
    }

    // A4 em pontos (1/72"): nenhuma página do PDF único passa disso (pode ser menor, ex.: recibo de maquininha)
    internal const double A4Menor = 595.28, A4Maior = 841.89;

    /// <summary>
    /// Cartão (+ imagens, já em A4) seguido das páginas dos PDFs anexados. Página maior que A4 (A3, planta, extrato largo)
    /// é reduzida para caber numa A4 da mesma orientação, mantendo a proporção; as menores ficam como estão.
    /// </summary>
    private static byte[] Juntar(byte[] cartao, IReadOnlyList<string> pdfs)
    {
        using var saida = new PdfSharp.Pdf.PdfDocument();
        using (var base0 = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(cartao), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
            foreach (var pg in base0.Pages) saida.AddPage(pg);
        foreach (var caminho in pdfs)
        {
            using var origem = PdfSharp.Pdf.IO.PdfReader.Open(caminho, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            for (var i = 0; i < origem.PageCount; i++)
            {
                var pg = origem.Pages[i];
                var (larg, alt) = TamanhoVisivel(pg);
                if (CabeEmA4(larg, alt)) { saida.AddPage(pg); continue; }

                var deitada = larg > alt;
                var nova = saida.AddPage();
                nova.Width = PdfSharp.Drawing.XUnit.FromPoint(deitada ? A4Maior : A4Menor);
                nova.Height = PdfSharp.Drawing.XUnit.FromPoint(deitada ? A4Menor : A4Maior);
                using var form = PdfSharp.Drawing.XPdfForm.FromFile(caminho);
                form.PageNumber = i + 1;
                var escala = Math.Min(nova.Width.Point / larg, nova.Height.Point / alt);
                var (w, h) = (larg * escala, alt * escala);
                using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(nova);
                gfx.DrawImage(form, (nova.Width.Point - w) / 2, (nova.Height.Point - h) / 2, w, h);
            }
        }
        using var ms = new MemoryStream();
        saida.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    /// <summary>Largura/altura como a página aparece (rotação de 90°/270° troca os lados).</summary>
    internal static (double Largura, double Altura) TamanhoVisivel(PdfSharp.Pdf.PdfPage pg)
    {
        double w = pg.MediaBox.Width, h = pg.MediaBox.Height;
        return pg.Rotate % 180 != 0 ? (h, w) : (w, h);
    }

    /// <summary>Cabe numa folha A4 em pé ou deitada (1 pt de folga para arredondamentos).</summary>
    internal static bool CabeEmA4(double largura, double altura) =>
        Math.Max(largura, altura) <= A4Maior + 1 && Math.Min(largura, altura) <= A4Menor + 1;

    private static bool EhPdf(string nome, string tipo) =>
        tipo.Contains("pdf", StringComparison.OrdinalIgnoreCase) || nome.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Abre o PDF antes de juntar: se falhar aqui (senha, arquivo quebrado), fica fora e listado no cartão.</summary>
    private static bool PdfLegivel(string caminho)
    {
        try
        {
            using var d = PdfSharp.Pdf.IO.PdfReader.Open(caminho, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            return d.PageCount > 0;
        }
        catch (Exception) { return false; }
    }

    private static bool ImagemLegivel(byte[] dados)
    {
        try { Image.FromBinaryData(dados).Dispose(); return true; }
        catch (Exception) { return false; }
    }

    private static string Seguro(string s) => string.Concat(s.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '-'));

    /// <summary>
    /// Cartão numa única página A4 (observação longa ou muitos anexos: o bloco é reduzido para caber) e cada imagem
    /// anexada numa página A4 própria. Sem "Página X de Y": os PDFs anexados entram depois e mudariam a conta.
    /// </summary>
    private static byte[] CartaoPdf(CartaoBpo c, IReadOnlyList<(string Nome, byte[] Dados)> imagens, int pdfs,
        IReadOnlyList<(string Nome, string Motivo)> fora) =>
        RelatoriosService.NovoPdf($"{c.Tipo} {c.Numero}", c.Empreendimento, $"Cartão BPO · gerado em {DatasBR.Agora():dd/MM/yyyy HH:mm}", col =>
        {
            col.Item().ScaleToFit().Column(cartao =>
            {
                cartao.Spacing(4);
                RelatoriosService.Campos(cartao, 1, ("Responsável financeiro", c.ResponsavelNome));
                RelatoriosService.Secao(cartao, "Lançamento");
                RelatoriosService.Campos(cartao, 2, ("Valor", $"{c.ValorTexto} ({c.Tipo.ToLowerInvariant()})"), ("Descrição", c.Descricao));
                RelatoriosService.Campos(cartao, 1, ("Empreendimento", c.Empreendimento));
                RelatoriosService.Campos(cartao, 1, ("Observação", c.Observacao));
                RelatoriosService.Campos(cartao, 4, ("Vencimento", Texto.Data(c.Vencimento)),
                    (c.Receita ? "Recebido em" : "Pago em", c.Liquidacao is { } d ? Texto.Data(d) : null),
                    ("Situação", c.Situacao), ("Conta", c.Conta));
                if (c.Pessoa is not null) RelatoriosService.Campos(cartao, 1, (c.Receita ? "Recebido de" : "Pago a", c.Pessoa));

                RelatoriosService.Secao(cartao, "Anexos");
                if (c.Anexos.Count == 0) RelatoriosService.Paragrafo(cartao, "Sem anexos.");
                foreach (var a in c.Anexos)
                {
                    var motivo = fora.FirstOrDefault(f => f.Nome == a.Nome).Motivo;
                    cartao.Item().Text(motivo is null ? $"• {a.Nome}" : $"• {a.Nome} — não incorporado ({motivo}): baixe pelo sistema").FontSize(9);
                }
                if (imagens.Count + pdfs > 0)
                    cartao.Item().PaddingTop(4).Text("Os anexos seguem nas próximas páginas.").FontSize(8.5f).Italic();
            });

            foreach (var (nome, dados) in imagens)
            {
                col.Item().PageBreak();
                col.Item().Text($"Anexo: {nome}").FontSize(9).Bold();
                col.Item().PaddingTop(6).MaxHeight(650).AlignCenter().Image(dados).FitArea();
            }
        }, numerarPaginas: false);
}
