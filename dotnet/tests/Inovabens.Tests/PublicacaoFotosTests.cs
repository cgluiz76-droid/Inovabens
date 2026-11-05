using System.Collections.Concurrent;
using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Inovabens.Tests;

/// <summary>Cloudflare R2 de mentira, em memória.</summary>
public sealed class ArmazenamentoPublicoMemoria : IArmazenamentoPublico
{
    public ConcurrentDictionary<string, (byte[] Dados, string Tipo)> Arquivos { get; } = new();
    public bool Habilitado => true;

    public Task EnviarAsync(string chave, byte[] dados, string contentType, CancellationToken ct = default)
    {
        Arquivos[chave] = (dados, contentType);
        return Task.CompletedTask;
    }

    public Task RemoverAsync(string chave)
    {
        Arquivos.TryRemove(chave, out _);
        return Task.CompletedTask;
    }

    public string? Url(string? chave) => chave is null ? null : $"https://fotos.exemplo/{chave}";

    public IEnumerable<string> Com(string? prefixo) => prefixo is null ? [] : Arquivos.Keys.Where(k => k.StartsWith(prefixo + "-"));
}

public class PublicacaoFotosUnidadeTests
{
    [Fact]
    public void Versoes_web_em_webp_sem_ampliar_e_sem_aceitar_o_que_nao_e_imagem()
    {
        var versoes = ImagemFoto.VersoesWeb(Imagens.Gerar(3000, 2000), PublicacaoFotosService.Larguras)!;
        Assert.Equal([(400, 267), (1200, 800), (1920, 1280)], versoes.Select(Imagens.Tamanho));
        Assert.All(versoes, v => Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(v, 0, 4)));
        Assert.All(versoes, v => Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(v, 8, 4)));

        // foto pequena: nenhuma versão maior que a original
        var pequenas = ImagemFoto.VersoesWeb(Imagens.Gerar(800, 600, SKEncodedImageFormat.Png), PublicacaoFotosService.Larguras)!;
        Assert.Equal([(400, 300), (800, 600), (800, 600)], pequenas.Select(Imagens.Tamanho));

        Assert.Null(ImagemFoto.VersoesWeb("não é imagem"u8.ToArray(), PublicacaoFotosService.Larguras));
    }

    [Fact]
    public void Le_o_texto_copiado_do_painel_da_Cloudflare()
    {
        string[] linhas =
        [
            "Token value", "tok", "", "Access Key ID", "  chave-123  ", "Secret Access Key", "segredo-456", "",
            "Use jurisdiction-specific endpoints for S3 clients:", "Default", "S3 API endpoint", "https://conta.r2.cloudflarestorage.com",
        ];
        Assert.Equal(("https://conta.r2.cloudflarestorage.com", "chave-123", "segredo-456"), R2ArmazenamentoPublico.LerArquivoCloudflare(linhas));
        Assert.Equal((null, null, null), R2ArmazenamentoPublico.LerArquivoCloudflare(["Access Key ID"]));

        // formato dos tokens novos: o endpoint vem logo depois de "Default", sem "S3 API endpoint"
        string[] novo =
        [
            "Token value", "tok", "Use the following credentials for S3 clients:", "Access Key ID", "chave-789", "Secret Access Key", "segredo-000",
            "Use jurisdiction-specific endpoints for S3 clients:", "Default", "https://outra.r2.cloudflarestorage.com",
            "For security reasons, these token values will not be shown again.",
        ];
        Assert.Equal(("https://outra.r2.cloudflarestorage.com", "chave-789", "segredo-000"), R2ArmazenamentoPublico.LerArquivoCloudflare(novo));
    }

    [Fact]
    public void Situacao_da_foto_em_relacao_ao_site()
    {
        Assert.Equal(SituacaoPublicacao.Privada, PublicacaoFotosService.Situacao(new ImovelMidia { ArquivoChave = "a" }));
        Assert.Equal(SituacaoPublicacao.Privada, PublicacaoFotosService.Situacao(new ImovelMidia { Tipo = TipoMidia.DOCUMENTO, Publica = true }));
        Assert.Equal(SituacaoPublicacao.Pendente, PublicacaoFotosService.Situacao(new ImovelMidia { ArquivoChave = "a", Publica = true }));
        Assert.Equal(SituacaoPublicacao.Publicada, PublicacaoFotosService.Situacao(
            new ImovelMidia { ArquivoChave = "a", Publica = true, PublicaChave = "p", PublicadaArquivo = "a" }));
        // a foto mudou (ex.: melhorada): volta a ficar pendente até a cópia nova ser publicada
        Assert.Equal(SituacaoPublicacao.Pendente, PublicacaoFotosService.Situacao(
            new ImovelMidia { ArquivoChave = "b", Publica = true, PublicaChave = "p", PublicadaArquivo = "a" }));
        Assert.Equal(SituacaoPublicacao.Falha, PublicacaoFotosService.Situacao(
            new ImovelMidia { ArquivoChave = "a", Publica = true, PublicadaArquivo = "a", PublicacaoErro = "x" }));
    }
}

/// <summary>Publicação no R2 com banco real e Drive/R2 em memória: só fotos marcadas como públicas vão para o site.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class PublicacaoFotosIntegracaoTests : IAsyncLifetime
{
    private ServiceProvider _sp = default!;
    private IServiceScope _escopo = default!;
    private readonly ArmazenamentoMemoria _drive = new();
    private readonly ArmazenamentoPublicoMemoria _r2 = new();

    public Task InitializeAsync()
    {
        var op = InovabensOpcoes.De(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = BancoFixture.Url, // sem MAGNIFIC_API_KEY: a publicação não espera melhoria
        }).Build(), producao: false);
        var servicos = new ServiceCollection().AddLogging().AddInovabensCore(op);
        servicos.AddSingleton<IArmazenamento>(_drive);
        servicos.AddSingleton<IArmazenamentoPublico>(_r2);
        _sp = servicos.BuildServiceProvider();
        _escopo = _sp.CreateScope();
        _escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _escopo.Dispose();
        await _sp.DisposeAsync();
    }

    private T S<T>() where T : notnull => _escopo.ServiceProvider.GetRequiredService<T>();

    [Fact]
    public async Task So_fotos_publicas_vao_para_o_R2_e_saem_quando_deixam_de_ser()
    {
        var admin = (await S<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var imoveis = S<ImoveisService>();
        var publicacao = S<PublicacaoFotosService>();
        var id = await imoveis.CriarAsync(admin, new ImovelForm { NomeDrive = Guid.NewGuid().ToString(), Titulo = "Casa no site", Tipo = "casa" });

        async Task Enviar(string nome, byte[] dados, string tipo, int subpasta, bool publica)
        {
            using var s = new MemoryStream(dados);
            await imoveis.EnviarMidiaAsync(admin, id, s, nome, tipo, subpasta, publica);
        }
        async Task<MidiaDetalhe> M(string nome) => (await imoveis.MidiasAsync(admin, id)).Single(m => m.Midia.NomeOriginal == nome);

        await Enviar("sala.jpg", Imagens.Gerar(3000, 2000), "image/jpeg", SubpastasDrive.FotosVideos, publica: true);
        await Enviar("quarto.jpg", Imagens.Gerar(1000, 800), "image/jpeg", SubpastasDrive.FotosVideos, publica: false);
        await Enviar("falsa.jpg", "não é imagem"u8.ToArray(), "image/jpeg", SubpastasDrive.FotosVideos, publica: true);
        // "pública" não vale para documentos nem fora da pasta de fotos
        await Enviar("planta.pdf", "%PDF-1.4"u8.ToArray(), "application/pdf", SubpastasDrive.FotosVideos, publica: true);
        await Enviar("matricula.jpg", Imagens.Gerar(100, 100), "image/jpeg", SubpastasDrive.Matricula, publica: true);

        Assert.Equal(SituacaoPublicacao.Pendente, (await M("sala.jpg")).Publicacao);
        Assert.Equal(SituacaoPublicacao.Privada, (await M("quarto.jpg")).Publicacao);
        Assert.False((await M("planta.pdf")).Midia.Publica);
        Assert.False((await M("matricula.jpg")).Midia.Publica);

        // rodada: a sala vai em três tamanhos; a falsa fica como falha e não é tentada de novo
        Assert.Equal(1, await publicacao.ProcessarAsync());
        var sala = await M("sala.jpg");
        Assert.Equal(SituacaoPublicacao.Publicada, sala.Publicacao);
        Assert.StartsWith($"imoveis/{id:N}/{sala.Midia.Id:N}-", sala.Midia.PublicaChave);
        Assert.Equal(PublicacaoFotosService.Larguras.Select(l => $"{sala.Midia.PublicaChave}-{l}.webp").Order(), _r2.Com(sala.Midia.PublicaChave).Order());
        Assert.Equal((1920, 1280), Imagens.Tamanho(_r2.Arquivos[$"{sala.Midia.PublicaChave}-1920.webp"].Dados));
        Assert.Equal("image/webp", _r2.Arquivos[$"{sala.Midia.PublicaChave}-400.webp"].Tipo);
        Assert.Equal($"https://fotos.exemplo/{sala.Midia.PublicaChave}-1200.webp", sala.UrlPublica);
        Assert.Null((await M("quarto.jpg")).UrlPublica);
        var falsa = await M("falsa.jpg");
        Assert.Equal(SituacaoPublicacao.Falha, falsa.Publicacao);
        Assert.Contains("Formato de imagem não suportado", falsa.Midia.PublicacaoErro);
        Assert.Equal(3, _r2.Arquivos.Count);
        Assert.Equal(0, await publicacao.ProcessarAsync());

        // marcar o quarto como público publica; desmarcar a sala retira do R2 (continua no Drive)
        await imoveis.AtualizarMidiaAsync(admin, (await M("quarto.jpg")).Midia.Id, new MidiaAlteracao { Publica = true });
        await imoveis.AtualizarMidiaAsync(admin, sala.Midia.Id, new MidiaAlteracao { Publica = false });
        Assert.Equal(1, await publicacao.ProcessarAsync());
        var quarto = await M("quarto.jpg");
        Assert.Equal(SituacaoPublicacao.Publicada, quarto.Publicacao);
        Assert.Empty(_r2.Com(sala.Midia.PublicaChave));
        sala = await M("sala.jpg");
        Assert.Equal((SituacaoPublicacao.Privada, (string?)null), (sala.Publicacao, sala.Midia.PublicaChave));
        Assert.NotNull(await _drive.BaixarAsync(sala.Midia.ArquivoChave));

        // documento não pode virar público
        await Assert.ThrowsAsync<RegraNegocioException>(() =>
            M("planta.pdf").ContinueWith(t => imoveis.AtualizarMidiaAsync(admin, t.Result.Midia.Id, new MidiaAlteracao { Publica = true })).Unwrap());

        // foto trocada no Drive (ex.: melhorada): nova cópia com outro endereço, e a antiga sai
        var antiga = quarto.Midia.PublicaChave;
        var novaId = await _drive.EnviarAsync("x", "quarto (melhorada).jpg", new MemoryStream(Imagens.Gerar(1600, 1200)), "image/jpeg");
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
            await db.ImovelMidias.Where(m => m.Id == quarto.Midia.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.ArquivoChave, novaId));
        Assert.Equal(SituacaoPublicacao.Pendente, (await M("quarto.jpg")).Publicacao);
        Assert.Equal(1, await publicacao.ProcessarAsync());
        quarto = await M("quarto.jpg");
        Assert.NotEqual(antiga, quarto.Midia.PublicaChave);
        Assert.Empty(_r2.Com(antiga));
        Assert.Equal((1600, 1200), Imagens.Tamanho(_r2.Arquivos[$"{quarto.Midia.PublicaChave}-1920.webp"].Dados));

        // falha corrigida: marcar de novo tenta outra vez
        await imoveis.AtualizarMidiaAsync(admin, falsa.Midia.Id, new MidiaAlteracao { Publica = false });
        await imoveis.AtualizarMidiaAsync(admin, falsa.Midia.Id, new MidiaAlteracao { Publica = true });
        Assert.Equal(SituacaoPublicacao.Pendente, (await M("falsa.jpg")).Publicacao);
        await publicacao.ProcessarAsync();
        Assert.Equal(SituacaoPublicacao.Falha, (await M("falsa.jpg")).Publicacao);

        // excluir a mídia apaga a cópia pública
        await imoveis.AtualizarMidiaAsync(admin, sala.Midia.Id, new MidiaAlteracao { Publica = true });
        await publicacao.ProcessarAsync();
        sala = await M("sala.jpg");
        Assert.Equal(3, _r2.Com(sala.Midia.PublicaChave).Count());
        await imoveis.RemoverMidiaAsync(admin, quarto.Midia.Id);
        Assert.Empty(_r2.Com(quarto.Midia.PublicaChave));

        // imóvel excluído: as fotos saem do site
        await imoveis.ExcluirAsync(admin, id);
        await publicacao.ProcessarAsync();
        Assert.Empty(_r2.Com(sala.Midia.PublicaChave));
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
            Assert.Null((await db.ImovelMidias.SingleAsync(m => m.Id == sala.Midia.Id)).PublicaChave);
    }
}
