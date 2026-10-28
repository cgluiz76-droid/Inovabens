using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;

namespace Inovabens.Core.Comum;

public sealed record Paginado<T>(IReadOnlyList<T> Itens, int Total, int Pagina, int TamanhoPagina)
{
    public int UltimaPagina => Math.Max(1, (int)Math.Ceiling(Total / (double)TamanhoPagina));
}

public sealed record Opcao(Guid Id, string Nome);

public static class Texto
{
    public static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private static readonly Dictionary<string, string> Acentos = new()
    {
        ["captacao"] = "captação", ["aquisicao"] = "aquisição", ["regularizacao"] = "regularização", ["locacao"] = "locação",
        ["negociacao"] = "negociação", ["documentacao"] = "documentação", ["retificacao"] = "retificação", ["area"] = "área",
        ["nao"] = "não", ["concluido"] = "concluído", ["concluida"] = "concluída", ["reuniao"] = "reunião", ["acao"] = "ação",
        ["estrategica"] = "estratégica", ["email"] = "e-mail",
    };

    /// <summary>EM_AQUISICAO -> "Em aquisição" (mesma regra do rotulo() da interface web).</summary>
    public static string Rotulo(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "—";
        var t = string.Join(' ', s.ToLowerInvariant().Split('_').Select(w => Acentos.GetValueOrDefault(w, w)));
        return char.ToUpper(t[0], PtBr) + t[1..];
    }

    public static string Rotulo<TEnum>(TEnum valor) where TEnum : struct, Enum => Rotulo(valor.ToString());

    /// <summary>Campo de formulário vazio vira null; o resto é aparado.</summary>
    public static string? Limpo(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Escapa curingas de LIKE para busca literal (escape = '\').</summary>
    public static string LikeLiteral(string s) => s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    public static string Moeda(decimal? v) => v is null ? "—" : v.Value.ToString("C", PtBr);

    public static string Data(DateOnly? d) => d is null ? "—" : d.Value.ToString("dd/MM/yyyy", PtBr);

    /// <summary>Fração para percentual: 0,153 → "15,3%".</summary>
    public static string Percentual(double? fracao) => fracao is null ? "—" : (fracao.Value * 100).ToString("0.##", PtBr) + "%";

    /// <summary>Instante UTC exibido no horário de Brasília: "28/10/2026 14:30".</summary>
    public static string DataHora(DateTime? utc) => utc is null ? "—" : DatasBR.DeUtc(utc.Value).ToString("dd/MM/yyyy HH:mm", PtBr);
}

public static class DatasBR
{
    private static readonly TimeZoneInfo Fuso = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    /// <summary>Data de hoje no fuso de Brasília — o negócio opera nesse fuso.</summary>
    public static DateOnly Hoje() => DateOnly.FromDateTime(Agora());

    /// <summary>Data e hora atuais no fuso de Brasília.</summary>
    public static DateTime Agora() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Fuso);

    /// <summary>Converte um instante UTC para o horário de Brasília.</summary>
    public static DateTime DeUtc(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Fuso);

    /// <summary>Converte um horário de Brasília para UTC.</summary>
    public static DateTime ParaUtc(DateTime horarioBR) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(horarioBR, DateTimeKind.Unspecified), Fuso);
}

public static class Validacao
{
    /// <summary>Valida DataAnnotations + IValidatableObject e lança <see cref="RegraNegocioException"/> com os erros por campo.</summary>
    public static void Validar(object modelo)
    {
        var resultados = new List<ValidationResult>();
        if (Validator.TryValidateObject(modelo, new ValidationContext(modelo), resultados, validateAllProperties: true)) return;
        var erros = resultados
            .SelectMany(r => (r.MemberNames.Any() ? r.MemberNames : ["_"]).Select(m => (m, msg: r.ErrorMessage ?? "Valor inválido")))
            .GroupBy(x => x.m)
            .ToDictionary(g => g.Key, g => g.Select(x => x.msg).ToArray());
        throw new RegraNegocioException(erros);
    }
}

public static class Cripto
{
    /// <summary>AES-256-GCM no MESMO formato da API Node: base64(iv[12] | tag[16] | cifrado). Usado no segredo TOTP.</summary>
    public static string Decifrar(string base64, string chaveHex)
    {
        var buf = Convert.FromBase64String(base64);
        var cifrado = buf.AsSpan(28);
        var claro = new byte[cifrado.Length];
        using var aes = new AesGcm(Convert.FromHexString(chaveHex), 16);
        aes.Decrypt(buf.AsSpan(0, 12), cifrado, buf.AsSpan(12, 16), claro);
        return System.Text.Encoding.UTF8.GetString(claro);
    }

    public static string Cifrar(string texto, string chaveHex)
    {
        var claro = System.Text.Encoding.UTF8.GetBytes(texto);
        var saida = new byte[28 + claro.Length];
        RandomNumberGenerator.Fill(saida.AsSpan(0, 12));
        using var aes = new AesGcm(Convert.FromHexString(chaveHex), 16);
        aes.Encrypt(saida.AsSpan(0, 12), claro, saida.AsSpan(28), saida.AsSpan(12, 16));
        return Convert.ToBase64String(saida);
    }
}
