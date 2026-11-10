using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Inovabens.Core.Servicos;

/// <summary>
/// Converte .docx em PDF com o LibreOffice sem interface (soffice --headless). Caminho em LIBREOFFICE_PATH ou nos locais
/// padrão do Windows e do Linux. Sem LibreOffice, <see cref="Disponivel"/> é falso e o sistema entrega o .docx preenchido.
/// Uma conversão por vez (o LibreOffice não gosta de instâncias paralelas no mesmo perfil).
/// </summary>
public sealed class ConversorPdf(ILogger<ConversorPdf> log)
{
    private static readonly string[] Candidatos =
    [
        @"C:\Program Files\LibreOffice\program\soffice.exe",
        @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
        "/usr/bin/soffice", "/usr/bin/libreoffice", "/usr/lib/libreoffice/program/soffice", "/opt/libreoffice/program/soffice",
    ];

    private readonly SemaphoreSlim fila = new(1, 1);
    private readonly Lazy<string?> caminho = new(() =>
        Environment.GetEnvironmentVariable("LIBREOFFICE_PATH") is { Length: > 0 } p && File.Exists(p) ? p : Candidatos.FirstOrDefault(File.Exists));

    public bool Disponivel => caminho.Value is not null;

    /// <summary>PDF gerado, ou null se o LibreOffice não estiver instalado ou a conversão falhar.</summary>
    public async Task<byte[]?> ConverterAsync(byte[] docx, CancellationToken ct = default)
    {
        if (caminho.Value is not { } exe) return null;
        var pasta = Directory.CreateTempSubdirectory("inovabens-pdf-").FullName;
        await fila.WaitAsync(ct);
        try
        {
            var entrada = Path.Combine(pasta, "documento.docx");
            await File.WriteAllBytesAsync(entrada, docx, ct);
            var perfil = new Uri(Path.Combine(pasta, "perfil")).AbsoluteUri;
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in new[] { "--headless", "--norestore", $"-env:UserInstallation={perfil}", "--convert-to", "pdf", "--outdir", pasta, entrada })
                psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(TimeSpan.FromSeconds(90));
            try { await proc.WaitForExitAsync(limite.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                proc.Kill(entireProcessTree: true);
                log.LogWarning("LibreOffice demorou demais para converter o documento");
                return null;
            }
            var saida = Path.Combine(pasta, "documento.pdf");
            if (proc.ExitCode == 0 && File.Exists(saida)) return await File.ReadAllBytesAsync(saida, ct);
            log.LogWarning("LibreOffice falhou ({Codigo}): {Erro}", proc.ExitCode, await proc.StandardError.ReadToEndAsync(ct));
            return null;
        }
        finally
        {
            fila.Release();
            try { Directory.Delete(pasta, recursive: true); } catch (IOException) { /* arquivo preso: o sistema limpa o temp depois */ }
        }
    }
}
