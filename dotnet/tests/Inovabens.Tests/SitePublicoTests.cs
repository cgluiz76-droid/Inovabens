using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Site aberto a visitantes (sem login): só imóveis divulgados e disponíveis, só fotos e características públicas.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class SitePublicoTests : IAsyncLifetime
{
    private IServiceScope _escopo = default!;
    private T S<T>() where T : notnull => _escopo.ServiceProvider.GetRequiredService<T>();

    public Task InitializeAsync()
    {
        _escopo = ExtratoComprovantesTests.Compartilhado.CreateScope();
        _escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _escopo.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Visitante_ve_so_imovel_divulgado_com_fotos_e_caracteristicas_publicas()
    {
        var admin = (await S<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var s = S<ImoveisService>();
        var marca = Guid.NewGuid().ToString("N")[..8];
        Task<Guid> Criar(string nome, bool divulgar, SituacaoImovel situacao) => s.CriarAsync(admin, new ImovelForm
        {
            NomeDrive = nome + " " + marca, Titulo = nome + " " + marca, Tipo = "casa", Cidade = "Inocência", Uf = "MS",
            Finalidade = FinalidadeImovel.VENDA, PrecoVenda = 300_000, DivulgarSite = divulgar, Situacao = situacao,
        });
        var noSite = await Criar("Casa no site", true, SituacaoImovel.DISPONIVEL);
        var naoDivulgado = await Criar("Casa interna", false, SituacaoImovel.DISPONIVEL);
        var vendido = await Criar("Casa vendida", true, SituacaoImovel.VENDIDO);

        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
        {
            db.ImovelMidias.Add(new ImovelMidia { ImovelId = noSite, Tipo = TipoMidia.FOTO, ArquivoChave = "pub-" + marca, Publica = true, Capa = true });
            db.ImovelMidias.Add(new ImovelMidia { ImovelId = noSite, Tipo = TipoMidia.FOTO, ArquivoChave = "priv-" + marca, Publica = false, Ordem = 1 });
            db.ImovelMidias.Add(new ImovelMidia { ImovelId = noSite, Tipo = TipoMidia.DOCUMENTO, ArquivoChave = "doc-" + marca, Publica = true, Ordem = 2 });
            db.ImovelMidias.Add(new ImovelMidia { ImovelId = naoDivulgado, Tipo = TipoMidia.FOTO, ArquivoChave = "int-" + marca, Publica = true });
            db.ImovelCaracteristicas.Add(new ImovelCaracteristica { ImovelId = noSite, Chave = "Piscina", Publico = true });
            db.ImovelCaracteristicas.Add(new ImovelCaracteristica { ImovelId = noSite, Chave = "Cofre", Publico = false });
            await db.SaveChangesAsync();
        }

        // vitrine: só o divulgado e disponível; a capa vem pela rota pública do site, não pela do sistema
        var vitrine = await s.VitrinePublicaAsync(new FiltroVitrine(FinalidadeImovel.VENDA, Maximo: 200));
        var cartao = Assert.Single(vitrine, v => v.Titulo.EndsWith(marca));
        Assert.Equal(noSite, cartao.Id);
        Assert.StartsWith("site/foto/", cartao.Capa);

        // ficha: nada interno
        var (d, _) = (await s.DetalhePublicoAsync(noSite))!.Value;
        Assert.Equal(["pub-" + marca], d.Midias.Select(m => m.Midia.ArquivoChave));
        Assert.All(d.Midias, m => Assert.StartsWith("site/foto/", m.Url));
        Assert.Equal(["Piscina"], d.Caracteristicas.Select(c => c.Chave));
        Assert.Empty(d.Proprietarios);
        Assert.Empty(d.Envolvidos);
        Assert.Null(d.LinkDrive);
        Assert.Null(await s.DetalhePublicoAsync(naoDivulgado));
        Assert.Null(await s.DetalhePublicoAsync(vendido));

        // foto: só a pública, atual, de imóvel que está no site
        var pub = d.Midias[0].Midia.Id;
        Assert.True(await s.FotoNoSiteAsync(pub, "pub-" + marca));
        Assert.False(await s.FotoNoSiteAsync(pub, "outro-arquivo"));
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
        {
            var priv = await db.ImovelMidias.Where(m => m.ArquivoChave == "priv-" + marca).Select(m => m.Id).SingleAsync();
            var doc = await db.ImovelMidias.Where(m => m.ArquivoChave == "doc-" + marca).Select(m => m.Id).SingleAsync();
            var interna = await db.ImovelMidias.Where(m => m.ArquivoChave == "int-" + marca).Select(m => m.Id).SingleAsync();
            Assert.False(await s.FotoNoSiteAsync(priv, "priv-" + marca));
            Assert.False(await s.FotoNoSiteAsync(doc, "doc-" + marca));
            Assert.False(await s.FotoNoSiteAsync(interna, "int-" + marca));
        }
    }
}
