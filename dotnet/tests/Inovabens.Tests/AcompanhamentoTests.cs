using ClosedXML.Excel;
using Inovabens.Core.Comum;
using Inovabens.Core.Crm;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

public class FiltroAcompanhamentoTests
{
    [Fact]
    public void Filtros_vao_e_voltam_pela_query_string()
    {
        var f = new FiltroAcompanhamento
        {
            ImovelId = Guid.NewGuid(), SemResponsavel = true, Pessoa = "Ana & Cia", Funil = Funil.LOCACAO, Status = TipoEstagio.ABERTO,
            De = new DateOnly(2026, 1, 2), Ate = new DateOnly(2026, 2, 3), Origem = "WhatsApp", ParadoDias = 7, SemProximoPasso = true,
        };
        var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery("?" + f.ParaConsulta());
        var lido = FiltroAcompanhamento.Ler(k => q.TryGetValue(k, out var v) ? v.ToString() : null);
        Assert.Equal(f.ParaConsulta(), lido.ParaConsulta());
        Assert.Equal("Ana & Cia", lido.Pessoa);
        Assert.False(lido.AtividadeVencida);
        Assert.Equal("", new FiltroAcompanhamento().ParaConsulta());
        Assert.Null(FiltroAcompanhamento.Ler(_ => "lixo").ImovelId);
    }
}

/// <summary>Painel de acompanhamento por imóvel, contra PostgreSQL real.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class AcompanhamentoIntegracaoTests : IAsyncLifetime
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
    public async Task Mostra_por_imovel_quem_esta_em_que_etapa_com_filtros_indicadores_e_Excel()
    {
        var admin = (await S<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var carla = (await S<AuthService>().LoginAsync("carla@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var crm = S<CrmService>();
        var painel = S<AcompanhamentoService>();
        var sufixo = Guid.NewGuid().ToString("N")[..6];

        var imovel = await S<ImoveisService>().CriarAsync(admin, new ImovelForm { NomeDrive = "Acomp " + sufixo, Titulo = "Casa acompanhada", Tipo = "casa" });
        var estagios = (await crm.EstagiosAsync(Funil.VENDA)).Where(e => e.Ativo).OrderBy(e => e.Ordem).ToList();
        var (primeira, segunda) = (estagios[0], estagios[1]);

        var ana = await crm.CriarAsync(admin, new LeadForm { NovoNome = $"Ana Acomp {sufixo}", ImoveisInteresse = [imovel], ResponsavelId = carla.Id });
        var bia = await crm.CriarAsync(admin, new LeadForm { NovoNome = $"Bia Acomp {sufixo}", ImoveisInteresse = [imovel] });
        var semImovel = await crm.CriarAsync(admin, new LeadForm { NovoNome = $"Caio Acomp {sufixo}" });
        await crm.MoverAsync(admin, ana, segunda.Id);

        // só o imóvel: as duas pessoas, cada uma na sua etapa e com o seu responsável
        var a = await painel.ConsultarAsync(admin, new FiltroAcompanhamento { ImovelId = imovel });
        var grupo = Assert.Single(a.PorImovel);
        Assert.Equal(imovel, grupo.Key);
        Assert.Equal(2, grupo.Count());
        var linhaAna = grupo.Single(l => l.LeadId == ana);
        Assert.Equal((segunda.Nome, "Carla Corretora"), (linhaAna.Estagio, linhaAna.Responsavel));
        Assert.Equal(primeira.Nome, grupo.Single(l => l.LeadId == bia).Estagio);

        // indicadores: os dois passaram pela primeira etapa e um seguiu (50%)
        var etapa1 = a.Etapas.Single(e => e.Funil == Funil.VENDA && e.Nome == primeira.Nome);
        Assert.Equal((1, 2, 50m), (etapa1.Atuais, etapa1.Passaram, etapa1.ConversaoProxima));
        Assert.Contains(a.Usuarios, u => u.Id == carla.Id && u.Abertos == 1);

        // filtros por pessoa, responsável e "sem próximo passo"; o sem imóvel vai para o grupo próprio
        var porPessoa = await painel.ConsultarAsync(admin, new FiltroAcompanhamento { Pessoa = $"caio acomp {sufixo}" });
        var linhaCaio = Assert.Single(porPessoa.Linhas);
        Assert.Equal((semImovel, (Guid?)null), (linhaCaio.LeadId, linhaCaio.ImovelId));
        Assert.Equal([ana], (await painel.ConsultarAsync(admin, new FiltroAcompanhamento { ImovelId = imovel, ResponsavelId = carla.Id })).Linhas.Select(l => l.LeadId));
        Assert.Contains((await painel.ConsultarAsync(admin, new FiltroAcompanhamento { ImovelId = imovel, SemProximoPasso = true })).Linhas, l => l.LeadId == bia);
        Assert.Empty((await painel.ConsultarAsync(admin, new FiltroAcompanhamento { ImovelId = imovel, Status = TipoEstagio.GANHO })).Linhas);

        // o Corretor só enxerga os leads dele
        Assert.Equal([ana], (await painel.ConsultarAsync(carla, new FiltroAcompanhamento { Pessoa = $"Acomp {sufixo}" })).Linhas.Select(l => l.LeadId));

        // Excel com as três abas e as mesmas linhas
        var arq = await painel.ExcelAsync(admin, new FiltroAcompanhamento { ImovelId = imovel });
        using var wb = new XLWorkbook(new MemoryStream(arq.Conteudo));
        Assert.Equal(["Acompanhamento", "Etapas", "Usuários"], wb.Worksheets.Select(w => w.Name));
        Assert.Equal(3, wb.Worksheet("Acompanhamento").LastRowUsed()!.RowNumber());
    }
}
