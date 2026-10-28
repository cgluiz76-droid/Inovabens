using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Isopoh.Cryptography.Argon2;

namespace Inovabens.Core.Servicos;

/// <summary>
/// Hash de senha Argon2id com os MESMOS parâmetros padrão do @node-rs/argon2 (m=19456, t=2, p=1, 32 bytes),
/// no formato PHC — senhas criadas aqui entram na versão Node e vice-versa.
/// </summary>
public static class Senhas
{
    public static string Hash(string senha)
    {
        var sal = new byte[16];
        RandomNumberGenerator.Fill(sal);
        return Argon2.Hash(new Argon2Config
        {
            Type = Argon2Type.HybridAddressing,
            Version = Argon2Version.Nineteen,
            TimeCost = 2,
            MemoryCost = 19456,
            Lanes = 1,
            Threads = 1,
            HashLength = 32,
            Salt = sal,
            Password = Encoding.UTF8.GetBytes(senha),
        });
    }

    public static bool Confere(string hash, string senha)
    {
        try { return Argon2.Verify(hash, senha); }
        catch { return false; }
    }

    /// <summary>
    /// Selo da sessão: muda quando a senha muda. Vai no cookie e é comparado na revalidação periódica,
    /// derrubando as outras sessões após troca de senha (papel do revogarTodas() da versão Node).
    /// </summary>
    public static string Selo(string senhaHash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(senhaHash)))[..16];
}

/// <summary>Política de senha da versão Node: ≥ 10 caracteres, com letra e número.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SenhaForteAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext ctx)
    {
        if (value is not string s || s.Length == 0) return ValidationResult.Success; // obrigatoriedade fica com [Required]
        string? erro = s.Length < 10 ? "A senha precisa ter pelo menos 10 caracteres"
            : s.Length > 128 ? "A senha pode ter no máximo 128 caracteres"
            : !s.Any(char.IsAsciiLetter) ? "A senha precisa ter ao menos uma letra"
            : !s.Any(char.IsAsciiDigit) ? "A senha precisa ter ao menos um número"
            : null;
        return erro is null ? ValidationResult.Success : new ValidationResult(erro, ctx.MemberName is { } m ? [m] : null);
    }
}
