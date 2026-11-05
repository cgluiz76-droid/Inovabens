using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Agenda;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Financeiro;
using Inovabens.Core.Imoveis;
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
builder.Services.AddHostedService<AlertasCompromissosJob>();
builder.Services.AddHostedService<RotinaFinanceiraDiariaJob>();
builder.Services.AddHostedService<SincronizacaoGoogleJob>();
builder.Services.AddHostedService<RenovacaoCanaisGoogleJob>();
builder.Services.AddHostedService<MelhoriaFotosJob>();
builder.Services.AddHostedService<PublicacaoFotosJob>();
builder.Services.AddSingleton<RenovacaoSessao>();

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
        HttpMethods.IsPost(c.Request.Method) && c.Request.Path.Equals("/login", StringComparison.OrdinalIgnoreCase)
            ? RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "?",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limiteLogin, Window = TimeSpan.FromMinutes(1) })
            : RateLimitPartition.GetNoLimiter(""));
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, ct) => new ValueTask(ctx.HttpContext.Response.WriteAsync(
        "Muitas tentativas de login a partir deste endereço. Aguarde um minuto e tente de novo.", ct));
});

var app = builder.Build();

// Tabelas e dados-base: aplicados a cada subida (idempotente, com lock entre instâncias).
// `dotnet Inovabens.Web.dll migrar` faz só isso e sai — use como passo de release, se preferir.
await app.Services.GetRequiredService<Migrador>().AplicarAsync();
await app.Services.GetRequiredService<DadosIniciais>().GarantirAsync();
if (args.Contains("migrar")) return;

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
        Arg("site") ?? "https://www.inovabens.com.br", comFotos: !args.Contains("--sem-fotos"), progresso: Console.WriteLine);
    Console.WriteLine($"Concluído: {itens.Count(i => i.Novo)} novos, {itens.Count(i => !i.Novo && i.Codigo != "—")} atualizados, " +
                      $"{itens.Count(i => i.Codigo == "—")} com falha, {itens.Sum(i => i.FotosNovas)} fotos enviadas.");
    return;
}

app.UseForwardedHeaders();
app.UseRateLimiter(); // depois do UseForwardedHeaders: limita pelo IP real do cliente

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

// Pré-visualização do imóvel como no site público (página própria, aberta em outra aba pelo botão "Ver no site").
imovel.MapGet("site", async (Guid id, HttpContext c, ImoveisService s) =>
{
    try
    {
        var d = await s.DetalheAsync(UsuarioAtual.De(c.User)!, id);
        var linkAtual = d.Dados.ImobibrasilId is { } ib ? $"https://www.inovabens.com.br/imovel/{ib}" : null;
        return (IResult)new Microsoft.AspNetCore.Http.HttpResults.RazorComponentResult<Inovabens.Web.Components.Site.FichaSite>(
            new { Detalhe = d, LinkImobibrasil = linkAtual });
    }
    catch (AcessoNegadoException e) { return Results.Problem(e.Message, statusCode: 403); }
    catch (NaoEncontradoException e) { return Results.Problem(e.Message, statusCode: 404); }
}).RequireAuthorization(Permissoes.ImoveisVer);

// Fotos e documentos: o Drive é privado, então o sistema confere o acesso ao imóvel e repassa o arquivo.
// O id do arquivo no Drive vai na URL e muda quando a foto é trocada, por isso o navegador pode guardar em cache.
app.MapGet("/imoveis/midias/{midiaId:guid}/{arquivo}", async (Guid midiaId, string arquivo, HttpContext c, ImoveisService s) =>
{
    try
    {
        var a = await s.ArquivoMidiaAsync(UsuarioAtual.De(c.User)!, midiaId, arquivo, c.RequestAborted);
        c.Response.Headers.CacheControl = "private, max-age=86400";
        return a.Download
            ? Results.File(a.Dados, a.ContentType, a.Nome)
            : Results.File(a.Dados, a.ContentType, enableRangeProcessing: true); // inline; range para vídeo
    }
    catch (AcessoNegadoException e) { return Results.Problem(e.Message, statusCode: 403); }
    catch (NaoEncontradoException e) { return Results.Problem(e.Message, statusCode: 404); }
}).RequireAuthorization(Permissoes.ImoveisVer);

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
