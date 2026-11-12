using System.Text.Json;
using Inovabens.Core;
using Inovabens.Core.Admin;
using Inovabens.Core.Agenda;
using Inovabens.Core.Comum;
using Inovabens.Core.Crm;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Fila aberta (lead sem responsável visível a todos os corretores), "Pegar atendimento" e rodízio.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class CrmFilaTests : IAsyncLifetime
{
    private IServiceScope _escopo = default!;
    private Integracoes _anteriores = Integracoes.Vazias;
    private T S<T>() where T : notnull => _escopo.ServiceProvider.GetRequiredService<T>();

    public Task InitializeAsync()
    {
        _escopo = ExtratoComprovantesTests.Compartilhado.CreateScope(); // provider compartilhado (limite de 20 do EF)
        _escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        _anteriores = S<InovabensOpcoes>().Integracoes;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        S<InovabensOpcoes>().Integracoes = _anteriores;
        _escopo.Dispose();
        return Task.CompletedTask;
    }

    private async Task<UsuarioAtual> Entrar(string email, string senha = BancoFixture.Senha) =>
        (await S<AuthService>().LoginAsync(email, senha, null)).Usuario!;

    /// <summary>Um corretor novo (perfil Corretor), para haver pelo menos dois na fila e no rodízio.</summary>
    private async Task<UsuarioAtual> NovoCorretor(UsuarioAtual admin)
    {
        var sufixo = Guid.NewGuid().ToString("N")[..8];
        var perfil = (await S<PerfisService>().ListarAsync(admin)).Single(p => p.Nome == "Corretor");
        var email = $"fila.{sufixo}@inovabens.com.br";
        await S<UsuariosService>().CriarAsync(admin, new UsuarioForm { Nome = $"Corretor Fila {sufixo}", Email = email, Senha = "SenhaBoa12345", PerfilId = perfil.Id });
        return await Entrar(email, "SenhaBoa12345");
    }

    [Fact]
    public async Task Lead_sem_responsavel_aparece_para_os_corretores_e_o_primeiro_que_pegar_fica_com_ele()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var carla = await Entrar("carla@inovabens.com.br");
        var outro = await NovoCorretor(admin);
        var crm = S<CrmService>();
        var fabrica = S<IDbContextFactory<InovabensDb>>();

        var id = await crm.CriarAsync(admin, new LeadForm { NovoNome = "Fila " + Guid.NewGuid().ToString("N")[..6] });
        await using (var db = await fabrica.CreateDbContextAsync())
        {
            await db.Leads.Where(l => l.Id == id).ExecuteUpdateAsync(s => s.SetProperty(l => l.ResponsavelId, (Guid?)null));
            db.Alertas.AddRange(
                new Alerta { Id = Guid.NewGuid(), Tipo = TipoAlerta.NOVO_LEAD, UsuarioId = carla.Id, LeadId = id, Mensagem = "fila" },
                new Alerta { Id = Guid.NewGuid(), Tipo = TipoAlerta.NOVO_LEAD, UsuarioId = admin.Id, LeadId = id, Mensagem = "fila" });
            await db.SaveChangesAsync();
        }

        // na fila: os dois corretores veem o cartão, mas ainda não abrem o lead
        var cartao = (await crm.PipelineAsync(carla, Funil.VENDA)).SelectMany(c => c.Leads).Single(l => l.Id == id);
        Assert.True(cartao.NaFila);
        Assert.Contains((await crm.PipelineAsync(outro, Funil.VENDA)).SelectMany(c => c.Leads), l => l.Id == id);
        await Assert.ThrowsAsync<NaoEncontradoException>(() => crm.ObterAsync(carla, id));
        Assert.Contains((await crm.PipelineAsync(admin, Funil.VENDA, semResponsavel: true)).SelectMany(c => c.Leads), l => l.Id == id);

        // o outro corretor pega; a Carla chega depois e é avisada de quem ficou com o lead
        await crm.PegarAsync(outro, id);
        var erro = await Assert.ThrowsAsync<RegraNegocioException>(() => crm.PegarAsync(carla, id));
        Assert.Contains(outro.Nome, erro.Message);

        Assert.DoesNotContain((await crm.PipelineAsync(carla, Funil.VENDA)).SelectMany(c => c.Leads), l => l.Id == id);
        Assert.Equal(outro.Id, (await crm.ObterAsync(outro, id)).Dados.ResponsavelId);
        Assert.DoesNotContain((await crm.PipelineAsync(admin, Funil.VENDA, semResponsavel: true)).SelectMany(c => c.Leads), l => l.Id == id);
        Assert.Contains((await crm.PipelineAsync(admin, Funil.VENDA, responsavelId: outro.Id)).SelectMany(c => c.Leads), l => l.Id == id);

        // o aviso da Carla se resolve; o do administrador continua (é sobre vincular o imóvel)
        var alertas = S<AlertasService>();
        Assert.DoesNotContain(await alertas.ListarAsync(carla, false), a => a.LeadId == id);
        Assert.Contains(await alertas.ListarAsync(admin, false), a => a.LeadId == id);
    }

    [Fact]
    public async Task Rodizio_entrega_cada_lead_novo_do_WhatsApp_a_um_corretor_diferente()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        await NovoCorretor(admin); // garante dois corretores (Carla e este)
        S<InovabensOpcoes>().Integracoes = Integracoes.Vazias.Com(new Dictionary<string, string>
        {
            ["whatsapp.verify_token"] = "token-de-teste",
            ["whatsapp.app_secret"] = "segredo-de-teste",
            ["crm.distribuicao_leads"] = CatalogoConfiguracoes.DistribuicaoRodizio,
        });

        var responsaveis = new List<Guid?>();
        for (var i = 0; i < 2; i++)
        {
            var numero = "55179" + Random.Shared.Next(10_000_000, 99_999_999);
            using var doc = JsonDocument.Parse(Avisos.Recebida("wamid." + Guid.NewGuid().ToString("N"), numero, $"Rodízio {i}", "Olá, quero informações"));
            Assert.Equal(1, (await S<WhatsappService>().ProcessarAsync(doc.RootElement)).LeadsCriados);
            await using var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync();
            var lead = await db.Leads.AsNoTracking().Where(l => l.Origem == WhatsappService.Origem && l.Pessoa!.Whatsapp != null)
                .OrderByDescending(l => l.CriadoEm).FirstAsync();
            var dono = await db.Usuarios.AsNoTracking().Include(u => u.Perfil).SingleAsync(u => u.Id == lead.ResponsavelId);
            Assert.True(dono.Perfil!.EscopoProprio);
            Assert.True(await db.Alertas.AnyAsync(a => a.LeadId == lead.Id && a.UsuarioId == dono.Id && a.Tipo == TipoAlerta.NOVO_LEAD));
            responsaveis.Add(lead.ResponsavelId);
        }
        Assert.NotEqual(responsaveis[0], responsaveis[1]);
    }
}
