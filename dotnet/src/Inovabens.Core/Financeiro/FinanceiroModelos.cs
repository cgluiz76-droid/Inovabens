using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Comum;
using Inovabens.Core.Dominio;

namespace Inovabens.Core.Financeiro;

/* ------------------------------ Consultas ------------------------------ */

public sealed record AquisicaoDetalhe(Aquisicao Dados, IReadOnlyList<AquisicaoItem> Itens);

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

/// <summary>
/// Nova receita / nova despesa (título a receber / a pagar). O usuário digita valores POSITIVOS; o sinal vem do tipo
/// (receita +, despesa −). <see cref="Data"/> é o vencimento; <see cref="Liquidado"/> marca como já pago/recebido.
/// Recorrente: todo mês, no <see cref="DiaRecorrencia"/>, nasce um lançamento PREVISTO com o <see cref="ValorEstimado"/>.
/// </summary>
public sealed class LancamentoForm : IValidatableObject
{
    public TipoConta Tipo { get; set; } = TipoConta.DESPESA;

    [Required(ErrorMessage = "Informe o vencimento.")]
    public DateOnly? Data { get; set; }

    [Required(ErrorMessage = "Informe o valor.")]
    public decimal? Valor { get; set; }

    /// <summary>Já pago/recebido: o título nasce LIQUIDADO (entra no saldo na data da liquidação).</summary>
    public bool Liquidado { get; set; }
    public DateOnly? DataLiquidacao { get; set; }

    [Required(ErrorMessage = "Informe a descrição.")]
    [StringLength(200, MinimumLength = 2, ErrorMessage = "A descrição deve ter de 2 a 200 caracteres.")]
    public string Descricao { get; set; } = "";

    [Required(ErrorMessage = "Escolha a conta orçamentária.")]
    public Guid? ContaId { get; set; }

    [StringLength(2000, ErrorMessage = "Máximo de 2000 caracteres.")]
    public string? Observacao { get; set; }

    /// <summary>Pessoa (quem pagou/recebeu): digitada ou escolhida entre as cadastradas.</summary>
    [StringLength(200, ErrorMessage = "Máximo de 200 caracteres.")]
    public string? Pessoa { get; set; }

    public bool Recorrente { get; set; }
    public int? DiaRecorrencia { get; set; }
    public decimal? ValorEstimado { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        foreach (var r in Dinheiro.Validar((nameof(Valor), Valor), (nameof(ValorEstimado), ValorEstimado))) yield return r;
        if (Valor == 0) yield return new ValidationResult("O valor não pode ser zero.", [nameof(Valor)]);
        if (Liquidado && DataLiquidacao is null) yield return new ValidationResult("Informe a data do pagamento/recebimento.", [nameof(DataLiquidacao)]);
        if (!Recorrente) yield break;
        if (DiaRecorrencia is not (>= 1 and <= 31)) yield return new ValidationResult("Informe o dia da recorrência (1 a 31).", [nameof(DiaRecorrencia)]);
        if (ValorEstimado is null or 0) yield return new ValidationResult("Informe o valor estimado dos próximos meses.", [nameof(ValorEstimado)]);
    }

    public static LancamentoForm Novo(TipoConta tipo)
    {
        var hoje = DatasBR.Hoje();
        return new LancamentoForm { Tipo = tipo, Data = hoje, Liquidado = true, DataLiquidacao = hoje, DiaRecorrencia = hoje.Day };
    }

    public static LancamentoForm De(LancamentoFinanceiro l) => new()
    {
        Tipo = l.Valor < 0 ? TipoConta.DESPESA : TipoConta.RECEITA, Data = l.Data, Valor = Math.Abs(l.Valor), Descricao = l.Descricao,
        Liquidado = l.Situacao == SituacaoLancamento.LIQUIDADO, DataLiquidacao = l.DataLiquidacao ?? DatasBR.Hoje(),
        ContaId = l.ContaId, Observacao = l.Observacao, Pessoa = l.PessoaNome, Recorrente = l.Recorrente, DiaRecorrencia = l.DiaRecorrencia ?? l.Data.Day,
        ValorEstimado = l.ValorEstimado is { } e ? Math.Abs(e) : null,
    };

    private decimal Sinal => Tipo == TipoConta.DESPESA ? -1 : 1;

    internal void AplicarEm(LancamentoFinanceiro l)
    {
        l.Data = Data!.Value;
        l.Valor = Sinal * Valor!.Value;
        l.Situacao = Liquidado ? SituacaoLancamento.LIQUIDADO : SituacaoLancamento.ABERTO;
        l.DataLiquidacao = Liquidado ? DataLiquidacao : null;
        l.Descricao = Descricao.Trim();
        l.ContaId = ContaId!.Value;
        l.Observacao = Texto.Limpo(Observacao);
        l.PessoaNome = Texto.Limpo(Pessoa);
        l.Recorrente = Recorrente;
        l.DiaRecorrencia = Recorrente ? DiaRecorrencia : null;
        l.ValorEstimado = Recorrente ? Sinal * ValorEstimado!.Value : null;
    }
}

/// <summary>
/// Confirmação de um previsto (vira título ABERTO, ou já LIQUIDADO) ou liquidação de um título aberto.
/// Valor positivo; o sinal é o do lançamento. <see cref="Data"/> = vencimento (na liquidação, não muda).
/// </summary>
public sealed class ConfirmacaoPrevistoForm : IValidatableObject
{
    [Required(ErrorMessage = "Informe o vencimento.")]
    public DateOnly? Data { get; set; }

    [Required(ErrorMessage = "Informe o valor.")]
    public decimal? Valor { get; set; }

    public bool Liquidado { get; set; }
    public DateOnly? DataLiquidacao { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        foreach (var r in Dinheiro.Validar((nameof(Valor), Valor))) yield return r;
        if (Valor == 0) yield return new ValidationResult("O valor não pode ser zero.", [nameof(Valor)]);
        if (Liquidado && DataLiquidacao is null) yield return new ValidationResult("Informe a data do pagamento/recebimento.", [nameof(DataLiquidacao)]);
    }
}

/// <summary>Fechamento da operação (venda) do imóvel, na aba Investimento.</summary>
public sealed class FechamentoForm : IValidatableObject
{
    [Required(ErrorMessage = "Informe a data da venda.")]
    public DateOnly? Data { get; set; }

    [Required(ErrorMessage = "Informe o valor da venda.")]
    public decimal? ValorVenda { get; set; }

    public decimal Comissao { get; set; }
    public decimal Impostos { get; set; }
    public decimal OutrosCustos { get; set; }

    /// <summary>Se o saldo dos lançamentos financeiros entra na apuração do lucro (null = decidir depois).</summary>
    public bool? IncluirLancamentos { get; set; }

    public Guid? CompradorId { get; set; }
    public string? Observacao { get; set; }

    public decimal Liquido => (ValorVenda ?? 0) - Comissao - Impostos - OutrosCustos;

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        foreach (var r in Dinheiro.Validar((nameof(ValorVenda), ValorVenda), (nameof(Comissao), Comissao), (nameof(Impostos), Impostos),
                     (nameof(OutrosCustos), OutrosCustos))) yield return r;
        if (ValorVenda is <= 0) yield return new ValidationResult("O valor da venda deve ser maior que zero.", [nameof(ValorVenda)]);
    }

    public static FechamentoForm De(FechamentoInvestimento f) => new()
    {
        Data = f.Data, ValorVenda = f.ValorVenda, Comissao = f.Comissao, Impostos = f.Impostos, OutrosCustos = f.OutrosCustos,
        IncluirLancamentos = f.IncluirLancamentos, CompradorId = f.CompradorId, Observacao = f.Observacao,
    };

    internal void AplicarEm(FechamentoInvestimento f)
    {
        f.Data = Data!.Value;
        f.ValorVenda = ValorVenda!.Value;
        f.Comissao = Comissao;
        f.Impostos = Impostos;
        f.OutrosCustos = OutrosCustos;
        f.IncluirLancamentos = IncluirLancamentos;
        f.CompradorId = CompradorId;
        f.Observacao = Texto.Limpo(Observacao);
    }
}

public sealed class ContaForm
{
    [Required(ErrorMessage = "Informe o nome.")]
    [StringLength(80, MinimumLength = 2, ErrorMessage = "O nome deve ter de 2 a 80 caracteres.")]
    public string Nome { get; set; } = "";

    public TipoConta Tipo { get; set; } = TipoConta.DESPESA;
    public bool Ativo { get; set; } = true;
}
