using Inovabens.Core.Comum;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Pessoas;
using Inovabens.Core.Servicos;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Regra única das buscas: sem diferenciar maiúsculas, acentos nem "ç", nos dois sentidos.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class BuscaSemAcentoTests : IAsyncLifetime
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
    public void Regra_em_memoria()
    {
        Assert.Equal("acucar sao joao", Texto.SemAcento(" AÇÚCAR São João "));
        Assert.Equal(@"%100\%%", Texto.LikeBusca("100%")); // curinga digitado continua literal
        Assert.True(Texto.Contem("Jardim Conceição", "conceicao"));
        Assert.True(Texto.Contem("Jardim Conceicao", "CONCEIÇÃO"));
        Assert.False(Texto.Contem("Jardim Conceição", "concessao"));
        Assert.True(Texto.Igual("São José", "sao jose"));
    }

    [Fact]
    public async Task Imoveis_e_pessoas_sem_acento_nos_dois_sentidos()
    {
        var admin = (await S<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var s = Guid.NewGuid().ToString("N")[..6];
        var imoveis = S<ImoveisService>();
        var comAcento = await imoveis.CriarAsync(admin, new ImovelForm
        {
            NomeDrive = "Acento " + s, Titulo = $"Chácara São Conceição Wq{s}", Tipo = "casa", Bairro = "Jardim Açaí",
        });
        var semAcento = await imoveis.CriarAsync(admin, new ImovelForm
        {
            NomeDrive = "Sem acento " + s, Titulo = $"Sitio Acucar Wq{s}", Tipo = "casa",
        });

        async Task<List<Guid>> Imoveis(string termo) =>
            [.. (await imoveis.ListarAsync(admin, new FiltroImoveis { Termo = termo, TamanhoPagina = 50 })).Itens.Select(i => i.Id)];

        Assert.Equal([comAcento], await Imoveis($"chacara sao conceicao wq{s}"));   // digitado sem acento
        Assert.Equal([semAcento], await Imoveis($"SÍTIO AÇÚCAR WQ{s}"));             // digitado com acento
        Assert.Contains(comAcento, await Imoveis("jardim acai"));                    // bairro

        var pessoas = S<PessoasService>();
        var pessoa = await pessoas.CriarAsync(admin, new PessoaForm { NomeRazao = $"João Gonçalves Wq{s}" });
        var achadas = await pessoas.ListarAsync(admin, new FiltroPessoas($"joao goncalves wq{s}"));
        Assert.Equal(pessoa, Assert.Single(achadas.Itens).Id);
    }
}
