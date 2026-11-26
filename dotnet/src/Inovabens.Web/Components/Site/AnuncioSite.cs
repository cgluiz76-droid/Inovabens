using System.Globalization;
using System.Text;
using System.Text.Json;
using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Locacao;

namespace Inovabens.Web.Components.Site;

/// <summary>
/// O anúncio do site em formatos para máquinas (agentes de IA, buscadores, integrações): JSON-LD schema.org
/// (RealEstateListing com a oferta e o imóvel), Markdown da ficha e itens do feed. Só o que a página mostra ao visitante:
/// endereço e coordenadas apenas quando o endereço é público, características públicas, preço exibido.
/// </summary>
public static class AnuncioSite
{
    private static readonly CultureInfo Br = Texto.PtBr;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string Venda = "http://purl.org/goodrelations/v1#Sell";
    private const string Aluguel = "http://purl.org/goodrelations/v1#LeaseOut";

    // o serializador padrão escapa < > & (seguro dentro de <script>)
    private static string Json(object o) => JsonSerializer.Serialize(o);

    /// <summary>Preço exibido na ficha: aluguel mensal, venda ou "a partir de" (lote mais barato do empreendimento).</summary>
    public static (decimal? Valor, bool Locacao, bool APartir) Preco(Imovel im, QuadroLotes? quadro)
    {
        var locacao = im.Finalidade == FinalidadeImovel.LOCACAO;
        var aPartir = !locacao && quadro?.APartirDe is not null;
        return (locacao ? im.PrecoLocacao : quadro?.APartirDe ?? im.PrecoVenda ?? im.PrecoLocacao, locacao, aPartir);
    }

    /// <summary>Tipo schema.org do imóvel (apartamento, casa, terreno/área, comercial).</summary>
    private static string TipoSchema(Imovel im) => (im.Tipo + " " + im.Subtipo).ToLowerInvariant() switch
    {
        var t when t.Contains("apart") || t.Contains("kitnet") || t.Contains("flat") || t.Contains("cobertura") => "Apartment",
        var t when t.Contains("casa") || t.Contains("sobrado") || t.Contains("chácara") || t.Contains("sítio") || t.Contains("fazenda") => "SingleFamilyResidence",
        var t when t.Contains("terreno") || t.Contains("lote") || t.Contains("área") => "Landform",
        _ => "Accommodation",
    };

    private static Dictionary<string, object> Endereco(Imovel im)
    {
        var e = new Dictionary<string, object> { ["@type"] = "PostalAddress", ["addressCountry"] = "BR" };
        if (im.EnderecoPublico && im.Logradouro is { } l) e["streetAddress"] = im.Numero is { } n ? $"{l}, {n}" : l;
        if (im.Bairro is { } b) e["addressLocality"] = im.Cidade is { } c0 ? $"{b}, {c0}" : b;
        else if (im.Cidade is { } c) e["addressLocality"] = c;
        if (im.Uf is { } uf) e["addressRegion"] = uf;
        if (im.EnderecoPublico && im.Cep is { } cep) e["postalCode"] = cep;
        return e;
    }

    private static Dictionary<string, object> M2(decimal v) => new()
    {
        ["@type"] = "QuantitativeValue", ["value"] = v, ["unitCode"] = "MTK", ["unitText"] = "m²",
    };

    /// <summary>O imóvel em si (Apartment, SingleFamilyResidence, Landform…): números, endereço público e características.</summary>
    private static Dictionary<string, object> Lugar(Imovel im, IEnumerable<ImovelCaracteristica> caracteristicas, string url)
    {
        var tipo = TipoSchema(im);
        var l = new Dictionary<string, object> { ["@type"] = tipo, ["name"] = im.Titulo, ["url"] = url, ["address"] = Endereco(im) };
        if (im.EnderecoPublico && im.Latitude is { } lat && im.Longitude is { } lng)
            l["geo"] = new Dictionary<string, object> { ["@type"] = "GeoCoordinates", ["latitude"] = lat, ["longitude"] = lng };
        if (tipo == "Landform")
        {
            if ((im.AreaTerrenoM2 ?? im.AreaTotalM2) is { } t) l["additionalProperty"] = new[] { Propriedade("Área do terreno (m²)", t) };
            return l;
        }
        if ((im.AreaConstruidaM2 ?? im.AreaUtilM2 ?? im.AreaTotalM2) is { } a) l["floorSize"] = M2(a);
        if (im.Quartos is > 0 and var q) l["numberOfBedrooms"] = q;
        if ((im.Quartos ?? 0) + (im.Salas ?? 0) is > 0 and var c) l["numberOfRooms"] = c;
        if (im.Banheiros is > 0 and var b) l["numberOfBathroomsTotal"] = b;
        if (im.AnoConstrucao is { } ano) l["yearBuilt"] = ano;
        var extras = new List<object>();
        if (im.Suites is > 0 and var s) extras.Add(Propriedade("Suítes", s));
        if (im.Vagas is > 0 and var v) extras.Add(Propriedade("Vagas de garagem", v));
        if (im.AreaTerrenoM2 is { } at) extras.Add(Propriedade("Área do terreno (m²)", at));
        if (extras.Count > 0) l["additionalProperty"] = extras;
        var feat = caracteristicas.Where(x => x.Publico).Take(40)
            .Select(x => new Dictionary<string, object>
            {
                ["@type"] = "LocationFeatureSpecification", ["name"] = x.Valor is null ? x.Chave : $"{x.Chave}: {x.Valor}", ["value"] = true,
            }).ToList();
        if (feat.Count > 0) l["amenityFeature"] = feat;
        return l;
    }

    private static Dictionary<string, object> Propriedade(string nome, object valor) =>
        new() { ["@type"] = "PropertyValue", ["name"] = nome, ["value"] = valor };

    private static Dictionary<string, object> Oferta(decimal? preco, bool locacao, bool aPartir, string url, string? unidade = null)
    {
        var o = new Dictionary<string, object>
        {
            ["@type"] = "Offer", ["url"] = url, ["priceCurrency"] = "BRL",
            ["businessFunction"] = locacao || unidade is not null ? Aluguel : Venda,
            ["availability"] = "https://schema.org/InStock",
            ["seller"] = new Dictionary<string, object> { ["@type"] = "RealEstateAgent", ["name"] = "Inova Bens Gestão Patrimonial" },
        };
        if (preco is not { } p) return o;
        if (locacao || unidade is not null)
            o["priceSpecification"] = new Dictionary<string, object>
            {
                ["@type"] = "UnitPriceSpecification", ["price"] = p, ["priceCurrency"] = "BRL", ["unitCode"] = unidade ?? "MON",
                ["unitText"] = unidade == "DAY" ? "diária" : "mês",
            };
        if (aPartir) o["priceSpecification"] = new Dictionary<string, object> { ["@type"] = "PriceSpecification", ["minPrice"] = p, ["priceCurrency"] = "BRL" };
        else o["price"] = p;
        return o;
    }

    /// <summary>JSON-LD da ficha (site/imovel/{id}): o anúncio, a oferta e o imóvel.</summary>
    public static string FichaJsonLd(InovabensOpcoes op, ImovelDetalhe d, QuadroLotes? quadro, IReadOnlyList<string> fotos, string descricao)
    {
        var im = d.Dados;
        var url = Seo.Absoluto(op, $"site/imovel/{im.Id}");
        var (preco, locacao, aPartir) = Preco(im, quadro);
        var lugar = Lugar(im, d.Caracteristicas, url);
        var j = new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "RealEstateListing",
            ["@id"] = url + "#anuncio",
            ["url"] = url,
            ["name"] = im.Titulo,
            ["description"] = descricao,
            ["identifier"] = im.Referencia ?? im.Codigo,
            ["inLanguage"] = "pt-BR",
            ["dateModified"] = im.AtualizadoEm.ToString("yyyy-MM-dd", Inv),
            ["about"] = lugar,
            ["offers"] = Oferta(preco, locacao, aPartir, url),
            ["provider"] = new Dictionary<string, object> { ["@type"] = "RealEstateAgent", ["name"] = "Inova Bens Gestão Patrimonial", ["url"] = Seo.Absoluto(op, "site") },
        };
        ((Dictionary<string, object>)j["offers"])["itemOffered"] = new Dictionary<string, object> { ["@id"] = url + "#imovel" };
        lugar["@id"] = url + "#imovel";
        if (fotos.Count > 0) j["image"] = fotos.Take(10).Select(f => Seo.Absoluto(op, f)).ToList();
        if (im.ValorCondominio is { } cond) j["additionalProperty"] = new List<object> { Propriedade("Condomínio (R$/mês)", cond) };
        if (im.ValorIptu is { } iptu)
        {
            var lista = j.TryGetValue("additionalProperty", out var x) ? (List<object>)x : [];
            lista.Add(Propriedade("IPTU (R$)", iptu));
            j["additionalProperty"] = lista;
        }
        return Json(j);
    }

    /// <summary>JSON-LD da temporada (site/temporada/{id}): o imóvel e a oferta por diária.</summary>
    public static string TemporadaJsonLd(InovabensOpcoes op, TemporadaPublica t)
    {
        var url = Seo.Absoluto(op, $"site/temporada/{t.ImovelId}");
        var oferta = Oferta(t.Diaria, true, false, url, "DAY");
        oferta["eligibleDuration"] = new Dictionary<string, object> { ["@type"] = "QuantitativeValue", ["minValue"] = t.MinimoNoites, ["maxValue"] = 90, ["unitCode"] = "DAY" };
        var j = new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "Accommodation",
            ["@id"] = url + "#imovel",
            ["url"] = url,
            ["name"] = $"Temporada: {t.Titulo}",
            ["identifier"] = t.Codigo,
            ["occupancy"] = new Dictionary<string, object> { ["@type"] = "QuantitativeValue", ["maxValue"] = t.HospedesMax },
            ["checkinTime"] = t.Checkin, ["checkoutTime"] = t.Checkout,
            ["offers"] = oferta,
        };
        if (t.Cidade is { } c) j["address"] = new Dictionary<string, object> { ["@type"] = "PostalAddress", ["addressLocality"] = c, ["addressCountry"] = "BR" };
        if (t.Regras is { } r) j["description"] = r;
        return Json(j);
    }

    /// <summary>Resumo de um imóvel da vitrine como anúncio (lista e feed).</summary>
    public static Dictionary<string, object> Item(InovabensOpcoes op, VitrineItem v)
    {
        var url = Seo.Absoluto(op, $"site/imovel/{v.Id}");
        var locacao = v.Finalidade == FinalidadeImovel.LOCACAO;
        var preco = locacao ? v.PrecoLocacao : v.PrecoVenda ?? v.PrecoLocacao;
        var lugar = new Dictionary<string, object> { ["@type"] = "Accommodation", ["name"] = v.Titulo };
        var end = new Dictionary<string, object> { ["@type"] = "PostalAddress", ["addressCountry"] = "BR" };
        if (v.Cidade is { } c) end["addressLocality"] = v.Bairro is { } b ? $"{b}, {c}" : c;
        if (v.Uf is { } uf) end["addressRegion"] = uf;
        lugar["address"] = end;
        if (v.Area is { } a) lugar["floorSize"] = M2(a);
        if (v.Quartos is > 0 and var q) lugar["numberOfBedrooms"] = q;
        var item = new Dictionary<string, object>
        {
            ["@type"] = "RealEstateListing", ["url"] = url, ["name"] = v.Titulo, ["identifier"] = v.Codigo,
            ["about"] = lugar, ["offers"] = Oferta(preco, locacao, v.APartirDe, url),
            ["subjectOf"] = new Dictionary<string, object> { ["@type"] = "DigitalDocument", ["encodingFormat"] = "text/markdown", ["url"] = url + ".md" },
        };
        if (v.Capa is { } capa) item["image"] = Seo.Absoluto(op, capa);
        return item;
    }

    /// <summary>JSON-LD da lista (site/imoveis): os anúncios em ordem.</summary>
    public static string ListaJsonLd(InovabensOpcoes op, string nome, IReadOnlyList<VitrineItem> itens) => Json(new Dictionary<string, object>
    {
        ["@context"] = "https://schema.org",
        ["@type"] = "ItemList",
        ["name"] = nome,
        ["numberOfItems"] = itens.Count,
        ["itemListElement"] = itens.Take(100).Select((v, i) => new Dictionary<string, object>
        {
            ["@type"] = "ListItem", ["position"] = i + 1, ["url"] = Seo.Absoluto(op, $"site/imovel/{v.Id}"), ["name"] = v.Titulo,
        }).ToList(),
    });

    /// <summary>Feed com todos os anúncios públicos (site/imoveis.json), em schema.org.</summary>
    public static string Feed(InovabensOpcoes op, IReadOnlyList<VitrineItem> itens) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["@context"] = "https://schema.org",
        ["@type"] = "ItemList",
        ["name"] = "Imóveis anunciados pela Inova Bens Gestão Patrimonial",
        ["url"] = Seo.Absoluto(op, "site/imoveis.json"),
        ["dateModified"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv),
        ["numberOfItems"] = itens.Count,
        ["provider"] = new Dictionary<string, object> { ["@type"] = "RealEstateAgent", ["name"] = "Inova Bens Gestão Patrimonial", ["telephone"] = "+55 17 98185-0171" },
        ["itemListElement"] = itens.Select((v, i) => new Dictionary<string, object> { ["@type"] = "ListItem", ["position"] = i + 1, ["item"] = Item(op, v) }).ToList(),
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /* ------------------------------- Markdown ------------------------------- */

    private static string Moeda(decimal v) => v.ToString("C0", Br);
    private static string Num(decimal v) => v.ToString("#,##0.##", Br);

    /// <summary>A ficha em Markdown (site/imovel/{id}.md ou Accept: text/markdown): mesmo conteúdo da página, sem layout.</summary>
    public static string FichaMarkdown(InovabensOpcoes op, ImovelDetalhe d, QuadroLotes? quadro, IReadOnlyList<string> fotos, bool temporada)
    {
        var im = d.Dados;
        var (preco, locacao, aPartir) = Preco(im, quadro);
        var url = Seo.Absoluto(op, $"site/imovel/{im.Id}");
        var sb = new StringBuilder();
        sb.AppendLine($"# {im.Titulo}").AppendLine();
        sb.AppendLine($"- **Referência:** {im.Referencia ?? im.Codigo}");
        sb.AppendLine($"- **Negócio:** {(locacao ? "locação" : im.Finalidade == FinalidadeImovel.AMBOS ? "venda (também para locação)" : "venda")}");
        sb.AppendLine($"- **Tipo:** {im.Subtipo ?? Texto.Rotulo(im.Tipo)}");
        sb.AppendLine($"- **Preço:** {(preco is { } p ? (aPartir ? "a partir de " : "") + Moeda(p) + (locacao ? " por mês" : "") : "consulte")}");
        if (im.ValorCondominio is { } cond) sb.AppendLine($"- **Condomínio:** {Moeda(cond)} por mês");
        if (im.ValorIptu is { } iptu) sb.AppendLine($"- **IPTU:** {Moeda(iptu)}");
        var onde = string.Join(", ", new[] { im.EnderecoPublico ? (im.Logradouro is { } l ? (im.Numero is { } n ? $"{l}, {n}" : l) : null) : null, im.Bairro, im.Cidade is { } c ? c + (im.Uf is { } uf ? "/" + uf : "") : null }.Where(x => x is not null));
        if (onde.Length > 0) sb.AppendLine($"- **Localização:** {onde}{(im.EnderecoPublico ? "" : " (endereço exato informado no atendimento)")}");
        if (im.AreaTotalM2 is { } at) sb.AppendLine($"- **Área total:** {Num(at)} m²");
        if (im.AreaConstruidaM2 is { } ac) sb.AppendLine($"- **Área construída:** {Num(ac)} m²");
        if (im.AreaUtilM2 is { } au) sb.AppendLine($"- **Área útil:** {Num(au)} m²");
        if (im.AreaTerrenoM2 is { } atr) sb.AppendLine($"- **Área do terreno:** {Num(atr)} m²");
        if (im.Quartos is > 0 and var q) sb.AppendLine($"- **Quartos:** {q}{(im.Suites is > 0 and var s ? $" ({s} suíte{(s > 1 ? "s" : "")})" : "")}");
        if (im.Banheiros is > 0 and var b) sb.AppendLine($"- **Banheiros:** {b}");
        if (im.Vagas is > 0 and var v) sb.AppendLine($"- **Vagas:** {v}");
        if (im.AnoConstrucao is { } ano) sb.AppendLine($"- **Ano de construção:** {ano}");
        sb.AppendLine();
        if (Texto.Limpo(im.DescricaoPublica) is { } desc) sb.AppendLine("## Descrição").AppendLine().AppendLine(desc.Trim()).AppendLine();
        foreach (var g in d.Caracteristicas.Where(x => x.Publico).GroupBy(x => x.Grupo ?? "Características"))
        {
            sb.AppendLine($"## {g.Key}").AppendLine();
            foreach (var x in g) sb.AppendLine($"- {(x.Valor is null ? x.Chave : $"{x.Chave}: {x.Valor}")}");
            sb.AppendLine();
        }
        if (quadro is { } qd && qd.Lotes.Count > 0)
        {
            sb.AppendLine("## Lotes").AppendLine();
            sb.AppendLine($"Empreendimento com {qd.Lotes.Count} lote(s); quadro completo na página do imóvel.").AppendLine();
        }
        if (fotos.Count > 0)
        {
            sb.AppendLine("## Fotos").AppendLine();
            foreach (var f in fotos.Take(20)) sb.AppendLine($"- {Seo.Absoluto(op, f)}");
            sb.AppendLine();
        }
        sb.AppendLine("## Como negociar").AppendLine();
        sb.AppendLine($"- Página do anúncio: {url}");
        if (temporada) sb.AppendLine($"- Aluguel por temporada (diárias, dias livres e pedido de reserva): {Seo.Absoluto(op, $"site/temporada/{im.Id}")}");
        sb.AppendLine($"- WhatsApp: {SiteMoldura.TelefoneTexto} (https://wa.me/{SiteMoldura.Whatsapp})");
        sb.AppendLine("- Inova Bens Gestão Patrimonial, CRECI 50457-J, São José do Rio Preto/SP");
        return sb.ToString();
    }

    /// <summary>A lista em Markdown (site/imoveis?finalidade=… com Accept: text/markdown ou site/imoveis.md).</summary>
    public static string ListaMarkdown(InovabensOpcoes op, string titulo, IReadOnlyList<VitrineItem> itens)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {titulo}").AppendLine();
        sb.AppendLine($"{itens.Count} imóvel(is). Cada linha leva à ficha; a ficha em Markdown fica no mesmo endereço com \".md\" no fim.").AppendLine();
        foreach (var v in itens)
        {
            var locacao = v.Finalidade == FinalidadeImovel.LOCACAO;
            var preco = locacao ? v.PrecoLocacao : v.PrecoVenda ?? v.PrecoLocacao;
            var partes = new List<string> { v.Tipo };
            if (v.Cidade is { } c) partes.Add(v.Bairro is { } b ? $"{b}, {c}" : c);
            if (v.Area is { } a) partes.Add($"{Num(a)} m²");
            if (v.Quartos is > 0 and var q) partes.Add($"{q} quarto(s)");
            if (v.Vagas is > 0 and var vg) partes.Add($"{vg} vaga(s)");
            partes.Add(preco is { } p ? (v.APartirDe ? "a partir de " : "") + Moeda(p) + (locacao ? "/mês" : "") : "preço: consulte");
            sb.AppendLine($"- [{v.Codigo} · {v.Titulo}]({Seo.Absoluto(op, $"site/imovel/{v.Id}")}): {string.Join(" · ", partes)}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Quem pediu Markdown no Accept (agentes de IA, ferramentas de linha de comando) e não preferiu HTML.
    /// Navegadores mandam text/html primeiro e nunca text/markdown.
    /// </summary>
    public static bool QuerMarkdown(HttpRequest req)
    {
        var accept = req.Headers.Accept.ToString();
        if (!accept.Contains("text/markdown", StringComparison.OrdinalIgnoreCase)) return false;
        double Q(string tipo)
        {
            foreach (var parte in accept.Split(','))
            {
                var p = parte.Trim();
                if (!p.StartsWith(tipo, StringComparison.OrdinalIgnoreCase)) continue;
                var i = p.IndexOf("q=", StringComparison.OrdinalIgnoreCase);
                return i >= 0 && double.TryParse(p[(i + 2)..].Trim(), NumberStyles.Float, Inv, out var q) ? q : 1;
            }
            return -1;
        }
        return Q("text/markdown") >= Q("text/html");
    }

    public static IResult Markdown(HttpContext c, string texto)
    {
        c.Response.Headers.Vary = "Accept";
        return Results.Text(texto, "text/markdown; charset=utf-8");
    }
}
