using System.ComponentModel.DataAnnotations.Schema;

namespace Inovabens.Core.Dominio;

/* Locação, fase 8 (migração 0054): temporada por diária (Lei 8.245, arts. 48 a 50). */

public enum OrigemReserva { DIRETA, SITE, AIRBNB, BOOKING, OUTRA }

/// <summary>PEDIDO (site ou equipe, ainda sem confirmar) não bloqueia datas; as demais, exceto CANCELADA, bloqueiam.</summary>
public enum StatusReserva { PEDIDO, CONFIRMADA, EM_CURSO, FINALIZADA, FECHADA, CANCELADA }

public enum OrigemCalendario { AIRBNB, BOOKING, OUTRA }

[Table("temporada_imoveis")]
public class TemporadaImovel
{
    [System.ComponentModel.DataAnnotations.Key] public Guid ImovelId { get; set; }
    public bool Ativo { get; set; } = true;
    public ModalidadeLocacao Modalidade { get; set; } = ModalidadeLocacao.ADMINISTRACAO;
    public decimal Diaria { get; set; }
    public decimal? DiariaFimSemana { get; set; }
    public short MinimoNoites { get; set; } = 2;
    public short HospedesMax { get; set; } = 4;
    public decimal TaxaLimpeza { get; set; }
    public decimal Caucao { get; set; }
    public decimal ComissaoPct { get; set; } = 20;
    public string Checkin { get; set; } = "14:00";
    public string Checkout { get; set; } = "11:00";
    public string? Regras { get; set; }
    public Guid? FaxineiraId { get; set; }
    public string IcalToken { get; set; } = "";
    public Guid? AtualizadoPor { get; set; }
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public Imovel? Imovel { get; set; }
}

[Table("temporada_calendarios")]
public class TemporadaCalendario
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImovelId { get; set; }
    public OrigemCalendario Origem { get; set; }
    public string Url { get; set; } = "";
    public DateTime? UltimaSincronizacao { get; set; }
    public string? Erro { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}

[Table("temporada_reservas")]
public class TemporadaReserva
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Numero { get; set; }
    public Guid ImovelId { get; set; }
    public OrigemReserva Origem { get; set; } = OrigemReserva.DIRETA;
    public StatusReserva Status { get; set; } = StatusReserva.CONFIRMADA;
    public Guid? HospedeId { get; set; }
    public string HospedeNome { get; set; } = "";
    public string? HospedeTelefone { get; set; }
    public string? HospedeEmail { get; set; }
    public string? HospedeDocumento { get; set; }
    public short Hospedes { get; set; } = 1;
    public DateOnly Entrada { get; set; }
    public DateOnly Saida { get; set; }
    public decimal Diarias { get; set; }
    public decimal Desconto { get; set; }
    public decimal Limpeza { get; set; }
    public decimal Caucao { get; set; }
    public decimal TaxaPlataforma { get; set; }
    public decimal ComissaoPct { get; set; }
    public decimal CaucaoRetida { get; set; }
    public string? Observacoes { get; set; }
    public Guid? CalendarioId { get; set; }
    public string? ExternoUid { get; set; }
    public DateTime? CheckinEm { get; set; }
    public DateTime? CheckoutEm { get; set; }
    public DateTime? FechadaEm { get; set; }
    public List<Guid> LancamentosEmpresaIds { get; set; } = [];
    public DateTime? CanceladaEm { get; set; }
    public string? MotivoCancelamento { get; set; }
    public Guid? CriadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    [NotMapped] public string Codigo => CodigoDe(Numero);
    public static string CodigoDe(int numero) => $"RES-{numero:0000}";
    [NotMapped] public int Noites => Saida.DayNumber - Entrada.DayNumber;
    /// <summary>O que o hóspede paga: diárias com desconto, limpeza e caução (a caução volta no check-out).</summary>
    [NotMapped] public decimal Total => Diarias - Desconto + Limpeza + Caucao;
    [NotMapped] public bool Bloqueia => Status is not (StatusReserva.PEDIDO or StatusReserva.CANCELADA);

    public TemporadaReserva Copia() => (TemporadaReserva)MemberwiseClone();
}

[Table("temporada_pagamentos")]
public class TemporadaPagamento
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReservaId { get; set; }
    public DateOnly Data { get; set; }
    public decimal Valor { get; set; }
    public string? Forma { get; set; }
    public Guid? ContaBancariaId { get; set; }
    public string? Observacao { get; set; }
    public Guid? LancamentoEmpresaId { get; set; }
    public Guid? LancamentoImovelId { get; set; }
    public Guid? RegistradoPor { get; set; }
    public DateTime RegistradoEm { get; set; } = DateTime.UtcNow;
    public DateTime? EstornadoEm { get; set; }
}
