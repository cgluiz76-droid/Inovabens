using Inovabens.Core;
using Inovabens.Core.Admin;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Regras das configurações de integração (sem banco).</summary>
public class ConfiguracoesRegrasTests
{
    private static ItemConfiguracao Item(string chave) => CatalogoConfiguracoes.Item(chave)!;

    [Fact]
    public void Todo_item_pertence_a_um_grupo_e_as_chaves_sao_unicas()
    {
        var grupos = CatalogoConfiguracoes.Grupos.Select(g => g.Codigo).ToHashSet();
        Assert.All(CatalogoConfiguracoes.Itens, i => Assert.Contains(i.Grupo, grupos));
        Assert.Equal(CatalogoConfiguracoes.Itens.Count, CatalogoConfiguracoes.Itens.Select(i => i.Chave).Distinct().Count());
        Assert.All(CatalogoConfiguracoes.Itens, i => Assert.True(i.Chave.Length <= 80));
    }

    [Theory]
    [InlineData("r2.url_publica", " https://fotos.inovabens.com.br/ ", "https://fotos.inovabens.com.br")]
    [InlineData("site.email_contato", "Contato@Inovabens.com.br", "contato@inovabens.com.br")]
    [InlineData("site.whatsapp", "(17) 99999-0000", "17999990000")]
    [InlineData("portal_olx.ativo", "TRUE", "true")]
    [InlineData("portal_olx.ativo", "qualquer", "false")]
    [InlineData("magnific.api_key", "  abc  ", "abc")]
    [InlineData("r2.bucket", "   ", "")]
    public void Normaliza_conforme_o_tipo(string chave, string valor, string esperado) =>
        Assert.Equal(esperado, ConfiguracoesService.Normalizar(Item(chave), valor));

    [Theory]
    [InlineData("r2.url_publica", "fotos.inovabens.com.br")]
    [InlineData("r2.endpoint", "ftp://x.com")]
    [InlineData("site.email_contato", "sem-arroba")]
    [InlineData("site.whatsapp", "1234")]
    [InlineData("google_drive.credenciais_json", "{\"type\":\"authorized_user\"}")]
    [InlineData("google_drive.credenciais_json", "não é json")]
    public void Recusa_valor_invalido(string chave, string valor) =>
        Assert.Throws<RegraNegocioException>(() => ConfiguracoesService.Normalizar(Item(chave), valor));

    [Fact]
    public void Chave_de_conta_de_servico_completa_e_aceita()
    {
        const string json = """{"type":"service_account","client_email":"x@y.iam.gserviceaccount.com","private_key":"-----BEGIN PRIVATE KEY-----"}""";
        Assert.Equal(json, ConfiguracoesService.Normalizar(Item("google_drive.credenciais_json"), json));
    }

    [Fact]
    public void Valor_da_tela_vale_por_cima_do_ambiente_e_o_padrao_vale_sem_nenhum()
    {
        var ambiente = CatalogoConfiguracoes.DoAmbiente(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MAGNIFIC_API_KEY"] = "do-ambiente", ["GOOGLE_CLIENT_ID"] = "id", ["GOOGLE_CLIENT_SECRET"] = "s",
        }).Build());
        Assert.Equal("do-ambiente", ambiente.MagnificApiKey);
        Assert.False(ambiente.GoogleConfigurado); // falta a URI de redirecionamento
        Assert.Equal("inovabens-site", ambiente.R2Bucket);
        Assert.Equal("https://www.inovabens.com.br", ambiente.ImobibrasilSite);

        var efetiva = ambiente.Com(new Dictionary<string, string>
        {
            ["magnific.api_key"] = "da-tela", ["google_agenda.redirect_uri"] = "https://x/cb", ["r2.url_publica"] = "https://fotos.x/",
        });
        Assert.Equal("da-tela", efetiva.MagnificApiKey);
        Assert.True(efetiva.GoogleConfigurado);
        Assert.Equal("https://fotos.x", efetiva.R2UrlPublica);
        Assert.Equal("do-ambiente", ambiente.MagnificApiKey); // o objeto anterior não muda
    }
}

/// <summary>Salvar pela tela, cifrar segredos e trocar o e-mail do administrador, num PostgreSQL real.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class ConfiguracoesIntegracaoTests
{
    private const string Senha = "Trocar@123456";

    private static (ServiceProvider Sp, IServiceScope Escopo, InovabensOpcoes Op) Montar(Dictionary<string, string?>? extra = null)
    {
        var valores = new Dictionary<string, string?> { ["DATABASE_URL"] = BancoFixture.Url };
        foreach (var (k, v) in extra ?? []) valores[k] = v;
        var op = InovabensOpcoes.De(new ConfigurationBuilder().AddInMemoryCollection(valores).Build(), producao: false);
        var sp = new ServiceCollection().AddLogging().AddInovabensCore(op).BuildServiceProvider();
        var escopo = sp.CreateScope();
        escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        return (sp, escopo, op);
    }

    [Fact]
    public async Task Segredo_salvo_fica_cifrado_vale_na_hora_e_ao_remover_volta_o_do_ambiente()
    {
        var (sp, escopo, op) = Montar(new() { ["GOOGLE_MAPS_API_KEY"] = "chave-do-ambiente" });
        await using var _ = sp;
        using var __ = escopo;
        var s = escopo.ServiceProvider;
        var admin = (await s.GetRequiredService<AuthService>().LoginAsync("admin@inovabens.com.br", Senha, null)).Usuario!;
        var cfg = s.GetRequiredService<ConfiguracoesService>();
        var fabrica = s.GetRequiredService<IDbContextFactory<InovabensDb>>();
        try
        {
            Assert.Equal("chave-do-ambiente", op.Integracoes.GoogleMapsApiKey);
            await cfg.SalvarAsync(admin, CatalogoConfiguracoes.GoogleMaps, new Dictionary<string, string> { ["google_maps.api_key"] = "chave-da-tela" });
            Assert.Equal("chave-da-tela", op.Integracoes.GoogleMapsApiKey);

            await using (var db = await fabrica.CreateDbContextAsync())
            {
                var linha = await db.Configuracoes.AsNoTracking().SingleAsync(c => c.Chave == "google_maps.api_key");
                Assert.True(linha.Cifrado);
                Assert.DoesNotContain("chave-da-tela", linha.Valor);
                var auditoria = await db.AuditLog.AsNoTracking().Where(a => a.Entidade == "configuracoes").Select(a => a.ValoresDepois).ToListAsync();
                Assert.Contains(auditoria, a => a!.Contains("google_maps.api_key"));
                Assert.All(auditoria, a => Assert.DoesNotContain("chave-da-tela", a));
            }

            // a tela nunca recebe o segredo de volta
            var item = (await cfg.ListarAsync(admin)).Single(i => i.Item.Chave == "google_maps.api_key");
            Assert.Equal(OrigemConfig.Tela, item.Origem);
            Assert.Null(item.Valor);

            // outra instância (nova subida) lê o salvo
            var (sp2, escopo2, op2) = Montar(new() { ["GOOGLE_MAPS_API_KEY"] = "chave-do-ambiente" });
            await using (sp2)
            using (escopo2)
            {
                await escopo2.ServiceProvider.GetRequiredService<ConfiguracoesService>().CarregarAsync();
                Assert.Equal("chave-da-tela", op2.Integracoes.GoogleMapsApiKey);
            }

            await cfg.SalvarAsync(admin, CatalogoConfiguracoes.GoogleMaps, new Dictionary<string, string> { ["google_maps.api_key"] = "" });
            Assert.Equal("chave-do-ambiente", op.Integracoes.GoogleMapsApiKey);
        }
        finally
        {
            await using var db = await fabrica.CreateDbContextAsync();
            await db.Configuracoes.Where(c => c.Chave == "google_maps.api_key").ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Recusa_chave_de_outro_grupo_e_usuario_sem_permissao()
    {
        var (sp, escopo, _) = Montar();
        await using var _ = sp;
        using var __ = escopo;
        var s = escopo.ServiceProvider;
        var auth = s.GetRequiredService<AuthService>();
        var admin = (await auth.LoginAsync("admin@inovabens.com.br", Senha, null)).Usuario!;
        var corretor = (await auth.LoginAsync("carla@inovabens.com.br", Senha, null)).Usuario!;
        var cfg = s.GetRequiredService<ConfiguracoesService>();

        await Assert.ThrowsAsync<RegraNegocioException>(() =>
            cfg.SalvarAsync(admin, CatalogoConfiguracoes.Site, new Dictionary<string, string> { ["magnific.api_key"] = "x" }));
        await Assert.ThrowsAsync<AcessoNegadoException>(() => cfg.ListarAsync(corretor));
        await Assert.ThrowsAsync<AcessoNegadoException>(() =>
            cfg.SalvarAsync(corretor, CatalogoConfiguracoes.Site, new Dictionary<string, string> { ["site.creci"] = "J-1" }));
    }

    [Fact]
    public async Task Troca_o_email_de_login_do_administrador_principal()
    {
        var (sp, escopo, _) = Montar();
        await using var _ = sp;
        using var __ = escopo;
        var s = escopo.ServiceProvider;
        var admin = (await s.GetRequiredService<AuthService>().LoginAsync("admin@inovabens.com.br", Senha, null)).Usuario!;
        var cfg = s.GetRequiredService<ConfiguracoesService>();
        var fabrica = s.GetRequiredService<IDbContextFactory<InovabensDb>>();
        var sufixo = Guid.NewGuid().ToString("N")[..8];

        // um administrador só deste teste vira o principal (os demais testes entram com admin@inovabens.com.br)
        Guid perfilAdmin;
        await using (var db = await fabrica.CreateDbContextAsync())
            perfilAdmin = await db.Perfis.Where(p => p.Nome == PerfisService.PerfilAdmin).Select(p => p.Id).SingleAsync();
        var id = await s.GetRequiredService<UsuariosService>().CriarAsync(admin, new UsuarioForm
        {
            Nome = "Admin principal " + sufixo, Email = $"principal-{sufixo}@inovabens.com.br", Senha = Senha, PerfilId = perfilAdmin,
        });
        try
        {
            await using (var db = await fabrica.CreateDbContextAsync())
            {
                db.Configuracoes.Add(new Inovabens.Core.Dominio.Configuracao { Chave = ConfiguracoesService.ChaveAdministrador, Valor = id.ToString() });
                await db.SaveChangesAsync();
            }
            Assert.Equal(id, (await cfg.AdministradorAsync(admin))!.Id);

            await Assert.ThrowsAsync<RegraNegocioException>(() => cfg.DefinirEmailAdministradorAsync(admin, "carla@inovabens.com.br"));
            await Assert.ThrowsAsync<RegraNegocioException>(() => cfg.DefinirEmailAdministradorAsync(admin, "invalido"));

            await cfg.DefinirEmailAdministradorAsync(admin, $" Novo-{sufixo}@Inovabens.com.br ");
            Assert.Equal($"novo-{sufixo}@inovabens.com.br", (await cfg.AdministradorAsync(admin))!.Email);
            var r = await s.GetRequiredService<AuthService>().LoginAsync($"novo-{sufixo}@inovabens.com.br", Senha, null);
            Assert.NotNull(r.Usuario);
        }
        finally
        {
            await using var db = await fabrica.CreateDbContextAsync();
            await db.Configuracoes.Where(c => c.Chave == ConfiguracoesService.ChaveAdministrador).ExecuteDeleteAsync();
            await db.Usuarios.Where(u => u.Id == id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Ativo, false));
        }
    }
}
