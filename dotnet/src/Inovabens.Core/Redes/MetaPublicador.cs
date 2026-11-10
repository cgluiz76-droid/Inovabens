using System.Net.Http.Headers;
using System.Text.Json;
using Inovabens.Core.Dominio;

namespace Inovabens.Core.Redes;

/// <summary>A Meta recusou o pedido (token vencido, permissão faltando, mídia inválida...).</summary>
public sealed class MetaRecusouException(string mensagem) : Exception(mensagem);

/// <summary>Publicação concluída: id do post na rede e o link para abri-lo.</summary>
public sealed record PostPublicado(string Id, string? Link);

/// <summary>
/// Publica na página do Facebook e no Instagram comercial pela Graph API, com o token da página
/// (Administração → Configurações → Meta). As mídias vão por URL pública (R2): a Meta baixa os arquivos.
/// No Instagram a publicação tem duas etapas: cria-se o contêiner e, quando a Meta termina de processar
/// (vídeos levam alguns minutos), publica-se. A rotina de posts confere os contêineres pendentes a cada minuto.
/// </summary>
public sealed class MetaPublicador(HttpClient http, InovabensOpcoes op)
{
    private Integracoes I => op.Integracoes;
    public bool Configurado => I.MetaConfigurado;
    public bool InstagramConfigurado => I.MetaPageToken is not null && I.MetaInstagramId is not null;
    public bool FacebookConfigurado => I.MetaPageToken is not null && I.MetaPageId is not null;

    /// <summary>Cria o contêiner do Instagram e devolve o id dele.</summary>
    public async Task<string> CriarContainerInstagramAsync(RedePublicacao rede, IReadOnlyList<string> urls, string legenda, CancellationToken ct = default)
    {
        var ig = I.MetaInstagramId ?? throw new MetaRecusouException("Informe o ID da conta do Instagram nas configurações.");
        if (urls.Count == 0) throw new MetaRecusouException("Nenhuma mídia para publicar.");
        switch (rede)
        {
            case RedePublicacao.INSTAGRAM_REELS:
                return await Id(Post($"{ig}/media", new() { ["media_type"] = "REELS", ["video_url"] = urls[0], ["caption"] = legenda, ["share_to_feed"] = "true" }, ct));
            case RedePublicacao.INSTAGRAM_STORY:
                return await Id(Post($"{ig}/media", new() { ["media_type"] = "STORIES", ["image_url"] = urls[0] }, ct));
            case RedePublicacao.INSTAGRAM_FEED when urls.Count == 1:
                return await Id(Post($"{ig}/media", new() { ["image_url"] = urls[0], ["caption"] = legenda }, ct));
            case RedePublicacao.INSTAGRAM_FEED:
                var filhos = new List<string>();
                foreach (var u in urls.Take(10))
                    filhos.Add(await Id(Post($"{ig}/media", new() { ["image_url"] = u, ["is_carousel_item"] = "true" }, ct)));
                return await Id(Post($"{ig}/media", new() { ["media_type"] = "CAROUSEL", ["children"] = string.Join(',', filhos), ["caption"] = legenda }, ct));
            default:
                throw new ArgumentOutOfRangeException(nameof(rede));
        }
    }

    /// <summary>Publica o contêiner se a Meta já terminou de processar; null enquanto ainda processa.</summary>
    public async Task<PostPublicado?> TentarPublicarInstagramAsync(string container, CancellationToken ct = default)
    {
        var ig = I.MetaInstagramId ?? throw new MetaRecusouException("Informe o ID da conta do Instagram nas configurações.");
        var status = await Get($"{container}?fields=status_code,status", ct);
        var codigo = status.TryGetProperty("status_code", out var s) ? s.GetString() : null;
        if (codigo is "ERROR" or "EXPIRED")
            throw new MetaRecusouException("O Instagram não aceitou a mídia: " + (status.TryGetProperty("status", out var d) ? d.GetString() : codigo));
        if (codigo != "FINISHED") return null;
        var id = await Id(Post($"{ig}/media_publish", new() { ["creation_id"] = container }, ct));
        string? link = null;
        try
        {
            var dados = await Get($"{id}?fields=permalink", ct);
            link = dados.TryGetProperty("permalink", out var p) ? p.GetString() : null;
        }
        catch (MetaRecusouException) { } // publicado; só não deu para obter o link
        return new PostPublicado(id, link);
    }

    /// <summary>Uma foto vira post com a foto; várias viram um post com álbum (fotos enviadas sem publicar e anexadas).</summary>
    public async Task<PostPublicado> PublicarFacebookFotosAsync(IReadOnlyList<string> urls, string texto, CancellationToken ct = default)
    {
        var pagina = I.MetaPageId ?? throw new MetaRecusouException("Informe o ID da página do Facebook nas configurações.");
        if (urls.Count == 1)
        {
            var r = await Post($"{pagina}/photos", new() { ["url"] = urls[0], ["caption"] = texto }, ct);
            var postId = r.TryGetProperty("post_id", out var pid) ? pid.GetString() : null;
            var id = postId ?? r.GetProperty("id").GetString()!;
            return new PostPublicado(id, $"https://www.facebook.com/{id}");
        }
        var fotos = new List<string>();
        foreach (var u in urls.Take(10))
            fotos.Add(await Id(Post($"{pagina}/photos", new() { ["url"] = u, ["published"] = "false" }, ct)));
        var anexos = JsonSerializer.Serialize(fotos.Select(f => new { media_fbid = f }));
        var feed = await Id(Post($"{pagina}/feed", new() { ["message"] = texto, ["attached_media"] = anexos }, ct));
        return new PostPublicado(feed, $"https://www.facebook.com/{feed}");
    }

    public async Task<PostPublicado> PublicarFacebookVideoAsync(string url, string texto, CancellationToken ct = default)
    {
        var pagina = I.MetaPageId ?? throw new MetaRecusouException("Informe o ID da página do Facebook nas configurações.");
        var id = await Id(Post($"{pagina}/videos", new() { ["file_url"] = url, ["description"] = texto }, ct));
        return new PostPublicado(id, $"https://www.facebook.com/{pagina}/videos/{id}");
    }

    /// <summary>Confere o token lendo o nome da página e o usuário do Instagram (consulta gratuita).</summary>
    public async Task<string> TestarAsync(CancellationToken ct = default)
    {
        if (I.MetaPageToken is null) throw new MetaRecusouException("Informe o token de acesso da página.");
        var partes = new List<string>();
        if (I.MetaPageId is { } pagina)
        {
            var p = await Get($"{pagina}?fields=name", ct);
            partes.Add($"página \"{p.GetProperty("name").GetString()}\"");
        }
        if (I.MetaInstagramId is { } ig)
        {
            var p = await Get($"{ig}?fields=username", ct);
            partes.Add($"Instagram @{p.GetProperty("username").GetString()}");
        }
        if (partes.Count == 0) throw new MetaRecusouException("Informe o ID da página e/ou do Instagram.");
        return "Token aceito: " + string.Join(" e ", partes) + ".";
    }

    private string Url(string caminho) => $"https://graph.facebook.com/{I.MetaVersao}/{caminho}";

    private async Task<JsonElement> Post(string caminho, Dictionary<string, string> campos, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Url(caminho)) { Content = new FormUrlEncodedContent(campos) };
        return await Enviar(req, ct);
    }

    private async Task<JsonElement> Get(string caminho, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Url(caminho));
        return await Enviar(req, ct);
    }

    private static async Task<string> Id(Task<JsonElement> resposta) =>
        (await resposta).TryGetProperty("id", out var id) ? id.GetString()! : throw new MetaRecusouException("A Meta não devolveu o id.");

    /// <summary>Token no cabeçalho (fora da URL, para não aparecer em logs). Erros da Graph API viram <see cref="MetaRecusouException"/>.</summary>
    private async Task<JsonElement> Enviar(HttpRequestMessage req, CancellationToken ct)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", I.MetaPageToken ?? throw new MetaRecusouException("Informe o token de acesso da página."));
        using var resp = await http.SendAsync(req, ct);
        var corpo = await resp.Content.ReadAsStringAsync(ct);
        JsonElement json;
        try
        {
            using var doc = JsonDocument.Parse(corpo.Length == 0 ? "{}" : corpo);
            json = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            resp.EnsureSuccessStatusCode();
            throw new MetaRecusouException("Resposta inválida da Meta.");
        }
        if (json.TryGetProperty("error", out var erro))
        {
            var msg = erro.TryGetProperty("error_user_msg", out var um) ? um.GetString()
                : erro.TryGetProperty("message", out var m) ? m.GetString() : "erro desconhecido";
            // 5xx e limite de chamadas: falha temporária, a rotina tenta de novo
            if ((int)resp.StatusCode >= 500 || erro.TryGetProperty("is_transient", out var t) && t.ValueKind == JsonValueKind.True)
                throw new HttpRequestException("Meta indisponível: " + msg);
            throw new MetaRecusouException(msg ?? "erro desconhecido");
        }
        resp.EnsureSuccessStatusCode();
        return json;
    }
}
