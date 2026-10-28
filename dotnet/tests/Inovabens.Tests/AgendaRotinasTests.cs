using Inovabens.Core.Agenda;
using Inovabens.Core.Dominio;

namespace Inovabens.Tests;

/// <summary>Mapeamento de eventos do Google e horários das rotinas agendadas (sem banco).</summary>
public class AgendaRotinasTests
{
    [Fact]
    public void Google_mapeamento_de_eventos()
    {
        var ev = new EventoGoogle { Summary = "Visita #imv-000123", Description = "cliente", Start = new() { Date = "2026-11-10" }, End = new() { Date = "2026-11-11" } };
        Assert.Equal("IMV-000123", MapeamentoGoogle.CodigoImovelNoTexto(ev));
        Assert.Null(MapeamentoGoogle.CodigoImovelNoTexto(new EventoGoogle { Summary = "Reunião #IMV-12" }));
        var (titulo, _, inicio, fim) = MapeamentoGoogle.DeEvento(ev)!.Value;
        Assert.Equal("Visita #imv-000123", titulo);
        Assert.Equal(new DateTime(2026, 11, 10, 3, 0, 0, DateTimeKind.Utc), inicio); // dia inteiro em Brasília
        Assert.Equal(new DateTime(2026, 11, 11, 2, 59, 59, DateTimeKind.Utc), fim);
        Assert.Null(MapeamentoGoogle.DeEvento(new EventoGoogle { Summary = "sem datas" }));

        var c = new Compromisso { Id = Guid.NewGuid(), Titulo = "Visita", InicioEm = new DateTime(2026, 11, 10, 13, 0, 0), FimEm = new DateTime(2026, 11, 10, 14, 0, 0), Status = StatusCompromisso.CONCLUIDO };
        var json = System.Text.Json.JsonSerializer.Serialize(MapeamentoGoogle.ParaEvento(c, "IMV-000001", ["a@x.com"]));
        Assert.Contains("\"dateTime\":\"2026-11-10T13:00:00.0000000Z\"", json);
        Assert.Contains("#IMV-000001", json);
        Assert.Contains(c.Id.ToString(), json);
        Assert.Contains("\"colorId\":\"2\"", json);
    }

    [Theory]
    [InlineData("2026-10-28 14:03", 5, "2026-10-28 14:05")]
    [InlineData("2026-10-28 14:05", 5, "2026-10-28 14:10")]
    [InlineData("2026-10-28 23:58", 10, "2026-10-29 00:00")]
    public void Rotinas_a_cada_N_minutos(string agora, int minutos, string esperado) =>
        Assert.Equal(DateTime.Parse(esperado), Web.Jobs.Rotina.ACada(DateTime.Parse(agora), minutos));

    [Theory]
    [InlineData("2026-10-28 05:59", 6, "2026-10-28 06:00")]
    [InlineData("2026-10-28 06:00", 6, "2026-10-29 06:00")]
    [InlineData("2026-12-31 22:00", 4, "2027-01-01 04:00")]
    public void Rotinas_diarias(string agora, int hora, string esperado) =>
        Assert.Equal(DateTime.Parse(esperado), Web.Jobs.Rotina.Diaria(DateTime.Parse(agora), hora));
}
