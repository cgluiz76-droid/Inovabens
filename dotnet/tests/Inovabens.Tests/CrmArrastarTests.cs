using Inovabens.Core.Comum;
using Inovabens.Core.Crm;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Arrastar cartões no funil: soltar sobre outro cartão põe o lead naquela posição.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class CrmArrastarTests : IAsyncLifetime
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
    public async Task Soltar_sobre_um_cartao_poe_o_lead_antes_dele_e_empurra_os_de_baixo()
    {
        var admin = (await S<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var crm = S<CrmService>();
        var sufixo = Guid.NewGuid().ToString("N")[..6];
        var estagios = (await crm.EstagiosAsync(Funil.VENDA)).Where(e => e.Ativo && e.Tipo == TipoEstagio.ABERTO).ToList();
        var destino = estagios[1];

        var ids = new List<Guid>();
        foreach (var n in new[] { "A", "B", "C" })
        {
            var id = await crm.CriarAsync(admin, new LeadForm { NovoNome = $"Arraste {n} {sufixo}" });
            await crm.MoverAsync(admin, id, destino.Id); // vão para o fim da coluna: A, B, C
            ids.Add(id);
        }
        var novo = await crm.CriarAsync(admin, new LeadForm { NovoNome = $"Arraste D {sufixo}" });

        async Task<List<string>> Ordem() => [.. (await crm.PipelineAsync(admin, Funil.VENDA)).Single(c => c.Estagio.Id == destino.Id).Leads
            .Where(l => l.PessoaNome.EndsWith(sufixo)).Select(l => l.PessoaNome[8..9])];

        Assert.Equal(["A", "B", "C"], await Ordem());

        // D solto sobre B (vindo de outra coluna): fica entre A e B
        var b = (await crm.PipelineAsync(admin, Funil.VENDA)).SelectMany(c => c.Leads).Single(l => l.Id == ids[1]);
        await crm.MoverAsync(admin, novo, destino.Id, ordem: b.Ordem);
        Assert.Equal(["A", "D", "B", "C"], await Ordem());

        // C solto sobre A (mesma coluna): vai para o topo
        var a = (await crm.PipelineAsync(admin, Funil.VENDA)).SelectMany(c => c.Leads).Single(l => l.Id == ids[0]);
        await crm.MoverAsync(admin, ids[2], destino.Id, ordem: a.Ordem);
        Assert.Equal(["C", "A", "D", "B"], await Ordem());
    }
}
