using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Inovabens.Core.Comum;
using Microsoft.Extensions.Logging;

namespace Inovabens.Core.Servicos;

/// <summary>Pedido de boleto com Pix (valores em reais; multa em % e juros em % ao mês).</summary>
public sealed record BoletoPedido(string Codigo, string NomePagador, string Documento, string? Email, string Descricao, decimal Valor,
    DateOnly Vencimento, decimal MultaPct, decimal JurosMesPct, Guid Idempotencia);

/// <summary>Boleto como o banco devolve: dados para pagar e, depois de pago, valores e data.</summary>
public sealed record BoletoBanco(string Id, string Status, decimal Valor, DateOnly Vencimento, string? LinhaDigitavel, string? CodigoBarras,
    string? PixCopiaCola, string? UrlPdf, decimal TotalPago = 0, DateOnly? PagoEm = null, decimal Multa = 0, decimal Juros = 0, string? Forma = null);

/// <summary>Banco que emite boleto/Pix e avisa os pagamentos (hoje, o Cora; nos testes, um simulado).</summary>
public interface ICobrancaBancaria
{
    bool Habilitado { get; }
    Task<BoletoBanco> EmitirAsync(BoletoPedido pedido, CancellationToken ct = default);
    Task<BoletoBanco?> ConsultarAsync(string id, CancellationToken ct = default);
    Task CancelarAsync(string id, CancellationToken ct = default);
    /// <summary>Cadastra no banco o endereço que recebe os avisos de boleto (pago, cancelado...).</summary>
    Task<string> RegistrarWebhookAsync(string url, CancellationToken ct = default);
    Task<string> TestarAsync(CancellationToken ct = default);
}

public sealed class CoraRecusouException(string mensagem) : Exception(mensagem);

/// <summary>
/// Cora, Integração Direta (developers.cora.com.br): mTLS com certificado + chave privada e token client_credentials
/// (24 h). Boleto com Pix em POST /v2/invoices (valores em centavos, Idempotency-Key por boleto); o webhook só avisa,
/// então o estado vale pela consulta GET /v2/invoices/{id}.
/// </summary>
public sealed class CoraCobranca(InovabensOpcoes op, ILogger<CoraCobranca> log) : ICobrancaBancaria
{
    private readonly SemaphoreSlim _trava = new(1, 1);
    private (string Chave, HttpClient Http)? _cliente;
    private (string Chave, string Token, DateTime Expira)? _token;

    public bool Habilitado => op.Integracoes.CoraConfigurado;

    private string Base => op.Integracoes.CoraProducao ? "https://matls-clients.api.cora.com.br" : "https://matls-clients.api.stage.cora.com.br";

    /// <summary>Cliente HTTP com o certificado em vigor (recriado quando a configuração muda).</summary>
    private HttpClient Cliente()
    {
        var i = op.Integracoes;
        if (!i.CoraConfigurado) throw new ServicoIndisponivelException("Banco Cora não configurado.");
        var chave = $"{Base}|{i.CoraClientId}|{i.CoraCertificado!.GetHashCode()}|{i.CoraChavePrivada!.GetHashCode()}";
        if (_cliente is { } c && c.Chave == chave) return c.Http;
        X509Certificate2 cert;
        try
        {
            // o PFX intermediário faz a chave valer também no Windows (SslStream não aceita chave efêmera)
            using var pem = X509Certificate2.CreateFromPem(i.CoraCertificado, i.CoraChavePrivada);
            cert = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            throw new CoraRecusouException("Certificado ou chave privada do Cora inválidos: " + e.Message);
        }
        var handler = new SocketsHttpHandler { SslOptions = { ClientCertificates = [cert] }, PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
        var http = new HttpClient(handler) { BaseAddress = new Uri(Base + "/"), Timeout = TimeSpan.FromSeconds(30) };
        _cliente = (chave, http);
        _token = null;
        return http;
    }

    private async Task<HttpClient> Autenticado(CancellationToken ct)
    {
        await _trava.WaitAsync(ct);
        try
        {
            var http = Cliente();
            var chave = _cliente!.Value.Chave;
            if (_token is not { } t || t.Chave != chave || t.Expira <= DateTime.UtcNow)
            {
                using var resp = await http.PostAsync("token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials", ["client_id"] = op.Integracoes.CoraClientId!,
                }), ct);
                var corpo = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) throw new CoraRecusouException($"O Cora recusou a autenticação ({(int)resp.StatusCode}): {Curto(corpo)}");
                using var doc = JsonDocument.Parse(corpo);
                var token = doc.RootElement.GetProperty("access_token").GetString()!;
                var segundos = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
                _token = (chave, token, DateTime.UtcNow.AddSeconds(Math.Max(60, segundos - 300)));
            }
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token!.Value.Token);
            return http;
        }
        finally { _trava.Release(); }
    }

    public async Task<string> TestarAsync(CancellationToken ct = default)
    {
        await Autenticado(ct);
        return $"Autenticação no Cora ({(op.Integracoes.CoraProducao ? "produção" : "testes")}) funcionando.";
    }

    public async Task<BoletoBanco> EmitirAsync(BoletoPedido p, CancellationToken ct = default)
    {
        var http = await Autenticado(ct);
        var doc = Texto.Digitos(p.Documento);
        var corpo = new
        {
            code = p.Codigo,
            customer = new
            {
                name = p.NomePagador.Length > 255 ? p.NomePagador[..255] : p.NomePagador,
                email = p.Email,
                document = new { identity = doc, type = doc.Length == 14 ? "CNPJ" : "CPF" },
            },
            services = new[] { new { name = p.Descricao.Length > 100 ? p.Descricao[..100] : p.Descricao, amount = Centavos(p.Valor) } },
            payment_terms = new
            {
                due_date = p.Vencimento.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                fine = p.MultaPct > 0 ? new { rate = p.MultaPct } : null,
                interest = p.JurosMesPct > 0 ? new { rate = p.JurosMesPct } : null,
            },
            payment_forms = new[] { "BANK_SLIP", "PIX" },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "v2/invoices")
        {
            Content = new StringContent(JsonSerializer.Serialize(corpo, Json), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Idempotency-Key", p.Idempotencia.ToString());
        using var resp = await http.SendAsync(req, ct);
        var texto = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            log.LogWarning("Cora recusou o boleto {Codigo}: {Status} {Corpo}", p.Codigo, (int)resp.StatusCode, Curto(texto));
            throw new CoraRecusouException($"O Cora recusou o boleto ({(int)resp.StatusCode}): {Curto(texto)}");
        }
        return Ler(texto);
    }

    public async Task<BoletoBanco?> ConsultarAsync(string id, CancellationToken ct = default)
    {
        var http = await Autenticado(ct);
        using var resp = await http.GetAsync($"v2/invoices/{Uri.EscapeDataString(id)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var texto = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new CoraRecusouException($"Consulta do boleto no Cora falhou ({(int)resp.StatusCode}): {Curto(texto)}");
        return Ler(texto);
    }

    public async Task CancelarAsync(string id, CancellationToken ct = default)
    {
        var http = await Autenticado(ct);
        using var resp = await http.DeleteAsync($"v2/invoices/{Uri.EscapeDataString(id)}", ct);
        if (resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NotFound) return;
        throw new CoraRecusouException($"O Cora não cancelou o boleto ({(int)resp.StatusCode}): {Curto(await resp.Content.ReadAsStringAsync(ct))}");
    }

    public async Task<string> RegistrarWebhookAsync(string url, CancellationToken ct = default)
    {
        var http = await Autenticado(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, "endpoints/")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { url, resource = "invoice", trigger = "paid" }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var resp = await http.SendAsync(req, ct);
        var texto = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new CoraRecusouException($"O Cora recusou o webhook ({(int)resp.StatusCode}): {Curto(texto)}");
        return "Webhook registrado no Cora: os pagamentos passam a dar baixa sozinhos.";
    }

    /* ------------------------------ apoio ------------------------------ */

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    internal static int Centavos(decimal v) => (int)decimal.Round(v * 100, 0, MidpointRounding.AwayFromZero);

    private static decimal Reais(JsonElement e, string nome) =>
        e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() / 100 : 0;

    private static string? Str(JsonElement e, params string[] caminho)
    {
        foreach (var p in caminho)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    private static DateOnly? Data(string? s) =>
        s is { Length: >= 10 } && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>Lê o boleto do Cora (valores em centavos).</summary>
    internal static BoletoBanco Ler(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        decimal multa = 0, juros = 0;
        string? forma = null;
        if (r.TryGetProperty("payments", out var pags) && pags.ValueKind == JsonValueKind.Array)
            foreach (var p in pags.EnumerateArray())
            {
                multa += Reais(p, "fine");
                juros += Reais(p, "interest");
                forma ??= Str(p, "method");
            }
        return new BoletoBanco(
            Str(r, "id") ?? throw new CoraRecusouException("Resposta do Cora sem o id do boleto."),
            Str(r, "status") ?? "OPEN",
            Reais(r, "total_amount"),
            Data(Str(r, "payment_terms", "due_date")) ?? DatasBR.Hoje(),
            Str(r, "payment_options", "bank_slip", "digitable"),
            Str(r, "payment_options", "bank_slip", "barcode"),
            Str(r, "pix", "emv"),
            Str(r, "payment_options", "bank_slip", "url"),
            Reais(r, "total_paid"),
            Data(Str(r, "occurrence_date")),
            multa, juros, forma);
    }

    private static string Curto(string s) => s.Length > 300 ? s[..300] : s;
}
