using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Crm;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Redes;
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
    public void Endereco_sugerido_nao_repete_a_cidade()
    {
        var im = new Imovel { Titulo = "Sítio à venda em Cedral", Tipo = "sitio", Cidade = "Cedral" };
        Assert.Equal("sitio-a-venda-em-cedral", PaginasImovelService.Sugestao(im, []).Slug);
    }

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
    public void Proposta_da_ia_respeita_os_limites_da_tela()
    {
        var g = new PaginaGerada
        {
            Titulo = new string('a', 50) + " " + new string('b', 100), Chamada = "Chamada.", TituloSeo = "Título", DescricaoSeo = "Resumo",
            Destaques = [.. Enumerable.Range(1, 9).Select(i => new DestaqueGerado { Valor = $"{i}", Rotulo = "quartos" }), new() { Valor = "", Rotulo = "vazio" }],
            Secoes = [new() { Titulo = "Sobre", Texto = "Texto." }, new() { Titulo = "Vazia", Texto = " " }],
            Diferenciais = ["Piscina", "Piscina", " "],
            Perguntas = [new() { Pergunta = "Aceita permuta?", Resposta = "Sim." }, new() { Pergunta = "Sem resposta?", Resposta = "" }],
            Condicoes = "",
        };
        var f = PaginasImovelService.Converter(g);
        Assert.True(f.Titulo.Length <= 120);
        Assert.Equal(PaginaForm.MaxDestaques, f.Conteudo.Destaques.Count);
        Assert.Equal("Sobre", Assert.Single(f.Conteudo.Secoes).Titulo);
        Assert.Equal(["Piscina"], f.Conteudo.Diferenciais);
        Assert.Single(f.Conteudo.Perguntas);
        Assert.Null(f.Conteudo.Condicoes);
        f.Slug = "casa";
        var erros = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        Assert.True(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(f, new(f), erros, true), string.Join("; ", erros.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void Pedido_para_ia_leva_o_pedido_do_usuario_ou_deixa_a_ia_decidir()
    {
        var im = new Imovel { Titulo = "Casa Jardim", Tipo = "casa", Cidade = "Jales", Uf = "SP", PrecoVenda = 450_000, Quartos = 3 };
        var d = PaginasImovelService.Dados(im, [new ImovelCaracteristica { Chave = "Cofre", Publico = false }], []);
        var vazio = ClaudePagina.PedidoUsuario(d, null);
        Assert.Contains("Pedido do usuário: nenhum", vazio);
        Assert.Contains("R$", vazio);
        Assert.DoesNotContain("Cofre", vazio); // característica privada não vai para a IA
        Assert.Contains("Pedido do usuário: foco em investidores", ClaudePagina.PedidoUsuario(d, "foco em investidores"));
        // structured outputs: todo objeto fechado (additionalProperties=false) e com todos os campos obrigatórios
        var esquema = System.Text.Json.JsonSerializer.Serialize(ClaudePagina.Esquema());
        Assert.Contains("\"faltando\"", esquema);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(esquema, "\"additionalProperties\":false").Count);
    }

    [Theory]
    [InlineData("whatsapp", null, "whatsapp")]
    [InlineData("QRCODE", null, "qrcode")]
    [InlineData("tiktok", null, "outro")]
    [InlineData(null, "https://www.google.com.br/", "google")]
    [InlineData("", "https://exemplo.com/", "direto")]
    [InlineData(null, null, "direto")]
    public void Origem_da_visita(string? utm, string? referer, string esperado) => Assert.Equal(esperado, PaginasImovelService.Origem(utm, referer));

    [Theory]
    [InlineData("WhatsApp/2.24.1 A", true)]
    [InlineData("facebookexternalhit/1.1", true)]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1)", true)]
    [InlineData(null, true)]
    [InlineData("Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 Chrome/131.0 Mobile Safari/537.36", false)]
    public void Robos_nao_contam(string? ua, bool robo) => Assert.Equal(robo, PaginasImovelService.EhRobo(ua));

    [Theory]
    [InlineData("(17) 99999-1234", "5517999991234")]
    [InlineData("+55 17 3333-1234", "551733331234")]
    [InlineData("99999-1234", null)]
    [InlineData("017 99999 1234", null)]
    public void Telefone_do_formulario_exige_ddd(string tel, string? esperado) => Assert.Equal(esperado, ContatoPaginaForm.Digitos(tel));

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

    [Fact]
    public async Task Ia_gera_proposta_sem_gravar_e_so_com_fotos_publicas()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var drive = (ArmazenamentoMemoria)S<IArmazenamento>();
        var falso = new GeradorPaginaFalso();
        var svc = new PaginasImovelService(S<IDbContextFactory<InovabensDb>>(), S<AuditService>(), S<PublicacaoFotosService>(),
            drive, S<IArmazenamentoPublico>(), falso);
        var id = await NovoImovel(admin, "Casa IA " + Guid.NewGuid().ToString("N")[..6]);
        var (pub, priv) = await Fotos(id);
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
        {
            // as duas fotos existem no Drive; só a pública pode ir para a IA
            foreach (var m in await db.ImovelMidias.Where(m => m.ImovelId == id).ToListAsync())
                m.ArquivoChave = await drive.EnviarAsync(ArmazenamentoMemoria.Raiz, m.Id + ".jpg", new MemoryStream(Imagens.Gerar(1600, 1200)), "image/jpeg");
            await db.SaveChangesAsync();
        }

        var proposta = await svc.GerarComIaAsync(admin, id, "  foco em investidores  ");
        var f = proposta.Form;
        Assert.Equal(["Valor do condomínio"], proposta.Faltando);
        Assert.Equal("foco em investidores", falso.Instrucoes);
        Assert.Single(falso.Ultimo!.Fotos);
        Assert.True(Imagens.Tamanho(falso.Ultimo.Fotos[0]).W <= 1024);
        Assert.Equal("Casa para investir", f.Titulo);
        Assert.Null((await svc.EdicaoAsync(admin, id)).Pagina); // proposta não grava nada

        await Assert.ThrowsAsync<RegraNegocioException>(() => svc.GerarComIaAsync(admin, id, new string('x', PaginasImovelService.MaxInstrucoesIa + 1)));
        var carla = await Entrar("carla@inovabens.com.br");
        await Assert.ThrowsAnyAsync<Exception>(() => svc.GerarComIaAsync(carla, id, null));
        // sem chave do Claude (serviço do container): avisa em vez de chamar a API
        var erro = await Assert.ThrowsAsync<RegraNegocioException>(() => S<PaginasImovelService>().GerarComIaAsync(admin, id, null));
        Assert.Contains("chave do Claude", erro.Message);
    }

    [Fact]
    public async Task Medicao_conta_so_pagina_publicada_e_agrupa_por_origem()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var svc = S<PaginasImovelService>();
        var id = await NovoImovel(admin, "Casa medida " + Guid.NewGuid().ToString("N")[..6]);
        var ed = await svc.EdicaoAsync(admin, id);
        await svc.SalvarAsync(admin, id, ed.Form);
        var slug = ed.Form.Slug;

        await svc.RegistrarEventoAsync(slug, PaginaImovelEvento.Visita, "whatsapp"); // rascunho: não conta
        Assert.Equal(0, (await svc.DesempenhoAsync(admin, id)).TotalVisitas);

        await svc.PublicarAsync(admin, id, true);
        await svc.RegistrarEventoAsync(slug, PaginaImovelEvento.Visita, "whatsapp");
        await svc.RegistrarEventoAsync(slug, PaginaImovelEvento.Visita, "whatsapp");
        await svc.RegistrarEventoAsync(slug, PaginaImovelEvento.Visita, "qrcode");
        await svc.RegistrarEventoAsync(slug, PaginaImovelEvento.Whatsapp, "whatsapp");
        await svc.RegistrarEventoAsync(slug, PaginaImovelEvento.Visita, "inventada"); // origem fora da lista vira "outro"
        await svc.RegistrarEventoAsync(slug, "QUALQUER", "whatsapp"); // tipo desconhecido: ignorado
        await svc.RegistrarEventoAsync("nao-existe", PaginaImovelEvento.Visita, "link"); // sem erro

        var d = await svc.DesempenhoAsync(admin, id);
        Assert.Equal(4, d.TotalVisitas);
        Assert.Equal(1, d.TotalWhatsapp);
        Assert.Equal(("whatsapp", 2, 1), (d.Linhas[0].Origem, d.Linhas[0].Visitas, d.Linhas[0].Whatsapp));
        Assert.Contains(d.Linhas, l => l.Origem == "outro" && l.Visitas == 1);
        Assert.NotNull(d.UltimaVisita);

        var carla = await Entrar("carla@inovabens.com.br");
        await Assert.ThrowsAsync<AcessoNegadoException>(() => svc.DesempenhoAsync(carla, id));
    }

    [Fact]
    public async Task Formulario_da_pagina_cria_lead_com_consentimento()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var paginas = S<PaginasImovelService>();
        var contato = S<ContatoPaginaService>();
        var id = await NovoImovel(admin, "Casa contato " + Guid.NewGuid().ToString("N")[..6]);
        var ed = await paginas.EdicaoAsync(admin, id);
        await paginas.SalvarAsync(admin, id, ed.Form);
        var slug = ed.Form.Slug;
        var tel = $"(17) 9{Random.Shared.Next(1000, 9999)}-{Random.Shared.Next(1000, 9999)}";
        ContatoPaginaForm F() => new() { Nome = " Maria Teste ", Telefone = tel, Mensagem = "Quero visitar.", Consentimento = true };

        Assert.False(await contato.EnviarAsync(slug, F())); // rascunho: formulário não grava
        await paginas.PublicarAsync(admin, id, true);

        // todos os campos obrigatórios, telefone com DDD e consentimento
        foreach (var ruim in new[]
        {
            new ContatoPaginaForm { Telefone = tel, Mensagem = "x", Consentimento = true },
            new ContatoPaginaForm { Nome = "a", Telefone = "9999-1234", Mensagem = "x", Consentimento = true },
            new ContatoPaginaForm { Nome = "a", Telefone = tel, Consentimento = true },
            new ContatoPaginaForm { Nome = "a", Telefone = tel, Mensagem = "x" },
        })
            await Assert.ThrowsAsync<RegraNegocioException>(() => contato.EnviarAsync(slug, ruim));

        var isca = F(); isca.Site = "http://spam";
        Assert.True(await contato.EnviarAsync(slug, isca)); // robô: finge sucesso e não grava
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
            Assert.False(await db.Leads.AnyAsync(l => l.ImovelSugeridoId == id));

        Assert.True(await contato.EnviarAsync(slug, F(), "qrcode"));
        Assert.True(await contato.EnviarAsync(slug, F(), "qrcode")); // mesma pessoa: mesmo lead, nova interação
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
        {
            var lead = await db.Leads.Include(l => l.Pessoa).SingleAsync(l => l.ImovelSugeridoId == id);
            Assert.Equal(ContatoPaginaService.Origem, lead.Origem);
            Assert.Equal("Maria Teste", lead.Pessoa!.NomeRazao);
            var interacoes = await db.LeadInteracoes.Where(i => i.LeadId == lead.Id).ToListAsync();
            Assert.Equal(2, interacoes.Count);
            Assert.All(interacoes, i => Assert.Contains("Consentimento LGPD aceito", i.Resumo));
            Assert.True(await db.Alertas.AnyAsync(a => a.LeadId == lead.Id && a.Tipo == TipoAlerta.NOVO_LEAD));
        }
        var d = await paginas.DesempenhoAsync(admin, id);
        Assert.Equal(2, d.TotalFormularios);
        Assert.Contains(d.Linhas, l => l.Origem == "qrcode" && l.Formularios == 2);
    }
}

/// <summary>Claude de mentira para a página: guarda o pedido e devolve uma proposta fixa.</summary>
public sealed class GeradorPaginaFalso : IGeradorPagina
{
    public DadosPost? Ultimo { get; private set; }
    public string? Instrucoes { get; private set; }
    public bool Configurado => true;

    public Task<PaginaGerada> GerarAsync(DadosPost dados, string? instrucoes, CancellationToken ct = default)
    {
        (Ultimo, Instrucoes) = (dados, instrucoes);
        return Task.FromResult(new PaginaGerada
        {
            Titulo = "Casa para investir", Chamada = "Boa casa.", TituloSeo = "Casa", DescricaoSeo = "Casa em Jales",
            Secoes = [new() { Titulo = "Sobre", Texto = "Texto." }],
            Faltando = ["Valor do condomínio", " ", "Valor do condomínio"],
        });
    }
}
