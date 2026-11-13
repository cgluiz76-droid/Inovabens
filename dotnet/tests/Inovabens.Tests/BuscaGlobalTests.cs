using Inovabens.Core.Busca;
using Inovabens.Core.Comum;
using Inovabens.Core.Crm;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Pessoas;
using Inovabens.Core.Servicos;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Busca global (Ctrl+K): sem acento, por dígitos do telefone e respeitando o escopo do Corretor.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class BuscaGlobalTests : IAsyncLifetime
{
    private IServiceScope _escopo = default!;
    private T S<T>() where T : notnull => _escopo.ServiceProvider.GetRequiredService<T>();

    public Task InitializeAsync()
    {
        _escopo = ExtratoComprovantesTests.Compartilhado.CreateScope(); // provider compartilhado (limite de 20 do EF)
        _escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _escopo.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void Normalizar_tira_acentos_e_maiusculas()
    {
        Assert.Equal("conceicao sao jose", BuscaGlobalService.Normalizar("  Conceição SÃO José "));
        Assert.Equal("", BuscaGlobalService.Normalizar(null));
    }

    [Fact]
    public async Task Encontra_sem_acento_pelo_telefone_e_respeita_o_escopo()
    {
        var auth = S<AuthService>();
        var admin = (await auth.LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var corretora = (await auth.LoginAsync("carla@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var busca = S<BuscaGlobalService>();
        var s = Guid.NewGuid().ToString("N")[..6];
        var fone = $"9{Random.Shared.Next(1000_0000, 9999_9999)}";

        var imovel = await S<ImoveisService>().CriarAsync(admin, new ImovelForm
        {
            NomeDrive = "Busca " + s, Titulo = $"Casa Jardim Tarrafé Qz{s}", Tipo = "casa",
        });
        var pessoa = await S<PessoasService>().CriarAsync(admin, new PessoaForm { NomeRazao = $"Conceição Qz{s}", Telefone = $"(17) {fone}" });
        var lead = await S<CrmService>().CriarAsync(admin, new LeadForm { NovoNome = $"Lead Ângelo Qz{s}" });

        // sem acento e sem maiúscula
        var r = await busca.BuscarAsync(admin, $"TARRAFE QZ{s}");
        var achado = Assert.Single(r.Imoveis);
        Assert.Equal(imovel, achado.Id);
        Assert.Equal($"imoveis/{imovel}", achado.Url);

        r = await busca.BuscarAsync(admin, $"conceicao qz{s}");
        Assert.Equal(pessoa, Assert.Single(r.Pessoas).Id);

        // telefone pelos dígitos, digitado com outra formatação
        r = await busca.BuscarAsync(admin, $"{fone[..5]}-{fone[5..]}");
        Assert.Contains(r.Pessoas, p => p.Id == pessoa);

        r = await busca.BuscarAsync(admin, $"angelo qz{s}");
        var l = Assert.Single(r.Leads);
        Assert.Equal(lead, l.Id);
        Assert.Equal($"crm?lead={lead}", l.Url);

        // a Corretora não vê o imóvel nem o lead de outro responsável
        Assert.Empty((await busca.BuscarAsync(corretora, $"tarrafe qz{s}")).Imoveis);
        Assert.Empty((await busca.BuscarAsync(corretora, $"angelo qz{s}")).Leads);

        // termo curto não consulta
        Assert.Empty((await busca.BuscarAsync(admin, "a")).Todos);
    }
}
