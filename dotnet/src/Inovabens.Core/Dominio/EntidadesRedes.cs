using System.ComponentModel.DataAnnotations.Schema;

namespace Inovabens.Core.Dominio;

/* ------------------------------ Posts para redes sociais ------------------------------ */

/// <summary>Peça gerada para um post: imagem do feed (4:5), story (9:16), clipe animado, música ou o vídeo montado.</summary>
public enum TipoMidiaPost { FEED, STORY, CLIPE, MUSICA, VIDEO }

public enum StatusMidiaPost { PENDENTE, PROCESSANDO, CONCLUIDA, FALHA }

/// <summary>Etapa do Magnific em andamento: expandir as bordas da foto, animar a foto (Kling) ou gerar a música (Lyria).</summary>
public enum EtapaMidiaPost { EXPANDIR, ANIMAR, MUSICA }

/// <summary>Destinos com publicação automática pela Graph API da Meta (as demais redes são publicadas à mão).</summary>
public enum RedePublicacao { INSTAGRAM_FEED, INSTAGRAM_STORY, INSTAGRAM_REELS, FACEBOOK_FEED, FACEBOOK_VIDEO }

public enum StatusPublicacaoPost { AGUARDANDO, PUBLICADA, FALHA }

/// <summary>Post de um imóvel para as redes sociais: textos por rede (jsonb) e as peças geradas.</summary>
[Table("posts_redes")]
public class PostRedes
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImovelId { get; set; }
    /// <summary>VENDA ou LOCACAO (foco dos textos).</summary>
    public FinalidadeImovel Finalidade { get; set; } = FinalidadeImovel.VENDA;
    /// <summary>Textos de cada rede e roteiro do vídeo (<c>TextosPost</c> em JSON).</summary>
    public string Textos { get; set; } = "{}";
    /// <summary>Completa as bordas das fotos com IA (Magnific) em vez do fundo desfocado.</summary>
    public bool ExpandirComIa { get; set; }
    public bool ComVideo { get; set; }
    public Guid? CriadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
}

[Table("posts_redes_midias")]
public class PostRedesMidia
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PostId { get; set; }
    public TipoMidiaPost Tipo { get; set; }
    public StatusMidiaPost Status { get; set; } = StatusMidiaPost.PENDENTE;
    public EtapaMidiaPost? Etapa { get; set; }
    /// <summary>task_id do Magnific da etapa em andamento.</summary>
    public string? Tarefa { get; set; }
    /// <summary>Foto do imóvel usada (feed, story e clipe).</summary>
    public Guid? MidiaOrigemId { get; set; }
    public int Ordem { get; set; }
    /// <summary>Recebe a faixa com finalidade, preço e características (primeira imagem do carrossel e o story de capa).</summary>
    public bool Capa { get; set; }
    /// <summary>Chave do arquivo gerado no R2 (posts/...).</summary>
    public string? Chave { get; set; }
    public string? ContentType { get; set; }
    public string? Erro { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
}

[Table("posts_redes_publicacoes")]
public class PostRedesPublicacao
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PostId { get; set; }
    public RedePublicacao Rede { get; set; }
    public StatusPublicacaoPost Status { get; set; } = StatusPublicacaoPost.AGUARDANDO;
    /// <summary>Contêiner do Instagram aguardando processamento (vídeos e imagens), publicado quando ficar pronto.</summary>
    public string? Container { get; set; }
    public string? IdExterno { get; set; }
    public string? Link { get; set; }
    public string? Erro { get; set; }
    public Guid? PublicadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? PublicadoEm { get; set; }
}
