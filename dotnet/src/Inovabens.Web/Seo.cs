using System.Text;
using System.Xml;
using Inovabens.Core;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Microsoft.EntityFrameworkCore;

namespace Inovabens.Web;

/// <summary>
/// Indexação no Google (Search Central: "Block Search indexing with noindex", "robots.txt", "Build and submit a sitemap").
/// Enquanto SITE_PUBLICO_URL estiver vazio, nada deste sistema entra no índice: toda resposta leva
/// <c>X-Robots-Tag: noindex, nofollow</c> e o robots.txt NÃO bloqueia o rastreamento — o Google só respeita o noindex
/// se puder ler a página. Com SITE_PUBLICO_URL preenchido, só as páginas do site (/site...) ficam indexáveis e o
/// sitemap lista a vitrine (imóveis disponíveis marcados para divulgar).
/// </summary>
public static class Seo
{
    public const string PrefixoSite = "/site";

    /// <summary>Página do site que pode ir para o índice (com o site público ligado).</summary>
    public static bool Indexavel(InovabensOpcoes op, PathString caminho) =>
        op.SitePublicoUrl is not null && caminho.StartsWithSegments(PrefixoSite);

    /// <summary>Endereço absoluto usado em canônicas, Open Graph e sitemap: o domínio do site, ou o do sistema na prévia.</summary>
    public static string Base(InovabensOpcoes op) => op.SitePublicoUrl ?? op.WebUrl;

    public static string Absoluto(InovabensOpcoes op, string caminho) =>
        caminho.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || caminho.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? caminho
            : Base(op) + "/" + caminho.TrimStart('/');

    public static void UseIndexacao(this WebApplication app)
    {
        var op = app.Services.GetRequiredService<InovabensOpcoes>();
        app.Use(async (c, next) =>
        {
            if (!Indexavel(op, c.Request.Path)) c.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            await next();
        });
    }

    public static void MapIndexacao(this WebApplication app)
    {
        var op = app.Services.GetRequiredService<InovabensOpcoes>();

        app.MapGet("/robots.txt", () => Results.Text(RobotsTxt(op), "text/plain; charset=utf-8")).AllowAnonymous();

        // navegadores e robôs pedem /favicon.ico mesmo com o ícone SVG declarado: evita um 404 com página inteira
        app.MapGet("/favicon.ico", () => Results.Redirect("/img/marca.svg", permanent: true)).AllowAnonymous();

        app.MapGet("/sitemap.xml", async (IDbContextFactory<InovabensDb> fabrica, CancellationToken ct) =>
        {
            if (op.SitePublicoUrl is null) return Results.NotFound();
            await using var db = await fabrica.CreateDbContextAsync(ct);
            var imoveis = await db.Imoveis.AsNoTracking()
                .Where(i => i.ExcluidoEm == null && i.DivulgarSite && i.Situacao == SituacaoImovel.DISPONIVEL)
                .OrderByDescending(i => i.AtualizadoEm)
                .Select(i => new { i.Id, i.AtualizadoEm })
                .ToListAsync(ct);
            var paginas = new List<(string Caminho, DateTime? Alterado)>
            {
                ("site", imoveis.Count > 0 ? imoveis[0].AtualizadoEm : null),
                ("site/imoveis?finalidade=venda", null),
                ("site/imoveis?finalidade=locacao", null),
            };
            paginas.AddRange(imoveis.Select(i => ($"site/imovel/{i.Id}", (DateTime?)i.AtualizadoEm)));
            return Results.Text(SitemapXml(op, paginas), "application/xml; charset=utf-8");
        }).AllowAnonymous();
    }

    public static string RobotsTxt(InovabensOpcoes op)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Sistema interno da Inovabens. As páginas não são indexadas (X-Robots-Tag: noindex);");
        sb.AppendLine("# o rastreamento fica liberado para que o Google consiga ler essa instrução.");
        sb.AppendLine("User-agent: *");
        sb.AppendLine("Allow: /");
        if (op.SitePublicoUrl is not null) sb.AppendLine($"Sitemap: {op.SitePublicoUrl}/sitemap.xml");
        return sb.ToString();
    }

    public static string SitemapXml(InovabensOpcoes op, IEnumerable<(string Caminho, DateTime? Alterado)> paginas)
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false }))
        {
            w.WriteStartElement("urlset", "http://www.sitemaps.org/schemas/sitemap/0.9");
            foreach (var (caminho, alterado) in paginas)
            {
                w.WriteStartElement("url");
                w.WriteElementString("loc", Absoluto(op, caminho));
                if (alterado is { } d) w.WriteElementString("lastmod", d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
    }
}
