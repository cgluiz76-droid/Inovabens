using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Limpeza do HTML/CSS colado na aba Página (vindo da ferramenta GeradorPaginas).</summary>
public class HtmlPersonalizadoTests
{
    [Fact]
    public void Remove_script_eventos_e_javascript_e_mantem_classes()
    {
        var (html, _) = HtmlPersonalizado.Limpar(
            """<div class="inb-root"><script>alert(1)</script><img src="https://x.com/a.webp" alt="Sala" onerror="alert(2)">""" +
            """<a href="javascript:alert(3)">x</a><a href="https://wa.me/5517" class="inb-btn">Falar</a><iframe src="https://e.com"></iframe></div>""",
            null);
        Assert.DoesNotContain("script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", html);
        Assert.DoesNotContain("javascript", html);
        Assert.DoesNotContain("iframe", html);
        Assert.Contains("class=\"inb-root\"", html);
        Assert.Contains("href=\"https://wa.me/5517\"", html);
        Assert.Contains("alt=\"Sala\"", html);
    }

    [Fact]
    public void Style_do_html_vai_para_o_css_e_o_css_perde_import_e_fechamento_de_tag()
    {
        var (html, css) = HtmlPersonalizado.Limpar(
            "<html><head><style>.inb-root{color:#03075f}</style></head><body><p class=\"inb-p\">Oi</p></body></html>",
            "@import url(https://evil.com/x.css); .inb-a{background:url(javascript:alert(1))} .inb-b{background:url('https://cdn.x/f.webp')} </style><script>");
        Assert.Equal("<p class=\"inb-p\">Oi</p>", html);
        Assert.Contains(".inb-root{color:#03075f}", css);
        Assert.DoesNotContain("@import", css);
        Assert.DoesNotContain("javascript", css);
        Assert.DoesNotContain("<", css);
        Assert.Contains("url('https://cdn.x/f.webp')", css);
    }
}

/// <summary>Página personalizada gravada e dados públicos do imóvel para a ferramenta (busca pelo código).</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class PaginaPersonalizadaTests : IAsyncLifetime
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
    public async Task Html_colado_vira_a_pagina_publica_e_o_gerador_recebe_so_dados_publicos()
    {
        var admin = (await S<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var paginas = S<PaginasImovelService>();
        var marca = Guid.NewGuid().ToString("N")[..8];
        var id = await S<ImoveisService>().CriarAsync(admin, new ImovelForm
        {
            NomeDrive = "Casa " + marca, Titulo = "Casa " + marca, Tipo = "casa", Cidade = "Rio Preto", Uf = "SP",
            Logradouro = "Rua Secreta", Numero = "10", EnderecoPublico = false, PrecoVenda = 500_000, Quartos = 3,
        });

        // sem página gravada não há onde colar
        await Assert.ThrowsAsync<RegraNegocioException>(() => paginas.SalvarPersonalizadoAsync(admin, id, "<p>x</p>", null));
        var form = (await paginas.EdicaoAsync(admin, id)).Form;
        await paginas.SalvarAsync(admin, id, form);

        var (html, css) = await paginas.SalvarPersonalizadoAsync(admin, id,
            "<div class=\"inb-root\"><h1>Casa</h1><script>x()</script></div>", ".inb-root{color:red}");
        Assert.Equal(("<div class=\"inb-root\"><h1>Casa</h1></div>", ".inb-root{color:red}"), (html, css));
        var publica = (await paginas.PublicaAsync(form.Slug, admin))!;
        Assert.True(publica.Pagina.Personalizada);

        // só código não permitido: recusa em vez de apagar a página
        await Assert.ThrowsAsync<RegraNegocioException>(() => paginas.SalvarPersonalizadoAsync(admin, id, "<script>x()</script>", null));
        // HTML vazio volta aos blocos padrão
        await paginas.SalvarPersonalizadoAsync(admin, id, "", "");
        Assert.False((await paginas.PublicaAsync(form.Slug, admin))!.Pagina.Personalizada);

        var codigo = (await S<ImoveisService>().DetalheAsync(admin, id)).Dados.Codigo;
        var dados = (await paginas.DadosGeradorAsync(codigo.ToLowerInvariant()))!;
        Assert.Equal((codigo, 500_000m, 3), (dados.Codigo, dados.PrecoVenda!.Value, dados.Quartos!.Value));
        Assert.Null(dados.Endereco); // endereço não público não sai
        Assert.Null(await paginas.DadosGeradorAsync("ZZ9999"));
    }
}
