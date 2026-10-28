using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Playwright;
using Testcontainers.PostgreSql;

namespace Inovabens.E2E;

[CollectionDefinition(Nome)]
public sealed class AplicacaoColecao : ICollectionFixture<Aplicacao>
{
    public const string Nome = "aplicacao";
}

/// <summary>
/// Sobe o sistema como em produção: PostgreSQL descartável (Testcontainers), a aplicação compilada
/// (<c>dotnet Inovabens.Web.dll</c>) numa porta livre e um Chromium sem janela (Playwright).
/// A aplicação aplica as migrações e cria o administrador inicial sozinha, como numa instalação nova.
/// </summary>
public sealed class Aplicacao : IAsyncLifetime
{
    public const string AdminEmail = "admin@inovabens.com.br";
    public const string SenhaPadrao = "Trocar@123456";

    private PostgreSqlContainer? _banco;
    private Process? _processo;
    private readonly StringBuilder _log = new();
    private IPlaywright? _playwright;

    public string Url { get; private set; } = "";
    public IBrowser Navegador { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        var conexao = Environment.GetEnvironmentVariable("INOVABENS_TEST_DB");
        if (string.IsNullOrWhiteSpace(conexao))
        {
            _banco = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("inovabens").WithUsername("inovabens").WithPassword("inovabens").Build();
            try { await _banco.StartAsync(); }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    "Os testes de ponta a ponta precisam do Docker em execução (ou de INOVABENS_TEST_DB). " +
                    "Para pular: dotnet test --filter \"Categoria!=E2E\".", e);
            }
            conexao = _banco.GetConnectionString();
        }

        Url = $"http://127.0.0.1:{PortaLivre()}";
        await IniciarAplicacao(conexao);

        // baixa o Chromium do Playwright na primeira execução (depois é instantâneo); no CI Linux, também as bibliotecas do sistema
        string[] instalar = Environment.GetEnvironmentVariable("CI") == "true" && OperatingSystem.IsLinux()
            ? ["install", "--with-deps", "chromium"] : ["install", "chromium"];
        if (Microsoft.Playwright.Program.Main(instalar) != 0)
            throw new InvalidOperationException("Falha ao instalar o Chromium do Playwright.");
        _playwright = await Playwright.CreateAsync();
        Navegador = await _playwright.Chromium.LaunchAsync(new() { Headless = Environment.GetEnvironmentVariable("E2E_JANELA") != "1" });
    }

    private async Task IniciarAplicacao(string conexao)
    {
        var dll = LocalizarAplicacao();
        var info = new ProcessStartInfo("dotnet", $"\"{dll}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Environment =
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["ASPNETCORE_URLS"] = Url,
                ["DATABASE_URL"] = conexao,
                ["DISABLE_JOBS"] = "true",
                ["Logging__LogLevel__Default"] = "Warning",
            },
        };
        _processo = Process.Start(info) ?? throw new InvalidOperationException("Não foi possível iniciar a aplicação.");
        _processo.OutputDataReceived += (_, e) => { lock (_log) _log.AppendLine(e.Data); };
        _processo.ErrorDataReceived += (_, e) => { lock (_log) _log.AppendLine(e.Data); };
        _processo.BeginOutputReadLine();
        _processo.BeginErrorReadLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var limite = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < limite)
        {
            if (_processo.HasExited) break;
            try
            {
                if ((await http.GetAsync($"{Url}/api/v1/health")).IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(500);
        }
        throw new InvalidOperationException($"A aplicação não respondeu em {Url}.\n{Log}");
    }

    public string Log { get { lock (_log) return _log.ToString(); } }

    /// <summary>A saída da compilação de Inovabens.Web, na mesma configuração (Debug/Release) destes testes.</summary>
    private static string LocalizarAplicacao()
    {
        var aqui = new DirectoryInfo(AppContext.BaseDirectory); // tests/Inovabens.E2E/bin/<Config>/net10.0/
        var config = aqui.Parent!.Name;
        var raiz = aqui.Parent.Parent!.Parent!.Parent!.Parent!.FullName; // dotnet/
        var dll = Path.Combine(raiz, "src", "Inovabens.Web", "bin", config, aqui.Name, "Inovabens.Web.dll");
        return File.Exists(dll) ? dll : throw new FileNotFoundException("Compile a solução antes (dotnet build).", dll);
    }

    private static int PortaLivre()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    /// <summary>Página nova num contexto isolado (cookies próprios), com espera padrão de 15 s.</summary>
    public async Task<IPage> NovaPaginaAsync()
    {
        var contexto = await Navegador.NewContextAsync(new() { BaseURL = Url, Locale = "pt-BR", AcceptDownloads = true });
        contexto.SetDefaultTimeout(15_000);
        return await contexto.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        if (Navegador is not null) await Navegador.CloseAsync();
        _playwright?.Dispose();
        if (_processo is { HasExited: false })
        {
            _processo.Kill(entireProcessTree: true);
            await _processo.WaitForExitAsync();
        }
        if (_banco is not null) await _banco.DisposeAsync();
    }
}
