using Microsoft.AspNetCore.DataProtection;

namespace Inovabens.Web;

/// <summary>
/// Vale-renovação de sessão após a troca de senha: a tela de Conta (dentro do circuito do próprio usuário)
/// gera um token de 1 minuto amarrado ao usuário e ao novo selo; só quem o tem consegue reemitir o cookie.
/// Um cookie roubado não serve para "atualizar" a sessão, porque o atacante não tem o token.
/// </summary>
public sealed class RenovacaoSessao(IDataProtectionProvider dp)
{
    private readonly ITimeLimitedDataProtector _protetor = dp.CreateProtector("Inovabens.RenovacaoSessao").ToTimeLimitedDataProtector();

    public string Gerar(Guid usuarioId, string selo) => _protetor.Protect($"{usuarioId:N}.{selo}", TimeSpan.FromMinutes(1));

    public bool Valido(string token, Guid usuarioId, string selo)
    {
        try { return _protetor.Unprotect(token) == $"{usuarioId:N}.{selo}"; }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }
}
