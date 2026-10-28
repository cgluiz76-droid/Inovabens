using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Microsoft.EntityFrameworkCore;
using OtpNet;

namespace Inovabens.Core.Servicos;

public enum SituacaoLogin { Sucesso, Falha, PedeTotp }

public sealed record ResultadoLogin(SituacaoLogin Situacao, UsuarioAtual? Usuario = null, string? Mensagem = null);

public sealed record ConfiguracaoTotp(string Segredo, string OtpauthUrl);

public sealed class AlterarSenhaForm
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Informe a senha atual.")]
    public string SenhaAtual { get; set; } = "";

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Informe a nova senha."), SenhaForte]
    public string NovaSenha { get; set; } = "";
}

/// <summary>
/// Login contra a tabela <c>usuarios</c> existente. As senhas foram gravadas pelo Node com Argon2id
/// no formato PHC ($argon2id$v=19$...), que é verificado aqui sem precisar recriar senhas.
/// </summary>
public sealed class AuthService(IDbContextFactory<InovabensDb> fabrica, AuditService audit, InovabensOpcoes op)
{
    private const int MaxFalhas = 5;
    // evita revelar (por tempo de resposta) se o e-mail existe
    private static readonly Lazy<string> HashFicticio = new(() => Senhas.Hash("dummy-password"));

    public async Task<ResultadoLogin> LoginAsync(string email, string senha, string? totp)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var e = email.Trim().ToLowerInvariant();
        var u = await db.Usuarios.Include(x => x.Perfil)
            .SingleOrDefaultAsync(x => x.Email.ToLower() == e && x.ExcluidoEm == null);

        if (u is null)
        {
            SenhaConfere(HashFicticio.Value, senha);
            return Falha("Credenciais inválidas.");
        }
        if (u.BloqueadoAte > DateTime.UtcNow)
            return Falha("Conta temporariamente bloqueada por excesso de tentativas. Tente novamente mais tarde.");

        if (!SenhaConfere(u.SenhaHash, senha) || !u.Ativo)
        {
            if (u.Ativo) await RegistrarFalha(db, u);
            return Falha("Credenciais inválidas.");
        }
        if (u.TotpAtivo)
        {
            if (string.IsNullOrWhiteSpace(totp)) return new ResultadoLogin(SituacaoLogin.PedeTotp, Mensagem: "Informe o código do aplicativo autenticador.");
            if (!await CodigoTotpAceito(db, u, totp))
            {
                await RegistrarFalha(db, u);
                return Falha("Código de verificação inválido.");
            }
        }

        u.TentativasFalhas = 0;
        u.BloqueadoAte = null;
        u.UltimoLoginEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "usuarios", u.Id, "LOGIN");
        await db.SaveChangesAsync();
        return new ResultadoLogin(SituacaoLogin.Sucesso, await Montar(db, u));
    }

    /// <summary>Relê usuário/perfil/permissões (usado para revalidar o cookie periodicamente). null = acesso revogado.</summary>
    public async Task<UsuarioAtual?> RecarregarAsync(Guid usuarioId)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var u = await db.Usuarios.AsNoTracking().Include(x => x.Perfil)
            .SingleOrDefaultAsync(x => x.Id == usuarioId && x.ExcluidoEm == null && x.Ativo);
        return u is null ? null : await Montar(db, u);
    }

    private static async Task<UsuarioAtual> Montar(InovabensDb db, Usuario u)
    {
        var perms = await db.PerfilPermissoes.Where(pp => pp.PerfilId == u.PerfilId)
            .Select(pp => pp.Permissao!.Codigo).ToListAsync();
        return new UsuarioAtual(u.Id, u.Nome, u.Email, u.PerfilId, u.Perfil!.Nome, u.Perfil.EscopoProprio, perms.ToHashSet(), Senhas.Selo(u.SenhaHash));
    }

    /// <summary>
    /// Incremento atômico no banco: tentativas simultâneas não se sobrescrevem (ler-e-gravar perderia falhas e
    /// enfraqueceria o bloqueio). O bloqueio só é gravado se ninguém contou outra falha nesse meio-tempo; quem
    /// contou depois grava o bloqueio maior.
    /// </summary>
    private static async Task RegistrarFalha(InovabensDb db, Usuario u)
    {
        var n = (await db.Database.SqlQuery<int>(
            $"update usuarios set tentativas_falhas = tentativas_falhas + 1 where id = {u.Id} returning tentativas_falhas as \"Value\"")
            .ToListAsync()).Single();
        var ate = DateTime.UtcNow + TempoDeBloqueio(n);
        await db.Usuarios.Where(x => x.Id == u.Id && x.TentativasFalhas == n)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.BloqueadoAte, ate));
    }

    /// <summary>Bloqueio progressivo: a partir da 5ª falha seguida, 5 min, 10, 20... (máx. 24 h); antes disso, nenhum.</summary>
    internal static TimeSpan? TempoDeBloqueio(int falhas) =>
        falhas < MaxFalhas ? null : TimeSpan.FromMinutes(Math.Min(5 * Math.Pow(2, falhas - MaxFalhas), 1440));

    private static bool SenhaConfere(string hash, string senha) => Senhas.Confere(hash, senha);

    /* --------------------------- Minha conta --------------------------- */

    /// <summary>Troca a senha e devolve o novo selo (as sessões com o selo antigo são encerradas na revalidação).</summary>
    public async Task<string> AlterarSenhaAsync(UsuarioAtual u, AlterarSenhaForm form)
    {
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var usr = await db.Usuarios.SingleAsync(x => x.Id == u.Id);
        if (!SenhaConfere(usr.SenhaHash, form.SenhaAtual))
            throw new RegraNegocioException(nameof(AlterarSenhaForm.SenhaAtual), "Senha atual incorreta");
        usr.SenhaHash = Senhas.Hash(form.NovaSenha);
        audit.Registrar(db, u.Id, "usuarios", u.Id, "SENHA");
        await db.SaveChangesAsync();
        return Senhas.Selo(usr.SenhaHash);
    }

    public async Task<bool> TotpAtivoAsync(Guid usuarioId)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        return await db.Usuarios.Where(x => x.Id == usuarioId).Select(x => x.TotpAtivo).SingleAsync();
    }

    /// <summary>Gera e guarda (cifrado) um novo segredo TOTP; só passa a valer depois de <see cref="TotpAtivarAsync"/>.</summary>
    public async Task<ConfiguracaoTotp> TotpIniciarAsync(UsuarioAtual u)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var usr = await db.Usuarios.SingleAsync(x => x.Id == u.Id);
        if (usr.TotpAtivo) throw new RegraNegocioException("_", "2FA já está ativo");
        var segredo = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20)).TrimEnd('=');
        usr.TotpSecret = Cripto.Cifrar(segredo, op.TokenEncKey);
        usr.TotpUltimoPasso = null;
        await db.SaveChangesAsync();
        var url = $"otpauth://totp/Inovabens:{Uri.EscapeDataString(usr.Email)}?secret={segredo}&issuer=Inovabens";
        return new ConfiguracaoTotp(segredo, url);
    }

    public async Task TotpAtivarAsync(UsuarioAtual u, string codigo)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var usr = await db.Usuarios.SingleAsync(x => x.Id == u.Id);
        if (!await CodigoTotpAceito(db, usr, codigo))
            throw new RegraNegocioException("codigo", "Código inválido");
        usr.TotpAtivo = true;
        audit.Registrar(db, u.Id, "usuarios", u.Id, "2FA_ATIVAR");
        await db.SaveChangesAsync();
    }

    public async Task TotpDesativarAsync(UsuarioAtual u, string senha, string? codigo)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var usr = await db.Usuarios.SingleAsync(x => x.Id == u.Id);
        if (!SenhaConfere(usr.SenhaHash, senha)) throw new RegraNegocioException("senha", "Senha incorreta");
        if (usr.TotpAtivo && !await CodigoTotpAceito(db, usr, codigo))
            throw new RegraNegocioException("codigo", "Código inválido");
        usr.TotpAtivo = false;
        usr.TotpSecret = null;
        usr.TotpUltimoPasso = null;
        audit.Registrar(db, u.Id, "usuarios", u.Id, "2FA_DESATIVAR");
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Confere o código (tolerância de ±1 intervalo de 30 s) e o "consome": um código só vale uma vez. A gravação do
    /// intervalo é condicional e atômica, então dois logins simultâneos com o mesmo código não passam os dois.
    /// </summary>
    private async Task<bool> CodigoTotpAceito(InovabensDb db, Usuario usr, string? codigo)
    {
        if (usr.TotpSecret is null || string.IsNullOrWhiteSpace(codigo)) return false;
        long passo;
        try
        {
            var segredo = Cripto.Decifrar(usr.TotpSecret, op.TokenEncKey);
            if (!new Totp(Base32Encoding.ToBytes(segredo)).VerifyTotp(codigo.Trim(), out passo, new VerificationWindow(previous: 1, future: 1)))
                return false;
        }
        catch { return false; }
        return await db.Usuarios
            .Where(x => x.Id == usr.Id && (x.TotpUltimoPasso == null || x.TotpUltimoPasso < passo))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TotpUltimoPasso, passo)) == 1;
    }

    private static ResultadoLogin Falha(string msg) => new(SituacaoLogin.Falha, Mensagem: msg);
}
