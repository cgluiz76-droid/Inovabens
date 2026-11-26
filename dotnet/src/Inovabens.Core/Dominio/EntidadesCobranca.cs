using System.ComponentModel.DataAnnotations.Schema;

namespace Inovabens.Core.Dominio;

/* Locação, fase 7: boleto/Pix no banco (Cora) e portal do inquilino e do proprietário. */

[Table("fatura_boletos")]
public class FaturaBoleto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Fatura do aluguel ou, na temporada, a reserva (um dos dois).</summary>
    public Guid? FaturaId { get; set; }
    public Guid? ReservaId { get; set; }
    public Guid? TemporadaPagamentoId { get; set; }
    public string Provedor { get; set; } = "CORA";
    public string ExternoId { get; set; } = "";
    public Guid Idempotencia { get; set; }
    public string Status { get; set; } = "OPEN";
    public decimal Valor { get; set; }
    public DateOnly Vencimento { get; set; }
    public string? LinhaDigitavel { get; set; }
    public string? CodigoBarras { get; set; }
    public string? PixCopiaCola { get; set; }
    public string? UrlPdf { get; set; }
    public DateOnly? PagoEm { get; set; }
    public decimal? ValorPago { get; set; }
    public string? FormaPagamento { get; set; }
    public Guid? PagamentoId { get; set; }
    public string? Erro { get; set; }
    public Guid? CriadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? CanceladoEm { get; set; }

    /// <summary>Boleto que ainda pode ser pago (não pago nem cancelado).</summary>
    [NotMapped] public bool Ativo => CanceladoEm is null && Status is not ("PAID" or "CANCELLED");
}

public enum TipoPortal { INQUILINO, PROPRIETARIO }

[Table("portal_acessos")]
public class PortalAcesso
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Token { get; set; } = "";
    public TipoPortal Tipo { get; set; }
    public Guid? ContratoId { get; set; }
    public Guid? PessoaId { get; set; }
    public Guid? CriadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? RevogadoEm { get; set; }
    public DateTime? UltimoAcesso { get; set; }
    public int Acessos { get; set; }
}
