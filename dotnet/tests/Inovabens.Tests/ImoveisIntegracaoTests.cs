using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>
/// Fluxo completo contra um PostgreSQL real (ver <see cref="BancoFixture"/>: Docker descartável ou INOVABENS_TEST_DB),
/// com as migrações do .NET aplicadas e os usuários admin@ / carla@ (Corretor) criados com a senha Trocar@123456.
/// </summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class ImoveisIntegracaoTests
{
    private static string Url => BancoFixture.Url;

    [Fact]
    public async Task Fluxo_completo_do_modulo_imoveis()
    {

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DATABASE_URL"] = Url }).Build();
        var services = new ServiceCollection().AddLogging().AddInovabensCore(InovabensOpcoes.De(cfg, producao: false));
        await using var sp = services.BuildServiceProvider();
        using var escopo = sp.CreateScope();
        escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        var auth = escopo.ServiceProvider.GetRequiredService<AuthService>();
        var s = escopo.ServiceProvider.GetRequiredService<ImoveisService>();
        var opcoes = escopo.ServiceProvider.GetRequiredService<OpcoesService>();
        var fabrica = escopo.ServiceProvider.GetRequiredService<IDbContextFactory<InovabensDb>>();

        // --- login (hash Argon2 do Node) ---
        Assert.Equal(SituacaoLogin.Falha, (await auth.LoginAsync("admin@inovabens.com.br", "errada", null)).Situacao);
        var admin = (await auth.LoginAsync("ADMIN@inovabens.com.br", "Trocar@123456", null)).Usuario!;
        Assert.True(admin.Pode(Permissoes.ImoveisExcluir));
        var corretor = (await auth.LoginAsync("carla@inovabens.com.br", "Trocar@123456", null)).Usuario!;
        Assert.True(corretor.EscopoProprio);
        Assert.False(corretor.Pode(Permissoes.ImoveisExcluir));

        // --- criar: código sequencial e enums gravados nos tipos do PostgreSQL ---
        var id1 = await s.CriarAsync(admin, new ImovelForm { NomeDrive = Guid.NewGuid().ToString(), Titulo = "Apartamento Gonzaga", Tipo = "apartamento", Cidade = "Santos", Uf = "sp", PrecoVenda = 750000m, Situacao = SituacaoImovel.EM_AQUISICAO });
        var id2 = await s.CriarAsync(admin, new ImovelForm { NomeDrive = Guid.NewGuid().ToString(), Titulo = "Terreno 50% à vista", Tipo = "terreno" });
        var d1 = await s.DetalheAsync(admin, id1);
        var d2 = await s.DetalheAsync(admin, id2);
        Assert.Matches(@"^IMV-\d{6}$", d1.Dados.Codigo);
        Assert.Equal(int.Parse(d1.Dados.Codigo[4..]) + 1, int.Parse(d2.Dados.Codigo[4..]));
        Assert.Equal("SP", d1.Dados.Uf);
        Assert.Equal(SituacaoImovel.EM_AQUISICAO, d1.Dados.Situacao);
        Assert.Equal("Administrador", d1.CorretorNome);

        // --- listar: busca (ILIKE com curinga escapado) e filtro por situação ---
        Assert.Contains((await s.ListarAsync(admin, new FiltroImoveis { Termo = "gonzaga" })).Itens, i => i.Id == id1);
        var porcento = await s.ListarAsync(admin, new FiltroImoveis { Termo = "50%" });
        Assert.Contains(porcento.Itens, i => i.Id == id2);
        Assert.DoesNotContain(porcento.Itens, i => i.Id == id1);
        Assert.All((await s.ListarAsync(admin, new FiltroImoveis { Situacao = SituacaoImovel.EM_AQUISICAO })).Itens,
            i => Assert.Equal(SituacaoImovel.EM_AQUISICAO, i.Situacao));

        // --- atualizar ---
        var form = ImovelForm.De(d1.Dados);
        form.Titulo = "Apartamento Gonzaga reformado";
        await s.AtualizarAsync(admin, id1, form);
        Assert.Equal("Apartamento Gonzaga reformado", (await s.DetalheAsync(admin, id1)).Dados.Titulo);

        // --- proprietários: regra dos 100% ---
        var pessoas = await opcoes.PessoasAsync(admin);
        Assert.True(pessoas.Count >= 2);
        await Assert.ThrowsAsync<RegraNegocioException>(() => s.SalvarProprietariosAsync(admin, id1,
            [new() { PessoaId = pessoas[0].Id, Percentual = 60 }, new() { PessoaId = pessoas[1].Id, Percentual = 30 }]));
        await s.SalvarProprietariosAsync(admin, id1,
            [new() { PessoaId = pessoas[0].Id, Percentual = 60, Principal = true }, new() { PessoaId = pessoas[1].Id, Percentual = 40 }]);
        Assert.Equal(100m, (await s.DetalheAsync(admin, id1)).Proprietarios.Sum(p => p.Percentual));

        // --- características: chaves repetidas (maiúsc./minúsc.) viram uma só ---
        await s.SalvarCaracteristicasAsync(admin, id1,
            [new() { Chave = "Piscina" }, new() { Chave = "piscina", Valor = "aquecida" }, new() { Chave = "Varanda", Publico = false }]);
        var carac = (await s.DetalheAsync(admin, id1)).Caracteristicas;
        Assert.Equal(2, carac.Count);
        Assert.Equal("aquecida", carac.Single(c => c.Chave.Equals("piscina", StringComparison.OrdinalIgnoreCase)).Valor);

        // --- mídias: sem Google Drive configurado, envio é recusado de forma controlada ---
        await Assert.ThrowsAsync<ServicoIndisponivelException>(() =>
            s.EnviarMidiaAsync(admin, id1, new MemoryStream([1, 2, 3]), "foto.jpg", "image/jpeg", SubpastasDrive.FotosVideos));

        // --- regularizações ---
        await s.CriarRegularizacaoAsync(admin, id1, new RegularizacaoForm { Tipo = TipoRegularizacao.RETIFICACAO_AREA, ProfissionalId = pessoas[0].Id, CustoEstimado = 3500.5m, PrazoPrevisto = new DateOnly(2026, 12, 15) });
        var regs = await s.ListarRegularizacoesAsync(admin, id1);
        var reg = Assert.Single(regs);
        Assert.Equal(pessoas[0].Nome, reg.ProfissionalNome);
        var rf = RegularizacaoForm.De(reg.Dados);
        rf.Status = StatusRegularizacao.CONCLUIDA;
        rf.ConcluidaEm = new DateOnly(2026, 11, 1);
        await s.AtualizarRegularizacaoAsync(admin, reg.Dados.Id, rf);
        Assert.Equal(StatusRegularizacao.CONCLUIDA, (await s.ListarRegularizacoesAsync(admin, id1))[0].Dados.Status);
        await s.ExcluirRegularizacaoAsync(admin, reg.Dados.Id);
        Assert.Empty(await s.ListarRegularizacoesAsync(admin, id1));

        // --- escopo do Corretor: só enxerga e altera os próprios imóveis ---
        Assert.DoesNotContain((await s.ListarAsync(corretor, new FiltroImoveis())).Itens, i => i.Id == id1);
        await Assert.ThrowsAsync<AcessoNegadoException>(() => s.DetalheAsync(corretor, id1));
        await Assert.ThrowsAsync<AcessoNegadoException>(() => s.ExcluirAsync(corretor, id1)); // sem imoveis:excluir
        var idCorretor = await s.CriarAsync(corretor, new ImovelForm { NomeDrive = Guid.NewGuid().ToString(), Titulo = "Casa da Carla", Tipo = "casa", CorretorResponsavelId = admin.Id });
        var dc = await s.DetalheAsync(corretor, idCorretor);
        Assert.Equal(corretor.Id, dc.Dados.CorretorResponsavelId); // não consegue atribuir a outro
        Assert.Contains((await s.ListarAsync(corretor, new FiltroImoveis())).Itens, i => i.Id == idCorretor);

        // --- excluir (lógico) ---
        await s.ExcluirAsync(admin, id2);
        await Assert.ThrowsAsync<NaoEncontradoException>(() => s.DetalheAsync(admin, id2));

        // --- auditoria gravada com IP e JSON ---
        await using var db = await fabrica.CreateDbContextAsync();
        var trilha = await db.AuditLog.Where(a => a.EntidadeId == id1).OrderBy(a => a.CriadoEm).ToListAsync();
        Assert.Contains(trilha, a => a.Acao == "CRIAR");
        var upd = trilha.First(a => a.Acao == "ATUALIZAR" && a.ValoresAntes != null);
        Assert.Contains("\"titulo\": \"Apartamento Gonzaga\"", upd.ValoresAntes);
        Assert.Contains("\"situacao\": \"EM_AQUISICAO\"", upd.ValoresDepois!); // jsonb normaliza o espaçamento
        Assert.DoesNotContain("senha", upd.ValoresDepois!);
        Assert.All(trilha, a => Assert.Equal("127.0.0.1", a.Ip));
    }
}
