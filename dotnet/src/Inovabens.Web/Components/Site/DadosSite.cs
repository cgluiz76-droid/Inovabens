using System.Text.Json;
using Inovabens.Core;

namespace Inovabens.Web.Components.Site;

/// <summary>
/// Dados estruturados (JSON-LD, schema.org) das páginas do site. Só o que é verdadeiro e aparece na página:
/// a imobiliária (nome, CRECI, endereço e telefone do rodapé) e a trilha de navegação. O Google usa a trilha
/// (BreadcrumbList) nos resultados; a organização ajuda a identificar a empresa. Não há avaliações nem preços
/// marcados como oferta: o Google não tem resultado rico para anúncio de imóvel.
/// </summary>
public static class DadosSite
{
    // o serializador padrão escapa < > & e acentos (\uXXXX): seguro dentro de <script>
    private static string Json(object o) => JsonSerializer.Serialize(o);

    public static string Imobiliaria(InovabensOpcoes op) => Json(new Dictionary<string, object>
    {
        ["@context"] = "https://schema.org",
        ["@type"] = "RealEstateAgent",
        ["name"] = "Inova Bens Gestão Patrimonial",
        ["alternateName"] = "Inovabens",
        ["description"] = "Imobiliária em São José do Rio Preto (CRECI 50457-J): compra, venda, locação e regularização de imóveis.",
        ["url"] = Seo.Absoluto(op, "site"),
        ["logo"] = Seo.Absoluto(op, "img/marca.svg"),
        ["telephone"] = "+55 17 98185-0171",
        ["address"] = new Dictionary<string, object>
        {
            ["@type"] = "PostalAddress",
            ["streetAddress"] = "Av. Pres. Juscelino K. de Oliveira, 391 - Jardim Panorama",
            ["addressLocality"] = "São José do Rio Preto",
            ["addressRegion"] = "SP",
            ["addressCountry"] = "BR",
        },
        ["areaServed"] = "São José do Rio Preto e região",
    });

    /// <summary>Trilha da página: (nome, caminho). O último item é a própria página.</summary>
    public static string Trilha(InovabensOpcoes op, params (string Nome, string Caminho)[] itens) => Json(new Dictionary<string, object>
    {
        ["@context"] = "https://schema.org",
        ["@type"] = "BreadcrumbList",
        ["itemListElement"] = itens.Select((x, i) => new Dictionary<string, object>
        {
            ["@type"] = "ListItem",
            ["position"] = i + 1,
            ["name"] = x.Nome,
            ["item"] = Seo.Absoluto(op, x.Caminho),
        }).ToList(),
    });

    /// <summary>Resumo de até ~155 caracteres, cortado em palavra (tamanho que o Google costuma mostrar).</summary>
    public static string Resumo(string texto, int limite = 155)
    {
        var t = string.Join(' ', texto.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (t.Length <= limite) return t;
        var corte = t.LastIndexOf(' ', limite - 1);
        return t[..(corte > 60 ? corte : limite - 1)].TrimEnd(',', ';', '.', ' ', '–', '-') + "…";
    }
}
