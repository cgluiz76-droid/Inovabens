using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Dominio;

namespace Inovabens.Core.Locacao;

/* Locação, fase 8: temporada por diária. */

public sealed class TemporadaConfigForm : IValidatableObject
{
    public bool Ativo { get; set; } = true;
    public ModalidadeLocacao Modalidade { get; set; } = ModalidadeLocacao.ADMINISTRACAO;

    [Required(ErrorMessage = "Informe a diária.")]
    [Range(typeof(decimal), "1", "999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Diária inválida.")]
    public decimal? Diaria { get; set; }

    [Range(typeof(decimal), "1", "999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Diária inválida.")]
    public decimal? DiariaFimSemana { get; set; }

    [Range(1, 90, ErrorMessage = "Entre 1 e 90 noites.")] public short MinimoNoites { get; set; } = 2;
    [Range(1, 50, ErrorMessage = "Entre 1 e 50 hóspedes.")] public short HospedesMax { get; set; } = 4;
    [Range(typeof(decimal), "0", "999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")] public decimal TaxaLimpeza { get; set; }
    [Range(typeof(decimal), "0", "999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")] public decimal Caucao { get; set; }
    [Range(typeof(decimal), "0", "100", ParseLimitsInInvariantCulture = true, ErrorMessage = "Entre 0 e 100%.")] public decimal ComissaoPct { get; set; } = 20;

    [RegularExpression(@"^([01]\d|2[0-3]):[0-5]\d$", ErrorMessage = "Use HH:MM.")] public string Checkin { get; set; } = "14:00";
    [RegularExpression(@"^([01]\d|2[0-3]):[0-5]\d$", ErrorMessage = "Use HH:MM.")] public string Checkout { get; set; } = "11:00";
    [StringLength(2000, ErrorMessage = "Até 2000 caracteres.")] public string? Regras { get; set; }
    public Guid? FaxineiraId { get; set; }
    /// <summary>Para a busca na tela.</summary>
    public string? FaxineiraNome { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext ctx)
    {
        if (TaxaLimpeza > 0 && FaxineiraId is null && Modalidade == ModalidadeLocacao.ADMINISTRACAO)
            yield return new ValidationResult("Informe quem faz a limpeza (recebe a taxa).", [nameof(FaxineiraId)]);
    }
}

public sealed record CalendarioLinha(Guid Id, OrigemCalendario Origem, string Url, DateTime? UltimaSincronizacao, string? Erro);

public sealed record TemporadaImovelLinha(Guid ImovelId, string Codigo, string Titulo, bool Ativo, ModalidadeLocacao Modalidade, decimal Diaria,
    decimal ComissaoPct, string LinkIcal, IReadOnlyList<CalendarioLinha> Calendarios, int ReservasFuturas);

/// <summary>Preço de uma estadia pelas regras do imóvel.</summary>
public sealed record Cotacao(int Noites, decimal Diarias, decimal Limpeza, decimal Caucao, bool Disponivel, string? Motivo)
{
    public decimal Total => Diarias + Limpeza + Caucao;
}

public sealed class ReservaForm : IValidatableObject
{
    public Guid? ImovelId { get; set; }
    public OrigemReserva Origem { get; set; } = OrigemReserva.DIRETA;
    public Guid? HospedeId { get; set; }

    [Required(ErrorMessage = "Informe o nome do hóspede.")]
    [StringLength(200, ErrorMessage = "Até 200 caracteres.")]
    public string HospedeNome { get; set; } = "";

    [StringLength(40)] public string? HospedeTelefone { get; set; }
    [StringLength(200), EmailAddress(ErrorMessage = "E-mail inválido.")] public string? HospedeEmail { get; set; }
    [StringLength(20)] public string? HospedeDocumento { get; set; }
    [Range(1, 50, ErrorMessage = "Entre 1 e 50.")] public short Hospedes { get; set; } = 2;

    [Required(ErrorMessage = "Informe a entrada.")] public DateOnly? Entrada { get; set; }
    [Required(ErrorMessage = "Informe a saída.")] public DateOnly? Saida { get; set; }

    /// <summary>Vazio: calcula pela diária do imóvel. Reserva de plataforma: valor das diárias que ela informa.</summary>
    [Range(typeof(decimal), "0", "9999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")] public decimal? Diarias { get; set; }
    [Range(typeof(decimal), "0", "9999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")] public decimal Desconto { get; set; }
    [Range(typeof(decimal), "0", "9999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")] public decimal? Limpeza { get; set; }
    [Range(typeof(decimal), "0", "9999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")] public decimal? Caucao { get; set; }
    [Range(typeof(decimal), "0", "9999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")] public decimal TaxaPlataforma { get; set; }
    [Range(typeof(decimal), "0", "100", ParseLimitsInInvariantCulture = true, ErrorMessage = "Entre 0 e 100%.")] public decimal? ComissaoPct { get; set; }
    [StringLength(2000)] public string? Observacoes { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext ctx)
    {
        if (Entrada is { } e && Saida is { } s)
        {
            if (s <= e) yield return new ValidationResult("A saída precisa ser depois da entrada.", [nameof(Saida)]);
            else if (s.DayNumber - e.DayNumber > 90) yield return new ValidationResult("Temporada é de até 90 dias (Lei 8.245, art. 48).", [nameof(Saida)]);
        }
        if (Desconto > 0 && Diarias is { } d && Desconto > d) yield return new ValidationResult("Desconto maior que as diárias.", [nameof(Desconto)]);
    }
}

public sealed class RecebimentoReservaForm
{
    [Required(ErrorMessage = "Informe a data.")] public DateOnly? Data { get; set; }

    [Required(ErrorMessage = "Informe o valor.")]
    [Range(typeof(decimal), "0.01", "9999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor precisa ser maior que zero.")]
    public decimal? Valor { get; set; }

    [StringLength(30)] public string? Forma { get; set; }
    public Guid? ContaBancariaId { get; set; }
    [StringLength(500)] public string? Observacao { get; set; }
}

/// <summary>Pedido de reserva feito pelo visitante do site.</summary>
public sealed class PedidoReservaForm
{
    [Required(ErrorMessage = "Informe seu nome."), StringLength(200)] public string Nome { get; set; } = "";
    [Required(ErrorMessage = "Informe seu telefone."), StringLength(40)] public string Telefone { get; set; } = "";
    [StringLength(200), EmailAddress(ErrorMessage = "E-mail inválido.")] public string? Email { get; set; }
    [Range(1, 50, ErrorMessage = "Hóspedes inválido.")] public short Hospedes { get; set; } = 2;
    [Required(ErrorMessage = "Informe a entrada.")] public DateOnly? Entrada { get; set; }
    [Required(ErrorMessage = "Informe a saída.")] public DateOnly? Saida { get; set; }
    [StringLength(1000)] public string? Mensagem { get; set; }
    /// <summary>Campo escondido contra robôs: preenchido, o pedido é ignorado.</summary>
    public string? Site { get; set; }
}

public sealed record ReservaLinha(Guid Id, string Codigo, Guid ImovelId, string ImovelCodigo, string Hospede, DateOnly Entrada, DateOnly Saida,
    StatusReserva Status, OrigemReserva Origem, decimal AReceber, decimal Recebido)
{
    public decimal Saldo => Math.Max(0, AReceber - Recebido);
}

public sealed record PagamentoReservaLinha(Guid Id, DateOnly Data, decimal Valor, string? Forma, string? Conta, bool Estornado);

public sealed record RepasseTemporada(Guid PessoaId, string Nome, decimal Percentual, decimal Valor);

/// <summary>
/// Acerto da reserva: base = diárias − desconto; comissão sobre a base; repasse = base − comissão − taxa da plataforma
/// + caução retida (danos ficam com o proprietário); limpeza vai para a faxineira; o resto da caução volta ao hóspede.
/// </summary>
public sealed record AcertoReserva(decimal Base, decimal Comissao, decimal TaxaPlataforma, decimal Limpeza, decimal CaucaoRetida,
    decimal CaucaoDevolver, IReadOnlyList<RepasseTemporada> Repasses)
{
    public decimal Repasse => Repasses.Sum(r => r.Valor);
}

public sealed record ReservaDetalhe(TemporadaReserva Reserva, string ImovelCodigo, string ImovelTitulo, string Endereco, TemporadaImovel Config,
    IReadOnlyList<PagamentoReservaLinha> Pagamentos, AcertoReserva Acerto, string? Faxineira)
{
    /// <summary>Reserva de plataforma: a plataforma repassa já sem a taxa dela.</summary>
    public decimal AReceber => Reserva.Total - Reserva.TaxaPlataforma;
    public decimal Recebido => Pagamentos.Where(p => !p.Estornado).Sum(p => p.Valor);
    public decimal Saldo => Math.Max(0, AReceber - Recebido);
}

/// <summary>O que o site mostra: preço, regras e os dias ocupados dos próximos meses (sem nomes).</summary>
public sealed record TemporadaPublica(Guid ImovelId, string Codigo, string Titulo, string? Cidade, decimal Diaria, decimal? DiariaFimSemana,
    short MinimoNoites, short HospedesMax, decimal TaxaLimpeza, decimal Caucao, string Checkin, string Checkout, string? Regras,
    IReadOnlySet<DateOnly> Ocupados);
