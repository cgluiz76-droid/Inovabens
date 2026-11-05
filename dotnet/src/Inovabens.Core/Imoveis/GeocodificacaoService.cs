using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Inovabens.Core.Imoveis;

/// <summary>
/// Converte o endereço do imóvel em coordenadas. Com GOOGLE_MAPS_API_KEY usa a Geocoding API do Google; sem ela,
/// o Nominatim do OpenStreetMap (gratuito, uso moderado: uma consulta por clique do usuário).
/// </summary>
public sealed class GeocodificacaoService(InovabensOpcoes op, ILogger<GeocodificacaoService> log)
{
    private static readonly HttpClient Http = CriarCliente();

    public string Provedor => op.Integracoes.GoogleMapsApiKey is null ? "OpenStreetMap" : "Google";

    /// <summary>Coordenadas do endereço ou null se não encontrado / serviço indisponível.</summary>
    public async Task<(double Latitude, double Longitude)?> LocalizarAsync(string endereco, CancellationToken ct = default)
    {
        try
        {
            return op.Integracoes.GoogleMapsApiKey is { } chave ? await Google(endereco, chave, ct) : await Nominatim(endereco, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning(e, "Falha ao localizar endereço no {Provedor}", Provedor);
            return null;
        }
    }

    private static async Task<(double, double)?> Google(string endereco, string chave, CancellationToken ct)
    {
        var url = $"https://maps.googleapis.com/maps/api/geocode/json?region=br&language=pt-BR&address={Uri.EscapeDataString(endereco)}&key={Uri.EscapeDataString(chave)}";
        var r = await Http.GetFromJsonAsync<RespostaGoogle>(url, ct);
        return r?.Results is [var primeiro, ..] ? (primeiro.Geometry.Location.Lat, primeiro.Geometry.Location.Lng) : null;
    }

    private static async Task<(double, double)?> Nominatim(string endereco, CancellationToken ct)
    {
        var url = $"https://nominatim.openstreetmap.org/search?format=jsonv2&limit=1&countrycodes=br&q={Uri.EscapeDataString(endereco)}";
        var r = await Http.GetFromJsonAsync<List<ResultadoNominatim>>(url, ct);
        return r is [var primeiro, ..]
            ? (double.Parse(primeiro.Lat, CultureInfo.InvariantCulture), double.Parse(primeiro.Lon, CultureInfo.InvariantCulture))
            : null;
    }

    private static HttpClient CriarCliente()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // o Nominatim exige um User-Agent que identifique a aplicação
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Inovabens/1.0 (gestao imobiliaria)");
        return c;
    }

    private sealed record RespostaGoogle([property: JsonPropertyName("results")] List<ResultadoGoogle>? Results);
    private sealed record ResultadoGoogle([property: JsonPropertyName("geometry")] GeometriaGoogle Geometry);
    private sealed record GeometriaGoogle([property: JsonPropertyName("location")] PontoGoogle Location);
    private sealed record PontoGoogle([property: JsonPropertyName("lat")] double Lat, [property: JsonPropertyName("lng")] double Lng);
    private sealed record ResultadoNominatim([property: JsonPropertyName("lat")] string Lat, [property: JsonPropertyName("lon")] string Lon);
}
