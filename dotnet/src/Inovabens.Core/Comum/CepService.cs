using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Inovabens.Core.Comum;

public sealed record EnderecoCep(string Cep, string? Logradouro, string? Bairro, string? Cidade, string? Uf);

public enum ResultadoCep { Encontrado, NaoEncontrado, Indisponivel }

/// <summary>
/// Endereço pelo CEP: ViaCEP (gratuito, sem chave); se ele estiver fora do ar, BrasilAPI.
/// Chamado do servidor (Blazor Server), uma consulta quando o CEP fica completo.
/// </summary>
public sealed class CepService(ILogger<CepService> log)
{
    private static readonly HttpClient Padrao = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>Trocado nos testes por um cliente com respostas falsas.</summary>
    internal HttpClient Http { get; init; } = Padrao;

    /// <summary>8 dígitos do CEP ou null se incompleto.</summary>
    public static string? Digitos(string? cep) =>
        new string([.. (cep ?? "").Where(char.IsAsciiDigit)]) is { Length: 8 } d ? d : null;

    public static string Formatado(string digitos) => $"{digitos[..5]}-{digitos[5..]}";

    public async Task<(ResultadoCep Resultado, EnderecoCep? Endereco)> ConsultarAsync(string? cep, CancellationToken ct = default)
    {
        if (Digitos(cep) is not { } d) return (ResultadoCep.NaoEncontrado, null);
        try
        {
            return await ViaCep(d, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning(e, "ViaCEP indisponível; tentando BrasilAPI");
        }
        try
        {
            return await BrasilApi(d, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning(e, "Falha ao consultar CEP {Cep}", d);
            return (ResultadoCep.Indisponivel, null);
        }
    }

    private async Task<(ResultadoCep, EnderecoCep?)> ViaCep(string d, CancellationToken ct)
    {
        using var r = await Http.GetAsync($"https://viacep.com.br/ws/{d}/json/", ct);
        if (r.StatusCode == HttpStatusCode.BadRequest) return (ResultadoCep.NaoEncontrado, null);
        r.EnsureSuccessStatusCode();
        var v = await r.Content.ReadFromJsonAsync<RespostaViaCep>(ct);
        // CEP inexistente: 200 com {"erro": "true"} (ou true, conforme a versão da API)
        if (v is null || v.Erro?.ToString() is "true" or "True") return (ResultadoCep.NaoEncontrado, null);
        return (ResultadoCep.Encontrado, new(Formatado(d), Texto.Limpo(v.Logradouro), Texto.Limpo(v.Bairro), Texto.Limpo(v.Localidade), Texto.Limpo(v.Uf)));
    }

    private async Task<(ResultadoCep, EnderecoCep?)> BrasilApi(string d, CancellationToken ct)
    {
        using var r = await Http.GetAsync($"https://brasilapi.com.br/api/cep/v1/{d}", ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return (ResultadoCep.NaoEncontrado, null);
        r.EnsureSuccessStatusCode();
        var b = await r.Content.ReadFromJsonAsync<RespostaBrasilApi>(ct);
        return b is null
            ? (ResultadoCep.NaoEncontrado, null)
            : (ResultadoCep.Encontrado, new(Formatado(d), Texto.Limpo(b.Street), Texto.Limpo(b.Neighborhood), Texto.Limpo(b.City), Texto.Limpo(b.State)));
    }

    private sealed record RespostaViaCep(
        [property: JsonPropertyName("logradouro")] string? Logradouro,
        [property: JsonPropertyName("bairro")] string? Bairro,
        [property: JsonPropertyName("localidade")] string? Localidade,
        [property: JsonPropertyName("uf")] string? Uf,
        [property: JsonPropertyName("erro")] object? Erro);

    private sealed record RespostaBrasilApi(
        [property: JsonPropertyName("street")] string? Street,
        [property: JsonPropertyName("neighborhood")] string? Neighborhood,
        [property: JsonPropertyName("city")] string? City,
        [property: JsonPropertyName("state")] string? State);
}
