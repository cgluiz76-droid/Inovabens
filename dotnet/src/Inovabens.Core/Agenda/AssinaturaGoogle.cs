using System.Security.Cryptography;
using System.Text;

namespace Inovabens.Core.Agenda;

/// <summary>
/// Assinaturas HMAC da integração com o Google (mesmo formato da versão Node): o <c>state</c> do OAuth identifica
/// o usuário na volta do Google e o token do canal autentica as chamadas do webhook.
/// </summary>
internal static class AssinaturaGoogle
{
    public static readonly TimeSpan ValidadeEstado = TimeSpan.FromMinutes(10);

    public static string Hmac(string segredo, string valor) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(segredo), Encoding.UTF8.GetBytes(valor)));

    /// <summary>state = usuário + validade, assinado com HMAC.</summary>
    public static string AssinarEstado(string segredo, Guid usuarioId, DateTimeOffset agora)
    {
        var corpo = $"{usuarioId:N}.{agora.Add(ValidadeEstado).ToUnixTimeSeconds()}";
        return $"{corpo}.{Hmac(segredo, "google-oauth:" + corpo)}";
    }

    /// <summary>O usuário do state, ou null se estiver malformado, adulterado ou expirado.</summary>
    public static Guid? LerEstado(string segredo, string state, DateTimeOffset agora)
    {
        var p = state.Split('.');
        if (p.Length != 3 || !Guid.TryParseExact(p[0], "N", out var id) || !long.TryParse(p[1], out var exp)) return null;
        if (!IgualSeguro(Hmac(segredo, $"google-oauth:{p[0]}.{p[1]}"), p[2])) return null;
        return agora.ToUnixTimeSeconds() > exp ? null : id;
    }

    public static string TokenDoCanal(string segredo, string canalId) => Hmac(segredo, canalId);

    public static bool TokenDoCanalValido(string segredo, string? canalId, string? token) =>
        canalId is not null && token is not null && IgualSeguro(token, TokenDoCanal(segredo, canalId));

    private static bool IgualSeguro(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
