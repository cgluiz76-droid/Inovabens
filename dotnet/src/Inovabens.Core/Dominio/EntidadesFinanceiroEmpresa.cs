using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Inovabens.Core.Dominio;

/* Financeiro da Imobiliária: as finanças da própria empresa (menu Financeiro), separadas do livro caixa dos imóveis. */

public enum TipoContaBancaria { CORRENTE, POUPANCA, APLICACAO, CAIXA, CARTAO_CREDITO }

public enum TipoCategoriaEmpresa { ENTRADA, SAIDA }

/// <summary>Linha da DRE. TERCEIROS (aluguel que passa pela imobiliária, repasse) e PATRIMONIAL ficam fora do resultado.</summary>
public enum GrupoCategoriaEmpresa { RECEITA, DEDUCAO, CUSTO, DESPESA, FINANCEIRO, TERCEIROS, PATRIMONIAL }

/// <summary>Onde o dinheiro da imobiliária está (ou o que ela deve, no cartão de crédito).</summary>
[Table("contas_bancarias")]
public class ContaBancaria
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Nome { get; set; } = "";
    public TipoContaBancaria Tipo { get; set; } = TipoContaBancaria.CORRENTE;
    public string? Banco { get; set; }
    public string? Agencia { get; set; }
    public string? Numero { get; set; }
    /// <summary>Saldo no início do dia <see cref="DataSaldoInicial"/>.</summary>
    public decimal SaldoInicial { get; set; }
    public DateOnly DataSaldoInicial { get; set; }
    public short? DiaFechamento { get; set; }
    public short? DiaVencimento { get; set; }
    public decimal? Limite { get; set; }
    public bool Ativa { get; set; } = true;
    public int Ordem { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;

    [NotMapped] public bool Cartao => Tipo == TipoContaBancaria.CARTAO_CREDITO;
    public ContaBancaria Copia() => (ContaBancaria)MemberwiseClone();
}

/// <summary>Categoria do plano de contas da imobiliária.</summary>
[Table("categorias_empresa")]
public class CategoriaEmpresa
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Nome { get; set; } = "";
    public TipoCategoriaEmpresa Tipo { get; set; }
    public GrupoCategoriaEmpresa Grupo { get; set; }
    public bool Ativa { get; set; } = true;

    public CategoriaEmpresa Copia() => (CategoriaEmpresa)MemberwiseClone();
}

/// <summary>
/// Conta a pagar/receber ou movimento da imobiliária. <see cref="Valor"/> com sinal (entrada +, saída −).
/// Pago = <see cref="DataPagamento"/> preenchida. Transferência = dois lançamentos com o mesmo <see cref="TransferenciaId"/>.
/// </summary>
[Table("lancamentos_empresa")]
public class LancamentoEmpresa
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Descricao { get; set; } = "";
    public decimal Valor { get; set; }
    public DateOnly Vencimento { get; set; }
    public DateOnly? DataPagamento { get; set; }
    public DateOnly Competencia { get; set; }
    public Guid? CategoriaId { get; set; }
    public Guid? ContaId { get; set; }
    public Guid? TransferenciaId { get; set; }
    public Guid? PessoaId { get; set; }
    public string? PessoaNome { get; set; }
    public Guid? ImovelId { get; set; }
    public string? Documento { get; set; }
    public string? Observacao { get; set; }
    public string? Parcela { get; set; }
    public Guid? CriadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public Guid? AtualizadoPor { get; set; }
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? ExcluidoEm { get; set; }

    [JsonIgnore] public CategoriaEmpresa? Categoria { get; set; }
    [JsonIgnore] public ContaBancaria? Conta { get; set; }
    [JsonIgnore] public Imovel? Imovel { get; set; }

    public LancamentoEmpresa Copia() => (LancamentoEmpresa)MemberwiseClone();
}
