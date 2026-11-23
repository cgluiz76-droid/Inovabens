using GeradorPaginas;

// Ferramenta local do administrador: gera HTML/CSS com o Claude Code (login da assinatura, nunca chave de API) para colar
// na aba Página do imóvel no Inovabens. Só escuta em localhost:5080; não é para hospedar (o login do Claude Code fica no
// perfil do usuário do Windows).
ClaudeCli.RemoverChavesDoProcesso();

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
// somente nesta máquina: 127.0.0.1 e ::1, porta fixa (ignora ASPNETCORE_URLS e afins)
builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(5080));
builder.Services.AddSingleton<ClaudeCli>();
builder.Services.AddSingleton<Gerador>();
builder.Services.AddHttpClient("inovabens", c => c.Timeout = TimeSpan.FromSeconds(20));

var app = builder.Build();

// Proteção de ferramenta local: outro site aberto no navegador não pode usá-la. Host diferente = DNS rebinding; POST
// sem o cabeçalho X-Gerador = formulário/fetch de outra origem (o cabeçalho exige preflight CORS, que não é liberado).
app.Use(async (ctx, next) =>
{
    var host = ctx.Request.Host.Host;
    if (host is not ("localhost" or "127.0.0.1" or "[::1]" or "::1"))
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Headers["X-Gerador"] != "1")
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/status", async (ClaudeCli c, CancellationToken ct) => Results.Ok(await c.VerificarAsync(ct)));

// toda geração confere o login antes; sem login válido, 428 e a tela mostra "Login necessário"
api.MapPost("/gerar", (PedidoGeracao p, ClaudeCli c, Gerador g, CancellationToken ct) => ComLogin(c, ct, () => g.GerarAsync(p, ct)));
api.MapPost("/refinar", (PedidoRefino p, ClaudeCli c, Gerador g, CancellationToken ct) => ComLogin(c, ct, () => g.RefinarAsync(p, ct)));

api.MapGet("/historico", (IWebHostEnvironment amb) => Results.Ok(Historico.Listar(amb.ContentRootPath)));
api.MapGet("/historico/{nome}", (string nome, IWebHostEnvironment amb) =>
    Historico.Abrir(amb.ContentRootPath, nome) is { } h ? Results.Ok(h) : Results.NotFound());

var inicio = await app.Services.GetRequiredService<ClaudeCli>().VerificarAsync();
foreach (var i in inicio.Itens) app.Logger.LogInformation("[{Situacao}] {Nome}: {Detalhe}", i.Ok ? "ok" : "FALHA", i.Nome, i.Detalhe);
if (inicio.Aviso is not null) app.Logger.LogWarning("{Aviso}", inicio.Aviso);
app.Logger.LogInformation("GeradorPaginas em http://localhost:5080 (Ctrl+C para encerrar)");

app.Run();

static async Task<IResult> ComLogin(ClaudeCli c, CancellationToken ct, Func<Task<ResultadoGeracao>> acao)
{
    var v = await c.VerificarAsync(ct);
    if (!v.Ok) return Results.Json(new { erro = "Login necessário.", verificacao = v }, statusCode: StatusCodes.Status428PreconditionRequired);
    try
    {
        return Results.Ok(await acao());
    }
    catch (ErroGerador e)
    {
        return Results.Json(new { erro = e.Message, bruto = e.Bruto }, statusCode: StatusCodes.Status400BadRequest);
    }
}
