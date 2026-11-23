using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Página exclusiva do imóvel: sugestão do cadastro, endereço único, rascunho invisível ao público, só fotos públicas.</summary>
public class PaginasImovelUnidadeTests
{
    [Theory]
    [InlineData("Espaço Flamboyant - Aparecida do Taboado", "espaco-flamboyant-aparecida-do-taboado")]
    [InlineData("  Casa  Nº 12 / Centro!! ", "casa-n-12-centro")]
    [InlineData("###", "imovel")]
    public void Slug_sem_acento_e_com_hifen(string texto, string esperado) => Assert.Equal(esperado, PaginasImovelService.Slug(texto));

    [Fact]
    public void Slug_longo_corta_em_palavra()
    {
        var s = PaginasImovelService.Slug(string.Join(' ', Enumerable.Repeat("palavra", 20)));
        Assert.True(s.Length <= 80);
        Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", s);
    }

    [Fact]
    public void Sugestao_usa_so_o_cadastro()
    {
        var im = new Imovel
        {
            Titulo = "Chácara Recanto", Tipo = "chacara", Finalidade = FinalidadeImovel.VENDA, Cidade = "Aparecida do Taboado", Uf = "MS",
            AreaTerrenoM2 = 20_000, AreaConstruidaM2 = 180, Quartos = 3, Vagas = 1, AceitaPermuta = true, DescricaoPublica = "Linda vista.",
        };
        var c = new[]
        {
            new ImovelCaracteristica { Chave = "Piscina", Publico = true },
            new ImovelCaracteristica { Chave = "Cofre", Publico = false },
        };
        var f = PaginasImovelService.Sugestao(im, c);
        Assert.Equal("chacara-recanto-aparecida-do-taboado", f.Slug);
        Assert.Equal("Chácara Recanto", f.Titulo);
        Assert.Contains("à venda em Aparecida do Taboado - MS", f.Chamada);
        Assert.Equal(["2 ha", "180 m²", "3", "1"], f.Conteudo.Destaques.Select(d => d.Valor));
        Assert.Equal(["Piscina"], f.Conteudo.Diferenciais); // característica privada não vai para a página
        Assert.Equal("Linda vista.", Assert.Single(f.Conteudo.Secoes).Texto);
        Assert.Contains("permuta", f.Conteudo.Condicoes);
        Assert.True(f.TituloSeo!.Length <= 60);
        Assert.True(f.DescricaoSeo!.Length <= 155);
        Assert.Empty(f.Conteudo.Perguntas); // nada inventado
    }

    [Fact]
    public void Imagem_de_compartilhamento_1200x630_e_leve()
    {
        var jpeg = ImagemFoto.Compartilhamento(Imagens.Ruido(1600, 1600));
        Assert.NotNull(jpeg);
        Assert.Equal((1200, 630), Imagens.Tamanho(jpeg));
        Assert.True(jpeg.Length <= ImagemFoto.CompartilhamentoMaxBytes, $"{jpeg.Length} bytes");
        Assert.Null(ImagemFoto.Compartilhamento([1, 2, 3]));
    }
}

[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class PaginasImovelTests : IAsyncLifetime
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

    private async Task<UsuarioAtual> Entrar(string email) => (await S<AuthService>().LoginAsync(email, BancoFixture.Senha, null)).Usuario!;

    private Task<Guid> NovoImovel(UsuarioAtual u, string titulo) =>
        S<ImoveisService>().CriarAsync(u, new ImovelForm
        {
            NomeDrive = "Pagina " + Guid.NewGuid().ToString("N")[..8], Titulo = titulo, Tipo = "casa", PrecoVenda = 450_000, Cidade = "Jales",
        });

    private async Task<(Guid Publica, Guid Privada)> Fotos(Guid imovel)
    {
        await using var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync();
        var pub = new ImovelMidia { ImovelId = imovel, ArquivoChave = "pub-" + Guid.NewGuid(), Publica = true, Capa = true };
        var priv = new ImovelMidia { ImovelId = imovel, ArquivoChave = "priv-" + Guid.NewGuid(), Publica = false, Ordem = 1 };
        db.ImovelMidias.AddRange(pub, priv);
        await db.SaveChangesAsync();
        return (pub.Id, priv.Id);
    }

    [Fact]
    public async Task Rascunho_so_para_o_sistema_e_publicada_para_todos()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var carla = await Entrar("carla@inovabens.com.br"); // corretora com escopo próprio, não é responsável
        var svc = S<PaginasImovelService>();
        var titulo = "Casa Landing " + Guid.NewGuid().ToString("N")[..6];
        var id = await NovoImovel(admin, titulo);
        var (pub, priv) = await Fotos(id);

        var ed = await svc.EdicaoAsync(admin, id);
        Assert.Null(ed.Pagina); // ainda não gravada: só a sugestão
        Assert.Equal(PaginasImovelService.Slug(titulo + " Jales"), ed.Form.Slug);
        Assert.Equal([pub], ed.Fotos.Select(f => f.Id)); // foto privada não é opção

        // foto privada não pode abrir a página
        ed.Form.FotoCapaId = priv;
        await Assert.ThrowsAsync<RegraNegocioException>(() => svc.SalvarAsync(admin, id, ed.Form));
        ed.Form.FotoCapaId = pub;
        ed.Form.Conteudo.Diferenciais = ["  Piscina ", ""];
        await svc.SalvarAsync(admin, id, ed.Form);
        var slug = ed.Form.Slug;

        Assert.Null(await svc.PublicaAsync(slug, null)); // rascunho: 404 para visitante
        Assert.Null(await svc.PublicaAsync(slug, carla)); // e para quem não tem acesso ao imóvel
        var previa = await svc.PublicaAsync(slug, admin);
        Assert.NotNull(previa);
        Assert.True(previa.Previa);
        Assert.Equal(["Piscina"], previa.Conteudo.Diferenciais);
        Assert.Equal([pub], previa.Fotos.Select(f => f.Id));
        Assert.StartsWith($"imovel/{slug}/foto/{pub}/", previa.Fotos[0].Src);

        await svc.PublicarAsync(admin, id, true);
        var aberta = await svc.PublicaAsync(slug, null);
        Assert.NotNull(aberta);
        Assert.False(aberta.Previa);

        // só foto pública e com o arquivo certo sai pela rota sem login
        Assert.Null(await svc.FotoAsync(slug, priv, "qualquer", null));
        Assert.Null(await svc.FotoAsync(slug, pub, "arquivo-errado", null));

        // página no ar não troca de endereço (links já enviados)
        var form = (await svc.EdicaoAsync(admin, id)).Form;
        form.Slug = slug + "-novo";
        var erro = await Assert.ThrowsAsync<RegraNegocioException>(() => svc.SalvarAsync(admin, id, form));
        Assert.Contains("tire a página do ar", erro.Message);

        // outro imóvel com o mesmo título recebe endereço livre; usar o mesmo endereço é recusado
        var id2 = await NovoImovel(admin, titulo);
        var ed2 = await svc.EdicaoAsync(admin, id2);
        Assert.Equal(slug + "-2", ed2.Form.Slug);
        ed2.Form.Slug = slug;
        erro = await Assert.ThrowsAsync<RegraNegocioException>(() => svc.SalvarAsync(admin, id2, ed2.Form));
        Assert.Contains("outro imóvel", erro.Message);

        // tirar do ar volta a esconder do público; imóvel excluído some de vez
        await svc.PublicarAsync(admin, id, false);
        Assert.Null(await svc.PublicaAsync(slug, null));
        await svc.PublicarAsync(admin, id, true);
        await S<ImoveisService>().ExcluirAsync(admin, id, BancoFixture.Senha);
        Assert.Null(await svc.PublicaAsync(slug, null));
        Assert.Null(await svc.PublicaAsync(slug, admin));
    }

    [Fact]
    public async Task Corretora_sem_acesso_nao_edita()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var carla = await Entrar("carla@inovabens.com.br");
        var id = await NovoImovel(admin, "Casa sem acesso " + Guid.NewGuid().ToString("N")[..6]);
        await Assert.ThrowsAsync<AcessoNegadoException>(() => S<PaginasImovelService>().EdicaoAsync(carla, id));
    }
}
