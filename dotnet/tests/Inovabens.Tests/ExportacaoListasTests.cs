using ClosedXML.Excel;
using Inovabens.Core.Comum;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Pessoas;
using Inovabens.Core.Servicos;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Botão "Exportar Excel" das listas de Imóveis e Pessoas: só com a permissão; filtros da tela; sem fotos.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class ExportacaoListasTests : IAsyncLifetime
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
    public async Task Admin_exporta_as_listas_filtradas_e_o_corretor_sem_permissao_nao()
    {
        var admin = (await S<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var carla = (await S<AuthService>().LoginAsync("carla@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var sufixo = Guid.NewGuid().ToString("N")[..6];

        // permissões: o Administrador tem as duas; o Corretor não recebe por padrão
        Assert.True(admin.Pode(Permissoes.ImoveisExportarLista) && admin.Pode(Permissoes.PessoasExportarLista));
        Assert.False(carla.Pode(Permissoes.ImoveisExportarLista) || carla.Pode(Permissoes.PessoasExportarLista));

        // imóveis: só o que o filtro traz, com as colunas da lista (sem foto)
        var imoveis = S<ImoveisService>();
        await imoveis.CriarAsync(admin, new ImovelForm { NomeDrive = "Export " + sufixo, Titulo = $"Casa exportada {sufixo}", Tipo = "casa" });
        var filtro = new FiltroImoveis { Termo = $"exportada {sufixo}" };
        var arq = await imoveis.ExcelListaAsync(admin, filtro);
        Assert.EndsWith(".xlsx", arq.Nome);
        using (var wb = new XLWorkbook(new MemoryStream(arq.Conteudo)))
        {
            var ws = wb.Worksheet("Imóveis");
            Assert.Equal(2, ws.LastRowUsed()!.RowNumber());
            Assert.Equal($"Casa exportada {sufixo}", ws.Cell(2, 2).GetString());
            var cabecalho = ws.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
            Assert.DoesNotContain(cabecalho, c => c.Contains("Foto", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(ws.Pictures);
        }
        // outro filtro da tela (situação que o imóvel novo não tem): planilha só com o cabeçalho
        using (var vazio = new XLWorkbook(new MemoryStream((await imoveis.ExcelListaAsync(admin, filtro with { Situacao = SituacaoImovel.VENDIDO })).Conteudo)))
            Assert.Equal(1, vazio.Worksheet(1).LastRowUsed()!.RowNumber());
        await Assert.ThrowsAsync<AcessoNegadoException>(() => imoveis.ExcelListaAsync(carla, filtro));

        // pessoas: filtro de busca, contato e perfis
        var pessoas = S<PessoasService>();
        await pessoas.CriarAsync(admin, new PessoaForm { NomeRazao = $"Pessoa Exportada {sufixo}" });
        var p = await pessoas.ExcelListaAsync(admin, new FiltroPessoas($"exportada {sufixo}"));
        using (var wb = new XLWorkbook(new MemoryStream(p.Conteudo)))
        {
            var ws = wb.Worksheet("Pessoas");
            Assert.Equal(2, ws.LastRowUsed()!.RowNumber());
            Assert.Equal($"Pessoa Exportada {sufixo}", ws.Cell(2, 1).GetString());
            Assert.Equal("Pessoa física", ws.Cell(2, 2).GetString());
        }
        await Assert.ThrowsAsync<AcessoNegadoException>(() => pessoas.ExcelListaAsync(carla, new FiltroPessoas()));
    }
}
