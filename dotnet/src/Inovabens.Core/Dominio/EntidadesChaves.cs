using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Inovabens.Core.Dominio;

/// <summary>Chave física de um imóvel (etiqueta com o código). Excluída só logicamente, para manter o histórico.</summary>
[Table("chaves")]
public class Chave
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImovelId { get; set; }
    public string Codigo { get; set; } = "";
    public string Descricao { get; set; } = "";
    public string? Observacao { get; set; }
    public Guid? CriadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? ExcluidoEm { get; set; }
    [JsonIgnore] public Imovel? Imovel { get; set; }

    public Chave Copia() => (Chave)MemberwiseClone();
}

/// <summary>Saída da chave (com quem, motivo e prazo) e, depois, a devolução. Aberto = sem <see cref="DevolvidaEm"/>.</summary>
[Table("chave_movimentos")]
public class ChaveMovimento
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChaveId { get; set; }
    public Guid? UsuarioId { get; set; }
    public Guid? PessoaId { get; set; }
    public string? PessoaNome { get; set; }
    public string? PessoaTelefone { get; set; }
    public string Motivo { get; set; } = "";
    public string? MotivoDetalhe { get; set; }
    public DateTime RetiradaEm { get; set; }
    public DateTime PrevisaoDevolucao { get; set; }
    public DateTime? DevolvidaEm { get; set; }
    public string? ObservacaoDevolucao { get; set; }
    public Guid? RegistradoPor { get; set; }
    public Guid? DevolucaoRegistradaPor { get; set; }
    [JsonIgnore] public Chave? Chave { get; set; }
    [JsonIgnore] public Usuario? Usuario { get; set; }
}
