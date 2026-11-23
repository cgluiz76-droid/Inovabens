using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GeradorPaginas;

/// <summary>Pedido da tela de geração.</summary>
public sealed record PedidoGeracao(string? Descricao, string? Paleta, string? Prefixo, string? Formato, bool Responsivo = true);

/// <summary>Pedido de ajuste: o código atual e a instrução (ex.: "aumente o título").</summary>
public sealed record PedidoRefino(string? Html, string? Css, string? Instrucao, string? Paleta, string? Prefixo, string? Formato, bool Responsivo = true);

/// <summary>
/// Resposta para a tela. <see cref="Html"/> e <see cref="Css"/> sempre separados; <see cref="HtmlFinal"/> é o que se copia
/// como HTML (no formato "único", com o &lt;style&gt; embutido). <see cref="Bruto"/>: resposta que não veio no JSON esperado.
/// </summary>
public sealed record ResultadoGeracao(string Html, string Css, string HtmlFinal, string Formato, string Prefixo, string? Historico,
    string? Imovel, string? Aviso, string? Bruto = null);

public sealed partial class Gerador(ClaudeCli claude, IHttpClientFactory http, IConfiguration config, IWebHostEnvironment amb)
{
    public const string FormatoUnico = "unico", FormatoSeparado = "separado";

    /// <summary>Código de imóvel do Inovabens: 2 letras + 4 dígitos, lote (-01) ou remanescente (-R1).</summary>
    [GeneratedRegex(@"\b([A-Za-z]{2}\d{4}(?:-(?:\d{2,3}|[Rr]\d{1,2}))?)\b")]
    private static partial Regex CodigoImovel();

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,14}$")]
    private static partial Regex PrefixoValido();

    private static string Prefixo(string? p)
    {
        var x = (p ?? "").Trim().ToLowerInvariant();
        if (x.Length == 0) return "inb-";
        if (!PrefixoValido().IsMatch(x)) throw new ErroGerador("Prefixo inválido: use letras minúsculas, números e hífen, começando por letra (ex.: inb-).");
        return x.EndsWith('-') ? x : x + "-";
    }

    private static string Formato(string? f) => f == FormatoSeparado ? FormatoSeparado : FormatoUnico;

    /// <summary>Regras fixas enviadas em todo pedido.</summary>
    private static string Regras(string prefixo, string? paleta, bool responsivo) => $$"""
        Você gera trechos de HTML e CSS que serão colados na página de um imóvel do sistema da imobiliária Inova Bens
        (o site já tem cabeçalho, rodapé, botão de WhatsApp e formulário de contato com id="contato").

        REGRAS OBRIGATÓRIAS:
        1. Responda SOMENTE um objeto JSON válido, sem texto antes ou depois e sem cercas de código, no formato:
           {"html": "...", "css": "..."}
        2. HTML semântico e acessível: títulos em ordem (comece em h2; o h1 pode ser o título principal da página), alt
           descritivo em toda imagem, labels quando houver campos, contraste de cores adequado (WCAG AA).
        3. Sem JavaScript (nenhuma tag script, nenhum atributo on...), sem frameworks, sem formulários e sem iframes.
        4. Sem recursos externos: nada de CDN, fontes externas (@import, link) ou imagens de terceiros. Use fontes do sistema
           (system-ui, sans-serif). A ÚNICA exceção são as URLs de fotos fornecidas nos dados do imóvel, quando houver.
        5. Todas as classes começam com o prefixo "{{prefixo}}". O elemento raiz do HTML é <div class="{{prefixo}}root">.
           Todo seletor do CSS começa por ".{{prefixo}}root" (escopo), para não afetar o resto da página. Não estilize
           html, body nem seletores globais.
        6. Textos em português do Brasil. Não invente dados do imóvel (preço, metragem, endereço): use só o que foi informado;
           sem dados, escreva textos genéricos e neutros.
        7. Botões de contato: link para "#contato" (o formulário da página).
        8. {{(responsivo ? "Responsivo, mobile first: estilos base para celular e @media (min-width: ...) para telas maiores." : "Layout pensado para desktop.")}}
        9. Paleta / estilo visual: {{(string.IsNullOrWhiteSpace(paleta) ? "sóbrio e moderno, a seu critério" : paleta.Trim())}}.
        """;

    public async Task<ResultadoGeracao> GerarAsync(PedidoGeracao p, CancellationToken ct)
    {
        var descricao = (p.Descricao ?? "").Trim();
        if (descricao.Length == 0) throw new ErroGerador("Descreva a página ou informe o código do imóvel.");
        var prefixo = Prefixo(p.Prefixo);
        var formato = Formato(p.Formato);

        var sb = new StringBuilder(Regras(prefixo, p.Paleta, p.Responsivo)).AppendLine();
        string? imovel = null, aviso = null;
        if (CodigoImovel().Match(descricao) is { Success: true } m)
        {
            imovel = m.Groups[1].Value.ToUpperInvariant();
            var (dados, problema) = await DadosImovelAsync(imovel, ct);
            if (dados is null)
            {
                // só o código e sem dados: não há o que gerar sem inventar
                if (descricao.Equals(m.Value, StringComparison.OrdinalIgnoreCase)) throw new ErroGerador(problema!);
                aviso = problema + " A página foi gerada só com a descrição.";
                imovel = null;
            }
            else
            {
                sb.AppendLine().AppendLine($"DADOS DO IMÓVEL {imovel} (cadastro do sistema, só dados públicos; use-os e não invente outros):")
                    .AppendLine(dados);
                if (descricao.Equals(m.Value, StringComparison.OrdinalIgnoreCase))
                    descricao = "Crie uma página de apresentação (landing page) completa e atraente para este imóvel: abertura com a " +
                                "foto principal, destaques, descrição, características, galeria de fotos, valores e chamada para contato.";
            }
        }
        sb.AppendLine().AppendLine("PEDIDO:").AppendLine(descricao);

        var resposta = await claude.PerguntarAsync(sb.ToString(), ct);
        return Montar(resposta, formato, prefixo, $"{descricao}\n\nPaleta: {p.Paleta}\nPrefixo: {prefixo}\nFormato: {formato}\nResponsivo: {p.Responsivo}",
            imovel, aviso);
    }

    public async Task<ResultadoGeracao> RefinarAsync(PedidoRefino p, CancellationToken ct)
    {
        var instrucao = (p.Instrucao ?? "").Trim();
        if (instrucao.Length == 0) throw new ErroGerador("Escreva o ajuste que quer (ex.: \"aumente o título\").");
        if (string.IsNullOrWhiteSpace(p.Html)) throw new ErroGerador("Não há código para refinar: gere uma página antes.");
        var prefixo = Prefixo(p.Prefixo);
        var formato = Formato(p.Formato);
        var prompt = new StringBuilder(Regras(prefixo, p.Paleta, p.Responsivo)).AppendLine()
            .AppendLine("Ajuste o código abaixo conforme o pedido, mantendo todo o resto como está e seguindo as mesmas regras.")
            .AppendLine("Devolva o HTML e o CSS completos (não só o trecho alterado).")
            .AppendLine().AppendLine("HTML ATUAL:").AppendLine(p.Html)
            .AppendLine().AppendLine("CSS ATUAL:").AppendLine(p.Css ?? "")
            .AppendLine().AppendLine("AJUSTE PEDIDO:").AppendLine(instrucao).ToString();
        var resposta = await claude.PerguntarAsync(prompt, ct);
        return Montar(resposta, formato, prefixo, $"Refinar: {instrucao}\n\nPrefixo: {prefixo}\nFormato: {formato}", null, null);
    }

    /// <summary>Lê o JSON {html, css}; se vier com texto em volta, extrai o objeto; se falhar, devolve a resposta bruta.</summary>
    private ResultadoGeracao Montar(string resposta, string formato, string prefixo, string pedido, string? imovel, string? aviso)
    {
        if (Extrair(resposta) is not { } par)
            return new("", "", "", formato, prefixo, null, imovel,
                "A resposta não veio no formato esperado (JSON com html e css). Veja a resposta bruta abaixo.", resposta);
        var (html, css) = par;
        var final = formato == FormatoUnico && css.Length > 0 ? $"<style>\n{css}\n</style>\n{html}" : html;
        var pasta = Historico.Salvar(amb.ContentRootPath, pedido, final, css,
            new { html, css, formato, prefixo, imovel });
        return new(html, css, final, formato, prefixo, pasta, imovel, aviso);
    }

    internal static (string Html, string Css)? Extrair(string texto)
    {
        foreach (var candidato in Candidatos(texto))
        {
            try
            {
                if (JsonNode.Parse(candidato) is JsonObject o && o["html"]?.GetValue<string>() is { Length: > 0 } h)
                    return (h.Trim(), (o["css"]?.GetValue<string>() ?? "").Trim());
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }
        }
        return null;
    }

    private static IEnumerable<string> Candidatos(string t)
    {
        yield return t.Trim();
        // cerca de código ```json ... ```
        var cerca = Regex.Match(t, @"```(?:json)?\s*(\{.*\})\s*```", RegexOptions.Singleline);
        if (cerca.Success) yield return cerca.Groups[1].Value;
        int i = t.IndexOf('{'), f = t.LastIndexOf('}');
        if (i >= 0 && f > i) yield return t[i..(f + 1)];
    }

    /// <summary>Dados públicos do imóvel pelo endpoint do Inovabens (chave em appsettings.Local.json). Texto JSON ou o problema.</summary>
    private async Task<(string? Dados, string? Problema)> DadosImovelAsync(string codigo, CancellationToken ct)
    {
        var url = config["Inovabens:Url"]?.TrimEnd('/');
        var chave = config["Inovabens:Chave"];
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(chave))
            return (null, $"Para buscar o imóvel {codigo}, configure a chave do Inovabens em appsettings.Local.json (veja o README).");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{url}/api/v1/gerador/imovel/{Uri.EscapeDataString(codigo)}");
            req.Headers.Add("X-Chave-Gerador", chave);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var r = await http.CreateClient("inovabens").SendAsync(req, ct);
            if (!r.IsSuccessStatusCode)
                return (null, $"Imóvel {codigo} não encontrado no Inovabens (ou a chave não confere com Administração → Integrações → GeradorPaginas).");
            var json = await r.Content.ReadAsStringAsync(ct);
            // sem campos vazios: prompt menor
            var limpo = JsonNode.Parse(json) is JsonObject o ? SemVazios(o).ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : json;
            return (limpo, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, $"Não foi possível consultar o Inovabens ({e.Message}).");
        }
    }

    private static JsonObject SemVazios(JsonObject o)
    {
        foreach (var (k, v) in o.ToList())
        {
            if (v is null || v is JsonValue jv && jv.TryGetValue<string>(out var s) && s.Length == 0 || v is JsonArray { Count: 0 }) o.Remove(k);
            else if (v is JsonObject filho) SemVazios(filho);
            else if (v is JsonArray arr) foreach (var item in arr.OfType<JsonObject>()) SemVazios(item);
        }
        return o;
    }
}
