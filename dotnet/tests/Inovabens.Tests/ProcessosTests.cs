using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Crm;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Funis de processos do imóvel (Administrativo, Projetos) e funil de captação de leads.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class ProcessosTests : IAsyncLifetime
{
    private IServiceScope _escopo = default!;
    private readonly string _sufixo = Guid.NewGuid().ToString("N")[..8];
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

    private Task<Guid> NovoImovel(UsuarioAtual u, Guid? corretor = null) =>
        S<ImoveisService>().CriarAsync(u, new ImovelForm
        {
            NomeDrive = "Proc " + Guid.NewGuid().ToString("N")[..8], Titulo = $"Casa processo {_sufixo}", Tipo = "casa",
            PrecoVenda = 300_000, CorretorResponsavelId = corretor,
        });

    private async Task<IReadOnlyList<FunilEstagio>> Etapas(Funil f)
    {
        await using var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync();
        return await db.FunilEstagios.AsNoTracking().Where(e => e.Funil == f && e.Ativo).OrderBy(e => e.Ordem).ToListAsync();
    }

    private async Task<Imovel> Imovel(Guid id)
    {
        await using var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync();
        return await db.Imoveis.AsNoTracking().SingleAsync(i => i.Id == id);
    }

    [Fact]
    public async Task Novos_funis_tem_etapas_padrao()
    {
        Assert.Equal(PadroesIniciais.EstagiosDo(Funil.CAPTACAO).Select(e => e.Nome), (await Etapas(Funil.CAPTACAO)).Select(e => e.Nome));
        Assert.Equal(PadroesIniciais.EstagiosDo(Funil.ADMINISTRATIVO).Select(e => e.Nome), (await Etapas(Funil.ADMINISTRATIVO)).Select(e => e.Nome));
        Assert.Equal(PadroesIniciais.EstagiosDo(Funil.PROJETOS).Select(e => e.Nome), (await Etapas(Funil.PROJETOS)).Select(e => e.Nome));
    }

    [Fact]
    public async Task Imovel_entra_no_funil_uma_vez_e_move_com_historico()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var svc = S<ProcessosService>();
        var imovel = await NovoImovel(admin);

        var id = await svc.CriarAsync(admin, new ProcessoForm { Funil = Funil.ADMINISTRATIVO, ImovelId = imovel, Observacoes = "Falta certidão" });
        var etapas = await Etapas(Funil.ADMINISTRATIVO);
        var quadro = await svc.QuadroAsync(admin, Funil.ADMINISTRATIVO, imovelId: imovel);
        var cartao = Assert.Single(quadro.SelectMany(c => c.Cartoes));
        Assert.Equal(etapas[0].Id, quadro.Single(c => c.Cartoes.Count > 0).Estagio.Id);
        Assert.Equal("Falta certidão", cartao.Observacoes);

        var dup = await Assert.ThrowsAsync<RegraNegocioException>(() =>
            svc.CriarAsync(admin, new ProcessoForm { Funil = Funil.ADMINISTRATIVO, ImovelId = imovel }));
        Assert.Contains("já está no funil", dup.Message);

        // etapa de outro funil é recusada
        var deProjetos = (await Etapas(Funil.PROJETOS))[1];
        await Assert.ThrowsAsync<RegraNegocioException>(() => svc.MoverAsync(admin, id, deProjetos.Id));

        await svc.MoverAsync(admin, id, etapas[2].Id);
        quadro = await svc.QuadroAsync(admin, Funil.ADMINISTRATIVO, imovelId: imovel);
        Assert.Equal(etapas[2].Id, quadro.Single(c => c.Cartoes.Count > 0).Estagio.Id);
        var hist = await svc.HistoricoAsync(admin, id);
        Assert.Equal(2, hist.Count);
        Assert.Equal(etapas[2].Nome, hist[0].EstagioNovo);
        Assert.Equal(etapas[0].Nome, hist[0].EstagioAnterior);

        // depois de sair do funil, pode entrar de novo
        await svc.ExcluirAsync(admin, id);
        Assert.Empty((await svc.QuadroAsync(admin, Funil.ADMINISTRATIVO, imovelId: imovel)).SelectMany(c => c.Cartoes));
        await svc.CriarAsync(admin, new ProcessoForm { Funil = Funil.ADMINISTRATIVO, ImovelId = imovel });
    }

    [Fact]
    public async Task Funil_projetos_atualiza_a_etapa_e_o_status_do_projeto_no_imovel()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var svc = S<ProcessosService>();
        var imovel = await NovoImovel(admin);
        var etapas = await Etapas(Funil.PROJETOS);

        var id = await svc.CriarAsync(admin, new ProcessoForm { Funil = Funil.PROJETOS, ImovelId = imovel });
        var i = await Imovel(imovel);
        Assert.Equal(etapas[0].Nome, i.ProjetoEtapa);
        Assert.Equal("Não iniciada", i.ProjetoStatus);

        await svc.MoverAsync(admin, id, etapas[2].Id);
        i = await Imovel(imovel);
        Assert.Equal(etapas[2].Nome, i.ProjetoEtapa);
        Assert.Equal("Em andamento", i.ProjetoStatus);

        await svc.MoverAsync(admin, id, etapas.Single(e => e.Tipo == TipoEstagio.GANHO).Id);
        i = await Imovel(imovel);
        Assert.Equal("Concluído", i.ProjetoStatus);
    }

    [Fact]
    public async Task Perfil_sem_permissao_nao_ve_processos_e_escopo_proprio_ve_so_os_seus()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var carla = await Entrar("carla@inovabens.com.br");
        var svc = S<ProcessosService>();
        await Assert.ThrowsAsync<AcessoNegadoException>(() => svc.QuadroAsync(carla, Funil.ADMINISTRATIVO));
        Assert.Empty(await svc.DosImoveisAsync(carla, null));

        // dá as permissões ao corretor só neste teste (cópia do usuário atual)
        var comPermissao = carla with { Permissoes = new HashSet<string>(carla.Permissoes) { Permissoes.ProcessosVer, Permissoes.ProcessosEditar } };
        var dela = await NovoImovel(admin, carla.Id);
        var outro = await NovoImovel(admin);
        await svc.CriarAsync(admin, new ProcessoForm { Funil = Funil.ADMINISTRATIVO, ImovelId = dela });
        await svc.CriarAsync(admin, new ProcessoForm { Funil = Funil.ADMINISTRATIVO, ImovelId = outro });

        var visiveis = (await svc.QuadroAsync(comPermissao, Funil.ADMINISTRATIVO)).SelectMany(c => c.Cartoes).Select(c => c.ImovelId).ToList();
        Assert.Contains(dela, visiveis);
        Assert.DoesNotContain(outro, visiveis);
    }

    [Fact]
    public async Task Funil_de_leads_nao_e_aceito_como_processo()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        await Assert.ThrowsAsync<RegraNegocioException>(() => S<ProcessosService>().QuadroAsync(admin, Funil.VENDA));
        Assert.Throws<ArgumentException>(() => Funis.FinalidadeDo(Funil.PROJETOS));
    }

    [Fact]
    public async Task Lead_de_captacao_fica_no_funil_de_captacao_e_nao_recebe_proposta()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var crm = S<CrmService>();
        var id = await crm.CriarAsync(admin, new LeadForm { NovoNome = "Proprietário " + _sufixo, Finalidade = FinalidadeLead.CAPTACAO });

        var colunas = await crm.PipelineAsync(admin, Funil.CAPTACAO);
        Assert.Contains(colunas.SelectMany(c => c.Leads), l => l.Id == id);
        Assert.Equal((await Etapas(Funil.CAPTACAO))[0].Id, colunas.Single(c => c.Leads.Any(l => l.Id == id)).Estagio.Id);
        Assert.DoesNotContain((await crm.PipelineAsync(admin, Funil.VENDA)).SelectMany(c => c.Leads), l => l.Id == id);

        var imovel = await NovoImovel(admin);
        var erro = await Assert.ThrowsAsync<RegraNegocioException>(() => S<PropostasService>().CriarAsync(admin, id, new PropostaForm
        {
            ImovelId = imovel, Titulo = "Proposta", Inicio = DatasBR.Agora().Date.AddDays(1).AddHours(10),
            Itens = [new() { Tipo = TipoItemProposta.ENTRADA, Valor = 300_000 }],
        }));
        Assert.Contains("captação", erro.Message);
    }
}
