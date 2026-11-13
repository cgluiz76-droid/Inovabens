using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inovabens.Core.Imoveis;

// ---------------- dados extraídos do painel (JSON gerado a partir do backup) ----------------

public sealed record ImbFotoPainel(string Arquivo, string? Legenda);
public sealed record ImbDocumentoPainel(string? Id, string? Texto);

public sealed record ImbImovelPainel
{
    public long Id { get; init; }
    public string? Referencia { get; init; }
    public string? Tipo { get; init; }
    public string? Finalidade { get; init; }
    public bool Ativo { get; init; }
    public DateOnly? CadastradoEm { get; init; }
    public decimal? Preco { get; init; }
    public decimal? ValorM2 { get; init; }
    public decimal? Condominio { get; init; }
    public decimal? Iptu { get; init; }
    public string? Cep { get; init; }
    public string? Logradouro { get; init; }
    public string? Numero { get; init; }
    public string? Complemento { get; init; }
    public string? Unidade { get; init; }
    public string? Andar { get; init; }
    public string? Bairro { get; init; }
    public string? Cidade { get; init; }
    public string? Uf { get; init; }
    public string? Zona { get; init; }
    public string? Regiao { get; init; }
    public string? PontoReferencia { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public bool ExibirEndereco { get; init; }
    public decimal? AreaTotal { get; init; }
    public decimal? AreaPrivativa { get; init; }
    public decimal? AreaConstruida { get; init; }
    public decimal? AreaTerreno { get; init; }
    public decimal? TerrenoFrente { get; init; }
    public decimal? TerrenoFundo { get; init; }
    public decimal? TerrenoEsquerda { get; init; }
    public decimal? TerrenoDireita { get; init; }
    public int? Quartos { get; init; }
    public int? Suites { get; init; }
    public int? Banheiros { get; init; }
    public int? Vagas { get; init; }
    public int? Salas { get; init; }
    public string? Pavimento { get; init; }
    public int? AnoConstrucao { get; init; }
    public bool Permuta { get; init; }
    public bool EmCondominio { get; init; }
    public bool Mobiliado { get; init; }
    public bool Exclusividade { get; init; }
    public string? Averbada { get; init; }
    public string? Escriturada { get; init; }
    public bool AceitaFinanciamento { get; init; }
    public bool ComPlaca { get; init; }
    public bool Autorizacao { get; init; }
    public string? LocalChave { get; init; }
    public string? Disponibilidade { get; init; }
    public string? Captacao { get; init; }
    public string? OutrasInfo { get; init; }
    public string? Descricao { get; init; }
    public string? DescricaoHtml { get; init; }
    public string? Video { get; init; }
    public long? ProprietarioId { get; init; }
    public string? ProprietarioNome { get; init; }
    public long? CorretorId { get; init; }
    public string? SeoTitulo { get; init; }
    public string? SeoDescricao { get; init; }
    public List<string> Caracteristicas { get; init; } = [];
    public List<ImbFotoPainel> Fotos { get; init; } = [];
    public List<ImbDocumentoPainel> Documentos { get; init; } = [];
    public string? Historico { get; init; }
}

public sealed record ImbConjugePainel(string? Nome, string? Cpf, string? Email, string? Celular, DateOnly? Nascimento, string? Profissao);

public sealed record ImbPessoaPainel
{
    public long Id { get; init; }
    public string? Nome { get; init; }
    public bool Juridica { get; init; }
    public string? NomeFantasia { get; init; }
    public string? CpfCnpj { get; init; }
    public string? Rg { get; init; }
    public string? Email { get; init; }
    public string? EmailSecundario { get; init; }
    public List<string> Telefones { get; init; } = [];
    public string? Cep { get; init; }
    public string? Logradouro { get; init; }
    public string? Numero { get; init; }
    public string? Complemento { get; init; }
    public string? Bairro { get; init; }
    public string? Cidade { get; init; }
    public string? Uf { get; init; }
    public DateOnly? Nascimento { get; init; }
    public string? Profissao { get; init; }
    public string? EstadoCivil { get; init; }
    public string? Nacionalidade { get; init; }
    public string? NaturalDe { get; init; }
    public string? Renda { get; init; }
    public string? Financiamento { get; init; }
    public string? Creci { get; init; }
    public ImbConjugePainel? Conjuge { get; init; }
    public bool Ativo { get; init; } = true;
    public List<string> Papeis { get; init; } = [];
    public string? TipoObs { get; init; }
    public string? OutrasInfo { get; init; }
    public DateOnly? ClienteDesde { get; init; }
    public string? Referencia { get; init; }
    public string? Captacao { get; init; }
    public long? CorretorId { get; init; }
    public string? PerfilBusca { get; init; }
}

public sealed record ImbLeadPainel
{
    public long Id { get; init; }
    public string? Lista { get; init; }
    public string? Nome { get; init; }
    public string? Email { get; init; }
    public string? Telefone { get; init; }
    public string? Mensagem { get; init; }
    public string? Origem { get; init; }
    public long? ImovelId { get; init; }
    public string? ReferenciaImovel { get; init; }
    public long? PessoaId { get; init; }
    public long? CorretorId { get; init; }
    public string? Temperatura { get; init; }
    public DateTime? RecebidoEm { get; init; }
}

public sealed record ImbAtividadePainel(string? Tipo, string? Titulo, string? Anotacao, string? Mensagem, string? Canal,
    string? EtapaAnterior, string? EtapaDestino, DateTime? Em, DateTime? Inicio, decimal? Valor, string? Texto = null);

public sealed record ImbNegocioPainel
{
    public long Id { get; init; }
    public string? Titulo { get; init; }
    public long? ClienteId { get; init; }
    public string? ClienteNome { get; init; }
    public string? ClienteTelefone { get; init; }
    public string? ClienteEmail { get; init; }
    public long? ImovelId { get; init; }
    public string? ReferenciaImovel { get; init; }
    public long? CorretorId { get; init; }
    public string? CorretorNome { get; init; }
    public string? Etapa { get; init; }
    public string? Status { get; init; }
    public string? Motivo { get; init; }
    public string? Descricao { get; init; }
    public string? Anotacao { get; init; }
    public decimal? Valor { get; init; }
    public string? Temperatura { get; init; }
    public DateTime? CriadoEm { get; init; }
    public DateTime? AlteradoEm { get; init; }
    public DateTime? FechadoEm { get; init; }
    public DateOnly? Previsao { get; init; }
    public string? Criador { get; init; }
    public List<ImbAtividadePainel> Atividades { get; init; } = [];
}

public sealed record ImbCompromissoPainel
{
    public long Id { get; init; }
    public string? Titulo { get; init; }
    public string? Categoria { get; init; }
    public string? Descricao { get; init; }
    public DateTime? Inicio { get; init; }
    public DateTime? Fim { get; init; }
    public bool DiaTodo { get; init; }
    public bool Concluido { get; init; }
    public long? ClienteId { get; init; }
    public long? ImovelId { get; init; }
    public long? ResponsavelId { get; init; }
    public int? Prioridade { get; init; }
}

public sealed record DadosPainelImobibrasil
{
    public List<ImbImovelPainel> Imoveis { get; init; } = [];
    public List<ImbPessoaPainel> Pessoas { get; init; } = [];
    public List<ImbLeadPainel> Leads { get; init; } = [];
    public List<ImbNegocioPainel> Negocios { get; init; } = [];
    public List<ImbCompromissoPainel> Agenda { get; init; } = [];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<DadosPainelImobibrasil> LerAsync(string arquivo, CancellationToken ct = default)
    {
        await using var f = File.OpenRead(arquivo);
        return await JsonSerializer.DeserializeAsync<DadosPainelImobibrasil>(f, Json, ct)
            ?? throw new InvalidDataException("Arquivo de dados do Imobibrasil vazio.");
    }
}

/// <summary>Contagens e observações de uma importação do painel.</summary>
public sealed class RelatorioPainel
{
    public SortedDictionary<string, int> Contagens { get; } = [];
    public List<string> Avisos { get; } = [];
    public void Contar(string chave, int n = 1) => Contagens[chave] = Contagens.GetValueOrDefault(chave) + n;
}

/// <summary>
/// Importa o painel do Imobibrasil (imóveis ativos e inativos com dados internos e fotos, pessoas, proprietários, leads,
/// negócios com o histórico de atendimento e agenda) a partir do JSON extraído do backup. Nada é alterado no Imobibrasil.
/// Rodar de novo completa o que faltou sem duplicar (ids do painel gravados em imobibrasil_id / imobibrasil_ref).
/// Imóvel que já existe no sistema só recebe o que está vazio aqui e os dados internos; o cadastro feito no sistema vale.
/// </summary>
public sealed class ImportacaoPainelImobibrasilService(
    IDbContextFactory<InovabensDb> fabrica, ImoveisService imoveis, CepService cep, AuditService audit,
    ILogger<ImportacaoPainelImobibrasilService> log)
{
    public const string SiteFotos = "https://www.inovabens.com.br/imagens/imoveis/";
    private static readonly HttpClient Http = CriarHttp();
    private static readonly TimeSpan FusoBrasilia = TimeSpan.FromHours(3);

    private static HttpClient CriarHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Inovabens-Importacao/1.0");
        return h;
    }

    private sealed class Contexto(UsuarioAtual u, DadosPainelImobibrasil d, RelatorioPainel rel, Action<string> progresso)
    {
        public UsuarioAtual U { get; } = u;
        public DadosPainelImobibrasil D { get; } = d;
        public RelatorioPainel Rel { get; } = rel;
        public Action<string> Progresso { get; } = progresso;
        public Dictionary<long, Guid> PessoaPorId { get; } = [];
        public Dictionary<long, Guid> ImovelPorId { get; } = [];
        public Dictionary<long, string> Corretores { get; } = [];
        public Dictionary<long, FinalidadeImovel> FinalidadePorImovel { get; } = [];
    }

    public async Task<RelatorioPainel> ImportarAsync(UsuarioAtual u, DadosPainelImobibrasil d, bool comFotos,
        Action<string>? progresso = null, CancellationToken ct = default)
    {
        var c = new Contexto(u, d, new RelatorioPainel(), progresso ?? (_ => { }));
        foreach (var p in d.Pessoas.Where(p => p.Papeis.Contains("Corretor") && p.Nome is not null)) c.Corretores[p.Id] = p.Nome!;

        await PessoasAsync(c, ct);
        // ativos primeiro: num código repetido no painel, o anúncio ativo fica com o código
        foreach (var im in d.Imoveis.OrderByDescending(i => i.Ativo).ThenBy(i => i.CadastradoEm).ThenBy(i => i.Id))
        {
            try { await ImovelAsync(c, im, comFotos, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Falha ao importar o imóvel {Id} ({Ref}) do painel", im.Id, im.Referencia);
                c.Rel.Avisos.Add($"Imóvel {im.Referencia ?? im.Id.ToString(CultureInfo.InvariantCulture)}: falhou ({e.Message})");
                c.Rel.Contar("imóveis com falha");
            }
        }
        await NegociosAsync(c, ct);
        await LeadsAsync(c, ct);
        await AgendaAsync(c, ct);
        return c.Rel;
    }

    // ------------------------------------------------ pessoas ------------------------------------------------

    private async Task PessoasAsync(Contexto c, CancellationToken ct)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var existentes = await db.Pessoas.Include(p => p.Papeis).Where(p => p.ExcluidoEm == null).ToListAsync(ct);
        var proprietarios = c.D.Imoveis.Where(i => i.ProprietarioId is not null).Select(i => i.ProprietarioId!.Value).ToHashSet();
        var agora = DateTime.UtcNow;
        foreach (var d in c.D.Pessoas)
        {
            var nome = Corta(d.Nome, 200) ?? $"Pessoa {d.Id} (Imobibrasil)";
            var fones = d.Telefones.Select(Formatos.TelefoneDigitos).Where(Formatos.TelefoneValido).Distinct().ToList();
            var email = EmailValido(d.Email);
            var doc = Texto.Digitos(d.CpfCnpj) is { Length: 11 or 14 } dg ? d.CpfCnpj!.Trim() : null;

            var p = existentes.FirstOrDefault(x => x.ImobibrasilId == d.Id);
            var como = "já importada";
            if (p is null && doc is not null) { p = existentes.FirstOrDefault(x => Texto.Digitos(x.CpfCnpj) == Texto.Digitos(doc)); como = "mesmo CPF/CNPJ"; }
            if (p is null && email is not null) { p = existentes.FirstOrDefault(x => string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase)); como = "mesmo e-mail"; }
            if (p is null && fones.Count > 0)
            {
                p = existentes.FirstOrDefault(x => x.ImobibrasilId is null
                    && (fones.Contains(Formatos.TelefoneDigitos(x.Telefone)) || fones.Contains(Formatos.TelefoneDigitos(x.Whatsapp)))
                    && MesmoPrimeiroNome(x.NomeRazao, nome));
                como = "mesmo telefone";
            }

            var papeis = Papeis(d, proprietarios.Contains(d.Id));
            var obs = ObservacoesPessoa(c, d);
            if (p is null)
            {
                p = new Pessoa
                {
                    Id = Guid.NewGuid(), Tipo = d.Juridica ? TipoPessoa.PJ : TipoPessoa.PF, NomeRazao = nome, ImobibrasilId = d.Id,
                    CpfCnpj = doc is not null && existentes.All(x => Texto.Digitos(x.CpfCnpj) != Texto.Digitos(doc)) ? doc : null,
                    Rg = Corta(d.Rg, 30), Email = email is not null && existentes.All(x => !string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase)) ? email : null,
                    Telefone = fones.Count > 0 ? Formatos.TelefoneFormatado(fones[0]) : null,
                    Whatsapp = fones.FirstOrDefault(f => f!.Length == 11) is { } cel ? Formatos.TelefoneFormatado(cel) : null,
                    Cep = CepService.Digitos(d.Cep) is { } cd ? CepService.Formatado(cd) : null, Logradouro = Corta(d.Logradouro, 200),
                    Numero = Corta(d.Numero, 20), Complemento = Corta(d.Complemento, 100), Bairro = Corta(d.Bairro, 100),
                    Cidade = Corta(d.Cidade, 100), Uf = RegrasImovel.UfValida(d.Uf) ? d.Uf : null,
                    Observacoes = obs, CriadoPor = c.U.Id,
                    CriadoEm = d.ClienteDesde is { } desde ? desde.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc) : agora, AtualizadoEm = agora,
                };
                p.Papeis = [.. papeis.Select(x => new PessoaPapel { PessoaId = p.Id, Papel = x.Papel, Especialidade = x.Especialidade })];
                db.Pessoas.Add(p);
                existentes.Add(p);
                audit.Registrar(db, c.U.Id, "pessoas", p.Id, "CRIAR", depois: p);
                c.Rel.Contar("pessoas novas");
            }
            else
            {
                var antes = p.Copia();
                p.ImobibrasilId ??= d.Id;
                if (p.Email is null && email is not null && existentes.All(x => x == p || !string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase))) p.Email = email;
                if (p.Telefone is null && fones.Count > 0) p.Telefone = Formatos.TelefoneFormatado(fones[0]);
                if (p.Whatsapp is null && fones.FirstOrDefault(f => f!.Length == 11) is { } cel) p.Whatsapp = Formatos.TelefoneFormatado(cel);
                if (p.CpfCnpj is null && doc is not null && existentes.All(x => Texto.Digitos(x.CpfCnpj) != Texto.Digitos(doc))) p.CpfCnpj = doc;
                p.Rg ??= Corta(d.Rg, 30);
                var marcador = Marcador("pessoa", d.Id);
                if (!(p.Observacoes ?? "").Contains(marcador, StringComparison.Ordinal))
                    p.Observacoes = string.IsNullOrWhiteSpace(p.Observacoes) ? obs : p.Observacoes.TrimEnd() + "\n\n" + obs;
                foreach (var (papel, esp) in papeis.Where(x => p.Papeis.All(y => y.Papel != x.Papel)))
                    p.Papeis.Add(new PessoaPapel { PessoaId = p.Id, Papel = papel, Especialidade = esp });
                p.AtualizadoEm = agora;
                audit.Registrar(db, c.U.Id, "pessoas", p.Id, "ATUALIZAR", antes, p);
                if (como != "já importada") c.Rel.Avisos.Add($"Pessoa \"{nome}\" (painel {d.Id}) juntada com \"{p.NomeRazao}\" ({como}).");
                c.Rel.Contar(como == "já importada" ? "pessoas já importadas" : "pessoas juntadas com cadastro existente");
            }
            c.PessoaPorId[d.Id] = p.Id;
        }
        await db.SaveChangesAsync(ct);
        c.Progresso($"Pessoas: {c.PessoaPorId.Count} de {c.D.Pessoas.Count}.");
    }

    private static List<(string Papel, string? Especialidade)> Papeis(ImbPessoaPainel d, bool proprietario)
    {
        var r = new List<(string, string?)>();
        void Add(string papel, string? esp = null) { if (r.All(x => x.Item1 != papel)) r.Add((papel, esp)); }
        foreach (var x in d.Papeis)
        {
            switch (x)
            {
                case "Proprietário": Add("PROPRIETARIO"); break;
                case "Corretor": Add("PARCEIRO", "Corretor"); break;
                case "Outros": Add("TERCEIRO"); break;
                default: Add("CLIENTE"); break; // Cliente, Locatário, Interessado
            }
        }
        if (proprietario) Add("PROPRIETARIO");
        if (r.Count == 0) Add("CLIENTE");
        return r;
    }

    private static string ObservacoesPessoa(Contexto c, ImbPessoaPainel d)
    {
        var sb = new StringBuilder(Marcador("pessoa", d.Id)).Append(" Importado do Imobibrasil em ").Append(DatasBR.Hoje().ToString("dd/MM/yyyy", Texto.PtBr));
        void L(string rotulo, string? valor) { if (!string.IsNullOrWhiteSpace(valor)) sb.Append('\n').Append(rotulo).Append(": ").Append(valor.Trim()); }
        L("Situação no Imobibrasil", d.Ativo ? null : "Inativo");
        L("Tipo no Imobibrasil", d.Papeis.Count > 0 ? string.Join(", ", d.Papeis) : null);
        L("Nome fantasia", d.NomeFantasia);
        L("Telefones", d.Telefones.Count > 1 ? string.Join(" · ", d.Telefones.Select(t => Formatos.TelefoneFormatado(t) ?? t)) : null);
        L("Telefone (não reconhecido)", d.Telefones.Count == 1 && !Formatos.TelefoneValido(Formatos.TelefoneDigitos(d.Telefones[0])) ? d.Telefones[0] : null);
        L("E-mail", d.Email is not null && EmailValido(d.Email) is null ? d.Email : null);
        L("E-mail secundário", d.EmailSecundario);
        L("Nascimento", d.Nascimento?.ToString("dd/MM/yyyy", Texto.PtBr));
        L("Profissão", d.Profissao);
        L("Estado civil", d.EstadoCivil);
        L("Nacionalidade", d.Nacionalidade);
        L("Natural de", d.NaturalDe);
        L("Renda", d.Renda);
        L("Financiamento", d.Financiamento);
        L("CRECI", d.Creci);
        if (d.Conjuge is { } cj)
            L("Cônjuge", string.Join(" · ", new[] { cj.Nome, cj.Cpf is null ? null : "CPF " + cj.Cpf, cj.Email, cj.Celular,
                cj.Nascimento?.ToString("dd/MM/yyyy", Texto.PtBr), cj.Profissao }.Where(x => !string.IsNullOrWhiteSpace(x))));
        L("Cliente desde", d.ClienteDesde?.ToString("dd/MM/yyyy", Texto.PtBr));
        L("Captação", d.Captacao);
        L("Referência", d.Referencia);
        L("Corretor responsável", d.CorretorId is { } cr ? c.Corretores.GetValueOrDefault(cr, $"corretor {cr}") : null);
        L("Observação do tipo", d.TipoObs);
        L("Perfil de busca", d.PerfilBusca);
        L("Outras informações", d.OutrasInfo);
        return sb.ToString();
    }

    // ------------------------------------------------ imóveis ------------------------------------------------

    private async Task ImovelAsync(Contexto c, ImbImovelPainel d, bool comFotos, CancellationToken ct)
    {
        Imovel? existente;
        await using (var db = await fabrica.CreateDbContextAsync(ct))
        {
            existente = await db.Imoveis.AsNoTracking().FirstOrDefaultAsync(i => i.ImobibrasilId == d.Id && i.ExcluidoEm == null, ct);
            if (existente is null && d.Referencia is { } r)
                existente = await db.Imoveis.AsNoTracking().FirstOrDefaultAsync(i => i.ImobibrasilId == null && i.ExcluidoEm == null
                    && (i.Codigo == r.ToUpper() || i.Referencia == r.ToUpper()), ct);
        }
        var finalidade = Locacao(d) ? FinalidadeImovel.LOCACAO : FinalidadeImovel.VENDA;
        c.FinalidadePorImovel[d.Id] = finalidade;
        var interno = DescricaoInterna(c, d);
        var avisos = new List<string>();

        Guid id;
        if (existente is null)
        {
            var (cidade, uf) = (d.Cidade, d.Uf);
            if (cidade is null && await cep.ConsultarAsync(d.Cep, ct) is (ResultadoCep.Encontrado, { } e)) (cidade, uf) = (e.Cidade, e.Uf);
            var anuncio = new AnuncioImobibrasil
            {
                Id = d.Id, Referencia = d.Referencia, TipoOriginal = d.Tipo ?? "Imóvel", Locacao = finalidade == FinalidadeImovel.LOCACAO,
                Bairro = Bairro(d.Bairro), Cidade = cidade, Chamada = d.Descricao?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(),
            };
            var tipo = ImportacaoImobibrasilService.TipoSistema(d.Tipo ?? "");
            var form = new ImovelForm
            {
                Finalidade = finalidade, Situacao = d.Ativo ? SituacaoImovel.DISPONIVEL : SituacaoImovel.ARQUIVADO,
                Tipo = tipo, Subtipo = string.Equals(tipo, d.Tipo, StringComparison.OrdinalIgnoreCase) ? null : Corta(d.Tipo, 60),
                Titulo = ImportacaoImobibrasilService.Titulo(anuncio), NomeDrive = ImportacaoImobibrasilService.NomeDrive(anuncio),
                DescricaoPublica = d.Descricao, DescricaoInterna = interno,
                Cep = CepService.Digitos(d.Cep) is { } cd ? CepService.Formatado(cd) : null, Logradouro = Corta(d.Logradouro, 200),
                Numero = Corta(d.Numero, 20), Complemento = Corta(Juntar(d.Complemento, d.Unidade is null ? null : "Unidade " + d.Unidade,
                    d.Andar is null ? null : d.Andar + "º andar"), 100),
                Bairro = Corta(Bairro(d.Bairro), 100), Cidade = Corta(cidade, 100), Uf = RegrasImovel.UfValida(uf) ? uf : null,
                Zona = Corta(d.Zona, 60), Regiao = Corta(d.Regiao, 100), PontoReferencia = Corta(d.PontoReferencia, 200),
                Latitude = d.Latitude is { } la && d.Longitude is not null ? la : null, Longitude = d.Latitude is not null ? d.Longitude : null,
                EnderecoPublico = d.ExibirEndereco,
                AreaTotalM2 = M(d.AreaTotal), AreaUtilM2 = M(d.AreaPrivativa), AreaConstruidaM2 = M(d.AreaConstruida), AreaTerrenoM2 = M(d.AreaTerreno),
                TerrenoFrenteM = M(d.TerrenoFrente), TerrenoFundoM = M(d.TerrenoFundo), TerrenoEsquerdaM = M(d.TerrenoEsquerda), TerrenoDireitaM = M(d.TerrenoDireita),
                Quartos = d.Quartos, Suites = d.Suites, Banheiros = d.Banheiros, Vagas = d.Vagas, Salas = d.Salas,
                Pavimento = Corta(d.Pavimento, 40), AnoConstrucao = d.AnoConstrucao is >= 1800 and <= 2100 ? d.AnoConstrucao : null,
                PrecoVenda = finalidade == FinalidadeImovel.VENDA ? M(d.Preco) : null, PrecoLocacao = finalidade == FinalidadeImovel.LOCACAO ? M(d.Preco) : null,
                ValorCondominio = M(d.Condominio), ValorIptu = M(d.Iptu), AceitaPermuta = d.Permuta, EmCondominio = d.EmCondominio,
                VideoUrl = RegrasImovel.IdYoutube(d.Video) is null ? null : d.Video, DivulgarSite = d.Ativo,
            };
            id = await imoveis.CriarAsync(c.U, form, codigoExterno: d.Referencia);
            c.Rel.Contar(d.Ativo ? "imóveis ativos criados" : "imóveis inativos criados");
        }
        else
        {
            id = existente.Id;
            var form = ImovelForm.De(existente);
            // só completa o que está vazio no sistema
            form.Cep ??= CepService.Digitos(d.Cep) is { } cd ? CepService.Formatado(cd) : null;
            form.Logradouro ??= Corta(d.Logradouro, 200); form.Numero ??= Corta(d.Numero, 20); form.Complemento ??= Corta(d.Complemento, 100);
            form.Cidade ??= Corta(d.Cidade, 100); form.Uf ??= RegrasImovel.UfValida(d.Uf) ? d.Uf : null;
            form.Zona ??= Corta(d.Zona, 60); form.Regiao ??= Corta(d.Regiao, 100); form.PontoReferencia ??= Corta(d.PontoReferencia, 200);
            if (form.Latitude is null && d.Latitude is not null && d.Longitude is not null) (form.Latitude, form.Longitude) = (d.Latitude, d.Longitude);
            form.AreaTotalM2 ??= M(d.AreaTotal); form.AreaUtilM2 ??= M(d.AreaPrivativa); form.AreaConstruidaM2 ??= M(d.AreaConstruida); form.AreaTerrenoM2 ??= M(d.AreaTerreno);
            form.Quartos ??= d.Quartos; form.Suites ??= d.Suites; form.Banheiros ??= d.Banheiros; form.Vagas ??= d.Vagas; form.Salas ??= d.Salas;
            form.ValorCondominio ??= M(d.Condominio); form.ValorIptu ??= M(d.Iptu);
            if (finalidade == FinalidadeImovel.VENDA) form.PrecoVenda ??= M(d.Preco); else form.PrecoLocacao ??= M(d.Preco);
            form.DescricaoPublica ??= d.Descricao;
            if (!(form.DescricaoInterna ?? "").Contains(Marcador("imóvel", d.Id), StringComparison.Ordinal))
                form.DescricaoInterna = string.IsNullOrWhiteSpace(form.DescricaoInterna) ? interno : form.DescricaoInterna.TrimEnd() + "\n\n" + interno;
            await imoveis.AtualizarAsync(c.U, id, form);
            c.Rel.Contar("imóveis existentes completados");
        }

        await using (var db = await fabrica.CreateDbContextAsync(ct))
        {
            var im = await db.Imoveis.SingleAsync(i => i.Id == id, ct);
            im.ImobibrasilId = d.Id;
            var attrs = JsonNode.Parse(string.IsNullOrWhiteSpace(im.Atributos) ? "{}" : im.Atributos) as JsonObject ?? [];
            attrs["imobibrasil"] = JsonSerializer.SerializeToNode(Extras(d), DadosPainelImobibrasil.Json);
            im.Atributos = attrs.ToJsonString();
            if (existente is null && d.CadastradoEm is { } cad) im.CriadoEm = cad.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
            await db.SaveChangesAsync(ct);
            if (d.Referencia is { } refPainel && !string.Equals(im.Codigo, refPainel, StringComparison.OrdinalIgnoreCase))
            {
                avisos.Add($"código {refPainel} já era de outro imóvel; ficou {im.Codigo}");
                c.Rel.Avisos.Add($"Imóvel {refPainel} (painel {d.Id}, {(d.Ativo ? "ativo" : "inativo")}): código repetido no Imobibrasil, recebeu {im.Codigo}.");
            }
        }
        c.ImovelPorId[d.Id] = id;

        // características e proprietário: só quando o imóvel ainda não tem
        await using (var db = await fabrica.CreateDbContextAsync(ct))
        {
            if (d.Caracteristicas.Count > 0 && !await db.ImovelCaracteristicas.AnyAsync(x => x.ImovelId == id, ct))
                await imoveis.SalvarCaracteristicasAsync(c.U, id, [.. d.Caracteristicas.Distinct().Select(x => new CaracteristicaEntrada { Grupo = "Características", Chave = Corta(x, 80)! })]);
            if (d.ProprietarioId is { } pid && c.PessoaPorId.TryGetValue(pid, out var pessoa)
                && !await db.ImovelProprietarios.AnyAsync(x => x.ImovelId == id, ct))
                await imoveis.SalvarProprietariosAsync(c.U, id, [new ProprietarioEntrada { PessoaId = pessoa, Percentual = 100, Principal = true }]);
        }

        var fotos = 0;
        if (comFotos && d.Fotos.Count > 0)
        {
            HashSet<string> jaTem;
            await using (var db = await fabrica.CreateDbContextAsync(ct))
                jaTem = [.. await db.ImovelMidias.Where(m => m.ImovelId == id && m.NomeOriginal != null).Select(m => m.NomeOriginal!).ToListAsync(ct)];
            foreach (var f in d.Fotos.Take(RegrasImovel.MaximoFotos))
            {
                if (jaTem.Contains(f.Arquivo)) continue;
                try
                {
                    using var resp = await Http.GetAsync(SiteFotos + Uri.EscapeDataString(f.Arquivo), ct);
                    resp.EnsureSuccessStatusCode();
                    var tipo = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                    await using var corpo = new MemoryStream(await resp.Content.ReadAsByteArrayAsync(ct));
                    await imoveis.EnviarMidiaAsync(c.U, id, corpo, f.Arquivo, tipo, SubpastasDrive.FotosVideos,
                        publica: d.Ativo, legenda: Corta(f.Legenda, 200), ct: ct, melhorar: false);
                    fotos++;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    avisos.Add($"foto {f.Arquivo} não baixada ({ex.Message})");
                    c.Rel.Contar("fotos com falha");
                }
            }
            c.Rel.Contar("fotos enviadas", fotos);
        }
        string codigo;
        await using (var db = await fabrica.CreateDbContextAsync(ct))
            codigo = await db.Imoveis.Where(i => i.Id == id).Select(i => i.Codigo).SingleAsync(ct);
        c.Progresso($"{codigo} ← {d.Referencia ?? "(sem ref.)"} {(d.Ativo ? "ativo" : "inativo")}{(existente is null ? " novo" : " completado")}, {fotos} fotos"
            + (avisos.Count == 0 ? "" : " · " + string.Join("; ", avisos)));
    }

    private static bool Locacao(ImbImovelPainel d) => d.Finalidade?.StartsWith("Loca", StringComparison.OrdinalIgnoreCase) == true
                                                  || d.Finalidade?.StartsWith("Alug", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>"Nova Ilha, opções variadas de salas" → "Nova Ilha" (o painel usa o bairro como chamada).</summary>
    private static string? Bairro(string? b) => Texto.Limpo(b?.Split(',')[0]);

    private static string DescricaoInterna(Contexto c, ImbImovelPainel d)
    {
        var sb = new StringBuilder(Marcador("imóvel", d.Id)).Append(" Importado do painel do Imobibrasil em ")
            .Append(DatasBR.Hoje().ToString("dd/MM/yyyy", Texto.PtBr)).Append(d.Ativo ? " (ativo lá)." : " (inativo lá).");
        void L(string rotulo, string? valor) { if (!string.IsNullOrWhiteSpace(valor)) sb.Append('\n').Append(rotulo).Append(": ").Append(valor.Trim()); }
        L("Referência no Imobibrasil", d.Referencia);
        L("Cadastrado no Imobibrasil", d.CadastradoEm?.ToString("dd/MM/yyyy", Texto.PtBr));
        L("Tipo no Imobibrasil", d.Tipo);
        L("Proprietário", d.ProprietarioNome);
        L("Corretor", d.CorretorId is { } cr ? c.Corretores.GetValueOrDefault(cr, $"corretor {cr}") : null);
        L("Captação", d.Captacao);
        L("Disponibilidade", d.Disponibilidade);
        L("Exclusividade", d.Exclusividade ? "Sim" : null);
        L("Autorização", d.Autorizacao ? "Sim" : null);
        L("Com placa", d.ComPlaca ? "Sim" : null);
        L("Mobiliado", d.Mobiliado ? "Sim" : null);
        L("Aceita financiamento", d.AceitaFinanciamento ? "Sim" : null);
        L("Averbada", d.Averbada switch { "S" => "Sim", "N" => "Não", _ => null });
        L("Escriturada", d.Escriturada switch { "S" => "Sim", "N" => "Não", _ => null });
        L("Local da chave", d.LocalChave);
        L("Valor do m²", d.ValorM2?.ToString("N2", Texto.PtBr));
        L("Unidade", d.Unidade);
        L("Andar", d.Andar);
        L("Bairro no Imobibrasil", d.Bairro);
        L("Vídeo", d.Video is not null && RegrasImovel.IdYoutube(d.Video) is null ? d.Video : null);
        L("Outras informações", d.OutrasInfo);
        if (d.Documentos.Count > 0)
            L("Documentos anexados no Imobibrasil (baixar à parte)", string.Join("; ", d.Documentos.Select(x => x.Texto)));
        if (d.Fotos.Count > RegrasImovel.MaximoFotos) L("Fotos acima do limite", $"{d.Fotos.Count - RegrasImovel.MaximoFotos} ficaram de fora");
        return sb.ToString();
    }

    private static object Extras(ImbImovelPainel d) => new
    {
        id = d.Id, referencia = d.Referencia, ativo = d.Ativo, tipo = d.Tipo, finalidade = d.Finalidade, cadastradoEm = d.CadastradoEm,
        proprietarioId = d.ProprietarioId, corretorId = d.CorretorId, captacao = d.Captacao, disponibilidade = d.Disponibilidade,
        exclusividade = d.Exclusividade, autorizacao = d.Autorizacao, comPlaca = d.ComPlaca, mobiliado = d.Mobiliado,
        aceitaFinanciamento = d.AceitaFinanciamento, averbada = d.Averbada, escriturada = d.Escriturada, localChave = d.LocalChave,
        seoTitulo = d.SeoTitulo, seoDescricao = d.SeoDescricao, descricaoHtml = d.DescricaoHtml, historico = d.Historico,
        fotos = d.Fotos.Select(f => f.Arquivo), documentos = d.Documentos,
    };

    // ------------------------------------------------ negócios e leads ------------------------------------------------

    private async Task NegociosAsync(Contexto c, CancellationToken ct)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var estagios = await db.FunilEstagios.Where(e => e.Ativo).OrderBy(e => e.Ordem).ToListAsync(ct);
        var ja = (await db.Leads.Where(l => l.ImobibrasilRef != null).Select(l => l.ImobibrasilRef!).ToListAsync(ct)).ToHashSet();
        foreach (var n in c.D.Negocios.OrderBy(n => n.CriadoEm))
        {
            var refPainel = "negocio:" + n.Id.ToString(CultureInfo.InvariantCulture);
            if (ja.Contains(refPainel)) { c.Rel.Contar("negócios já importados"); continue; }
            if (n.ClienteId is null && n.ClienteNome is null)
            {
                // o painel cria um "negócio" sem cliente ao desativar o imóvel (motivo: vendido, desistência...):
                // vira memorando do imóvel, não lead
                await EncerramentoAsync(c, db, n, ct);
                continue;
            }
            var pessoa = await PessoaAsync(c, db, n.ClienteId, n.ClienteNome, n.ClienteTelefone, n.ClienteEmail, ct);
            var funil = n.ImovelId is { } im && c.FinalidadePorImovel.GetValueOrDefault(im) == FinalidadeImovel.LOCACAO ? Funil.LOCACAO : Funil.VENDA;
            var estagio = Estagio(estagios, funil, n.Status, n.Etapa);
            var criado = Utc(n.CriadoEm) ?? DateTime.UtcNow;
            var obs = new StringBuilder(Marcador("negócio", n.Id)).Append(' ').Append(n.Titulo ?? "Negócio").Append(" (Imobibrasil)");
            void L(string r, string? v) { if (!string.IsNullOrWhiteSpace(v)) obs.Append('\n').Append(r).Append(": ").Append(v.Trim()); }
            L("Etapa no Imobibrasil", n.Etapa); L("Situação", n.Status); L("Imóvel", n.ReferenciaImovel);
            L("Corretor", n.CorretorNome ?? (n.CorretorId is { } cr and > 0 ? c.Corretores.GetValueOrDefault(cr, $"corretor {cr}") : null));
            L("Temperatura", n.Temperatura); L("Valor", n.Valor?.ToString("C0", Texto.PtBr)); L("Previsão", n.Previsao?.ToString("dd/MM/yyyy", Texto.PtBr));
            L("Criado por", n.Criador); L("Descrição", n.Descricao); L("Anotação", n.Anotacao);
            var lead = new Lead
            {
                Id = Guid.NewGuid(), PessoaId = pessoa, Finalidade = funil == Funil.LOCACAO ? FinalidadeLead.LOCACAO : FinalidadeLead.COMPRA,
                Origem = "Imobibrasil", EstagioId = estagio.Id, ImobibrasilRef = refPainel,
                MotivoPerda = estagio.Tipo == TipoEstagio.PERDIDO ? Corta(n.Motivo ?? "Perdido no Imobibrasil", 500) : null,
                Observacoes = obs.ToString(), CriadoEm = criado, AtualizadoEm = Utc(n.AlteradoEm) ?? criado,
            };
            db.Leads.Add(lead);
            db.LeadHistoricoEstagios.Add(new LeadHistoricoEstagio { Id = Guid.NewGuid(), LeadId = lead.Id, EstagioNovoId = estagio.Id, CriadoEm = criado });
            if (n.ImovelId is { } imp && c.ImovelPorId.TryGetValue(imp, out var imovel))
                db.LeadImoveisInteresse.Add(new LeadImovelInteresse { LeadId = lead.Id, ImovelId = imovel, CriadoEm = criado });
            foreach (var a in n.Atividades)
            {
                db.LeadInteracoes.Add(new LeadInteracao
                {
                    Id = Guid.NewGuid(), LeadId = lead.Id, Canal = Canal(a.Canal), Resumo = Resumo(a),
                    CriadoEm = Utc(a.Em) ?? criado,
                });
                c.Rel.Contar("interações de negócios");
            }
            audit.Registrar(db, c.U.Id, "leads", lead.Id, "CRIAR", depois: lead);
            ja.Add(refPainel);
            c.Rel.Contar($"negócios importados ({estagio.Tipo.ToString().ToLowerInvariant()})");
        }
        await db.SaveChangesAsync(ct);
        c.Progresso($"Negócios: {c.D.Negocios.Count} lidos.");
    }

    private async Task EncerramentoAsync(Contexto c, InovabensDb db, ImbNegocioPainel n, CancellationToken ct)
    {
        var marcador = Marcador("negócio", n.Id);
        if (n.ImovelId is not { } imp || !c.ImovelPorId.TryGetValue(imp, out var imovel))
        {
            c.Rel.Contar("encerramentos sem imóvel (não importados)");
            c.Rel.Avisos.Add($"Negócio {n.Id} sem cliente e sem imóvel: {n.Titulo} — {n.Motivo} {n.Descricao}");
            return;
        }
        if (await db.Memorandos.AnyAsync(m => m.ImovelId == imovel && m.Corpo.StartsWith(marcador), ct)) { c.Rel.Contar("negócios já importados"); return; }
        var corpo = new StringBuilder(marcador).Append(' ').Append(n.Status == "GANHO" ? "Imóvel desativado no Imobibrasil como negócio ganho." : "Imóvel desativado no Imobibrasil como negócio perdido.");
        void L(string r, string? v) { if (!string.IsNullOrWhiteSpace(v)) corpo.Append('\n').Append(r).Append(": ").Append(v.Trim()); }
        L("Data", (n.FechadoEm ?? n.CriadoEm)?.ToString("dd/MM/yyyy HH:mm", Texto.PtBr));
        L("Motivo", n.Motivo); L("Descrição", n.Descricao); L("Anotação", n.Anotacao);
        L("Corretor", n.CorretorNome ?? (n.CorretorId is { } cr and > 0 ? c.Corretores.GetValueOrDefault(cr, $"corretor {cr}") : null));
        foreach (var a in n.Atividades.Where(a => a.Texto is not null)) corpo.Append('\n').Append(a.Texto);
        var criado = Utc(n.FechadoEm ?? n.CriadoEm) ?? DateTime.UtcNow;
        var m = new Memorando
        {
            Id = Guid.NewGuid(), ImovelId = imovel, AutorId = c.U.Id, Tipo = TipoMemorando.MEMORANDO,
            Titulo = n.Status == "GANHO" ? "Imobibrasil: desativado (negócio ganho)" : "Imobibrasil: desativado (negócio perdido)",
            Corpo = corpo.ToString(), CriadoEm = criado, AtualizadoEm = criado,
        };
        db.Memorandos.Add(m);
        audit.Registrar(db, c.U.Id, "memorandos", m.Id, "CRIAR", depois: m);
        c.Rel.Contar($"encerramentos de imóvel ({(n.Status == "GANHO" ? "ganho" : "perdido")}) como memorando");
    }

    /// <summary>Lixo que chega pelo formulário do site (robôs): não vira lead.</summary>
    public static bool LeadLixo(string? nome, string? mensagem)
    {
        var t = ((nome ?? "") + " " + (mensagem ?? "")).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(nome) || t.Contains("passwd") || t.Contains("dating") || t.Contains("yandex.com/poll")
               || (nome ?? "").Contains("http", StringComparison.OrdinalIgnoreCase) || (nome ?? "").Contains("../", StringComparison.Ordinal);
    }

    private async Task LeadsAsync(Contexto c, CancellationToken ct)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var estagios = await db.FunilEstagios.Where(e => e.Ativo).OrderBy(e => e.Ordem).ToListAsync(ct);
        var ja = (await db.Leads.Where(l => l.ImobibrasilRef != null).Select(l => l.ImobibrasilRef!).ToListAsync(ct)).ToHashSet();
        foreach (var l in c.D.Leads.OrderBy(l => l.RecebidoEm))
        {
            var refPainel = "lead:" + l.Id.ToString(CultureInfo.InvariantCulture);
            var marcador = Marcador("lead", l.Id);
            if (ja.Contains(refPainel) || await db.LeadInteracoes.AnyAsync(i => i.Resumo.StartsWith(marcador), ct))
            {
                c.Rel.Contar("leads já importados");
                continue;
            }
            if (LeadLixo(l.Nome, l.Mensagem))
            {
                c.Rel.Contar("leads ignorados (spam)");
                c.Rel.Avisos.Add($"Lead {l.Id} ignorado como spam: \"{Corta(l.Nome, 60)}\".");
                continue;
            }
            var pessoa = await PessoaAsync(c, db, l.PessoaId, l.Nome, l.Telefone, l.Email, ct);
            var recebido = Utc(l.RecebidoEm) ?? DateTime.UtcNow;
            var resumo = $"{marcador} Mensagem recebida no Imobibrasil ({l.Origem ?? "sem origem"}{(l.ReferenciaImovel is null ? "" : ", imóvel " + l.ReferenciaImovel)}):\n{l.Mensagem ?? "(sem texto)"}";

            // pessoa que já tem negócio importado: a mensagem entra no histórico dele
            var existente = await db.Leads.Where(x => x.PessoaId == pessoa && x.ExcluidoEm == null && x.ImobibrasilRef != null)
                .OrderByDescending(x => x.CriadoEm).FirstOrDefaultAsync(ct)
                ?? db.Leads.Local.Where(x => x.PessoaId == pessoa && x.ImobibrasilRef != null).OrderByDescending(x => x.CriadoEm).FirstOrDefault();
            if (existente is not null && l.Lista is "historico" or "qualificados")
            {
                db.LeadInteracoes.Add(new LeadInteracao { Id = Guid.NewGuid(), LeadId = existente.Id, Canal = CanalOrigem(l.Origem), Resumo = resumo, CriadoEm = recebido });
                c.Rel.Contar("leads anexados a negócios");
                ja.Add(refPainel);
                continue;
            }
            var funil = l.ImovelId is { } im && c.FinalidadePorImovel.GetValueOrDefault(im) == FinalidadeImovel.LOCACAO ? Funil.LOCACAO : Funil.VENDA;
            var estagio = l.Lista == "arquivados" ? Estagio(estagios, funil, "PERDIDO", null) : Estagio(estagios, funil, "ATIVO", "ATENDIMENTO");
            var lead = new Lead
            {
                Id = Guid.NewGuid(), PessoaId = pessoa, Finalidade = funil == Funil.LOCACAO ? FinalidadeLead.LOCACAO : FinalidadeLead.COMPRA,
                Origem = Corta($"Imobibrasil · {l.Origem ?? "lead"}", 80), EstagioId = estagio.Id, ImobibrasilRef = refPainel,
                MotivoPerda = estagio.Tipo == TipoEstagio.PERDIDO ? "Arquivado no Imobibrasil" : null,
                Observacoes = $"{Marcador("lead", l.Id)} Lead do Imobibrasil ({ListaLead(l.Lista)})"
                              + (l.Temperatura is null ? "" : $"\nTemperatura: {l.Temperatura}")
                              + (l.CorretorId is { } cr and > 0 ? $"\nCorretor: {c.Corretores.GetValueOrDefault(cr, $"corretor {cr}")}" : ""),
                CriadoEm = recebido, AtualizadoEm = recebido,
            };
            db.Leads.Add(lead);
            db.LeadHistoricoEstagios.Add(new LeadHistoricoEstagio { Id = Guid.NewGuid(), LeadId = lead.Id, EstagioNovoId = estagio.Id, CriadoEm = recebido });
            db.LeadInteracoes.Add(new LeadInteracao { Id = Guid.NewGuid(), LeadId = lead.Id, Canal = CanalOrigem(l.Origem), Resumo = resumo, CriadoEm = recebido });
            if (l.ImovelId is { } imp && c.ImovelPorId.TryGetValue(imp, out var imovel))
                db.LeadImoveisInteresse.Add(new LeadImovelInteresse { LeadId = lead.Id, ImovelId = imovel, CriadoEm = recebido });
            audit.Registrar(db, c.U.Id, "leads", lead.Id, "CRIAR", depois: lead);
            ja.Add(refPainel);
            c.Rel.Contar($"leads importados ({ListaLead(l.Lista)})");
        }
        await db.SaveChangesAsync(ct);
        c.Progresso($"Leads: {c.D.Leads.Count} lidos.");
    }

    private static string ListaLead(string? lista) => lista switch
    {
        "leads" => "não qualificado", "qualificados" => "qualificado", "arquivados" => "arquivado", "historico" => "histórico", _ => lista ?? "?",
    };

    /// <summary>Pessoa do painel já importada; senão procura pelo contato; senão cria (cliente).</summary>
    private async Task<Guid> PessoaAsync(Contexto c, InovabensDb db, long? idPainel, string? nome, string? telefone, string? email, CancellationToken ct)
    {
        if (idPainel is { } ip && c.PessoaPorId.TryGetValue(ip, out var ja)) return ja;
        var fone = Formatos.TelefoneDigitos(telefone);
        var mail = EmailValido(email);
        var todas = db.Pessoas.Local.Where(p => p.ExcluidoEm == null).ToList();
        var p = todas.FirstOrDefault(x => mail is not null && string.Equals(x.Email, mail, StringComparison.OrdinalIgnoreCase))
                ?? todas.FirstOrDefault(x => Formatos.TelefoneValido(fone) && (Formatos.TelefoneDigitos(x.Telefone) == fone || Formatos.TelefoneDigitos(x.Whatsapp) == fone)
                                             && MesmoPrimeiroNome(x.NomeRazao, nome));
        p ??= await db.Pessoas.FirstOrDefaultAsync(x => x.ExcluidoEm == null && mail != null && x.Email != null && x.Email.ToLower() == mail.ToLower(), ct);
        if (p is null && Formatos.TelefoneValido(fone))
        {
            var fmt = Formatos.TelefoneFormatado(fone);
            p = (await db.Pessoas.Where(x => x.ExcluidoEm == null && (x.Telefone == fmt || x.Whatsapp == fmt)).ToListAsync(ct))
                .FirstOrDefault(x => MesmoPrimeiroNome(x.NomeRazao, nome));
        }
        if (p is null)
        {
            p = new Pessoa
            {
                Id = Guid.NewGuid(), NomeRazao = Corta(nome, 200) ?? "Contato do Imobibrasil", Email = mail,
                Telefone = Formatos.TelefoneValido(fone) ? Formatos.TelefoneFormatado(fone) : null,
                Whatsapp = Formatos.TelefoneValido(fone) && fone!.Length == 11 ? Formatos.TelefoneFormatado(fone) : null,
                Observacoes = "Contato vindo de lead/negócio do Imobibrasil." + (Formatos.TelefoneValido(fone) || telefone is null ? "" : $"\nTelefone informado: {telefone}"),
                CriadoPor = c.U.Id, ImobibrasilId = idPainel is > 0 && !c.PessoaPorId.ContainsKey(idPainel.Value) ? idPainel : null,
            };
            p.Papeis = [new PessoaPapel { PessoaId = p.Id, Papel = "CLIENTE" }];
            db.Pessoas.Add(p);
            audit.Registrar(db, c.U.Id, "pessoas", p.Id, "CRIAR", depois: p);
            c.Rel.Contar("pessoas novas (de leads)");
        }
        if (idPainel is > 0) c.PessoaPorId[idPainel.Value] = p.Id;
        return p.Id;
    }

    /// <summary>Etapa do Imobibrasil → etapa do funil do sistema (pela posição entre as etapas abertas).</summary>
    private static FunilEstagio Estagio(List<FunilEstagio> estagios, Funil funil, string? status, string? etapa)
    {
        var doFunil = estagios.Where(e => e.Funil == funil).ToList();
        if (doFunil.Count == 0) throw new RegraNegocioException("funil", $"Funil {funil} sem etapas ativas.");
        FunilEstagio? Tipo(TipoEstagio t) => doFunil.FirstOrDefault(e => e.Tipo == t);
        if (status == "GANHO" || etapa == "GANHO") return Tipo(TipoEstagio.GANHO) ?? doFunil[^1];
        if (status == "PERDIDO" || etapa == "PERDIDO") return Tipo(TipoEstagio.PERDIDO) ?? doFunil[^1];
        var abertos = doFunil.Where(e => e.Tipo == TipoEstagio.ABERTO).ToList();
        var pos = (etapa ?? "").ToUpperInvariant() switch
        {
            "QUALIFICAÇÃO" or "NUTRIÇÃO" => 1,
            "AGENDAMENTO" or "COMPARECIMENTO" => 2,
            "NEGOCIAÇÃO" => 4,
            "FORMALIZAÇÃO" => 5,
            _ => 0,
        };
        return abertos.Count == 0 ? doFunil[0] : abertos[Math.Min(pos, abertos.Count - 1)];
    }

    private static CanalInteracao Canal(string? canal) => (canal ?? "").ToLowerInvariant() switch
    {
        "whatsapp" => CanalInteracao.WHATSAPP,
        "telefone" => CanalInteracao.TELEFONE,
        "e-mail" or "email" => CanalInteracao.EMAIL,
        "presencial" => CanalInteracao.VISITA,
        _ => CanalInteracao.OUTRO,
    };

    private static CanalInteracao CanalOrigem(string? origem) =>
        origem?.Contains("whatsapp", StringComparison.OrdinalIgnoreCase) == true ? CanalInteracao.WHATSAPP : CanalInteracao.OUTRO;

    private static string Resumo(ImbAtividadePainel a)
    {
        var tipo = a.Tipo switch
        {
            "NP" => "Negócio perdido", "NG" => "Negócio ganho", "AN" => "Anotação", "AT" => "Atendimento", "TA" => "Tarefa",
            "VI" => "Visita", "RE" => "Reserva", "RA" => "Reaberto", "ED" => "Edição", "AE" => "Mudança de etapa", "AR" => "Arquivo",
            "AG" => "Edição", "LI" => "Ligação",
            _ => a.Tipo ?? "Atividade",
        };
        // texto da linha do tempo do painel (já traz etapa, observação e quem fez)
        if (a.Texto is { } tx)
            return $"[Imobibrasil · {tipo}] {tx}" + (a.Mensagem is { } msg && !tx.Contains(msg, StringComparison.Ordinal) ? "\nMensagem: " + msg : "");
        var partes = new List<string> { $"[Imobibrasil · {tipo}]" };
        if (a.Titulo is { } t) partes.Add(t);
        if (a.EtapaAnterior is not null || a.EtapaDestino is not null)
            partes.Add($"(etapa {a.EtapaAnterior ?? "—"}{(a.EtapaDestino is null ? "" : " → " + a.EtapaDestino)})");
        if (a.Inicio is { } i) partes.Add($"em {i:dd/MM/yyyy HH:mm}");
        if (a.Valor is > 0) partes.Add(a.Valor.Value.ToString("C0", Texto.PtBr));
        var s = string.Join(' ', partes);
        if (a.Anotacao is { } an) s += "\n" + an;
        if (a.Mensagem is { } m) s += "\nMensagem: " + m;
        return s;
    }

    // ------------------------------------------------ agenda ------------------------------------------------

    private async Task AgendaAsync(Contexto c, CancellationToken ct)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var ja = (await db.Compromissos.Where(x => x.ImobibrasilId != null).Select(x => x.ImobibrasilId!.Value).ToListAsync(ct)).ToHashSet();
        var agora = DateTime.UtcNow;
        foreach (var e in c.D.Agenda)
        {
            if (ja.Contains(e.Id)) { c.Rel.Contar("compromissos já importados"); continue; }
            var inicio = Utc(e.Inicio) ?? agora;
            var fim = Utc(e.Fim) is { } f && f > inicio ? f : inicio.AddHours(1);
            if (e.DiaTodo && fim - inicio > TimeSpan.FromHours(12)) fim = inicio.AddHours(1);
            Guid? leadId = null;
            if (e.ClienteId is { } cli && c.PessoaPorId.TryGetValue(cli, out var pessoa))
                leadId = await db.Leads.Where(l => l.PessoaId == pessoa && l.ExcluidoEm == null).OrderByDescending(l => l.CriadoEm).Select(l => (Guid?)l.Id).FirstOrDefaultAsync(ct);
            var cliente = e.ClienteId is { } cid ? c.D.Pessoas.FirstOrDefault(p => p.Id == cid)?.Nome : null;
            var resp = e.ResponsavelId is { } r ? c.Corretores.GetValueOrDefault(r, $"usuário {r} do Imobibrasil") : null;
            var passado = fim < agora;
            var desc = (e.Descricao ?? "") + $"\n\n{Marcador("compromisso", e.Id)} Agenda do Imobibrasil ({e.Categoria})"
                       + (resp is null ? "" : $"\nResponsável lá: {resp}") + (cliente is null ? "" : $"\nCliente: {cliente}")
                       + (passado && !e.Concluido ? "\nSem baixa no Imobibrasil (data já passou)." : "");
            Guid? imovelId = e.ImovelId is { } im && c.ImovelPorId.TryGetValue(im, out var imovel) ? imovel : null;
            if (imovelId is null && leadId is null)
            {
                // a agenda do sistema exige imóvel ou lead; o texto fica no relatório (e no backup)
                c.Rel.Contar("compromissos sem imóvel nem lead (não importados)");
                c.Rel.Avisos.Add($"Compromisso {e.Id} de {e.Inicio:dd/MM/yyyy} sem imóvel nem lead: {e.Titulo} — {e.Descricao?.ReplaceLineEndings(" ")}");
                continue;
            }
            var comp = new Compromisso
            {
                Id = Guid.NewGuid(), ImovelId = imovelId,
                LeadId = leadId, Tipo = TipoAgenda(e.Titulo), Titulo = Corta(e.Titulo ?? "Compromisso", 200)!, Descricao = desc.Trim(),
                InicioEm = inicio, FimEm = fim, ResponsavelId = c.U.Id, ImobibrasilId = e.Id,
                // compromisso antigo sem baixa não pode virar alerta de "não cumprido" para o administrador
                Status = e.Concluido || passado ? StatusCompromisso.CONCLUIDO : StatusCompromisso.PENDENTE,
                ConcluidoEm = e.Concluido || passado ? fim : null, CriadoEm = inicio, AtualizadoEm = agora,
            };
            db.Compromissos.Add(comp);
            audit.Registrar(db, c.U.Id, "compromissos", comp.Id, "CRIAR", depois: comp);
            c.Rel.Contar("compromissos importados");
        }
        await db.SaveChangesAsync(ct);
        c.Progresso($"Agenda: {c.D.Agenda.Count} lidos.");
    }

    private static TipoCompromisso TipoAgenda(string? titulo) => (titulo ?? "").ToLowerInvariant() switch
    {
        var t when t.Contains("visita") => TipoCompromisso.VISITA,
        var t when t.Contains("reuni") => TipoCompromisso.REUNIAO,
        var t when t.Contains("ligar") || t.Contains("liga") => TipoCompromisso.LIGACAO,
        var t when t.Contains("mensagem") || t.Contains("e-mail") || t.Contains("whatsapp") => TipoCompromisso.MENSAGEM,
        _ => TipoCompromisso.TAREFA,
    };

    // ------------------------------------------------ utilitários ------------------------------------------------

    /// <summary>Telefone igual só junta cadastros do mesmo primeiro nome (o painel tem leads com o telefone do corretor).</summary>
    public static bool MesmoPrimeiroNome(string? a, string? b)
    {
        static string Primeiro(string? s) => Texto.Limpo(s)?.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]
            .Normalize(NormalizationForm.FormD).Where(char.IsLetter).Aggregate("", (t, ch) => t + char.ToLowerInvariant(ch)) ?? "";
        var (pa, pb) = (Primeiro(a), Primeiro(b));
        return pa.Length == 0 || pb.Length == 0 || pa == pb;
    }

    private static string Marcador(string tipo, long id) => $"[Imobibrasil {tipo} {id.ToString(CultureInfo.InvariantCulture)}]";

    /// <summary>Horário do painel é de Brasília (UTC−3, sem horário de verão desde 2019).</summary>
    private static DateTime? Utc(DateTime? local) => local is { } l ? DateTime.SpecifyKind(l, DateTimeKind.Unspecified).Add(FusoBrasilia).ToUniversalTimeKind() : null;

    private static decimal? M(decimal? v) => v is > 0 ? decimal.Round(v.Value, 2) : null;

    private static string? EmailValido(string? e) => Texto.Limpo(e) is { } t && t.Contains('@') && !t.Contains(' ') && t.Length <= 200 ? t : null;

    private static string? Juntar(params string?[] partes) =>
        partes.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() is { Count: > 0 } l ? string.Join(", ", l) : null;

    private static string? Corta(string? s, int max) => Texto.Limpo(s) is not { } t ? null : t.Length <= max ? t : t[..max].TrimEnd();
}

internal static class DataUtcExt
{
    public static DateTime ToUniversalTimeKind(this DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
