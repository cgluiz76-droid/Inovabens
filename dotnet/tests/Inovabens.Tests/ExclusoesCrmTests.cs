using Inovabens.Core.Admin;
using Inovabens.Core.Agenda;
using Inovabens.Core.Comum;
using Inovabens.Core.Crm;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

public class AuditoriaResumoTests
{
    [Fact]
    public void Exclusao_usa_a_descricao_gravada() =>
        Assert.Equal("Lead de Fulano (Compra)", AuditoriaService.Resumir("""{"descricao":"Lead de Fulano (Compra)","dados":{"id":"x"}}"""));

    [Fact]
    public void Registro_antigo_usa_o_campo_que_identifica() =>
        Assert.Equal("Visita", AuditoriaService.Resumir("""{"id":"x","titulo":"Visita"}"""));

    [Fact]
    public void Sem_dados_nao_resume() => Assert.Null(AuditoriaService.Resumir(null));
}

/// <summary>Administrador exclui lead, ações, atividades e propostas no CRM; tudo fica na Auditoria (quem e o quê).</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class ExclusoesCrmTests : IAsyncLifetime
{
    private IServiceScope _escopo = default!;
    private T S<T>() where T : notnull => _escopo.ServiceProvider.GetRequiredService<T>();
    private readonly string sufixo = Guid.NewGuid().ToString("N")[..6];
    private UsuarioAtual admin = default!, corretor = default!;

    public async Task InitializeAsync()
    {
        _escopo = ExtratoComprovantesTests.Compartilhado.CreateScope();
        _escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        var auth = S<AuthService>();
        admin = (await auth.LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        corretor = (await auth.LoginAsync("carla@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
    }

    public Task DisposeAsync()
    {
        _escopo.Dispose();
        return Task.CompletedTask;
    }

    private async Task<AuditLog> Exclusao(string entidade, Guid id)
    {
        await using var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync();
        return await db.AuditLog.AsNoTracking().SingleAsync(a => a.Entidade == entidade && a.EntidadeId == id && a.Acao == "EXCLUIR");
    }

    [Fact]
    public async Task Corretor_nao_exclui_e_administrador_exclui_com_registro_na_auditoria()
    {
        var crm = S<CrmService>();
        var propostas = S<PropostasService>();
        var casa = await S<ImoveisService>().CriarAsync(corretor, new ImovelForm
        {
            NomeDrive = Guid.NewGuid().ToString(), Titulo = $"Casa Exclusão {sufixo}", Tipo = "casa", PrecoVenda = 400_000,
            CorretorResponsavelId = corretor.Id,
        });
        var nome = $"Cliente Exclusão {sufixo}";
        var lead = await crm.CriarAsync(corretor, new LeadForm { NovoNome = nome });
        var proposta = await propostas.CriarAsync(corretor, lead, new PropostaForm
        {
            ImovelId = casa, Titulo = "Levar ao proprietário", Inicio = DatasBR.Agora().Date.AddDays(1).AddHours(10),
            Itens = [new PropostaItemForm { Tipo = TipoItemProposta.ENTRADA, Valor = 380_000 }],
        });
        var acao = (await crm.InteracoesAsync(corretor, lead))[0].Dados;
        var atividade = (await crm.PendentesDoLeadAsync(corretor, lead))[0];

        // corretor (sem crm:excluir) não consegue excluir nada
        await Assert.ThrowsAsync<AcessoNegadoException>(() => crm.ExcluirAcaoAsync(corretor, lead, acao.Id));
        await Assert.ThrowsAsync<AcessoNegadoException>(() => crm.ExcluirAtividadeAsync(corretor, lead, atividade.Id));
        await Assert.ThrowsAsync<AcessoNegadoException>(() => propostas.ExcluirAsync(corretor, proposta));
        await Assert.ThrowsAsync<AcessoNegadoException>(() => crm.ExcluirAsync(corretor, lead));

        // administrador exclui; cada exclusão registra quem excluiu e o que era
        await propostas.ExcluirAsync(admin, proposta);
        Assert.Empty(await propostas.DoLeadAsync(admin, lead));
        var ap = await Exclusao("propostas", proposta);
        Assert.Equal(admin.Id, ap.UsuarioId);
        Assert.Contains(nome, AuditoriaService.Resumir(ap.ValoresAntes));
        Assert.Contains("380000", ap.ValoresAntes); // itens da proposta guardados

        await crm.ExcluirAtividadeAsync(admin, lead, atividade.Id);
        Assert.Empty(await crm.PendentesDoLeadAsync(admin, lead));
        Assert.Null((await crm.ObterAsync(admin, lead)).Dados.ProximaAcaoEm);
        var aa = await Exclusao("compromissos", atividade.Id);
        Assert.Equal(admin.Id, aa.UsuarioId);
        Assert.Contains(atividade.Titulo, AuditoriaService.Resumir(aa.ValoresAntes));

        await crm.ExcluirAcaoAsync(admin, lead, acao.Id);
        Assert.DoesNotContain(await crm.InteracoesAsync(admin, lead), i => i.Dados.Id == acao.Id);
        var ai = await Exclusao("lead_interacoes", acao.Id);
        Assert.Equal(admin.Id, ai.UsuarioId);
        Assert.Contains(acao.Resumo, ai.ValoresAntes);

        await crm.ExcluirAsync(admin, lead);
        await Assert.ThrowsAsync<NaoEncontradoException>(() => crm.ObterAsync(admin, lead));
        var al = await Exclusao("leads", lead);
        Assert.Equal($"Lead de {nome} (Compra)", AuditoriaService.Resumir(al.ValoresAntes));

        // a tela Auditoria lista só as exclusões, com o nome de quem excluiu e o resumo
        var lista = await S<AuditoriaService>().ListarAsync(admin, "leads", lead, soExclusoes: true);
        var item = Assert.Single(lista.Itens);
        Assert.Equal(admin.Nome, item.UsuarioNome);
        Assert.Equal($"Lead de {nome} (Compra)", item.Resumo);
    }
}
