using Amazon.Runtime;
using Amazon.S3;
using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>
/// Contra o Google Drive e o Cloudflare R2 de verdade: só roda com GOOGLE_DRIVE_ID, GOOGLE_DRIVE_CREDENCIAIS e
/// R2_CREDENCIAIS (texto do painel da Cloudflare) no ambiente. Envia uma foto pública, publica, confere no bucket,
/// torna privada e confere que saiu. No fim, apaga tudo.
/// </summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class PublicacaoR2RealTests
{
    [Fact]
    public async Task Foto_publica_vai_para_o_R2_real_e_sai_quando_fica_privada()
    {
        var driveId = Environment.GetEnvironmentVariable("GOOGLE_DRIVE_ID");
        var credDrive = Environment.GetEnvironmentVariable("GOOGLE_DRIVE_CREDENCIAIS");
        var credR2 = Environment.GetEnvironmentVariable("R2_CREDENCIAIS");
        if (string.IsNullOrEmpty(driveId) || string.IsNullOrEmpty(credDrive) || string.IsNullOrEmpty(credR2)) return;

        var op = InovabensOpcoes.De(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = BancoFixture.Url, ["GOOGLE_DRIVE_ID"] = driveId, ["GOOGLE_DRIVE_CREDENCIAIS"] = credDrive,
            ["R2_CREDENCIAIS"] = credR2,
        }).Build(), producao: false);
        await using var sp = new ServiceCollection().AddLogging().AddInovabensCore(op).BuildServiceProvider();
        using var escopo = sp.CreateScope();
        T S<T>() where T : notnull => escopo.ServiceProvider.GetRequiredService<T>();
        S<ContextoConexao>().Ip = "127.0.0.1";
        var publicacao = S<PublicacaoFotosService>();
        Assert.True(publicacao.Habilitada);

        // cliente próprio para conferir o bucket por fora do sistema
        var (endpoint, chave, segredo) = R2ArmazenamentoPublico.LerArquivoCloudflare(await File.ReadAllLinesAsync(credR2));
        using var s3 = new AmazonS3Client(new BasicAWSCredentials(chave, segredo), new AmazonS3Config
        {
            ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = "auto",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });
        async Task<long?> Tamanho(string k)
        {
            try { return (await s3.GetObjectMetadataAsync(op.Integracoes.R2Bucket, k)).ContentLength; }
            catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
        }

        var admin = (await S<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var imoveis = S<ImoveisService>();
        var id = await imoveis.CriarAsync(admin, new ImovelForm
        {
            NomeDrive = $"_teste automatizado R2 {DateTime.Now:yyyyMMdd-HHmmss}", Titulo = "Teste do R2 real", Tipo = "casa",
        });
        var raiz = (await imoveis.DetalheAsync(admin, id)).Dados.DrivePastaId;
        string? prefixo = null;
        try
        {
            using (var s = new MemoryStream(Imagens.Gerar(2400, 1600)))
                await imoveis.EnviarMidiaAsync(admin, id, s, "fachada.jpg", "image/jpeg", SubpastasDrive.FotosVideos, publica: true);
            using (var s = new MemoryStream(Imagens.Gerar(800, 600)))
                await imoveis.EnviarMidiaAsync(admin, id, s, "privada.jpg", "image/jpeg", SubpastasDrive.FotosVideos, publica: false);

            await publicacao.ProcessarAsync();
            var midias = await imoveis.MidiasAsync(admin, id);
            var foto = midias.Single(m => m.Midia.NomeOriginal == "fachada.jpg");
            Assert.Equal(SituacaoPublicacao.Publicada, foto.Publicacao);
            Assert.Null(midias.Single(m => m.Midia.NomeOriginal == "privada.jpg").Midia.PublicaChave);
            prefixo = foto.Midia.PublicaChave!;
            foreach (var largura in PublicacaoFotosService.Larguras)
                Assert.True(await Tamanho(PublicacaoFotosService.ChaveVersao(prefixo, largura)) > 0);
            Assert.Equal(3, (await s3.ListObjectsV2Async(new() { BucketName = op.Integracoes.R2Bucket, Prefix = $"imoveis/{id:N}/" })).S3Objects.Count);
            // a medição do bucket (alerta do limite gratuito) enxerga os arquivos publicados
            var (bytes, arquivos) = await ((R2ArmazenamentoPublico)S<IArmazenamentoPublico>()).UsoAsync();
            Assert.True(arquivos >= 3 && bytes > 0);

            await imoveis.AtualizarMidiaAsync(admin, foto.Midia.Id, new MidiaAlteracao { Publica = false });
            await publicacao.ProcessarAsync();
            foreach (var largura in PublicacaoFotosService.Larguras)
                Assert.Null(await Tamanho(PublicacaoFotosService.ChaveVersao(prefixo, largura)));
        }
        finally
        {
            await publicacao.RemoverCopiaAsync(prefixo);
            if (raiz is not null) await S<IArmazenamento>().RemoverAsync(raiz);
            await imoveis.ExcluirAsync(admin, id);
        }
    }
}
