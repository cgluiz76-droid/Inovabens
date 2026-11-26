using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Dominio;

namespace Inovabens.Core.Locacao;

/* Formulários e leituras da vistoria terceirizada (fase 4). */

public sealed class VistoriaForm
{
    [Required(ErrorMessage = "Escolha o contrato.")]
    public Guid? ContratoId { get; set; }

    public TipoVistoria Tipo { get; set; } = TipoVistoria.ENTRADA;

    /// <summary>Empresa de vistoria (cadastro de Pessoas).</summary>
    public Guid? EmpresaId { get; set; }

    [StringLength(200, ErrorMessage = "Até 200 caracteres.")]
    public string? Vistoriador { get; set; }

    [StringLength(2000, ErrorMessage = "Até 2000 caracteres.")]
    public string? Observacao { get; set; }
}

public sealed class AgendamentoVistoriaForm
{
    [Required(ErrorMessage = "Informe dia e hora.")]
    public DateTime? Inicio { get; set; }

    [Range(15, 1440, ErrorMessage = "Duração entre 15 minutos e 24 horas.")]
    public int DuracaoMinutos { get; set; } = 90;

    public Guid? ResponsavelId { get; set; }

    /// <summary>Chave entregue à empresa de vistoria (controle de chaves).</summary>
    public Guid? ChaveId { get; set; }
    public DateTime? DevolverChaveAte { get; set; }
}

public sealed class RealizacaoVistoriaForm
{
    [Required(ErrorMessage = "Informe a data da vistoria.")]
    public DateOnly? Data { get; set; }

    [StringLength(30)] public string? LeituraAgua { get; set; }
    [StringLength(30)] public string? LeituraLuz { get; set; }
    [StringLength(30)] public string? LeituraGas { get; set; }

    [Range(0, 99, ErrorMessage = "Quantidade inválida.")]
    public int? Chaves { get; set; }

    [StringLength(200)] public string? Vistoriador { get; set; }

    /// <summary>Até quando o inquilino pode contestar o laudo (o contrato costuma fixar o prazo).</summary>
    public DateOnly? PrazoContestacao { get; set; }
}

public sealed class CustoVistoriaForm : IValidatableObject
{
    [Required(ErrorMessage = "Informe o valor.")]
    [Range(typeof(decimal), "0", "9999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")]
    public decimal? Valor { get; set; }

    [Required(ErrorMessage = "Escolha como lançar o custo.")]
    public CobrancaManutencao? Cobranca { get; set; }

    public DateOnly? Vencimento { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Cobranca is not null and not CobrancaManutencao.DIRETO && Valor > 0 && Vencimento is null)
            yield return new("Informe o vencimento do pagamento à empresa de vistoria.", [nameof(Vencimento)]);
    }
}

public sealed class PendenciaForm
{
    [StringLength(100, ErrorMessage = "Até 100 caracteres.")]
    public string? Comodo { get; set; }

    [Required(ErrorMessage = "Descreva a pendência.")]
    [StringLength(1000, ErrorMessage = "Até 1000 caracteres.")]
    public string Descricao { get; set; } = "";

    public ResponsavelManutencao Responsavel { get; set; } = ResponsavelManutencao.LOCATARIO;

    [Range(typeof(decimal), "0", "9999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")]
    public decimal? Valor { get; set; }
}

public sealed record VistoriaLinha(Guid Id, string Codigo, TipoVistoria Tipo, StatusVistoria Status, Guid ContratoId, string ContratoCodigo,
    string ImovelCodigo, string? Empresa, DateTime? AgendadaPara, DateOnly? RealizadaEm, bool TemLaudo, int PendenciasAbertas,
    DateOnly? PrazoContestacao, DateOnly? AceiteEm, DateOnly? ContestadaEm);

public sealed record PendenciaLinha(Guid Id, string? Comodo, string Descricao, ResponsavelManutencao Responsavel, decimal? Valor,
    StatusPendenciaVistoria Status, Guid? ChamadoId, string? ChamadoCodigo);

/// <summary>Vistoria anterior do contrato (a de entrada, para comparar com a de saída).</summary>
public sealed record VistoriaResumo(Guid Id, string Codigo, TipoVistoria Tipo, DateOnly? RealizadaEm, string? LeituraAgua, string? LeituraLuz,
    string? LeituraGas, int? Chaves, string? LaudoNome, IReadOnlyList<PendenciaLinha> Pendencias);

public sealed record VistoriaDetalhe(Vistoria Vistoria, string ImovelCodigo, string ImovelEndereco, string ContratoCodigo,
    ModalidadeLocacao Modalidade, StatusContratoLocacao ContratoStatus, string? EmpresaNome, string? EmpresaTelefone, string? LocatarioNome,
    string? LocatarioTelefone, IReadOnlyList<PendenciaLinha> Pendencias, IReadOnlyList<VistoriaResumo> Anteriores, decimal AbatidoRepasse,
    string? LinkPasta)
{
    public string Codigo => Vistoria.Codigo;
}

/// <summary>Débitos do inquilino apontados na vistoria de saída, para o acerto da rescisão.</summary>
public sealed record DebitosVistoria(decimal Total, string? Descricao, IReadOnlyList<Guid> Pendencias);
