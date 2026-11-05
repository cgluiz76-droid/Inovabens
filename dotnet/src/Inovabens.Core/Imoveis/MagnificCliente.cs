using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Inovabens.Core.Imoveis;

public enum SituacaoTarefaMagnific { EmAndamento, Concluida, Falhou }

public sealed record TarefaMagnific(string Id, SituacaoTarefaMagnific Situacao, string? UrlResultado);

/// <summary>O Magnific recusou o pedido (4xx): não adianta repetir com a mesma foto.</summary>
public sealed class MagnificRecusouException(string mensagem) : Exception(mensagem);

/// <summary>
/// Cliente da API do Magnific (https://docs.magnific.com), upscaler Precision V2: fiel à foto original, sem inventar
/// elementos. As tarefas são assíncronas: cria-se a tarefa e consulta-se o resultado depois.
/// </summary>
public sealed class MagnificCliente(HttpClient http, InovabensOpcoes op)
{
    public const string Endpoint = "https://api.magnific.com/v1/ai/image-upscaler-precision-v2";
    private const long TamanhoMaximoResultado = 100 * 1024 * 1024;

    public bool Configurado => op.Integracoes.MagnificApiKey is not null;

    /// <summary>Envia a foto (JPEG) para ampliar 2x com o perfil "photo".</summary>
    public async Task<TarefaMagnific> CriarAsync(byte[] jpeg, CancellationToken ct = default)
    {
        using var req = Requisicao(HttpMethod.Post, Endpoint);
        req.Content = JsonContent.Create(new
        {
            image = Convert.ToBase64String(jpeg),
            scale_factor = 2,
            flavor = "photo",
        });
        return await Enviar(req, ct);
    }

    public async Task<TarefaMagnific> ConsultarAsync(string tarefaId, CancellationToken ct = default)
    {
        using var req = Requisicao(HttpMethod.Get, $"{Endpoint}/{Uri.EscapeDataString(tarefaId)}");
        return await Enviar(req, ct);
    }

    /// <summary>Baixa a imagem gerada (só HTTPS, até 100 MB).</summary>
    public async Task<byte[]> BaixarResultadoAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new MagnificRecusouException("URL de resultado inválida.");
        using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > TamanhoMaximoResultado) throw new MagnificRecusouException("Imagem gerada grande demais.");
        await using var origem = await resp.Content.ReadAsStreamAsync(ct);
        using var destino = new MemoryStream();
        var buffer = new byte[81920];
        int lidos;
        while ((lidos = await origem.ReadAsync(buffer, ct)) > 0)
        {
            if (destino.Length + lidos > TamanhoMaximoResultado) throw new MagnificRecusouException("Imagem gerada grande demais.");
            destino.Write(buffer, 0, lidos);
        }
        return destino.ToArray();
    }

    /// <summary>Confere a chave numa consulta gratuita (lista de estilos).</summary>
    public async Task TestarAsync(CancellationToken ct = default)
    {
        using var req = Requisicao(HttpMethod.Get, "https://api.magnific.com/v1/ai/loras");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new MagnificRecusouException($"Magnific recusou a chave ({(int)resp.StatusCode}).");
    }

    private HttpRequestMessage Requisicao(HttpMethod metodo, string url)
    {
        var req = new HttpRequestMessage(metodo, url);
        req.Headers.Add("x-magnific-api-key", op.Integracoes.MagnificApiKey ?? throw new InvalidOperationException("MAGNIFIC_API_KEY não configurada."));
        return req;
    }

    private async Task<TarefaMagnific> Enviar(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError and not HttpStatusCode.TooManyRequests)
        {
            var corpo = await resp.Content.ReadAsStringAsync(ct);
            throw new MagnificRecusouException($"Magnific recusou ({(int)resp.StatusCode}): {(corpo.Length > 300 ? corpo[..300] : corpo)}");
        }
        resp.EnsureSuccessStatusCode(); // 429/5xx: falha temporária, tenta de novo na próxima rodada
        var r = await resp.Content.ReadFromJsonAsync<Resposta>(ct) ?? throw new HttpRequestException("Resposta vazia do Magnific.");
        var situacao = r.Data.Status switch
        {
            "COMPLETED" => SituacaoTarefaMagnific.Concluida,
            "FAILED" => SituacaoTarefaMagnific.Falhou,
            _ => SituacaoTarefaMagnific.EmAndamento, // CREATED, IN_PROGRESS
        };
        return new TarefaMagnific(r.Data.TaskId, situacao, r.Data.Generated?.FirstOrDefault());
    }

    private sealed record Resposta([property: JsonPropertyName("data")] DadosTarefa Data);

    private sealed record DadosTarefa(
        [property: JsonPropertyName("task_id")] string TaskId,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("generated")] List<string>? Generated);
}
