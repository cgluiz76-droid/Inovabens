using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Notion;

/// <summary>O que a importação fez (ou faria, na simulação) e o que ficou pendente de decisão.</summary>
public sealed class RelatorioNotion
{
    public bool Simulado { get; init; }
    public List<string> Detalhes { get; } = [];
    public List<string> Pendencias { get; } = [];
    public SortedDictionary<string, int> Contagem { get; } = new(StringComparer.Ordinal);

    public void Contar(string chave, int n = 1) => Contagem[chave] = Contagem.GetValueOrDefault(chave) + n;
}

/// <summary>
/// Traz o workspace "Inova Bens" do Notion para o sistema, SEM alterar nada no Notion (o <see cref="NotionCliente"/> só lê)
/// e sem apagar ou sobrescrever o que já existe aqui:
/// <list type="bullet">
/// <item>ENVOLVIDOS e CLIENTES viram pessoas (procura antes por CPF, e-mail, telefone e nome); em pessoa já cadastrada só
///   completa campos vazios, acrescenta perfis e um bloco nas Observações com tudo o que veio do Notion;</item>
/// <item>PROJETOS: cada projeto é um imóvel; status/etapa/previsão vão para os campos novos (só se vazios), o conteúdo da
///   página vira um memorando fixado e cada linha do histórico "Data | Atualização" vira um memorando com aquela data;</item>
/// <item>ATIVOS viram memorando no imóvel; TAREFAS viram compromissos na agenda do imóvel, com os responsáveis como participantes;</item>
/// <item>arquivos guardados no Notion vão para "05 - Documentos" do imóvel no Drive.</item>
/// </list>
/// Cada item trazido fica em notion_importacoes: rodar de novo não duplica, e só atualiza o que mudou no Notion e não foi
/// editado no sistema depois da importação.
/// </summary>
public sealed partial class ImportacaoNotionService(
    IDbContextFactory<InovabensDb> fabrica, ImoveisService imoveis, AuditService audit, ILogger<ImportacaoNotionService> log)
{
    // ids das bases no Notion (aparecem nos links; não são credenciais: o acesso depende do token da integração)
    public const string BaseEnvolvidos = "2810be81eecc80cb98f5e74b4d9adb1f";
    public const string BaseProjetos = "2810be81eecc80c7913ff223139bab18";
    public const string BaseClientes = "2820be81eecc8041b3c5f19da8f49848";
    public const string BaseAtivos = "2c00be81eecc80acae8ad0eaf55966ff";
    public const string BaseTarefas = "2c00be81eecc809a959edb3359e2ca61";

    /// <summary>Projeto do Notion (título normalizado) → imóvel (código IMV-... ou referência dos anúncios).</summary>
    public static readonly IReadOnlyDictionary<string, string> MapaProjetos = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PREDIO BANESPA"] = "IMV-000019",
        ["PREDIO DA BENE"] = "CO0002",
        ["SANTA ANA POSTO"] = "AR0006",
        ["SANTA ANA FABRICA DE VELAS"] = "AR0007",
        ["SANTA ANA RUA GAVIOES"] = "AR0005",
        ["MONTE APRAZIVEL"] = "AR0002",
        ["ECOPARQUE BOCAINA"] = "TE0043",
        ["NEVES PAULISTA"] = "SI0001",
        ["ILHA SOLTEIRA"] = "SA0005",
        ["CHACARA BETEL"] = "AR0004",
        ["ENG BALDUINO"] = "AR0001",
        ["CASA DO PALESTRA"] = "CA0013",
        ["CASA SAO FRANCISCO"] = "CA0060",
        ["CASA ALTO RIO PRETO"] = "CA0096",
        ["FLAMBOYANT"] = "TE0044",
        ["SITIO CEDRAL"] = "SI0002",
    };

    /// <summary>Referências que podem ser cadastradas se ainda não existirem (pedido do usuário), com tipo e cidade.</summary>
    public static readonly IReadOnlyDictionary<string, (string Tipo, string Cidade)> CriarSeFaltar = new Dictionary<string, (string, string)>
    {
        ["SI0001"] = ("fazenda/sítio", "Neves Paulista"),
        ["SA0005"] = ("outro", "Ilha Solteira"),
        ["AR0004"] = ("área", "São José do Rio Preto"),
        ["AR0001"] = ("área", "Monte Aprazível"),
    };

    /// <summary>Ativo do Notion (título normalizado) → projeto (título normalizado).</summary>
    public static readonly IReadOnlyDictionary<string, string> AtivoParaProjeto = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ENGENHEIRO BALDUINO"] = "ENG BALDUINO",
        ["CHACARA CEDRAL"] = "SITIO CEDRAL",
        ["SANTA ANA POSTO"] = "SANTA ANA POSTO",
        ["SP001 PREDIO BANESPA"] = "PREDIO BANESPA",
        ["SP002 PREDIO DA BENEFICENCIA PORTUGUESA"] = "PREDIO DA BENE",
        ["MONTE APRASIVEL SP"] = "MONTE APRAZIVEL",
        ["NEVES PAULISTA SP"] = "NEVES PAULISTA",
        ["ILHA SOLTEIRA SP"] = "ILHA SOLTEIRA",
        ["CHACARA BETEL"] = "CHACARA BETEL",
        ["MS003 ARARA VERMELHA"] = "FLAMBOYANT", // TE0044
    };

    /// <summary>Tarefas sem projeto que o usuário decidiu não trazer (título normalizado); continuam no Notion.</summary>
    public static readonly string[] TarefasIgnoradas = ["ACABAR RODA PE LOJA 2 INTERIOR", "APROVACAO FASE 2"];

    /// <summary>Tarefa sem projeto → projeto, por decisão do usuário (título normalizado).</summary>
    public static readonly IReadOnlyDictionary<string, string> TarefaParaProjeto = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CONTAINERS ALOJAMENTO DISPOSICAO INTERNA"] = "ECOPARQUE BOCAINA", // TE0043
    };

    /// <summary>Ativos que o usuário decidiu não trazer (início do título normalizado); continuam no Notion.</summary>
    public static readonly string[] AtivosIgnorados = ["CAR DANIELE", "CAR DANELE", "MS001", "SANTA ANA OUTROS"];

    /// <summary>Tarefa sem projeto: palavra no título/ação → projeto.</summary>
    public static readonly (string Palavra, string Projeto)[] PalavrasTarefa =
    [
        ("PALESTRA", "CASA DO PALESTRA"), ("BANESPA", "PREDIO BANESPA"), ("BENE", "PREDIO DA BENE"),
        ("BENEFICENCIA", "PREDIO DA BENE"), ("BETEL", "CHACARA BETEL"), ("BALDUINO", "ENG BALDUINO"),
        ("CEDRAL", "SITIO CEDRAL"), ("NEVES PAULISTA", "NEVES PAULISTA"), ("ILHA SOLTEIRA", "ILHA SOLTEIRA"),
        ("FLAMBOYANT", "FLAMBOYANT"), ("SAO FRANCISCO", "CASA SAO FRANCISCO"), ("ALTO RIO PRETO", "CASA ALTO RIO PRETO"),
        ("BOCAINA", "ECOPARQUE BOCAINA"), ("INOCENCIA", "ECOPARQUE BOCAINA"), ("ARARA VERMELHA", "FLAMBOYANT"),
        ("GAVIOES", "SANTA ANA RUA GAVIOES"), ("FABRICA DE VELAS", "SANTA ANA FABRICA DE VELAS"), ("POSTO", "SANTA ANA POSTO"),
        ("MONTE APRAZIVEL", "MONTE APRAZIVEL"),
    ];

    private const string TipoPessoaReg = "PESSOA", TipoImovelReg = "IMOVEL", TipoMemorandoReg = "MEMORANDO",
        TipoCompromissoReg = "COMPROMISSO", TipoArquivoReg = "ARQUIVO", TipoEnvolvidoReg = "ENVOLVIDO";

    /// <summary>Maiúsculas, sem acento, sem pontuação, espaços simples ("ENGº BALDUÍNO" → "ENG BALDUINO").</summary>
    public static string Normalizar(string? s)
    {
        var d = (s ?? "").Replace('º', ' ').Replace('ª', ' ').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>Perfil da pessoa vinda de ENVOLVIDOS (o atributo profissional vira a especialidade).</summary>
    public static string PerfilEnvolvido(string? atributo, string? divisao, string? vinculo)
    {
        var a = Normalizar(atributo);
        if (a.Contains("VIZINH") || a.Contains("PREFEITURA") || a.Contains("CARTORIO")) return "TERCEIRO";
        if (Normalizar(divisao) is { Length: > 0 } d && d != "TERCEIROS") return "EQUIPE";
        if (Normalizar(vinculo) == "PARCERIA") return "PARCEIRO";
        return "PRESTADOR";
    }

    public static TipoPessoa TipoPorNome(string nome) =>
        Regex.IsMatch(Normalizar(nome), @"\b(LTDA|EIRELI|CARTORIO|S A|SA|ME|EPP|MEI)\b") ? TipoPessoa.PJ : TipoPessoa.PF;

    [GeneratedRegexAttribute(@"\b\d{3}\.?\d{3}\.?\d{3}-?\d{2}\b")]
    private static partial Regex CpfNoTexto();

    /// <summary>CPF válido (com dígitos verificadores) achado no texto de documentos, sem máscara.</summary>
    public static string? CpfDoTexto(string? texto)
    {
        foreach (Match m in CpfNoTexto().Matches(texto ?? ""))
        {
            var d = new string([.. m.Value.Where(char.IsAsciiDigit)]);
            if (DigitosCpfOk(d)) return d;
        }
        return null;
    }

    private static bool DigitosCpfOk(string d)
    {
        if (d.Length != 11 || d.Distinct().Count() == 1) return false;
        int Dv(int n) { var s = 0; for (var i = 0; i < n; i++) s += (d[i] - '0') * (n + 1 - i); var r = s * 10 % 11; return r == 10 ? 0 : r; }
        return Dv(9) == d[9] - '0' && Dv(10) == d[10] - '0';
    }

    /* ------------------------------------------------------------------------------------------------------------ */

    private sealed record Pagina(JsonElement Json, string Id, string Titulo, string? Link, DateTime Editada, DateTime Criada, ConteudoPagina Conteudo);

    private sealed class Leitura
    {
        public List<Pagina> Envolvidos = [], Clientes = [], Projetos = [], Ativos = [], Tarefas = [];
        public Dictionary<string, List<string>> Relacoes = [];
        public List<string> Rel(Pagina p, string prop) => Relacoes.GetValueOrDefault(p.Id + "|" + prop) ?? [];
    }

    private sealed record ArquivoPendente(ArquivoNotion Arquivo, Guid ImovelId, string Origem);

    /// <summary>Faz a importação; com <paramref name="simular"/> lê tudo, monta o relatório e desfaz as gravações.</summary>
    public async Task<RelatorioNotion> ImportarAsync(UsuarioAtual u, NotionCliente notion, bool simular,
        IReadOnlyDictionary<string, string>? mapaExtra = null, Action<string>? progresso = null, CancellationToken ct = default)
    {
        u.Exigir(P.PessoasCriar); u.Exigir(P.PessoasEditar); u.Exigir(P.ImoveisEditar);
        u.Exigir(P.MemorandosCriar); u.Exigir(P.AgendaCriar);
        var rel = new RelatorioNotion { Simulado = simular };
        var mapa = new Dictionary<string, string>(MapaProjetos, StringComparer.Ordinal);
        foreach (var (k, v) in mapaExtra ?? new Dictionary<string, string>()) mapa[Normalizar(k)] = v.Trim().ToUpperInvariant();

        // 1) lê tudo do Notion (só leitura)
        var l = await LerAsync(notion, progresso, ct);
        progresso?.Invoke($"Notion lido: {l.Envolvidos.Count} envolvidos, {l.Clientes.Count} clientes, {l.Projetos.Count} projetos, " +
                          $"{l.Ativos.Count} ativos, {l.Tarefas.Count} tarefas.");

        // 2) imóveis: os que faltam e podem ser criados (só na importação de verdade: cria pasta no Drive)
        var refsUsadas = l.Projetos.Select(p => mapa.GetValueOrDefault(Normalizar(p.Titulo))).OfType<string>().Distinct().ToList();
        Dictionary<string, Guid> imovelPorRef;
        await using (var db0 = await fabrica.CreateDbContextAsync(ct))
            imovelPorRef = await MapaImoveisAsync(db0, ct);
        foreach (var r in refsUsadas.Where(r => !imovelPorRef.ContainsKey(r)))
        {
            if (!CriarSeFaltar.TryGetValue(r, out var c))
            {
                rel.Pendencias.Add($"Imóvel {r} não existe no sistema e não está na lista para criar.");
                continue;
            }
            var projeto = l.Projetos.First(p => mapa.GetValueOrDefault(Normalizar(p.Titulo)) == r);
            if (simular) { rel.Detalhes.Add($"Imóvel novo (seria criado): {r} – {projeto.Titulo} ({c.Cidade}/SP)"); rel.Contar("imóveis criados"); continue; }
            var titulo = CultureInfo.GetCultureInfo("pt-BR").TextInfo.ToTitleCase(projeto.Titulo.ToLowerInvariant());
            var id = await imoveis.CriarAsync(u, new ImovelForm
            {
                Tipo = c.Tipo, Titulo = titulo, NomeDrive = $"{r} - {titulo}", Referencia = r, Cidade = c.Cidade, Uf = "SP",
                DescricaoInterna = $"Cadastrado na importação do Notion (projeto \"{projeto.Titulo}\"). Completar os dados.",
            });
            imovelPorRef[r] = id;
            rel.Detalhes.Add($"Imóvel novo: {r} – {titulo}");
            rel.Contar("imóveis criados");
        }

        // 3) gravações no banco, numa transação (desfeita na simulação)
        var arquivos = new List<ArquivoPendente>();
        await using (var db = await fabrica.CreateDbContextAsync(ct))
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var ctx = new Contexto(db, u, rel, l, mapa, imovelPorRef, arquivos);
            await ctx.CarregarAsync(ct);
            await ImportarPessoasAsync(ctx, ct);
            await ImportarProjetosAsync(ctx, ct);
            await ImportarAtivosAsync(ctx, ct);
            await ImportarTarefasAsync(ctx, ct);
            await db.SaveChangesAsync(ct);
            if (simular) await tx.RollbackAsync(ct);
            else await tx.CommitAsync(ct);
        }

        // 4) arquivos guardados no Notion → Drive do imóvel (05 - Documentos)
        await ArquivosAsync(u, notion, arquivos, simular, rel, progresso, ct);
        return rel;
    }

    private static async Task<Dictionary<string, Guid>> MapaImoveisAsync(InovabensDb db, CancellationToken ct)
    {
        var lista = await db.Imoveis.Where(i => i.ExcluidoEm == null).Select(i => new { i.Id, i.Codigo, i.Referencia, i.CodigoAntigo }).ToListAsync(ct);
        var r = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in lista)
        {
            r[i.Codigo] = i.Id;
            if (i.Referencia is { Length: > 0 } re) r.TryAdd(re, i.Id);
            if (i.CodigoAntigo is { Length: > 0 } ant) r.TryAdd(ant, i.Id); // mapeamento antigo "IMV-000019"
        }
        return r;
    }

    /* ----------------------------------------------- leitura ----------------------------------------------- */

    private static readonly (string Base, string[] Relacoes)[] Bases =
    [
        (BaseEnvolvidos, ["PROJETOS"]),
        (BaseClientes, []),
        (BaseProjetos, ["Cliente", "Equipe"]),
        (BaseAtivos, ["Cliente"]),
        (BaseTarefas, ["Projeto", "Responsável"]),
    ];

    private static async Task<Leitura> LerAsync(NotionCliente notion, Action<string>? progresso, CancellationToken ct)
    {
        var l = new Leitura();
        foreach (var (baseId, relacoes) in Bases)
        {
            var destino = baseId switch
            {
                BaseEnvolvidos => l.Envolvidos, BaseClientes => l.Clientes, BaseProjetos => l.Projetos,
                BaseAtivos => l.Ativos, _ => l.Tarefas,
            };
            var linhas = await notion.LinhasAsync(baseId, ct);
            progresso?.Invoke($"Lendo {linhas.Count} páginas da base {baseId}...");
            foreach (var j in linhas)
            {
                if (j.TryGetProperty("archived", out var a) && a.GetBoolean()) continue;
                if (j.TryGetProperty("in_trash", out var t) && t.GetBoolean()) continue;
                var id = NotionLeitor.Id(j);
                var conteudo = await NotionLeitor.ConteudoAsync(notion, id, ct);
                destino.Add(new Pagina(j, id, NotionLeitor.Titulo(j), NotionLeitor.Link(j), NotionLeitor.EditadoEm(j), NotionLeitor.CriadoEm(j), conteudo));
                foreach (var prop in relacoes)
                {
                    var (ids, incompleta, propId) = NotionLeitor.Relacao(j, prop);
                    if (incompleta && propId is not null) ids = await notion.RelacaoAsync(id, propId, ct);
                    l.Relacoes[id + "|" + prop] = ids;
                }
            }
        }
        return l;
    }

    /* ----------------------------------------------- gravação ----------------------------------------------- */

    private sealed class Contexto(InovabensDb db, UsuarioAtual u, RelatorioNotion rel, Leitura l, Dictionary<string, string> mapa,
        Dictionary<string, Guid> imovelPorRef, List<ArquivoPendente> arquivos)
    {
        public InovabensDb Db => db;
        public UsuarioAtual U => u;
        public RelatorioNotion Rel => rel;
        public Leitura L => l;
        public List<ArquivoPendente> Arquivos => arquivos;
        public List<Pessoa> Pessoas = [];
        public Dictionary<(string, string), NotionImportacao> Registros = [];
        public Dictionary<string, Guid> PessoaPorNotion = [];
        public HashSet<(Guid, Guid, string)> Envolvidos = [];
        public Dictionary<string, string> NomePorNotion = [];
        public DateTime Agora = DateTime.UtcNow;

        public async Task CarregarAsync(CancellationToken ct)
        {
            Pessoas = await db.Pessoas.Include(p => p.Papeis).Where(p => p.ExcluidoEm == null).ToListAsync(ct);
            Registros = (await db.NotionImportacoes.ToListAsync(ct)).ToDictionary(r => (r.NotionId, r.Tipo));
            foreach (var r in Registros.Values.Where(r => r.Tipo == TipoPessoaReg)) PessoaPorNotion[r.NotionId] = r.EntidadeId;
            Envolvidos = [.. (await db.ImovelEnvolvidos.Select(e => new { e.ImovelId, e.PessoaId, e.Papel }).ToListAsync(ct))
                .Select(e => (e.ImovelId, e.PessoaId, e.Papel))];
            foreach (var p in l.Envolvidos.Concat(l.Clientes)) NomePorNotion[p.Id] = p.Titulo;
        }

        /// <summary>Chave (título normalizado) do projeto pelo id da página.</summary>
        public string? ChaveProjeto(string paginaId) => l.Projetos.FirstOrDefault(p => p.Id == paginaId) is { } p ? Normalizar(p.Titulo) : null;

        /// <summary>Imóvel do projeto (null se não mapeado ou não existir).</summary>
        public Guid? ImovelDoProjeto(string chave) =>
            mapa.TryGetValue(chave, out var r) && imovelPorRef.TryGetValue(r, out var id) ? id : null;

        public string? RefDoProjeto(string chave) => mapa.GetValueOrDefault(chave);

        public Guid? ImovelDoAtivo(string tituloAtivo)
        {
            var k = Normalizar(tituloAtivo);
            if (mapa.TryGetValue(k, out var r)) return imovelPorRef.TryGetValue(r, out var id) ? id : null;
            return AtivoParaProjeto.TryGetValue(k, out var proj) ? ImovelDoProjeto(proj) : null;
        }

        public void Registrar(string notionId, string tipo, Guid entidade, DateTime editado)
        {
            if (Registros.TryGetValue((notionId, tipo), out var r))
            {
                r.EditadoNotionEm = editado; r.ImportadoEm = Agora; r.EntidadeId = entidade;
                return;
            }
            r = new NotionImportacao { NotionId = notionId, Tipo = tipo, EntidadeId = entidade, EditadoNotionEm = editado, ImportadoEm = Agora };
            Registros[(notionId, tipo)] = r;
            db.NotionImportacoes.Add(r);
        }

        public string Nomes(IEnumerable<string> ids) => string.Join(", ", ids.Select(i => NomePorNotion.GetValueOrDefault(i, "(página sem acesso)")));
    }

    /// <summary>Registro existente: true = pular (já importado e sem mudança no Notion, ou editado aqui depois).</summary>
    private static bool JaImportado(Contexto c, string notionId, string tipo, DateTime editadoNotion, DateTime? atualizadoLocal, out bool atualizar)
    {
        atualizar = false;
        if (!c.Registros.TryGetValue((notionId, tipo), out var r)) return false;
        if (r.EditadoNotionEm is { } antes && editadoNotion <= antes.AddSeconds(1)) return true;
        if (atualizadoLocal is { } loc && loc > r.ImportadoEm.AddSeconds(5)) return true; // mexeram aqui: não sobrescreve
        atualizar = true;
        return false;
    }

    /* ---- pessoas ---- */

    private sealed record DadosPessoa(string Nome, TipoPessoa Tipo, string Papel, string? Especialidade, string? Email,
        string? Telefone, string? Cpf, string Base, List<(string Rotulo, string? Valor)> Extras);

    private async Task ImportarPessoasAsync(Contexto c, CancellationToken ct)
    {
        foreach (var e in c.L.Envolvidos)
        {
            var j = e.Json;
            var atributo = NotionLeitor.Texto(j, "Atributos profissionais");
            var divisao = NotionLeitor.Texto(j, "Divisão");
            var vinculo = NotionLeitor.Texto(j, "Vínculo");
            var documentos = NotionLeitor.Texto(j, "Documentos");
            var nome = e.Titulo.Length > 0 ? e.Titulo : "(sem nome no Notion)";
            var papel = PerfilEnvolvido(atributo, divisao, vinculo);
            var dados = new DadosPessoa(nome, TipoPorNome(nome), papel, atributo, NotionLeitor.Texto(j, "Email"),
                NotionLeitor.Texto(j, "Chame no Whatsapp"), CpfDoTexto(documentos), "ENVOLVIDOS",
                [("Atributo profissional", atributo), ("Divisão", divisao), ("Vínculo", vinculo), ("Documentos", documentos),
                 ("Endereço", NotionLeitor.Texto(j, "Endereço")), ("Projetos", NomesProjetos(c, c.L.Rel(e, "PROJETOS")))]);
            var p = Pessoa(c, e, dados);
            foreach (var projId in c.L.Rel(e, "PROJETOS"))
                Envolver(c, projId, p, papel, atributo, $"{nome} em {c.ChaveProjeto(projId)}");
        }

        foreach (var cl in c.L.Clientes)
        {
            var j = cl.Json;
            var nome = cl.Titulo.Length > 0 ? cl.Titulo : "(sem nome no Notion)";
            var dados = new DadosPessoa(nome, TipoPorNome(nome), "PROPRIETARIO", null, NotionLeitor.Texto(j, "Email"),
                NotionLeitor.Texto(j, "Telefone"), null, "CLIENTES",
                [("Perfil de cliente", NotionLeitor.Texto(j, "Perfil de Cliente")), ("Status no processo", NotionLeitor.Texto(j, "Status no Processo")),
                 ("Origem", NotionLeitor.Texto(j, "Origem")), ("Cliente desde", DataTexto(j, "Cliente desde"))]);
            var p = Pessoa(c, cl, dados);
            foreach (var proj in c.L.Projetos.Where(x => c.L.Rel(x, "Cliente").Contains(cl.Id)))
                Envolver(c, proj.Id, p, "PROPRIETARIO", "Cliente no Notion", $"{nome} em {proj.Titulo}");
            foreach (var at in c.L.Ativos.Where(x => c.L.Rel(x, "Cliente").Contains(cl.Id)))
                if (c.ImovelDoAtivo(at.Titulo) is { } im) EnvolverImovel(c, im, p, "PROPRIETARIO", "Cliente no Notion");
        }
        await Task.CompletedTask;
    }

    private static string? NomesProjetos(Contexto c, List<string> ids) =>
        ids.Count == 0 ? null : string.Join(", ", ids.Select(i => c.L.Projetos.FirstOrDefault(p => p.Id == i)?.Titulo ?? "(projeto sem acesso)"));

    private static string? DataTexto(JsonElement j, string prop) =>
        NotionLeitor.Data(j, prop) is { Inicio: { } i } d ? i.ToString("dd/MM/yyyy") + (d.Fim is { } f ? " a " + f.ToString("dd/MM/yyyy") : "") : null;

    private Pessoa Pessoa(Contexto c, Pagina pg, DadosPessoa d)
    {
        var marcador = $"[Notion {pg.Id}]";
        var telDig = Formatos.TelefoneDigitos(d.Telefone);
        var telOk = Formatos.TelefoneValido(telDig);
        var email = d.Email is { } em && em.Contains('@') && em.Length <= 200 ? em.Trim() : null;

        Pessoa? p = null;
        string como = "";
        if (c.PessoaPorNotion.TryGetValue(pg.Id, out var jaId)) { p = c.Pessoas.FirstOrDefault(x => x.Id == jaId); como = "já importada"; }
        if (p is null && d.Cpf is { } cpfBusca) { p = c.Pessoas.FirstOrDefault(x => x.CpfCnpj == cpfBusca); como = "mesmo CPF"; }
        if (p is null && email is not null) { p = c.Pessoas.FirstOrDefault(x => string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase)); como = "mesmo e-mail"; }
        if (p is null && telOk)
        {
            p = c.Pessoas.FirstOrDefault(x => Formatos.TelefoneDigitos(x.Telefone) == telDig || Formatos.TelefoneDigitos(x.Whatsapp) == telDig);
            como = "mesmo telefone";
        }
        if (p is null) { p = c.Pessoas.FirstOrDefault(x => Normalizar(x.NomeRazao) == Normalizar(d.Nome)); como = "mesmo nome"; }

        var obs = BlocoObservacoes(pg, d, telOk ? null : d.Telefone, marcador);
        if (p is null)
        {
            p = new Pessoa
            {
                Id = Guid.NewGuid(), Tipo = d.Tipo, NomeRazao = Corta(d.Nome, 200)!, Email = email, CriadoPor = c.U.Id,
                Whatsapp = telOk ? Formatos.TelefoneFormatado(telDig) : null, Telefone = telOk ? Formatos.TelefoneFormatado(telDig) : null,
                CpfCnpj = d.Cpf is { } cpf && c.Pessoas.All(x => x.CpfCnpj != cpf) ? cpf : null,
                Observacoes = obs, CriadoEm = pg.Criada, AtualizadoEm = c.Agora,
            };
            p.Papeis = [new PessoaPapel { PessoaId = p.Id, Papel = d.Papel, Especialidade = Corta(d.Especialidade, 100) }];
            c.Db.Pessoas.Add(p);
            c.Pessoas.Add(p);
            audit.Registrar(c.Db, c.U.Id, "pessoas", p.Id, "CRIAR", depois: p);
            c.Rel.Detalhes.Add($"Pessoa nova: {p.NomeRazao} ({d.Papel}{(d.Especialidade is null ? "" : " · " + d.Especialidade)}) ← {d.Base}");
            c.Rel.Contar("pessoas novas");
        }
        else
        {
            var antes = p.Copia();
            var mudou = false;
            if (p.Email is null && email is not null && c.Pessoas.All(x => x == p || !string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase))) { p.Email = email; mudou = true; }
            if (telOk && p.Whatsapp is null) { p.Whatsapp = Formatos.TelefoneFormatado(telDig); mudou = true; }
            if (telOk && p.Telefone is null) { p.Telefone = Formatos.TelefoneFormatado(telDig); mudou = true; }
            if (p.CpfCnpj is null && d.Cpf is { } cpf && c.Pessoas.All(x => x.CpfCnpj != cpf)) { p.CpfCnpj = cpf; mudou = true; }
            if (!(p.Observacoes ?? "").Contains(marcador, StringComparison.Ordinal))
            {
                p.Observacoes = string.IsNullOrWhiteSpace(p.Observacoes) ? obs : p.Observacoes.TrimEnd() + "\n\n" + obs;
                mudou = true;
            }
            var papel = p.Papeis.FirstOrDefault(x => x.Papel == d.Papel);
            if (papel is null)
            {
                p.Papeis.Add(new PessoaPapel { PessoaId = p.Id, Papel = d.Papel, Especialidade = Corta(d.Especialidade, 100) });
                mudou = true;
            }
            else if (papel.Especialidade is null && d.Especialidade is not null) { papel.Especialidade = Corta(d.Especialidade, 100); mudou = true; }
            if (mudou)
            {
                p.AtualizadoEm = c.Agora;
                audit.Registrar(c.Db, c.U.Id, "pessoas", p.Id, "ATUALIZAR", antes, p);
                c.Rel.Detalhes.Add($"Pessoa existente completada: {p.NomeRazao} ({como}) ← {d.Base} \"{d.Nome}\"");
                c.Rel.Contar("pessoas completadas");
            }
        }
        c.PessoaPorNotion[pg.Id] = p.Id;
        c.Registrar(pg.Id, TipoPessoaReg, p.Id, pg.Editada);
        return p;
    }

    private static string BlocoObservacoes(Pagina pg, DadosPessoa d, string? telefoneInvalido, string marcador)
    {
        var sb = new StringBuilder();
        sb.Append(marcador).Append(" Importado do Notion (").Append(d.Base).Append(") em ").Append(DateTime.Now.ToString("dd/MM/yyyy")).Append('\n');
        foreach (var (rotulo, valor) in d.Extras)
            if (!string.IsNullOrWhiteSpace(valor)) sb.Append(rotulo).Append(": ").Append(valor.Trim()).Append('\n');
        if (telefoneInvalido is not null) sb.Append("Telefone no Notion (fora do padrão): ").Append(telefoneInvalido).Append('\n');
        if (pg.Conteudo.TextoFinal.Length > 0) sb.Append("Conteúdo da página:\n").Append(pg.Conteudo.TextoFinal).Append('\n');
        if (pg.Link is not null) sb.Append("Link: ").Append(pg.Link).Append('\n');
        return sb.ToString().TrimEnd();
    }

    private void Envolver(Contexto c, string projetoId, Pessoa p, string papel, string? obs, string descricao)
    {
        if (c.ChaveProjeto(projetoId) is not { } chave) { c.Rel.Pendencias.Add($"Vínculo não feito (projeto sem acesso): {descricao}"); return; }
        if (c.ImovelDoProjeto(chave) is not { } im)
        {
            c.Rel.Pendencias.Add($"Vínculo aguardando o imóvel do projeto \"{chave}\": {p.NomeRazao} ({papel})");
            c.Rel.Contar("vínculos pendentes");
            return;
        }
        EnvolverImovel(c, im, p, papel, obs);
    }

    private void EnvolverImovel(Contexto c, Guid imovelId, Pessoa p, string papel, string? obs)
    {
        if (!c.Envolvidos.Add((imovelId, p.Id, papel))) return;
        var e = new ImovelEnvolvido { ImovelId = imovelId, PessoaId = p.Id, Papel = papel, Observacao = Texto.Limpo(obs) };
        c.Db.ImovelEnvolvidos.Add(e);
        audit.Registrar(c.Db, c.U.Id, "imovel_envolvidos", e.Id, "CRIAR", depois: e);
        c.Rel.Contar("vínculos pessoa × imóvel");
    }

    /* ---- projetos ---- */

    private async Task ImportarProjetosAsync(Contexto c, CancellationToken ct)
    {
        foreach (var pr in c.L.Projetos)
        {
            var chave = Normalizar(pr.Titulo);
            var j = pr.Json;
            var status = NotionLeitor.Texto(j, "Status");
            var etapa = NotionLeitor.Texto(j, "Etapa da Estratégia");
            var (prev, _, _) = NotionLeitor.Data(j, "Previsão de Entrega");
            if (c.ImovelDoProjeto(chave) is not { } imovelId)
            {
                var r = c.RefDoProjeto(chave);
                c.Rel.Pendencias.Add($"Projeto \"{pr.Titulo}\" sem imóvel {(r is null ? "definido" : $"({r} ainda não existe)")}: " +
                                     $"{pr.Conteudo.Historico.Count} atualizações, {pr.Conteudo.Arquivos.Count} arquivos aguardam.");
                c.Rel.Contar("projetos pendentes");
                continue;
            }

            // campos novos do imóvel: só preenche os vazios
            var im = await c.Db.Imoveis.SingleAsync(i => i.Id == imovelId, ct);
            var antes = im.Copia();
            var mudou = false;
            if (im.ProjetoStatus is null && Texto.Limpo(status) is { } s) { im.ProjetoStatus = Corta(s, 30); mudou = true; }
            if (im.ProjetoEtapa is null && Texto.Limpo(etapa) is { } e) { im.ProjetoEtapa = Corta(e, 40); mudou = true; }
            if (im.ProjetoPrevisaoEntrega is null && prev is { } pv) { im.ProjetoPrevisaoEntrega = DateOnly.FromDateTime(pv); mudou = true; }
            if (mudou)
            {
                im.AtualizadoEm = c.Agora;
                audit.Registrar(c.Db, c.U.Id, "imoveis", im.Id, "ATUALIZAR", antes, im);
                c.Rel.Contar("imóveis com dados do projeto");
            }
            c.Registrar(pr.Id, TipoImovelReg, imovelId, pr.Editada);

            var corpo = new StringBuilder();
            corpo.Append("Status: ").Append(status ?? "—").Append(" · Etapa: ").Append(etapa ?? "—")
                 .Append(" · Previsão de entrega: ").Append(prev?.ToString("dd/MM/yyyy") ?? "—").Append('\n');
            if (c.L.Rel(pr, "Cliente") is { Count: > 0 } cli) corpo.Append("Clientes: ").Append(c.Nomes(cli)).Append('\n');
            if (c.L.Rel(pr, "Equipe") is { Count: > 0 } eq) corpo.Append("Equipe: ").Append(c.Nomes(eq)).Append('\n');
            if (pr.Link is not null) corpo.Append("Página no Notion: ").Append(pr.Link).Append('\n');
            if (pr.Conteudo.TextoFinal.Length > 0) corpo.Append('\n').Append(pr.Conteudo.TextoFinal);
            Memorando(c, pr.Id, imovelId, $"Projeto (Notion): {pr.Titulo}", corpo.ToString(), fixado: true, pr.Criada, pr.Editada, "memorandos de projeto");

            foreach (var h in pr.Conteudo.Historico)
            {
                var quando = DatasBR.ParaUtc(h.Data.ToDateTime(new TimeOnly(12, 0)));
                Memorando(c, h.Id, imovelId, $"Atualização de {h.Data:dd/MM/yyyy} (Notion · {pr.Titulo})",
                    h.Texto.Length > 0 ? h.Texto : "(sem texto)", fixado: false, quando, pr.Editada, "memorandos do histórico");
            }
            foreach (var a in pr.Conteudo.Arquivos) c.Arquivos.Add(new ArquivoPendente(a, imovelId, $"projeto {pr.Titulo}"));
        }
    }

    private void Memorando(Contexto c, string notionId, Guid imovelId, string titulo, string corpo, bool fixado, DateTime criado,
        DateTime editadoNotion, string contador)
    {
        Memorando? m = null;
        if (c.Registros.TryGetValue((notionId, TipoMemorandoReg), out var reg))
        {
            m = c.Db.Memorandos.Local.FirstOrDefault(x => x.Id == reg.EntidadeId) ?? c.Db.Memorandos.Find(reg.EntidadeId);
            if (m is null || m.ExcluidoEm is not null) return; // apagado aqui: respeita
        }
        if (m is not null)
        {
            if (JaImportado(c, notionId, TipoMemorandoReg, editadoNotion, m.AtualizadoEm, out var atualizar) || !atualizar) return;
            var antes = m.Copia();
            m.Titulo = Corta(titulo, 200)!; m.Corpo = corpo; m.AtualizadoEm = c.Agora;
            audit.Registrar(c.Db, c.U.Id, "memorandos", m.Id, "ATUALIZAR", antes, m);
            c.Registrar(notionId, TipoMemorandoReg, m.Id, editadoNotion);
            c.Rel.Contar(contador + " (atualizados)");
            return;
        }
        m = new Memorando
        {
            Id = Guid.NewGuid(), ImovelId = imovelId, AutorId = c.U.Id, Tipo = TipoMemorando.MEMORANDO, Titulo = Corta(titulo, 200)!,
            Corpo = corpo, Fixado = fixado, CriadoEm = criado, AtualizadoEm = c.Agora,
        };
        c.Db.Memorandos.Add(m);
        audit.Registrar(c.Db, c.U.Id, "memorandos", m.Id, "CRIAR", depois: m);
        c.Registrar(notionId, TipoMemorandoReg, m.Id, editadoNotion);
        c.Rel.Contar(contador);
    }

    /* ---- ativos ---- */

    private Task ImportarAtivosAsync(Contexto c, CancellationToken ct)
    {
        foreach (var at in c.L.Ativos)
        {
            var j = at.Json;
            var arquivosProp = NotionLeitor.Arquivos(j, "Imagens").Concat(NotionLeitor.Arquivos(j, "Documentos")).ToList();
            if (AtivosIgnorados.Any(p => Normalizar(at.Titulo).StartsWith(p, StringComparison.Ordinal)))
            {
                c.Rel.Detalhes.Add($"Ativo não importado (decisão do usuário; continua no Notion): {at.Titulo}");
                c.Rel.Contar("ativos não importados (decisão)");
                continue;
            }
            if (c.ImovelDoAtivo(at.Titulo) is not { } imovelId)
            {
                c.Rel.Pendencias.Add($"Ativo \"{at.Titulo}\" sem imóvel definido ({arquivosProp.Count + at.Conteudo.Arquivos.Count} arquivos aguardam).");
                c.Rel.Contar("ativos pendentes");
                continue;
            }
            var corpo = new StringBuilder();
            foreach (var (rot, prop) in new[] { ("Tipo de ativo", "Tipo de Ativo"), ("Estratégia prevista", "Estratégia Prevista"),
                         ("Fator de desenvolvimento", "Fator de Desenvolvimento"), ("Em vitrine", "Em vitrine"), ("Endereço", "Endereço") })
                if (NotionLeitor.Texto(j, prop) is { } v) corpo.Append(rot).Append(": ").Append(v).Append('\n');
            if (c.L.Rel(at, "Cliente") is { Count: > 0 } cli) corpo.Append("Cliente: ").Append(c.Nomes(cli)).Append('\n');
            if (arquivosProp.Count > 0)
                corpo.Append("Arquivos (copiados para 05 - Documentos): ").Append(string.Join(", ", arquivosProp.Select(a => a.Nome))).Append('\n');
            if (at.Link is not null) corpo.Append("Página no Notion: ").Append(at.Link).Append('\n');
            if (at.Conteudo.TextoFinal.Length > 0) corpo.Append('\n').Append(at.Conteudo.TextoFinal);
            Memorando(c, at.Id, imovelId, $"Ativo e diagnose (Notion): {at.Titulo}", corpo.ToString().TrimEnd(), fixado: false,
                at.Criada, at.Editada, "memorandos de ativos");
            foreach (var a in arquivosProp.Concat(at.Conteudo.Arquivos)) c.Arquivos.Add(new ArquivoPendente(a, imovelId, $"ativo {at.Titulo}"));
        }
        return Task.CompletedTask;
    }

    /* ---- tarefas → agenda ---- */

    public static StatusCompromisso StatusTarefa(string? status) => Normalizar(status) switch
    {
        "CONCLUIDO" or "CONCLUIDA" => StatusCompromisso.CONCLUIDO,
        "CANCELADA" or "CANCELADO" => StatusCompromisso.CANCELADO,
        _ => StatusCompromisso.PENDENTE,
    };

    /// <summary>Início e fim do compromisso (UTC). Datas sem hora: 09:00–18:00 de Brasília. Fim antes do início vira o próprio dia.</summary>
    public static (DateTime Inicio, DateTime Fim, bool Ajustado) Horario(DateTime? ini, DateTime? iniFim, bool iniHora, DateTime? fim, DateTime? fimFim, bool fimHora, DateTime criada)
    {
        DateTime Abre(DateTime d, bool hora) => hora ? d.ToUniversalTime() : DatasBR.ParaUtc(d.Date.AddHours(9));
        DateTime Fecha(DateTime d, bool hora) => hora ? d.ToUniversalTime() : DatasBR.ParaUtc(d.Date.AddHours(18));
        DateTime i, f;
        if (ini is { } a)
        {
            i = Abre(a, iniHora);
            f = fim is { } b ? Fecha(fimFim ?? b, fimHora) : iniFim is { } c ? Fecha(c, iniHora) : iniHora ? i.AddHours(1) : Fecha(a, false);
        }
        else if (fim is { } b)
        {
            i = Abre(b, fimHora);
            f = fimFim is { } c ? Fecha(c, fimHora) : fimHora ? i.AddHours(1) : Fecha(b, false);
        }
        else
        {
            var dia = DatasBR.ParaUtc(TimeZoneInfo.ConvertTimeFromUtc(criada, TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")).Date.AddHours(9));
            return (dia, dia.AddHours(9), true);
        }
        if (f < i) return (i, i.AddHours(iniHora || (ini is null && fimHora) ? 1 : 9), true); // 09:00 → 18:00 do mesmo dia
        return (i, f, false);
    }

    private async Task ImportarTarefasAsync(Contexto c, CancellationToken ct)
    {
        foreach (var t in c.L.Tarefas)
        {
            var j = t.Json;
            var acao = NotionLeitor.Texto(j, "Ação");
            if (t.Titulo.Length == 0 && acao is null && t.Conteudo.TextoFinal.Length == 0)
            {
                c.Rel.Contar("tarefas vazias (ignoradas)");
                continue;
            }
            var titulo = t.Titulo.Length > 0 ? t.Titulo : Corta(acao, 120)!;

            // imóvel: projeto da tarefa; senão, palavra-chave no texto
            var projetos = c.L.Rel(t, "Projeto").Select(c.ChaveProjeto).OfType<string>().ToList();
            Guid? imovelId = projetos.Select(c.ImovelDoProjeto).FirstOrDefault(x => x is not null);
            var origem = "projeto";
            if (projetos.Count == 0 && TarefasIgnoradas.Contains(Normalizar(titulo)))
            {
                c.Rel.Detalhes.Add($"Tarefa não importada (decisão do usuário; continua no Notion): {titulo}");
                c.Rel.Contar("tarefas não importadas (decisão)");
                continue;
            }
            if (projetos.Count == 0 && TarefaParaProjeto.TryGetValue(Normalizar(titulo), out var projDecidido))
            {
                imovelId = c.ImovelDoProjeto(projDecidido);
                origem = "decisão do usuário";
            }
            if (imovelId is null && projetos.Count == 0)
            {
                var texto = " " + Normalizar(titulo + " " + acao) + " ";
                var achado = PalavrasTarefa.FirstOrDefault(p => texto.Contains(" " + p.Palavra + " ", StringComparison.Ordinal));
                if (achado.Projeto is not null) { imovelId = c.ImovelDoProjeto(achado.Projeto); origem = $"palavra \"{achado.Palavra}\""; }
            }
            if (imovelId is not { } im)
            {
                c.Rel.Pendencias.Add($"Tarefa \"{titulo}\" sem imóvel" + (projetos.Count > 0 ? $" (projeto {string.Join(", ", projetos)} sem imóvel)" : " (sem projeto no Notion)"));
                c.Rel.Contar("tarefas pendentes");
                continue;
            }

            var (ini, iniFim, iniHora) = NotionLeitor.Data(j, "Data de Início");
            var (fim, fimFim, fimHora) = NotionLeitor.Data(j, "Concluir até");
            var (inicioEm, fimEm, ajustado) = Horario(ini, iniFim, iniHora, fim, fimFim, fimHora, t.Criada);
            var status = NotionLeitor.Texto(j, "Status");
            var resp = c.L.Rel(t, "Responsável");

            var desc = new StringBuilder();
            if (acao is not null) desc.Append(acao).Append("\n\n");
            if (resp.Count > 0) desc.Append("Responsáveis (Notion): ").Append(c.Nomes(resp)).Append('\n');
            desc.Append("Status no Notion: ").Append(status ?? "—").Append('\n');
            if (ajustado)
                desc.Append("Datas no Notion: início ").Append(DataTexto(j, "Data de Início") ?? "—")
                    .Append(", concluir até ").Append(DataTexto(j, "Concluir até") ?? "—").Append('\n');
            if (projetos.Count > 1) desc.Append("Projetos no Notion: ").Append(string.Join(", ", projetos)).Append('\n');
            if (origem != "projeto") desc.Append("Imóvel definido na importação pela ").Append(origem).Append('\n');
            if (t.Link is not null) desc.Append("Página no Notion: ").Append(t.Link).Append('\n');
            if (t.Conteudo.TextoFinal.Length > 0) desc.Append('\n').Append(t.Conteudo.TextoFinal);
            var st = StatusTarefa(status);

            Compromisso? comp = null;
            if (c.Registros.TryGetValue((t.Id, TipoCompromissoReg), out var reg))
            {
                comp = await c.Db.Compromissos.FindAsync([reg.EntidadeId], ct);
                if (comp is null || comp.ExcluidoEm is not null) continue;
                if (JaImportado(c, t.Id, TipoCompromissoReg, t.Editada, comp.AtualizadoEm, out var atualizar) || !atualizar) continue;
                var antes = comp.Copia();
                comp.Titulo = Corta(titulo, 200)!; comp.Descricao = desc.ToString().TrimEnd(); comp.InicioEm = inicioEm; comp.FimEm = fimEm;
                comp.Status = st; comp.ConcluidoEm = st == StatusCompromisso.CONCLUIDO ? comp.ConcluidoEm ?? t.Editada : null;
                comp.AtualizadoEm = c.Agora;
                audit.Registrar(c.Db, c.U.Id, "compromissos", comp.Id, "ATUALIZAR", antes, comp);
                c.Registrar(t.Id, TipoCompromissoReg, comp.Id, t.Editada);
                c.Rel.Contar("compromissos (atualizados)");
                continue;
            }

            if (origem != "projeto") c.Rel.Detalhes.Add($"Tarefa ligada pela {origem}: \"{titulo}\" → imóvel {c.Db.Imoveis.Local.FirstOrDefault(i => i.Id == im)?.Codigo ?? im.ToString()[..8]}");
            comp = new Compromisso
            {
                Id = Guid.NewGuid(), ImovelId = im, Tipo = TipoCompromisso.TAREFA, Titulo = Corta(titulo, 200)!,
                Descricao = desc.ToString().TrimEnd(), InicioEm = inicioEm, FimEm = fimEm, Status = st,
                ConcluidoEm = st == StatusCompromisso.CONCLUIDO ? t.Editada : null, ResponsavelId = c.U.Id,
                CriadoEm = t.Criada, AtualizadoEm = c.Agora,
            };
            c.Db.Compromissos.Add(comp);
            foreach (var pid in resp.Select(r => c.PessoaPorNotion.GetValueOrDefault(r)).Where(g => g != Guid.Empty).Distinct())
                c.Db.CompromissoParticipantes.Add(new CompromissoParticipante { Id = Guid.NewGuid(), CompromissoId = comp.Id, PessoaId = pid });
            audit.Registrar(c.Db, c.U.Id, "compromissos", comp.Id, "CRIAR", depois: comp);
            c.Registrar(t.Id, TipoCompromissoReg, comp.Id, t.Editada);
            c.Rel.Contar(st switch
            {
                StatusCompromisso.CONCLUIDO => "compromissos concluídos",
                StatusCompromisso.CANCELADO => "compromissos cancelados",
                _ => fimEm < c.Agora ? "compromissos pendentes já vencidos" : "compromissos pendentes",
            });
            foreach (var a in t.Conteudo.Arquivos) c.Arquivos.Add(new ArquivoPendente(a, im, $"tarefa {titulo}"));
        }
    }

    /* ---- arquivos ---- */

    private async Task ArquivosAsync(UsuarioAtual u, NotionCliente notion, List<ArquivoPendente> arquivos, bool simular,
        RelatorioNotion rel, Action<string>? progresso, CancellationToken ct)
    {
        HashSet<string> feitos;
        await using (var db = await fabrica.CreateDbContextAsync(ct))
            feitos = [.. await db.NotionImportacoes.Where(r => r.Tipo == TipoArquivoReg).Select(r => r.NotionId).ToListAsync(ct)];
        foreach (var a in arquivos.DistinctBy(x => x.Arquivo.Id))
        {
            if (!a.Arquivo.HospedadoNoNotion) { rel.Contar("links externos (ficaram no texto)"); continue; }
            if (feitos.Contains(a.Arquivo.Id)) continue;
            if (simular) { rel.Contar("arquivos a copiar para o Drive"); rel.Detalhes.Add($"Arquivo: {a.Arquivo.Nome} ({a.Origem})"); continue; }
            try
            {
                progresso?.Invoke($"Copiando {a.Arquivo.Nome}...");
                var (dados, tipo) = await notion.BaixarAsync(a.Arquivo.Url, ct);
                using var ms = new MemoryStream(dados);
                await imoveis.EnviarMidiaAsync(u, a.ImovelId, ms, a.Arquivo.Nome, tipo, SubpastasDrive.Documentos,
                    legenda: $"Do Notion: {a.Origem}", ct: ct, melhorar: false);
                await using var db = await fabrica.CreateDbContextAsync(ct);
                db.NotionImportacoes.Add(new NotionImportacao { NotionId = a.Arquivo.Id, Tipo = TipoArquivoReg, EntidadeId = a.ImovelId, ImportadoEm = DateTime.UtcNow });
                await db.SaveChangesAsync(ct);
                rel.Contar("arquivos copiados para o Drive");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Arquivo do Notion não copiado: {Nome}", a.Arquivo.Nome);
                rel.Pendencias.Add($"Arquivo não copiado: {a.Arquivo.Nome} ({a.Origem}): {ex.Message}");
            }
        }
    }

    private static string? Corta(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..(max - 1)] + "…";
}
