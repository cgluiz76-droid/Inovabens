using System.ComponentModel.DataAnnotations.Schema;

namespace Inovabens.Core.Dominio;

/// <summary>Página exclusiva do imóvel (landing page) em /imovel/{Slug}: pública sem login quando publicada.</summary>
[Table("paginas_imovel")]
public class PaginaImovel
{
    public Guid Id { get; set; }
    public Guid ImovelId { get; set; }
    /// <summary>Endereço amigável (letras minúsculas sem acento, números e hífen), único.</summary>
    public string Slug { get; set; } = "";
    public bool Publicada { get; set; }
    /// <summary>Título principal da página (h1).</summary>
    public string Titulo { get; set; } = "";
    /// <summary>Frase logo abaixo do título.</summary>
    public string? Chamada { get; set; }
    /// <summary>Título na aba e no Google (até ~60 caracteres); vazio = o título.</summary>
    public string? TituloSeo { get; set; }
    /// <summary>Resumo no Google e na prévia do WhatsApp (até ~155 caracteres).</summary>
    public string? DescricaoSeo { get; set; }
    public Guid? FotoCapaId { get; set; }
    /// <summary>Blocos da página (<c>ConteudoPagina</c> em JSON).</summary>
    public string Conteudo { get; set; } = "{}";
    public DateTime? PublicadaEm { get; set; }
    public Guid? CriadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public Guid? AtualizadoPor { get; set; }
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public PaginaImovel Copia() => (PaginaImovel)MemberwiseClone();
}

/// <summary>Visita ou clique no WhatsApp da página do imóvel (só contagem, sem dados do visitante).</summary>
[Table("paginas_imovel_eventos")]
public class PaginaImovelEvento
{
    public const string Visita = "VISITA", Whatsapp = "WHATSAPP";

    public long Id { get; set; }
    public Guid PaginaId { get; set; }
    public string Tipo { get; set; } = Visita;
    /// <summary>link, whatsapp, email, qrcode, instagram, facebook, google, outro ou direto.</summary>
    public string Origem { get; set; } = "direto";
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}
