using Microsoft.Playwright;
using OtpNet;
using static Microsoft.Playwright.Assertions;

namespace Inovabens.E2E;

/// <summary>Passos comuns, usando a tela como o usuário (campos achados pelo rótulo visível).</summary>
internal static class Navegacao
{
    public static string Sufixo() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Espera o circuito do Blazor assumir a página pré-renderizada; antes disso os cliques se perdem.</summary>
    public static Task Interativa(this IPage p) => Expect(p.Locator("[data-interativo=sim]")).ToBeVisibleAsync();

    public static async Task EntrarAsync(this IPage p, string email, string senha, string? segredoTotp = null)
    {
        await p.GotoAsync("/login");
        await p.GetByLabel("E-mail").FillAsync(email);
        await p.GetByLabel("Senha").FillAsync(senha);
        await p.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync();
        if (segredoTotp is not null)
        {
            await p.GetByLabel("Código 2FA").FillAsync(CodigoTotp(segredoTotp));
            await p.GetByLabel("Senha").FillAsync(senha);
            await p.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync();
        }
        await p.Interativa();
    }

    public static string CodigoTotp(string segredo) => new Totp(Base32Encoding.ToBytes(segredo)).ComputeTotp();

    public static ILocator Botao(this IPage p, string nome) => p.GetByRole(AriaRole.Button, new() { Name = nome, Exact = true });

    /// <summary>Administrador cadastra um usuário pela tela de Administração.</summary>
    public static async Task CriarUsuarioAsync(this IPage admin, string nome, string email, string senha, string perfil)
    {
        await admin.GotoAsync("/admin");
        await admin.Interativa();
        await admin.Botao("+ Novo usuário").ClickAsync();
        await admin.GetByLabel("Nome *").FillAsync(nome);
        await admin.GetByLabel("E-mail *").FillAsync(email);
        await admin.GetByLabel("Senha inicial *").FillAsync(senha);
        await admin.GetByLabel("Perfil *").SelectOptionAsync(new SelectOptionValue { Label = perfil });
        await admin.Botao("Salvar").ClickAsync();
        await Expect(admin.GetByRole(AriaRole.Cell, new() { Name = email })).ToBeVisibleAsync();
    }

    /// <summary>Cadastra um imóvel e devolve o id (da URL da ficha).</summary>
    public static async Task<Guid> CriarImovelAsync(this IPage p, string titulo)
    {
        await p.GotoAsync("/imoveis/novo");
        await p.Interativa();
        await p.GetByLabel("Título *").FillAsync(titulo);
        await p.Botao("Cadastrar imóvel").ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = titulo, Level = 1 })).ToBeVisibleAsync();
        return Guid.Parse(p.Url.TrimEnd('/').Split('/')[^1]);
    }

    /// <summary>Clica no link de download e devolve os primeiros bytes do arquivo baixado.</summary>
    public static async Task<(string Nome, byte[] Inicio)> BaixarAsync(this IPage p, ILocator link)
    {
        var download = await p.RunAndWaitForDownloadAsync(() => link.ClickAsync());
        await using var s = await download.CreateReadStreamAsync();
        var buffer = new byte[4];
        await s.ReadExactlyAsync(buffer);
        return (download.SuggestedFilename, buffer);
    }
}
