using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Inovabens.Core.Dominio;

/* Locação (migração 0046). "Locado" vem do contrato ATIVO, não da situação do imóvel: assim o imóvel pode estar
   locado e continuar à venda. Os tipos abaixo são gravados como texto (varchar + CHECK). */

/// <summary>ADMINISTRACAO: imóvel de terceiros (taxa e repasse). PROPRIO: imóvel da empresa ou de sócios (sem repasse).</summary>
public enum ModalidadeLocacao { ADMINISTRACAO, PROPRIO }

public enum TipoLocacao { RESIDENCIAL, COMERCIAL }

public enum StatusContratoLocacao { RASCUNHO, ATIVO, ENCERRADO, CANCELADO }

public enum PapelContratoLocacao { LOCADOR, LOCATARIO, FIADOR }

public enum StatusFaturaLocacao { ABERTA, PAGA, CANCELADA }

/// <summary>
/// DESCONTO é negativo e reduz a base da taxa de administração (como o aluguel). Os quatro últimos são do acerto da
/// rescisão (migração 0047); CAUCAO é negativo (abate a caução devolvida dos débitos).
/// </summary>
public enum TipoItemFatura { ALUGUEL, CONDOMINIO, IPTU, SEGURO_INCENDIO, DESCONTO, OUTRO, MULTA_RESCISORIA, AVISO_PREVIO, VISTORIA, CAUCAO }

/// <summary>Quem pediu o fim do contrato: define multa (art. 4) e aviso prévio (art. 6).</summary>
public enum IniciativaRescisao { LOCATARIO, LOCADOR, ACORDO, FIM_PRAZO }

public enum RespostaPreferencia { ACEITA, RECUSADA, SEM_RESPOSTA }

public enum TipoHistoricoLocacao { REAJUSTE, ADITIVO, RENOVACAO, RESCISAO, PREFERENCIA, ACORDO, DOCUMENTO }

/// <summary>Evento do contrato que gera aviso no sino e tarefa na agenda.</summary>
public enum TipoAvisoLocacao { REAJUSTE, FIM, GARANTIA, SEGURO_INCENDIO, PREFERENCIA }

[Table("contratos_locacao")]
public class ContratoLocacao
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Gerado pelo banco (sequência); exibido como LOC-0001.</summary>
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)] public int Numero { get; set; }
    public Guid ImovelId { get; set; }
    public ModalidadeLocacao Modalidade { get; set; } = ModalidadeLocacao.ADMINISTRACAO;
    public TipoLocacao Tipo { get; set; } = TipoLocacao.RESIDENCIAL;
    public StatusContratoLocacao Status { get; set; } = StatusContratoLocacao.RASCUNHO;
    public Guid? PropostaId { get; set; }
    public DateOnly Inicio { get; set; }
    public int PrazoMeses { get; set; } = 30;
    public short DiaVencimento { get; set; } = 10;
    public bool PagamentoAntecipado { get; set; }
    public decimal ValorAluguel { get; set; }
    public decimal ValorCondominio { get; set; }
    public decimal ValorIptu { get; set; }
    public decimal ValorSeguroIncendio { get; set; }
    public CodigoIndice IndiceReajuste { get; set; } = CodigoIndice.IGPM;
    public GarantiaLocacao Garantia { get; set; } = GarantiaLocacao.SEM_GARANTIA;
    public decimal? GarantiaValor { get; set; }
    public string? GarantiaDetalhe { get; set; }
    public decimal TaxaAdministracaoPct { get; set; } = 10;
    public decimal IntermediacaoValor { get; set; }
    public decimal MultaAtrasoPct { get; set; } = 10;
    public decimal JurosMesPct { get; set; } = 1;
    public decimal MultaRescisoriaAlugueis { get; set; } = 3;
    public short DiasRepasse { get; set; } = 5;
    public bool ClausulaVigencia { get; set; }
    public bool Averbado { get; set; }
    public string? Observacoes { get; set; }
    public DateTime? AtivadoEm { get; set; }
    public DateOnly? EncerradoEm { get; set; }
    public string? MotivoEncerramento { get; set; }
    /// <summary>Aniversário do último reajuste aplicado; o próximo é 12 meses depois (sem reajuste, conta do início).</summary>
    public DateOnly? UltimoReajuste { get; set; }
    /// <summary>Vencimento do seguro-fiança, título de capitalização ou carta-fiança.</summary>
    public DateOnly? GarantiaVence { get; set; }
    public DateOnly? SeguroIncendioVence { get; set; }
    public IniciativaRescisao? IniciativaRescisao { get; set; }
    public DateOnly? AvisoDesocupacaoEm { get; set; }
    /// <summary>Notificação de preferência na venda (art. 27): o locatário tem 30 dias para aceitar.</summary>
    public DateOnly? PreferenciaNotificadaEm { get; set; }
    public decimal? PreferenciaValor { get; set; }
    public string? PreferenciaCondicoes { get; set; }
    public RespostaPreferencia? PreferenciaResposta { get; set; }
    /// <summary>Subpasta do contrato em "08 - Locação" (renovações e aditivos ficam na mesma).</summary>
    public string? DrivePastaId { get; set; }
    public Guid? CriadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public Guid? AtualizadoPor { get; set; }
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public Imovel? Imovel { get; set; }
    [JsonIgnore] public List<ContratoLocacaoParte> Partes { get; set; } = [];

    /// <summary>Último dia do contrato (início + prazo − 1 dia).</summary>
    [NotMapped] public DateOnly Fim => Inicio.AddMonths(PrazoMeses).AddDays(-1);
    [NotMapped] public string Codigo => CodigoDe(Numero);
    /// <summary>Aniversário em que cabe o próximo reajuste (anual, contado do início ou do último reajuste).</summary>
    [NotMapped] public DateOnly ProximoReajuste => (UltimoReajuste ?? Inicio).AddMonths(12);
    /// <summary>Último dia do prazo do direito de preferência (30 dias da notificação).</summary>
    [NotMapped] public DateOnly? PreferenciaPrazo => PreferenciaNotificadaEm?.AddDays(30);

    public static string CodigoDe(int numero) => $"LOC-{numero:0000}";

    public ContratoLocacao Copia() => (ContratoLocacao)MemberwiseClone();
}

[Table("contrato_locacao_partes")]
public class ContratoLocacaoParte
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ContratoId { get; set; }
    public Guid PessoaId { get; set; }
    public PapelContratoLocacao Papel { get; set; }
    /// <summary>Só locadores: parte no repasse (a soma dá 100).</summary>
    public decimal? Percentual { get; set; }
    [JsonIgnore] public Pessoa? Pessoa { get; set; }
}

[Table("faturas_locacao")]
public class FaturaLocacao
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ContratoId { get; set; }
    /// <summary>Mês de uso do imóvel (dia 1).</summary>
    public DateOnly Competencia { get; set; }
    public DateOnly Vencimento { get; set; }
    public StatusFaturaLocacao Status { get; set; } = StatusFaturaLocacao.ABERTA;
    public string? Observacao { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? CanceladaEm { get; set; }
    public string? MotivoCancelamento { get; set; }
    /// <summary>Fatura do acerto da rescisão (multa, vistoria, caução), separada do aluguel do mês.</summary>
    public bool Rescisao { get; set; }

    [JsonIgnore] public ContratoLocacao? Contrato { get; set; }
    [JsonIgnore] public List<FaturaLocacaoItem> Itens { get; set; } = [];
    [JsonIgnore] public List<FaturaLocacaoPagamento> Pagamentos { get; set; } = [];

    public FaturaLocacao Copia() => (FaturaLocacao)MemberwiseClone();
}

[Table("fatura_locacao_itens")]
public class FaturaLocacaoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FaturaId { get; set; }
    public int Ordem { get; set; }
    public TipoItemFatura Tipo { get; set; }
    public string Descricao { get; set; } = "";
    public decimal Valor { get; set; }
}

/// <summary>Um recebimento da fatura (pode ser parcial). Estornado = desconsiderado, com os lançamentos desfeitos.</summary>
[Table("fatura_locacao_pagamentos")]
public class FaturaLocacaoPagamento
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FaturaId { get; set; }
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)] public int ReciboNumero { get; set; }
    public DateOnly Data { get; set; }
    /// <summary>Quanto abateu da fatura (sem multa e juros).</summary>
    public decimal Principal { get; set; }
    public decimal Multa { get; set; }
    public decimal Juros { get; set; }
    public string? Forma { get; set; }
    public Guid? ContaBancariaId { get; set; }
    public string? Observacao { get; set; }
    public Guid? RegistradoPor { get; set; }
    public DateTime RegistradoEm { get; set; } = DateTime.UtcNow;
    public List<Guid> LancamentosEmpresaIds { get; set; } = [];
    public Guid? LancamentoImovelId { get; set; }
    public DateTime? EstornadoEm { get; set; }
    public Guid? EstornadoPor { get; set; }

    [NotMapped] public decimal Total => Principal + Multa + Juros;

    public FaturaLocacaoPagamento Copia() => (FaturaLocacaoPagamento)MemberwiseClone();
}

/// <summary>Parte de um locador num recebimento: bruto − taxa de administração − intermediação = líquido.</summary>
[Table("repasses_locacao")]
public class RepasseLocacao
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PagamentoId { get; set; }
    public Guid ContratoId { get; set; }
    public Guid PessoaId { get; set; }
    public decimal Percentual { get; set; }
    public decimal Bruto { get; set; }
    public decimal TaxaAdministracao { get; set; }
    public decimal Intermediacao { get; set; }
    public decimal Liquido { get; set; }
    public DateOnly PrevistoPara { get; set; }
    public DateOnly? PagoEm { get; set; }
    public Guid? LancamentoEmpresaId { get; set; }
    [JsonIgnore] public Pessoa? Pessoa { get; set; }
}

/// <summary>Histórico do contrato: reajustes, aditivos, renovações, rescisão e notificação de preferência.</summary>
[Table("contrato_locacao_historico")]
public class ContratoLocacaoHistorico
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ContratoId { get; set; }
    public TipoHistoricoLocacao Tipo { get; set; }
    public DateOnly Data { get; set; }
    public decimal? ValorAnterior { get; set; }
    public decimal? ValorNovo { get; set; }
    public int? PrazoAnterior { get; set; }
    public int? PrazoNovo { get; set; }
    public CodigoIndice? Indice { get; set; }
    public decimal? Percentual { get; set; }
    public string Descricao { get; set; } = "";
    /// <summary>JSON com os dados do ato (acordo: faturas, desconto, parcelas), para refazer o documento.</summary>
    public string? Dados { get; set; }
    public Guid? CriadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}

/// <summary>Aviso de vencimento já dado (um por evento e antecedência), com a tarefa criada na agenda.</summary>
[Table("locacao_avisos")]
public class LocacaoAviso
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ContratoId { get; set; }
    public TipoAvisoLocacao Tipo { get; set; }
    public DateOnly DataEvento { get; set; }
    public short Antecedencia { get; set; }
    public Guid? CompromissoId { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}
