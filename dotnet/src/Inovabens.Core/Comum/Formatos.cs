using System.Text.RegularExpressions;
using Inovabens.Core.Dominio;

namespace Inovabens.Core.Comum;

/// <summary>Formatos brasileiros: CPF/CNPJ, telefone com DDD, e-mail e UF. As máscaras da tela (mascaras.js) seguem as mesmas regras.</summary>
public static partial class Formatos
{
    /// <summary>
    /// E-mail: parte local com letras, dígitos e . _ % + -; domínio com rótulos de até 63 caracteres (sem hífen nas pontas)
    /// e extensão de 2+ letras. Mais rígido que [EmailAddress], que só exige um "@".
    /// </summary>
    public const string PadraoEmail =
        @"^[A-Za-z0-9._%+-]+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\.[A-Za-z]{2,}$";

    [GeneratedRegex(PadraoEmail)]
    public static partial Regex Email();

    public static readonly IReadOnlyDictionary<Uf, string> NomesUf = new Dictionary<Uf, string>
    {
        [Uf.AC] = "Acre", [Uf.AL] = "Alagoas", [Uf.AP] = "Amapá", [Uf.AM] = "Amazonas", [Uf.BA] = "Bahia", [Uf.CE] = "Ceará",
        [Uf.DF] = "Distrito Federal", [Uf.ES] = "Espírito Santo", [Uf.GO] = "Goiás", [Uf.MA] = "Maranhão", [Uf.MT] = "Mato Grosso",
        [Uf.MS] = "Mato Grosso do Sul", [Uf.MG] = "Minas Gerais", [Uf.PA] = "Pará", [Uf.PB] = "Paraíba", [Uf.PR] = "Paraná",
        [Uf.PE] = "Pernambuco", [Uf.PI] = "Piauí", [Uf.RJ] = "Rio de Janeiro", [Uf.RN] = "Rio Grande do Norte",
        [Uf.RS] = "Rio Grande do Sul", [Uf.RO] = "Rondônia", [Uf.RR] = "Roraima", [Uf.SC] = "Santa Catarina", [Uf.SP] = "São Paulo",
        [Uf.SE] = "Sergipe", [Uf.TO] = "Tocantins",
    };

    /// <summary>Opções para seleção: sigla e nome, na ordem do enum.</summary>
    public static readonly IReadOnlyList<(string Sigla, string Nome)> Ufs = [.. Enum.GetValues<Uf>().Select(u => (u.ToString(), NomesUf[u]))];

    // só 2 letras: Enum.TryParse também aceitaria "5" (o valor numérico)
    public static bool UfValida(string? uf) =>
        uf?.Trim() is { Length: 2 } s && s.All(char.IsAsciiLetter) && Enum.TryParse<Uf>(s, ignoreCase: true, out _);

    /// <summary>
    /// CPF/CNPJ sem máscara, em maiúsculas: CPF só dígitos; CNPJ aceita letras nas 12 primeiras posições
    /// (CNPJ alfanumérico da Receita Federal, emitido desde julho de 2026). Os 2 dígitos verificadores são sempre numéricos.
    /// </summary>
    public static string? DocumentoSemMascara(string? s) =>
        Texto.Limpo(s) is { } t ? new string([.. t.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit)]) : null;

    [GeneratedRegex(@"^\d{11}$")]
    private static partial Regex Cpf();

    [GeneratedRegex(@"^[A-Z0-9]{12}\d{2}$")]
    private static partial Regex Cnpj();

    public static bool CpfValido(string? semMascara) => semMascara is not null && Cpf().IsMatch(semMascara);

    public static bool CnpjValido(string? semMascara) => semMascara is not null && Cnpj().IsMatch(semMascara);

    /// <summary>000.000.000-00 (11) ou AA.AAA.AAA/AAAA-00 (14); outro tamanho volta como está, "—" se vazio.</summary>
    public static string Documento(string? d) => d switch
    {
        { Length: 11 } => $"{d[..3]}.{d[3..6]}.{d[6..9]}-{d[9..]}",
        { Length: 14 } => $"{d[..2]}.{d[2..5]}.{d[5..8]}/{d[8..12]}-{d[12..]}",
        null or "" => "—",
        _ => d,
    };

    /// <summary>
    /// Dígitos do telefone com DDD (10 fixo, 11 celular). Aceita o código do país (+55) na frente e descarta.
    /// Null se vazio.
    /// </summary>
    public static string? TelefoneDigitos(string? s)
    {
        if (Texto.Limpo(s) is not { } t) return null;
        var d = new string([.. t.Where(char.IsAsciiDigit)]);
        return d.Length is 12 or 13 && d.StartsWith("55") ? d[2..] : d;
    }

    // DDD de 11 a 99 (sem zero); fixo começa com 2-5; celular tem 9 dígitos começando com 9
    [GeneratedRegex(@"^[1-9][1-9](?:[2-5]\d{7}|9\d{8})$")]
    private static partial Regex Telefone();

    public static bool TelefoneValido(string? digitos) => digitos is not null && Telefone().IsMatch(digitos);

    /// <summary>(11) 3456-7890 ou (11) 98765-4321, com ou sem máscara na entrada; outro tamanho volta como está.</summary>
    public static string? TelefoneFormatado(string? s) => TelefoneDigitos(s) switch
    {
        { Length: 10 } d => $"({d[..2]}) {d[2..6]}-{d[6..]}",
        { Length: 11 } d => $"({d[..2]}) {d[2..7]}-{d[7..]}",
        _ => Texto.Limpo(s),
    };
}
