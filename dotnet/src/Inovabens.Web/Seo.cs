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
    /// <summary>Páginas exclusivas dos imóveis (landing pages): /imovel/{slug}.</summary>
    public const string PrefixoPaginas = "/imovel";

    /// <summary>Página do site que pode ir para o índice (com o site público ligado). Rascunho de página leva noindex na própria página.</summary>
    public static bool Indexavel(InovabensOpcoes op, PathString caminho) =>
        op.SitePublicoUrl is not null && (caminho.StartsWithSegments(PrefixoSite) || caminho.StartsWithSegments(PrefixoPaginas));

    /// <summary>Endereço absoluto usado em canônicas, Open Graph e sitemap: o domínio do site, ou o do sistema na prévia.</summary>
    public static string Base(InovabensOpcoes op) => op.SitePublicoUrl ?? op.WebUrl;

    public static string Absoluto(InovabensOpcoes op, string caminho) =>
        caminho.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || caminho.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? caminho
            : Base(op) + "/" + caminho.TrimStart('/');

    public static void UseIndexacao(this WebApplication app)
    {
        var op = app.Services.GetRequiredService<InovabensOpcoes>();
        // com o domínio próprio ligado, a página do imóvel aberta pelo endereço do sistema (link antigo, enviado antes
        // da troca do DNS) vai para o domínio: um só endereço por página para o Google e nenhum link perdido.
        // ?previa=1 (botão "Ver página" do sistema) fica no sistema: o rascunho precisa da sessão, que é deste endereço.
        var dominio = op.SitePublicoUrl is { } u && Uri.TryCreate(u, UriKind.Absolute, out var d) ? d : null;
        app.Use(async (c, next) =>
        {
            if (dominio is not null && HttpMethods.IsGet(c.Request.Method) && c.Request.Path.StartsWithSegments(PrefixoPaginas)
                && !c.Request.Query.ContainsKey("previa") && !string.Equals(c.Request.Host.Host, dominio.Host, StringComparison.OrdinalIgnoreCase))
            {
                c.Response.Redirect(op.SitePublicoUrl + c.Request.Path + c.Request.QueryString, permanent: true);
                return;
            }
            if (!Indexavel(op, c.Request.Path)) c.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            await next();
        });
    }

    public static void MapIndexacao(this WebApplication app)
    {
        var op = app.Services.GetRequiredService<InovabensOpcoes>();

        app.MapGet("/robots.txt", () => Results.Text(RobotsTxt(op), "text/plain; charset=utf-8")).AllowAnonymous();
        app.MapGet("/llms.txt", (HttpContext c) =>
        {
            c.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Text(LlmsTxt(op), "text/markdown; charset=utf-8");
        }).AllowAnonymous();

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
            // páginas exclusivas publicadas (o imóvel não precisa estar marcado para o site: a página é um anúncio à parte)
            var exclusivas = await db.PaginasImovel.AsNoTracking()
                .Where(p => p.Publicada && db.Imoveis.Any(i => i.Id == p.ImovelId && i.ExcluidoEm == null))
                .OrderByDescending(p => p.AtualizadoEm).Select(p => new { p.Slug, p.AtualizadoEm }).ToListAsync(ct);
            paginas.AddRange(exclusivas.Select(p => (Inovabens.Core.Imoveis.PaginasImovelService.Caminho(p.Slug), (DateTime?)p.AtualizadoEm)));
            return Results.Text(SitemapXml(op, paginas), "application/xml; charset=utf-8");
        }).AllowAnonymous();
    }

    public static string RobotsTxt(InovabensOpcoes op)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Sistema interno da Inovabens. As páginas não são indexadas (X-Robots-Tag: noindex);");
        sb.AppendLine("# o rastreamento fica liberado para que o Google consiga ler essa instrução.");
        sb.AppendLine();
        // robôs que coletam conteúdo para TREINAR modelos de IA: bloqueados (decisão da Inova Bens, 2026-11-26).
        // Os de busca com IA (OAI-SearchBot, Claude-SearchBot, PerplexityBot) e os acionados por um usuário
        // (ChatGPT-User, Claude-User, Perplexity-User) seguem liberados pela regra geral: o site aparece nas respostas.
        foreach (var robo in RobosTreino) sb.AppendLine($"User-agent: {robo}");
        sb.AppendLine("Disallow: /");
        sb.AppendLine();
        sb.AppendLine("User-agent: *");
        sb.AppendLine("Allow: /");
        if (op.SitePublicoUrl is not null) sb.AppendLine($"Sitemap: {op.SitePublicoUrl}/sitemap.xml");
        return sb.ToString();
    }

    /// <summary>Robôs de treino de IA (cada empresa documenta o seu): OpenAI, Anthropic, Google, Apple, Common Crawl, Meta, ByteDance.</summary>
    public static readonly string[] RobosTreino = ["GPTBot", "ClaudeBot", "Google-Extended", "Applebot-Extended", "CCBot", "meta-externalagent", "Bytespider"];

    /// <summary>
    /// /llms.txt: apresentação curta para agentes de IA (proposta llmstxt.org) com os endereços úteis. Poucos agentes leem
    /// hoje; custa pouco e aponta para as versões em Markdown e o feed, que são o que importa.
    /// </summary>
    public static string LlmsTxt(InovabensOpcoes op)
    {
        string A(string c) => Absoluto(op, c);
        var sb = new StringBuilder();
        sb.AppendLine("# Inova Bens Gestão Patrimonial");
        sb.AppendLine();
        sb.AppendLine("> Imobiliária em São José do Rio Preto/SP (CRECI 50457-J): venda, locação, aluguel por temporada, administração de imóveis e regularização.");
        sb.AppendLine();
        sb.AppendLine("Os anúncios trazem preço, área, quartos e características. Endereço exato só quando o proprietário autoriza; senão, bairro e cidade.");
        sb.AppendLine("Atendimento: WhatsApp (17) 98185-0171. Visitas, propostas e reservas são confirmadas pela equipe.");
        sb.AppendLine();
        sb.AppendLine("## Imóveis");
        sb.AppendLine();
        sb.AppendLine($"- [Imóveis à venda (Markdown)]({A("site/imoveis.md?finalidade=venda")}): lista com preço e link de cada ficha");
        sb.AppendLine($"- [Imóveis para alugar (Markdown)]({A("site/imoveis.md?finalidade=locacao")}): lista com aluguel mensal");
        sb.AppendLine($"- [Feed de anúncios (JSON-LD schema.org)]({A("site/imoveis.json")}): todos os anúncios públicos, RealEstateListing com oferta");
        sb.AppendLine($"- Ficha de um imóvel em Markdown: {A("site/imovel/{id}.md")} (ou a página da ficha pedindo Accept: text/markdown)");
        sb.AppendLine($"- Aluguel por temporada: {A("site/temporada/{id}")} (dias livres, preço da estadia e pedido de reserva)");
        sb.AppendLine();
        sb.AppendLine("## Site");
        sb.AppendLine();
        sb.AppendLine($"- [Página inicial]({A("site")})");
        sb.AppendLine($"- [Comprar]({A("site/imoveis?finalidade=venda")})");
        sb.AppendLine($"- [Alugar]({A("site/imoveis?finalidade=locacao")})");
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
