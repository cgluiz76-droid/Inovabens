using System.Security.Claims;

namespace Inovabens.Core.Comum;

/// <summary>Usuário autenticado (equivalente ao AuthUser da API Node). Vive nas claims do cookie.</summary>
public sealed record UsuarioAtual(
    Guid Id,
    string Nome,
    string Email,
    Guid PerfilId,
    string PerfilNome,
    bool EscopoProprio,
    IReadOnlySet<string> Permissoes,
    string Selo = "")
{
    public const string ClaimPerfilId = "perfil_id";
    public const string ClaimPerfilNome = "perfil_nome";
    public const string ClaimEscopoProprio = "escopo_proprio";
    public const string ClaimPermissao = "perm";
    public const string ClaimSelo = "selo";
    public const string Esquema = "Cookies";

    public bool Pode(string permissao) => Permissoes.Contains(permissao);

    /// <summary>Lança <see cref="AcessoNegadoException"/> se faltar a permissão (defesa em profundidade além da tela).</summary>
    public void Exigir(string permissao)
    {
        if (!Pode(permissao)) throw new AcessoNegadoException();
    }

    public ClaimsPrincipal ParaPrincipal()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Id.ToString()),
            new(ClaimTypes.Name, Nome),
            new(ClaimTypes.Email, Email),
            new(ClaimPerfilId, PerfilId.ToString()),
            new(ClaimPerfilNome, PerfilNome),
            new(ClaimEscopoProprio, EscopoProprio ? "true" : "false"),
            new(ClaimSelo, Selo),
        };
        claims.AddRange(Permissoes.Select(p => new Claim(ClaimPermissao, p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Esquema));
    }

    public static UsuarioAtual? De(ClaimsPrincipal? p)
    {
        if (p?.Identity?.IsAuthenticated != true) return null;
        if (!Guid.TryParse(p.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)) return null;
        Guid.TryParse(p.FindFirst(ClaimPerfilId)?.Value, out var perfilId);
        return new UsuarioAtual(
            id,
            p.FindFirst(ClaimTypes.Name)?.Value ?? "",
            p.FindFirst(ClaimTypes.Email)?.Value ?? "",
            perfilId,
            p.FindFirst(ClaimPerfilNome)?.Value ?? "",
            p.FindFirst(ClaimEscopoProprio)?.Value == "true",
            p.FindAll(ClaimPermissao).Select(c => c.Value).ToHashSet(),
            p.FindFirst(ClaimSelo)?.Value ?? "");
    }
}
