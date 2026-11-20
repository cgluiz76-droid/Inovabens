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
    /// <summary>Linha do extrato que confirma este lançamento (conciliado).</summary>
    public Guid? ExtratoItemId { get; set; }
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

/// <summary>Um arquivo OFX importado numa conta (com o saldo que o banco informou, para conferência).</summary>
[Table("extrato_importacoes")]
public class ExtratoImportacao
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ContaId { get; set; }
    public string Arquivo { get; set; } = "";
    public DateOnly? De { get; set; }
    public DateOnly? Ate { get; set; }
    public decimal? SaldoBanco { get; set; }
    public DateOnly? DataSaldo { get; set; }
    public int Itens { get; set; }
    public int Novos { get; set; }
    public Guid? ImportadoPor { get; set; }
    public DateTime ImportadoEm { get; set; } = DateTime.UtcNow;
}

/// <summary>Linha do extrato do banco. Pendente até ser conciliada (confirmada por lançamentos) ou ignorada.</summary>
[Table("extrato_itens")]
public class ExtratoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ContaId { get; set; }
    public Guid ImportacaoId { get; set; }
    public string FitId { get; set; } = "";
    public DateOnly Data { get; set; }
    public decimal Valor { get; set; }
    public string Descricao { get; set; } = "";
    public string? Documento { get; set; }
    public DateTime? ConciliadoEm { get; set; }
    public DateTime? IgnoradoEm { get; set; }
    public Guid? ConciliadoPor { get; set; }

    public ExtratoItem Copia() => (ExtratoItem)MemberwiseClone();
}

/// <summary>Aprendizado da conciliação: descrição do banco (normalizada) → categoria.</summary>
[Table("regras_conciliacao")]
public class RegraConciliacao
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Chave { get; set; } = "";
    public TipoCategoriaEmpresa Tipo { get; set; }
    public Guid CategoriaId { get; set; }
    public string? PessoaNome { get; set; }
    public int Usos { get; set; } = 1;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
}

/// <summary>Mês fechado: lançamentos com pagamento ou competência nele não mudam até reabrir.</summary>
[Table("fechamentos_financeiros")]
public class FechamentoFinanceiro
{
    [System.ComponentModel.DataAnnotations.Key] public DateOnly Mes { get; set; }
    public Guid? FechadoPor { get; set; }
    public DateTime FechadoEm { get; set; } = DateTime.UtcNow;
}
