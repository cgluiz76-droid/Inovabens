using System.Text.Json;
using System.Xml.Linq;
using Inovabens.Core;
using Inovabens.Web;
using Inovabens.Web.Components.Site;
using Microsoft.AspNetCore.Http;

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
    public void Robots_nao_bloqueia_e_so_cita_sitemap_com_site_publico()
    {
        var privado = Seo.RobotsTxt(Op());
        Assert.Contains("Allow: /", privado);
        Assert.DoesNotContain("Disallow", privado);
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
}
