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

/// <summary>Conta orçamentária (plano de contas simples) de um lançamento: de receita ou de despesa.</summary>
[Table("contas_orcamentarias")]
public class ContaOrcamentaria
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Nome { get; set; } = "";
    public TipoConta Tipo { get; set; } = TipoConta.DESPESA;
    public bool Ativo { get; set; } = true;
}

/// <summary>
/// Lançamento financeiro do imóvel (receitas e despesas correntes). <see cref="Valor"/> tem SINAL: receita positiva,
/// despesa negativa. Situação: PREVISTO (recorrência) → ABERTO (título a pagar/receber, vence em <see cref="Data"/>) →
/// LIQUIDADO (pago/recebido em <see cref="DataLiquidacao"/>; só ele entra no saldo). <see cref="Numero"/> é sequencial
/// no imóvel e existe nos ABERTOS e LIQUIDADOS (exibido "CODIGO-0001"). Um lançamento recorrente gera, a cada mês, um
/// PREVISTO (<see cref="OrigemId"/> + <see cref="Competencia"/>) que o usuário confirma ou ignora.
/// </summary>
[Table("lancamentos_financeiros")]
public class LancamentoFinanceiro
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImovelId { get; set; }
    public int? Numero { get; set; }
    public SituacaoLancamento Situacao { get; set; } = SituacaoLancamento.ABERTO;
    /// <summary>Vencimento.</summary>
    public DateOnly Data { get; set; }
    public DateOnly? DataLiquidacao { get; set; }
    public decimal Valor { get; set; }
    public string Descricao { get; set; } = "";
    public Guid ContaId { get; set; }
    /// <summary>Quem pagou ou recebeu: nome livre; <see cref="PessoaId"/> quando bate com uma pessoa cadastrada.</summary>
    public Guid? PessoaId { get; set; }
    public string? PessoaNome { get; set; }
    public string? Observacao { get; set; }
    public bool Recorrente { get; set; }
    public int? DiaRecorrencia { get; set; }
    public decimal? ValorEstimado { get; set; }
    public Guid? OrigemId { get; set; }
    public DateOnly? Competencia { get; set; }
    public Guid? CriadoPor { get; set; }
    public Guid? AtualizadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? ExcluidoEm { get; set; }
    /// <summary>Quando e por quem o lançamento foi enviado ao responsável financeiro (BPO) do imóvel.</summary>
    public DateTime? BpoEnviadoEm { get; set; }
    public Guid? BpoEnviadoPor { get; set; }
    /// <summary>Conta bancária da imobiliária por onde o dinheiro passou (entra no saldo e na conciliação dela).</summary>
    public Guid? ContaBancariaId { get; set; }
    /// <summary>Linha do extrato bancário que confirma este lançamento.</summary>
    public Guid? ExtratoItemId { get; set; }
    [JsonIgnore] public ContaOrcamentaria? Conta { get; set; }
    [JsonIgnore] public Imovel? Imovel { get; set; }

    public LancamentoFinanceiro Copia() => (LancamentoFinanceiro)MemberwiseClone();
}

/// <summary>
/// Fechamento da operação (venda) — um por imóvel, só administrador. <see cref="ValorLiquido"/> é coluna GERADA.
/// <see cref="IncluirLancamentos"/>: se o saldo dos lançamentos financeiros entra na apuração do lucro (null = não decidido).
/// </summary>
[Table("fechamentos_investimento")]
public class FechamentoInvestimento
{
    [System.ComponentModel.DataAnnotations.Key] public Guid ImovelId { get; set; }
    public DateOnly Data { get; set; }
    public decimal ValorVenda { get; set; }
    public decimal Comissao { get; set; }
    public decimal Impostos { get; set; }
    public decimal OutrosCustos { get; set; }
    public decimal ValorLiquido { get; set; }
    public bool? IncluirLancamentos { get; set; }
    public Guid? CompradorId { get; set; }
    public string? Observacao { get; set; }
    public Guid? AtualizadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public FechamentoInvestimento Copia() => (FechamentoInvestimento)MemberwiseClone();
}

/// <summary>
/// Comprovante (nota, recibo, boleto pago) de um lançamento financeiro. O arquivo fica no Google Drive,
/// em "&lt;pasta do imóvel&gt;/07 - Financeiro/&lt;ano&gt;".
/// </summary>
[Table("financeiro_comprovantes")]
public class ComprovanteFinanceiro
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImovelId { get; set; }
    public Guid LancamentoId { get; set; }
    public string ArquivoChave { get; set; } = "";
    public string NomeOriginal { get; set; } = "";
    public string TipoConteudo { get; set; } = "";
    public long Tamanho { get; set; }
    public Guid? CriadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}

/// <summary>Item do Notion já importado (página, linha de tabela, arquivo) e o registro que ele virou no sistema.</summary>
[Table("notion_importacoes")]
public class NotionImportacao
{
    public string NotionId { get; set; } = "";
    /// <summary>PESSOA, IMOVEL, MEMORANDO, COMPROMISSO, ARQUIVO, ENVOLVIDO.</summary>
    public string Tipo { get; set; } = "";
    public Guid EntidadeId { get; set; }
    public DateTime? EditadoNotionEm { get; set; }
    public DateTime ImportadoEm { get; set; } = DateTime.UtcNow;
}

/// <summary>Taxa média (fração: 0,1205 = 12,05%) de financiamento imobiliário de um banco no mês, do Banco Central.</summary>
[Table("taxas_financiamento")]
public class TaxaFinanciamento
{
    public string Cnpj8 { get; set; } = "";
    public DateOnly Competencia { get; set; }
    public string Instituicao { get; set; } = "";
    public decimal TaxaMes { get; set; }
    public decimal TaxaAno { get; set; }
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
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
