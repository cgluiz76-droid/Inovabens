using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Relatorios;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Redes;

/// <summary>Pedido de um post novo (aba Redes sociais do imóvel).</summary>
public sealed class NovoPostForm
{
    public FinalidadeImovel Finalidade { get; set; } = FinalidadeImovel.VENDA;
    /// <summary>Fotos escolhidas, na ordem do carrossel (1 a 10).</summary>
    public List<Guid> Fotos { get; set; } = [];
    /// <summary>Completa as bordas com o Magnific (consome créditos) em vez do fundo desfocado.</summary>
    public bool ExpandirComIa { get; set; }
    /// <summary>Gera o vídeo curto (Reels/TikTok/Shorts) com as fotos animadas pelo Magnific.</summary>
    public bool ComVideo { get; set; }
    [Range(3, 6, ErrorMessage = "O vídeo usa de 3 a 6 fotos.")]
    public int CenasVideo { get; set; } = 4;
    /// <summary>Ciência de que o Magnific cobra créditos por tarefa.</summary>
    public bool ConfirmoCreditos { get; set; }
}

/// <summary>Tarefas do Magnific que o post pode criar ("até": fotos já verticais não precisam de expansão).</summary>
public sealed record EstimativaCreditos(int Expansoes, int Clipes, int Musicas)
{
    public int Total => Expansoes + Clipes + Musicas;
}

public sealed record PostResumo(Guid Id, FinalidadeImovel Finalidade, DateTime CriadoEm, string? CriadoPorNome,
    int Pecas, int Prontas, int Falhas, int Publicadas);

public sealed record PecaPost(PostRedesMidia Midia, string? Url);

public sealed record PostDetalhe(PostRedes Post, TextosPost Textos, IReadOnlyList<PecaPost> Pecas, IReadOnlyList<PostRedesPublicacao> Publicacoes,
    string? LinkAnuncio, string? Whatsapp)
{
    public IEnumerable<PecaPost> Feed => Pecas.Where(p => p.Midia.Tipo == TipoMidiaPost.FEED).OrderBy(p => p.Midia.Ordem);
    public IEnumerable<PecaPost> Stories => Pecas.Where(p => p.Midia.Tipo == TipoMidiaPost.STORY).OrderBy(p => p.Midia.Ordem);
    public IEnumerable<PecaPost> Clipes => Pecas.Where(p => p.Midia.Tipo == TipoMidiaPost.CLIPE).OrderBy(p => p.Midia.Ordem);
    public PecaPost? Video => Pecas.FirstOrDefault(p => p.Midia.Tipo == TipoMidiaPost.VIDEO);
    public bool EmAndamento => Pecas.Any(p => p.Midia.Status is StatusMidiaPost.PENDENTE or StatusMidiaPost.PROCESSANDO)
        || Publicacoes.Any(p => p.Status == StatusPublicacaoPost.AGUARDANDO);
}

/// <summary>
/// Posts dos imóveis para redes sociais: textos de cada rede (Claude), imagens nos formatos de feed e story,
/// vídeo curto com as fotos animadas pelo Magnific (Kling) e música (Lyria), montado com FFmpeg, e publicação no
/// Facebook e no Instagram pela Graph API. As peças ficam no R2 (posts/...), que a Meta precisa acessar por URL.
/// O trabalho demorado roda na rotina <c>PostsRedesJob</c> (a cada minuto).
/// </summary>
public sealed class PostsService(
    IDbContextFactory<InovabensDb> fabrica, ImoveisService imoveis, IArmazenamento drive, IArmazenamentoPublico publico,
    MagnificCliente magnific, IGeradorTextosPost gerador, MontagemVideo montagem, MetaPublicador meta, InovabensOpcoes op,
    ILogger<PostsService> log)
{
    private const int PorRodada = 20;
    /// <summary>Contêiner do Instagram que não ficou pronto nesse prazo é dado como falho.</summary>
    private static readonly TimeSpan PrazoInstagram = TimeSpan.FromHours(2);

    public const string PromptExpansao =
        "Seamlessly continue the photo beyond its borders: same real estate scene, same architecture, materials and lighting, " +
        "realistic professional real estate photography, no people, no text.";
    public const string NegativoClipe = "people, person, animals, text, watermark, logo, distortion, warping, morphing, new furniture, new objects, cartoon";

    /// <summary>O que falta configurar para gerar posts (vazio = tudo certo).</summary>
    public IReadOnlyList<string> Pendencias(bool comMagnific, bool comVideo)
    {
        var p = new List<string>();
        if (!gerador.Configurado) p.Add("chave do Claude (Administração → Configurações → Claude)");
        if (!publico.Habilitado || publico.Url("x") is null) p.Add("Cloudflare R2 com endereço público (as redes baixam as imagens por ele)");
        if (!drive.Habilitado) p.Add("Google Drive (fotos do imóvel)");
        if ((comMagnific || comVideo) && !magnific.Configurado) p.Add("chave do Magnific");
        if (comVideo && !montagem.Disponivel) p.Add("FFmpeg instalado no servidor (montagem do vídeo)");
        return p;
    }

    public bool MetaConfigurada => meta.Configurado;
    public bool InstagramConfigurado => meta.InstagramConfigurado;
    public bool FacebookConfigurado => meta.FacebookConfigurado;

    public static EstimativaCreditos Estimar(NovoPostForm f)
    {
        var fotos = Math.Min(f.Fotos.Count, 10);
        var stories = Math.Min(fotos, 3);
        var clipes = f.ComVideo ? Math.Min(f.CenasVideo, fotos) : 0;
        var expansoes = f.ExpandirComIa ? fotos + stories + clipes : 0;
        return new EstimativaCreditos(expansoes, clipes, f.ComVideo && clipes > 0 ? 1 : 0);
    }

    /* ------------------------------ consultas ------------------------------ */

    public async Task<IReadOnlyList<PostResumo>> ListarAsync(UsuarioAtual u, Guid imovelId)
    {
        await imoveis.DetalheAsync(u, imovelId); // confere acesso ao imóvel
        await using var db = await fabrica.CreateDbContextAsync();
        var posts = await db.PostsRedes.AsNoTracking().Where(p => p.ImovelId == imovelId).OrderByDescending(p => p.CriadoEm).ToListAsync();
        var ids = posts.Select(p => p.Id).ToList();
        var midias = await db.PostsRedesMidias.AsNoTracking().Where(m => ids.Contains(m.PostId)).Select(m => new { m.PostId, m.Status }).ToListAsync();
        var pubs = await db.PostsRedesPublicacoes.AsNoTracking().Where(p => ids.Contains(p.PostId) && p.Status == StatusPublicacaoPost.PUBLICADA)
            .GroupBy(p => p.PostId).Select(g => new { g.Key, Qtd = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Qtd);
        var autores = posts.Where(p => p.CriadoPor != null).Select(p => p.CriadoPor!.Value).Distinct().ToList();
        var nomes = await db.Usuarios.AsNoTracking().Where(x => autores.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Nome);
        return [.. posts.Select(p =>
        {
            var m = midias.Where(x => x.PostId == p.Id).ToList();
            return new PostResumo(p.Id, p.Finalidade, p.CriadoEm, p.CriadoPor is { } c ? nomes.GetValueOrDefault(c) : null,
                m.Count, m.Count(x => x.Status == StatusMidiaPost.CONCLUIDA), m.Count(x => x.Status == StatusMidiaPost.FALHA), pubs.GetValueOrDefault(p.Id));
        })];
    }

    public async Task<PostDetalhe> DetalheAsync(UsuarioAtual u, Guid postId)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var post = await db.PostsRedes.AsNoTracking().FirstOrDefaultAsync(p => p.Id == postId) ?? throw new NaoEncontradoException("Post não encontrado.");
        var detalhe = await imoveis.DetalheAsync(u, post.ImovelId);
        var midias = await db.PostsRedesMidias.AsNoTracking().Where(m => m.PostId == postId).OrderBy(m => m.Tipo).ThenBy(m => m.Ordem).ToListAsync();
        var pubs = await db.PostsRedesPublicacoes.AsNoTracking().Where(p => p.PostId == postId).OrderByDescending(p => p.CriadoEm).ToListAsync();
        return new PostDetalhe(post, TextosPost.De(post.Textos), [.. midias.Select(m => new PecaPost(m, publico.Url(m.Chave)))], pubs,
            LinkAnuncio(detalhe.Dados), FormatarTelefone(op.Integracoes["site.whatsapp"]));
    }

    public async Task<Arquivo> ArquivoAsync(UsuarioAtual u, Guid midiaId, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var m = await db.PostsRedesMidias.AsNoTracking().FirstOrDefaultAsync(x => x.Id == midiaId, ct) ?? throw new NaoEncontradoException("Arquivo não encontrado.");
        var post = await db.PostsRedes.AsNoTracking().FirstAsync(p => p.Id == m.PostId, ct);
        var detalhe = await imoveis.DetalheAsync(u, post.ImovelId);
        if (m.Chave is null) throw new NaoEncontradoException("Arquivo ainda não gerado.");
        var dados = await publico.BaixarAsync(m.Chave, ct) ?? throw new NaoEncontradoException("Arquivo não encontrado no armazenamento.");
        var ext = Path.GetExtension(m.Chave);
        var nome = $"{detalhe.Dados.Referencia ?? detalhe.Dados.Codigo}-{m.Tipo.ToString().ToLowerInvariant()}{(m.Tipo is TipoMidiaPost.FEED or TipoMidiaPost.STORY ? "-" + (m.Ordem + 1) : "")}{ext}";
        return new Arquivo(dados, nome, m.ContentType ?? "application/octet-stream");
    }

    /* ------------------------------ criação e edição ------------------------------ */

    /// <summary>Gera os textos (Claude) e agenda as peças; as imagens sem IA já saem prontas na volta.</summary>
    public async Task<Guid> CriarAsync(UsuarioAtual u, Guid imovelId, NovoPostForm f, CancellationToken ct = default)
    {
        u.Exigir(P.ImoveisEditar);
        if (f.Finalidade == FinalidadeImovel.AMBOS) throw new RegraNegocioException("finalidade", "Escolha venda ou locação.");
        if (f.Fotos.Count is < 1 or > 10) throw new RegraNegocioException("fotos", "Escolha de 1 a 10 fotos.");
        if (f.ComVideo && f.CenasVideo is < 3 or > 6) throw new RegraNegocioException("cenas", "O vídeo usa de 3 a 6 fotos.");
        if (f.ComVideo && f.Fotos.Count < 3) throw new RegraNegocioException("fotos", "Para o vídeo, escolha pelo menos 3 fotos.");
        var pendencias = Pendencias(f.ExpandirComIa, f.ComVideo);
        if (pendencias.Count > 0) throw new RegraNegocioException("configuracao", "Falta configurar: " + string.Join("; ", pendencias) + ".");
        if ((f.ExpandirComIa || f.ComVideo) && !f.ConfirmoCreditos)
            throw new RegraNegocioException("creditos", "Confirme o uso de créditos do Magnific.");

        var detalhe = await imoveis.DetalheAsync(u, imovelId);
        var fotosImovel = detalhe.Midias.Where(m => m.Midia.Tipo == TipoMidia.FOTO).ToDictionary(m => m.Midia.Id, m => m.Midia);
        var fotos = f.Fotos.Distinct().Select(id => fotosImovel.GetValueOrDefault(id)
            ?? throw new RegraNegocioException("fotos", "Foto não pertence a este imóvel.")).ToList();

        // fotos reduzidas para a IA ver o imóvel (descrições fiéis ao que aparece)
        var paraIa = new List<byte[]>();
        foreach (var m in fotos)
        {
            var arquivo = await drive.BaixarAsync(m.ArquivoChave, ct)
                ?? throw new RegraNegocioException("fotos", $"A foto {m.NomeOriginal ?? m.Id.ToString()} não foi encontrada no Google Drive.");
            paraIa.Add(ImagemPost.Reduzir(arquivo.Dados, 1024) ?? throw new RegraNegocioException("fotos", "Formato de foto não suportado (use JPG, PNG ou WebP)."));
        }

        var cenas = f.ComVideo ? Math.Min(f.CenasVideo, fotos.Count) : 0;
        var dados = Dados(detalhe, f.Finalidade, paraIa);
        var textos = await gerador.GerarAsync(dados, cenas, ct);
        CompletarCenas(textos, cenas, fotos.Count);

        var post = new PostRedes
        {
            ImovelId = imovelId, Finalidade = f.Finalidade, Textos = textos.ParaJson(), ExpandirComIa = f.ExpandirComIa,
            ComVideo = cenas > 0, CriadoPor = u.Id,
        };
        var midias = new List<PostRedesMidia>();
        for (var i = 0; i < fotos.Count; i++)
            midias.Add(new PostRedesMidia { PostId = post.Id, Tipo = TipoMidiaPost.FEED, MidiaOrigemId = fotos[i].Id, Ordem = i, Capa = i == 0 });
        for (var i = 0; i < Math.Min(3, fotos.Count); i++)
            midias.Add(new PostRedesMidia { PostId = post.Id, Tipo = TipoMidiaPost.STORY, MidiaOrigemId = fotos[i].Id, Ordem = i, Capa = i == 0 });
        if (cenas > 0)
        {
            for (var i = 0; i < cenas; i++)
                midias.Add(new PostRedesMidia { PostId = post.Id, Tipo = TipoMidiaPost.CLIPE, MidiaOrigemId = fotos[textos.Cenas[i].Foto - 1].Id, Ordem = i });
            midias.Add(new PostRedesMidia { PostId = post.Id, Tipo = TipoMidiaPost.MUSICA });
            midias.Add(new PostRedesMidia { PostId = post.Id, Tipo = TipoMidiaPost.VIDEO });
        }

        await using (var db = await fabrica.CreateDbContextAsync(ct))
        {
            db.PostsRedes.Add(post);
            db.PostsRedesMidias.AddRange(midias);
            await db.SaveChangesAsync(ct);
        }
        // adianta o trabalho deste post (imagens locais saem prontas; tarefas do Magnific já ficam criadas)
        try
        {
            await ProcessarAsync(ct, post.Id);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Post {Post}: processamento inicial adiado para a rotina", post.Id);
        }
        return post.Id;
    }

    public async Task SalvarTextosAsync(UsuarioAtual u, Guid postId, TextosPost textos)
    {
        u.Exigir(P.ImoveisEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var post = await PostComAcesso(db, u, postId);
        post.Textos = textos.ParaJson();
        post.AtualizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>Monta o vídeo de novo com os textos atuais (sem custo: reaproveita os clipes e a música).</summary>
    public async Task RemontarVideoAsync(UsuarioAtual u, Guid postId)
    {
        u.Exigir(P.ImoveisEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        await PostComAcesso(db, u, postId);
        var video = await db.PostsRedesMidias.FirstOrDefaultAsync(m => m.PostId == postId && m.Tipo == TipoMidiaPost.VIDEO)
            ?? throw new RegraNegocioException("video", "Este post não tem vídeo.");
        if (video.Status is StatusMidiaPost.PENDENTE or StatusMidiaPost.PROCESSANDO) return;
        video.Status = StatusMidiaPost.PENDENTE;
        video.Erro = null;
        video.AtualizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>Tenta de novo as peças que falharam (pode consumir créditos do Magnific outra vez).</summary>
    public async Task RefazerFalhasAsync(UsuarioAtual u, Guid postId)
    {
        u.Exigir(P.ImoveisEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        await PostComAcesso(db, u, postId);
        var falhas = await db.PostsRedesMidias.Where(m => m.PostId == postId && m.Status == StatusMidiaPost.FALHA).ToListAsync();
        foreach (var m in falhas)
        {
            m.Status = StatusMidiaPost.PENDENTE;
            m.Etapa = null;
            m.Tarefa = m.Erro = null;
            m.AtualizadoEm = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    public async Task ExcluirAsync(UsuarioAtual u, Guid postId)
    {
        u.Exigir(P.ImoveisEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var post = await PostComAcesso(db, u, postId);
        var chaves = await db.PostsRedesMidias.Where(m => m.PostId == postId && m.Chave != null).Select(m => m.Chave!).ToListAsync();
        db.PostsRedes.Remove(post);
        await db.SaveChangesAsync();
        foreach (var c in chaves) await publico.RemoverAsync(c);
    }

    /* ------------------------------ publicação (Meta) ------------------------------ */

    /// <summary>Publica no destino. Instagram: contêiner criado agora e publicado quando a Meta terminar de processar.</summary>
    public async Task<PostRedesPublicacao> PublicarAsync(UsuarioAtual u, Guid postId, RedePublicacao rede, CancellationToken ct = default)
    {
        u.Exigir(P.ImoveisEditar);
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var post = await PostComAcesso(db, u, postId);
        var instagram = rede is RedePublicacao.INSTAGRAM_FEED or RedePublicacao.INSTAGRAM_STORY or RedePublicacao.INSTAGRAM_REELS;
        if (instagram ? !meta.InstagramConfigurado : !meta.FacebookConfigurado)
            throw new RegraNegocioException("meta", $"Configure a {(instagram ? "conta do Instagram" : "página do Facebook")} em Administração → Configurações → Meta.");
        if (await db.PostsRedesPublicacoes.AnyAsync(p => p.PostId == postId && p.Rede == rede && p.Status != StatusPublicacaoPost.FALHA, ct))
            throw new RegraNegocioException("rede", "Este post já foi publicado (ou está sendo publicado) neste destino.");

        var midias = await db.PostsRedesMidias.AsNoTracking().Where(m => m.PostId == postId && m.Status == StatusMidiaPost.CONCLUIDA).ToListAsync(ct);
        var urls = (rede switch
        {
            RedePublicacao.INSTAGRAM_FEED or RedePublicacao.FACEBOOK_FEED => midias.Where(m => m.Tipo == TipoMidiaPost.FEED).OrderBy(m => m.Ordem),
            RedePublicacao.INSTAGRAM_STORY => midias.Where(m => m.Tipo == TipoMidiaPost.STORY).OrderBy(m => m.Ordem).Take(1),
            _ => midias.Where(m => m.Tipo == TipoMidiaPost.VIDEO),
        }).Select(m => publico.Url(m.Chave)!).Where(x => x is not null).ToList();
        if (urls.Count == 0) throw new RegraNegocioException("midias", "As peças deste destino ainda não estão prontas.");

        var textos = TextosPost.De(post.Textos);
        var texto = instagram ? textos.Instagram : textos.Facebook;
        var pub = new PostRedesPublicacao { PostId = postId, Rede = rede, PublicadoPor = u.Id };
        db.PostsRedesPublicacoes.Add(pub);
        try
        {
            switch (rede)
            {
                case RedePublicacao.FACEBOOK_FEED:
                    Concluir(pub, await meta.PublicarFacebookFotosAsync(urls, texto, ct));
                    break;
                case RedePublicacao.FACEBOOK_VIDEO:
                    Concluir(pub, await meta.PublicarFacebookVideoAsync(urls[0], texto, ct));
                    break;
                default:
                    pub.Container = await meta.CriarContainerInstagramAsync(rede, urls, texto, ct);
                    if (rede != RedePublicacao.INSTAGRAM_REELS && await meta.TentarPublicarInstagramAsync(pub.Container, ct) is { } feito)
                        Concluir(pub, feito);
                    break;
            }
        }
        catch (MetaRecusouException e)
        {
            pub.Status = StatusPublicacaoPost.FALHA;
            pub.Erro = e.Message;
        }
        await db.SaveChangesAsync(ct);
        return pub;
    }

    private static void Concluir(PostRedesPublicacao pub, PostPublicado r)
    {
        pub.Status = StatusPublicacaoPost.PUBLICADA;
        pub.IdExterno = r.Id;
        pub.Link = r.Link;
        pub.PublicadoEm = DateTime.UtcNow;
        pub.Erro = null;
    }

    /* ------------------------------ rotina ------------------------------ */

    /// <summary>
    /// Uma rodada: avança as peças (cria e acompanha as tarefas do Magnific, compõe as imagens, monta o vídeo quando os
    /// clipes ficam prontos) e publica os contêineres do Instagram já processados. Devolve quantas peças concluiu.
    /// </summary>
    public async Task<int> ProcessarAsync(CancellationToken ct = default, Guid? soPost = null)
    {
        if (!publico.Habilitado) return 0;
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var consulta = db.PostsRedesMidias.Where(m => m.Status == StatusMidiaPost.PENDENTE || m.Status == StatusMidiaPost.PROCESSANDO);
        if (soPost is { } id) consulta = consulta.Where(m => m.PostId == id);
        var lote = await consulta.OrderBy(m => m.CriadoEm).ThenBy(m => m.Tipo).ThenBy(m => m.Ordem).Take(PorRodada * 2).ToListAsync(ct);
        // o vídeo por último: depende dos clipes e da música da mesma rodada
        lote = [.. lote.Where(m => m.Tipo != TipoMidiaPost.VIDEO).Take(PorRodada), .. lote.Where(m => m.Tipo == TipoMidiaPost.VIDEO)];

        var contexto = new Dictionary<Guid, (PostRedes Post, Imovel Imovel)>();
        var concluidas = 0;
        foreach (var m in lote)
        {
            if (!contexto.TryGetValue(m.PostId, out var ctx))
            {
                var post = await db.PostsRedes.AsNoTracking().FirstAsync(p => p.Id == m.PostId, ct);
                var imovel = await db.Imoveis.AsNoTracking().FirstAsync(i => i.Id == post.ImovelId, ct);
                contexto[m.PostId] = ctx = (post, imovel);
            }
            try
            {
                await Avancar(db, m, ctx.Post, ctx.Imovel, ct);
            }
            catch (Exception e) when (e is MagnificRecusouException or ArgumentException or InvalidOperationException or TimeoutException or NaoEncontradoException)
            {
                log.LogWarning("Peça {Midia} do post {Post} falhou: {Erro}", m.Id, m.PostId, e.Message);
                m.Status = StatusMidiaPost.FALHA;
                m.Erro = e.Message.Length > 500 ? e.Message[..500] : e.Message;
            }
            catch (Exception e) when (e is HttpRequestException or Amazon.S3.AmazonS3Exception or Amazon.Runtime.AmazonServiceException
                                          or Google.GoogleApiException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                log.LogWarning("Peça {Midia} adiada: {Erro}", m.Id, e.Message); // falha temporária: tenta na próxima rodada
                continue;
            }
            m.AtualizadoEm = DateTime.UtcNow;
            if (m.Status == StatusMidiaPost.CONCLUIDA) concluidas++;
            await db.SaveChangesAsync(ct);
        }

        await AtualizarPublicacoesAsync(db, soPost, ct);
        return concluidas;
    }

    private async Task Avancar(InovabensDb db, PostRedesMidia m, PostRedes post, Imovel imovel, CancellationToken ct)
    {
        switch (m.Tipo)
        {
            case TipoMidiaPost.FEED or TipoMidiaPost.STORY:
                await AvancarImagem(db, m, post, imovel, ct);
                break;
            case TipoMidiaPost.CLIPE:
                await AvancarClipe(db, m, post, ct);
                break;
            case TipoMidiaPost.MUSICA:
                await AvancarMusica(m, post, ct);
                break;
            case TipoMidiaPost.VIDEO:
                await AvancarVideo(db, m, post, imovel, ct);
                break;
        }
    }

    private async Task AvancarImagem(InovabensDb db, PostRedesMidia m, PostRedes post, Imovel imovel, CancellationToken ct)
    {
        var (w, h) = ImagemPost.Dimensoes(m.Tipo);
        var capa = m.Capa ? InfoCapa(imovel, post.Finalidade) : null;
        if (m.Tarefa is null)
        {
            var foto = await FotoOrigem(db, m, ct);
            if (post.ExpandirComIa && magnific.Configurado && ImagemPost.PrepararExpansao(foto, w, h) is { } pedido)
            {
                await IniciarExpansao(m, pedido, ct);
                return;
            }
            await Gravar(m, post, ImagemPost.Finalizar(foto, w, h, capa, jaExpandida: false), "image/jpeg", ".jpg", ct);
            return;
        }

        var tarefa = await magnific.ConsultarTarefaAsync(ServicoMagnific.Expandir, m.Tarefa, ct);
        if (tarefa.Situacao == SituacaoTarefaMagnific.EmAndamento) return;
        byte[] imagem;
        bool expandida;
        if (tarefa is { Situacao: SituacaoTarefaMagnific.Concluida, UrlResultado: { } url })
        {
            imagem = await magnific.BaixarResultadoAsync(url, ct);
            expandida = true;
        }
        else
        {
            // a expansão falhou: segue com o fundo desfocado em vez de perder a peça
            log.LogWarning("Expansão da peça {Midia} falhou no Magnific; usando fundo desfocado", m.Id);
            imagem = await FotoOrigem(db, m, ct);
            expandida = false;
        }
        await Gravar(m, post, ImagemPost.Finalizar(imagem, w, h, capa, expandida), "image/jpeg", ".jpg", ct);
    }

    private async Task AvancarClipe(InovabensDb db, PostRedesMidia m, PostRedes post, CancellationToken ct)
    {
        if (m.Tarefa is null)
        {
            var foto = await FotoOrigem(db, m, ct);
            if (post.ExpandirComIa && ImagemPost.PrepararExpansao(foto, 720, 1280) is { } pedido)
                await IniciarExpansao(m, pedido, ct);
            else
                await IniciarAnimacao(m, post, ImagemPost.BaseClipe(foto), ct);
            return;
        }

        if (m.Etapa == EtapaMidiaPost.EXPANDIR)
        {
            var exp = await magnific.ConsultarTarefaAsync(ServicoMagnific.Expandir, m.Tarefa, ct);
            if (exp.Situacao == SituacaoTarefaMagnific.EmAndamento) return;
            var base9x16 = exp is { Situacao: SituacaoTarefaMagnific.Concluida, UrlResultado: { } u }
                ? ImagemPost.AjustarClipe(await magnific.BaixarResultadoAsync(u, ct))
                : ImagemPost.BaseClipe(await FotoOrigem(db, m, ct));
            await IniciarAnimacao(m, post, base9x16, ct);
            return;
        }

        var tarefa = await magnific.ConsultarTarefaAsync(ServicoMagnific.Animar, m.Tarefa, ct);
        if (tarefa.Situacao == SituacaoTarefaMagnific.EmAndamento) return;
        if (tarefa is not { Situacao: SituacaoTarefaMagnific.Concluida, UrlResultado: { } url })
            throw new MagnificRecusouException("O Magnific não conseguiu animar esta foto.");
        await Gravar(m, post, await magnific.BaixarResultadoAsync(url, ct), "video/mp4", ".mp4", ct);
    }

    private async Task AvancarMusica(PostRedesMidia m, PostRedes post, CancellationToken ct)
    {
        if (m.Tarefa is null)
        {
            var textos = TextosPost.De(post.Textos);
            var prompt = string.IsNullOrWhiteSpace(textos.Musica) || textos.Musica.Trim().Length < 10
                ? (post.Finalidade == FinalidadeImovel.LOCACAO
                    ? "Light modern acoustic pop, cheerful and relaxed, soft percussion, 30 seconds, instrumental, no vocals"
                    : "Warm cinematic corporate track, elegant piano and soft strings, uplifting, 30 seconds, instrumental, no vocals")
                : textos.Musica.Trim();
            var t = await magnific.CriarTarefaAsync(ServicoMagnific.Musica, new { prompt = prompt.Length > 2000 ? prompt[..2000] : prompt, model = "clip" }, ct);
            m.Tarefa = t.Id;
            m.Etapa = EtapaMidiaPost.MUSICA;
            m.Status = StatusMidiaPost.PROCESSANDO;
            return;
        }
        var tarefa = await magnific.ConsultarTarefaAsync(ServicoMagnific.Musica, m.Tarefa, ct);
        if (tarefa.Situacao == SituacaoTarefaMagnific.EmAndamento) return;
        if (tarefa is not { Situacao: SituacaoTarefaMagnific.Concluida, UrlResultado: { } url })
            throw new MagnificRecusouException("O Magnific não conseguiu gerar a música.");
        var audio = await magnific.BaixarResultadoAsync(url, ct);
        var (tipo, ext) = TipoAudio(audio);
        await Gravar(m, post, audio, tipo, ext, ct);
    }

    private async Task AvancarVideo(InovabensDb db, PostRedesMidia m, PostRedes post, Imovel imovel, CancellationToken ct)
    {
        var pecas = await db.PostsRedesMidias.AsNoTracking()
            .Where(x => x.PostId == post.Id && (x.Tipo == TipoMidiaPost.CLIPE || x.Tipo == TipoMidiaPost.MUSICA)).ToListAsync(ct);
        if (pecas.Any(x => x.Status is StatusMidiaPost.PENDENTE or StatusMidiaPost.PROCESSANDO)) return; // espera clipes e música
        if (!montagem.Disponivel) throw new InvalidOperationException("FFmpeg não instalado no servidor: o vídeo não pode ser montado.");
        var clipes = pecas.Where(x => x.Tipo == TipoMidiaPost.CLIPE && x.Status == StatusMidiaPost.CONCLUIDA && x.Chave != null).OrderBy(x => x.Ordem).ToList();
        if (clipes.Count == 0) throw new InvalidOperationException("Nenhum clipe foi gerado; use \"Tentar de novo\" nas peças que falharam.");

        m.Status = StatusMidiaPost.PROCESSANDO;
        var textos = TextosPost.De(post.Textos);
        var trechos = new List<TrechoVideo>();
        foreach (var c in clipes)
        {
            var dados = await publico.BaixarAsync(c.Chave!, ct) ?? throw new NaoEncontradoException("Clipe não encontrado no armazenamento.");
            var texto = c.Ordem < textos.Cenas.Count ? textos.Cenas[c.Ordem].TextoTela : "";
            trechos.Add(new TrechoVideo(dados, ImagemPost.Sobreposicao(texto)));
        }
        var musica = pecas.FirstOrDefault(x => x.Tipo == TipoMidiaPost.MUSICA && x.Status == StatusMidiaPost.CONCLUIDA && x.Chave != null) is { } mu
            ? await publico.BaixarAsync(mu.Chave!, ct)
            : null;
        var cartao = ImagemPost.CartaoFinal(InfoCapa(imovel, post.Finalidade), textos.TextoFinal);
        var video = await montagem.MontarAsync(trechos, cartao, musica, ct);
        var anterior = m.Chave;
        // endereço novo a cada montagem (o R2 serve com cache "immutable")
        await Gravar(m, post, video, "video/mp4", $"-{DateTime.UtcNow:yyyyMMddHHmmss}.mp4", ct);
        if (anterior is not null && anterior != m.Chave) await publico.RemoverAsync(anterior);
    }

    private async Task AtualizarPublicacoesAsync(InovabensDb db, Guid? soPost, CancellationToken ct)
    {
        if (!meta.InstagramConfigurado) return;
        var consulta = db.PostsRedesPublicacoes.Where(p => p.Status == StatusPublicacaoPost.AGUARDANDO && p.Container != null);
        if (soPost is { } id) consulta = consulta.Where(p => p.PostId == id);
        foreach (var pub in await consulta.Take(PorRodada).ToListAsync(ct))
        {
            try
            {
                if (await meta.TentarPublicarInstagramAsync(pub.Container!, ct) is { } feito) Concluir(pub, feito);
                else if (DateTime.UtcNow - pub.CriadoEm > PrazoInstagram)
                {
                    pub.Status = StatusPublicacaoPost.FALHA;
                    pub.Erro = "O Instagram não terminou de processar a mídia a tempo.";
                }
            }
            catch (MetaRecusouException e)
            {
                pub.Status = StatusPublicacaoPost.FALHA;
                pub.Erro = e.Message;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                log.LogWarning("Publicação {Pub} adiada: {Erro}", pub.Id, e.Message);
                continue;
            }
            await db.SaveChangesAsync(ct);
        }
    }

    /* ------------------------------ auxiliares ------------------------------ */

    private async Task IniciarExpansao(PostRedesMidia m, PedidoExpansao p, CancellationToken ct)
    {
        var t = await magnific.CriarTarefaAsync(ServicoMagnific.Expandir, new
        {
            image = Convert.ToBase64String(p.Jpeg),
            prompt = PromptExpansao,
            left = p.Esquerda, right = p.Direita, top = p.Cima, bottom = p.Baixo,
        }, ct);
        m.Tarefa = t.Id;
        m.Etapa = EtapaMidiaPost.EXPANDIR;
        m.Status = StatusMidiaPost.PROCESSANDO;
    }

    private async Task IniciarAnimacao(PostRedesMidia m, PostRedes post, byte[] base9x16, CancellationToken ct)
    {
        var textos = TextosPost.De(post.Textos);
        var movimento = m.Ordem < textos.Cenas.Count && !string.IsNullOrWhiteSpace(textos.Cenas[m.Ordem].Movimento)
            ? textos.Cenas[m.Ordem].Movimento.Trim()
            : "slow cinematic dolly in, steady camera";
        var t = await magnific.CriarTarefaAsync(ServicoMagnific.Animar, new
        {
            image = Convert.ToBase64String(base9x16),
            prompt = $"{movimento}. Real estate video tour, smooth and stable camera, realistic, natural light, architecture unchanged.",
            negative_prompt = NegativoClipe,
            duration = "5",
            cfg_scale = 0.5,
            generate_audio = false,
        }, ct);
        m.Tarefa = t.Id;
        m.Etapa = EtapaMidiaPost.ANIMAR;
        m.Status = StatusMidiaPost.PROCESSANDO;
    }

    private async Task Gravar(PostRedesMidia m, PostRedes post, byte[] dados, string tipo, string sufixo, CancellationToken ct)
    {
        var chave = $"posts/{post.ImovelId:N}/{post.Id:N}/{m.Tipo.ToString().ToLowerInvariant()}-{m.Ordem + 1}-{m.Id.ToString("N")[..8]}{sufixo}";
        await publico.EnviarAsync(chave, dados, tipo, ct);
        if (m.Chave is { } antiga && antiga != chave && m.Tipo != TipoMidiaPost.VIDEO) await publico.RemoverAsync(antiga);
        m.Chave = chave;
        m.ContentType = tipo;
        m.Status = StatusMidiaPost.CONCLUIDA;
        m.Tarefa = null;
        m.Etapa = null;
        m.Erro = null;
    }

    private async Task<byte[]> FotoOrigem(InovabensDb db, PostRedesMidia m, CancellationToken ct)
    {
        var origem = m.MidiaOrigemId is { } id ? await db.ImovelMidias.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) : null;
        if (origem is null) throw new NaoEncontradoException("A foto usada nesta peça foi excluída do imóvel.");
        var arquivo = await drive.BaixarAsync(origem.ArquivoChave, ct) ?? throw new NaoEncontradoException("A foto não foi encontrada no Google Drive.");
        return arquivo.Dados;
    }

    private async Task<PostRedes> PostComAcesso(InovabensDb db, UsuarioAtual u, Guid postId)
    {
        var post = await db.PostsRedes.FirstOrDefaultAsync(p => p.Id == postId) ?? throw new NaoEncontradoException("Post não encontrado.");
        await imoveis.DetalheAsync(u, post.ImovelId); // confere acesso (corretor só vê os próprios imóveis)
        return post;
    }

    /// <summary>Completa o roteiro quando a IA devolveu menos cenas que o pedido (fotos ainda não usadas, na ordem).</summary>
    internal static void CompletarCenas(TextosPost t, int cenas, int fotos)
    {
        var usadas = t.Cenas.Select(c => c.Foto).ToHashSet();
        for (var f = 1; t.Cenas.Count < cenas && f <= fotos; f++)
            if (usadas.Add(f)) t.Cenas.Add(new CenaVideo { Foto = f, Movimento = "slow cinematic dolly in, steady camera" });
        while (t.Cenas.Count < cenas) t.Cenas.Add(new CenaVideo { Foto = t.Cenas.Count % fotos + 1, Movimento = "slow pan to the right" });
    }

    private DadosPost Dados(ImovelDetalhe d, FinalidadeImovel finalidade, IReadOnlyList<byte[]> fotos)
    {
        var i = d.Dados;
        var caracteristicas = d.Caracteristicas.Where(c => c.Publico)
            .Select(c => string.IsNullOrWhiteSpace(c.Valor) || c.Valor.Equals("sim", StringComparison.OrdinalIgnoreCase) ? c.Chave : $"{c.Chave}: {c.Valor}")
            .Take(40).ToList();
        return new DadosPost(finalidade, i.Tipo, i.Subtipo, i.Titulo, i.DescricaoPublica, i.Bairro, i.Cidade, i.Uf,
            i.Quartos, i.Suites, i.Banheiros, i.Vagas, i.AreaUtilM2 ?? i.AreaConstruidaM2 ?? i.AreaTotalM2,
            finalidade == FinalidadeImovel.LOCACAO ? i.PrecoLocacao : i.PrecoVenda,
            finalidade == FinalidadeImovel.LOCACAO ? i.ValorCondominio : null,
            finalidade == FinalidadeImovel.LOCACAO ? i.ValorIptu : null,
            i.AceitaPermuta, caracteristicas, i.Referencia ?? i.Codigo, LinkAnuncio(i),
            FormatarTelefone(op.Integracoes["site.whatsapp"]), Texto.Limpo(op.Integracoes["site.creci"]), fotos);
    }

    private string? LinkAnuncio(Imovel i) =>
        i.ImobibrasilId is { } id ? $"{op.Integracoes.ImobibrasilSite}/imovel/{id}" : op.Integracoes["site.url"];

    internal InfoCapa InfoCapa(Imovel i, FinalidadeImovel finalidade)
    {
        var locacao = finalidade == FinalidadeImovel.LOCACAO;
        var preco = locacao ? i.PrecoLocacao : i.PrecoVenda;
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        var detalhes = new List<string>();
        if (i.Quartos is > 0) detalhes.Add($"{i.Quartos} quarto{(i.Quartos > 1 ? "s" : "")}");
        if (i.Suites is > 0) detalhes.Add($"{i.Suites} suíte{(i.Suites > 1 ? "s" : "")}");
        if (i.Vagas is > 0) detalhes.Add($"{i.Vagas} vaga{(i.Vagas > 1 ? "s" : "")}");
        if ((i.AreaUtilM2 ?? i.AreaConstruidaM2 ?? i.AreaTotalM2) is > 0 and var area) detalhes.Add($"{area.ToString("0.#", pt)} m²");
        if (detalhes.Count == 0) detalhes.Add(pt.TextInfo.ToTitleCase(i.Tipo));
        var local = string.Join(" · ", new[] { i.Bairro, i.Cidade is { } c ? c + (i.Uf is { } uf ? "/" + uf : "") : null }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return new InfoCapa(
            locacao ? "PARA ALUGAR" : "À VENDA",
            preco is > 0 ? Texto.Moeda(preco) + (locacao ? "/mês" : "") : "Valor sob consulta",
            string.Join(" · ", detalhes), local, i.Referencia ?? i.Codigo,
            FormatarTelefone(op.Integracoes["site.whatsapp"]), Texto.Limpo(op.Integracoes["site.creci"]));
    }

    /// <summary>(17) 99999-0000 a partir dos dígitos; null se vazio.</summary>
    public static string? FormatarTelefone(string? telefone)
    {
        var d = Texto.Digitos(telefone);
        if (d.StartsWith("55") && d.Length is 12 or 13) d = d[2..];
        return d.Length switch
        {
            11 => $"({d[..2]}) {d[2..7]}-{d[7..]}",
            10 => $"({d[..2]}) {d[2..6]}-{d[6..]}",
            0 => null,
            _ => d,
        };
    }

    internal static (string Tipo, string Ext) TipoAudio(byte[] a) =>
        a.Length > 12 && a[0] == 'R' && a[1] == 'I' && a[2] == 'F' && a[3] == 'F' ? ("audio/wav", ".wav")
        : a.Length > 8 && a[4] == 'f' && a[5] == 't' && a[6] == 'y' && a[7] == 'p' ? ("audio/mp4", ".m4a")
        : a.Length > 4 && a[0] == 'O' && a[1] == 'g' && a[2] == 'g' ? ("audio/ogg", ".ogg")
        : ("audio/mpeg", ".mp3");
}
