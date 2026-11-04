using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Inovabens.Core.Dominio;

// Entidades mapeadas para as tabelas do PostgreSQL (o esquema é mantido pelos scripts SQL em
// Dados/Migracoes, não por migrations do EF). Colunas: snake_case do nome da propriedade.
// Navegações levam [JsonIgnore] para não vazarem na auditoria (ex.: senha_hash do corretor).

[Table("imoveis")]
public class Imovel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Codigo { get; set; } = "";
    public FinalidadeImovel Finalidade { get; set; } = FinalidadeImovel.VENDA;
    public SituacaoImovel Situacao { get; set; } = SituacaoImovel.CAPTACAO;
    public string Tipo { get; set; } = "";
    public string? Subtipo { get; set; }
    public string Titulo { get; set; } = "";
    public string? DescricaoPublica { get; set; }
    public string? DescricaoInterna { get; set; }
    public string? Cep { get; set; }
    public string? Logradouro { get; set; }
    public string? Numero { get; set; }
    public string? Complemento { get; set; }
    public string? Bairro { get; set; }
    public string? Cidade { get; set; }
    public string? Uf { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool EnderecoPublico { get; set; }
    public string? Matricula { get; set; }
    public string? Cartorio { get; set; }
    public string? InscricaoMunicipal { get; set; }
    public decimal? AreaTotalM2 { get; set; }
    public decimal? AreaConstruidaM2 { get; set; }
    public decimal? AreaUtilM2 { get; set; }
    public int? Quartos { get; set; }
    public int? Suites { get; set; }
    public int? Banheiros { get; set; }
    public int? Vagas { get; set; }
    public decimal? PrecoVenda { get; set; }
    public decimal? PrecoLocacao { get; set; }
    public bool DivulgarSite { get; set; }
    public Guid? CorretorResponsavelId { get; set; }
    /// <summary>Nome da pasta principal do imóvel no Google Drive ("Nome para Drive do Empreendimento").</summary>
    public string? NomeDrive { get; set; }
    /// <summary>Id da pasta principal no Drive; null enquanto ainda não foi criada.</summary>
    public string? DrivePastaId { get; set; }
    /// <summary>jsonb livre (atributos extras).</summary>
    public string? Atributos { get; set; } = "{}";
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? ExcluidoEm { get; set; }

    [JsonIgnore] public Usuario? Corretor { get; set; }
    [JsonIgnore] public List<ImovelCaracteristica> Caracteristicas { get; set; } = [];
    [JsonIgnore] public List<ImovelMidia> Midias { get; set; } = [];
    [JsonIgnore] public List<ImovelProprietario> Proprietarios { get; set; } = [];
    [JsonIgnore] public List<ImovelEnvolvido> Envolvidos { get; set; } = [];

    /// <summary>Cópia rasa, usada para registrar o "antes" na auditoria.</summary>
    public Imovel Copia() => (Imovel)MemberwiseClone();
}

[Table("imovel_caracteristicas")]
public class ImovelCaracteristica
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImovelId { get; set; }
    public string Chave { get; set; } = "";
    public string? Valor { get; set; }
    public bool Publico { get; set; } = true;
}

[Table("imovel_midias")]
public class ImovelMidia
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImovelId { get; set; }
    public TipoMidia Tipo { get; set; } = TipoMidia.FOTO;
    /// <summary>Id do arquivo no Google Drive.</summary>
    public string ArquivoChave { get; set; } = "";
    /// <summary>Subpasta do imóvel no Drive (1 a 6, ver <c>SubpastasDrive</c>); null nos arquivos anteriores ao Drive.</summary>
    public short? Subpasta { get; set; }
    public string? NomeOriginal { get; set; }
    public string? Legenda { get; set; }
    public int Ordem { get; set; }
    public bool Capa { get; set; }
    public bool Publica { get; set; } = true;
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    /// <summary>Foto como foi enviada, guardada quando o Magnific gera a versão melhorada (que vira ArquivoChave).</summary>
    public string? ArquivoOriginalChave { get; set; }
    public StatusMelhoria? MelhoriaStatus { get; set; }
    /// <summary>task_id do Magnific enquanto a melhoria está em processamento.</summary>
    public string? MelhoriaTarefa { get; set; }
    public string? MelhoriaErro { get; set; }
    public DateTime? MelhoriaAtualizadaEm { get; set; }
}

[Table("imovel_proprietarios")]
public class ImovelProprietario
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImovelId { get; set; }
    public Guid PessoaId { get; set; }
    public decimal Percentual { get; set; } = 100;
    public bool Principal { get; set; }
    public DateOnly? InicioEm { get; set; }
    public DateOnly? FimEm { get; set; }
    [JsonIgnore] public Pessoa? Pessoa { get; set; }
}

/// <summary>Tipo de imóvel, mantido na Administração; imoveis.tipo guarda o nome (renomear propaga por FK).</summary>
[Table("tipos_imovel")]
public class TipoImovel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Nome { get; set; } = "";
    public bool Ativo { get; set; } = true;
}

[Table("imovel_envolvidos")]
public class ImovelEnvolvido
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImovelId { get; set; }
    public Guid PessoaId { get; set; }
    /// <summary>Código do perfil de pessoa (tabela perfis_pessoa).</summary>
    public string Papel { get; set; } = "";
    public string? Observacao { get; set; }
    [JsonIgnore] public Pessoa? Pessoa { get; set; }
}

[Table("regularizacoes")]
public class Regularizacao
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImovelId { get; set; }
    public TipoRegularizacao Tipo { get; set; }
    public StatusRegularizacao Status { get; set; } = StatusRegularizacao.NAO_INICIADA;
    public Guid? ProfissionalId { get; set; }
    public DateOnly? PrazoPrevisto { get; set; }
    public DateOnly? ConcluidaEm { get; set; }
    public decimal? CustoEstimado { get; set; }
    public string? Descricao { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? ExcluidoEm { get; set; }
    [JsonIgnore] public Pessoa? Profissional { get; set; }

    public Regularizacao Copia() => (Regularizacao)MemberwiseClone();
}

[Table("usuarios")]
public class Usuario
{
    public Guid Id { get; set; }
    public string Nome { get; set; } = "";
    public string Email { get; set; } = "";
    [JsonIgnore] public string SenhaHash { get; set; } = "";
    public Guid PerfilId { get; set; }
    public bool Ativo { get; set; } = true;
    [JsonIgnore] public string? TotpSecret { get; set; }
    public bool TotpAtivo { get; set; }
    /// <summary>Último intervalo TOTP (30 s) aceito: códigos desse intervalo ou anteriores são recusados.</summary>
    [JsonIgnore] public long? TotpUltimoPasso { get; set; }
    public int TentativasFalhas { get; set; }
    public DateTime? BloqueadoAte { get; set; }
    public DateTime? UltimoLoginEm { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? ExcluidoEm { get; set; }
    [JsonIgnore] public Perfil? Perfil { get; set; }
}

[Table("perfis")]
public class Perfil
{
    public Guid Id { get; set; }
    public string Nome { get; set; } = "";
    public string? Descricao { get; set; }
    /// <summary>true = só enxerga registros pelos quais é responsável (Corretor).</summary>
    public bool EscopoProprio { get; set; }
}

[Table("permissoes")]
public class Permissao
{
    public Guid Id { get; set; }
    public string Codigo { get; set; } = "";
    public string? Descricao { get; set; }
}

[Table("perfil_permissoes")]
public class PerfilPermissao
{
    public Guid PerfilId { get; set; }
    public Guid PermissaoId { get; set; }
    [JsonIgnore] public Permissao? Permissao { get; set; }
}

/// <summary>Trilha de auditoria. O banco bloqueia UPDATE/DELETE (trigger trg_audit_imutavel).</summary>
[Table("audit_log")]
public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UsuarioId { get; set; }
    public string Entidade { get; set; } = "";
    public Guid? EntidadeId { get; set; }
    public string Acao { get; set; } = "";
    public string? ValoresAntes { get; set; }
    public string? ValoresDepois { get; set; }
    public string? Ip { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}
