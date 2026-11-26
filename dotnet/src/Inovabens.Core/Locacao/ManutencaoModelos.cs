using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Dominio;

namespace Inovabens.Core.Locacao;

/* Formulários e leituras da manutenção (fase 5). */

public sealed class ChamadoForm
{
    [Required(ErrorMessage = "Escolha o imóvel.")]
    public Guid? ImovelId { get; set; }

    /// <summary>Contrato de locação; vazio = o contrato ativo do imóvel, se houver.</summary>
    public Guid? ContratoId { get; set; }

    [Required(ErrorMessage = "Descreva o problema em poucas palavras.")]
    [StringLength(200, ErrorMessage = "Até 200 caracteres.")]
    public string Titulo { get; set; } = "";

    [StringLength(4000, ErrorMessage = "Até 4000 caracteres.")]
    public string? Descricao { get; set; }

    public CategoriaManutencao Categoria { get; set; } = CategoriaManutencao.HIDRAULICA;
    public UrgenciaManutencao Urgencia { get; set; } = UrgenciaManutencao.MEDIA;
    public ResponsavelManutencao Responsavel { get; set; } = ResponsavelManutencao.LOCADOR;

    [StringLength(200)] public string? SolicitanteNome { get; set; }
    [StringLength(30)] public string? SolicitanteTelefone { get; set; }
}

public sealed class OrcamentoForm
{
    [Required(ErrorMessage = "Escolha o prestador.")]
    public Guid? PrestadorId { get; set; }

    [Required(ErrorMessage = "Informe o valor.")]
    [Range(typeof(decimal), "0", "9999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")]
    public decimal? Valor { get; set; }

    [Range(0, 365, ErrorMessage = "Prazo entre 0 e 365 dias.")]
    public int? PrazoDias { get; set; }

    [StringLength(2000, ErrorMessage = "Até 2000 caracteres.")]
    public string? Descricao { get; set; }
}

public sealed class AgendamentoForm
{
    [Required(ErrorMessage = "Informe dia e hora.")]
    public DateTime? Inicio { get; set; }

    [Range(15, 1440, ErrorMessage = "Duração entre 15 minutos e 24 horas.")]
    public int DuracaoMinutos { get; set; } = 120;

    /// <summary>Quem da equipe acompanha (dono do compromisso na agenda).</summary>
    public Guid? ResponsavelId { get; set; }

    /// <summary>Chave entregue ao prestador (controle de chaves).</summary>
    public Guid? ChaveId { get; set; }
    public DateTime? DevolverChaveAte { get; set; }
}

public sealed class ConclusaoForm : IValidatableObject
{
    [Required(ErrorMessage = "Informe a data do serviço.")]
    public DateOnly? Data { get; set; }

    [Required(ErrorMessage = "Informe o valor final.")]
    [Range(typeof(decimal), "0", "9999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")]
    public decimal? ValorFinal { get; set; }

    [Required(ErrorMessage = "Escolha como lançar o custo.")]
    public CobrancaManutencao? Cobranca { get; set; }

    /// <summary>Quando a imobiliária (ou o imóvel) paga o prestador.</summary>
    public DateOnly? VencimentoPrestador { get; set; }

    [Range(1, 5, ErrorMessage = "Nota de 1 a 5.")]
    public int? Avaliacao { get; set; }

    [StringLength(1000, ErrorMessage = "Até 1000 caracteres.")]
    public string? Observacao { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Cobranca is not null and not CobrancaManutencao.DIRETO && ValorFinal > 0 && VencimentoPrestador is null)
            yield return new("Informe o vencimento do pagamento ao prestador.", [nameof(VencimentoPrestador)]);
    }
}

public sealed class PreventivaForm
{
    [Required(ErrorMessage = "Escolha o imóvel.")]
    public Guid? ImovelId { get; set; }

    [Required(ErrorMessage = "Informe o serviço.")]
    [StringLength(200, ErrorMessage = "Até 200 caracteres.")]
    public string Titulo { get; set; } = "";

    public CategoriaManutencao Categoria { get; set; } = CategoriaManutencao.LIMPEZA;
    public ResponsavelManutencao Responsavel { get; set; } = ResponsavelManutencao.LOCADOR;

    [Range(1, 120, ErrorMessage = "Entre 1 e 120 meses.")]
    public int PeriodicidadeMeses { get; set; } = 12;

    [Required(ErrorMessage = "Informe a data do próximo serviço.")]
    public DateOnly? ProximaEm { get; set; }

    public Guid? PrestadorId { get; set; }

    [StringLength(1000, ErrorMessage = "Até 1000 caracteres.")]
    public string? Observacao { get; set; }

    public bool Ativa { get; set; } = true;
}

/// <summary>Chamado aberto pelo inquilino no link do contrato (sem login).</summary>
public sealed class AberturaInquilinoForm
{
    [Required(ErrorMessage = "Informe seu nome.")]
    [StringLength(200, ErrorMessage = "Até 200 caracteres.")]
    public string Nome { get; set; } = "";

    [Required(ErrorMessage = "Informe um telefone para contato.")]
    [StringLength(30, ErrorMessage = "Telefone inválido.")]
    public string Telefone { get; set; } = "";

    [Required(ErrorMessage = "Diga o que aconteceu.")]
    [StringLength(200, ErrorMessage = "Até 200 caracteres.")]
    public string Titulo { get; set; } = "";

    [StringLength(4000, ErrorMessage = "Até 4000 caracteres.")]
    public string? Descricao { get; set; }

    public CategoriaManutencao Categoria { get; set; } = CategoriaManutencao.OUTROS;
    public UrgenciaManutencao Urgencia { get; set; } = UrgenciaManutencao.MEDIA;

    /// <summary>Campo escondido contra robôs: preenchido = descartado.</summary>
    public string? Site { get; set; }
}

public sealed record FiltroChamados(bool SoAbertos = true, StatusChamado? Status = null, Guid? ImovelId = null, Guid? ContratoId = null, string? Termo = null);

public sealed record ChamadoLinha(Guid Id, string Codigo, string Titulo, Guid ImovelId, string ImovelCodigo, Guid? ContratoId, string? ContratoCodigo,
    CategoriaManutencao Categoria, UrgenciaManutencao Urgencia, StatusChamado Status, ResponsavelManutencao Responsavel, OrigemChamado Origem,
    string? PrestadorNome, decimal? Valor, DateTime? AgendadoPara, DateTime CriadoEm);

public sealed record OrcamentoLinha(Guid Id, Guid PrestadorId, string PrestadorNome, string? PrestadorTelefone, decimal Valor, int? PrazoDias,
    string? Descricao, bool Escolhido, string? Cotacao = null);

public sealed record ArquivoChamadoLinha(Guid Id, string Nome, TipoArquivoChamado Tipo, bool PeloInquilino, DateTime CriadoEm);

public sealed record EventoChamadoLinha(DateTime Data, string Descricao, string? Usuario);

public sealed record ChamadoDetalhe(ChamadoManutencao Chamado, string ImovelCodigo, string ImovelEndereco, string? ContratoCodigo,
    ModalidadeLocacao? Modalidade, decimal LimiteManutencao, string? PrestadorNome, string? PrestadorTelefone, string? LocatarioNome,
    string? LocatarioTelefone, string? LocadorNome, string? LocadorTelefone, IReadOnlyList<OrcamentoLinha> Orcamentos,
    IReadOnlyList<ArquivoChamadoLinha> Arquivos, IReadOnlyList<EventoChamadoLinha> Eventos, decimal AbatidoRepasse, string? LinkPasta,
    string? LinkAprovacao)
{
    public string Codigo => Chamado.Codigo;
}

/// <summary>Resultado do pedido de aprovação: aprovado na hora (dentro do limite) ou link para o proprietário.</summary>
public sealed record PedidoAprovacao(bool AprovadoNaHora, string? Motivo, string? Link, string? LinkWhatsapp);

/// <summary>Mensagens prontas para avisar prestador e inquilino do agendamento.</summary>
public sealed record AvisosAgendamento(string? WhatsappPrestador, string? WhatsappInquilino);

public sealed record LinkInquilino(string Link, string? LinkWhatsapp);

public sealed record OrcamentoPublico(Guid Id, string Prestador, decimal Valor, int? PrazoDias, string? Descricao, bool TemCotacao = false);

/// <summary>Arquivo da cotação para o proprietário baixar no link de aprovação.</summary>
public sealed record CotacaoArquivo(string Nome, byte[] Dados, string Tipo);

/// <summary>O que o proprietário vê no link de aprovação (sem dados de contato de terceiros).</summary>
public sealed record AprovacaoPublica(string Codigo, string Titulo, string? Descricao, string Imovel, CategoriaManutencao Categoria,
    UrgenciaManutencao Urgencia, IReadOnlyList<OrcamentoPublico> Orcamentos, Guid? Sugerido, StatusChamado Status, string? AprovadoPor,
    DateTime? RespondidoEm, string? Obs, string? JustificativaCotacoes = null);

/// <summary>O que o inquilino vê no link de abertura de chamado.</summary>
public sealed record ContextoChamadoPublico(string Imovel, string Contrato, IReadOnlyList<(string Codigo, string Titulo, StatusChamado Status)> Abertos);

public sealed record ManutencaoPendente(Guid ChamadoId, decimal Pendente);

public sealed record PreventivaLinha(Guid Id, Guid ImovelId, string ImovelCodigo, string Titulo, CategoriaManutencao Categoria,
    ResponsavelManutencao Responsavel, int PeriodicidadeMeses, DateOnly ProximaEm, Guid? PrestadorId, string? PrestadorNome, string? Observacao, bool Ativa);
