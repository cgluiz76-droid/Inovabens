using Inovabens.Core;
using Inovabens.Core.Admin;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Financeiro;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Locacao;
using Inovabens.Core.Pessoas;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Leitura do .ics das plataformas (sem banco).</summary>
public class IcalTests
{
    [Fact]
    public void Le_eventos_de_dia_inteiro_e_linhas_dobradas()
    {
        var ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20270110\r\nDTEND;VALUE=DATE:20270115\r\nUID:abc-123@air\r\n bnb.com\r\n"
            + "SUMMARY:Reserved\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nDTSTART:20270201T150000Z\r\nUID:x2\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var ev = TemporadaService.LerIcal(ics);
        Assert.Equal(2, ev.Count);
        Assert.Equal("abc-123@airbnb.com", ev[0].Uid);
        Assert.Equal(new DateOnly(2027, 1, 10), ev[0].Inicio);
        Assert.Equal(new DateOnly(2027, 1, 15), ev[0].Fim);
        Assert.Equal(new DateOnly(2027, 2, 2), ev[1].Fim); // sem DTEND: um dia
    }

    [Theory]
    [InlineData("https://www.airbnb.com.br/calendar/ical/123.ics?s=abc", true)]
    [InlineData("http://www.airbnb.com/calendar.ics", false)]
    [InlineData("https://127.0.0.1/x.ics", false)]
    [InlineData("https://localhost/x.ics", false)]
    [InlineData("https://servidor.internal/x.ics", false)]
    public void So_aceita_https_publico(string url, bool ok) => Assert.Equal(ok, TemporadaService.UrlCalendarioValida(url));

    [Fact]
    public void Diaria_de_fim_de_semana_nas_noites_de_sexta_e_sabado()
    {
        var c = new TemporadaImovel { Diaria = 200, DiariaFimSemana = 300 };
        var sexta = new DateOnly(2027, 1, 8); // sexta-feira
        Assert.Equal(DayOfWeek.Friday, sexta.DayOfWeek);
        Assert.Equal(800, TemporadaService.CalcularDiarias(c, sexta, sexta.AddDays(3))); // sex 300 + sáb 300 + dom 200
    }
}

/// <summary>Locação, fase 8: temporada (reserva, recebimento, fechamento com repasse, site, iCal e boleto).</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class TemporadaTests : IAsyncLifetime
{
    private IServiceScope _escopo = default!;
    private T S<T>() where T : notnull => _escopo.ServiceProvider.GetRequiredService<T>();

    public Task InitializeAsync()
    {
        _escopo = ExtratoComprovantesTests.Compartilhado.CreateScope();
        _escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _escopo.Dispose();
        return Task.CompletedTask;
    }

    private async Task<UsuarioAtual> Admin() => (await S<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
    private static string Unico(string nome) => $"{nome} {Guid.NewGuid().ToString("N")[..6]}";

    /// <summary>Próxima sexta-feira a pelo menos 10 dias.</summary>
    private static DateOnly Sexta()
    {
        var d = DatasBR.Hoje().AddDays(10);
        while (d.DayOfWeek != DayOfWeek.Friday) d = d.AddDays(1);
        return d;
    }

    private async Task<(UsuarioAtual Admin, Guid Imovel, Guid Dono, Guid Conta)> Cenario()
    {
        var admin = await Admin();
        var dono = await S<PessoasService>().CriarAsync(admin, new PessoaForm { NomeRazao = Unico("Tania Proprietaria") });
        var fax = await S<PessoasService>().CriarAsync(admin, new PessoaForm { NomeRazao = Unico("Fatima Limpeza") });
        var imovel = await S<ImoveisService>().CriarAsync(admin, new ImovelForm { NomeDrive = Unico("Temp"), Titulo = Unico("Casa de praia"), Tipo = "casa" });
        await S<ImoveisService>().SalvarProprietariosAsync(admin, imovel, [new ProprietarioEntrada { PessoaId = dono, Percentual = 100 }]);
        await S<TemporadaService>().SalvarConfigAsync(admin, imovel, new TemporadaConfigForm
        {
            Diaria = 200, DiariaFimSemana = 300, MinimoNoites = 2, HospedesMax = 6, TaxaLimpeza = 100, Caucao = 300, ComissaoPct = 20, FaxineiraId = fax,
        });
        var nomeConta = Unico("Conta Temporada");
        var conta = await S<FinanceiroEmpresaService>().CriarContaAsync(admin, new ContaBancariaForm { Nome = nomeConta, DataSaldoInicial = new(2020, 1, 1) });
        return (admin, imovel, dono, conta);
    }

    [Fact]
    public async Task Reserva_recebe_e_fecha_com_comissao_repasse_limpeza_e_caucao()
    {
        var (admin, imovel, dono, conta) = await Cenario();
        var t = S<TemporadaService>();
        var sexta = Sexta();

        var cot = await t.CotarAsync(imovel, sexta, sexta.AddDays(3));
        Assert.True(cot.Disponivel);
        Assert.Equal(800, cot.Diarias);
        Assert.False((await t.CotarAsync(imovel, sexta, sexta.AddDays(1))).Disponivel); // mínimo 2 noites

        var id = await t.CriarAsync(admin, new ReservaForm { ImovelId = imovel, HospedeNome = "Joana Hóspede", Hospedes = 4, Entrada = sexta, Saida = sexta.AddDays(3) });
        // sobreposição barrada; saída de uma no dia da entrada da outra é permitida
        await Assert.ThrowsAsync<RegraNegocioException>(() => t.CriarAsync(admin, new ReservaForm
            { ImovelId = imovel, HospedeNome = "Outro", Entrada = sexta.AddDays(2), Saida = sexta.AddDays(5) }));
        var seguinte = await t.CriarAsync(admin, new ReservaForm { ImovelId = imovel, HospedeNome = "Seguinte", Entrada = sexta.AddDays(3), Saida = sexta.AddDays(5) });
        await t.CancelarAsync(admin, seguinte, "teste");

        var d = await t.ObterAsync(admin, id);
        Assert.Equal(1200, d.AReceber); // 800 + limpeza 100 + caução 300
        await Assert.ThrowsAsync<RegraNegocioException>(() => t.ReceberAsync(admin, id, new RecebimentoReservaForm { Data = DatasBR.Hoje(), Valor = 1300, ContaBancariaId = conta }));
        await t.ReceberAsync(admin, id, new RecebimentoReservaForm { Data = DatasBR.Hoje(), Valor = 600, ContaBancariaId = conta, Forma = "Pix" });
        var pg2 = await t.ReceberAsync(admin, id, new RecebimentoReservaForm { Data = DatasBR.Hoje(), Valor = 600, ContaBancariaId = conta });
        await Assert.ThrowsAsync<RegraNegocioException>(() => t.CancelarAsync(admin, id, null)); // tem recebimento

        await t.CheckinAsync(admin, id);
        await Assert.ThrowsAsync<RegraNegocioException>(() => t.FecharAsync(admin, id)); // antes do check-out
        await t.CheckoutAsync(admin, id, 50, "copo quebrado");

        // estorno e novo recebimento: saldo volta e precisa quitar antes de fechar
        await t.EstornarAsync(admin, pg2);
        await Assert.ThrowsAsync<RegraNegocioException>(() => t.FecharAsync(admin, id));
        await t.ReceberAsync(admin, id, new RecebimentoReservaForm { Data = DatasBR.Hoje(), Valor = 600, ContaBancariaId = conta });

        await t.FecharAsync(admin, id);
        d = await t.ObterAsync(admin, id);
        Assert.Equal(StatusReserva.FECHADA, d.Reserva.Status);
        Assert.Equal(160, d.Acerto.Comissao); // 20% de 800
        Assert.Equal(690, d.Acerto.Repasse);  // 800 − 160 + 50 retidos
        Assert.Equal(250, d.Acerto.CaucaoDevolver);

        // financeiro: entrou 1200 de terceiros; saem 160 (vira receita) + 690 + 100 + 250 = 1200
        await using var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync();
        var lancs = await db.LancamentosEmpresa.Where(l => l.ImovelId == imovel && l.ExcluidoEm == null).ToListAsync();
        Assert.Equal(1200, lancs.Where(l => l.Descricao.StartsWith("Hospedagem")).Sum(l => l.Valor));
        Assert.Equal(160, lancs.Single(l => l.Descricao.StartsWith(TemporadaService.CategoriaComissao + " —")).Valor);
        var repasse = lancs.Single(l => l.Descricao.StartsWith(TemporadaService.CategoriaRepasse));
        Assert.Equal(-690, repasse.Valor);
        Assert.Equal(dono, repasse.PessoaId);
        Assert.Null(repasse.DataPagamento);
        // dinheiro de terceiros zera (1200 − 160 − 690 − 100 − 250); fica só a receita da comissão
        Assert.Equal(160, lancs.Sum(l => l.Valor));

        // documentos
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await t.ContratoPdfAsync(admin, id)).Conteudo[..4]));
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await t.AcertoPdfAsync(admin, id)).Conteudo[..4]));

        // reabrir exclui o acerto
        await t.ReabrirAsync(admin, id);
        Assert.Equal(StatusReserva.FINALIZADA, (await t.ObterAsync(admin, id)).Reserva.Status);
        Assert.False(await db.LancamentosEmpresa.AnyAsync(l => l.ImovelId == imovel && l.ExcluidoEm == null && l.Descricao.StartsWith(TemporadaService.CategoriaRepasse)));
    }

    [Fact]
    public async Task Pedido_do_site_ical_e_boleto_da_reserva()
    {
        var (admin, imovel, _, conta) = await Cenario();
        var t = S<TemporadaService>();
        var sexta = Sexta().AddDays(14);

        // pedido do site: não bloqueia; confirmar bloqueia; robô (campo escondido) é ignorado
        var cod = await t.PedidoAsync(imovel, new PedidoReservaForm { Nome = "Paulo Site", Telefone = "(17) 99999-0001", Hospedes = 2, Entrada = sexta, Saida = sexta.AddDays(2) });
        Assert.StartsWith("RES-", cod);
        Assert.Equal("ok", await t.PedidoAsync(imovel, new PedidoReservaForm { Nome = "x", Telefone = "1", Entrada = sexta, Saida = sexta.AddDays(2), Site = "spam" }));
        await Assert.ThrowsAsync<RegraNegocioException>(() => t.PedidoAsync(imovel, new PedidoReservaForm
            { Nome = "Muitos", Telefone = "1799", Hospedes = 9, Entrada = sexta, Saida = sexta.AddDays(2) }));
        var pedido = (await t.ReservasAsync(admin, imovel)).Single(r => r.Status == StatusReserva.PEDIDO);
        Assert.True((await t.CotarAsync(imovel, sexta, sexta.AddDays(2))).Disponivel);
        await t.ConfirmarAsync(admin, pedido.Id);
        Assert.False((await t.CotarAsync(imovel, sexta, sexta.AddDays(2))).Disponivel);
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
            Assert.True(await db.Alertas.AnyAsync(a => a.TemporadaReservaId == pedido.Id));

        // iCal exportado: tem a reserva; link errado não abre
        var cfg = (await t.ImoveisAsync(admin)).Single(x => x.ImovelId == imovel);
        var token = cfg.LinkIcal.Split('/').Last().Replace(".ics", "");
        var ics = await t.IcalAsync(token);
        Assert.Contains($"DTSTART;VALUE=DATE:{sexta:yyyyMMdd}", ics);
        Assert.DoesNotContain("Paulo", ics);
        Assert.Null(await t.IcalAsync("nada"));

        // iCal importado do Airbnb: cria a reserva, muda a data e cancela quando some do calendário
        await t.AdicionarCalendarioAsync(admin, imovel, OrigemCalendario.AIRBNB, "https://www.airbnb.com.br/calendar/ical/1.ics?s=x");
        var calId = (await t.ImoveisAsync(admin)).Single(x => x.ImovelId == imovel).Calendarios.Single().Id;
        var ini = sexta.AddDays(10);
        string Ics(DateOnly a, DateOnly b) => $"BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:air-1\r\nDTSTART;VALUE=DATE:{a:yyyyMMdd}\r\nDTEND;VALUE=DATE:{b:yyyyMMdd}\r\nSUMMARY:Reserved\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        Assert.Equal(1, await t.SincronizarAsync(calId, null, default, Ics(ini, ini.AddDays(3))));
        Assert.Equal(0, await t.SincronizarAsync(calId, null, default, Ics(ini, ini.AddDays(3))));
        Assert.Equal(1, await t.SincronizarAsync(calId, null, default, Ics(ini, ini.AddDays(4))));
        var air = (await t.ReservasAsync(admin, imovel)).Single(r => r.Origem == OrigemReserva.AIRBNB);
        Assert.Equal(ini.AddDays(4), air.Saida);
        Assert.Equal(1, await t.SincronizarAsync(calId, null, default, "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n"));
        Assert.Equal(StatusReserva.CANCELADA, (await t.ObterAsync(admin, air.Id)).Reserva.Status);

        // boleto da reserva (Cora simulado): pago, o aviso registra o recebimento
        var nomeConta = await NomeConta(conta);
        var tokenWh = "tok" + Guid.NewGuid().ToString("N");
        await S<ConfiguracoesService>().SalvarAsync(admin, CatalogoConfiguracoes.Cora,
            new Dictionary<string, string> { ["cora.webhook_token"] = tokenWh, ["cora.conta"] = nomeConta });
        var form = TemporadaService.Formulario((await t.ObterAsync(admin, pedido.Id)).Reserva);
        form.HospedeDocumento = "529.982.247-25";
        await t.AtualizarAsync(admin, pedido.Id, form);
        var cob = S<CobrancaBancariaService>();
        var b = await cob.EmitirReservaAsync(admin, pedido.Id, 300);
        Assert.Equal(300, b.Valor);
        CoraSimulado.Instancia.Pagar(b.ExternoId, 300, DatasBR.Hoje());
        Assert.True(await cob.ProcessarWebhookAsync(tokenWh, b.ExternoId));
        var d = await t.ObterAsync(admin, pedido.Id);
        Assert.Equal(300, d.Recebido);
        Assert.Equal("Pix (Cora)", d.Pagamentos.Single().Forma);
        Assert.NotNull((await cob.BoletosReservaAsync(admin, pedido.Id)).Single().TemporadaPagamentoId);
    }

    private async Task<string> NomeConta(Guid id)
    {
        await using var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync();
        return await db.ContasBancarias.Where(c => c.Id == id).Select(c => c.Nome).SingleAsync();
    }
}
