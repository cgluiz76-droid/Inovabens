using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inovabens.Core.Comum;

namespace Inovabens.Core.Imoveis;

/// <summary>Foto de um anúncio, na ordem da galeria do site.</summary>
public sealed record FotoAnuncio(string Url, string NomeArquivo, string? Legenda);

/// <summary>Dados públicos de um anúncio do site Imobibrasil (template das fichas em /imovel/{id}/{slug}).</summary>
public sealed record AnuncioImobibrasil
{
    public long Id { get; init; }
    public string Url { get; init; } = "";
    public string? Referencia { get; init; }
    /// <summary>Tipo como o Imobibrasil chama (Casa, Área, Barracão...).</summary>
    public string TipoOriginal { get; init; } = "";
    public bool Locacao { get; init; }
    public decimal? Preco { get; init; }
    public string? Logradouro { get; init; }
    public string? Numero { get; init; }
    public string? Complemento { get; init; }
    public string? Bairro { get; init; }
    /// <summary>Bairro como veio (no Imobibrasil o campo costuma levar texto de anúncio depois da vírgula).</summary>
    public string? BairroOriginal { get; init; }
    public string? Cidade { get; init; }
    public string? Uf { get; init; }
    public string? Cep { get; init; }
    public string? Zona { get; init; }
    public string? Regiao { get; init; }
    public string? PontoReferencia { get; init; }
    public decimal? AreaTotalM2 { get; init; }
    public decimal? AreaConstruidaM2 { get; init; }
    public decimal? AreaUtilM2 { get; init; }
    public decimal? AreaTerrenoM2 { get; init; }
    public decimal? TerrenoFrenteM { get; init; }
    public decimal? TerrenoFundoM { get; init; }
    public decimal? TerrenoEsquerdaM { get; init; }
    public decimal? TerrenoDireitaM { get; init; }
    public string? Pavimento { get; init; }
    public int? AnoConstrucao { get; init; }
    public bool AceitaPermuta { get; init; }
    public bool EmCondominio { get; init; }
    public decimal? ValorCondominio { get; init; }
    public decimal? ValorIptu { get; init; }
    public int? Quartos { get; init; }
    public int? Suites { get; init; }
    public int? Banheiros { get; init; }
    public int? Salas { get; init; }
    public int? Vagas { get; init; }
    /// <summary>Primeira linha da descrição (o título de anúncio que o corretor escreveu).</summary>
    public string? Chamada { get; init; }
    public string? Descricao { get; init; }
    public IReadOnlyList<(string Grupo, string Item)> Caracteristicas { get; init; } = [];
    public IReadOnlyList<FotoAnuncio> Fotos { get; init; } = [];
    public string? VideoUrl { get; init; }
    /// <summary>Rótulos da tabela de dados que o leitor não reconheceu (para não perder informação nova).</summary>
    public IReadOnlyDictionary<string, string> OutrosCampos { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Lê as páginas públicas do site feito no Imobibrasil. Só usa o que o site publica: proprietário, documentos e
/// dados internos não aparecem lá.
/// </summary>
public static partial class LeitorImobibrasil
{
    /// <summary>As páginas vêm declaradas como iso-8859-1, mas algumas são UTF-8 de fato (ex.: com emoji).</summary>
    public static string Decodificar(byte[] bytes)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    /// <summary>Links das fichas numa página de lista (/imovel/venda/?pag=N): id e caminho.</summary>
    public static IReadOnlyList<(long Id, string Caminho)> LinksDaLista(string html) =>
        [.. LinkFicha().Matches(html).Select(m => (long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Value)).DistinctBy(x => x.Item1)];

    public static AnuncioImobibrasil LerFicha(long id, string url, string html)
    {
        var campos = Campos(html);
        string? C(params string[] nomes) => nomes.Select(n => campos.GetValueOrDefault(n)).FirstOrDefault(v => Util(v) is not null) is { } v ? Util(v) : null;

        // JSON-LD: preço, endereço, cidade, CEP e o código (sku)
        decimal? preco = null; string? cidade = null, uf = null, cep = null, sku = null, ruaLd = null; bool locacao = false;
        foreach (Match m in JsonLd().Matches(html))
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value);
                var r = doc.RootElement;
                var tipo = r.TryGetProperty("@type", out var t) ? t.GetString() : null;
                if (tipo is "BuyAction" or "RentAction")
                {
                    locacao = tipo == "RentAction";
                    if (r.TryGetProperty("price", out var p) && decimal.TryParse(p.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var pv) && pv > 0) preco = pv;
                    if (r.TryGetProperty("object", out var o) && o.TryGetProperty("address", out var a))
                    {
                        cidade = Prop(a, "addressLocality"); uf = Prop(a, "addressRegion"); cep = Prop(a, "postalCode"); ruaLd = Prop(a, "streetAddress");
                    }
                }
                else if (tipo == "Product") sku = Prop(r, "sku");
            }
            catch (JsonException) { /* bloco malformado: ignora */ }
        }

        var trilha = Trilha().Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
        // "Casa em Condomínio para Venda" → tipo e finalidade
        var tipoTrilha = trilha.FirstOrDefault(x => ParaFinalidade().IsMatch(x) && !x.StartsWith("Imóveis", StringComparison.Ordinal));
        string tipoOriginal = "Outro";
        if (tipoTrilha is not null)
        {
            var mt = ParaFinalidade().Match(tipoTrilha);
            tipoOriginal = mt.Groups[1].Value.Trim();
            locacao = mt.Groups[2].Value.StartsWith("Loca", StringComparison.OrdinalIgnoreCase);
        }

        var (logradouro, numero, complExtra) = Endereco(C("Endereço") ?? ruaLd);
        var complemento = string.Join(", ", new[] { C("Complemento"), complExtra }.Where(x => x is not null));
        var bairroOriginal = C("Bairro")?.TrimEnd('›', ' ');

        var descHtml = Descricao().Match(html) is { Success: true } d ? d.Groups[1].Value : null;
        var descricao = descHtml is null ? null : DescricaoTexto(descHtml);
        var chamada = descricao?.Split('\n', 2)[0].Trim();

        var caracteristicas = new List<(string, string)>();
        foreach (Match g in Grupo().Matches(html))
        {
            var nome = TextoSimples(g.Groups[1].Value);
            foreach (Match i in Item().Matches(g.Groups[2].Value))
                if (TextoSimples(i.Groups[1].Value) is { Length: > 0 } item) caracteristicas.Add((nome, item));
        }

        var fotos = new List<FotoAnuncio>();
        foreach (Match f in Foto().Matches(html))
        {
            var u = f.Groups[1].Value;
            if (fotos.Any(x => x.Url == u)) continue;
            var nomeArq = Path.GetFileName(new Uri(u).AbsolutePath);
            fotos.Add(new FotoAnuncio(u, nomeArq, TextoSimples(f.Groups[2].Value) is { Length: > 0 } leg ? leg : null));
        }

        var video = Youtube().Match(html) is { Success: true } y ? $"https://www.youtube.com/watch?v={y.Groups[1].Value}" : null;

        var conhecidos = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Código", "Endereço", "Complemento", "Bairro", "Zona", "Região", "Ponto de Referência", "Área Total", "Área Construída",
            "Área Útil", "Área Terreno", "Terreno Frente", "Terreno Fundo", "Terreno Esquerda", "Terreno Direita", "Pavimento",
            "Construído em", "Permuta", "Em Condomínio", "Condomínio", "IPTU", "Dormitórios", "Dormitório", "Suíte", "Suítes",
            "Banheiro", "Banheiros", "Sala", "Salas", "Vaga", "Vagas", "Valor por m²",
        };

        return new AnuncioImobibrasil
        {
            Id = id, Url = url,
            Referencia = Util(sku) ?? C("Código"),
            TipoOriginal = tipoOriginal, Locacao = locacao,
            Preco = preco ?? Dinheiro(PrecoTopo().Match(html) is { Success: true } pt ? pt.Groups[1].Value : null),
            Logradouro = logradouro, Numero = numero, Complemento = Util(complemento),
            Bairro = BairroCurto(bairroOriginal), BairroOriginal = bairroOriginal,
            Cidade = Util(cidade), Uf = Util(uf)?.ToUpperInvariant(), Cep = FormatarCep(cep),
            Zona = C("Zona"), Regiao = C("Região"), PontoReferencia = C("Ponto de Referência"),
            AreaTotalM2 = Area(C("Área Total")), AreaConstruidaM2 = Area(C("Área Construída")),
            AreaUtilM2 = Area(C("Área Útil")), AreaTerrenoM2 = Area(C("Área Terreno")),
            TerrenoFrenteM = Area(C("Terreno Frente")), TerrenoFundoM = Area(C("Terreno Fundo")),
            TerrenoEsquerdaM = Area(C("Terreno Esquerda")), TerrenoDireitaM = Area(C("Terreno Direita")),
            Pavimento = C("Pavimento"),
            AnoConstrucao = Inteiro(C("Construído em")) is { } ano and >= 1800 and <= 2100 ? ano : null,
            AceitaPermuta = Sim(C("Permuta")), EmCondominio = Sim(C("Em Condomínio")),
            ValorCondominio = Dinheiro(C("Condomínio")), ValorIptu = Dinheiro(C("IPTU")),
            Quartos = Inteiro(C("Dormitórios", "Dormitório")), Suites = Inteiro(C("Suítes", "Suíte")),
            Banheiros = Inteiro(C("Banheiros", "Banheiro")), Salas = Inteiro(C("Salas", "Sala")), Vagas = Inteiro(C("Vagas", "Vaga")),
            Chamada = chamada, Descricao = descricao,
            Caracteristicas = caracteristicas, Fotos = fotos, VideoUrl = video,
            OutrosCampos = campos.Where(kv => !conhecidos.Contains(kv.Key) && Util(kv.Value) is not null).ToDictionary(),
        };
    }

    /* ------------------------------ auxiliares ------------------------------ */

    /// <summary>Linhas "Rótulo: valor" da tabela de dados da ficha.</summary>
    private static Dictionary<string, string> Campos(string html)
    {
        var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inicio = html.IndexOf("C&oacute;digo: ", StringComparison.Ordinal);
        if (inicio < 0) inicio = html.IndexOf("Código: ", StringComparison.Ordinal);
        if (inicio < 0) return r;
        var fim = html.IndexOf("tb_features", inicio, StringComparison.Ordinal);
        var trecho = html[(html.LastIndexOf("<td>", inicio, StringComparison.Ordinal) is var td and >= 0 ? td : inicio)..(fim < 0 ? html.Length : fim)];
        foreach (Match m in Celula().Matches(trecho))
        {
            var t = TextoSimples(m.Groups[1].Value);
            var i = t.IndexOf(':');
            if (i > 0) r.TryAdd(t[..i].Trim(), t[(i + 1)..].Trim());
        }
        return r;
    }

    private static string? Prop(JsonElement e, string nome) =>
        e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? Util(WebUtility.HtmlDecode(v.GetString())) : null;

    /// <summary>Valores que o Imobibrasil mostra quando o campo está vazio ("N", "0").</summary>
    private static string? Util(string? v) => Texto.Limpo(v) is { } t && t is not ("N" or "0" or "-") ? t : null;

    private static bool Sim(string? v) => v is not null && v.StartsWith("Sim", StringComparison.OrdinalIgnoreCase);

    private static int? Inteiro(string? v) => v is not null && NumeroInteiro().Match(v) is { Success: true } m && int.TryParse(m.Value, out var n) ? n : null;

    /// <summary>"1.234,56" → 1234.56.</summary>
    private static decimal? NumeroBr(string? v)
    {
        if (v is null || NumeroDecimal().Match(v) is not { Success: true } m) return null;
        return decimal.TryParse(m.Value.Replace(".", "").Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static decimal? Dinheiro(string? v) => NumeroBr(v) is { } d and > 0 ? d : null;

    /// <summary>Área ou medida em m² (ou metros); hectares e alqueires são convertidos.</summary>
    public static decimal? Area(string? v)
    {
        if (NumeroBr(v) is not { } n || n <= 0) return null;
        var u = v!.ToLowerInvariant();
        var fator = u.Contains("hectare") || Regex.IsMatch(u, @"\bha\b") ? 10_000m
            : u.Contains("alqueire") ? (u.Contains("mineiro") || u.Contains("goiano") ? 48_400m : u.Contains("baiano") ? 96_800m : 24_200m)
            : 1m;
        return decimal.Round(n * fator, 2);
    }

    /// <summary>"Rua X, 580" → (Rua X, 580); "sn"/"0" viram "s/n"; partes extras vão para o complemento.</summary>
    public static (string? Logradouro, string? Numero, string? Extra) Endereco(string? texto)
    {
        if (Util(texto) is not { } t) return (null, null, null);
        var partes = t.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var rua = partes[0];
        string? numero = partes.Length > 1 ? partes[1] : null;
        if (numero is not null && Regex.IsMatch(numero, @"^(s\.?/?n\.?|sem n[uú]mero|0)$", RegexOptions.IgnoreCase)) numero = "s/n";
        if (numero is { Length: > 20 }) { rua = $"{rua}, {numero}"; numero = null; }
        var extra = partes.Length > 2 ? string.Join(", ", partes[2..]) : null;
        return (rua, numero, extra);
    }

    /// <summary>No Imobibrasil o bairro costuma vir com texto de anúncio ("Centro, diversas opções de lotes").</summary>
    public static string? BairroCurto(string? bairro)
    {
        if (Util(bairro) is not { } b) return null;
        var corte = b.IndexOf(", ", StringComparison.Ordinal);
        var traco = b.IndexOf(" - ", StringComparison.Ordinal);
        if (traco > 0 && (corte < 0 || traco < corte)) corte = traco;
        var r = (corte > 0 ? b[..corte] : b).Trim();
        return r.Length > 100 ? r[..100].Trim() : r;
    }

    private static string? FormatarCep(string? cep)
    {
        var d = new string([.. (cep ?? "").Where(char.IsAsciiDigit)]);
        return d.Length == 8 ? $"{d[..5]}-{d[5..]}" : null;
    }

    private static string TextoSimples(string html)
    {
        var s = EstiloOuScript().Replace(html, " ");
        s = WebUtility.HtmlDecode(Tag().Replace(s, " ")).Replace(' ', ' ');
        return Espacos().Replace(s, " ").Trim();
    }

    /// <summary>HTML da descrição → texto com parágrafos e itens "- ".</summary>
    public static string? DescricaoTexto(string html)
    {
        var h = EstiloOuScript().Replace(html, "");
        h = Regex.Replace(h, @"<li[^>]*>", "\n- ", RegexOptions.IgnoreCase);
        h = Regex.Replace(h, @"<br\s*/?>|</p>|</h\d>|</ul>|</li>|<p[^>]*>|<h\d[^>]*>|<ul[^>]*>|</div>", "\n", RegexOptions.IgnoreCase);
        h = WebUtility.HtmlDecode(Tag().Replace(h, "")).Replace(' ', ' ');
        var linhas = new List<string>();
        var vazio = false;
        foreach (var bruta in h.Split('\n'))
        {
            var l = Regex.Replace(bruta, @"[ \t]+", " ").Trim();
            if (l.Equals("www.inovabens.com.br", StringComparison.OrdinalIgnoreCase)) continue;
            if (l.Length == 0) { vazio = true; continue; }
            var anterior = linhas.Count > 0 ? linhas[^1] : null;
            // pedaços de uma mesma frase partidos por <strong>/<span> ("analisamos permutas e" + "aceitamos veículos" + ".")
            if (anterior is { Length: > 0 } && !l.StartsWith("- ", StringComparison.Ordinal)
                && (".,;:".Contains(l[0]) || (!".:!?".Contains(anterior[^1]) && !anterior.StartsWith("- ", StringComparison.Ordinal) && char.IsLower(l[0]))))
            {
                linhas[^1] = ".,;:".Contains(l[0]) ? anterior + l : $"{anterior} {l}";
                vazio = false;
                continue;
            }
            if (linhas.Count > 0 && vazio && !l.StartsWith("- ", StringComparison.Ordinal)) linhas.Add("");
            linhas.Add(l);
            vazio = false;
        }
        var texto = string.Join('\n', linhas).Trim();
        return texto.Length == 0 ? null : texto;
    }

    [GeneratedRegex(@"/imovel/(\d+)/[a-z0-9-]+")] private static partial Regex LinkFicha();
    [GeneratedRegex(@"<script type=""application/ld\+json"">(.*?)</script>", RegexOptions.Singleline)] private static partial Regex JsonLd();
    [GeneratedRegex(@"""name"": ""([^""]+)""\s*}\s*}")] private static partial Regex Trilha();
    [GeneratedRegex(@"^(.+?) para (Venda|Loca\S+)$", RegexOptions.IgnoreCase)] private static partial Regex ParaFinalidade();
    [GeneratedRegex(@"id=""desc_descricao"".*?<div class=""item-imovel"">(.*?)(?:<style>\s*\.imovel_cx_caracteristicas|<h3 class=""titulo-int"")", RegexOptions.Singleline)] private static partial Regex Descricao();
    [GeneratedRegex(@"<div class=""cxSpan"">(.*?)</div>(.*?)<div class=""clear"">", RegexOptions.Singleline)] private static partial Regex Grupo();
    [GeneratedRegex(@"<div class=""cxItem"">(.*?)</div>", RegexOptions.Singleline)] private static partial Regex Item();
    [GeneratedRegex(@"<a class=""rsImg""[^>]*data-rsbigimg=""([^""]+)""[^>]*>(.*?)<img", RegexOptions.Singleline)] private static partial Regex Foto();
    [GeneratedRegex(@"(?:youtube\.com/embed/|youtu\.be/|youtube\.com/watch\?v=)([\w-]{11})")] private static partial Regex Youtube();
    [GeneratedRegex(@"</h1>.*?(R\$ ?[\d.]+,\d{2})", RegexOptions.Singleline)] private static partial Regex PrecoTopo();
    [GeneratedRegex(@"<td>(.*?)</td>", RegexOptions.Singleline)] private static partial Regex Celula();
    [GeneratedRegex(@"<style.*?</style>|<script.*?</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex EstiloOuScript();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tag();
    [GeneratedRegex(@"\s+")] private static partial Regex Espacos();
    [GeneratedRegex(@"\d+")] private static partial Regex NumeroInteiro();
    [GeneratedRegex(@"\d{1,3}(?:\.\d{3})+(?:,\d+)?|\d+(?:,\d+)?")] private static partial Regex NumeroDecimal();
}
