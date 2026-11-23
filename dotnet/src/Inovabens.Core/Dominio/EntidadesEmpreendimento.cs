using System.ComponentModel.DataAnnotations.Schema;

namespace Inovabens.Core.Dominio;

/// <summary>Tipos de unidade de um empreendimento (texto em <see cref="Imovel.UnidadeTipo"/>).</summary>
public static class TipoUnidade
{
    public const string Lote = "LOTE", Remanescente = "REMANESCENTE";
}

/// <summary>Andamento do desmembramento, da prancheta ao registro no cartório.</summary>
public enum SituacaoDesmembramento { PROJETO, APROVADO_PREFEITURA, PROTOCOLADO, REGISTRADO }

/// <summary>
/// Uma fase do empreendimento: a matrícula <see cref="OrigemId"/> (o empreendimento ou uma área remanescente) dividida
/// em lotes e, se houver, numa nova área remanescente.
/// </summary>
[Table("desmembramentos")]
public class Desmembramento
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmpreendimentoId { get; set; }
    public Guid OrigemId { get; set; }
    public int Fase { get; set; }
    public SituacaoDesmembramento Situacao { get; set; } = SituacaoDesmembramento.PROJETO;
    public string? Protocolo { get; set; }
    public DateOnly? DataRegistro { get; set; }
    public decimal? AreaDoadaM2 { get; set; }
    public string? Observacao { get; set; }
    public Guid? CriadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public Guid? AtualizadoPor { get; set; }
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public Desmembramento Copia() => (Desmembramento)MemberwiseClone();
}
