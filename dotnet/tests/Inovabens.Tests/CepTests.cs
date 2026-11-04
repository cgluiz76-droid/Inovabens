using System.Net;
using Inovabens.Core.Comum;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inovabens.Tests;

/// <summary>Responde pela URL: ViaCEP e BrasilAPI com corpo e status escolhidos; null = falha de rede.</summary>
internal sealed class CepFalso(Func<string, (HttpStatusCode Status, string Corpo)?> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
        responder(req.RequestUri!.ToString()) is { } r
            ? Task.FromResult(new HttpResponseMessage(r.Status) { Content = new StringContent(r.Corpo) })
            : throw new HttpRequestException("fora do ar");
}

public class CepTests
{
    private static CepService Servico(Func<string, (HttpStatusCode, string)?> responder) =>
        new(NullLogger<CepService>.Instance) { Http = new HttpClient(new CepFalso(responder)) };

    private const string ViaCepSe = """{"cep":"01001-000","logradouro":"Praça da Sé","complemento":"lado ímpar","bairro":"Sé","localidade":"São Paulo","uf":"SP"}""";

    [Fact]
    public void Digitos_so_com_8()
    {
        Assert.Equal("01001000", CepService.Digitos("01001-000"));
        Assert.Null(CepService.Digitos("01001-00"));
        Assert.Null(CepService.Digitos(null));
    }

    [Fact]
    public async Task Encontra_pelo_ViaCep()
    {
        var (r, e) = await Servico(u => u.Contains("viacep") ? (HttpStatusCode.OK, ViaCepSe) : null).ConsultarAsync("01001000");
        Assert.Equal(ResultadoCep.Encontrado, r);
        Assert.Equal(new EnderecoCep("01001-000", "Praça da Sé", "Sé", "São Paulo", "SP"), e);
    }

    [Fact]
    public async Task Cep_inexistente_no_ViaCep_nao_consulta_outro()
    {
        var (r, e) = await Servico(u => u.Contains("viacep") ? (HttpStatusCode.OK, """{"erro": "true"}""") : (HttpStatusCode.OK, "{}"))
            .ConsultarAsync("99999-999");
        Assert.Equal(ResultadoCep.NaoEncontrado, r);
        Assert.Null(e);
    }

    [Fact]
    public async Task ViaCep_fora_do_ar_usa_BrasilApi()
    {
        var brasil = """{"cep":"01001000","state":"SP","city":"São Paulo","neighborhood":"Sé","street":"Praça da Sé"}""";
        var (r, e) = await Servico(u => u.Contains("brasilapi") ? (HttpStatusCode.OK, brasil) : null).ConsultarAsync("01001000");
        Assert.Equal(ResultadoCep.Encontrado, r);
        Assert.Equal("Praça da Sé", e!.Logradouro);

        var (r404, _) = await Servico(u => u.Contains("brasilapi") ? (HttpStatusCode.NotFound, "{}") : null).ConsultarAsync("01001000");
        Assert.Equal(ResultadoCep.NaoEncontrado, r404);
    }

    [Fact]
    public async Task Tudo_fora_do_ar_indisponivel()
    {
        var (r, _) = await Servico(_ => null).ConsultarAsync("01001000");
        Assert.Equal(ResultadoCep.Indisponivel, r);
    }
}
