using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Comum;
using Inovabens.Core.Dominio;

namespace Inovabens.Core.Imoveis;

/* ------------------------------ Consultas ------------------------------ */

public sealed record FiltroImoveis
{
    public string? Termo { get; init; }
    public SituacaoImovel? Situacao { get; init; }
    public FinalidadeImovel? Finalidade { get; init; }
    public string? Tipo { get; init; }
    public string? Cidade { get; init; }
    public Guid? CorretorId { get; init; }
    public int Pagina { get; init; } = 1;
    public int TamanhoPagina { get; init; } = 20;
}

public sealed record ImovelListaItem(
    Guid Id, string Codigo, string Titulo, string Tipo, SituacaoImovel Situacao, FinalidadeImovel Finalidade,
    string? Cidade, string? Bairro, string? Uf, decimal? PrecoVenda, decimal? PrecoLocacao, string? CorretorNome);

/// <param name="Url">Endereço no próprio sistema (o arquivo vem do Drive pelo servidor, com verificação de acesso).</param>
/// <param name="UrlPublica">Endereço da cópia no R2 (site), quando publicada e com domínio público configurado.</param>
public sealed record MidiaDetalhe(ImovelMidia Midia, string? Url, string? UrlPublica = null)
{
    public string? NomeSubpasta => Midia.Subpasta is { } s && SubpastasDrive.Valida(s) ? SubpastasDrive.Nome(s) : null;
    public SituacaoPublicacao Publicacao => PublicacaoFotosService.Situacao(Midia);
}

/// <summary>Arquivo de mídia pronto para ser entregue ao navegador.</summary>
public sealed record ArquivoMidia(byte[] Dados, string ContentType, string Nome, bool Download);

public sealed record ProprietarioDetalhe(Guid PessoaId, string PessoaNome, decimal Percentual, bool Principal, DateOnly? InicioEm, DateOnly? FimEm);

public sealed record EnvolvidoDetalhe(Guid PessoaId, string PessoaNome, string Papel, string PapelNome, string? Observacao);

public sealed record ImovelDetalhe(
    Imovel Dados,
    string? CorretorNome,
    IReadOnlyList<ImovelCaracteristica> Caracteristicas,
    IReadOnlyList<MidiaDetalhe> Midias,
    IReadOnlyList<ProprietarioDetalhe> Proprietarios,
    IReadOnlyList<EnvolvidoDetalhe> Envolvidos,
    string? LinkDrive = null);

public sealed record RegularizacaoDetalhe(Regularizacao Dados, string? ProfissionalNome);

/* ------------------------------ Entradas ------------------------------ */

/// <summary>Formulário do imóvel (mesmas regras do schema Zod imovelCriar da API Node).</summary>
public sealed class ImovelForm : IValidatableObject
{
    public FinalidadeImovel Finalidade { get; set; } = FinalidadeImovel.VENDA;
    public SituacaoImovel Situacao { get; set; } = SituacaoImovel.CAPTACAO;

    [Required(ErrorMessage = "Informe o tipo.")]
    [StringLength(40, MinimumLength = 2, ErrorMessage = "O tipo deve ter de 2 a 40 caracteres.")]
    public string Tipo { get; set; } = "apartamento";

    [StringLength(60)] public string? Subtipo { get; set; }

    [Required(ErrorMessage = "Informe o título.")]
    [StringLength(200, MinimumLength = 3, ErrorMessage = "O título deve ter de 3 a 200 caracteres.")]
    public string Titulo { get; set; } = "";

    /// <summary>Nome da pasta principal do imóvel no Google Drive.</summary>
    [Required(ErrorMessage = "Informe o nome da pasta do imóvel no Drive.")]
    [StringLength(120, MinimumLength = 2, ErrorMessage = "O nome da pasta deve ter de 2 a 120 caracteres.")]
    public string NomeDrive { get; set; } = "";

    public string? DescricaoPublica { get; set; }
    public string? DescricaoInterna { get; set; }

    [StringLength(12, ErrorMessage = "CEP muito longo.")] public string? Cep { get; set; }
    [StringLength(200)] public string? Logradouro { get; set; }
    [StringLength(20)] public string? Numero { get; set; }
    [StringLength(100)] public string? Complemento { get; set; }
    [StringLength(100)] public string? Bairro { get; set; }
    [StringLength(100)] public string? Cidade { get; set; }

    public string? Uf { get; set; }

    [Range(-90, 90, ErrorMessage = "Latitude inválida.")] public double? Latitude { get; set; }
    [Range(-180, 180, ErrorMessage = "Longitude inválida.")] public double? Longitude { get; set; }
    public bool EnderecoPublico { get; set; }

    [StringLength(40)] public string? Matricula { get; set; }
    [StringLength(120)] public string? Cartorio { get; set; }
    [StringLength(40)] public string? InscricaoMunicipal { get; set; }

    public decimal? AreaTotalM2 { get; set; }
    public decimal? AreaConstruidaM2 { get; set; }
    public decimal? AreaUtilM2 { get; set; }

    [Range(0, 999, ErrorMessage = "Use um número entre 0 e 999.")] public int? Quartos { get; set; }
    [Range(0, 999, ErrorMessage = "Use um número entre 0 e 999.")] public int? Suites { get; set; }
    [Range(0, 999, ErrorMessage = "Use um número entre 0 e 999.")] public int? Banheiros { get; set; }
    [Range(0, 999, ErrorMessage = "Use um número entre 0 e 999.")] public int? Vagas { get; set; }

    public decimal? PrecoVenda { get; set; }
    public decimal? PrecoLocacao { get; set; }
    public bool DivulgarSite { get; set; }
    public Guid? CorretorResponsavelId { get; set; }

    [StringLength(20, ErrorMessage = "Referência muito longa (máx. 20).")] public string? Referencia { get; set; }
    public decimal? AreaTerrenoM2 { get; set; }
    public decimal? TerrenoFrenteM { get; set; }
    public decimal? TerrenoFundoM { get; set; }
    public decimal? TerrenoEsquerdaM { get; set; }
    public decimal? TerrenoDireitaM { get; set; }
    [StringLength(60)] public string? Zona { get; set; }
    [StringLength(100)] public string? Regiao { get; set; }
    [StringLength(200)] public string? PontoReferencia { get; set; }
    [StringLength(40)] public string? Pavimento { get; set; }
    [Range(1800, 2100, ErrorMessage = "Ano inválido.")] public int? AnoConstrucao { get; set; }
    [Range(0, 999, ErrorMessage = "Use um número entre 0 e 999.")] public int? Salas { get; set; }
    public bool AceitaPermuta { get; set; }
    public bool EmCondominio { get; set; }
    public decimal? ValorCondominio { get; set; }
    public decimal? ValorIptu { get; set; }
    [StringLength(300)] public string? VideoUrl { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (Texto.Limpo(VideoUrl) is { } v && RegrasImovel.IdYoutube(v) is null)
            yield return new ValidationResult("Cole o link de um vídeo do YouTube.", [nameof(VideoUrl)]);
        foreach (var (nome, valor) in new (string, decimal?)[]
                 {
                     (nameof(TerrenoFrenteM), TerrenoFrenteM), (nameof(TerrenoFundoM), TerrenoFundoM),
                     (nameof(TerrenoEsquerdaM), TerrenoEsquerdaM), (nameof(TerrenoDireitaM), TerrenoDireitaM),
                 })
        {
            if (valor is null) continue;
            if (valor < 0) yield return new ValidationResult("O valor não pode ser negativo.", [nome]);
            else if (valor > 99_999_999.99m) yield return new ValidationResult("Valor muito alto.", [nome]);
            else if (decimal.Round(valor.Value, 2) != valor) yield return new ValidationResult("Use no máximo 2 casas decimais.", [nome]);
        }
        // caracteres que o Windows não aceita em nome de pasta (o Drive para computador sincroniza a pasta)
        if (NomeDrive?.IndexOfAny(RegrasImovel.CaracteresProibidosPasta) >= 0)
            yield return new ValidationResult("O nome da pasta não pode ter / \\ : * ? \" < > |", [nameof(NomeDrive)]);
        if (Texto.Limpo(Uf) is not null && !RegrasImovel.UfValida(Uf))
            yield return new ValidationResult("Selecione uma UF válida.", [nameof(Uf)]);
        if ((Latitude is null) != (Longitude is null))
            yield return new ValidationResult("Informe latitude e longitude.", [nameof(Latitude)]);
        // numeric(12,2) para medidas e numeric(15,2) para dinheiro; nunca negativos
        foreach (var (nome, valor, max) in new (string, decimal?, decimal)[]
                 {
                     (nameof(AreaTotalM2), AreaTotalM2, 9_999_999_999.99m), (nameof(AreaConstruidaM2), AreaConstruidaM2, 9_999_999_999.99m),
                     (nameof(AreaUtilM2), AreaUtilM2, 9_999_999_999.99m), (nameof(AreaTerrenoM2), AreaTerrenoM2, 9_999_999_999.99m),
                     (nameof(PrecoVenda), PrecoVenda, 9_999_999_999_999.99m), (nameof(PrecoLocacao), PrecoLocacao, 9_999_999_999_999.99m),
                     (nameof(ValorCondominio), ValorCondominio, 9_999_999_999_999.99m), (nameof(ValorIptu), ValorIptu, 9_999_999_999_999.99m),
                 })
        {
            if (valor is null) continue;
            if (valor < 0) yield return new ValidationResult("O valor não pode ser negativo.", [nome]);
            else if (valor > max) yield return new ValidationResult("Valor muito alto.", [nome]);
            else if (decimal.Round(valor.Value, 2) != valor) yield return new ValidationResult("Use no máximo 2 casas decimais.", [nome]);
        }
    }

    public static ImovelForm De(Imovel i) => new()
    {
        Finalidade = i.Finalidade, Situacao = i.Situacao, Tipo = i.Tipo, Subtipo = i.Subtipo, Titulo = i.Titulo, NomeDrive = i.NomeDrive ?? "",
        DescricaoPublica = i.DescricaoPublica, DescricaoInterna = i.DescricaoInterna,
        Cep = i.Cep, Logradouro = i.Logradouro, Numero = i.Numero, Complemento = i.Complemento, Bairro = i.Bairro, Cidade = i.Cidade, Uf = i.Uf,
        Latitude = i.Latitude, Longitude = i.Longitude, EnderecoPublico = i.EnderecoPublico,
        Matricula = i.Matricula, Cartorio = i.Cartorio, InscricaoMunicipal = i.InscricaoMunicipal,
        AreaTotalM2 = i.AreaTotalM2, AreaConstruidaM2 = i.AreaConstruidaM2, AreaUtilM2 = i.AreaUtilM2,
        Quartos = i.Quartos, Suites = i.Suites, Banheiros = i.Banheiros, Vagas = i.Vagas,
        PrecoVenda = i.PrecoVenda, PrecoLocacao = i.PrecoLocacao, DivulgarSite = i.DivulgarSite,
        CorretorResponsavelId = i.CorretorResponsavelId,
        Referencia = i.Referencia, AreaTerrenoM2 = i.AreaTerrenoM2,
        TerrenoFrenteM = i.TerrenoFrenteM, TerrenoFundoM = i.TerrenoFundoM, TerrenoEsquerdaM = i.TerrenoEsquerdaM, TerrenoDireitaM = i.TerrenoDireitaM,
        Zona = i.Zona, Regiao = i.Regiao, PontoReferencia = i.PontoReferencia, Pavimento = i.Pavimento,
        AnoConstrucao = i.AnoConstrucao, Salas = i.Salas, AceitaPermuta = i.AceitaPermuta, EmCondominio = i.EmCondominio,
        ValorCondominio = i.ValorCondominio, ValorIptu = i.ValorIptu, VideoUrl = i.VideoUrl,
    };

    /// <summary>Copia para a entidade normalizando: texto vazio vira null, UF em maiúsculas.</summary>
    internal void AplicarEm(Imovel i)
    {
        i.Finalidade = Finalidade; i.Situacao = Situacao;
        i.Tipo = Tipo.Trim(); i.Subtipo = Texto.Limpo(Subtipo); i.Titulo = Titulo.Trim();
        i.NomeDrive = RegrasImovel.NomePasta(NomeDrive);
        i.DescricaoPublica = Texto.Limpo(DescricaoPublica); i.DescricaoInterna = Texto.Limpo(DescricaoInterna);
        i.Cep = Texto.Limpo(Cep); i.Logradouro = Texto.Limpo(Logradouro); i.Numero = Texto.Limpo(Numero);
        i.Complemento = Texto.Limpo(Complemento); i.Bairro = Texto.Limpo(Bairro); i.Cidade = Texto.Limpo(Cidade);
        i.Uf = Texto.Limpo(Uf)?.ToUpperInvariant();
        i.Latitude = Latitude; i.Longitude = Longitude; i.EnderecoPublico = EnderecoPublico;
        i.Matricula = Texto.Limpo(Matricula); i.Cartorio = Texto.Limpo(Cartorio); i.InscricaoMunicipal = Texto.Limpo(InscricaoMunicipal);
        i.AreaTotalM2 = AreaTotalM2; i.AreaConstruidaM2 = AreaConstruidaM2; i.AreaUtilM2 = AreaUtilM2;
        i.Quartos = Quartos; i.Suites = Suites; i.Banheiros = Banheiros; i.Vagas = Vagas;
        i.PrecoVenda = PrecoVenda; i.PrecoLocacao = PrecoLocacao; i.DivulgarSite = DivulgarSite;
        i.Referencia = Texto.Limpo(Referencia)?.ToUpperInvariant(); i.AreaTerrenoM2 = AreaTerrenoM2;
        i.TerrenoFrenteM = TerrenoFrenteM; i.TerrenoFundoM = TerrenoFundoM; i.TerrenoEsquerdaM = TerrenoEsquerdaM; i.TerrenoDireitaM = TerrenoDireitaM;
        i.Zona = Texto.Limpo(Zona); i.Regiao = Texto.Limpo(Regiao); i.PontoReferencia = Texto.Limpo(PontoReferencia);
        i.Pavimento = Texto.Limpo(Pavimento); i.AnoConstrucao = AnoConstrucao; i.Salas = Salas;
        i.AceitaPermuta = AceitaPermuta; i.EmCondominio = EmCondominio; i.ValorCondominio = ValorCondominio; i.ValorIptu = ValorIptu;
        i.VideoUrl = Texto.Limpo(VideoUrl);
    }
}

public sealed class CaracteristicaEntrada
{
    [Required(ErrorMessage = "Informe a característica.")]
    [StringLength(80, ErrorMessage = "Característica muito longa (máx. 80).")]
    public string Chave { get; set; } = "";

    [StringLength(200, ErrorMessage = "Valor muito longo (máx. 200).")]
    public string? Valor { get; set; }

    public bool Publico { get; set; } = true;

    [StringLength(40)] public string? Grupo { get; set; }
}

public sealed class ProprietarioEntrada
{
    public Guid? PessoaId { get; set; }
    public decimal? Percentual { get; set; }
    public bool Principal { get; set; }
    public DateOnly? InicioEm { get; set; }
    public DateOnly? FimEm { get; set; }
}

public sealed class EnvolvidoEntrada
{
    public Guid PessoaId { get; set; }
    /// <summary>Código do perfil de pessoa.</summary>
    public string Papel { get; set; } = "";
    public string? Observacao { get; set; }
}

public sealed class MidiaAlteracao
{
    [StringLength(200)] public string? Legenda { get; set; }
    public int? Ordem { get; set; }
    public bool? Capa { get; set; }
    public bool? Publica { get; set; }
}

public sealed class RegularizacaoForm : IValidatableObject
{
    public TipoRegularizacao Tipo { get; set; } = TipoRegularizacao.DOCUMENTACAO;
    public StatusRegularizacao Status { get; set; } = StatusRegularizacao.NAO_INICIADA;
    public Guid? ProfissionalId { get; set; }
    public DateOnly? PrazoPrevisto { get; set; }
    public DateOnly? ConcluidaEm { get; set; }
    public decimal? CustoEstimado { get; set; }
    public string? Descricao { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (CustoEstimado < 0) yield return new ValidationResult("O valor não pode ser negativo.", [nameof(CustoEstimado)]);
        else if (CustoEstimado is { } c && decimal.Round(c, 2) != c) yield return new ValidationResult("Use no máximo 2 casas decimais.", [nameof(CustoEstimado)]);
    }

    public static RegularizacaoForm De(Regularizacao r) => new()
    {
        Tipo = r.Tipo, Status = r.Status, ProfissionalId = r.ProfissionalId, PrazoPrevisto = r.PrazoPrevisto,
        ConcluidaEm = r.ConcluidaEm, CustoEstimado = r.CustoEstimado, Descricao = r.Descricao,
    };

    internal void AplicarEm(Regularizacao r)
    {
        r.Tipo = Tipo; r.Status = Status; r.ProfissionalId = ProfissionalId; r.PrazoPrevisto = PrazoPrevisto;
        r.ConcluidaEm = ConcluidaEm; r.CustoEstimado = CustoEstimado; r.Descricao = Texto.Limpo(Descricao);
    }
}
