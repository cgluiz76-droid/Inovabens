using System.Text.Json;
using System.Xml.Linq;
using Inovabens.Core;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Web;
using Inovabens.Web.Components.Site;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Inovabens.Tests;

/// <summary>Indexação (robots, sitemap, noindex) e dados estruturados do site.</summary>
public class SeoTests
{
    private static InovabensOpcoes Op(string? site = null) =>
        new() { DatabaseUrl = "x", WebUrl = "https://inovabens.fly.dev", SitePublicoUrl = site };

    [Fact]
    public void Sem_site_publico_nada_e_indexavel()
    {
        var op = Op();
        Assert.False(Seo.Indexavel(op, new PathString("/site")));
        Assert.False(Seo.Indexavel(op, new PathString("/imoveis")));
        Assert.False(Seo.Indexavel(op, new PathString("/login")));
    }

    [Fact]
    public void Com_site_publico_so_paginas_do_site_sao_indexaveis()
    {
        var op = Op("https://www.inovabens.com.br");
        Assert.True(Seo.Indexavel(op, new PathString("/site")));
        Assert.True(Seo.Indexavel(op, new PathString("/site/imovel/123")));
        Assert.False(Seo.Indexavel(op, new PathString("/sitex")));
        Assert.False(Seo.Indexavel(op, new PathString("/imoveis")));
        Assert.False(Seo.Indexavel(op, new PathString("/crm")));
    }

    [Fact]
    public void Robots_libera_busca_bloqueia_so_treino_de_ia_e_so_cita_sitemap_com_site_publico()
    {
        var privado = Seo.RobotsTxt(Op());
        var grupos = System.Text.RegularExpressions.Regex.Split(privado.Replace("\r", ""), @"\n\s*\n").Where(g => g.Contains("User-agent")).ToList();
        var treino = Assert.Single(grupos, g => g.Contains("Disallow: /"));
        foreach (var robo in new[] { "GPTBot", "ClaudeBot", "Google-Extended", "CCBot" }) Assert.Contains($"User-agent: {robo}", treino);
        // busca com IA e pedidos de usuário seguem na regra geral (liberados)
        foreach (var robo in new[] { "OAI-SearchBot", "ChatGPT-User", "Claude-SearchBot", "Claude-User", "PerplexityBot" })
            Assert.DoesNotContain(robo, privado);
        Assert.Contains(grupos, g => g.Contains("User-agent: *") && g.Contains("Allow: /") && !g.Contains("Disallow"));
        Assert.DoesNotContain("Sitemap:", privado);

        Assert.Contains("Sitemap: https://www.inovabens.com.br/sitemap.xml", Seo.RobotsTxt(Op("https://www.inovabens.com.br")));
    }

    [Fact]
    public void Sitemap_tem_enderecos_absolutos_no_dominio_do_site_e_escapa_parametros()
    {
        var xml = Seo.SitemapXml(Op("https://www.inovabens.com.br"),
        [
            ("site", new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc)),
            ("site/imoveis?finalidade=venda&tipo=CASA", null),
        ]);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var urls = doc.Root!.Elements(ns + "url").ToList();
        Assert.Equal(2, urls.Count);
        Assert.Equal("https://www.inovabens.com.br/site", urls[0].Element(ns + "loc")!.Value);
        Assert.Equal("2026-11-12T10:00:00Z", urls[0].Element(ns + "lastmod")!.Value);
        Assert.Equal("https://www.inovabens.com.br/site/imoveis?finalidade=venda&tipo=CASA", urls[1].Element(ns + "loc")!.Value);
        Assert.Null(urls[1].Element(ns + "lastmod"));
    }

    [Fact]
    public void Absoluto_usa_o_sistema_na_previa_e_mantem_url_completa()
    {
        Assert.Equal("https://inovabens.fly.dev/site", Seo.Absoluto(Op(), "site"));
        Assert.Equal("https://inovabens.fly.dev/site", Seo.Absoluto(Op(), "/site"));
        Assert.Equal("https://cdn.exemplo/f.webp", Seo.Absoluto(Op(), "https://cdn.exemplo/f.webp"));
    }

    [Theory]
    [InlineData("Casa térrea", "Casa térrea")]
    [InlineData("  muitos   espaços\n aqui ", "muitos espaços aqui")]
    public void Resumo_curto_so_normaliza_espacos(string texto, string esperado) =>
        Assert.Equal(esperado, DadosSite.Resumo(texto));

    [Fact]
    public void Resumo_longo_corta_em_palavra_com_reticencias()
    {
        var texto = string.Join(' ', Enumerable.Repeat("palavra", 40));
        var r = DadosSite.Resumo(texto);
        Assert.True(r.Length <= 155, r.Length.ToString());
        Assert.EndsWith("palavra…", r);
    }

    [Fact]
    public void Dados_estruturados_sao_json_valido_e_seguro_dentro_de_script()
    {
        var op = Op("https://www.inovabens.com.br");
        using var org = JsonDocument.Parse(DadosSite.Imobiliaria(op));
        Assert.Equal("RealEstateAgent", org.RootElement.GetProperty("@type").GetString());
        Assert.Equal("https://www.inovabens.com.br/site", org.RootElement.GetProperty("url").GetString());

        var trilha = DadosSite.Trilha(op, ("Início", "site"), ("</script><b>", "site/imovel/1"));
        Assert.DoesNotContain("</script>", trilha);
        using var t = JsonDocument.Parse(trilha);
        var itens = t.RootElement.GetProperty("itemListElement");
        Assert.Equal(2, itens.GetArrayLength());
        Assert.Equal(2, itens[1].GetProperty("position").GetInt32());
        Assert.Equal("</script><b>", itens[1].GetProperty("name").GetString());
    }

    /* ------------------------------- agentes de IA ------------------------------- */

    private static ImovelDetalhe Detalhe(bool enderecoPublico, FinalidadeImovel fin = FinalidadeImovel.VENDA) => new(
        new Imovel
        {
            Codigo = "CA0001", Titulo = "Casa com piscina", Tipo = "Casa", Finalidade = fin, Logradouro = "Rua Secreta", Numero = "10",
            Bairro = "Centro", Cidade = "São José do Rio Preto", Uf = "SP", Latitude = -20.8, Longitude = -49.4, EnderecoPublico = enderecoPublico,
            PrecoVenda = 500000, PrecoLocacao = 2500, Quartos = 3, Suites = 1, Banheiros = 2, Vagas = 2, AreaConstruidaM2 = 150, ValorIptu = 900,
            DescricaoPublica = "Casa ampla.",
        },
        null,
        [new ImovelCaracteristica { Chave = "Piscina", Publico = true }, new ImovelCaracteristica { Chave = "Cofre", Publico = false }],
        [], [], []);

    [Fact]
    public void Anuncio_json_ld_tem_oferta_e_imovel_sem_endereco_privado()
    {
        var op = Op("https://www.inovabens.com.br");
        var json = AnuncioSite.FichaJsonLd(op, Detalhe(enderecoPublico: false), null, ["site/foto/1/a.webp"], "Casa ampla.");
        Assert.DoesNotContain("Rua Secreta", json);
        Assert.DoesNotContain("Cofre", json);
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;
        Assert.Equal("RealEstateListing", r.GetProperty("@type").GetString());
        Assert.Equal("https://www.inovabens.com.br/site/foto/1/a.webp", r.GetProperty("image")[0].GetString());
        var oferta = r.GetProperty("offers");
        Assert.Equal(500000, oferta.GetProperty("price").GetDecimal());
        Assert.Equal("BRL", oferta.GetProperty("priceCurrency").GetString());
        Assert.EndsWith("#Sell", oferta.GetProperty("businessFunction").GetString());
        var lugar = r.GetProperty("about");
        Assert.Equal("SingleFamilyResidence", lugar.GetProperty("@type").GetString());
        Assert.Equal(3, lugar.GetProperty("numberOfBedrooms").GetInt32());
        Assert.False(lugar.TryGetProperty("geo", out _));
        Assert.False(lugar.GetProperty("address").TryGetProperty("streetAddress", out _));

        var publico = AnuncioSite.FichaJsonLd(op, Detalhe(enderecoPublico: true, FinalidadeImovel.LOCACAO), null, [], "x");
        using var p = JsonDocument.Parse(publico);
        Assert.Contains("Rua Secreta", publico);
        var of = p.RootElement.GetProperty("offers");
        Assert.EndsWith("#LeaseOut", of.GetProperty("businessFunction").GetString());
        Assert.Equal("MON", of.GetProperty("priceSpecification").GetProperty("unitCode").GetString());
        Assert.Equal(2500, of.GetProperty("price").GetDecimal());
    }

    [Fact]
    public void Ficha_em_markdown_mostra_o_mesmo_que_a_pagina_sem_dado_privado()
    {
        var md = AnuncioSite.FichaMarkdown(Op(), Detalhe(enderecoPublico: false), null, [], temporada: true);
        Assert.StartsWith("# Casa com piscina", md);
        Assert.Contains("R$ 500.000", md);
        Assert.Contains("- Piscina", md);
        Assert.DoesNotContain("Cofre", md);
        Assert.DoesNotContain("Rua Secreta", md);
        Assert.Contains("site/temporada/", md);
    }

    [Theory]
    [InlineData("text/html,application/xhtml+xml,*/*;q=0.8", false)]
    [InlineData("text/markdown", true)]
    [InlineData("text/markdown, text/html;q=0.9", true)]
    [InlineData("text/html, text/markdown;q=0.5", false)]
    [InlineData("", false)]
    public void Markdown_so_para_quem_pede(string accept, bool esperado)
    {
        var c = new DefaultHttpContext();
        c.Request.Headers.Accept = accept;
        Assert.Equal(esperado, AnuncioSite.QuerMarkdown(c.Request));
    }

    [Fact]
    public void Css_do_site_usa_endereco_com_impressao_digital_das_rotas_do_blazor()
    {
        // o site é minimal API: sem isso o CSS saía sem impressão digital e com "no-cache" (revalidado a cada visita)
        var colecao = new Microsoft.AspNetCore.Components.ResourceAssetCollection(
        [
            new("site-previa.abc123.css", [new("label", "site-previa.css"), new("fingerprint", "abc123")]),
        ]);
        var rota = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new object[] { colecao }), "blazor");
        Assert.Equal("site-previa.abc123.css", new AtivosSite(new DefaultEndpointDataSource(rota))["site-previa.css"]);
        // sem a coleção (ex.: testes sem MapStaticAssets), volta o nome original em vez de quebrar a página
        Assert.Equal("site-previa.css", new AtivosSite(new DefaultEndpointDataSource())["site-previa.css"]);
    }

    [Fact]
    public void Llms_txt_aponta_para_markdown_e_feed()
    {
        var t = Seo.LlmsTxt(Op("https://www.inovabens.com.br"));
        Assert.StartsWith("# Inova Bens", t);
        Assert.Contains("https://www.inovabens.com.br/site/imoveis.json", t);
        Assert.Contains("https://www.inovabens.com.br/site/imoveis.md?finalidade=venda", t);
    }
}
