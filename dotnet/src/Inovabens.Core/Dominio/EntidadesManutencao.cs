using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Inovabens.Core.Dominio;

/* Manutenção (migração 0049): chamado → orçamentos → aprovação do proprietário → agenda → conclusão com custo.
   Tipos gravados como texto (varchar + CHECK). */

public enum CategoriaManutencao
{
    HIDRAULICA, ELETRICA, PINTURA, TELHADO, ESTRUTURA, ESQUADRIAS, ELETRODOMESTICO, AR_CONDICIONADO, LIMPEZA, DEDETIZACAO,
    JARDIM, OUTROS,
}

public enum UrgenciaManutencao { BAIXA, MEDIA, ALTA, EMERGENCIA }

public enum StatusChamado { ABERTO, ORCAMENTO, AGUARDANDO_APROVACAO, APROVADO, AGENDADO, CONCLUIDO, RECUSADO, CANCELADO }

/// <summary>Quem arca com o conserto.</summary>
public enum ResponsavelManutencao { LOCADOR, LOCATARIO, IMOBILIARIA }

/// <summary>
/// Como o custo é lançado na conclusão. REPASSE: a imobiliária paga o prestador e desconta dos repasses seguintes do
/// locador. FATURA: a imobiliária paga e cobra do inquilino na próxima fatura. IMOVEL: despesa no financeiro do imóvel
/// (próprio/sócios). IMOBILIARIA: despesa da imobiliária. DIRETO: proprietário ou inquilino pagou o prestador (só registro).
/// </summary>
public enum CobrancaManutencao { REPASSE, FATURA, IMOVEL, IMOBILIARIA, DIRETO }

public enum OrigemChamado { EQUIPE, INQUILINO, PREVENTIVA }

public enum TipoArquivoChamado { FOTO, ORCAMENTO, NOTA, OUTRO }

[Table("chamados_manutencao")]
public class ChamadoManutencao
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Numero { get; set; }
    public Guid ImovelId { get; set; }
    public Guid? ContratoId { get; set; }
    public Guid? PreventivaId { get; set; }
    public OrigemChamado Origem { get; set; }
    public string Titulo { get; set; } = "";
    public string? Descricao { get; set; }
    public CategoriaManutencao Categoria { get; set; }
    public UrgenciaManutencao Urgencia { get; set; } = UrgenciaManutencao.MEDIA;
    public ResponsavelManutencao Responsavel { get; set; }
    public StatusChamado Status { get; set; } = StatusChamado.ABERTO;
    public string? SolicitanteNome { get; set; }
    public string? SolicitanteTelefone { get; set; }
    public Guid? PrestadorId { get; set; }
    public Guid? OrcamentoId { get; set; }
    public decimal? Valor { get; set; }
    public string? AprovacaoToken { get; set; }
    public DateTime? AprovacaoPedidaEm { get; set; }
    public DateTime? AprovadoEm { get; set; }
    public string? AprovadoPor { get; set; }
    public string? AprovacaoObs { get; set; }
    /// <summary>Por que o proprietário recebeu menos de 3 cotações (urgência, só um prestador atende etc.).</summary>
    public string? JustificativaCotacoes { get; set; }
    public DateTime? AgendadoPara { get; set; }
    public Guid? CompromissoId { get; set; }
    public DateOnly? ConcluidoEm { get; set; }
    public decimal? ValorFinal { get; set; }
    public CobrancaManutencao? Cobranca { get; set; }
    public DateOnly? VencimentoPrestador { get; set; }
    public Guid? LancamentoEmpresaId { get; set; }
    public Guid? LancamentoImovelId { get; set; }
    public Guid? FaturaItemId { get; set; }
    public int? Avaliacao { get; set; }
    /// <summary>Motivo da recusa ou do cancelamento.</summary>
    public string? Motivo { get; set; }
    public string? DrivePastaId { get; set; }
    public Guid? CriadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    [NotMapped] public string Codigo => CodigoDe(Numero);
    public static string CodigoDe(int numero) => $"MAN-{numero:0000}";
    [NotMapped] public bool Aberto => Status is not (StatusChamado.CONCLUIDO or StatusChamado.RECUSADO or StatusChamado.CANCELADO);

    [JsonIgnore] public Imovel? Imovel { get; set; }
}

[Table("orcamentos_manutencao")]
public class OrcamentoManutencao
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChamadoId { get; set; }
    public Guid PrestadorId { get; set; }
    public decimal Valor { get; set; }
    public int? PrazoDias { get; set; }
    public string? Descricao { get; set; }
    /// <summary>Arquivo da cotação (subpasta "Cotações" do chamado no Drive).</summary>
    public Guid? ArquivoId { get; set; }
    public Guid? CriadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    [JsonIgnore] public Pessoa? Prestador { get; set; }
}

[Table("chamado_manutencao_arquivos")]
public class ChamadoArquivo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChamadoId { get; set; }
    public string Nome { get; set; } = "";
    public string DriveId { get; set; } = "";
    public TipoArquivoChamado Tipo { get; set; }
    public bool PeloInquilino { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}

[Table("chamado_manutencao_eventos")]
public class ChamadoEvento
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChamadoId { get; set; }
    public string Descricao { get; set; } = "";
    public Guid? UsuarioId { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}

[Table("manutencao_abatimentos")]
public class ManutencaoAbatimento
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChamadoId { get; set; }
    public Guid PagamentoId { get; set; }
    public decimal Valor { get; set; }
}

[Table("manutencoes_preventivas")]
public class ManutencaoPreventiva
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImovelId { get; set; }
    public string Titulo { get; set; } = "";
    public CategoriaManutencao Categoria { get; set; }
    public ResponsavelManutencao Responsavel { get; set; }
    public int PeriodicidadeMeses { get; set; } = 12;
    public DateOnly ProximaEm { get; set; }
    public Guid? PrestadorId { get; set; }
    public string? Observacao { get; set; }
    public bool Ativa { get; set; } = true;
    public Guid? CriadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    [JsonIgnore] public Imovel? Imovel { get; set; }
}
