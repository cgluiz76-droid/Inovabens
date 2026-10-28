using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Comum;
using Inovabens.Core.Dominio;

namespace Inovabens.Core.Financeiro;

/* ------------------------------ Consultas ------------------------------ */

public sealed record AquisicaoDetalhe(Aquisicao Dados, IReadOnlyList<AquisicaoItem> Itens);

public sealed record DespesaDetalhe(Despesa Dados, string Categoria);

public sealed record ChecklistItem(
    Guid DespesaId, Guid LancamentoId, string Descricao, string Categoria, Recorrencia Recorrencia,
    DateOnly? Vencimento, decimal Valor, StatusLancamento? Status, DateOnly? DataPagamento, string? Justificativa);

public sealed record ChecklistResumo(int Pago, int NaoPago, int NaoSeAplica, int AMarcar);

public sealed record Checklist(DateOnly Mes, ChecklistResumo Resumo, IReadOnlyList<ChecklistItem> Itens);

public sealed record LancamentoHistorico(DespesaLancamento Dados, string Descricao, string Categoria);

/* ------------------------------ Entradas ------------------------------ */

/// <summary>Valores monetários: numeric(15,2), nunca negativos, no máximo 2 casas.</summary>
internal static class Dinheiro
{
    private const decimal Maximo = 9_999_999_999_999.99m;

    public static IEnumerable<ValidationResult> Validar(params (string Campo, decimal? Valor)[] valores)
    {
        foreach (var (campo, v) in valores)
        {
            if (v is null) continue;
            if (v < 0) yield return new ValidationResult("O valor não pode ser negativo.", [campo]);
            else if (v > Maximo) yield return new ValidationResult("Valor muito alto.", [campo]);
            else if (decimal.Round(v.Value, 2) != v) yield return new ValidationResult("Use no máximo 2 casas decimais.", [campo]);
        }
    }
}

public sealed class AquisicaoForm : IValidatableObject
{
    [Required(ErrorMessage = "Informe a data da aquisição.")]
    public DateOnly? DataAquisicao { get; set; }

    public decimal ValorPago { get; set; }
    public decimal Impostos { get; set; }
    public decimal Taxas { get; set; }
    public decimal DespesasDiversas { get; set; }

    [StringLength(80, ErrorMessage = "Máximo de 80 caracteres.")]
    public string? FormaPagamento { get; set; }

    public string? Observacao { get; set; }

    /// <summary>Calculado só para exibição; o valor oficial é a coluna gerada no banco.</summary>
    public decimal Total => ValorPago + Impostos + Taxas + DespesasDiversas;

    public IEnumerable<ValidationResult> Validate(ValidationContext _) => Dinheiro.Validar(
        (nameof(ValorPago), ValorPago), (nameof(Impostos), Impostos), (nameof(Taxas), Taxas), (nameof(DespesasDiversas), DespesasDiversas));

    public static AquisicaoForm De(Aquisicao a) => new()
    {
        DataAquisicao = a.DataAquisicao, ValorPago = a.ValorPago, Impostos = a.Impostos, Taxas = a.Taxas,
        DespesasDiversas = a.DespesasDiversas, FormaPagamento = a.FormaPagamento, Observacao = a.Observacao,
    };

    /// <param name="componentes">false quando há itens detalhados: aí impostos/taxas/despesas vêm do trigger.</param>
    internal void AplicarEm(Aquisicao a, bool componentes)
    {
        a.DataAquisicao = DataAquisicao!.Value;
        a.ValorPago = ValorPago;
        if (componentes)
        {
            a.Impostos = Impostos;
            a.Taxas = Taxas;
            a.DespesasDiversas = DespesasDiversas;
        }
        a.FormaPagamento = Texto.Limpo(FormaPagamento);
        a.Observacao = Texto.Limpo(Observacao);
    }
}

public sealed class AquisicaoItemForm : IValidatableObject
{
    public TipoItemAquisicao Tipo { get; set; } = TipoItemAquisicao.IMPOSTO;

    [Required(ErrorMessage = "Informe a descrição.")]
    [StringLength(200, MinimumLength = 2, ErrorMessage = "A descrição deve ter de 2 a 200 caracteres.")]
    public string Descricao { get; set; } = "";

    [Required(ErrorMessage = "Informe o valor.")]
    public decimal? Valor { get; set; }

    public DateOnly? DataPagamento { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _) => Dinheiro.Validar((nameof(Valor), Valor));

    internal void AplicarEm(AquisicaoItem i)
    {
        i.Tipo = Tipo;
        i.Descricao = Descricao.Trim();
        i.Valor = Valor!.Value;
        i.DataPagamento = DataPagamento;
    }
}

public sealed class DespesaForm : IValidatableObject
{
    [Required(ErrorMessage = "Selecione a categoria.")]
    public Guid? CategoriaId { get; set; }

    [Required(ErrorMessage = "Informe a descrição.")]
    [StringLength(200, MinimumLength = 2, ErrorMessage = "A descrição deve ter de 2 a 200 caracteres.")]
    public string Descricao { get; set; } = "";

    public Recorrencia Recorrencia { get; set; } = Recorrencia.MENSAL;

    [Range(1, 31, ErrorMessage = "Dia entre 1 e 31.")]
    public int? DiaVencimento { get; set; } = 10;

    public decimal ValorPrevisto { get; set; }

    [Required(ErrorMessage = "Informe o início.")]
    public DateOnly? InicioEm { get; set; }

    public DateOnly? FimEm { get; set; }
    public Guid? RegularizacaoId { get; set; }
    public bool Ativa { get; set; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        foreach (var e in Dinheiro.Validar((nameof(ValorPrevisto), ValorPrevisto))) yield return e;
        if (FimEm < InicioEm) yield return new ValidationResult("O fim não pode ser antes do início.", [nameof(FimEm)]);
    }

    public static DespesaForm De(Despesa d) => new()
    {
        CategoriaId = d.CategoriaId, Descricao = d.Descricao, Recorrencia = d.Recorrencia, DiaVencimento = d.DiaVencimento,
        ValorPrevisto = d.ValorPrevisto, InicioEm = d.InicioEm, FimEm = d.FimEm, RegularizacaoId = d.RegularizacaoId, Ativa = d.Ativa,
    };

    internal void AplicarEm(Despesa d)
    {
        d.CategoriaId = CategoriaId!.Value;
        d.Descricao = Descricao.Trim();
        d.Recorrencia = Recorrencia;
        d.DiaVencimento = DiaVencimento;
        d.ValorPrevisto = ValorPrevisto;
        d.InicioEm = InicioEm!.Value;
        d.FimEm = FimEm;
        d.RegularizacaoId = RegularizacaoId;
        d.Ativa = Ativa;
    }
}

/// <summary>Marcação do checklist. REGRA: Não Pago exige justificativa (mín. 10 caracteres úteis).</summary>
public sealed class StatusLancamentoEntrada : IValidatableObject
{
    public const int MinimoJustificativa = 10;

    public StatusLancamento Status { get; set; }
    public string? Justificativa { get; set; }
    public DateOnly? DataPagamento { get; set; }
    public decimal? Valor { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (Status == StatusLancamento.NAO_PAGO && (Justificativa?.Trim().Length ?? 0) < MinimoJustificativa)
            yield return new ValidationResult(
                $"Justificativa obrigatória (mínimo {MinimoJustificativa} caracteres) quando o status é Não Pago.", [nameof(Justificativa)]);
        foreach (var e in Dinheiro.Validar((nameof(Valor), Valor))) yield return e;
    }

    /// <summary>Pago e Não se aplica encerram o alerta de despesa vencida do lançamento.</summary>
    public bool ResolveAlerta => Status is StatusLancamento.PAGO or StatusLancamento.NAO_SE_APLICA;

    /// <summary>
    /// Justificativa só em Não Pago / Não se aplica; data de pagamento só em Pago (padrão: hoje);
    /// valor só muda quando informado.
    /// </summary>
    internal void AplicarEm(DespesaLancamento l, Guid usuarioId, DateOnly hoje)
    {
        var comNota = Status is StatusLancamento.NAO_PAGO or StatusLancamento.NAO_SE_APLICA;
        l.Status = Status;
        l.Justificativa = comNota ? Texto.Limpo(Justificativa) : null;
        l.DataPagamento = Status == StatusLancamento.PAGO ? DataPagamento ?? hoje : null;
        if (Valor is { } v) l.Valor = v;
        l.AtualizadoPor = usuarioId;
    }
}

public sealed class ReceitaForm : IValidatableObject
{
    public TipoReceita Tipo { get; set; } = TipoReceita.VENDA;

    [Required(ErrorMessage = "Informe a data.")]
    public DateOnly? Data { get; set; }

    [Required(ErrorMessage = "Informe o valor bruto.")]
    public decimal? ValorBruto { get; set; }

    public decimal CustosVenda { get; set; }
    public decimal Tributos { get; set; }
    public Guid? ContraparteId { get; set; }
    public string? Observacao { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _) => Dinheiro.Validar(
        (nameof(ValorBruto), ValorBruto), (nameof(CustosVenda), CustosVenda), (nameof(Tributos), Tributos));

    internal void AplicarEm(Receita r)
    {
        r.Tipo = Tipo;
        r.Data = Data!.Value;
        r.ValorBruto = ValorBruto!.Value;
        r.CustosVenda = CustosVenda;
        r.Tributos = Tributos;
        r.ContraparteId = ContraparteId;
        r.Observacao = Texto.Limpo(Observacao);
    }
}

public sealed class CategoriaForm
{
    [Required(ErrorMessage = "Informe o nome.")]
    [StringLength(80, MinimumLength = 2, ErrorMessage = "O nome deve ter de 2 a 80 caracteres.")]
    public string Nome { get; set; } = "";

    public bool Ativo { get; set; } = true;
}
