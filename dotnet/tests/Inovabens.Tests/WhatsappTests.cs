using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Inovabens.Core;
using Inovabens.Core.Agenda;
using Inovabens.Core.Comum;
using Inovabens.Core.Crm;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Leitura do aviso da Cloud API do WhatsApp (sem banco).</summary>
public class WhatsappLeituraTests
{
    [Theory]
    [InlineData("5517981850171", "1781850171")]   // celular com o 9
    [InlineData("551781850171", "1781850171")]    // como o WhatsApp às vezes manda: sem o 9
    [InlineData("(17) 98185-0171", "1781850171")]
    [InlineData("+55 17 3222-1100", "1732221100")] // fixo
    [InlineData("12", null)]
    public void Chave_do_telefone_ignora_formato_55_e_o_9(string telefone, string? chave) =>
        Assert.Equal(chave, WhatsappService.ChaveTelefone(telefone));

    [Theory]
    [InlineData("551781850171", "(17) 98185-0171")]
    [InlineData("5517981850171", "(17) 98185-0171")]
    [InlineData("551732221100", "(17) 3222-1100")]
    [InlineData("14155550100", "+14155550100")]
    public void Telefone_do_cadastro_fica_no_formato_brasileiro(string waId, string esperado) =>
        Assert.Equal(esperado, WhatsappService.FormatarTelefone(waId));

    [Fact]
    public void Le_mensagens_do_cliente_e_ecos_do_celular_e_filtra_o_numero()
    {
        using var doc = JsonDocument.Parse(Avisos.Recebida("wamid.1", "5517981850171", "Maria", "Olá") );
        var m = Assert.Single(WhatsappService.Ler(doc.RootElement));
        Assert.Equal(("wamid.1", "5517981850171", "Maria", true, "Olá"), (m.Id, m.Telefone, m.Nome, m.Entrada, m.Texto));
        Assert.Empty(WhatsappService.Ler(doc.RootElement, numeroPermitido: "outro-numero"));
        Assert.Single(WhatsappService.Ler(doc.RootElement, numeroPermitido: Avisos.NumeroId));

        using var eco = JsonDocument.Parse(Avisos.Eco("wamid.2", "5517981850171", "Já te mando as fotos"));
        var e = Assert.Single(WhatsappService.Ler(eco.RootElement));
        Assert.False(e.Entrada);
        Assert.Equal("Já te mando as fotos", e.Texto);

        using var foto = JsonDocument.Parse(Avisos.Foto("wamid.3", "5517981850171", "fachada"));
        Assert.Equal("📷 Foto: fachada", Assert.Single(WhatsappService.Ler(foto.RootElement)).Texto);
    }
}

/// <summary>Avisos no formato da Cloud API.</summary>
internal static class Avisos
{
    public const string NumeroId = "123456789";

    private static string Envelope(string campo, string valor) => $$$"""
        {"object":"whatsapp_business_account","entry":[{"id":"waba","changes":[{"field":"{{{campo}}}","value":{
          "messaging_product":"whatsapp","metadata":{"display_phone_number":"5517900000000","phone_number_id":"{{{NumeroId}}}"},{{{valor}}}}}]}]}
        """;

    private static string Ts => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

    public static string Recebida(string id, string de, string? nome, string texto) => Envelope("messages", $$$"""
        "contacts":[{"profile":{"name":{{{JsonSerializer.Serialize(nome)}}}},"wa_id":"{{{de}}}"}],
        "messages":[{"from":"{{{de}}}","id":"{{{id}}}","timestamp":"{{{Ts}}}","type":"text","text":{"body":{{{JsonSerializer.Serialize(texto)}}}}}]
        """);

    public static string Foto(string id, string de, string legenda) => Envelope("messages", $$$"""
        "messages":[{"from":"{{{de}}}","id":"{{{id}}}","timestamp":"{{{Ts}}}","type":"image","image":{"caption":"{{{legenda}}}","id":"m1"}}]
        """);

    public static string Eco(string id, string para, string texto) => Envelope("smb_message_echoes", $$$"""
        "message_echoes":[{"from":"5517900000000","to":"{{{para}}}","id":"{{{id}}}","timestamp":"{{{Ts}}}","type":"text","text":{"body":{{{JsonSerializer.Serialize(texto)}}}}}]
        """);
}

/// <summary>Mensagens do WhatsApp viram contato, lead e alerta, contra PostgreSQL real.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class WhatsappIntegracaoTests : IAsyncLifetime
{
    private IServiceScope _escopo = default!;
    private T S<T>() where T : notnull => _escopo.ServiceProvider.GetRequiredService<T>();

    public Task InitializeAsync()
    {
        // provider compartilhado (limite de 20 do EF); a configuração do WhatsApp volta ao normal no fim
        _escopo = ExtratoComprovantesTests.Compartilhado.CreateScope();
        _escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        _anteriores = S<InovabensOpcoes>().Integracoes;
        S<InovabensOpcoes>().Integracoes = Integracoes.Vazias.Com(new Dictionary<string, string>
        {
            ["whatsapp.verify_token"] = "token-de-teste",
            ["whatsapp.app_secret"] = "segredo-de-teste",
        });
        return Task.CompletedTask;
    }

    private Integracoes _anteriores = Integracoes.Vazias;

    public Task DisposeAsync()
    {
        S<InovabensOpcoes>().Integracoes = _anteriores;
        _escopo.Dispose();
        return Task.CompletedTask;
    }

    private static string NumeroNovo() => "55179" + Random.Shared.Next(10_000_000, 99_999_999);

    private async Task<ResultadoWhatsapp> Receber(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return await S<WhatsappService>().ProcessarAsync(doc.RootElement);
    }

    private async Task<UsuarioAtual> Admin() =>
        (await S<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;

    [Fact]
    public void Webhook_aceita_assinatura_da_Meta_ou_token_do_parceiro()
    {
        var w = S<WhatsappService>();
        var corpo = Encoding.UTF8.GetBytes("{\"entry\":[]}");
        var assinatura = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("segredo-de-teste"), corpo));
        Assert.True(w.Autentico(corpo, assinatura, null));
        Assert.False(w.Autentico(corpo, "sha256=" + new string('0', 64), null));
        Assert.False(w.Autentico(Encoding.UTF8.GetBytes("{\"outro\":1}"), assinatura, null));
        Assert.True(w.Autentico(corpo, null, "token-de-teste"));
        Assert.False(w.Autentico(corpo, null, "errado"));

        Assert.Equal("123", w.Desafio("subscribe", "token-de-teste", "123"));
        Assert.Null(w.Desafio("subscribe", "errado", "123"));
    }

    [Fact]
    public async Task Mensagem_de_numero_novo_cria_contato_e_lead_na_primeira_coluna_com_imovel_sugerido()
    {
        var admin = await Admin();
        var imovelId = await S<ImoveisService>().CriarAsync(admin, new ImovelForm
        { NomeDrive = "Zap " + Guid.NewGuid().ToString("N")[..8], Titulo = "Casa do zap", Tipo = "casa" });
        string codigo;
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
            codigo = await db.Imoveis.Where(i => i.Id == imovelId).Select(i => i.Codigo).SingleAsync();

        var numero = NumeroNovo();
        var id1 = "wamid." + Guid.NewGuid().ToString("N");
        var texto = $"Olá! Tenho interesse no imóvel {codigo} (Casa do zap). Pode me passar mais informações?";
        var r = await Receber(Avisos.Recebida(id1, numero, "Maria Zap", texto));
        Assert.Equal((1, 1, 0), (r.Mensagens, r.LeadsCriados, r.Repetidas));

        // a Meta reenvia o mesmo aviso: nada duplica
        Assert.Equal((0, 0, 1), (await Receber(Avisos.Recebida(id1, numero, "Maria Zap", texto))) is var rr ? (rr.Mensagens, rr.LeadsCriados, rr.Repetidas) : default);

        // segunda mensagem e resposta enviada pelo celular: mesmo lead, só histórico
        await Receber(Avisos.Recebida("wamid." + Guid.NewGuid().ToString("N"), numero, "Maria Zap", "Ainda está disponível?"));
        await Receber(Avisos.Eco("wamid." + Guid.NewGuid().ToString("N"), numero, "Está sim! Quer visitar?"));

        var crm = S<CrmService>();
        var cartao = (await crm.PipelineAsync(admin, Funil.VENDA)).First().Leads.Single(l => l.PessoaNome == "Maria Zap" && l.Telefone!.EndsWith(numero[^4..]));
        Assert.Equal(WhatsappService.Origem, cartao.Origem);
        Assert.True(cartao.VincularImovel);
        Assert.Equal(codigo, cartao.ImovelSugerido);

        var detalhe = await crm.ObterAsync(admin, cartao.Id);
        Assert.Equal(imovelId, detalhe.ImovelSugerido?.Id);
        Assert.True(detalhe.VincularImovel);
        Assert.Equal(WhatsappService.FormatarTelefone(numero), detalhe.Pessoa.Whatsapp);
        var interacoes = await crm.InteracoesAsync(admin, cartao.Id);
        Assert.Equal(3, interacoes.Count);
        Assert.All(interacoes, i => Assert.Equal(CanalInteracao.WHATSAPP, i.Dados.Canal));
        Assert.Contains(interacoes, i => i.Dados.Resumo == "Enviada pelo celular: Está sim! Quer visitar?");
        Assert.Contains(interacoes, i => i.Dados.Resumo == "Mensagem recebida: " + texto);

        // o administrador recebe o alerta; vincular o imóvel resolve
        var alertas = S<AlertasService>();
        var alerta = (await alertas.ListarAsync(admin, false)).Single(a => a.LeadId == cartao.Id);
        Assert.Equal(TipoAlerta.NOVO_LEAD, alerta.Dados.Tipo);
        Assert.Contains(codigo, alerta.Dados.Mensagem);

        await crm.DefinirInteresseAsync(admin, cartao.Id, [imovelId]);
        Assert.DoesNotContain(await alertas.ListarAsync(admin, false), a => a.LeadId == cartao.Id);
        var depois = await crm.ObterAsync(admin, cartao.Id);
        Assert.False(depois.VincularImovel);
        Assert.Null(depois.ImovelSugerido);
    }

    [Fact]
    public async Task Contato_ja_cadastrado_e_reconhecido_mesmo_sem_o_9_e_lead_aberto_nao_duplica()
    {
        var admin = await Admin();
        var numero = NumeroNovo();                      // 55 17 9xxxxxxxx
        var cadastrado = $"({numero[2..4]}) {numero[4..9]}-{numero[9..]}";
        var crm = S<CrmService>();
        var leadId = await crm.CriarAsync(admin, new LeadForm { NovoNome = "João Cadastrado", NovoTelefone = cadastrado, Origem = "Indicação" });
        var semNove = numero[..4] + numero[5..];         // como o WhatsApp manda alguns celulares

        var r = await Receber(Avisos.Recebida("wamid." + Guid.NewGuid().ToString("N"), semNove, "Joãozinho", "Bom dia"));
        Assert.Equal(0, r.LeadsCriados);
        var interacao = Assert.Single(await crm.InteracoesAsync(admin, leadId));
        Assert.Equal("Mensagem recebida: Bom dia", interacao.Dados.Resumo);
        // lead manual continua como estava (sem aviso de vincular)
        Assert.False((await crm.ObterAsync(admin, leadId)).VincularImovel);

        // lead perdido não recebe mensagem nova: ela abre outro lead para a mesma pessoa
        var perdido = (await crm.EstagiosAsync(Funil.VENDA)).First(e => e.Tipo == TipoEstagio.PERDIDO && e.Ativo);
        await crm.MoverAsync(admin, leadId, perdido.Id, "Comprou com outra imobiliária");
        var r2 = await Receber(Avisos.Recebida("wamid." + Guid.NewGuid().ToString("N"), numero, "Joãozinho", "Tem casa nova?"));
        Assert.Equal(1, r2.LeadsCriados);
        await using var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync();
        var pessoaId = (await crm.ObterAsync(admin, leadId)).Pessoa.Id;
        Assert.Equal(2, await db.Leads.CountAsync(l => l.PessoaId == pessoaId));
        Assert.Equal(1, await db.Pessoas.CountAsync(p => p.Id == pessoaId || p.NomeRazao == "Joãozinho"));
    }

    [Fact]
    public async Task Eco_do_celular_para_quem_nao_e_lead_nao_cria_lead()
    {
        var r = await Receber(Avisos.Eco("wamid." + Guid.NewGuid().ToString("N"), NumeroNovo(), "Oi, tudo bem?"));
        Assert.Equal((1, 0), (r.Mensagens, r.LeadsCriados));
    }
}
