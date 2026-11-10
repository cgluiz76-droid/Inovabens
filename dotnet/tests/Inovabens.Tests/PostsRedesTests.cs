using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Redes;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Inovabens.Tests;

public class PostsRedesUnidadeTests
{
    private static readonly InfoCapa Capa = new("À VENDA", "R$ 480.000,00", "3 quartos · 2 vagas · 120 m²", "Centro · São José do Rio Preto/SP", "CA0060", "(17) 99999-0000", "12345-J");

    [Theory]
    [InlineData(4000, 3000)] // horizontal: fundo desfocado
    [InlineData(1200, 1300)] // quase no formato: corta as sobras
    [InlineData(900, 1600)]  // vertical
    public void Imagens_saem_no_tamanho_exato_de_cada_rede(int w, int h)
    {
        var foto = Imagens.Gerar(w, h);
        Assert.Equal((1080, 1350), Imagens.Tamanho(ImagemPost.Finalizar(foto, 1080, 1350, Capa, jaExpandida: false)));
        Assert.Equal((1080, 1920), Imagens.Tamanho(ImagemPost.Finalizar(foto, 1080, 1920, null, jaExpandida: false)));
        Assert.Equal((720, 1280), Imagens.Tamanho(ImagemPost.BaseClipe(foto)));
    }

    [Fact]
    public void Expansao_pede_so_as_bordas_que_faltam_dentro_do_limite_do_Magnific()
    {
        var p = ImagemPost.PrepararExpansao(Imagens.Gerar(4000, 3000), 1080, 1350)!;
        var (w, h) = Imagens.Tamanho(p.Jpeg);
        Assert.Equal(0, w % 16);
        Assert.True(w * (h + p.Cima + p.Baixo) <= 1_600_000);
        Assert.Equal(0.8, w / (double)(h + p.Cima + p.Baixo), 2);
        Assert.Equal((0, 0), (p.Esquerda, p.Direita));
        Assert.InRange(p.Cima - p.Baixo, -1, 1);

        // foto já vertical não gasta crédito: só corta
        Assert.Null(ImagemPost.PrepararExpansao(Imagens.Gerar(900, 1600), 1080, 1350));
        Assert.NotNull(ImagemPost.PrepararExpansao(Imagens.Gerar(1200, 1300), 1080, 1920));
    }

    [Fact]
    public void Camadas_do_video_tem_o_tamanho_vertical()
    {
        Assert.Equal((1080, 1920), Imagens.Tamanho(ImagemPost.Sobreposicao("3 quartos, 1 suíte com closet e varanda gourmet")));
        Assert.Equal((1080, 1920), Imagens.Tamanho(ImagemPost.Sobreposicao("")));
        Assert.Equal((1080, 1920), Imagens.Tamanho(ImagemPost.CartaoFinal(Capa, "Agende sua visita")));
    }

    [Fact]
    public void Pedido_ao_Claude_leva_os_dados_do_imovel_e_o_foco()
    {
        var d = new DadosPost(FinalidadeImovel.LOCACAO, "apartamento", null, "Apartamento no Centro", "Sol da manhã.", "Centro", "São José do Rio Preto", "SP",
            2, 1, 2, 1, 70m, 2500m, 600m, 120m, AceitaPermuta: true, ["Piscina", "Portaria 24h"], "AP0010", "https://site/imovel/10", "(17) 99999-0000", "12345-J", [[1], [2]]);
        var texto = ClaudeTextosPost.PedidoUsuario(d, 4);
        Assert.Contains("LOCAÇÃO", texto);
        Assert.Contains("Aluguel: R$ 2.500,00 por mês", texto);
        Assert.Contains("Condomínio: R$ 600,00", texto);
        Assert.Contains("Centro, São José do Rio Preto/SP", texto);
        Assert.Contains("2 (1 suíte)", texto);
        Assert.Contains("Piscina; Portaria 24h", texto);
        Assert.Contains("CRECI: 12345-J", texto);
        Assert.Contains("roteiro com 4 cenas", texto);
        Assert.DoesNotContain("permuta", texto); // permuta só na venda
        Assert.Contains("Preço: sob consulta", ClaudeTextosPost.PedidoUsuario(d with { Finalidade = FinalidadeImovel.VENDA, Preco = null }, 0));
    }

    [Fact]
    public void Esquema_da_resposta_segue_as_regras_de_structured_outputs()
    {
        var e = ClaudeTextosPost.Esquema();
        Assert.False(e["additionalProperties"].GetBoolean());
        var obrigatorios = e["required"].EnumerateArray().Select(x => x.GetString()!).ToHashSet();
        Assert.Equal(e["properties"].EnumerateObject().Select(p => p.Name).ToHashSet(), obrigatorios);
        var cena = e["properties"].GetProperty("cenas").GetProperty("items");
        Assert.False(cena.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(3, cena.GetProperty("required").GetArrayLength());
    }

    [Fact]
    public void Resposta_do_Claude_e_corrigida_e_JSON_invalido_vira_erro_legivel()
    {
        const string json = """{"instagram":"Oi #riopreto","facebook":"f","tiktok":"t","linkedin":"l","whatsapp":"w","youtube_titulo":"y","youtube_descricao":"d","texto_final":"Agende","musica":"calm piano","cenas":[{"foto":9,"texto_tela":"A","movimento":"m"},{"foto":0,"texto_tela":"B","movimento":"m"},{"foto":2,"texto_tela":"C","movimento":"m"}]}""";
        var t = ClaudeTextosPost.Interpretar(json, cenas: 2, fotos: 3);
        Assert.Equal("Oi #riopreto", t.Instagram);
        Assert.Equal([3, 1], t.Cenas.Select(c => c.Foto));
        Assert.Equal(t.Instagram, TextosPost.De(t.ParaJson()).Instagram); // ida e volta do banco
        Assert.Throws<ServicoIndisponivelException>(() => ClaudeTextosPost.Interpretar("não é json", 2, 3));
    }

    [Fact]
    public void Roteiro_incompleto_e_completado_com_as_fotos_nao_usadas()
    {
        var t = new TextosPost { Cenas = [new CenaVideo { Foto = 2 }] };
        PostsService.CompletarCenas(t, 4, 3);
        Assert.Equal([2, 1, 3, 1], t.Cenas.Select(c => c.Foto));
    }

    [Fact]
    public void Estimativa_de_creditos_e_auxiliares()
    {
        var f = new NovoPostForm { Fotos = [.. Enumerable.Range(0, 5).Select(_ => Guid.NewGuid())], ComVideo = true, CenasVideo = 4 };
        Assert.Equal(new EstimativaCreditos(0, 4, 1), PostsService.Estimar(f));
        f.ExpandirComIa = true;
        Assert.Equal(5 + 3 + 4, PostsService.Estimar(f).Expansoes);
        Assert.Equal(0, PostsService.Estimar(new NovoPostForm { Fotos = [Guid.NewGuid()] }).Total);

        Assert.Equal("(17) 99999-0000", PostsService.FormatarTelefone("5517999990000"));
        Assert.Equal("(17) 3333-0000", PostsService.FormatarTelefone("1733330000"));
        Assert.Null(PostsService.FormatarTelefone(null));
        Assert.Equal(("audio/wav", ".wav"), PostsService.TipoAudio("RIFF0000WAVEfmt "u8.ToArray()));
        Assert.Equal(("audio/mpeg", ".mp3"), PostsService.TipoAudio("ID3\u0004\0\0\0\0\0\0"u8.ToArray()));
    }

    [Fact]
    public void Montagem_usa_musica_em_loop_ou_audio_silencioso()
    {
        var com = string.Join(' ', MontagemVideo.ArgsFinal("v.mp4", "m.mp3", "f.mp4", 23));
        Assert.Contains("-stream_loop -1 -i m.mp3", com);
        Assert.Contains("afade=t=out:st=21.5:d=1.5", com);
        Assert.Contains("-t 23", com);
        Assert.Contains("+faststart", com);
        Assert.Contains("anullsrc", string.Join(' ', MontagemVideo.ArgsFinal("v.mp4", null, "f.mp4", 23)));
    }

    /// <summary>Monta um vídeo de verdade quando o FFmpeg está instalado (no CI sem FFmpeg, só confere a detecção).</summary>
    [Fact]
    public async Task Montagem_real_com_FFmpeg_gera_video_vertical()
    {
        var m = new MontagemVideo(NullLogger<MontagemVideo>.Instance);
        if (m.Caminho is not { } ffmpeg) return;
        var pasta = Directory.CreateTempSubdirectory("teste-video-").FullName;
        try
        {
            byte[] Clipe(string nome)
            {
                var arq = Path.Combine(pasta, nome);
                var p = Process.Start(new ProcessStartInfo(ffmpeg, $"-y -hide_banner -loglevel error -f lavfi -i testsrc=size=720x1280:rate=24 -t 5 -pix_fmt yuv420p \"{arq}\"")
                    { UseShellExecute = false, CreateNoWindow = true })!;
                p.WaitForExit();
                return File.ReadAllBytes(arq);
            }
            var trechos = new[] { new TrechoVideo(Clipe("a.mp4"), ImagemPost.Sobreposicao("Sala ampla")), new TrechoVideo(Clipe("b.mp4"), ImagemPost.Sobreposicao("")) };
            var video = await m.MontarAsync(trechos, ImagemPost.CartaoFinal(Capa, "Agende"), musica: null);
            Assert.True(video.Length > 10_000);
            Assert.Equal("ftyp", Encoding.ASCII.GetString(video, 4, 4));
        }
        finally
        {
            Directory.Delete(pasta, recursive: true);
        }
    }
}

/// <summary>Claude de mentira: devolve textos fixos e guarda o pedido.</summary>
public sealed class GeradorTextosFalso : IGeradorTextosPost
{
    public DadosPost? Ultimo { get; private set; }
    public bool Configurado => true;

    public Task<TextosPost> GerarAsync(DadosPost dados, int cenas, CancellationToken ct = default)
    {
        Ultimo = dados;
        return Task.FromResult(new TextosPost
        {
            Instagram = "Casa linda no Centro! #riopreto", Facebook = "Casa à venda", Tiktok = "Tour", Linkedin = "Patrimônio",
            Whatsapp = "Olha essa casa", YoutubeTitulo = "Casa", YoutubeDescricao = "Desc",
            Cenas = [.. Enumerable.Range(1, Math.Max(0, cenas - 1)).Select(i => new CenaVideo { Foto = i, TextoTela = $"Cena {i}", Movimento = "slow dolly in" })],
        });
    }
}

/// <summary>Magnific e Graph API da Meta de mentira, respondendo pela URL.</summary>
public sealed class HttpFalso : HttpMessageHandler
{
    public ConcurrentQueue<(string Metodo, string Url, string Corpo, string? Autorizacao)> Pedidos { get; } = new();
    private int _n;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var url = req.RequestUri!.ToString();
        var corpo = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
        Pedidos.Enqueue((req.Method.Method, url, corpo, req.Headers.Authorization?.Parameter));
        var n = Interlocked.Increment(ref _n);

        if (url.StartsWith("https://magnific.exemplo/")) // resultado do Expand: 4:5 já expandida
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Imagens.Gerar(1088, 1360)) };
        if (url.StartsWith(MagnificCliente.Base))
            return Json(req.Method == HttpMethod.Post
                ? new { data = new { task_id = $"t{n}", status = "CREATED" } }
                : new { data = new { task_id = url.Split('/')[^1], status = "COMPLETED", generated = new[] { $"https://magnific.exemplo/{n}.jpg" } } });

        if (url.Contains("graph.facebook.com"))
        {
            if (url.Contains("fields=status_code")) return Json(new { status_code = "FINISHED" });
            if (url.Contains("fields=permalink")) return Json(new { permalink = "https://www.instagram.com/p/abc/" });
            if (url.EndsWith("/media_publish")) return Json(new { id = "ig-post-1" });
            if (url.EndsWith("/feed")) return Json(new { id = "pagina_99" });
            return Json(new { id = $"c{n}" });
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object o) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
}

/// <summary>Fluxo completo com banco real: textos, imagens (com e sem IA), publicação no Instagram e no Facebook.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class PostsRedesIntegracaoTests : IAsyncLifetime
{
    // usa o provedor compartilhado (cada provedor novo conta no limite de 20 do EF) e monta o serviço com os falsos
    private static readonly ArmazenamentoPublicoMemoria _r2 = new();
    private static readonly GeradorTextosFalso _claude = new();
    private static readonly HttpFalso _http = new();
    private static readonly InovabensOpcoes _op = InovabensOpcoes.De(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["DATABASE_URL"] = BancoFixture.Url,
        ["MAGNIFIC_API_KEY"] = "chave-magnific",
        ["META_PAGE_ID"] = "pagina",
        ["META_INSTAGRAM_ID"] = "ig",
        ["META_PAGE_TOKEN"] = "token-pagina",
        ["SITE_WHATSAPP"] = "17999990000",
    }).Build(), producao: false);
    private IServiceScope _escopo = default!;
    private PostsService _posts = default!;

    public Task InitializeAsync()
    {
        _escopo = ExtratoComprovantesTests.Compartilhado.CreateScope();
        _escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        _http.Pedidos.Clear();
        _posts = new PostsService(S<IDbContextFactory<InovabensDb>>(), S<ImoveisService>(), S<IArmazenamento>(), _r2,
            new MagnificCliente(new HttpClient(_http), _op), _claude, new MontagemVideo(NullLogger<MontagemVideo>.Instance),
            new MetaPublicador(new HttpClient(_http), _op), _op, NullLogger<PostsService>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _escopo.Dispose();
        return Task.CompletedTask;
    }

    private T S<T>() where T : notnull => _escopo.ServiceProvider.GetRequiredService<T>();

    private async Task<(UsuarioAtual Admin, Guid Imovel, List<Guid> Fotos)> Preparar()
    {
        var admin = (await S<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var imoveis = S<ImoveisService>();
        var id = await imoveis.CriarAsync(admin, new ImovelForm
        {
            NomeDrive = Guid.NewGuid().ToString(), Titulo = "Casa no Centro", Tipo = "casa", Finalidade = FinalidadeImovel.VENDA,
            PrecoVenda = 480_000m, Quartos = 3, Vagas = 2, Bairro = "Centro", Cidade = "São José do Rio Preto", Uf = "SP",
        });
        foreach (var (nome, w, h) in new[] { ("fachada.jpg", 1600, 1200), ("sala.jpg", 1200, 1500), ("cozinha.jpg", 1600, 900) })
        {
            using var s = new MemoryStream(Imagens.Gerar(w, h));
            await imoveis.EnviarMidiaAsync(admin, id, s, nome, "image/jpeg", SubpastasDrive.FotosVideos, publica: true);
        }
        var fotos = (await imoveis.MidiasAsync(admin, id)).OrderBy(m => m.Midia.Ordem).Select(m => m.Midia.Id).ToList();
        return (admin, id, fotos);
    }

    [Fact]
    public async Task Post_sem_IA_sai_pronto_e_publica_no_Instagram_e_no_Facebook()
    {
        var (admin, imovel, fotos) = await Preparar();
        var posts = _posts;
        var postId = await posts.CriarAsync(admin, imovel, new NovoPostForm { Fotos = fotos });

        // a IA recebeu os dados e as fotos reduzidas; o post já nasce com as imagens locais prontas
        Assert.Equal(480_000m, _claude.Ultimo!.Preco);
        Assert.Equal(3, _claude.Ultimo.Fotos.Count);
        Assert.Equal("(17) 99999-0000", _claude.Ultimo.Whatsapp);
        var d = await posts.DetalheAsync(admin, postId);
        Assert.Equal(3, d.Feed.Count());
        Assert.Equal(3, d.Stories.Count());
        Assert.All(d.Pecas, p => Assert.Equal(StatusMidiaPost.CONCLUIDA, p.Midia.Status));
        Assert.Null(d.Video);
        Assert.Equal((1080, 1350), Imagens.Tamanho(_r2.Arquivos[d.Feed.First().Midia.Chave!].Dados));
        Assert.Equal((1080, 1920), Imagens.Tamanho(_r2.Arquivos[d.Stories.First().Midia.Chave!].Dados));
        Assert.Equal("image/jpeg", d.Feed.First().Midia.ContentType);
        Assert.Empty(_http.Pedidos); // sem Magnific nem Meta até aqui

        // textos editados valem na publicação
        d.Textos.Instagram = "Legenda revisada #riopreto";
        await posts.SalvarTextosAsync(admin, postId, d.Textos);

        var ig = await posts.PublicarAsync(admin, postId, RedePublicacao.INSTAGRAM_FEED);
        Assert.Equal(StatusPublicacaoPost.PUBLICADA, ig.Status);
        Assert.Equal("https://www.instagram.com/p/abc/", ig.Link);
        var midia = _http.Pedidos.Where(p => p.Url.EndsWith("/ig/media")).ToList();
        Assert.Equal(4, midia.Count); // 3 itens do carrossel + o carrossel
        Assert.Contains("media_type=CAROUSEL", midia[^1].Corpo);
        Assert.Contains(Uri.EscapeDataString("Legenda revisada #riopreto").Replace("%20", "+"), midia[^1].Corpo);
        Assert.All(_http.Pedidos, p =>
        {
            Assert.Equal("token-pagina", p.Autorizacao); // token no cabeçalho, nunca na URL
            Assert.DoesNotContain("token-pagina", p.Url);
        });
        await Assert.ThrowsAsync<RegraNegocioException>(() => posts.PublicarAsync(admin, postId, RedePublicacao.INSTAGRAM_FEED));

        var fb = await posts.PublicarAsync(admin, postId, RedePublicacao.FACEBOOK_FEED);
        Assert.Equal(StatusPublicacaoPost.PUBLICADA, fb.Status);
        Assert.Equal("https://www.facebook.com/pagina_99", fb.Link);
        Assert.Equal(3, _http.Pedidos.Count(p => p.Url.EndsWith("/pagina/photos") && p.Corpo.Contains("published=false")));

        // sem vídeo, Reels não tem o que publicar
        await Assert.ThrowsAsync<RegraNegocioException>(() => posts.PublicarAsync(admin, postId, RedePublicacao.INSTAGRAM_REELS));

        var arquivo = await posts.ArquivoAsync(admin, d.Feed.First().Midia.Id);
        Assert.EndsWith("-feed-1.jpg", arquivo.Nome);

        await posts.ExcluirAsync(admin, postId);
        Assert.DoesNotContain(_r2.Arquivos.Keys, k => k.Contains(postId.ToString("N")));
        Assert.Empty(await posts.ListarAsync(admin, imovel));
    }

    [Fact]
    public async Task Bordas_com_IA_usam_o_Expand_do_Magnific_so_nas_fotos_horizontais()
    {
        var (admin, imovel, fotos) = await Preparar();
        var posts = _posts;
        await Assert.ThrowsAsync<RegraNegocioException>(() =>
            posts.CriarAsync(admin, imovel, new NovoPostForm { Fotos = fotos, ExpandirComIa = true })); // sem confirmar créditos

        var postId = await posts.CriarAsync(admin, imovel, new NovoPostForm { Fotos = fotos, ExpandirComIa = true, ConfirmoCreditos = true });
        var d = await posts.DetalheAsync(admin, postId);
        // a sala (vertical) não precisa no feed 4:5; as horizontais esperam o Magnific
        var feed = d.Feed.ToList();
        Assert.Equal(EtapaMidiaPost.EXPANDIR, feed[0].Midia.Etapa);
        Assert.Equal(StatusMidiaPost.CONCLUIDA, feed[1].Midia.Status);
        Assert.Equal(EtapaMidiaPost.EXPANDIR, feed[2].Midia.Etapa);
        var criadas = _http.Pedidos.Count(p => p.Metodo == "POST" && p.Url == MagnificCliente.Base + "image-expand/flux-pro");
        Assert.Equal(2 + 3, criadas); // 2 do feed + 3 stories (9:16 é mais alto que todas)
        Assert.Contains("\"top\":", _http.Pedidos.First(p => p.Url.EndsWith("flux-pro")).Corpo);

        Assert.True(await posts.ProcessarAsync() >= 5);
        d = await posts.DetalheAsync(admin, postId);
        Assert.All(d.Pecas, p => Assert.Equal(StatusMidiaPost.CONCLUIDA, p.Midia.Status));
        Assert.Equal((1080, 1350), Imagens.Tamanho(_r2.Arquivos[d.Feed.First().Midia.Chave!].Dados));
        Assert.False(d.EmAndamento);
    }

    [Fact]
    public async Task Corretor_sem_permissao_de_editar_nao_gera_post()
    {
        var (admin, imovel, fotos) = await Preparar();
        var leitor = admin with { Permissoes = new HashSet<string> { Permissoes.ImoveisVer } };
        await Assert.ThrowsAsync<AcessoNegadoException>(() => _posts.CriarAsync(leitor, imovel, new NovoPostForm { Fotos = fotos }));
        await Assert.ThrowsAsync<RegraNegocioException>(() => _posts.CriarAsync(admin, imovel, new NovoPostForm { Fotos = [Guid.NewGuid()] }));
    }
}
