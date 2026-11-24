using System.Text.Json;
using Inovabens.Core.Crm;
using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Agenda;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Financeiro;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Pessoas;
using Inovabens.Core.Relatorios;
using Inovabens.Core.Servicos;
using Inovabens.Web;
using Inovabens.Web.Components;
using Inovabens.Web.Jobs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Mesmas variáveis de ambiente da versão Node (DATABASE_URL, TOKEN_ENC_KEY...), mais GOOGLE_DRIVE_* para os arquivos.
var opcoes = InovabensOpcoes.De(builder.Configuration, builder.Environment.IsProduction());
builder.Services.AddInovabensCore(opcoes);
builder.Services.AddHostedService<SincronizacaoIndicesJob>();
builder.Services.AddHostedService<TaxasFinanciamentoJob>();
builder.Services.AddHostedService<AlertasCompromissosJob>();
builder.Services.AddHostedService<ExpiracaoPropostasJob>();
builder.Services.AddHostedService<PrazosEtapasJob>();
builder.Services.AddHostedService<RotinaFinanceiraDiariaJob>();
builder.Services.AddHostedService<SincronizacaoGoogleJob>();
builder.Services.AddHostedService<RenovacaoCanaisGoogleJob>();
builder.Services.AddHostedService<MelhoriaFotosJob>();
builder.Services.AddHostedService<PublicacaoFotosJob>();
builder.Services.AddHostedService<RenomearPastasDriveJob>();
builder.Services.AddHostedService<PostsRedesJob>();
builder.Services.AddHostedService<LimiteGratuitoR2Job>();
builder.Services.AddSingleton<RenovacaoSessao>();

builder.Services.AddSingleton<FotosReduzidas>();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Chaves que assinam o cookie de sessão no banco: valem em todas as instâncias e sobrevivem a deploys/reinícios
// (no disco do container elas se perderiam e todos os usuários seriam desconectados).
builder.Services.AddDataProtection()
    .SetApplicationName("Inovabens")
    .PersistKeysToDbContext<InovabensDb>();

// Autenticação por cookie (substitui JWT + refresh da API Node: o Blazor Server roda no servidor).
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(UsuarioAtual.Esquema)
    .AddCookie(UsuarioAtual.Esquema, o =>
    {
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/sem-acesso";
        o.Cookie.Name = "inovabens.sessao";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = RevalidarSessao;
    });

// Uma policy por permissão: [Authorize(Policy = Permissoes.ImoveisVer)]
builder.Services.AddAuthorization(o =>
{
    foreach (var p in Permissoes.Todas)
        o.AddPolicy(p, b => b.RequireClaim(UsuarioAtual.ClaimPermissao, p));
});

// Atrás de proxy (Render/Cloud Run): IP real do cliente para a auditoria.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = int.TryParse(builder.Configuration["TRUST_PROXY"], out var n) ? n : 1;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// Tentativas de login por IP (como o @Throttle da versão Node: 10/min). Complementa o bloqueio por conta, que não
// impede testar uma senha comum em muitas contas. LOGIN_LIMITE_POR_MINUTO ajusta o limite.
var limiteLogin = int.TryParse(builder.Configuration["LOGIN_LIMITE_POR_MINUTO"], out var l) && l > 0 ? l : 10;
builder.Services.AddRateLimiter(o =>
{
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c =>
        // site aberto a visitantes (páginas e fotos sem login): teto generoso por endereço, contra robôs que varrem tudo
        // (a lista pode ter 200 cartões, cada um com a sua foto)
        !HttpMethods.IsPost(c.Request.Method) ? (c.Request.Path.StartsWithSegments("/site")
            ? RateLimitPartition.GetFixedWindowLimiter("site:" + (c.Connection.RemoteIpAddress?.ToString() ?? "?"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) })
            : RateLimitPartition.GetNoLimiter(""))
        : c.Request.Path.Equals("/login", StringComparison.OrdinalIgnoreCase)
            ? RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "?",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limiteLogin, Window = TimeSpan.FromMinutes(1) })
        // formulário de contato das páginas de imóvel (público): poucos envios por endereço, contra spam
        : c.Request.Path.StartsWithSegments("/" + PaginasImovelService.Prefixo) && c.Request.Path.Value!.EndsWith("/formulario", StringComparison.Ordinal)
            ? RateLimitPartition.GetFixedWindowLimiter("form:" + (c.Connection.RemoteIpAddress?.ToString() ?? "?"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(10) })
            : RateLimitPartition.GetNoLimiter(""));
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, ct) => new ValueTask(ctx.HttpContext.Response.WriteAsync(
        ctx.HttpContext.Request.Path.Value?.EndsWith("/formulario") == true
            ? "Muitos envios a partir deste endereço. Aguarde alguns minutos ou fale conosco pelo WhatsApp."
            : "Muitas tentativas de login a partir deste endereço. Aguarde um minuto e tente de novo.", ct));
});

var app = builder.Build();

// Tabelas e dados-base: aplicados a cada subida (idempotente, com lock entre instâncias).
// `dotnet Inovabens.Web.dll migrar` faz só isso e sai — use como passo de release, se preferir.
await app.Services.GetRequiredService<Migrador>().AplicarAsync();
await app.Services.GetRequiredService<DadosIniciais>().GarantirAsync();
// integrações salvas em Administração → Configurações valem por cima das variáveis de ambiente
using (var escopo = app.Services.CreateScope())
    await escopo.ServiceProvider.GetRequiredService<Inovabens.Core.Admin.ConfiguracoesService>().CarregarAsync();
if (args.Contains("migrar")) return;

// `dotnet Inovabens.Web.dll localizar-imoveis`: preenche as coordenadas dos imóveis sem localização (para o mapa).
if (args.Contains("localizar-imoveis"))
{
    using var escopo = app.Services.CreateScope();
    var lote = escopo.ServiceProvider.GetRequiredService<Inovabens.Core.Imoveis.LocalizacaoLoteService>();
    // --refinar=CA0001,TE0002: refaz esses pelo endereço completo e só troca quando o ponto novo é exato
    if (args.FirstOrDefault(a => a.StartsWith("--refinar=")) is { } refinar)
    {
        var codigos = refinar["--refinar=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rr = await lote.RefinarAsync(codigos, Console.WriteLine);
        Console.WriteLine($"Refino concluído: {rr.Refinados} de {rr.Total} com ponto exato; mantidos: {rr.Mantidos.Count}.");
    }
    var r = await lote.LocalizarPendentesAsync(Console.WriteLine);
    Console.WriteLine($"Concluído: {r.Pendentes} sem coordenadas; {r.PeloEndereco} pelo endereço, {r.PelaRua} pela rua, " +
                      $"{r.PeloBairro} pelo bairro; {r.NaoEncontrados.Count} não encontrados" +
                      (r.NaoEncontrados.Count > 0 ? $" ({string.Join(", ", r.NaoEncontrados)})." : "."));
    return;
}

// `dotnet Inovabens.Web.dll criar-pasta-financeiro`: cria "07 - Financeiro" na pasta do Drive dos imóveis que ainda não têm.
if (args.Contains("criar-pasta-financeiro"))
{
    using var escopo = app.Services.CreateScope();
    var (criadas, existentes, falhas) = await escopo.ServiceProvider.GetRequiredService<Inovabens.Core.Imoveis.PastasDriveService>()
        .CriarPastaFinanceiroExistentesAsync(Console.WriteLine);
    Console.WriteLine($"Concluído: {criadas} criadas, {existentes} já existiam, {falhas} falhas.");
    return;
}

// `dotnet Inovabens.Web.dll importar-imobibrasil [--usuario=email] [--site=https://...] [--sem-fotos]`
// traz os anúncios ativos do site atual (Imobibrasil); rodar de novo atualiza sem duplicar.
if (args.Contains("importar-imobibrasil"))
{
    string? Arg(string nome) => args.FirstOrDefault(a => a.StartsWith($"--{nome}=", StringComparison.Ordinal))?[(nome.Length + 3)..];
    using var escopo = app.Services.CreateScope();
    var sp = escopo.ServiceProvider;
    var fabricaDb = sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<InovabensDb>>();
    Guid usuarioId;
    await using (var db = await fabricaDb.CreateDbContextAsync())
    {
        var email = Arg("usuario");
        usuarioId = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            db.Usuarios.Where(x => x.Ativo && x.ExcluidoEm == null && (email == null ? x.Perfil!.Nome == "Administrador" : x.Email == email))
                .OrderBy(x => x.CriadoEm).Select(x => x.Id));
    }
    var usuario = usuarioId == Guid.Empty ? null : await sp.GetRequiredService<AuthService>().RecarregarAsync(usuarioId);
    if (usuario is null)
    {
        Console.Error.WriteLine("Usuário não encontrado (informe --usuario=email de um usuário ativo).");
        Environment.ExitCode = 1;
        return;
    }
    var itens = await sp.GetRequiredService<ImportacaoImobibrasilService>().ImportarAsync(usuario,
        Arg("site") ?? opcoes.Integracoes.ImobibrasilSite, comFotos: !args.Contains("--sem-fotos"), progresso: Console.WriteLine);
    Console.WriteLine($"Concluído: {itens.Count(i => i.Novo)} novos, {itens.Count(i => !i.Novo && i.Codigo != "—")} atualizados, " +
                      $"{itens.Count(i => i.Codigo == "—")} com falha, {itens.Sum(i => i.FotosNovas)} fotos enviadas.");
    return;
}

// `dotnet Inovabens.Web.dll importar-imobibrasil-painel --arquivo=dados.json [--usuario=email] [--sem-fotos]`
// importa o painel do Imobibrasil (imóveis ativos e inativos, fotos, pessoas, leads, negócios, agenda) do JSON extraído
// do backup. Só lê o arquivo (nada muda no Imobibrasil); rodar de novo completa sem duplicar.
if (args.Contains("importar-imobibrasil-painel"))
{
    string? Arg(string nome) => args.FirstOrDefault(a => a.StartsWith($"--{nome}=", StringComparison.Ordinal))?[(nome.Length + 3)..];
    if (Arg("arquivo") is not { } arquivo || !File.Exists(arquivo))
    {
        Console.Error.WriteLine("Informe --arquivo=caminho do JSON extraído do painel do Imobibrasil.");
        Environment.ExitCode = 1;
        return;
    }
    using var escopo = app.Services.CreateScope();
    var sp = escopo.ServiceProvider;
    var fabricaDb = sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<InovabensDb>>();
    Guid usuarioId;
    await using (var db = await fabricaDb.CreateDbContextAsync())
    {
        var email = Arg("usuario");
        usuarioId = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            db.Usuarios.Where(x => x.Ativo && x.ExcluidoEm == null && (email == null ? x.Perfil!.Nome == "Administrador" : x.Email == email))
                .OrderBy(x => x.CriadoEm).Select(x => x.Id));
    }
    var usuario = usuarioId == Guid.Empty ? null : await sp.GetRequiredService<AuthService>().RecarregarAsync(usuarioId);
    if (usuario is null)
    {
        Console.Error.WriteLine("Usuário não encontrado (informe --usuario=email de um usuário ativo).");
        Environment.ExitCode = 1;
        return;
    }
    var dados = await Inovabens.Core.Imoveis.DadosPainelImobibrasil.LerAsync(arquivo);
    var rel = await sp.GetRequiredService<Inovabens.Core.Imoveis.ImportacaoPainelImobibrasilService>().ImportarAsync(usuario, dados,
        comFotos: !args.Contains("--sem-fotos"), progresso: Console.WriteLine);
    Console.WriteLine();
    foreach (var (chave, n) in rel.Contagens) Console.WriteLine($"{chave}: {n}");
    if (rel.Avisos.Count > 0) Console.WriteLine();
    foreach (var a in rel.Avisos) Console.WriteLine("aviso: " + a);
    return;
}

// `dotnet Inovabens.Web.dll importar-notion-complemento --pasta=caminho [--usuario=email] [--simular]`: memorandos e
// arquivos do Notion já preparados num pacote (manifesto.json + arquivos); não acessa o Notion. Rodar de novo não duplica.
if (args.Contains("importar-notion-complemento"))
{
    string? Arg(string nome) => args.FirstOrDefault(a => a.StartsWith($"--{nome}=", StringComparison.Ordinal))?[(nome.Length + 3)..];
    if (Arg("pasta") is not { } pasta || !File.Exists(Path.Combine(pasta, "manifesto.json")))
    {
        Console.Error.WriteLine("Informe --pasta=caminho com o manifesto.json.");
        Environment.ExitCode = 1;
        return;
    }
    using var escopo = app.Services.CreateScope();
    var sp = escopo.ServiceProvider;
    Guid usuarioId;
    await using (var db = await sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
    {
        var email = Arg("usuario");
        usuarioId = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            db.Usuarios.Where(x => x.Ativo && x.ExcluidoEm == null && (email == null ? x.Perfil!.Nome == "Administrador" : x.Email == email))
                .OrderBy(x => x.CriadoEm).Select(x => x.Id));
    }
    var usuario = usuarioId == Guid.Empty ? null : await sp.GetRequiredService<AuthService>().RecarregarAsync(usuarioId);
    if (usuario is null)
    {
        Console.Error.WriteLine("Usuário não encontrado (informe --usuario=email de um usuário ativo).");
        Environment.ExitCode = 1;
        return;
    }
    var simular = args.Contains("--simular");
    var itensLog = await sp.GetRequiredService<Inovabens.Core.Notion.ComplementoNotionService>().ImportarAsync(usuario, pasta, simular, Console.WriteLine);
    foreach (var item in itensLog) Console.WriteLine("  " + item);
    Console.WriteLine(simular ? "=== SIMULAÇÃO (nada foi gravado) ===" : "=== COMPLEMENTO CONCLUÍDO ===");
    return;
}

// `dotnet Inovabens.Web.dll importar-notion [--usuario=email] [--simular] [--token-arquivo=caminho] [--mapear="PROJETO=REF"]...`
// traz o workspace do Notion (só leitura lá). Token da integração em NOTION_TOKEN ou no arquivo. --simular desfaz tudo no fim.
if (args.Contains("importar-notion"))
{
    string? Arg(string nome) => args.FirstOrDefault(a => a.StartsWith($"--{nome}=", StringComparison.Ordinal))?[(nome.Length + 3)..];
    var token = Environment.GetEnvironmentVariable("NOTION_TOKEN");
    if (Arg("token-arquivo") is { } arq) token = (await File.ReadAllTextAsync(arq)).Trim();
    if (string.IsNullOrWhiteSpace(token))
    {
        Console.Error.WriteLine("Informe o token da integração do Notion em NOTION_TOKEN ou --token-arquivo=caminho.");
        Environment.ExitCode = 1;
        return;
    }
    var mapear = args.Where(a => a.StartsWith("--mapear=", StringComparison.Ordinal)).Select(a => a[9..].Split('=', 2))
        .Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
    using var escopo = app.Services.CreateScope();
    var sp = escopo.ServiceProvider;
    var fabricaDb = sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<InovabensDb>>();
    Guid usuarioId;
    await using (var db = await fabricaDb.CreateDbContextAsync())
    {
        var email = Arg("usuario");
        usuarioId = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            db.Usuarios.Where(x => x.Ativo && x.ExcluidoEm == null && (email == null ? x.Perfil!.Nome == "Administrador" : x.Email == email))
                .OrderBy(x => x.CriadoEm).Select(x => x.Id));
    }
    var usuario = usuarioId == Guid.Empty ? null : await sp.GetRequiredService<AuthService>().RecarregarAsync(usuarioId);
    if (usuario is null)
    {
        Console.Error.WriteLine("Usuário não encontrado (informe --usuario=email de um usuário ativo).");
        Environment.ExitCode = 1;
        return;
    }
    var simular = args.Contains("--simular");
    var r = await sp.GetRequiredService<Inovabens.Core.Notion.ImportacaoNotionService>().ImportarAsync(usuario,
        new Inovabens.Core.Notion.NotionCliente(token), simular, mapear, Console.WriteLine);
    Console.WriteLine();
    Console.WriteLine(simular ? "=== SIMULAÇÃO (nada foi gravado) ===" : "=== IMPORTAÇÃO CONCLUÍDA ===");
    foreach (var d in r.Detalhes) Console.WriteLine("  " + d);
    Console.WriteLine();
    Console.WriteLine("Totais:");
    foreach (var (k, v) in r.Contagem) Console.WriteLine($"  {k}: {v}");
    Console.WriteLine();
    Console.WriteLine($"Pendências ({r.Pendencias.Count}):");
    foreach (var p in r.Pendencias.Distinct()) Console.WriteLine("  - " + p);
    return;
}

app.UseForwardedHeaders();
app.UseRateLimiter(); // depois do UseForwardedHeaders: limita pelo IP real do cliente
app.UseIndexacao(); // X-Robots-Tag: noindex em tudo enquanto o site público não estiver ligado (Seo.cs)

// Cabeçalhos de segurança (papel do helmet na versão Node): sem iframe de outros sites (clickjacking), sem o
// navegador "adivinhar" tipos de arquivo, formulários só para o próprio sistema. Sem script-src: o Blazor não muda.
app.Use(async (c, next) =>
{
    var h = c.Response.Headers;
    h.XContentTypeOptions = "nosniff";
    h.XFrameOptions = "DENY";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h.ContentSecurityPolicy = "frame-ancestors 'none'; base-uri 'self'; object-src 'none'; form-action 'self'";
    await next();
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapPost("/sair", async (HttpContext c) =>
{
    await c.SignOutAsync(UsuarioAtual.Esquema);
    return Results.LocalRedirect("/login");
});

// Health check da nuvem (Render/Fly/ECS): verifica também a conexão com o banco. Mesmo caminho da API Node.
app.MapGet("/api/v1/health", async (Microsoft.EntityFrameworkCore.IDbContextFactory<Inovabens.Core.Dados.InovabensDb> f) =>
{
    try
    {
        await using var db = await f.CreateDbContextAsync();
        return await db.Database.CanConnectAsync()
            ? Results.Ok(new { status = "ok", db = "up" })
            : Results.Problem("Banco indisponível", statusCode: 503);
    }
    catch { return Results.Problem("Banco indisponível", statusCode: 503); }
}).AllowAnonymous();

// Downloads de relatórios (links comuns, fora do circuito Blazor). Mesmas permissões da versão Node.
var imovel = app.MapGroup("/imoveis/{id:guid}");
imovel.MapGet("ficha.pdf", (Guid id, HttpContext c, RelatoriosService r) => Baixar(c, u => r.FichaImovelPdfAsync(u, id)))
    .RequireAuthorization(Permissoes.ImoveisExportar);
imovel.MapGet("ficha.xlsx", (Guid id, HttpContext c, RelatoriosService r) => Baixar(c, u => r.FichaImovelXlsxAsync(u, id)))
    .RequireAuthorization(Permissoes.ImoveisExportar);
imovel.MapGet("ficha-financeira.pdf", (Guid id, CodigoIndice? indice, HttpContext c, RelatoriosService r) =>
    Baixar(c, u => r.FichaFinanceiraPdfAsync(u, id, indice ?? CodigoIndice.IPCA))).RequireAuthorization(Permissoes.FinanceiroExportar);
imovel.MapGet("ficha-financeira.xlsx", (Guid id, CodigoIndice? indice, HttpContext c, RelatoriosService r) =>
    Baixar(c, u => r.FichaFinanceiraXlsxAsync(u, id, indice ?? CodigoIndice.IPCA))).RequireAuthorization(Permissoes.FinanceiroExportar);

// Painel de acompanhamento do CRM em Excel, com os mesmos filtros da tela (crm/acompanhamento?...)
app.MapGet("/crm/acompanhamento.xlsx", (HttpContext c, AcompanhamentoService s) =>
    Baixar(c, u => s.ExcelAsync(u, FiltroAcompanhamento.Ler(k => c.Request.Query[k].FirstOrDefault())))).RequireAuthorization(Permissoes.CrmVer);

// Listas de Imóveis e Pessoas em Excel, com os filtros da tela (só informações, sem fotos)
app.MapGet("/imoveis/lista.xlsx", (string? termo, SituacaoImovel? situacao, string? tipo, string? cidade, Guid? corretor, HttpContext c, ImoveisService s) =>
    Baixar(c, u => s.ExcelListaAsync(u, new FiltroImoveis { Termo = termo, Situacao = situacao, Tipo = tipo, Cidade = cidade, CorretorId = corretor })))
    .RequireAuthorization(Permissoes.ImoveisExportarLista);
// Chaves: planilha para conferir o chaveiro e etiquetas para recortar (?imovel= um imóvel; ?chave= uma etiqueta)
app.MapGet("/imoveis/chaves.xlsx", (Guid? imovel, HttpContext c, ChavesService s) => Baixar(c, u => s.ExcelAsync(u, imovel)))
    .RequireAuthorization(Permissoes.ChavesVer);
app.MapGet("/imoveis/chaves/etiquetas.pdf", (Guid? imovel, Guid? chave, HttpContext c, ChavesService s) =>
    Baixar(c, u => s.EtiquetasPdfAsync(u, imovel, chave))).RequireAuthorization(Permissoes.ChavesVer);
// DRE do Financeiro da Imobiliária (ano inteiro, por competência)
app.MapGet("/financeiro/dre.xlsx", (int ano, HttpContext c, FinanceiroEmpresaService s) => Baixar(c, u => s.DreExcelAsync(u, ano)))
    .RequireAuthorization(Permissoes.FinanceiroEmpresaExportar);
app.MapGet("/financeiro/dre.pdf", (int ano, HttpContext c, FinanceiroEmpresaService s) => Baixar(c, u => s.DrePdfAsync(u, ano)))
    .RequireAuthorization(Permissoes.FinanceiroEmpresaExportar);
app.MapGet("/pessoas/lista.xlsx", (string? termo, string? papel, HttpContext c, PessoasService s) =>
    Baixar(c, u => s.ExcelListaAsync(u, new FiltroPessoas(termo, papel)))).RequireAuthorization(Permissoes.PessoasExportarLista);

// Site público (páginas próprias): /site (início), /site/imoveis?finalidade=venda|locacao (busca) e /site/imovel/{id} (ficha).
// Aberto a visitantes sem login: só imóveis disponíveis e marcados para divulgar, com fotos e características públicas.
// Usuário do sistema também abre a ficha de imóvel que ainda não está no site (botão "Ver no site"), com a faixa de avisos.
// Enquanto SITE_PUBLICO_URL estiver vazio, segue fora do Google (noindex, Seo.cs).
Task<IResult> FichaSite(Guid id, HttpContext c, ImoveisService s, EmpreendimentosService emp) => PaginaImovelSite(id, c, s, emp, null);
async Task<IResult> PaginaImovelSite(Guid id, HttpContext c, ImoveisService s, EmpreendimentosService emp, TaxasFinanciamentoService? taxas)
{
    try
    {
        var u = UsuarioAtual.De(c.User);
        ImovelDetalhe d; QuadroLotes? quadro; string? linkAtual = null;
        if (u is not null && u.Pode(Permissoes.ImoveisVer))
        {
            d = await s.DetalheAsync(u, id);
            quadro = await emp.QuadroAsync(u, id);
            linkAtual = d.Dados.ImobibrasilId is { } ib ? $"https://www.inovabens.com.br/imovel/{ib}" : null;
        }
        else if (await s.DetalhePublicoAsync(id, c.RequestAborted) is { } pub) (d, quadro) = pub;
        else return Results.NotFound();
        CacheSite(c);
        return new Microsoft.AspNetCore.Http.HttpResults.RazorComponentResult<Inovabens.Web.Components.Site.FichaSite>(
            new { Detalhe = d, LinkImobibrasil = linkAtual, Quadro = quadro, SoSimulador = taxas is not null, Taxas = taxas is null ? null : await taxas.AtuaisAsync(c.RequestAborted) });
    }
    catch (AcessoNegadoException e) { return Results.Problem(e.Message, statusCode: 403); }
    catch (NaoEncontradoException e) { return Results.Problem(e.Message, statusCode: 404); }
}
// visitante: a página pode ficar 5 min no cache; logado: é a versão com avisos do sistema, só dele
static void CacheSite(HttpContext c) => c.Response.Headers.CacheControl =
    c.User.Identity?.IsAuthenticated == true ? "private, no-store" : "public, max-age=300";
imovel.MapGet("site", FichaSite).RequireAuthorization(Permissoes.ImoveisVer);
// Extrato dos Lançamentos Financeiros com os mesmos filtros da tela: ?de=AAAA-MM-DD&ate=AAAA-MM-DD&tipo=RECEITAS|DESPESAS&conta=<id>&busca=texto&projetado=true&naoEnviadosBpo=true
imovel.MapGet("extrato.pdf", (Guid id, DateOnly? de, DateOnly? ate, TipoExtrato? tipo, Guid? conta, string? busca, bool? projetado, FiltroSituacao? situacao, bool? naoEnviadosBpo, HttpContext c, RelatoriosService r) =>
    Baixar(c, u => r.ExtratoPdfAsync(u, id, new FiltroLancamentos { De = de, Ate = ate, Tipo = tipo ?? TipoExtrato.TODOS, ContaId = conta, Busca = busca, Projetado = projetado == true, Situacao = situacao ?? FiltroSituacao.TODAS, NaoEnviadosBpo = naoEnviadosBpo == true })))
    .RequireAuthorization(Permissoes.FinanceiroExportar);
imovel.MapGet("extrato.xlsx", (Guid id, DateOnly? de, DateOnly? ate, TipoExtrato? tipo, Guid? conta, string? busca, bool? projetado, FiltroSituacao? situacao, bool? naoEnviadosBpo, HttpContext c, RelatoriosService r) =>
    Baixar(c, u => r.ExtratoXlsxAsync(u, id, new FiltroLancamentos { De = de, Ate = ate, Tipo = tipo ?? TipoExtrato.TODOS, ContaId = conta, Busca = busca, Projetado = projetado == true, Situacao = situacao ?? FiltroSituacao.TODAS, NaoEnviadosBpo = naoEnviadosBpo == true })))
    .RequireAuthorization(Permissoes.FinanceiroExportar);

// Documento da proposta: PDF padrão ou modelo Word preenchido (?modelo=<id>); e o arquivo original de um modelo (admin)
app.MapGet("/crm/propostas/{id:guid}/documento", (Guid id, Guid? modelo, HttpContext c, DocumentosService d) => Baixar(c, u => d.GerarAsync(u, id, modelo)))
    .RequireAuthorization(Permissoes.CrmVer);
// Peça gerada de um post para redes sociais (imagem, vídeo, música), para baixar e publicar à mão
app.MapGet("/imoveis/posts/arquivo/{id:guid}", (Guid id, HttpContext c, Inovabens.Core.Redes.PostsService p) => Baixar(c, u => p.ArquivoAsync(u, id, c.RequestAborted)))
    .RequireAuthorization(Permissoes.ImoveisVer);
app.MapGet("/admin/modelos/{id:guid}/arquivo", (Guid id, HttpContext c, DocumentosService d) => Baixar(c, u => d.BaixarModeloAsync(u, id)))
    .RequireAuthorization(Permissoes.AdminConfig);

var site = app.MapGroup("/site").AllowAnonymous();
site.MapGet("imovel/{id:guid}", FichaSite);
site.MapGet("imovel/{id:guid}/simulador", (Guid id, HttpContext c, ImoveisService s, EmpreendimentosService emp, TaxasFinanciamentoService t) => PaginaImovelSite(id, c, s, emp, t));
// início e busca: a mesma vitrine para todos (visitante ou usuário), a que o site realmente mostra
site.MapGet("", async (HttpContext c, ImoveisService s) =>
{
    var venda = await s.VitrinePublicaAsync(new FiltroVitrine(FinalidadeImovel.VENDA), c.RequestAborted);
    var aluguel = await s.VitrinePublicaAsync(new FiltroVitrine(FinalidadeImovel.LOCACAO), c.RequestAborted);
    CacheSite(c);
    return new Microsoft.AspNetCore.Http.HttpResults.RazorComponentResult<Inovabens.Web.Components.Site.InicioSite>(
        new { Venda = venda, Aluguel = aluguel });
});
site.MapGet("imoveis", async (string? finalidade, string? tipo, string? cidade, string? ordem, HttpContext c, ImoveisService s) =>
{
    var locacao = string.Equals(finalidade, "locacao", StringComparison.OrdinalIgnoreCase);
    var fin = locacao ? FinalidadeImovel.LOCACAO : FinalidadeImovel.VENDA;
    tipo = string.IsNullOrWhiteSpace(tipo) ? null : tipo.Trim();
    cidade = string.IsNullOrWhiteSpace(cidade) ? null : cidade.Trim();
    var todos = await s.VitrinePublicaAsync(new FiltroVitrine(fin, Maximo: 200), c.RequestAborted);
    var itens = tipo is null && cidade is null ? todos : await s.VitrinePublicaAsync(new FiltroVitrine(fin, tipo, cidade, 200), c.RequestAborted);
    CacheSite(c);
    return new Microsoft.AspNetCore.Http.HttpResults.RazorComponentResult<Inovabens.Web.Components.Site.ListaSite>(
        new { Itens = itens, Todos = todos, Locacao = locacao, Tipo = tipo, Cidade = cidade, Ordem = ordem is "menor" or "maior" ? ordem : null });
});
// fotos públicas do site ainda fora do R2: confere que a foto e o imóvel estão no site; a versão reduzida em memória
// evita baixar o original do Drive a cada pedido. Só larguras conhecidas (padrão 1200), sempre WebP quando possível.
site.MapGet("foto/{midiaId:guid}/{arquivo}", async (Guid midiaId, string arquivo, int? l, HttpContext c, ImoveisService s, FotosReduzidas reduzidas) =>
{
    var largura = l is { } x && ImoveisService.LarguraValida(x) ? x : ImoveisService.LarguraTela;
    if (!await s.FotoNoSiteAsync(midiaId, arquivo, c.RequestAborted)) return Results.NotFound();
    c.Response.Headers.CacheControl = "public, max-age=2592000, immutable"; // o id do arquivo na URL muda quando a foto é trocada
    if (reduzidas.EmMemoria(arquivo, largura) is { } pronta) return Results.File(pronta, "image/webp");
    if (await s.BaixarFotoNoSiteAsync(arquivo, c.RequestAborted) is not { } original) return Results.NotFound();
    return FotosReduzidas.Reduzivel(original.Tipo) && reduzidas.Obter(arquivo, largura, () => original.Dados) is { } webp
        ? Results.File(webp, "image/webp")
        : Results.NotFound(); // só imagem: nada de repassar outro tipo de arquivo sem login
});

// Página exclusiva do imóvel (landing page): /imovel/{slug}, aberta sem login quando publicada (rascunho: só usuários
// do sistema com acesso ao imóvel). Imagem da prévia do link (WhatsApp, e-mail) e fotos públicas também sem login.
var paginaImovel = app.MapGroup("/" + PaginasImovelService.Prefixo).AllowAnonymous();
paginaImovel.MapGet("{slug}", async (string slug, HttpContext c, PaginasImovelService s) =>
{
    var pg = await s.PublicaAsync(slug, UsuarioAtual.De(c.User), c.RequestAborted);
    if (pg is null) return Results.NotFound();
    c.Response.Headers.CacheControl = pg.Previa ? "private, no-store" : "public, max-age=300";
    // medição: visitante de verdade (sem login, não robô); usuários do sistema conferindo a página não contam
    if (!pg.Previa && c.User.Identity?.IsAuthenticated != true && !PaginasImovelService.EhRobo(c.Request.Headers.UserAgent))
        await s.RegistrarEventoAsync(slug, PaginaImovelEvento.Visita,
            PaginasImovelService.Origem(c.Request.Query["utm_source"], c.Request.Headers.Referer), c.RequestAborted);
    return new Microsoft.AspNetCore.Http.HttpResults.RazorComponentResult<Inovabens.Web.Components.Site.LandingImovel>(new { Dados = pg });
});
// clique no WhatsApp (enviado pelo navegador com sendBeacon ao clicar no botão); sempre 204, sem revelar nada
paginaImovel.MapPost("{slug}/contato", async (string slug, string? o, HttpContext c, PaginasImovelService s) =>
{
    if (c.User.Identity?.IsAuthenticated != true && !PaginasImovelService.EhRobo(c.Request.Headers.UserAgent))
        await s.RegistrarEventoAsync(slug, PaginaImovelEvento.Whatsapp, PaginasImovelService.Origem(o, null), c.RequestAborted);
    return Results.NoContent();
}).DisableAntiforgery();
// formulário de contato da página: vira lead no CRM. Com JavaScript responde JSON (mensagem na própria página);
// sem JavaScript, volta para a página com ?contato=ok|erro. Formulário público: sem antiforgery, com limite por IP.
paginaImovel.MapPost("{slug}/formulario", async (string slug, HttpContext c, ContatoPaginaService s) =>
{
    var f = await c.Request.ReadFormAsync(c.RequestAborted);
    var form = new ContatoPaginaForm
    {
        Nome = f["nome"].ToString(), Telefone = f["telefone"].ToString(), Mensagem = f["mensagem"].ToString(),
        Consentimento = f["consentimento"].ToString() is "on" or "true", Site = f["site"].ToString(),
    };
    var porScript = c.Request.Headers["X-Requested-With"] == "fetch";
    string? erro = null;
    try
    {
        if (!await s.EnviarAsync(slug, form, PaginasImovelService.Origem(f["o"].ToString(), null), c.RequestAborted))
            return Results.NotFound();
    }
    catch (Exception ex) when (ex is RegraNegocioException or ServicoIndisponivelException)
    {
        erro = ex.Message;
    }
    if (porScript) return erro is null ? Results.Ok(new { ok = true }) : Results.BadRequest(new { ok = false, erro });
    return Results.Redirect($"/{PaginasImovelService.Caminho(slug)}?contato={(erro is null ? "ok" : "erro")}#contato");
}).DisableAntiforgery();
paginaImovel.MapGet("{slug}/compartilhar.jpg", async (string slug, HttpContext c, PaginasImovelService s) =>
{
    var u = UsuarioAtual.De(c.User);
    if (await s.ImagemCompartilhamentoAsync(slug, u, c.RequestAborted) is not { } img) return Results.NotFound();
    // o endereço leva a versão da foto (?v=): trocou a foto, muda o endereço; por isso o cache pode ser longo
    c.Response.Headers.CacheControl = u is null ? "public, max-age=86400" : "private, max-age=300";
    return Results.File(img.Jpeg, "image/jpeg");
});
paginaImovel.MapGet("{slug}/foto/{midiaId:guid}/{arquivo}", async (string slug, Guid midiaId, string arquivo, int? l, HttpContext c,
    PaginasImovelService s, FotosReduzidas reduzidas) =>
{
    var u = UsuarioAtual.De(c.User);
    var largura = l is { } x && ImoveisService.LarguraValida(x) ? x : ImoveisService.LarguraTela;
    // a versão reduzida já em memória só é entregue depois de conferir que a foto é pública e a página pode ser vista;
    // o original só é baixado do Drive quando ela ainda não existe (antes, cada foto baixava o original: ~1 s por foto)
    if (!await s.FotoVisivelAsync(slug, midiaId, arquivo, u, c.RequestAborted)) return Results.NotFound();
    c.Response.Headers.CacheControl = u is null ? "public, max-age=2592000, immutable" : "private, max-age=86400";
    if (reduzidas.EmMemoria(arquivo, largura) is { } pronta) return Results.File(pronta, "image/webp");
    if (await s.FotoAsync(slug, midiaId, arquivo, u, c.RequestAborted) is not { } original) return Results.NotFound();
    return FotosReduzidas.Reduzivel(original.Tipo) && reduzidas.Obter(arquivo, largura, () => original.Dados) is { } webp
        ? Results.File(webp, "image/webp")
        : Results.File(original.Dados, original.Tipo);
});
// QR code da página (para placa, panfleto, cartão): PNG que abre o link com a origem "qrcode" para a medição.
imovel.MapGet("pagina/qr.png", async (Guid id, HttpContext c, PaginasImovelService s, InovabensOpcoes op) =>
{
    try
    {
        var e = await s.EdicaoAsync(UsuarioAtual.De(c.User)!, id);
        if (e.Pagina is null) return Results.NotFound();
        var link = Inovabens.Web.Components.Imoveis.PaginaImovelAba.Link(op, e.Pagina.Slug, e.Codigo, "qrcode");
        using var dados = new QRCoder.QRCodeGenerator().CreateQrCode(link, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(dados).GetGraphic(20);
        c.Response.Headers.CacheControl = "private, no-store";
        return Results.File(png, "image/png", $"qrcode-{e.Pagina.Slug}.png");
    }
    catch (AcessoNegadoException e) { return Results.Problem(e.Message, statusCode: 403); }
    catch (NaoEncontradoException e) { return Results.Problem(e.Message, statusCode: 404); }
}).RequireAuthorization(Permissoes.ImoveisVer);

// Fotos e documentos: o Drive é privado, então o sistema confere o acesso ao imóvel e repassa o arquivo.
// O id do arquivo no Drive vai na URL e muda quando a foto é trocada, por isso o navegador pode guardar em cache.
// ?l=160|400|800|1200: foto reduzida em WebP (ícone da lista, cartões, galeria) em vez do original de vários MB.
app.MapGet("/imoveis/midias/{midiaId:guid}/{arquivo}", async (Guid midiaId, string arquivo, int? l, HttpContext c, ImoveisService s, FotosReduzidas reduzidas) =>
{
    try
    {
        var u = UsuarioAtual.De(c.User)!;
        if (l is { } largura && ImoveisService.LarguraValida(largura))
        {
            // o arquivo do Drive na URL muda quando a foto é trocada: a versão reduzida nunca fica velha
            if (reduzidas.EmMemoria(arquivo, largura) is { } pronta)
            {
                await s.ConferirArquivoMidiaAsync(u, midiaId, arquivo, c.RequestAborted);
                c.Response.Headers.CacheControl = "private, max-age=2592000, immutable";
                return Results.File(pronta, "image/webp");
            }
            // ícone menor que a miniatura já em memória: reduz a partir dela, sem baixar de novo o original do Drive
            if (largura < ImoveisService.LarguraMiniatura && reduzidas.EmMemoria(arquivo, ImoveisService.LarguraMiniatura) is { } miniatura
                && reduzidas.Obter(arquivo, largura, () => miniatura) is { } icone)
            {
                await s.ConferirArquivoMidiaAsync(u, midiaId, arquivo, c.RequestAborted);
                c.Response.Headers.CacheControl = "private, max-age=2592000, immutable";
                return Results.File(icone, "image/webp");
            }
            var original = await s.ArquivoMidiaAsync(u, midiaId, arquivo, c.RequestAborted);
            if (FotosReduzidas.Reduzivel(original.ContentType) && reduzidas.Obter(arquivo, largura, () => original.Dados) is { } webp)
            {
                c.Response.Headers.CacheControl = "private, max-age=2592000, immutable";
                return Results.File(webp, "image/webp");
            }
            c.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(original.Dados, original.ContentType);
        }
        var a = await s.ArquivoMidiaAsync(u, midiaId, arquivo, c.RequestAborted);
        c.Response.Headers.CacheControl = "private, max-age=86400";
        return a.Download
            ? Results.File(a.Dados, a.ContentType, a.Nome)
            : Results.File(a.Dados, a.ContentType, enableRangeProcessing: true); // inline; range para vídeo
    }
    catch (AcessoNegadoException e) { return Results.Problem(e.Message, statusCode: 403); }
    catch (NaoEncontradoException e) { return Results.Problem(e.Message, statusCode: 404); }
}).RequireAuthorization(Permissoes.ImoveisVer);

// Comprovantes financeiros (Drive "07 - Financeiro"): só quem edita o financeiro abre.
app.MapGet("/financeiro/comprovantes/{id:guid}", async (Guid id, HttpContext c, ComprovantesService s) =>
{
    try
    {
        var a = await s.ArquivoAsync(UsuarioAtual.De(c.User)!, id, c.RequestAborted);
        c.Response.Headers.CacheControl = "private, no-store";
        return a.Download ? Results.File(a.Dados, a.ContentType, a.Nome) : Results.File(a.Dados, a.ContentType);
    }
    catch (AcessoNegadoException e) { return Results.Problem(e.Message, statusCode: 403); }
    catch (NaoEncontradoException e) { return Results.Problem(e.Message, statusCode: 404); }
}).RequireAuthorization(Permissoes.FinanceiroEditar);

// Cartão BPO em PDF único (cartão + comprovantes juntados) para mandar ao responsável financeiro do imóvel.
app.MapGet("/financeiro/lancamentos/{id:guid}/bpo.pdf", (Guid id, HttpContext c, BpoService s) =>
    Baixar(c, u => s.PdfAsync(u, id, c.RequestAborted))).RequireAuthorization(Permissoes.FinanceiroEditar);

// Google Agenda: mesmos caminhos da API Node, para não mudar a configuração no Google Cloud Console.
app.MapGet("/api/v1/integracoes/google/callback", async (string? code, string? state, string? error, GoogleService g, ILogger<Program> log) =>
{
    if (error is not null || code is null || state is null) return Results.Redirect("/agenda?google=cancelado");
    try
    {
        return Results.Redirect(await g.CallbackAsync(code, state));
    }
    catch (Exception e)
    {
        log.LogWarning("Falha ao conectar Google: {Erro}", e.Message);
        return Results.Redirect("/agenda?google=erro");
    }
}).AllowAnonymous();
app.MapPost("/api/v1/webhooks/google/calendar", async (HttpRequest req, GoogleService g) =>
{
    await g.WebhookAsync(req.Headers["x-goog-channel-id"], req.Headers["x-goog-channel-token"], req.Headers["x-goog-resource-state"]);
    return Results.Ok();
}).AllowAnonymous().DisableAntiforgery();

// WhatsApp Business (Cloud API): a Meta confere a URL com GET e entrega as mensagens com POST assinado.
// Ferramenta local GeradorPaginas: dados públicos do imóvel pelo código, com a chave de Administração → Integrações
// no cabeçalho X-Chave-Gerador. Sem chave configurada (ou errada), 404: o endereço não revela que existe.
app.MapGet("/api/v1/gerador/imovel/{codigo}", async (string codigo, HttpRequest req, InovabensOpcoes op, PaginasImovelService s, CancellationToken ct) =>
{
    var chave = op.Integracoes.GeradorPaginasChave;
    var enviada = req.Headers["X-Chave-Gerador"].ToString();
    if (chave is null || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(enviada), System.Text.Encoding.UTF8.GetBytes(chave)))
        return Results.NotFound();
    var dados = await s.DadosGeradorAsync(codigo, ct);
    if (dados is null) return Results.NotFound(new { erro = $"Imóvel {codigo} não encontrado." });
    var url = op.SitePublicoUrl ?? $"{req.Scheme}://{req.Host}";
    return Results.Ok(dados with { PaginaPublica = dados.PaginaPublica is { } pg ? $"{url.TrimEnd('/')}/{pg}" : null });
}).AllowAnonymous();
app.MapGet("/api/v1/webhooks/whatsapp", (HttpRequest req, WhatsappService w) =>
{
    SemPaginaDeErro(req);
    return w.Desafio(req.Query["hub.mode"], req.Query["hub.verify_token"], req.Query["hub.challenge"]) is { } desafio
        ? Results.Text(desafio, "text/plain")
        : Results.StatusCode(StatusCodes.Status403Forbidden);
}).AllowAnonymous();
app.MapPost("/api/v1/webhooks/whatsapp", async (HttpRequest req, WhatsappService w, CancellationToken ct) =>
{
    SemPaginaDeErro(req);
    if (!w.Configurado) return Results.NotFound();
    if (req.ContentLength > 1_000_000) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    using var buffer = new MemoryStream();
    await req.Body.CopyToAsync(buffer, ct);
    if (buffer.Length > 1_000_000) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    var corpo = buffer.ToArray();
    if (!w.Autentico(corpo, req.Headers["X-Hub-Signature-256"], req.Query["token"])) return Results.Unauthorized();
    JsonDocument aviso;
    try { aviso = JsonDocument.Parse(corpo); }
    catch (JsonException) { return Results.BadRequest(); }
    using (aviso)
    {
        // erro de banco devolve 500: a Meta reenvia depois, e mensagens já gravadas não entram de novo
        await w.ProcessarAsync(aviso.RootElement, ct);
    }
    return Results.Ok();
}).AllowAnonymous().DisableAntiforgery();

// Webhook responde só o código HTTP: sem reexecutar a página "não encontrado" do site (viraria 400 no POST).
static bool SemPaginaDeErro(HttpRequest req)
{
    if (req.HttpContext.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } f) f.Enabled = false;
    return true;
}

// Depois de trocar a senha, a tela de Conta reemite o cookie desta sessão com o novo selo (as demais caem).
app.MapGet("/conta/renovar-sessao", async (string t, HttpContext c, AuthService auth, RenovacaoSessao renovacao) =>
{
    var sessao = UsuarioAtual.De(c.User);
    var atual = sessao is null ? null : await auth.RecarregarAsync(sessao.Id);
    if (atual is null || !renovacao.Valido(t, atual.Id, atual.Selo))
    {
        await c.SignOutAsync(UsuarioAtual.Esquema);
        return Results.LocalRedirect("/login");
    }
    await c.SignInAsync(UsuarioAtual.Esquema, atual.ParaPrincipal());
    return Results.LocalRedirect("/conta?senha=alterada");
}).RequireAuthorization();

app.MapIndexacao(); // robots.txt, sitemap.xml (só com SITE_PUBLICO_URL) e favicon.ico

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Gera o relatório e devolve como download; erros de acesso viram 403/404 em vez de página de erro.</summary>
static async Task<IResult> Baixar(HttpContext c, Func<UsuarioAtual, Task<Arquivo>> gerar)
{
    try
    {
        var a = await gerar(UsuarioAtual.De(c.User)!);
        return Results.File(a.Conteudo, a.Tipo, a.Nome);
    }
    catch (AcessoNegadoException e) { return Results.Problem(e.Message, statusCode: 403); }
    catch (NaoEncontradoException e) { return Results.Problem(e.Message, statusCode: 404); }
    catch (RegraNegocioException e) { return Results.Problem(e.Message, statusCode: 400); }
}

/// <summary>
/// A cada 15 min relê usuário, perfil e permissões no banco: usuário desativado perde o acesso e
/// mudanças de perfil passam a valer sem novo login (papel que o access token curto tinha na versão Node);
/// troca de senha encerra as demais sessões.
/// </summary>
static async Task RevalidarSessao(CookieValidatePrincipalContext ctx)
{
    if (ctx.Properties.IssuedUtc is { } emitido && DateTimeOffset.UtcNow - emitido < TimeSpan.FromMinutes(15)) return;
    var sessao = UsuarioAtual.De(ctx.Principal);
    var atual = sessao is null ? null : await ctx.HttpContext.RequestServices.GetRequiredService<AuthService>().RecarregarAsync(sessao.Id);
    // selo diferente = a senha foi trocada (por ele ou por um administrador) depois deste login
    if (atual is null || atual.Selo != sessao!.Selo)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(UsuarioAtual.Esquema);
        return;
    }
    ctx.ReplacePrincipal(atual.ParaPrincipal());
    ctx.ShouldRenew = true;
}
