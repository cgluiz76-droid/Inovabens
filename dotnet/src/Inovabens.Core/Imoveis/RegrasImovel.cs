using Inovabens.Core.Comum;
using Inovabens.Core.Dominio;

namespace Inovabens.Core.Imoveis;

/// <summary>Arquivo pronto para o armazenamento: extensão limpa, tipo permitido e se deve ser baixado em vez de aberto.</summary>
public sealed record ArquivoArmazenado(string Extensao, string ContentType, bool Download, TipoMidia Tipo);

/// <summary>Regras de negócio puras do módulo (sem banco), fáceis de testar.</summary>
public static class RegrasImovel
{
    private static readonly HashSet<string> Imagens = ["image/jpeg", "image/png", "image/webp", "image/gif", "image/avif", "image/heic", "image/heif"];
    private static readonly HashSet<string> Videos = ["video/mp4", "video/webm", "video/quicktime"];

    /// <summary>
    /// Como um arquivo enviado é guardado e servido. Só imagens, vídeos e PDF comuns abrem no navegador; o resto
    /// (inclusive HTML e SVG, que podem conter script) vira download: application/octet-stream + attachment.
    /// O Content-Type vem do navegador de quem envia, então nunca é gravado sem passar por esta lista.
    /// </summary>
    public static ArquivoArmazenado ArquivoSeguro(string nomeOriginal, string? contentType)
    {
        var ct = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        var ext = new string([.. Path.GetExtension(nomeOriginal).ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).Take(8)]);
        ext = ext.Length > 0 ? "." + ext : "";
        if (Imagens.Contains(ct)) return new(ext, ct, Download: false, TipoMidia.FOTO);
        if (Videos.Contains(ct)) return new(ext, ct, Download: false, TipoMidia.VIDEO);
        if (ct == "application/pdf") return new(ext, ct, Download: false, TipoMidia.DOCUMENTO);
        return new(ext, "application/octet-stream", Download: true, TipoMidia.DOCUMENTO);
    }

    /// <summary>Caracteres que o Windows não aceita em nome de pasta (o Drive para computador sincroniza as pastas).</summary>
    public static readonly char[] CaracteresProibidosPasta = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>Nome da pasta no Drive: sem espaços nas pontas nem repetidos; vazio vira null.</summary>
    public static string? NomePasta(string? nome) =>
        Texto.Limpo(nome) is { } n ? string.Join(' ', n.Split(' ', StringSplitOptions.RemoveEmptyEntries)) : null;

    /// <summary>Content-Type informado pelo navegador, só o tipo (sem parâmetros); vazio vira application/octet-stream.</summary>
    public static string TipoInformado(string? contentType) =>
        (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant() is { Length: > 0 } ct && ct.Contains('/') ? ct : "application/octet-stream";

    /// <summary>Próximo código sequencial no formato IMV-000123.</summary>
    public static string FormatarCodigo(int numero) => $"IMV-{numero:D6}";

    /// <summary>Limite de fotos por imóvel (vídeos, plantas e documentos não contam). Anúncios do Imobibrasil chegam a 56.</summary>
    public const int MaximoFotos = 60;

    /// <summary>Grupos de características, os mesmos do Imobibrasil.</summary>
    public static readonly IReadOnlyList<string> GruposCaracteristicas = ["Características", "Cômodos", "Proximidades"];

    /// <summary>Id do vídeo (11 caracteres) num link do YouTube (watch, youtu.be, embed, shorts); null se não for do YouTube.</summary>
    public static string? IdYoutube(string? link)
    {
        if (Texto.Limpo(link) is not { } l) return null;
        var m = System.Text.RegularExpressions.Regex.Match(l,
            @"^(?:https?://)?(?:www\.|m\.)?(?:youtube\.com/(?:watch\?(?:.*&)?v=|embed/|shorts/|live/)|youtu\.be/)([\w-]{11})(?:[?&#/].*)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static IReadOnlyList<(string Sigla, string Nome)> Ufs => Formatos.Ufs;

    public static bool UfValida(string? uf) => Formatos.UfValida(uf);

    /// <summary>
    /// Lê coordenadas coladas do Google Maps ("-23.5505, -46.6333") ou digitadas com vírgula decimal
    /// separadas por ponto e vírgula ("-23,5505; -46,6333"). Devolve null se o texto não for um par válido.
    /// </summary>
    public static (double Latitude, double Longitude)? LerCoordenadas(string? texto)
    {
        var t = texto?.Trim().Trim('(', ')');
        if (string.IsNullOrEmpty(t)) return null;
        string[] partes = t.Contains(';') ? [.. t.Split(';').Select(p => p.Replace(',', '.'))] : t.Split(',');
        if (partes.Length != 2) return null;
        const System.Globalization.NumberStyles estilo = System.Globalization.NumberStyles.Float;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!double.TryParse(partes[0].Trim(), estilo, inv, out var lat) || !double.TryParse(partes[1].Trim(), estilo, inv, out var lng)) return null;
        if (lat is < -90 or > 90 || lng is < -180 or > 180) return null;
        return (Math.Round(lat, 7), Math.Round(lng, 7));
    }

    public static string FormatarCoordenadas(double? lat, double? lng) =>
        lat is { } a && lng is { } b
            ? $"{a.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {b.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : "";

    /// <summary>Endereço em uma linha para busca no mapa: "Rua X, 10, Bairro, Cidade - UF, CEP, Brasil".</summary>
    public static string? EnderecoParaMapa(string? logradouro, string? numero, string? bairro, string? cidade, string? uf, string? cep)
    {
        var rua = Texto.Limpo(logradouro) is { } l ? (Texto.Limpo(numero) is { } n ? $"{l}, {n}" : l) : null;
        var local = Texto.Limpo(cidade) is { } c ? (Texto.Limpo(uf) is { } u ? $"{c} - {u}" : c) : Texto.Limpo(uf);
        string?[] partes = [rua, Texto.Limpo(bairro), local, Texto.Limpo(cep)];
        var preenchidas = partes.Where(p => p is not null).ToList();
        return preenchidas.Count == 0 ? null : string.Join(", ", preenchidas) + ", Brasil";
    }

    /// <summary>Link do Google Maps: pelas coordenadas, se houver; senão pelo endereço.</summary>
    public static string? LinkGoogleMaps(double? lat, double? lng, string? endereco)
    {
        var consulta = lat is not null && lng is not null ? FormatarCoordenadas(lat, lng).Replace(" ", "") : Texto.Limpo(endereco);
        return consulta is null ? null : "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(consulta);
    }

    /// <summary>
    /// Regra: a soma dos percentuais VIGENTES (na data de hoje) dos proprietários deve ser 100%.
    /// Lista vazia é permitida (imóvel ainda sem proprietários).
    /// </summary>
    public static void ValidarProprietarios(IReadOnlyCollection<ProprietarioEntrada> itens, DateOnly hoje)
    {
        var erros = new List<string>();
        foreach (var (it, i) in itens.Select((x, i) => (x, i + 1)))
        {
            if (it.PessoaId is null || it.PessoaId == Guid.Empty) erros.Add($"Linha {i}: selecione a pessoa.");
            if (it.Percentual is not { } p || p <= 0 || p > 100) erros.Add($"Linha {i}: o percentual deve ser maior que 0 e no máximo 100.");
            else if (decimal.Round(p, 2) != p) erros.Add($"Linha {i}: use no máximo 2 casas decimais no percentual.");
            if (it.InicioEm is { } ini && it.FimEm is { } fim && fim < ini) erros.Add($"Linha {i}: o fim não pode ser antes do início.");
        }
        if (erros.Count > 0) throw new RegraNegocioException(new Dictionary<string, string[]> { ["proprietarios"] = [.. erros] });

        if (itens.Count == 0) return;
        var soma = itens
            .Where(i => (i.InicioEm is null || i.InicioEm <= hoje) && (i.FimEm is null || i.FimEm >= hoje))
            .Sum(i => i.Percentual!.Value);
        if (soma != 100m)
            throw new RegraNegocioException("proprietarios",
                $"A soma dos percentuais vigentes deve ser 100% (atual: {soma.ToString("N2", Texto.PtBr)}%).");
    }
}
