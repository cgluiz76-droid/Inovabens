using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Inovabens.Core.Dominio;

/// <summary>
/// Aquisição (CAPEX) — uma por imóvel. <see cref="TotalAquisicao"/> é coluna GERADA no banco
/// (valor_pago + impostos + taxas + despesas_diversas): nunca é gravada pela aplicação.
/// Quando há itens detalhados, o trigger trg_aquisicao_itens_recalcula refaz impostos/taxas/despesas.
/// </summary>
[Table("aquisicoes")]
public class Aquisicao
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImovelId { get; set; }
    public DateOnly DataAquisicao { get; set; }
    public decimal ValorPago { get; set; }
    public decimal Impostos { get; set; }
    public decimal Taxas { get; set; }
    public decimal DespesasDiversas { get; set; }
    public decimal TotalAquisicao { get; set; }
    public string? FormaPagamento { get; set; }
    public string? Observacao { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public Aquisicao Copia() => (Aquisicao)MemberwiseClone();
}

[Table("aquisicao_itens")]
public class AquisicaoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AquisicaoId { get; set; }
    public TipoItemAquisicao Tipo { get; set; }
    public string Descricao { get; set; } = "";
    public decimal Valor { get; set; }
    public DateOnly? DataPagamento { get; set; }
    public string? ComprovanteChave { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
    [JsonIgnore] public Aquisicao? Aquisicao { get; set; }

    public AquisicaoItem Copia() => (AquisicaoItem)MemberwiseClone();
}

[Table("despesa_categorias")]
public class DespesaCategoria
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Nome { get; set; } = "";
    public bool Ativo { get; set; } = true;
}

/// <summary>Despesa recorrente (OPEX). Os lançamentos mensais (checklist) ficam em <see cref="DespesaLancamento"/>.</summary>
[Table("despesas")]
public class Despesa
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImovelId { get; set; }
    public Guid CategoriaId { get; set; }
    public Guid? RegularizacaoId { get; set; }
    public string Descricao { get; set; } = "";
    public Recorrencia Recorrencia { get; set; } = Recorrencia.UNICA;
    public int? DiaVencimento { get; set; }
    public decimal ValorPrevisto { get; set; }
    public DateOnly InicioEm { get; set; }
    public DateOnly? FimEm { get; set; }
    public bool Ativa { get; set; } = true;
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? ExcluidoEm { get; set; }
    [JsonIgnore] public DespesaCategoria? Categoria { get; set; }

    public Despesa Copia() => (Despesa)MemberwiseClone();
}

/// <summary>
/// Lançamento de uma despesa numa competência (sempre dia 1 do mês). Status nulo = "a marcar".
/// REGRA (também CHECK no banco): NAO_PAGO exige justificativa com pelo menos 10 caracteres.
/// </summary>
[Table("despesa_lancamentos")]
public class DespesaLancamento
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DespesaId { get; set; }
    public DateOnly Competencia { get; set; }
    public DateOnly? Vencimento { get; set; }
    public decimal Valor { get; set; }
    public StatusLancamento? Status { get; set; }
    public DateOnly? DataPagamento { get; set; }
    public string? Justificativa { get; set; }
    public string? ComprovanteChave { get; set; }
    public Guid? AtualizadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
    [JsonIgnore] public Despesa? Despesa { get; set; }

    public DespesaLancamento Copia() => (DespesaLancamento)MemberwiseClone();
}

/// <summary>Receita (venda/locação). <see cref="ValorLiquido"/> é coluna GERADA (bruto − custos − tributos).</summary>
[Table("receitas")]
public class Receita
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImovelId { get; set; }
    public TipoReceita Tipo { get; set; }
    public DateOnly Data { get; set; }
    public decimal ValorBruto { get; set; }
    public decimal CustosVenda { get; set; }
    public decimal Tributos { get; set; }
    public decimal ValorLiquido { get; set; }
    public Guid? ContraparteId { get; set; }
    public string? Observacao { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? ExcluidoEm { get; set; }

    public Receita Copia() => (Receita)MemberwiseClone();
}

/// <summary>Variação mensal (%) de um índice econômico (IPCA, INCC, IGP-M, Selic). Competência = dia 1.</summary>
[Table("indices_serie")]
public class IndiceSerie
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public CodigoIndice Codigo { get; set; }
    public DateOnly Competencia { get; set; }
    public decimal VariacaoMensalPct { get; set; }
    public string Fonte { get; set; } = "BCB-SGS";
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
}
