using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Quadro público de lotes e preço "a partir de" do empreendimento (site, página do imóvel e vitrine).</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class QuadroLotesTests : IAsyncLifetime
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

    [Fact]
    public async Task Quadro_mostra_preco_so_do_lote_a_venda_e_a_partir_de_e_o_menor_disponivel()
    {
        var admin = (await S<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var emp = S<EmpreendimentosService>();
        var marca = Guid.NewGuid().ToString("N")[..8];
        var raizId = await S<ImoveisService>().CriarAsync(admin, new ImovelForm
        {
            NomeDrive = "Loteamento " + marca, Titulo = "Loteamento " + marca, Tipo = "terreno", Cidade = "Inocência", Uf = "MS",
            Matricula = "7.000", AreaTerrenoM2 = 2000, DivulgarSite = true, Situacao = SituacaoImovel.DISPONIVEL,
        });
        await emp.DesmembrarAsync(admin, raizId, new DesmembramentoForm
        {
            Quantidade = 4, AreaLoteM2 = 300, PrecoLote = 100_000, Remanescente = true, AreaRemanescenteM2 = 800,
            Fase = new FaseForm { Situacao = SituacaoDesmembramento.REGISTRADO, Protocolo = "P-1", DataRegistro = new(2025, 1, 10) },
        });
        var d = (await emp.DetalheAsync(admin, raizId))!;
        var lotes = d.Lotes.Select(UnidadeEdicao.De).ToList();
        // 1º disponível a 120 mil; 2º vendido; 3º sem matrícula (em breve); 4º disponível a 150 mil
        (lotes[0].Matricula, lotes[0].Preco) = ("7.001", 120_000);
        (lotes[1].Matricula, lotes[1].Preco, lotes[1].Situacao) = ("7.002", 90_000, SituacaoImovel.VENDIDO);
        (lotes[3].Matricula, lotes[3].Preco) = ("7.004", 150_000);
        await emp.SalvarUnidadesAsync(admin, raizId, lotes);

        var q = (await emp.QuadroAsync(admin, raizId))!;
        Assert.Equal(4, q.Lotes.Count); // a área remanescente não entra
        Assert.Equal((2, 1, 1, 120_000m), (q.Disponiveis, q.Vendidos, q.EmBreve, q.APartirDe!.Value));
        Assert.Equal((SituacaoLoteSite.Vendido, (decimal?)null), (q.Lotes[1].Situacao, q.Lotes[1].Preco));
        Assert.Equal((SituacaoLoteSite.EmBreve, (decimal?)null), (q.Lotes[2].Situacao, q.Lotes[2].Preco));
        Assert.Null(await emp.QuadroAsync(admin, lotes[0].Id)); // lote não tem quadro

        // vitrine: o cartão do empreendimento mostra "a partir de"
        var vitrine = await S<ImoveisService>().VitrineAsync(admin, new FiltroVitrine(FinalidadeImovel.VENDA, Maximo: 200));
        var cartao = vitrine.Single(v => v.Id == raizId);
        Assert.Equal((120_000m, true), (cartao.PrecoVenda!.Value, cartao.APartirDe));

        await using var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync();
        Assert.Empty(await QuadroEmpreendimento.APartirDeAsync(db, [lotes[0].Id]));
    }
}
