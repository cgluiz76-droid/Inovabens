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

    /// <summary>Próximo código sequencial no formato IMV-000123.</summary>
    public static string FormatarCodigo(int numero) => $"IMV-{numero:D6}";

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
