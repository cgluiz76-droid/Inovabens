using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Controle de chaves do imóvel: cadastro com código único, saída, devolução, atraso e escopo do corretor.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class ChavesTests : IAsyncLifetime
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

    private Task<Guid> NovoImovel(UsuarioAtual u) =>
        S<ImoveisService>().CriarAsync(u, new ImovelForm
        {
            NomeDrive = "Chave " + Guid.NewGuid().ToString("N")[..8], Titulo = "Casa com chaves", Tipo = "casa", PrecoVenda = 300_000,
        });

    [Fact]
    public async Task Chave_sai_volta_atrasa_e_mantem_historico()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var svc = S<ChavesService>();
        var imovel = await NovoImovel(admin);

        var codigo = await svc.ProximoCodigoAsync();
        Assert.Matches("^CH\\d{4}$", codigo);
        var id = await svc.CriarAsync(admin, imovel, new ChaveForm { Codigo = codigo.ToLowerInvariant(), Descricao = "Portão" });
        Assert.NotEqual(codigo, await svc.ProximoCodigoAsync());

        // código é a etiqueta física: não repete (nem com outra caixa)
        var dup = await Assert.ThrowsAsync<RegraNegocioException>(() =>
            svc.CriarAsync(admin, imovel, new ChaveForm { Codigo = codigo, Descricao = "Outra" }));
        Assert.Contains("Já existe", dup.Message);

        var chave = Assert.Single((await svc.DoImovelAsync(admin, imovel)).Chaves);
        Assert.Equal(codigo, chave.Codigo);
        Assert.Equal(SituacaoChave.DISPONIVEL, chave.Situacao);

        // regras da saída: com quem, motivo "Outro" exige detalhe, prazo no futuro
        await Assert.ThrowsAsync<RegraNegocioException>(() => svc.EntregarAsync(admin, id,
            new EntregaChaveForm { Motivo = "Visita", DevolverAte = DatasBR.Agora().AddHours(2) }));
        await Assert.ThrowsAsync<RegraNegocioException>(() => svc.EntregarAsync(admin, id,
            new EntregaChaveForm { PessoaNome = "Ana", Motivo = ChavesService.MotivoOutro, DevolverAte = DatasBR.Agora().AddHours(2) }));
        await Assert.ThrowsAsync<RegraNegocioException>(() => svc.EntregarAsync(admin, id,
            new EntregaChaveForm { PessoaNome = "Ana", Motivo = "Visita", DevolverAte = DatasBR.Agora().AddHours(-1) }));

        await svc.EntregarAsync(admin, id, new EntregaChaveForm
        {
            PessoaNome = "Ana Vistoriadora", PessoaTelefone = "17999998888", Motivo = "Vistoria", DevolverAte = DatasBR.Agora().AddHours(2),
        });
        chave = Assert.Single((await svc.DoImovelAsync(admin, imovel)).Chaves);
        Assert.Equal(SituacaoChave.FORA, chave.Situacao);
        Assert.Equal("Ana Vistoriadora", chave.Fora!.ComQuem);
        Assert.Equal("(17) 99999-8888", chave.Fora.Telefone);
        Assert.Contains(await svc.ForaAsync(admin), c => c.Id == id);

        // fora: não sai de novo nem pode ser excluída
        await Assert.ThrowsAsync<RegraNegocioException>(() => svc.EntregarAsync(admin, id,
            new EntregaChaveForm { UsuarioId = admin.Id, Motivo = "Visita", DevolverAte = DatasBR.Agora().AddHours(1) }));
        await Assert.ThrowsAsync<RegraNegocioException>(() => svc.ExcluirAsync(admin, id));

        // prazo vencido vira atrasada sozinho
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
            await db.ChaveMovimentos.Where(m => m.ChaveId == id).ExecuteUpdateAsync(x => x.SetProperty(m => m.PrevisaoDevolucao, DateTime.UtcNow.AddMinutes(-5)));
        Assert.Equal(SituacaoChave.ATRASADA, Assert.Single((await svc.DoImovelAsync(admin, imovel)).Chaves).Situacao);
        Assert.Equal(SituacaoChave.ATRASADA, (await svc.ForaAsync(admin))[0].Situacao);
        Assert.True((await svc.ContagemAsync(admin)).Atrasadas >= 1);

        await svc.ReceberAsync(admin, id, "Devolvida na recepção");
        var dados = await svc.DoImovelAsync(admin, imovel);
        Assert.Equal(SituacaoChave.DISPONIVEL, Assert.Single(dados.Chaves).Situacao);
        var mov = Assert.Single(dados.Historico);
        Assert.NotNull(mov.DevolvidaEm);
        Assert.Equal("Devolvida na recepção", mov.ObservacaoDevolucao);
        Assert.Equal("Administrador", mov.RecebidoPor);
        await Assert.ThrowsAsync<RegraNegocioException>(() => svc.ReceberAsync(admin, id, null));

        // saída para alguém da equipe e exclusão lógica (histórico continua)
        await svc.EntregarAsync(admin, id, new EntregaChaveForm { UsuarioId = admin.Id, Motivo = "Visita", DevolverAte = DatasBR.Agora().AddHours(1) });
        Assert.Equal("Administrador", Assert.Single((await svc.DoImovelAsync(admin, imovel)).Chaves).Fora!.ComQuem);
        await svc.ReceberAsync(admin, id, null);
        await svc.ExcluirAsync(admin, id);
        dados = await svc.DoImovelAsync(admin, imovel);
        Assert.Empty(dados.Chaves);
        Assert.Equal(2, dados.Historico.Count);
    }

    [Fact]
    public async Task Planilha_e_etiquetas_saem_com_as_chaves_do_imovel()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var svc = S<ChavesService>();
        var imovel = await NovoImovel(admin);
        var ids = new List<Guid>();
        for (var i = 0; i < 26; i++) // 26 > 24 por folha: etiquetas em 2 páginas
            ids.Add(await svc.CriarAsync(admin, imovel, new ChaveForm { Codigo = await svc.ProximoCodigoAsync(), Descricao = $"Porta {i + 1}" }));
        await svc.EntregarAsync(admin, ids[0], new EntregaChaveForm { PessoaNome = "Pedro", Motivo = "Visita", DevolverAte = DatasBR.Agora().AddHours(3) });

        var xlsx = await svc.ExcelAsync(admin, imovel);
        Assert.EndsWith(".xlsx", xlsx.Nome);
        using (var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(xlsx.Conteudo)))
        {
            var ws = wb.Worksheet("Chaves");
            Assert.Equal(27, ws.LastRowUsed()!.RowNumber());
            var linhaFora = ws.RowsUsed().Single(r => r.Cell(7).GetString() == "Pedro");
            Assert.Equal("Fora", linhaFora.Cell(6).GetString());
            Assert.Equal(2, wb.Worksheet("Histórico").LastRowUsed()!.RowNumber());
        }

        var todas = await svc.EtiquetasPdfAsync(admin, imovel, null);
        using (var doc = Docnet.Core.DocLib.Instance.GetDocReader(todas.Conteudo, new Docnet.Core.Models.PageDimensions(1.0)))
            Assert.Equal(2, doc.GetPageCount());
        var uma = await svc.EtiquetasPdfAsync(admin, null, ids[1]);
        Assert.StartsWith("etiqueta-CH", uma.Nome);

        foreach (var id in ids.Skip(1)) await svc.ExcluirAsync(admin, id);
        await svc.ReceberAsync(admin, ids[0], null);
    }

    [Fact]
    public async Task Com_quem_busca_em_Pessoas_e_cadastra_nome_novo_na_saida()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var svc = S<ChavesService>();
        var imovel = await NovoImovel(admin);
        var id = await svc.CriarAsync(admin, imovel, new ChaveForm { Codigo = await svc.ProximoCodigoAsync(), Descricao = "Casa" });
        var sufixo = Guid.NewGuid().ToString("N")[..6];
        var nome = $"Zélia Vistoriadora {sufixo}";

        // não existe: sem acento já não acha ninguém; registra a saída cadastrando a pessoa na hora
        Assert.DoesNotContain(await svc.BuscarResponsaveisAsync(admin, $"zelia vistoriadora {sufixo}"), r => !r.Equipe);
        await svc.EntregarAsync(admin, id, new EntregaChaveForm
        {
            PessoaNome = nome, PessoaTelefone = "17 97777-1234", CadastrarPessoa = true, Motivo = "Vistoria", DevolverAte = DatasBR.Agora().AddHours(2),
        });
        var fora = Assert.Single((await svc.DoImovelAsync(admin, imovel)).Chaves).Fora!;
        Assert.Equal(nome, fora.ComQuem);
        Assert.Equal("(17) 97777-1234", fora.Telefone);

        // agora aparece na busca (sem acento, e pelo telefone) e pode ser escolhida da próxima vez
        var achada = Assert.Single(await svc.BuscarResponsaveisAsync(admin, $"zelia vistoriadora {sufixo}"));
        Assert.False(achada.Equipe);
        Assert.Contains(await svc.BuscarResponsaveisAsync(admin, "977771234"), r => r.Id == achada.Id);
        Assert.Contains(await svc.BuscarResponsaveisAsync(admin, "administ"), r => r.Equipe && r.Id == admin.Id);

        await svc.ReceberAsync(admin, id, null);
        await svc.EntregarAsync(admin, id, new EntregaChaveForm { PessoaId = achada.Id, Motivo = "Visita", DevolverAte = DatasBR.Agora().AddHours(1) });
        fora = Assert.Single((await svc.DoImovelAsync(admin, imovel)).Chaves).Fora!;
        Assert.Equal(nome, fora.ComQuem);
        Assert.Equal("(17) 97777-1234", fora.Telefone);
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
            Assert.Equal(1, await db.Pessoas.CountAsync(p => p.NomeRazao == nome));
        await svc.ReceberAsync(admin, id, null);

        // "só anotar o nome": não cria pessoa
        var anotado = $"Pedreiro {sufixo}";
        await svc.EntregarAsync(admin, id, new EntregaChaveForm { PessoaNome = anotado, Motivo = "Manutenção / obra", DevolverAte = DatasBR.Agora().AddHours(1) });
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
            Assert.False(await db.Pessoas.AnyAsync(p => p.NomeRazao == anotado));
        await svc.ReceberAsync(admin, id, null);
    }

    [Fact]
    public async Task Corretor_movimenta_mas_nao_cadastra_e_so_ve_os_seus_imoveis()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var carla = await Entrar("carla@inovabens.com.br");
        var svc = S<ChavesService>();
        var imovel = await NovoImovel(admin); // responsável: admin, fora do escopo da corretora
        var id = await svc.CriarAsync(admin, imovel, new ChaveForm { Codigo = await svc.ProximoCodigoAsync(), Descricao = "Apartamento" });

        await Assert.ThrowsAsync<AcessoNegadoException>(() =>
            svc.CriarAsync(carla, imovel, new ChaveForm { Codigo = "XX9999", Descricao = "x" }));
        await Assert.ThrowsAsync<AcessoNegadoException>(() => svc.DoImovelAsync(carla, imovel));
        await Assert.ThrowsAsync<AcessoNegadoException>(() => svc.EntregarAsync(carla, id,
            new EntregaChaveForm { UsuarioId = carla.Id, Motivo = "Visita", DevolverAte = DatasBR.Agora().AddHours(1) }));

        await svc.EntregarAsync(admin, id, new EntregaChaveForm { PessoaNome = "Cliente", Motivo = "Visita", DevolverAte = DatasBR.Agora().AddHours(1) });
        Assert.DoesNotContain(await svc.ForaAsync(carla), c => c.Id == id);
        await svc.ReceberAsync(admin, id, null);
    }
}
