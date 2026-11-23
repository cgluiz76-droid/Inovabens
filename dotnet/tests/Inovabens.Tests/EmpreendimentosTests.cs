using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

public class EmpreendimentosUnidadeTests
{
    [Theory]
    [InlineData(TipoUnidade.Lote, 7, "TE0043-07")]
    [InlineData(TipoUnidade.Lote, 120, "TE0043-120")]
    [InlineData(TipoUnidade.Remanescente, 2, "TE0043-R2")]
    public void Codigo_da_unidade(string tipo, int n, string esperado) => Assert.Equal(esperado, EmpreendimentosService.CodigoUnidade("TE0043", tipo, n));

    [Theory]
    [InlineData("TE0043-07", "TE0043-07 - Lote 7", "TE0043-07 - Lote 7")]
    [InlineData("TE0043-R1", "TE0043-R1 - Área remanescente", "TE0043-R1 - Área remanescente")]
    [InlineData("CA0060", "CA0060 - 12 casas", "CA0060 - 12 casas")]
    public void Nome_da_pasta_mantem_o_codigo_do_lote(string codigo, string nome, string esperado) =>
        Assert.Equal(esperado, RegrasImovel.NomeDriveComCodigo(codigo, nome));
}

/// <summary>Empreendimento: área desmembrada em lotes, remanescente desmembrada de novo, venda só com matrícula própria.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class EmpreendimentosTests : IAsyncLifetime
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

    private async Task<UsuarioAtual> Entrar(string email) => (await S<AuthService>().LoginAsync(email, BancoFixture.Senha, null)).Usuario!;

    [Fact]
    public async Task Desmembra_em_duas_fases_e_so_vende_com_matricula()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var imoveis = S<ImoveisService>();
        var emp = S<EmpreendimentosService>();
        var marca = Guid.NewGuid().ToString("N")[..8];
        var raizId = await imoveis.CriarAsync(admin, new ImovelForm
        {
            NomeDrive = "Loteamento " + marca, Titulo = "Lotes no centro " + marca, Tipo = "casa", Cidade = "Inocência", Uf = "MS",
            Matricula = "1.000", Cartorio = "RI Inocência", AreaTerrenoM2 = 1000,
        });
        var raiz = (await imoveis.DetalheAsync(admin, raizId)).Dados;

        // fase 1: 3 lotes de 200 m² + remanescente de 350 m² + 50 m² de rua doada = 1.000 m²
        await emp.DesmembrarAsync(admin, raizId, new DesmembramentoForm
        {
            Quantidade = 3, AreaLoteM2 = 200, PrecoLote = 100_000, Remanescente = true, AreaRemanescenteM2 = 350,
            Fase = new FaseForm { AreaDoadaM2 = 50 },
        });
        var d = (await emp.DetalheAsync(admin, raizId))!;
        Assert.Equal([$"{raiz.Codigo}-01", $"{raiz.Codigo}-02", $"{raiz.Codigo}-03", $"{raiz.Codigo}-R1"], d.Unidades.Select(x => x.Codigo));
        Assert.All(d.Unidades, x => Assert.Equal(SituacaoImovel.EM_REGULARIZACAO, x.Situacao));
        Assert.All(d.Unidades, x => Assert.True(x.Prevista));
        Assert.Equal(0, Assert.Single(d.Fases).Diferenca);
        var r1 = d.Unidades.Single(x => x.Remanescente);
        Assert.Equal([r1.Id], d.Desmembraveis.Select(x => x.Id)); // a matrícula de origem já foi dividida

        // o lote herda o cadastro do empreendimento e aponta para a matrícula de origem
        var lote1 = (await imoveis.DetalheAsync(admin, d.Unidades[0].Id)).Dados;
        Assert.Equal((raizId, raizId, "Inocência", "RI Inocência", false), (lote1.EmpreendimentoId!.Value, lote1.OrigemId!.Value, lote1.Cidade, lote1.Cartorio, lote1.DivulgarSite));
        Assert.Equal($"{raiz.Codigo}-01 - Lote 1", lote1.NomeDrive);

        // a matrícula de origem não é desmembrada duas vezes; lote não é desmembrado
        await Assert.ThrowsAsync<RegraNegocioException>(() => emp.DesmembrarAsync(admin, raizId, new DesmembramentoForm()));
        await Assert.ThrowsAsync<RegraNegocioException>(() => emp.DesmembrarAsync(admin, lote1.Id, new DesmembramentoForm()));

        // sem matrícula própria não vende (tabela de lotes e ficha); com matrícula, vende
        var e1 = UnidadeEdicao.De(d.Unidades[0]);
        e1.Situacao = SituacaoImovel.VENDIDO;
        await Assert.ThrowsAsync<RegraNegocioException>(() => emp.SalvarUnidadesAsync(admin, raizId, [e1]));
        var ficha = ImovelForm.De(lote1);
        ficha.Situacao = SituacaoImovel.VENDIDO;
        await Assert.ThrowsAsync<RegraNegocioException>(() => imoveis.AtualizarAsync(admin, lote1.Id, ficha));
        e1.Matricula = "1.001";
        e1.Preco = 120_000;
        await emp.SalvarUnidadesAsync(admin, raizId, [e1]);

        // registrar a fase 1 libera os lotes
        var fase1 = (await emp.DetalheAsync(admin, raizId))!.Fases[0].Dados;
        await emp.AtualizarFaseAsync(admin, fase1.Id, new FaseForm { Situacao = SituacaoDesmembramento.REGISTRADO, AreaDoadaM2 = 50 });
        d = (await emp.DetalheAsync(admin, raizId))!;
        Assert.Equal([SituacaoImovel.VENDIDO, SituacaoImovel.DISPONIVEL, SituacaoImovel.DISPONIVEL, SituacaoImovel.DISPONIVEL],
            d.Unidades.Select(x => x.Situacao));
        Assert.Equal(100_000, d.APartirDe);

        // fase 2: a remanescente vira mais 2 lotes e outra remanescente; a numeração continua
        await emp.DesmembrarAsync(admin, r1.Id, new DesmembramentoForm
        {
            Quantidade = 2, AreaLoteM2 = 150, Remanescente = true, AreaRemanescenteM2 = 50,
        });
        d = (await emp.DetalheAsync(admin, raizId))!;
        Assert.Equal([$"{raiz.Codigo}-04", $"{raiz.Codigo}-05"], d.Unidades.Where(x => x.Fase == 2 && !x.Remanescente).Select(x => x.Codigo));
        Assert.All(d.Unidades.Where(x => x.Fase == 2), x => Assert.Equal(r1.Codigo, x.OrigemCodigo));
        Assert.Contains(d.Unidades, x => x.Codigo == $"{raiz.Codigo}-R2");
        Assert.Equal(0, d.Fases[1].Diferenca);
        Assert.Equal([d.Unidades.Single(x => x.Codigo == $"{raiz.Codigo}-R2").Id], d.Desmembraveis.Select(x => x.Id));

        // registrada a fase 2, a R1 é encerrada (arquivada) e sai da edição
        await emp.AtualizarFaseAsync(admin, d.Fases[1].Dados.Id, new FaseForm { Situacao = SituacaoDesmembramento.REGISTRADO });
        d = (await emp.DetalheAsync(admin, raizId))!;
        Assert.True(d.Unidades.Single(x => x.Id == r1.Id).Encerrada);
        Assert.Equal(SituacaoImovel.ARQUIVADO, d.Unidades.Single(x => x.Id == r1.Id).Situacao);

        // lista: os lotes ficam no empreendimento; a busca pelo código ainda encontra
        var lista = await imoveis.ListarAsync(admin, new FiltroImoveis { Cidade = "Inocência", TamanhoPagina = 100 });
        Assert.DoesNotContain(lista.Itens, i => i.Codigo.StartsWith(raiz.Codigo + "-"));
        Assert.Equal("5 lotes · 4 disponíveis", lista.Itens.Single(i => i.Id == raizId).Unidades);
        var busca = await imoveis.ListarAsync(admin, new FiltroImoveis { Termo = raiz.Codigo + "-04" });
        Assert.Single(busca.Itens);

        // vínculo do lote (cabeçalho da ficha) e a aba Lotes não existe para ele
        var v = (await emp.VinculoAsync(admin, d.Unidades.Single(x => x.Codigo == $"{raiz.Codigo}-04").Id))!;
        Assert.Equal((raiz.Codigo, r1.Codigo, 2), (v.EmpreendimentoCodigo, v.OrigemCodigo, v.Fase));
        Assert.Null(await emp.DetalheAsync(admin, v.OrigemId));
        Assert.Null(await emp.VinculoAsync(admin, raizId));
    }

    [Fact]
    public async Task Corretor_sem_acesso_nao_desmembra()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var carla = await Entrar("carla@inovabens.com.br"); // corretora com escopo próprio, não é responsável
        var id = await S<ImoveisService>().CriarAsync(admin, new ImovelForm
        {
            NomeDrive = "Area " + Guid.NewGuid().ToString("N")[..8], Titulo = "Área", Tipo = "casa",
        });
        await Assert.ThrowsAnyAsync<Exception>(() => S<EmpreendimentosService>().DesmembrarAsync(carla, id, new DesmembramentoForm { Quantidade = 2 }));
        await using var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync();
        Assert.False(await db.Imoveis.AnyAsync(i => i.EmpreendimentoId == id));
    }
}
