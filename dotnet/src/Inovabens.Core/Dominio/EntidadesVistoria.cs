using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Inovabens.Core.Dominio;

/* Locação, fase 4: vistoria terceirizada (registro da vistoria feita pela empresa contratada). */

public enum TipoVistoria { ENTRADA, SAIDA, PERIODICA }

public enum StatusVistoria { AGENDADA, REALIZADA, CANCELADA }

/// <summary>ABERTA → CHAMADO (virou conserto) / COBRADA (débito na rescisão) / RESOLVIDA.</summary>
public enum StatusPendenciaVistoria { ABERTA, CHAMADO, COBRADA, RESOLVIDA }

[Table("vistorias")]
public class Vistoria
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Numero { get; set; }
    public Guid ImovelId { get; set; }
    public Guid ContratoId { get; set; }
    public TipoVistoria Tipo { get; set; }
    public StatusVistoria Status { get; set; } = StatusVistoria.AGENDADA;
    public Guid? EmpresaId { get; set; }
    public string? Vistoriador { get; set; }
    public DateTime? AgendadaPara { get; set; }
    public Guid? CompromissoId { get; set; }
    public DateOnly? RealizadaEm { get; set; }
    public string? LeituraAgua { get; set; }
    public string? LeituraLuz { get; set; }
    public string? LeituraGas { get; set; }
    public int? Chaves { get; set; }
    public string? LaudoDriveId { get; set; }
    public string? LaudoNome { get; set; }
    public DateOnly? PrazoContestacao { get; set; }
    public DateOnly? AceiteEm { get; set; }
    public string? Contestacao { get; set; }
    public DateOnly? ContestadaEm { get; set; }
    public decimal? Valor { get; set; }
    public CobrancaManutencao? Cobranca { get; set; }
    public DateOnly? VencimentoPrestador { get; set; }
    public Guid? LancamentoEmpresaId { get; set; }
    public Guid? LancamentoImovelId { get; set; }
    public Guid? FaturaItemId { get; set; }
    public string? Observacao { get; set; }
    public string? Motivo { get; set; }
    public Guid? CriadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public Imovel? Imovel { get; set; }

    [NotMapped] public string Codigo => CodigoDe(Numero);
    public static string CodigoDe(int numero) => $"VIS-{numero:0000}";
}

[Table("vistoria_pendencias")]
public class PendenciaVistoria
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VistoriaId { get; set; }
    public string? Comodo { get; set; }
    public string Descricao { get; set; } = "";
    public ResponsavelManutencao Responsavel { get; set; } = ResponsavelManutencao.LOCATARIO;
    public decimal? Valor { get; set; }
    public StatusPendenciaVistoria Status { get; set; } = StatusPendenciaVistoria.ABERTA;
    public Guid? ChamadoId { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}
