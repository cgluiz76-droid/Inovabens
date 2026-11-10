using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Inovabens.Core.Redes;

/// <summary>Clipe animado (MP4) e a camada com o texto da cena (PNG transparente 1080x1920).</summary>
public sealed record TrechoVideo(byte[] Clipe, byte[] Sobreposicao);

/// <summary>
/// Monta o vídeo vertical (Reels/TikTok/Shorts) com o FFmpeg: cada clipe em 1080x1920 a 30 fps com o texto da cena,
/// o cartão final da marca e a música em volta, com entrada e saída suaves. Caminho em FFMPEG_PATH, no PATH ou nos
/// locais padrão (no Windows também a instalação do winget). Uma montagem por vez.
/// </summary>
public sealed class MontagemVideo(ILogger<MontagemVideo> log)
{
    public const double DuracaoCartao = 3;
    private static readonly TimeSpan Limite = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim fila = new(1, 1);
    private readonly Lazy<string?> caminho = new(Localizar);

    public bool Disponivel => caminho.Value is not null;

    /// <summary>Executável encontrado (null = não instalado).</summary>
    public string? Caminho => caminho.Value;

    /// <summary>MP4 (H.264 + AAC, faststart) pronto para Instagram, Facebook, TikTok e YouTube.</summary>
    public async Task<byte[]> MontarAsync(IReadOnlyList<TrechoVideo> trechos, byte[] cartaoFinal, byte[]? musica, CancellationToken ct = default)
    {
        if (caminho.Value is not { } exe) throw new InvalidOperationException("FFmpeg não instalado.");
        if (trechos.Count == 0) throw new ArgumentException("Sem clipes para montar.", nameof(trechos));
        var pasta = Directory.CreateTempSubdirectory("inovabens-video-").FullName;
        await fila.WaitAsync(ct);
        try
        {
            var lista = new List<string>();
            for (var i = 0; i < trechos.Count; i++)
            {
                var clipe = Path.Combine(pasta, $"clipe{i}.mp4");
                var camada = Path.Combine(pasta, $"texto{i}.png");
                var saida = Path.Combine(pasta, $"trecho{i}.mp4");
                await File.WriteAllBytesAsync(clipe, trechos[i].Clipe, ct);
                await File.WriteAllBytesAsync(camada, trechos[i].Sobreposicao, ct);
                await Rodar(exe, ArgsTrecho(clipe, camada, saida), ct);
                lista.Add(saida);
            }
            var cartao = Path.Combine(pasta, "cartao.png");
            var trechoCartao = Path.Combine(pasta, "cartao.mp4");
            await File.WriteAllBytesAsync(cartao, cartaoFinal, ct);
            await Rodar(exe, ArgsCartao(cartao, trechoCartao), ct);
            lista.Add(trechoCartao);

            var arquivoLista = Path.Combine(pasta, "lista.txt");
            await File.WriteAllLinesAsync(arquivoLista, lista.Select(l => $"file '{l.Replace("\\", "/").Replace("'", "'\\''")}'"), ct);
            var junto = Path.Combine(pasta, "junto.mp4");
            await Rodar(exe, ["-y", "-f", "concat", "-safe", "0", "-i", arquivoLista, "-c", "copy", junto], ct);

            var final = Path.Combine(pasta, "final.mp4");
            var duracao = trechos.Count * DuracaoTrecho + DuracaoCartao;
            string? audio = null;
            if (musica is { Length: > 0 })
            {
                audio = Path.Combine(pasta, "musica.bin");
                await File.WriteAllBytesAsync(audio, musica, ct);
            }
            await Rodar(exe, ArgsFinal(junto, audio, final, duracao), ct);
            return await File.ReadAllBytesAsync(final, ct);
        }
        finally
        {
            fila.Release();
            try { Directory.Delete(pasta, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Duração usada de cada clipe (o Kling entrega 5 s).</summary>
    public const double DuracaoTrecho = 5;

    internal static string[] ArgsTrecho(string clipe, string camada, string saida) =>
    [
        "-y", "-i", clipe, "-i", camada,
        "-filter_complex",
        "[0:v]scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,fps=30,setsar=1[v];[v][1:v]overlay=0:0,format=yuv420p[o]",
        "-map", "[o]", "-an", "-t", Num(DuracaoTrecho),
        "-c:v", "libx264", "-preset", "veryfast", "-crf", "21", "-r", "30", saida,
    ];

    internal static string[] ArgsCartao(string cartao, string saida) =>
    [
        "-y", "-loop", "1", "-i", cartao, "-t", Num(DuracaoCartao),
        "-vf", "scale=1080:1920,fps=30,setsar=1,format=yuv420p,fade=t=in:st=0:d=0.4",
        "-c:v", "libx264", "-preset", "veryfast", "-crf", "21", "-r", "30", saida,
    ];

    /// <summary>Junta a música (repetida se for curta, com fade de entrada e saída) ou um áudio silencioso.</summary>
    internal static string[] ArgsFinal(string video, string? musica, string saida, double duracao)
    {
        var fim = Num(Math.Max(0, duracao - 1.5));
        return musica is null
            ?
            [
                "-y", "-i", video, "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=44100",
                "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "aac", "-b:a", "128k", "-t", Num(duracao),
                "-movflags", "+faststart", saida,
            ]
            :
            [
                "-y", "-i", video, "-stream_loop", "-1", "-i", musica,
                "-filter_complex", $"[1:a]afade=t=in:st=0:d=0.5,afade=t=out:st={fim}:d=1.5,volume=0.85[a]",
                "-map", "0:v", "-map", "[a]", "-c:v", "copy", "-c:a", "aac", "-b:a", "160k", "-t", Num(duracao),
                "-movflags", "+faststart", saida,
            ];
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private async Task Rodar(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg não iniciou.");
        var erro = p.StandardError.ReadToEndAsync(ct);
        _ = p.StandardOutput.ReadToEndAsync(ct);
        using var tempo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        tempo.CancelAfter(Limite);
        try
        {
            await p.WaitForExitAsync(tempo.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("A montagem do vídeo demorou demais.");
        }
        if (p.ExitCode != 0)
        {
            var msg = await erro;
            log.LogWarning("FFmpeg falhou ({Codigo}): {Erro}", p.ExitCode, msg);
            throw new InvalidOperationException("Falha ao montar o vídeo: " + (msg.Length > 300 ? msg[..300] : msg).Trim());
        }
    }

    private static string? Localizar()
    {
        if (Environment.GetEnvironmentVariable("FFMPEG_PATH") is { Length: > 0 } p && File.Exists(p)) return p;
        var nome = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var c = Path.Combine(dir.Trim(), nome);
            if (File.Exists(c)) return c;
        }
        foreach (var c in new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", @"C:\ffmpeg\bin\ffmpeg.exe" })
            if (File.Exists(c)) return c;
        // winget (Gyan.FFmpeg) instala em %LOCALAPPDATA%\Microsoft\WinGet\Packages\Gyan.FFmpeg_*\ffmpeg-*\bin
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (OperatingSystem.IsWindows() && Directory.Exists(winget))
            return Directory.EnumerateDirectories(winget, "Gyan.FFmpeg*")
                .SelectMany(d => Directory.EnumerateFiles(d, "ffmpeg.exe", SearchOption.AllDirectories))
                .FirstOrDefault();
        return null;
    }
}
