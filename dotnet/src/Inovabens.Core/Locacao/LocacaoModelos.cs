using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Dominio;

namespace Inovabens.Core.Locacao;

public sealed record ContratoLinha(
    Guid Id, string Codigo, Guid ImovelId, string ImovelCodigo, string ImovelTitulo, StatusContratoLocacao Status,
    ModalidadeLocacao Modalidade, decimal Aluguel, DateOnly Inicio, DateOnly Fim, string Locatarios, string Locadores,
    int FaturasAtrasadas);

public sealed record ParteDetalhe(Guid PessoaId, string Nome, string? CpfCnpj, string? Contato, PapelContratoLocacao Papel, decimal? Percentual);

public sealed record PagamentoDetalhe(
    Guid Id, int ReciboNumero, DateOnly Data, decimal Principal, decimal Multa, decimal Juros, string? Forma, string? Conta,
    bool Estornado, bool RepassePago)
{
    public decimal Total => Principal + Multa + Juros;
    public string Recibo => $"{ReciboNumero:000000}";
}

public sealed record FaturaDetalhe(
    Guid Id, DateOnly Competencia, DateOnly Vencimento, StatusFaturaLocacao Status, decimal Total, decimal Pago,
    IReadOnlyList<FaturaLocacaoItem> Itens, IReadOnlyList<PagamentoDetalhe> Pagamentos, string? Observacao, bool Rescisao = false)
{
    public decimal Saldo => Status == StatusFaturaLocacao.CANCELADA ? 0 : Math.Max(0, Total - Pago);
    public bool Atrasada(DateOnly hoje) => Status == StatusFaturaLocacao.ABERTA && Vencimento < hoje;
}

public sealed record RepasseLinha(
    Guid Id, Guid ContratoId, string ContratoCodigo, string ImovelCodigo, Guid PessoaId, string Pessoa, decimal Percentual,
    decimal Bruto, decimal Taxa, decimal Intermediacao, decimal Liquido, DateOnly PrevistoPara, DateOnly? PagoEm,
    DateOnly Competencia, int ReciboNumero, bool Estornado);

public sealed record ContratoDetalhe(
    ContratoLocacao Contrato, string ImovelCodigo, string ImovelTitulo, string? ImovelEndereco, IReadOnlyList<ParteDetalhe> Partes,
    IReadOnlyList<FaturaDetalhe> Faturas, IReadOnlyList<RepasseLinha> Repasses, decimal IntermediacaoRetida,
    IReadOnlyList<ContratoLocacaoHistorico> Historico)
{
    public string Codigo => Contrato.Codigo;
}

/// <summary>Uma linha da régua de cobrança: fatura em aberto com o texto pronto para o WhatsApp.</summary>
public sealed record CobrancaLinha(
    Guid FaturaId, Guid ContratoId, string ContratoCodigo, string ImovelCodigo, string Locatario, string? Telefone,
    DateOnly Competencia, DateOnly Vencimento, decimal Saldo, int DiasAtraso, string Etapa, string Mensagem, string? LinkWhatsapp);

public sealed record PainelLocacao(
    int ContratosAtivos, int Rascunhos, decimal AluguelMensal, int FaturasAtrasadas, decimal ValorAtrasado,
    int RepassesAPagar, decimal ValorRepassesAPagar);

public sealed class ParteForm
{
    public Guid? PessoaId { get; set; }
    public string? PessoaNome { get; set; }
    public PapelContratoLocacao Papel { get; set; } = PapelContratoLocacao.LOCATARIO;
    public decimal? Percentual { get; set; }
}

public sealed class ContratoForm : IValidatableObject
{
    public Guid? ImovelId { get; set; }
    /// <summary>Para a busca na tela.</summary>
    public string? ImovelCodigo { get; set; }
    public Guid? PropostaId { get; set; }
    public ModalidadeLocacao Modalidade { get; set; } = ModalidadeLocacao.ADMINISTRACAO;
    public TipoLocacao Tipo { get; set; } = TipoLocacao.RESIDENCIAL;

    [Required(ErrorMessage = "Informe a data de início.")]
    public DateOnly? Inicio { get; set; }

    [Range(1, 600, ErrorMessage = "Prazo entre 1 e 600 meses.")]
    public int PrazoMeses { get; set; } = 30;

    [Range(1, 28, ErrorMessage = "Dia de vencimento entre 1 e 28.")]
    public short DiaVencimento { get; set; } = 10;

    public bool PagamentoAntecipado { get; set; }

    [Required(ErrorMessage = "Informe o valor do aluguel.")]
    [Range(typeof(decimal), "0.01", "99999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Aluguel precisa ser maior que zero.")]
    public decimal? ValorAluguel { get; set; }

    [Range(typeof(decimal), "0", "99999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")] public decimal ValorCondominio { get; set; }
    [Range(typeof(decimal), "0", "99999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")] public decimal ValorIptu { get; set; }
    [Range(typeof(decimal), "0", "99999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")] public decimal ValorSeguroIncendio { get; set; }
    public CodigoIndice IndiceReajuste { get; set; } = CodigoIndice.IGPM;
    public GarantiaLocacao Garantia { get; set; } = GarantiaLocacao.SEM_GARANTIA;
    public decimal? GarantiaValor { get; set; }
    [StringLength(500, ErrorMessage = "Até 500 caracteres.")] public string? GarantiaDetalhe { get; set; }

    [Range(typeof(decimal), "0", "100", ParseLimitsInInvariantCulture = true, ErrorMessage = "Taxa entre 0 e 100%.")] public decimal TaxaAdministracaoPct { get; set; } = 10;
    /// <summary>Nulo = igual ao 1º aluguel (padrão da Inova Bens).</summary>
    [Range(typeof(decimal), "0", "99999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")] public decimal? IntermediacaoValor { get; set; }
    [Range(typeof(decimal), "0", "20", ParseLimitsInInvariantCulture = true, ErrorMessage = "Multa entre 0 e 20%.")] public decimal MultaAtrasoPct { get; set; } = 10;
    [Range(typeof(decimal), "0", "10", ParseLimitsInInvariantCulture = true, ErrorMessage = "Juros entre 0 e 10% ao mês.")] public decimal JurosMesPct { get; set; } = 1;
    [Range(typeof(decimal), "0", "12", ParseLimitsInInvariantCulture = true, ErrorMessage = "Entre 0 e 12 aluguéis.")] public decimal MultaRescisoriaAlugueis { get; set; } = 3;
    [Range(0, 30, ErrorMessage = "Entre 0 e 30 dias.")] public short DiasRepasse { get; set; } = 5;
    public bool ClausulaVigencia { get; set; }
    public bool Averbado { get; set; }
    public string? Observacoes { get; set; }
    /// <summary>Vencimento do seguro-fiança, título ou carta-fiança (gera aviso para renovar).</summary>
    public DateOnly? GarantiaVence { get; set; }
    public DateOnly? SeguroIncendioVence { get; set; }
    public List<ParteForm> Partes { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ImovelId is null) yield return new("Escolha o imóvel.", [nameof(ImovelCodigo)]);
        if (Garantia == GarantiaLocacao.CAUCAO && ValorAluguel is { } a && GarantiaValor > a * 3)
            yield return new("Caução em dinheiro é limitada a 3 aluguéis (Lei 8.245, art. 38).", [nameof(GarantiaValor)]);
        if (PagamentoAntecipado && Garantia != GarantiaLocacao.SEM_GARANTIA)
            yield return new("Aluguel adiantado só é permitido sem garantia (Lei 8.245, art. 20).", [nameof(PagamentoAntecipado)]);
        foreach (var p in Partes.Where(p => p.PessoaId is null))
            yield return new("Escolha a pessoa de cada parte.", [nameof(Partes)]);
    }
}

public sealed class RecebimentoLocacaoForm
{
    [Required(ErrorMessage = "Informe a data do recebimento.")]
    public DateOnly? Data { get; set; }

    /// <summary>Quanto abate da fatura (sem multa e juros).</summary>
    [Required(ErrorMessage = "Informe o valor.")]
    [Range(typeof(decimal), "0.01", "99999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor precisa ser maior que zero.")]
    public decimal? Principal { get; set; }

    [Range(typeof(decimal), "0", "99999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")] public decimal Multa { get; set; }
    [Range(typeof(decimal), "0", "99999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")] public decimal Juros { get; set; }

    /// <summary>Conta da imobiliária onde o dinheiro entrou (obrigatória na administração de terceiros).</summary>
    public Guid? ContaBancariaId { get; set; }

    [StringLength(30)] public string? Forma { get; set; } = "Pix";
    [StringLength(300)] public string? Observacao { get; set; }
}

public sealed class ItemAvulsoForm
{
    public TipoItemFatura Tipo { get; set; } = TipoItemFatura.OUTRO;

    [Required(ErrorMessage = "Descreva o item.")]
    [StringLength(200, ErrorMessage = "Até 200 caracteres.")]
    public string Descricao { get; set; } = "";

    [Required(ErrorMessage = "Informe o valor.")]
    [Range(typeof(decimal), "0.01", "99999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor precisa ser maior que zero.")]
    public decimal? Valor { get; set; }
}

/* ------------------------------ fase 3: reajuste, aditivo, rescisão, preferência ------------------------------ */

/// <summary>
/// Reajuste anual pelo índice acumulado nos 12 meses que terminam no mês anterior ao aniversário. Faltando mês
/// publicado, <see cref="Percentual"/> é nulo (sincronize os índices ou lance o mês à mão em Financeiro).
/// </summary>
public sealed record ReajustePrevia(
    DateOnly Aniversario, CodigoIndice Indice, DateOnly De, DateOnly Ate, decimal? Percentual, decimal ValorAtual, decimal ValorNovo,
    IReadOnlyList<DateOnly> Faltando)
{
    /// <summary>Índice acumulado negativo: o aluguel não diminui (fica o valor atual).</summary>
    public bool Negativo => Percentual < 0;
}

/// <summary>Um vencimento do contrato (reajuste, fim do prazo, garantia, seguro, prazo da preferência).</summary>
public sealed record VencimentoLocacao(Guid ContratoId, string ContratoCodigo, string ImovelCodigo, TipoAvisoLocacao Tipo, DateOnly Data, string Descricao);

public sealed class AditivoForm
{
    public TipoHistoricoLocacao Tipo { get; set; } = TipoHistoricoLocacao.RENOVACAO;

    [Required(ErrorMessage = "Informe a data do aditivo.")]
    public DateOnly? Data { get; set; }

    /// <summary>Meses somados ao prazo (renovação). Zero quando o aditivo só muda o valor.</summary>
    [Range(0, 600, ErrorMessage = "Entre 0 e 600 meses.")]
    public int MesesAdicionais { get; set; }

    /// <summary>Novo aluguel; vazio mantém o atual.</summary>
    [Range(typeof(decimal), "0.01", "99999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Aluguel precisa ser maior que zero.")]
    public decimal? NovoValor { get; set; }

    [Required(ErrorMessage = "Descreva o que mudou.")]
    [StringLength(1000, ErrorMessage = "Até 1000 caracteres.")]
    public string Descricao { get; set; } = "";
}

public sealed class RescisaoForm
{
    [Required(ErrorMessage = "Informe a data da entrega das chaves.")]
    public DateOnly? EntregaChaves { get; set; }

    public IniciativaRescisao Iniciativa { get; set; } = IniciativaRescisao.LOCATARIO;

    /// <summary>Quando o locatário avisou que ia sair (contrato por prazo indeterminado: 30 dias, art. 6).</summary>
    public DateOnly? AvisoEm { get; set; }

    /// <summary>Dispensa a multa e o aviso prévio (ex.: transferência de emprego, art. 4 parágrafo único, ou acordo).</summary>
    public bool IsentarMulta { get; set; }

    [Range(typeof(decimal), "0", "99999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")]
    public decimal DebitosVistoria { get; set; }
    [StringLength(200, ErrorMessage = "Até 200 caracteres.")] public string? DescricaoVistoria { get; set; }

    /// <summary>Encargos pendentes não faturados (contas de consumo, condomínio extra...).</summary>
    [Range(typeof(decimal), "0", "99999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")]
    public decimal OutrosDebitos { get; set; }
    [StringLength(200, ErrorMessage = "Até 200 caracteres.")] public string? DescricaoOutros { get; set; }

    /// <summary>Caução corrigida a devolver (vazio: o valor depositado). Abate os débitos do acerto.</summary>
    [Range(typeof(decimal), "0", "99999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")]
    public decimal? CaucaoCorrigida { get; set; }

    /// <summary>Vencimento da fatura do acerto (vazio: 5 dias após a entrega).</summary>
    public DateOnly? VencimentoAcerto { get; set; }

    [StringLength(500, ErrorMessage = "Até 500 caracteres.")] public string? Motivo { get; set; }

    /// <summary>Registra a devolução das chaves que estão fora (controle de chaves).</summary>
    public bool DevolverChaves { get; set; } = true;

    /// <summary>Volta a anunciar o imóvel para alugar no site (finalidade passa a incluir locação).</summary>
    public bool AnunciarLocacao { get; set; }
}

/// <summary>Resultado do cálculo da rescisão (prévia na tela e base do termo).</summary>
public sealed record RescisaoCalculo(
    int DiasRestantes, int DiasPrazo, decimal MultaRescisoria, int DiasAvisoFaltando, decimal AvisoPrevio,
    IReadOnlyList<ItemCalculado> Itens, decimal Caucao, decimal CaucaoAbatida)
{
    public decimal Debitos => Itens.Where(i => i.Valor > 0).Sum(i => i.Valor);
    public decimal TotalAcerto => Itens.Sum(i => i.Valor);
    /// <summary>Sobra da caução depois de abater os débitos: devolver ao locatário.</summary>
    public decimal ADevolver => Caucao - CaucaoAbatida;
}

public sealed class PreferenciaForm
{
    [Required(ErrorMessage = "Informe o preço da venda.")]
    [Range(typeof(decimal), "0.01", "9999999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Preço precisa ser maior que zero.")]
    public decimal? Valor { get; set; }

    /// <summary>Forma de pagamento, condições e onde ver a documentação (art. 27 exige todas as condições do negócio).</summary>
    [Required(ErrorMessage = "Descreva as condições do negócio.")]
    [StringLength(1000, ErrorMessage = "Até 1000 caracteres.")]
    public string Condicoes { get; set; } = "";
}
