using System.Text.RegularExpressions;
using Ganss.Xss;

namespace Inovabens.Core.Imoveis;

/// <summary>
/// Limpeza do HTML/CSS colado na aba Página (gerado pela ferramenta GeradorPaginas) antes de ir para a página pública:
/// sem script, sem atributos de evento (onclick...), sem javascript:, sem iframe/form e sem @import. Mesmo colado por um
/// administrador, o código vai para uma página aberta a qualquer visitante, então nada executável passa.
/// </summary>
public static partial class HtmlPersonalizado
{
    /// <summary>Tamanho máximo de cada parte (HTML e CSS), em caracteres.</summary>
    public const int TamanhoMaximo = 300_000;

    private static readonly HtmlSanitizer Sanitizador = Criar();

    private static HtmlSanitizer Criar()
    {
        var s = new HtmlSanitizer();
        foreach (var a in new[] { "role", "aria-label", "aria-labelledby", "aria-describedby", "aria-hidden", "srcset", "sizes",
                     "loading", "decoding", "target", "rel", "id", "class" })
            s.AllowedAttributes.Add(a);
        s.AllowedSchemes.Add("mailto");
        s.AllowedSchemes.Add("tel");
        // ícones em SVG simples (sem <use>, que poderia buscar arquivo de fora)
        foreach (var t in new[] { "svg", "path", "circle", "rect", "g", "line", "polyline", "polygon", "ellipse", "title" })
            s.AllowedTags.Add(t);
        foreach (var a in new[] { "viewbox", "d", "fill", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin", "cx", "cy",
                     "r", "rx", "ry", "x", "y", "x1", "y1", "x2", "y2", "points", "xmlns", "fill-rule", "clip-rule", "transform" })
            s.AllowedAttributes.Add(a);
        return s;
    }

    /// <summary>HTML sem nada executável. Blocos &lt;style&gt; colados junto saem do HTML e vão para o CSS.</summary>
    public static (string Html, string Css) Limpar(string? html, string? css)
    {
        html ??= "";
        var estilos = new List<string>();
        html = Estilo().Replace(html, m => { estilos.Add(m.Groups[1].Value); return ""; });
        // só o conteúdo do <body>, se vier um documento inteiro (o cabeçalho e o rodapé do site continuam os do sistema)
        if (Corpo().Match(html) is { Success: true } b) html = b.Groups[1].Value;
        var cssTotal = string.Join("\n", estilos.Append(css ?? "").Where(x => !string.IsNullOrWhiteSpace(x)));
        return (Sanitizador.Sanitize(html).Trim(), LimparCss(cssTotal));
    }

    /// <summary>
    /// CSS sem @import, sem expression()/behavior/javascript: e só com url() de imagem embutida ou https. Os sinais
    /// &lt; e &gt; saem (impede fechar o &lt;style&gt; e abrir outra tag); seletores com "&gt;" viram descendentes.
    /// </summary>
    public static string LimparCss(string? css)
    {
        if (string.IsNullOrWhiteSpace(css)) return "";
        var c = css.Replace("<", " ").Replace(">", " ");
        c = Import().Replace(c, "");
        c = Perigoso().Replace(c, "/*removido*/");
        c = Url().Replace(c, m =>
        {
            var alvo = m.Groups[1].Value.Trim().Trim('"', '\'').Trim();
            return alvo.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || alvo.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && !alvo.Contains("svg", StringComparison.OrdinalIgnoreCase)
                ? m.Value : "none";
        });
        return c.Trim();
    }

    [GeneratedRegex(@"<style[^>]*>(.*?)</style\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Estilo();
    [GeneratedRegex(@"<body[^>]*>(.*)</body\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Corpo();
    [GeneratedRegex(@"@import[^;]*;?", RegexOptions.IgnoreCase)]
    private static partial Regex Import();
    [GeneratedRegex(@"expression\s*\(|behavior\s*:|-moz-binding|javascript\s*:|vbscript\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex Perigoso();
    [GeneratedRegex(@"url\s*\(([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex Url();
}
