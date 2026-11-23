using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Redes;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Imoveis;

/* ------------------------------------------------ modelos ------------------------------------------------ */

public sealed class DestaquePagina
{
    [MaxLength(30)] public string Valor { get; set; } = "";
    [MaxLength(40)] public string Rotulo { get; set; } = "";
}

public sealed class SecaoPagina
{
    [MaxLength(100)] public string Titulo { get; set; } = "";
    [MaxLength(4000)] public string Texto { get; set; } = "";
}

public sealed class PerguntaPagina
{
    [MaxLength(200)] public string Pergunta { get; set; } = "";
    [MaxLength(1500)] public string Resposta { get; set; } = "";
}

/// <summary>Blocos da página (gravados juntos em jsonb). Linhas começando com "- " viram lista na página.</summary>
public sealed class ConteudoPagina
{
    public List<DestaquePagina> Destaques { get; set; } = [];
    public List<SecaoPagina> Secoes { get; set; } = [];
    public List<string> Diferenciais { get; set; } = [];
    public List<PerguntaPagina> Perguntas { get; set; } = [];
    /// <summary>Condições comerciais (entrada, permuta, prazo...), em destaque perto do botão de contato.</summary>
    public string? Condicoes { get; set; }
    public bool MostrarPreco { get; set; } = true;
    public bool MostrarMapa { get; set; } = true;
    /// <summary>Ficha técnica (áreas, medidas, ano...) vinda do cadastro do imóvel.</summary>
    public bool MostrarFicha { get; set; } = true;

    private static readonly JsonSerializerOptions Opcoes = new(JsonSerializerDefaults.Web);
    public string Json() => JsonSerializer.Serialize(this, Opcoes);
    public static ConteudoPagina De(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<ConteudoPagina>(json, Opcoes) ?? new();
}

public sealed class PaginaForm : IValidatableObject
{
    public const int MaxDestaques = 6, MaxSecoes = 8, MaxDiferenciais = 24, MaxPerguntas = 10;

    [Required(ErrorMessage = "Informe o endereço da página.")]
    [MaxLength(100, ErrorMessage = "Endereço com no máximo 100 caracteres.")]
    [RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$", ErrorMessage = "Use só letras minúsculas sem acento, números e hífen (ex.: casa-jardim-panorama).")]
    public string Slug { get; set; } = "";

    [Required(ErrorMessage = "Informe o título.")]
    [MaxLength(120, ErrorMessage = "Título com no máximo 120 caracteres.")]
    public string Titulo { get; set; } = "";

    [MaxLength(300, ErrorMessage = "Chamada com no máximo 300 caracteres.")]
    public string? Chamada { get; set; }

    [MaxLength(70, ErrorMessage = "Título para o Google com no máximo 70 caracteres (o ideal é até 60).")]
    public string? TituloSeo { get; set; }

    [MaxLength(170, ErrorMessage = "Resumo com no máximo 170 caracteres (o ideal é até 155).")]
    public string? DescricaoSeo { get; set; }

    public Guid? FotoCapaId { get; set; }
    public ConteudoPagina Conteudo { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var c = Conteudo;
        if (c.Destaques.Count > MaxDestaques) yield return new($"No máximo {MaxDestaques} números de destaque.", [nameof(Conteudo)]);
        if (c.Secoes.Count > MaxSecoes) yield return new($"No máximo {MaxSecoes} seções.", [nameof(Conteudo)]);
        if (c.Diferenciais.Count > MaxDiferenciais) yield return new($"No máximo {MaxDiferenciais} diferenciais.", [nameof(Conteudo)]);
        if (c.Perguntas.Count > MaxPerguntas) yield return new($"No máximo {MaxPerguntas} perguntas.", [nameof(Conteudo)]);
        if (c.Diferenciais.Any(d => d.Length > 120)) yield return new("Cada diferencial com no máximo 120 caracteres.", [nameof(Conteudo)]);
        if ((c.Condicoes?.Length ?? 0) > 1000) yield return new("Condições com no máximo 1.000 caracteres.", [nameof(Conteudo)]);
        var itens = c.Destaques.Cast<object>().Concat(c.Secoes).Concat(c.Perguntas);
        foreach (var item in itens)
        {
            var r = new List<ValidationResult>();
            if (!Validator.TryValidateObject(item, new ValidationContext(item), r, true))
                foreach (var e in r) yield return new(e.ErrorMessage ?? "Texto grande demais.", [nameof(Conteudo)]);
        }
    }

    /// <summary>Tira espaços e blocos vazios (linha nova deixada em branco na tela).</summary>
    public void Limpar()
    {
        Slug = Slug.Trim();
        Titulo = Titulo.Trim();
        Chamada = Texto.Limpo(Chamada);
        TituloSeo = Texto.Limpo(TituloSeo);
        DescricaoSeo = Texto.Limpo(DescricaoSeo);
        var c = Conteudo;
        c.Destaques = [.. c.Destaques.Select(d => new DestaquePagina { Valor = d.Valor.Trim(), Rotulo = d.Rotulo.Trim() }).Where(d => d.Valor.Length > 0 || d.Rotulo.Length > 0)];
        c.Secoes = [.. c.Secoes.Select(s => new SecaoPagina { Titulo = s.Titulo.Trim(), Texto = s.Texto.Trim() }).Where(s => s.Titulo.Length > 0 || s.Texto.Length > 0)];
        c.Diferenciais = [.. c.Diferenciais.Select(d => d.Trim()).Where(d => d.Length > 0)];
        c.Perguntas = [.. c.Perguntas.Select(p => new PerguntaPagina { Pergunta = p.Pergunta.Trim(), Resposta = p.Resposta.Trim() }).Where(p => p.Pergunta.Length > 0)];
        c.Condicoes = Texto.Limpo(c.Condicoes);
    }

    public static PaginaForm De(PaginaImovel p) => new()
    {
        Slug = p.Slug, Titulo = p.Titulo, Chamada = p.Chamada, TituloSeo = p.TituloSeo, DescricaoSeo = p.DescricaoSeo,
        FotoCapaId = p.FotoCapaId, Conteudo = ConteudoPagina.De(p.Conteudo),
    };
}

/// <summary>Foto pública do imóvel que pode abrir a página (escolha da capa na tela).</summary>
public sealed record FotoOpcaoPagina(Guid Id, string Miniatura, string? Legenda, bool CapaDoImovel);

/// <summary>Tela de edição: a página gravada (ou null) e o formulário (gravado ou sugerido a partir do cadastro).</summary>
public sealed record PaginaEdicao(PaginaImovel? Pagina, PaginaForm Form, IReadOnlyList<FotoOpcaoPagina> Fotos, string Codigo);

/// <param name="Src">1200 px.</param><param name="Pequena">400 px.</param><param name="Media">800 px, se houver.</param>
public sealed record FotoPagina(Guid Id, string Src, string Pequena, string? Media, string? Legenda);

/// <summary>Tudo o que a página pública mostra. <paramref name="Previa"/>: rascunho visto por um usuário do sistema.</summary>
public sealed record PaginaPublica(
    PaginaImovel Pagina, ConteudoPagina Conteudo, Imovel Imovel, IReadOnlyList<ImovelCaracteristica> Caracteristicas,
    IReadOnlyList<FotoPagina> Fotos, string? CorretorNome, bool Previa);

/// <summary>Proposta da IA: o formulário preenchido e o que falta no cadastro para a página convencer.</summary>
public sealed record PropostaIa(PaginaForm Form, IReadOnlyList<string> Faltando);

/// <summary>Visitas e cliques no WhatsApp de uma origem no período.</summary>
public sealed record LinhaDesempenho(string Origem, int Visitas, int Whatsapp);

/// <summary>Desempenho da página nos últimos <paramref name="Dias"/> dias (Linhas por origem, mais visitas primeiro).</summary>
public sealed record DesempenhoPagina(int Dias, IReadOnlyList<LinhaDesempenho> Linhas, int TotalVisitas, int TotalWhatsapp, DateTime? UltimaVisita);

/* ------------------------------------------------ serviço ------------------------------------------------ */

/// <summary>
/// Página exclusiva do imóvel (landing page) em /imovel/{slug}: link para mandar a clientes e investidores.
/// Publicada, abre sem login e mostra só o que é público no cadastro (fotos públicas, características públicas,
/// endereço só se for público). Rascunho: só usuários do sistema com acesso ao imóvel veem (pré-visualização).
/// </summary>
public sealed class PaginasImovelService(
    IDbContextFactory<InovabensDb> fabrica, AuditService audit, PublicacaoFotosService publicacao,
    IArmazenamento drive, IArmazenamentoPublico publico, IGeradorPagina gerador)
{
    public const string Prefixo = "imovel";

    public static string Caminho(string slug) => $"{Prefixo}/{slug}";

    public async Task<PaginaEdicao> EdicaoAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.ImoveisVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var im = await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var pagina = await db.PaginasImovel.AsNoTracking().SingleOrDefaultAsync(p => p.ImovelId == imovelId);
        var fotos = (await FotosPublicas(db, imovelId))
            .Select(m => new FotoOpcaoPagina(m.Id, Fotos(m, null).Pequena, m.Legenda ?? m.NomeOriginal, m.Capa)).ToList();
        PaginaForm form;
        if (pagina is not null) form = PaginaForm.De(pagina);
        else
        {
            var carac = await db.ImovelCaracteristicas.AsNoTracking().Where(c => c.ImovelId == imovelId && c.Publico).ToListAsync();
            form = Sugestao(im, carac);
            form.Slug = await SlugLivre(db, form.Slug, imovelId);
        }
        return new(pagina, form, fotos, im.Referencia ?? im.Codigo);
    }

    /// <summary>Sugestão montada de novo a partir do cadastro (botão "Restaurar sugestão"); não grava nada.</summary>
    public async Task<PaginaForm> SugestaoAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.ImoveisVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var im = await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var carac = await db.ImovelCaracteristicas.AsNoTracking().Where(c => c.ImovelId == imovelId && c.Publico).ToListAsync();
        var form = Sugestao(im, carac);
        form.Slug = await SlugLivre(db, form.Slug, imovelId);
        return form;
    }

    /// <summary>Fotos públicas enviadas à IA (capa primeiro): bastam para ela ver o imóvel sem encarecer o pedido.</summary>
    public const int MaxFotosIa = 6;
    public const int MaxInstrucoesIa = 2000;

    public bool IaConfigurada => gerador.Configurado;

    /// <summary>
    /// Proposta de página escrita pelo Claude a partir do cadastro, das fotos públicas e do pedido do usuário (vazio = a
    /// IA decide). Não grava nada: a tela mostra a proposta e o usuário aprova antes de salvar. Cada chamada é cobrada
    /// pela Anthropic; fica na auditoria do imóvel.
    /// </summary>
    public async Task<PropostaIa> GerarComIaAsync(UsuarioAtual u, Guid imovelId, string? instrucoes, CancellationToken ct = default)
    {
        u.Exigir(P.ImoveisEditar);
        instrucoes = Texto.Limpo(instrucoes);
        if (instrucoes?.Length > MaxInstrucoesIa)
            throw new RegraNegocioException("instrucoes", $"Pedido com no máximo {MaxInstrucoesIa:N0} caracteres.");
        if (!gerador.Configurado)
            throw new RegraNegocioException("ia", "Configure a chave do Claude em Administração → Configurações para gerar com IA.");

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var im = await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var carac = await db.ImovelCaracteristicas.AsNoTracking().Where(c => c.ImovelId == imovelId && c.Publico)
            .OrderBy(c => c.Grupo).ThenBy(c => c.Chave).ToListAsync(ct);
        var fotos = new List<byte[]>();
        foreach (var m in (await FotosPublicas(db, imovelId, ct)).OrderByDescending(m => m.Capa).ThenBy(m => m.Ordem).Take(MaxFotosIa))
            // foto que sumiu do Drive ou em formato sem suporte fica de fora: a IA trabalha com as que houver
            if (await drive.BaixarAsync(m.ArquivoChave, ct) is { } arq && ImagemPost.Reduzir(arq.Dados, 1024) is { } jpeg) fotos.Add(jpeg);

        var g = await gerador.GerarAsync(Dados(im, carac, fotos), instrucoes, ct);
        audit.Registrar(db, u.Id, "imoveis", imovelId, "GERAR_PAGINA_IA", depois: new { instrucoes, fotos = fotos.Count });
        await db.SaveChangesAsync(ct);
        return new(Converter(g), [.. g.Faltando.Select(f => Cortar(f.Trim(), 200)).Where(f => f.Length > 0).Distinct().Take(10)]);
    }

    /* ------------------------------ medição (visitas e cliques no WhatsApp) ------------------------------ */

    /// <summary>Origens aceitas no utm_source dos links (as da aba Página e as redes onde o link costuma ir).</summary>
    public static readonly string[] Origens = ["link", "whatsapp", "email", "qrcode", "instagram", "facebook", "google"];

    /// <summary>utm_source da URL; sem utm, "google" se veio da busca do Google, senão "direto". Fora da lista: "outro".</summary>
    public static string Origem(string? utm, string? referer)
    {
        var o = utm?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(o)) return Origens.Contains(o) ? o : "outro";
        return Uri.TryCreate(referer, UriKind.Absolute, out var r) && r.Host.Contains("google.", StringComparison.OrdinalIgnoreCase) ? "google" : "direto";
    }

    private static readonly Regex Robo = new("bot|crawl|spider|slurp|preview|facebookexternalhit|whatsapp|telegram|skype|lighthouse|headless|curl|wget|python|java/|monitor",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Robôs (buscadores, leitores de prévia de link, medições) não contam como visita.</summary>
    public static bool EhRobo(string? userAgent) => string.IsNullOrWhiteSpace(userAgent) || Robo.IsMatch(userAgent);

    /// <summary>
    /// Conta uma visita ou um clique no WhatsApp da página publicada. Só a contagem: nada do visitante é gravado.
    /// Falha aqui nunca derruba a página (a medição é acessória).
    /// </summary>
    public async Task RegistrarEventoAsync(string slug, string tipo, string origem, CancellationToken ct = default)
    {
        if (tipo is not (PaginaImovelEvento.Visita or PaginaImovelEvento.Whatsapp)) return;
        try
        {
            await using var db = await fabrica.CreateDbContextAsync(ct);
            var id = await db.PaginasImovel.Where(p => p.Slug == slug && p.Publicada
                    && db.Imoveis.Any(i => i.Id == p.ImovelId && i.ExcluidoEm == null))
                .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
            if (id is null) return;
            db.PaginasImovelEventos.Add(new PaginaImovelEvento { PaginaId = id.Value, Tipo = tipo, Origem = origem is "direto" or "outro" || Origens.Contains(origem) ? origem : "outro" });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // contador perdido não é motivo para o visitante ver erro
        }
    }

    public async Task<DesempenhoPagina> DesempenhoAsync(UsuarioAtual u, Guid imovelId, int dias = 30)
    {
        u.Exigir(P.ImoveisVer);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var desde = DateTime.UtcNow.AddDays(-dias);
        var q = db.PaginasImovelEventos.Where(e => db.PaginasImovel.Any(p => p.Id == e.PaginaId && p.ImovelId == imovelId));
        var grupos = await q.Where(e => e.CriadoEm >= desde).GroupBy(e => new { e.Origem, e.Tipo })
            .Select(g => new { g.Key.Origem, g.Key.Tipo, N = g.Count() }).ToListAsync();
        var linhas = grupos.GroupBy(g => g.Origem)
            .Select(g => new LinhaDesempenho(g.Key,
                g.Where(x => x.Tipo == PaginaImovelEvento.Visita).Sum(x => x.N),
                g.Where(x => x.Tipo == PaginaImovelEvento.Whatsapp).Sum(x => x.N)))
            .OrderByDescending(l => l.Visitas).ThenByDescending(l => l.Whatsapp).ToList();
        var ultima = await q.Where(e => e.Tipo == PaginaImovelEvento.Visita).MaxAsync(e => (DateTime?)e.CriadoEm);
        return new(dias, linhas, linhas.Sum(l => l.Visitas), linhas.Sum(l => l.Whatsapp), ultima);
    }

    internal static DadosPost Dados(Imovel i, IEnumerable<ImovelCaracteristica> caracteristicas, IReadOnlyList<byte[]> fotos)
    {
        var locacao = i.Finalidade == FinalidadeImovel.LOCACAO;
        var carac = caracteristicas.Where(c => c.Publico)
            .Select(c => string.IsNullOrWhiteSpace(c.Valor) || c.Valor.Equals("sim", StringComparison.OrdinalIgnoreCase) ? c.Chave : $"{c.Chave}: {c.Valor}")
            .Take(40).ToList();
        return new DadosPost(i.Finalidade, i.Tipo, i.Subtipo, i.Titulo, i.DescricaoPublica, i.Bairro, i.Cidade, i.Uf,
            i.Quartos, i.Suites, i.Banheiros, i.Vagas, i.AreaUtilM2 ?? i.AreaConstruidaM2 ?? i.AreaTotalM2 ?? i.AreaTerrenoM2,
            locacao ? i.PrecoLocacao : i.PrecoVenda ?? i.PrecoLocacao, i.ValorCondominio, i.ValorIptu, i.AceitaPermuta,
            carac, i.Referencia ?? i.Codigo, null, null, null, fotos);
    }

    /// <summary>Proposta da IA dentro dos limites da tela (textos cortados em palavra, blocos vazios fora).</summary>
    public static PaginaForm Converter(PaginaGerada g)
    {
        static string C(string? t, int max) => Cortar((t ?? "").Trim(), max);
        var titulo = C(g.Titulo, 120);
        return new PaginaForm
        {
            Titulo = titulo,
            Chamada = Texto.Limpo(C(g.Chamada, 300)),
            TituloSeo = Texto.Limpo(C(g.TituloSeo, 70)),
            DescricaoSeo = Texto.Limpo(C(g.DescricaoSeo, 170)),
            Conteudo = new ConteudoPagina
            {
                Destaques = [.. g.Destaques.Select(d => new DestaquePagina { Valor = C(d.Valor, 30), Rotulo = C(d.Rotulo, 40) })
                    .Where(d => d.Valor.Length > 0).Take(PaginaForm.MaxDestaques)],
                Secoes = [.. g.Secoes.Select(s => new SecaoPagina { Titulo = C(s.Titulo, 100), Texto = C(s.Texto, 4000) })
                    .Where(s => s.Texto.Length > 0).Take(PaginaForm.MaxSecoes)],
                Diferenciais = [.. g.Diferenciais.Select(d => C(d, 120)).Where(d => d.Length > 0).Distinct().Take(PaginaForm.MaxDiferenciais)],
                Perguntas = [.. g.Perguntas.Select(p => new PerguntaPagina { Pergunta = C(p.Pergunta, 200), Resposta = C(p.Resposta, 1500) })
                    .Where(p => p.Pergunta.Length > 0 && p.Resposta.Length > 0).Take(PaginaForm.MaxPerguntas)],
                Condicoes = Texto.Limpo(C(g.Condicoes, 1000)),
            },
        };
    }

    public async Task SalvarAsync(UsuarioAtual u, Guid imovelId, PaginaForm form)
    {
        u.Exigir(P.ImoveisEditar);
        form.Limpar();
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        if (await db.PaginasImovel.AnyAsync(p => p.Slug == form.Slug && p.ImovelId != imovelId))
            throw new RegraNegocioException(nameof(PaginaForm.Slug), "Este endereço já é usado pela página de outro imóvel.");
        if (form.FotoCapaId is { } capa && !(await FotosPublicas(db, imovelId)).Any(m => m.Id == capa))
            throw new RegraNegocioException(nameof(PaginaForm.FotoCapaId), "A foto de abertura precisa ser uma foto pública deste imóvel.");

        var p = await db.PaginasImovel.SingleOrDefaultAsync(x => x.ImovelId == imovelId);
        var antes = p?.Copia();
        if (p is null)
        {
            p = new PaginaImovel { ImovelId = imovelId, CriadoPor = u.Id };
            db.PaginasImovel.Add(p);
        }
        else if (p.Publicada && p.Slug != form.Slug)
            // o link já foi enviado a clientes: trocar o endereço de uma página no ar quebraria esses links
            throw new RegraNegocioException(nameof(PaginaForm.Slug), "Para mudar o endereço, tire a página do ar antes (links já enviados deixariam de abrir).");
        p.Slug = form.Slug;
        p.Titulo = form.Titulo;
        p.Chamada = form.Chamada;
        p.TituloSeo = form.TituloSeo;
        p.DescricaoSeo = form.DescricaoSeo;
        p.FotoCapaId = form.FotoCapaId;
        p.Conteudo = form.Conteudo.Json();
        p.AtualizadoPor = u.Id;
        p.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "paginas_imovel", p.Id, antes is null ? "CRIAR" : "ATUALIZAR", antes, p);
        await db.SaveChangesAsync();
    }

    /// <summary>Põe no ar (abre sem login) ou tira do ar (volta a rascunho; o link deixa de abrir para quem não é do sistema).</summary>
    public async Task PublicarAsync(UsuarioAtual u, Guid imovelId, bool publicar)
    {
        u.Exigir(P.ImoveisEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var p = await db.PaginasImovel.SingleOrDefaultAsync(x => x.ImovelId == imovelId)
            ?? throw new RegraNegocioException("", "Salve a página antes de publicar.");
        if (p.Publicada == publicar) return;
        var antes = p.Copia();
        p.Publicada = publicar;
        if (publicar) p.PublicadaEm = DateTime.UtcNow;
        p.AtualizadoPor = u.Id;
        p.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "paginas_imovel", p.Id, publicar ? "PUBLICAR" : "DESPUBLICAR", antes, p);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A página pelo endereço: publicada para qualquer visitante; rascunho só para usuário do sistema com acesso ao
    /// imóvel. Null = não existe (ou não pode ver): a tela responde 404, sem revelar que há um rascunho.
    /// </summary>
    public async Task<PaginaPublica?> PublicaAsync(string slug, UsuarioAtual? u, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var (p, im) = await Visivel(db, slug, u, ct);
        if (p is null || im is null) return null;
        var carac = await db.ImovelCaracteristicas.AsNoTracking().Where(c => c.ImovelId == im.Id && c.Publico)
            .OrderBy(c => c.Grupo).ThenBy(c => c.Chave).ToListAsync(ct);
        var fotos = (await FotosPublicas(db, im.Id, ct))
            .OrderByDescending(m => m.Id == p.FotoCapaId).ThenByDescending(m => m.Capa).ThenBy(m => m.Ordem)
            .Select(m => Fotos(m, p.Slug)).ToList();
        var corretor = im.CorretorResponsavelId is { } cid
            ? await db.Usuarios.Where(x => x.Id == cid).Select(x => x.Nome).FirstOrDefaultAsync(ct)
            : null;
        return new(p, ConteudoPagina.De(p.Conteudo), im, carac, fotos, corretor, Previa: !p.Publicada);
    }

    /// <summary>Foto pública do imóvel da página (original do Drive), para quem pode ver a página. Null = não pode.</summary>
    public async Task<ConteudoArquivo?> FotoAsync(string slug, Guid midiaId, string arquivo, UsuarioAtual? u, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var (p, im) = await Visivel(db, slug, u, ct);
        if (p is null || im is null) return null;
        var m = await db.ImovelMidias.AsNoTracking().SingleOrDefaultAsync(x => x.Id == midiaId && x.ImovelId == im.Id
            && x.Publica && x.Tipo == TipoMidia.FOTO && x.ArquivoChave == arquivo, ct);
        return m is null ? null : await drive.BaixarAsync(m.ArquivoChave, ct);
    }

    /// <summary>
    /// Imagem da prévia do link (1200×630 JPEG): a foto de abertura, pela cópia pública de 1200 px no R2 quando houver
    /// (mais leve que o original do Drive). Null = sem foto pública ou sem acesso.
    /// </summary>
    public async Task<(byte[] Jpeg, string Versao)?> ImagemCompartilhamentoAsync(string slug, UsuarioAtual? u, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var (p, im) = await Visivel(db, slug, u, ct);
        if (p is null || im is null) return null;
        var m = (await FotosPublicas(db, im.Id, ct))
            .OrderByDescending(x => x.Id == p.FotoCapaId).ThenByDescending(x => x.Capa).ThenBy(x => x.Ordem).FirstOrDefault();
        if (m is null) return null;
        byte[]? dados = null;
        if (PublicacaoFotosService.Situacao(m) == SituacaoPublicacao.Publicada)
            dados = await publico.BaixarAsync(PublicacaoFotosService.ChaveVersao(m.PublicaChave!, PublicacaoFotosService.LarguraFicha), ct);
        dados ??= (await drive.BaixarAsync(m.ArquivoChave, ct))?.Dados;
        return dados is not null && ImagemFoto.Compartilhamento(dados) is { } jpeg ? (jpeg, m.ArquivoChave) : null;
    }

    /// <summary>Versão da imagem de prévia (muda quando a foto de abertura muda): entra no endereço para o cache dos apps.</summary>
    public static string VersaoImagem(PaginaPublica pg) =>
        pg.Fotos.Count == 0 ? "0" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(pg.Fotos[0].Src)))[..10].ToLowerInvariant();

    private static async Task<(PaginaImovel?, Imovel?)> Visivel(InovabensDb db, string slug, UsuarioAtual? u, CancellationToken ct)
    {
        var p = await db.PaginasImovel.AsNoTracking().SingleOrDefaultAsync(x => x.Slug == slug, ct);
        if (p is null) return (null, null);
        var im = await db.Imoveis.AsNoTracking().SingleOrDefaultAsync(i => i.Id == p.ImovelId && i.ExcluidoEm == null, ct);
        if (im is null) return (null, null);
        if (p.Publicada) return (p, im);
        var podeVer = u is not null && u.Pode(P.ImoveisVer) && (!u.EscopoProprio || im.CorretorResponsavelId == u.Id);
        return podeVer ? (p, im) : (null, null);
    }

    private static Task<List<ImovelMidia>> FotosPublicas(InovabensDb db, Guid imovelId, CancellationToken ct = default) =>
        db.ImovelMidias.AsNoTracking()
            .Where(m => m.ImovelId == imovelId && m.Publica && m.Tipo == TipoMidia.FOTO && m.ArquivoChave != "")
            .OrderBy(m => m.Ordem).ToListAsync(ct);

    /// <summary>
    /// Endereços da foto: a cópia pública do R2 quando publicada; senão a rota da página (sem login, só para fotos
    /// públicas). Sem slug (tela do sistema), a rota interna, que exige login.
    /// </summary>
    private FotoPagina Fotos(ImovelMidia m, string? slug)
    {
        if (PublicacaoFotosService.Situacao(m) == SituacaoPublicacao.Publicada && publicacao.Url(m) is { } r2)
            return new(m.Id, r2, publicacao.Url(m, ImoveisService.LarguraMiniatura)!,
                m.PublicadaMedia ? publicacao.Url(m, ImoveisService.LarguraMedia) : null, m.Legenda);
        var baseUrl = slug is null
            ? $"imoveis/midias/{m.Id}/{Uri.EscapeDataString(m.ArquivoChave)}"
            : $"{Caminho(slug)}/foto/{m.Id}/{Uri.EscapeDataString(m.ArquivoChave)}";
        return new(m.Id, $"{baseUrl}?l={ImoveisService.LarguraTela}", $"{baseUrl}?l={ImoveisService.LarguraMiniatura}",
            $"{baseUrl}?l={ImoveisService.LarguraMedia}", m.Legenda);
    }

    /* ------------------------------ sugestão a partir do cadastro (sem IA) ------------------------------ */

    private static readonly CultureInfo Br = Texto.PtBr;

    /// <summary>
    /// Primeiro rascunho montado só com o que está no cadastro (nada inventado): título, chamada com tipo e local,
    /// números de destaque, a descrição pública, as características públicas e as condições (preço, permuta, condomínio).
    /// </summary>
    public static PaginaForm Sugestao(Imovel im, IEnumerable<ImovelCaracteristica> caracteristicas)
    {
        var locacao = im.Finalidade == FinalidadeImovel.LOCACAO;
        var tipo = im.Subtipo ?? Texto.Rotulo(im.Tipo);
        var onde = string.Join(" - ", new[] { string.Join(", ", new[] { im.Bairro, im.Cidade }.Where(x => !string.IsNullOrWhiteSpace(x))), im.Uf }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        var chamada = $"{tipo} {(locacao ? "para alugar" : "à venda")}{(onde.Length > 0 ? " em " + onde : "")}.";

        var destaques = new List<DestaquePagina>();
        void D(string valor, string rotulo) { if (destaques.Count < 4) destaques.Add(new() { Valor = valor, Rotulo = rotulo }); }
        if (im.AreaTerrenoM2 is { } t) D(Area(t), "de terreno");
        if ((im.AreaConstruidaM2 ?? im.AreaUtilM2) is { } a) D(Area(a), im.AreaConstruidaM2 is not null ? "construídos" : "de área útil");
        else if (im.AreaTerrenoM2 is null && im.AreaTotalM2 is { } tot) D(Area(tot), "de área total");
        if (im.Quartos is > 0 and var q) D(q.ToString(Br), q > 1 ? "quartos" : "quarto");
        if (im.Suites is > 0 and var s) D(s.ToString(Br), s > 1 ? "suítes" : "suíte");
        if (im.Vagas is > 0 and var v) D(v.ToString(Br), v > 1 ? "vagas" : "vaga");

        var secoes = new List<SecaoPagina>();
        if (Texto.Limpo(im.DescricaoPublica) is { } descricao) secoes.Add(new() { Titulo = "Sobre o imóvel", Texto = descricao });

        var condicoes = new List<string>();
        if (im.AceitaPermuta) condicoes.Add("Aceita permuta.");
        if (im.ValorCondominio is { } cond) condicoes.Add($"Condomínio de {cond.ToString("C0", Br)} por mês.");
        if (im.ValorIptu is { } iptu) condicoes.Add($"IPTU de {iptu.ToString("C0", Br)}.");

        var titulo = im.Titulo.Length <= 120 ? im.Titulo : im.Titulo[..120];
        var seo = $"{tipo} {(locacao ? "para alugar" : "à venda")}{(im.Cidade is { } c ? " em " + c : "")}";
        return new PaginaForm
        {
            // cidade no endereço só se o título ainda não a tem (evita "...-cedral-cedral")
            Slug = Slug(im.Cidade is { Length: > 0 } cid && !Slug(im.Titulo).Contains(Slug(cid)) ? $"{im.Titulo} {cid}" : im.Titulo),
            Titulo = titulo,
            Chamada = chamada,
            TituloSeo = Cortar($"{titulo} | {im.Cidade ?? "Inovabens"}", 60),
            DescricaoSeo = Cortar(string.Join(". ", new[] { seo, string.Join(", ", destaques.Take(3).Select(d => $"{d.Valor} {d.Rotulo}")) }
                .Where(x => x.Length > 0)) + ". Fale com a Inovabens.", 155),
            Conteudo = new ConteudoPagina
            {
                Destaques = destaques,
                Secoes = secoes,
                Diferenciais = [.. caracteristicas.Where(c => c.Publico).Select(c => c.Valor is null ? c.Chave : $"{c.Chave}: {c.Valor}").Take(PaginaForm.MaxDiferenciais)],
                Condicoes = condicoes.Count > 0 ? string.Join(' ', condicoes) : null,
            },
        };
    }

    /// <summary>"Espaço Flamboyant - Aparecida do Taboado" → "espaco-flamboyant-aparecida-do-taboado" (até 80 caracteres, cortado em palavra).</summary>
    public static string Slug(string texto)
    {
        var sb = new StringBuilder();
        foreach (var c in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }
        var slug = Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
        if (slug.Length > 80) slug = slug[..(slug.LastIndexOf('-', 80) is var i and > 20 ? i : 80)].Trim('-');
        return slug.Length == 0 ? "imovel" : slug;
    }

    private static async Task<string> SlugLivre(InovabensDb db, string slug, Guid imovelId)
    {
        var usados = await db.PaginasImovel.Where(p => p.ImovelId != imovelId && p.Slug.StartsWith(slug)).Select(p => p.Slug).ToListAsync();
        var livre = slug;
        for (var i = 2; usados.Contains(livre); i++) livre = $"{slug}-{i}";
        return livre;
    }

    private static string Area(decimal m2) => m2 >= 10_000
        ? $"{(m2 / 10_000).ToString("0.##", Br)} ha"
        : $"{m2.ToString(m2 % 1 == 0 ? "N0" : "N2", Br)} m²";

    private static string Cortar(string t, int max)
    {
        if (t.Length <= max) return t;
        var corte = t.LastIndexOf(' ', max - 1);
        return t[..(corte > 20 ? corte : max - 1)].TrimEnd(',', '.', ' ', '-', '|') + "…";
    }
}
