using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GeradorPaginas;

/// <summary>Um item da verificação de login (tela "Login necessário").</summary>
public sealed record ItemVerificacao(string Nome, bool Ok, string Detalhe);

/// <summary>Resultado da verificação: só gera com <see cref="Ok"/>; o aviso aparece mesmo quando está tudo certo.</summary>
public sealed record Verificacao(bool Ok, IReadOnlyList<ItemVerificacao> Itens, string? Aviso);

/// <summary>Erro mostrado ao usuário como está (mensagem em português).</summary>
public sealed class ErroGerador(string mensagem, string? bruto = null) : Exception(mensagem)
{
    public string? Bruto { get; } = bruto;
}

/// <summary>
/// Chama o Claude Code CLI (<c>claude -p</c>) com o login da assinatura do usuário do Windows. Nunca usa chave de API:
/// as variáveis ANTHROPIC_* e CLAUDE_* não passam para o processo filho (uma ANTHROPIC_API_KEY faria o uso ser cobrado
/// pela API; as CLAUDE_* de uma sessão do Claude Code aberta trocariam o login pelo daquela sessão).
/// </summary>
public sealed class ClaudeCli(IConfiguration config, ILogger<ClaudeCli> log)
{
    private static readonly string[] VariaveisCobradas = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN"];

    /// <summary>Variável de chave encontrada ao subir (já removida do processo); fica no aviso da tela.</summary>
    public static string? ChaveEncontrada { get; private set; }

    /// <summary>Ao iniciar: tira as variáveis de chave deste processo para nenhum filho herdar.</summary>
    public static void RemoverChavesDoProcesso()
    {
        foreach (var v in VariaveisCobradas)
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)))
            {
                ChaveEncontrada = v;
                Environment.SetEnvironmentVariable(v, null);
            }
    }

    private TimeSpan Timeout => TimeSpan.FromSeconds(config.GetValue("Gerador:TimeoutSegundos", 180));

    /// <summary>Caminho do executável do Claude Code no PATH (claude.exe, ou claude.cmd/.bat da instalação pelo npm).</summary>
    public static string? Localizar()
    {
        var pastas = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var nomes = OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd", "claude.bat" } : new[] { "claude" };
        foreach (var pasta in pastas)
            foreach (var nome in nomes)
            {
                try
                {
                    var caminho = Path.Combine(pasta.Trim('"'), nome);
                    if (File.Exists(caminho)) return caminho;
                }
                catch (ArgumentException) { /* pasta inválida no PATH */ }
            }
        return null;
    }

    /// <summary>
    /// 1) claude no PATH; 2) login ativo pela assinatura (<c>claude auth status --json</c>); 3) variável de chave da API.
    /// A variável é removida deste processo ao subir e nunca chega ao claude; se ainda estiver definida no Windows, avisa.
    /// </summary>
    public async Task<Verificacao> VerificarAsync(CancellationToken ct = default)
    {
        var itens = new List<ItemVerificacao>();
        RemoverChavesDoProcesso();
        var noWindows = VariaveisCobradas.Where(v => OperatingSystem.IsWindows()
            && (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v, EnvironmentVariableTarget.User))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v, EnvironmentVariableTarget.Machine)))).ToList();
        var aviso = ChaveEncontrada is null && noWindows.Count == 0 ? null
            : $"A variável {ChaveEncontrada ?? noWindows[0]} está definida no Windows. Com ela, o Claude Code cobraria o uso pela API " +
              "(fora da assinatura). Esta ferramenta a remove antes de chamar o claude, mas apague-a das variáveis de ambiente do " +
              "Windows para não ser cobrado em outros usos.";
        itens.Add(new("Chave da API fora do caminho", true, ChaveEncontrada is null && noWindows.Count == 0
            ? "ANTHROPIC_API_KEY não está definida."
            : "Variável encontrada e removida: o claude é chamado sem ela."));

        var exe = Localizar();
        if (exe is null)
        {
            itens.Insert(0, new("Claude Code instalado", false, "O comando \"claude\" não foi encontrado no PATH."));
            return new(false, itens, aviso);
        }
        itens.Insert(0, new("Claude Code instalado", true, exe));

        try
        {
            var (saida, erro, codigo) = await ExecutarAsync(exe, ["auth", "status", "--json"], null, TimeSpan.FromSeconds(30), ct);
            using var doc = JsonDocument.Parse(saida);
            var r = doc.RootElement;
            var logado = r.TryGetProperty("loggedIn", out var l) && l.ValueKind == JsonValueKind.True;
            var metodo = r.TryGetProperty("authMethod", out var m) ? m.GetString() : null;
            var plano = r.TryGetProperty("subscriptionType", out var p) ? p.GetString() : null;
            var assinatura = logado && metodo == "claude.ai";
            itens.Insert(1, new("Login da assinatura", assinatura,
                !logado ? "Não está logado no Claude Code."
                : !assinatura ? $"Logado por \"{metodo}\", não pela assinatura (claude.ai). Faça /login com a conta Claude."
                : $"Logado pela assinatura Claude{(plano is null ? "" : " " + plano)}."));
            if (codigo != 0 && !logado) log.LogWarning("claude auth status saiu com {Codigo}: {Erro}", codigo, erro);
        }
        catch (Exception e) when (e is JsonException or ErroGerador or Win32Exception)
        {
            itens.Insert(1, new("Login da assinatura", false, "Não foi possível consultar o login: " + e.Message));
        }
        return new(itens.All(i => i.Ok), itens, aviso);
    }

    /// <summary>
    /// Pedido ao Claude: o prompt vai pelo stdin (sem limite de tamanho nem escape da linha de comando do Windows), sem
    /// ferramentas, sem MCP, sem configurações de usuário/projeto e sem guardar a sessão. Devolve o texto da resposta.
    /// </summary>
    public async Task<string> PerguntarAsync(string prompt, CancellationToken ct = default)
    {
        var exe = Localizar() ?? throw new ErroGerador("O comando \"claude\" não foi encontrado no PATH.");
        var args = new List<string> { "-p", "--output-format", "json", "--no-session-persistence", "--tools", "",
            "--strict-mcp-config", "--setting-sources", "" };
        if (config["Gerador:Modelo"] is { Length: > 0 } modelo) { args.Add("--model"); args.Add(modelo); }

        var (saida, erro, codigo) = await ExecutarAsync(exe, args, prompt, Timeout, ct);
        JsonElement r;
        try { r = JsonDocument.Parse(saida).RootElement; }
        catch (JsonException)
        {
            throw new ErroGerador($"O claude não devolveu JSON (código {codigo}). {Resumo(erro)}", saida);
        }
        var texto = r.TryGetProperty("result", out var res) ? res.GetString() ?? "" : "";
        if (r.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True || codigo != 0)
            throw new ErroGerador("O Claude respondeu com erro: " + (texto.Length > 0 ? texto : Resumo(erro)), saida);
        return texto;
    }

    private static string Resumo(string t) => t.Length > 400 ? t[..400] + "…" : t;

    /// <summary>Roda o claude com o ambiente limpo; mata a árvore de processos no tempo limite.</summary>
    private static async Task<(string Saida, string Erro, int Codigo)> ExecutarAsync(
        string exe, IReadOnlyList<string> args, string? entrada, TimeSpan limite, CancellationToken ct)
    {
        var cmd = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(cmd ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            // pasta vazia fora de qualquer projeto: o claude não carrega CLAUDE.md nem configurações de um repositório
            WorkingDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "GeradorPaginas-trabalho")).FullName,
        };
        if (cmd) { psi.ArgumentList.Add("/d"); psi.ArgumentList.Add("/s"); psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(exe); }
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var chave in psi.Environment.Keys.ToList())
            if (chave.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase) || chave.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase))
                psi.Environment.Remove(chave);

        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Win32Exception e) { throw new ErroGerador("Não foi possível iniciar o claude: " + e.Message); }

        var lerSaida = p.StandardOutput.ReadToEndAsync(ct);
        var lerErro = p.StandardError.ReadToEndAsync(ct);
        if (entrada is not null)
        {
            // UTF-8 sem BOM: o claude lê o prompt exatamente como foi escrito (acentos incluídos)
            await using var sw = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false));
            await sw.WriteAsync(entrada.AsMemory(), ct);
        }
        else p.StandardInput.Close();

        using var tempo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        tempo.CancelAfter(limite);
        try { await p.WaitForExitAsync(tempo.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw ct.IsCancellationRequested
                ? new ErroGerador("Pedido cancelado.")
                : new ErroGerador($"O Claude não respondeu em {limite.TotalSeconds:0} segundos. Tente um pedido menor ou tente de novo.");
        }
        return (await lerSaida, await lerErro, p.ExitCode);
    }
}
