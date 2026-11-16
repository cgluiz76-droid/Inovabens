using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Inovabens.Core.Comum;
using Microsoft.Extensions.Logging;

namespace Inovabens.Core.Imoveis;

/// <summary>Quão preciso é o ponto: no imóvel (número da rua), no meio da rua, ou só a região (bairro/cidade).</summary>
public enum PrecisaoLocal { Exata, Rua, Aproximada }

public sealed record Localizacao(double Latitude, double Longitude, PrecisaoLocal Precisao);

/// <summary>
/// Converte o endereço do imóvel em coordenadas pela Geocoding API do Google (chave em Administração → Configurações →
/// Google Maps, ou GOOGLE_MAPS_API_KEY). Sem a chave, não localiza (o sistema usa só o Google Maps).
/// </summary>
public sealed class GeocodificacaoService(InovabensOpcoes op, ILogger<GeocodificacaoService> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public bool Configurado => op.Integracoes.GoogleMapsApiKey is not null;

    /// <summary>Coordenadas do endereço ou null se não encontrado / sem chave / serviço indisponível.</summary>
    public async Task<(double Latitude, double Longitude)?> LocalizarAsync(string endereco, CancellationToken ct = default)
    {
        try { return await LocalizarComPrecisaoAsync(endereco, ct) is { } l ? (l.Latitude, l.Longitude) : null; }
        catch (ServicoIndisponivelException) { return null; }
    }

    /// <summary>
    /// Como <see cref="LocalizarAsync"/>, dizendo também a precisão do ponto. Null = endereço não encontrado;
    /// sem chave ou chave recusada pelo Google (ex.: sem faturamento) lança <see cref="ServicoIndisponivelException"/>.
    /// </summary>
    public async Task<Localizacao?> LocalizarComPrecisaoAsync(string endereco, CancellationToken ct = default)
    {
        if (op.Integracoes.GoogleMapsApiKey is not { } chave)
            throw new ServicoIndisponivelException("Falta a chave do Google Maps (Administração → Configurações → Google Maps).");
        try
        {
            var url = $"https://maps.googleapis.com/maps/api/geocode/json?region=br&language=pt-BR&address={Uri.EscapeDataString(endereco)}&key={Uri.EscapeDataString(chave)}";
            var r = await Http.GetFromJsonAsync<RespostaGoogle>(url, ct);
            if (r?.Status is not ("OK" or "ZERO_RESULTS" or null))
            {
                log.LogWarning("Google Geocoding recusou a consulta: {Status} {Mensagem}", r.Status, r.Erro);
                throw new ServicoIndisponivelException(MensagemRecusa(r.Status, r.Erro));
            }
            return r?.Results is [var p, ..]
                ? new(p.Geometry.Location.Lat, p.Geometry.Location.Lng, PrecisaoGoogle(p.Geometry.Tipo))
                : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning(e, "Falha ao localizar endereço no Google Maps");
            throw new ServicoIndisponivelException("O Google Maps não respondeu. Tente de novo em instantes.");
        }
    }

    /// <summary>Recusa do Google em português, com o que fazer.</summary>
    public static string MensagemRecusa(string? status, string? erro) => status switch
    {
        "REQUEST_DENIED" when erro?.Contains("Billing", StringComparison.OrdinalIgnoreCase) == true =>
            "O Google Maps recusou a consulta: falta ativar o faturamento (cartão) no projeto do Google Cloud.",
        "REQUEST_DENIED" => "O Google Maps recusou a chave. Confira a chave e se a Geocoding API está ativada no Google Cloud.",
        "OVER_QUERY_LIMIT" or "OVER_DAILY_LIMIT" => "O Google Maps atingiu o limite de consultas. Tente mais tarde.",
        _ => $"O Google Maps recusou a consulta ({status}).",
    };

    /// <summary>ROOFTOP/RANGE_INTERPOLATED = no número; GEOMETRIC_CENTER = meio da rua; APPROXIMATE = bairro/cidade.</summary>
    public static PrecisaoLocal PrecisaoGoogle(string? tipo) => tipo switch
    {
        "ROOFTOP" or "RANGE_INTERPOLATED" => PrecisaoLocal.Exata,
        "GEOMETRIC_CENTER" => PrecisaoLocal.Rua,
        _ => PrecisaoLocal.Aproximada,
    };

    private sealed record RespostaGoogle([property: JsonPropertyName("results")] List<ResultadoGoogle>? Results,
        [property: JsonPropertyName("status")] string? Status = null, [property: JsonPropertyName("error_message")] string? Erro = null);
    private sealed record ResultadoGoogle([property: JsonPropertyName("geometry")] GeometriaGoogle Geometry);
    private sealed record GeometriaGoogle([property: JsonPropertyName("location")] PontoGoogle Location,
        [property: JsonPropertyName("location_type")] string? Tipo = null);
    private sealed record PontoGoogle([property: JsonPropertyName("lat")] double Lat, [property: JsonPropertyName("lng")] double Lng);
}
