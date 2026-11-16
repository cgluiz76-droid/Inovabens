using Inovabens.Core.Comum;
using Inovabens.Core.Financeiro;
using Microsoft.AspNetCore.Components.Forms;

namespace Inovabens.Web.Components.Financeiro;

/// <summary>Envio dos arquivos escolhidos num InputFile como comprovantes (um a um; para no primeiro erro).</summary>
public static class EnvioComprovantes
{
    /// <summary>Confere quantidade e tamanho antes de enviar qualquer arquivo.</summary>
    public static IReadOnlyList<IBrowserFile> Selecionar(InputFileChangeEventArgs e)
    {
        if (e.FileCount > ComprovantesService.MaximoPorEnvio)
            throw new RegraNegocioException("arquivo", $"Selecione no máximo {ComprovantesService.MaximoPorEnvio} arquivos por vez.");
        var arquivos = e.GetMultipleFiles(ComprovantesService.MaximoPorEnvio);
        var grandes = arquivos.Where(f => f.Size > ComprovantesService.TamanhoMaximo).Select(f => f.Name).ToList();
        if (grandes.Count > 0)
            throw new RegraNegocioException("arquivo", $"Arquivo maior que {ComprovantesService.TamanhoMaximo / 1024 / 1024} MB: {string.Join(", ", grandes)}");
        return arquivos;
    }

    public static Task EnviarAsync(ComprovantesService servico, UsuarioAtual u, Guid lancamentoId, InputFileChangeEventArgs e,
        Action<int, int>? progresso = null, Action? enviado = null) =>
        EnviarAsync(servico, u, lancamentoId, Selecionar(e), progresso, enviado);

    public static async Task EnviarAsync(ComprovantesService servico, UsuarioAtual u, Guid lancamentoId, IReadOnlyList<IBrowserFile> arquivos,
        Action<int, int>? progresso = null, Action? enviado = null)
    {
        for (var i = 0; i < arquivos.Count; i++)
        {
            var f = arquivos[i];
            progresso?.Invoke(i + 1, arquivos.Count);
            await using var origemArquivo = f.OpenReadStream(ComprovantesService.TamanhoMaximo);
            using var buffer = new MemoryStream();
            await origemArquivo.CopyToAsync(buffer);
            buffer.Position = 0;
            await servico.EnviarAsync(u, lancamentoId, buffer, f.Name,
                string.IsNullOrEmpty(f.ContentType) ? "application/octet-stream" : f.ContentType, buffer.Length);
            enviado?.Invoke();
        }
    }
}
