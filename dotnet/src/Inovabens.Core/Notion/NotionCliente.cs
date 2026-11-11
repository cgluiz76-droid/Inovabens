using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Inovabens.Core.Notion;

public sealed class NotionException(string mensagem) : Exception(mensagem);

/// <summary>
/// Cliente SÓ DE LEITURA da API oficial do Notion. Não há método que crie, altere, arquive ou apague nada:
/// os únicos pedidos são GET (páginas, blocos, propriedades) e a consulta de base (POST /databases/{id}/query, que
/// só lê). Use uma integração interna com a capacidade "Ler conteúdo" apenas, para o Notion também recusar escrita.
/// </summary>
public sealed class NotionCliente
{
    public const string Versao = "2022-06-28";
    private const string Base = "https://api.notion.com/v1/";

    private readonly HttpClient http;
    private readonly string token;

    public NotionCliente(string token, HttpClient? http = null)
    {
        this.token = token.Trim();
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>Pausa entre pedidos (o Notion aceita em média 3 por segundo).</summary>
    public TimeSpan Pausa { get; init; } = TimeSpan.FromMilliseconds(350);

    private async Task<JsonElement> PedirAsync(HttpMethod metodo, string caminho, object? corpo, CancellationToken ct)
    {
        if (metodo != HttpMethod.Get && !(metodo == HttpMethod.Post && caminho.EndsWith("/query", StringComparison.Ordinal)))
            throw new InvalidOperationException("O cliente do Notion é só de leitura.");
        for (var tentativa = 0; ; tentativa++)
        {
            using var req = new HttpRequestMessage(metodo, Base + caminho);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("Notion-Version", Versao);
            if (corpo is not null) req.Content = JsonContent.Create(corpo);
            using var resp = await http.SendAsync(req, ct);
            if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
                && tentativa < 5)
            {
                var espera = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * (tentativa + 1));
                await Task.Delay(espera, ct);
                continue;
            }
            var texto = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var msg = texto;
                try { msg = JsonDocument.Parse(texto).RootElement.GetProperty("message").GetString() ?? texto; } catch (JsonException) { }
                throw new NotionException($"Notion {(int)resp.StatusCode} em {caminho}: {msg}");
            }
            await Task.Delay(Pausa, ct);
            return JsonDocument.Parse(texto).RootElement.Clone();
        }
    }

    /// <summary>Todas as linhas (páginas) de uma base, inclusive as que passam de 100.</summary>
    public async Task<List<JsonElement>> LinhasAsync(string baseId, CancellationToken ct = default)
    {
        var r = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var corpo = cursor is null ? (object)new { page_size = 100 } : new { page_size = 100, start_cursor = cursor };
            var pag = await PedirAsync(HttpMethod.Post, $"databases/{baseId}/query", corpo, ct);
            r.AddRange(pag.GetProperty("results").EnumerateArray());
            cursor = pag.TryGetProperty("has_more", out var mais) && mais.GetBoolean() ? pag.GetProperty("next_cursor").GetString() : null;
        } while (cursor is not null);
        return r;
    }

    public Task<JsonElement> PaginaAsync(string id, CancellationToken ct = default) => PedirAsync(HttpMethod.Get, $"pages/{id}", null, ct);

    /// <summary>Blocos filhos de um bloco ou página (um nível), todos.</summary>
    public async Task<List<JsonElement>> FilhosAsync(string blocoId, CancellationToken ct = default)
    {
        var r = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var pag = await PedirAsync(HttpMethod.Get,
                $"blocks/{blocoId}/children?page_size=100" + (cursor is null ? "" : "&start_cursor=" + Uri.EscapeDataString(cursor)), null, ct);
            r.AddRange(pag.GetProperty("results").EnumerateArray());
            cursor = pag.TryGetProperty("has_more", out var mais) && mais.GetBoolean() ? pag.GetProperty("next_cursor").GetString() : null;
        } while (cursor is not null);
        return r;
    }

    /// <summary>Ids de uma relação completa (a página só traz as 25 primeiras quando há mais).</summary>
    public async Task<List<string>> RelacaoAsync(string paginaId, string propriedadeId, CancellationToken ct = default)
    {
        var r = new List<string>();
        string? cursor = null;
        do
        {
            var pag = await PedirAsync(HttpMethod.Get,
                $"pages/{paginaId}/properties/{Uri.EscapeDataString(propriedadeId)}?page_size=100" + (cursor is null ? "" : "&start_cursor=" + Uri.EscapeDataString(cursor)), null, ct);
            if (pag.TryGetProperty("results", out var itens))
                r.AddRange(itens.EnumerateArray().Select(i => NotionLeitor.IdCurto(i.GetProperty("relation").GetProperty("id").GetString())));
            cursor = pag.TryGetProperty("has_more", out var mais) && mais.GetBoolean() ? pag.GetProperty("next_cursor").GetString() : null;
        } while (cursor is not null);
        return r;
    }

    /// <summary>Baixa um arquivo guardado no Notion (link assinado, vale por 1 hora).</summary>
    public async Task<(byte[] Dados, string Tipo)> BaixarAsync(string url, CancellationToken ct = default)
    {
        using var resp = await http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadAsByteArrayAsync(ct), resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }
}
