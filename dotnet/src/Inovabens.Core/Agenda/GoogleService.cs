using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inovabens.Core.Agenda;

public sealed record StatusGoogle(bool Configurado, bool Conectado, string? GoogleEmail, DateTime? UltimaSyncEm);

public sealed record ResultadoSyncGoogle(int Importados, int Atualizados, int Cancelados);

public sealed class EventoGoogle
{
    public string Id { get; set; } = "";
    public string? Etag { get; set; }
    public string? Status { get; set; }
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public string? Location { get; set; }
    public DateTimeOffset? Updated { get; set; }
    public HorarioGoogle? Start { get; set; }
    public HorarioGoogle? End { get; set; }
    public PropriedadesGoogle? ExtendedProperties { get; set; }
}

public sealed class HorarioGoogle
{
    public DateTimeOffset? DateTime { get; set; }
    public string? Date { get; set; }
    public string? TimeZone { get; set; }
}

public sealed class PropriedadesGoogle
{
    public Dictionary<string, string>? Private { get; set; }
}

/// <summary>Mapeamentos puros entre compromisso e evento do Google (testáveis sem rede).</summary>
public static partial class MapeamentoGoogle
{
    private const string Fuso = "America/Sao_Paulo";
    public const string PropriedadeId = "inovabens_compromisso_id";

    public static object ParaEvento(Compromisso c, string imovelCodigo, IReadOnlyCollection<string> emails) => new
    {
        summary = c.Titulo,
        description = string.Join("\n\n", new[] { c.Descricao, $"Imóvel {imovelCodigo} (#{imovelCodigo})" }.Where(x => !string.IsNullOrWhiteSpace(x))),
        location = c.Local,
        start = new { dateTime = DateTime.SpecifyKind(c.InicioEm, DateTimeKind.Utc).ToString("o"), timeZone = Fuso },
        end = new { dateTime = DateTime.SpecifyKind(c.FimEm, DateTimeKind.Utc).ToString("o"), timeZone = Fuso },
        colorId = c.Status == StatusCompromisso.CONCLUIDO ? "2" : null,
        attendees = emails.Count > 0 ? emails.Select(e => new { email = e }).ToArray() : null,
        extendedProperties = new { @private = new Dictionary<string, string> { [PropriedadeId] = c.Id.ToString() } },
    };

    /// <summary>end.date do Google (dia inteiro) é exclusivo: termina 1 s antes da meia-noite.</summary>
    private static DateTime? Quando(HorarioGoogle? h, bool fim)
    {
        if (h?.DateTime is { } dt) return dt.UtcDateTime;
        if (h?.Date is { } d && DateOnly.TryParse(d, out var dia))
            return new DateTimeOffset(dia.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(-3)).UtcDateTime.AddSeconds(fim ? -1 : 0);
        return null;
    }

    public static (string Titulo, string? Local, DateTime Inicio, DateTime Fim)? DeEvento(EventoGoogle ev)
    {
        if (Quando(ev.Start, false) is not { } inicio || Quando(ev.End, true) is not { } fim) return null;
        var titulo = string.IsNullOrWhiteSpace(ev.Summary) ? "(sem título)" : ev.Summary;
        return (titulo.Length > 200 ? titulo[..200] : titulo, ev.Location is { Length: > 200 } l ? l[..200] : ev.Location,
            inicio, fim < inicio ? inicio : fim);
    }

    /// <summary>Evento criado direto no Google é importado se citar o código do imóvel (ex.: "Visita #IMV-000012").</summary>
    public static string? CodigoImovelNoTexto(EventoGoogle ev) =>
        CodigoRegex().Match($"{ev.Summary} {ev.Description}") is { Success: true } m ? m.Groups[1].Value.ToUpperInvariant() : null;

    [GeneratedRegex(@"#(IMV-\d{4,})", RegexOptions.IgnoreCase)]
    private static partial Regex CodigoRegex();
}

/// <summary>
/// Integração com o Google Calendar (port de google.service.ts): OAuth por usuário, envio dos compromissos,
/// sincronização incremental por syncToken (vence a alteração mais recente) e notificações por webhook.
/// Singleton: guarda o cache de access tokens.
/// </summary>
public sealed class GoogleService(IDbContextFactory<InovabensDb> fabrica, InovabensOpcoes op, ILogger<GoogleService> log)
{
    private const string Api = "https://www.googleapis.com/calendar/v3";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly ConcurrentDictionary<Guid, (string Token, DateTime Expira)> _tokens = new();

    public bool Configurado => op.GoogleConfigurado;

    private void Exigir()
    {
        if (!Configurado) throw new ServicoIndisponivelException("Integração com o Google não configurada (GOOGLE_CLIENT_ID/SECRET/REDIRECT_URI).");
    }

    private static Task<GoogleConta?> ContaDe(InovabensDb db, Guid usuarioId) =>
        db.GoogleContas.SingleOrDefaultAsync(c => c.UsuarioId == usuarioId);

    public async Task<StatusGoogle> StatusAsync(Guid usuarioId)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await ContaDe(db, usuarioId);
        return new StatusGoogle(Configurado, c is not null, c?.GoogleEmail, c?.UltimaSyncEm);
    }

    /* ------------------------------- OAuth ------------------------------- */

    public string Hmac(string valor) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(op.SegredoHmac), Encoding.UTF8.GetBytes(valor)));

    /// <summary>state = usuário + validade (10 min), assinado com HMAC: identifica o usuário na volta do Google.</summary>
    private string AssinarEstado(Guid usuarioId)
    {
        var corpo = $"{usuarioId:N}.{DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()}";
        return $"{corpo}.{Hmac("google-oauth:" + corpo)}";
    }

    private Guid? LerEstado(string state)
    {
        var p = state.Split('.');
        if (p.Length != 3 || !Guid.TryParseExact(p[0], "N", out var id) || !long.TryParse(p[1], out var exp)) return null;
        var esperado = Hmac($"google-oauth:{p[0]}.{p[1]}");
        if (!IgualSeguro(esperado, p[2])) return null;
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp ? null : id;
    }

    private static bool IgualSeguro(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    public string UrlAutorizacao(UsuarioAtual u)
    {
        u.Exigir(Permissoes.AgendaVer);
        Exigir();
        var q = new Dictionary<string, string>
        {
            ["client_id"] = op.GoogleClientId!, ["redirect_uri"] = op.GoogleRedirectUri!, ["response_type"] = "code",
            ["scope"] = "https://www.googleapis.com/auth/calendar.events openid email", ["access_type"] = "offline",
            ["prompt"] = "consent", ["state"] = AssinarEstado(u.Id),
        };
        return "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join('&', q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>Volta do Google: troca o code pelo refresh token (guardado cifrado) e devolve para onde redirecionar.</summary>
    public async Task<string> CallbackAsync(string code, string state)
    {
        Exigir();
        var usuarioId = LerEstado(state) ?? throw new RegraNegocioException("state", "Estado OAuth inválido ou expirado");
        using var r = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code, ["client_id"] = op.GoogleClientId!, ["client_secret"] = op.GoogleClientSecret!,
            ["redirect_uri"] = op.GoogleRedirectUri!, ["grant_type"] = "authorization_code",
        }));
        var t = await r.Content.ReadFromJsonAsync<JsonElement>();
        var refresh = t.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        if (!r.IsSuccessStatusCode || refresh is null)
            throw new RegraNegocioException("_", t.TryGetProperty("error_description", out var ed) ? ed.GetString() ?? "Falha no Google"
                : "O Google não devolveu permissão offline. Revogue o acesso e tente de novo.");
        string? email = null;
        if (t.TryGetProperty("id_token", out var idt) && idt.GetString()?.Split('.') is [_, var payload, ..])
        {
            var b = payload.Replace('-', '+').Replace('_', '/');
            b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(b));
            email = doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
        }

        await using (var db = await fabrica.CreateDbContextAsync())
        {
            var conta = await ContaDe(db, usuarioId);
            if (conta is null) db.GoogleContas.Add(conta = new GoogleConta { UsuarioId = usuarioId });
            conta.GoogleEmail = email;
            conta.RefreshTokenCifrado = Cripto.Cifrar(refresh, op.TokenEncKey);
            conta.SyncToken = null;
            await db.SaveChangesAsync();
            _tokens.TryRemove(conta.Id, out _);
        }
        EmSegundoPlano(async () =>
        {
            await IniciarWatchAsync(usuarioId);
            await SincronizarAsync(usuarioId);
        }, "conexão inicial");
        return $"{op.WebUrl}/agenda?google=ok";
    }

    public async Task DesconectarAsync(Guid usuarioId)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var conta = await ContaDe(db, usuarioId);
        if (conta is null) return;
        try { await PararWatchAsync(conta); } catch { /* segue desconectando */ }
        try { await Http.PostAsync($"https://oauth2.googleapis.com/revoke?token={Uri.EscapeDataString(Cripto.Decifrar(conta.RefreshTokenCifrado, op.TokenEncKey))}", null); }
        catch { /* token já inválido */ }
        db.GoogleContas.Remove(conta);
        await db.SaveChangesAsync();
        _tokens.TryRemove(conta.Id, out _);
    }

    private async Task<string> AccessTokenAsync(GoogleConta conta)
    {
        if (_tokens.TryGetValue(conta.Id, out var c) && c.Expira > DateTime.UtcNow.AddMinutes(1)) return c.Token;
        using var r = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = op.GoogleClientId!, ["client_secret"] = op.GoogleClientSecret!,
            ["refresh_token"] = Cripto.Decifrar(conta.RefreshTokenCifrado, op.TokenEncKey), ["grant_type"] = "refresh_token",
        }));
        var t = await r.Content.ReadFromJsonAsync<JsonElement>();
        if (!r.IsSuccessStatusCode || !t.TryGetProperty("access_token", out var at))
            throw new ServicoIndisponivelException("Não foi possível renovar o acesso ao Google (reconecte a conta).");
        var expira = DateTime.UtcNow.AddSeconds(t.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600);
        _tokens[conta.Id] = (at.GetString()!, expira);
        return at.GetString()!;
    }

    private async Task<(HttpStatusCode Status, T? Dados)> ChamarAsync<T>(GoogleConta conta, HttpMethod metodo, string caminho, object? corpo = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(metodo, $"{Api}/calendars/{Uri.EscapeDataString(conta.CalendarId)}{caminho}");
        req.Headers.Authorization = new("Bearer", await AccessTokenAsync(conta));
        if (corpo is not null) req.Content = JsonContent.Create(corpo, options: Json);
        using var r = await Http.SendAsync(req, ct);
        if (!r.IsSuccessStatusCode && r.StatusCode is not (HttpStatusCode.Gone or HttpStatusCode.NotFound))
            throw new ServicoIndisponivelException($"Google Calendar {metodo} {caminho}: HTTP {(int)r.StatusCode}");
        var dados = r.StatusCode == HttpStatusCode.NoContent || !r.IsSuccessStatusCode ? default
            : await r.Content.ReadFromJsonAsync<T>(Json, ct);
        return (r.StatusCode, dados);
    }

    /// <summary>Executa sem bloquear nem quebrar a operação principal (como o "void ... .catch()" do Node).</summary>
    private void EmSegundoPlano(Func<Task> acao, string oQue) => _ = Task.Run(async () =>
    {
        try { await acao(); }
        catch (Exception e) { log.LogWarning(e, "Google ({OQue}) falhou", oQue); }
    });

    /* ----------------------- Sistema → Google (push) ----------------------- */

    public void Empurrar(Guid compromissoId)
    {
        if (Configurado) EmSegundoPlano(() => EmpurrarAsync(compromissoId), "envio");
    }

    public async Task EmpurrarAsync(Guid compromissoId)
    {
        if (!Configurado) return;
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await db.Compromissos.Include(x => x.Imovel).SingleOrDefaultAsync(x => x.Id == compromissoId);
        if (c is null) return;
        var conta = await ContaDe(db, c.ResponsavelId);
        if (conta is null) return;
        if (c.Status == StatusCompromisso.CANCELADO || c.ExcluidoEm is not null)
        {
            await RemoverEventoAsync(conta, c.GoogleEventId);
            return;
        }
        var emails = await db.CompromissoParticipantes.Where(p => p.CompromissoId == compromissoId && p.Email != null)
            .Select(p => p.Email!).ToListAsync();
        var corpo = MapeamentoGoogle.ParaEvento(c, c.Imovel!.Codigo, emails);
        var res = c.GoogleEventId is { } ev
            ? await ChamarAsync<EventoGoogle>(conta, HttpMethod.Put, $"/events/{Uri.EscapeDataString(ev)}", corpo)
            : await ChamarAsync<EventoGoogle>(conta, HttpMethod.Post, "/events", corpo);
        if (res.Status == HttpStatusCode.NotFound && c.GoogleEventId is not null) // apagado no Google: recria
            res = await ChamarAsync<EventoGoogle>(conta, HttpMethod.Post, "/events", corpo);
        if (res.Dados is { } novo)
        {
            c.GoogleEventId = novo.Id;
            c.GoogleEtag = novo.Etag;
            c.SincronizadoEm = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    private async Task RemoverEventoAsync(GoogleConta conta, string? eventId)
    {
        if (eventId is not null) await ChamarAsync<JsonElement>(conta, HttpMethod.Delete, $"/events/{Uri.EscapeDataString(eventId)}");
    }

    public void RemoverDoGoogle(Guid responsavelId, string? eventId)
    {
        if (!Configurado || eventId is null) return;
        EmSegundoPlano(async () =>
        {
            await using var db = await fabrica.CreateDbContextAsync();
            if (await ContaDe(db, responsavelId) is { } conta) await RemoverEventoAsync(conta, eventId);
        }, "remoção");
    }

    /* ----------------------- Google → Sistema (pull) ----------------------- */

    private sealed class PaginaEventos
    {
        public List<EventoGoogle>? Items { get; set; }
        public string? NextPageToken { get; set; }
        public string? NextSyncToken { get; set; }
    }

    public async Task<ResultadoSyncGoogle> SincronizarAsync(UsuarioAtual u)
    {
        u.Exigir(Permissoes.AgendaEditar);
        return await SincronizarAsync(u.Id);
    }

    /// <summary>Sincronização incremental por syncToken. Conflito: vence a alteração mais recente.</summary>
    public async Task<ResultadoSyncGoogle> SincronizarAsync(Guid usuarioId, int tentativa = 0, CancellationToken ct = default)
    {
        Exigir();
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var conta = await ContaDe(db, usuarioId) ?? throw new RegraNegocioException("_", "Conta Google não conectada");
        int importados = 0, atualizados = 0, cancelados = 0;
        string? pagina = null, proximoSync = null;
        do
        {
            var q = new List<string> { "showDeleted=true", "singleEvents=true", "maxResults=250" };
            q.Add(conta.SyncToken is { } st ? $"syncToken={Uri.EscapeDataString(st)}"
                : $"timeMin={Uri.EscapeDataString(DateTime.UtcNow.AddDays(-30).ToString("o"))}");
            if (pagina is not null) q.Add($"pageToken={Uri.EscapeDataString(pagina)}");
            var r = await ChamarAsync<PaginaEventos>(conta, HttpMethod.Get, "/events?" + string.Join('&', q), ct: ct);
            if (r.Status == HttpStatusCode.Gone) // syncToken expirado: recomeça do zero
            {
                conta.SyncToken = null;
                await db.SaveChangesAsync(ct);
                if (tentativa > 0) throw new ServicoIndisponivelException("syncToken do Google inválido repetidamente");
                return await SincronizarAsync(usuarioId, tentativa + 1, ct);
            }
            foreach (var ev in r.Dados?.Items ?? [])
            {
                switch (await AplicarEventoAsync(db, conta, ev, ct))
                {
                    case 'i': importados++; break;
                    case 'a': atualizados++; break;
                    case 'c': cancelados++; break;
                }
            }
            pagina = r.Dados?.NextPageToken;
            proximoSync = r.Dados?.NextSyncToken ?? proximoSync;
        } while (pagina is not null);
        conta.SyncToken = proximoSync ?? conta.SyncToken;
        conta.UltimaSyncEm = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new ResultadoSyncGoogle(importados, atualizados, cancelados);
    }

    /// <summary>Aplica um evento do Google. Devolve 'i' (importado), 'a' (atualizado), 'c' (cancelado) ou '-' (ignorado).</summary>
    private async Task<char> AplicarEventoAsync(InovabensDb db, GoogleConta conta, EventoGoogle ev, CancellationToken ct)
    {
        Guid? idLocal = ev.ExtendedProperties?.Private?.GetValueOrDefault(MapeamentoGoogle.PropriedadeId) is { } s && Guid.TryParse(s, out var g) ? g : null;
        var c = await db.Compromissos.SingleOrDefaultAsync(x => x.ResponsavelId == conta.UsuarioId
            && (idLocal != null ? x.Id == idLocal : x.GoogleEventId == ev.Id), ct);

        if (ev.Status == "cancelled")
        {
            if (c is null || c.Status == StatusCompromisso.CANCELADO) return '-';
            c.Status = StatusCompromisso.CANCELADO;
            c.SincronizadoEm = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await AlertasService.ResolverDoCompromisso(db, c.Id);
            return 'c';
        }
        if (MapeamentoGoogle.DeEvento(ev) is not { } dados) return '-';

        if (c is not null)
        {
            if (ev.Etag is not null && ev.Etag == c.GoogleEtag) return '-'; // eco do nosso próprio envio
            var googleMaisNovo = ev.Updated is not { } upd || upd.UtcDateTime > c.AtualizadoEm;
            if (!googleMaisNovo)
            {
                await EmpurrarAsync(c.Id);
                return '-';
            }
            (c.Titulo, c.Local, c.InicioEm, c.FimEm) = dados;
            c.GoogleEventId = ev.Id;
            c.GoogleEtag = ev.Etag;
            c.SincronizadoEm = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            if (dados.Fim > DateTime.UtcNow) await AlertasService.ResolverDoCompromisso(db, c.Id);
            return 'a';
        }

        if (MapeamentoGoogle.CodigoImovelNoTexto(ev) is not { } codigo) return '-'; // evento pessoal: ignorado
        var imovelId = await db.Imoveis.Where(i => i.Codigo == codigo && i.ExcluidoEm == null).Select(i => (Guid?)i.Id).SingleOrDefaultAsync(ct);
        if (imovelId is null) return '-';
        var tipo = Regex.IsMatch(dados.Titulo, "visita", RegexOptions.IgnoreCase) ? TipoCompromisso.VISITA
            : Regex.IsMatch(dados.Titulo, "reuni", RegexOptions.IgnoreCase) ? TipoCompromisso.REUNIAO : TipoCompromisso.TAREFA;
        db.Compromissos.Add(new Compromisso
        {
            ImovelId = imovelId.Value, Tipo = tipo, Titulo = dados.Titulo, Local = dados.Local, InicioEm = dados.Inicio, FimEm = dados.Fim,
            ResponsavelId = conta.UsuarioId, GoogleEventId = ev.Id, GoogleEtag = ev.Etag, SincronizadoEm = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return 'i';
    }

    /* --------------------- Webhook (push notifications) --------------------- */

    private async Task IniciarWatchAsync(Guid usuarioId)
    {
        if (op.GoogleWebhookUrl is null || !Configurado) return;
        await using var db = await fabrica.CreateDbContextAsync();
        if (await ContaDe(db, usuarioId) is not { } conta) return;
        var canal = Guid.NewGuid().ToString();
        var r = await ChamarAsync<JsonElement>(conta, HttpMethod.Post, "/events/watch",
            new { id = canal, type = "web_hook", address = op.GoogleWebhookUrl, token = Hmac(canal) });
        conta.CanalWatchId = canal;
        conta.CanalResourceId = r.Dados.ValueKind == JsonValueKind.Object && r.Dados.TryGetProperty("resourceId", out var rid) ? rid.GetString() : null;
        conta.CanalExpiraEm = r.Dados.ValueKind == JsonValueKind.Object && r.Dados.TryGetProperty("expiration", out var exp)
            && long.TryParse(exp.GetString(), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null;
        await db.SaveChangesAsync();
    }

    private async Task PararWatchAsync(GoogleConta conta)
    {
        if (conta.CanalWatchId is null) return;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Api}/channels/stop")
        {
            Content = JsonContent.Create(new { id = conta.CanalWatchId, resourceId = conta.CanalResourceId }),
        };
        req.Headers.Authorization = new("Bearer", await AccessTokenAsync(conta));
        using var _ = await Http.SendAsync(req);
    }

    /// <summary>O Google chama sem autenticação nossa: o token do canal (HMAC do id) é conferido.</summary>
    public async Task WebhookAsync(string? canalId, string? token, string? estado)
    {
        if (canalId is null || token is null || estado == "sync" || !IgualSeguro(token, Hmac(canalId))) return;
        await using var db = await fabrica.CreateDbContextAsync();
        var usuarioId = await db.GoogleContas.Where(c => c.CanalWatchId == canalId).Select(c => (Guid?)c.UsuarioId).SingleOrDefaultAsync();
        if (usuarioId is { } id) EmSegundoPlano(() => SincronizarAsync(id), "webhook");
    }

    /* ------------------------------- Jobs ------------------------------- */

    /// <summary>Rede de segurança caso o webhook falhe ou não esteja configurado (a cada 10 min).</summary>
    public async Task SincronizarTodasAsync(CancellationToken ct)
    {
        if (!Configurado) return;
        List<Guid> usuarios;
        await using (var db = await fabrica.CreateDbContextAsync(ct))
            usuarios = await db.GoogleContas.Select(c => c.UsuarioId).ToListAsync(ct);
        foreach (var u in usuarios)
        {
            try { await SincronizarAsync(u, ct: ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Sync Google do usuário {Usuario}", u); }
        }
    }

    /// <summary>Renova os canais de webhook que vencem em até 2 dias (diário, 04:00).</summary>
    public async Task RenovarCanaisAsync(CancellationToken ct)
    {
        if (!Configurado || op.GoogleWebhookUrl is null) return;
        var limite = DateTime.UtcNow.AddDays(2);
        List<Guid> usuarios;
        await using (var db = await fabrica.CreateDbContextAsync(ct))
            usuarios = await db.GoogleContas.Where(c => c.CanalExpiraEm == null || c.CanalExpiraEm < limite).Select(c => c.UsuarioId).ToListAsync(ct);
        foreach (var u in usuarios)
        {
            try { await IniciarWatchAsync(u); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Renovar canal Google do usuário {Usuario}", u); }
        }
    }
}
