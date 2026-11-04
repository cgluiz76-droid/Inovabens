using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>
/// Contra o Google Drive de verdade: só roda com GOOGLE_DRIVE_ID e GOOGLE_DRIVE_CREDENCIAIS definidos no ambiente
/// (sem eles, passa sem fazer nada). Cria a pasta "_teste automatizado ..." no drive, envia, baixa, renomeia e,
/// no fim, manda a pasta para a lixeira.
/// </summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class GoogleDriveRealTests
{
    [Fact]
    public async Task Pastas_envio_download_e_renomear_no_drive_real()
    {
        var driveId = Environment.GetEnvironmentVariable("GOOGLE_DRIVE_ID");
        var credenciais = Environment.GetEnvironmentVariable("GOOGLE_DRIVE_CREDENCIAIS");
        if (string.IsNullOrEmpty(driveId) || string.IsNullOrEmpty(credenciais)) return;

        var op = InovabensOpcoes.De(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = BancoFixture.Url, ["GOOGLE_DRIVE_ID"] = driveId, ["GOOGLE_DRIVE_CREDENCIAIS"] = credenciais,
        }).Build(), producao: false);
        await using var sp = new ServiceCollection().AddLogging().AddInovabensCore(op).BuildServiceProvider();
        using var escopo = sp.CreateScope();
        T S<T>() where T : notnull => escopo.ServiceProvider.GetRequiredService<T>();
        S<ContextoConexao>().Ip = "127.0.0.1";
        var drive = S<IArmazenamento>();
        Assert.True(drive.Habilitado);

        var admin = (await S<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var imoveis = S<ImoveisService>();
        var nome = $"_teste automatizado {DateTime.Now:yyyyMMdd-HHmmss}";
        var id = await imoveis.CriarAsync(admin, new ImovelForm { NomeDrive = nome, Titulo = "Teste do Drive real", Tipo = "casa" });
        var raiz = (await imoveis.DetalheAsync(admin, id)).Dados.DrivePastaId;
        Assert.NotNull(raiz);
        try
        {
            foreach (var (_, sub) in SubpastasDrive.Todas)
                Assert.NotNull(await drive.BuscarPastaAsync(sub, raiz));

            var pdf = "%PDF-1.4\n%%EOF"u8.ToArray();
            using (var s = new MemoryStream(pdf))
                await imoveis.EnviarMidiaAsync(admin, id, s, "matrícula teste.pdf", "application/pdf", SubpastasDrive.Matricula);
            using (var s = new MemoryStream(Imagens.Gerar(200, 100)))
                await imoveis.EnviarMidiaAsync(admin, id, s, "fachada.jpg", "image/jpeg", SubpastasDrive.FotosVideos);

            var midias = await imoveis.MidiasAsync(admin, id);
            var doc = midias.Single(m => m.Midia.NomeOriginal == "matrícula teste.pdf");
            Assert.Equal(await drive.BuscarPastaAsync("01 - Matrícula", raiz), await drive.PastaDoArquivoAsync(doc.Midia.ArquivoChave));
            var baixado = await imoveis.ArquivoMidiaAsync(admin, doc.Midia.Id, doc.Midia.ArquivoChave);
            Assert.Equal(pdf, baixado.Dados);
            Assert.Equal(("application/pdf", false), (baixado.ContentType, baixado.Download));

            var form = ImovelForm.De((await imoveis.DetalheAsync(admin, id)).Dados);
            form.NomeDrive = nome + " (renomeado)";
            await imoveis.AtualizarAsync(admin, id, form);
            // a busca por nome do Drive demora alguns segundos para refletir a mudança
            string? renomeada = null;
            for (var i = 0; i < 15 && renomeada is null; i++)
            {
                renomeada = await drive.BuscarPastaAsync(nome + " (renomeado)", null);
                if (renomeada is null) await Task.Delay(2000);
            }
            Assert.Equal(raiz, renomeada);

            await imoveis.RemoverMidiaAsync(admin, doc.Midia.Id); // vai para a lixeira, sem erro
        }
        finally
        {
            await drive.RemoverAsync(raiz!);
            await imoveis.ExcluirAsync(admin, id);
        }
    }
}
