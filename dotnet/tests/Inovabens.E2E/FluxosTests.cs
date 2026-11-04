using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Inovabens.E2E.Aplicacao;
using static Microsoft.Playwright.Assertions;

namespace Inovabens.E2E;

/// <summary>Fluxos principais no navegador, contra a aplicação real e um banco novo.</summary>
[Collection(AplicacaoColecao.Nome)]
[Trait("Categoria", "E2E")]
public class FluxosTests(Aplicacao app)
{
    [Fact]
    public async Task Login_protege_as_paginas_e_nao_redireciona_para_fora()
    {
        var p = await app.NovaPaginaAsync();

        // sem sessão: página interna manda para o login, guardando o destino
        await p.GotoAsync("/imoveis");
        await Expect(p).ToHaveURLAsync(new Regex("/login\\?ReturnUrl=%2Fimoveis"));

        // cabeçalhos de segurança (clickjacking, tipo de arquivo)
        var h = (await p.GotoAsync("/login"))!.Headers;
        Assert.Equal("DENY", h["x-frame-options"]);
        Assert.Equal("nosniff", h["x-content-type-options"]);
        Assert.Contains("frame-ancestors 'none'", h["content-security-policy"]);

        await p.GetByLabel("E-mail").FillAsync(AdminEmail);
        await p.GetByLabel("Senha").FillAsync("senha-errada");
        await p.Botao("Entrar").ClickAsync();
        await Expect(p.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Credenciais inválidas.");

        // ReturnUrl externo é ignorado (open redirect)
        await p.GotoAsync("/login?ReturnUrl=//exemplo.com/roubo");
        await p.GetByLabel("E-mail").FillAsync(AdminEmail);
        await p.GetByLabel("Senha").FillAsync(SenhaPadrao);
        await p.Botao("Entrar").ClickAsync();
        await p.Interativa();
        Assert.StartsWith(app.Url, p.Url);

        // sair encerra a sessão
        await p.Botao("Sair").ClickAsync();
        await Expect(p).ToHaveURLAsync(new Regex("/login"));
        await p.GotoAsync("/imoveis");
        await Expect(p).ToHaveURLAsync(new Regex("/login"));
    }

    [Fact]
    public async Task Tentativas_de_login_sao_limitadas_por_IP()
    {
        var p = await app.NovaPaginaAsync();
        // IP de documentação (RFC 5737), só deste teste; a aplicação confia no 1º proxy, como atrás do Render
        var cabecalhos = new Dictionary<string, string> { ["X-Forwarded-For"] = "203.0.113.7" };
        var status = new List<int>();
        for (var i = 0; i <= LimiteLogin; i++)
            status.Add((await p.Context.APIRequest.PostAsync("/login", new() { Headers = cabecalhos, Form = p.Context.APIRequest.CreateFormData()
                .Set("Entrada.Email", AdminEmail).Set("Entrada.Senha", "errada").Set("_handler", "login") })).Status);

        Assert.DoesNotContain(429, status.Take(LimiteLogin));
        Assert.Equal(429, status[^1]);
        // outros endereços continuam entrando normalmente
        await p.EntrarAsync(AdminEmail, SenhaPadrao);
    }

    [Fact]
    public async Task Sessao_continua_valida_depois_de_reiniciar_a_aplicacao()
    {
        // as chaves que assinam o cookie ficam no banco, não no disco do processo
        var p = await app.NovaPaginaAsync();
        await p.EntrarAsync(AdminEmail, SenhaPadrao);
        await app.ReiniciarAsync();
        await p.GotoAsync("/imoveis");
        await p.Interativa();
        await Expect(p).ToHaveURLAsync(new Regex("/imoveis$"));
        await Expect(p.Botao("Sair")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Crm_imoveis_de_interesse_pela_busca_parcial_e_observacoes()
    {
        var s = Navegacao.Sufixo();
        var p = await app.NovaPaginaAsync();
        await p.EntrarAsync(AdminEmail, SenhaPadrao);
        await p.CriarImovelAsync($"Apartamento Gonçalves {s}");
        await p.CriarImovelAsync($"Casa Outra {s}");

        await p.GotoAsync("/crm");
        await p.Interativa();
        await p.Botao("+ Novo lead").ClickAsync();
        await p.GetByLabel("Nome *").FillAsync($"Cliente E2E {s}");

        // busca parcial, sem acento: "goncalves" acha "Gonçalves"; a outra casa não aparece
        var busca = p.GetByRole(AriaRole.Combobox, new() { Name = "Imóveis de interesse" });
        await busca.PressSequentiallyAsync($"goncalves {s}");
        var opcao = p.GetByRole(AriaRole.Option).Filter(new() { HasText = $"Apartamento Gonçalves {s}" });
        await Expect(opcao).ToBeVisibleAsync();
        await Expect(p.GetByRole(AriaRole.Option).Filter(new() { HasText = $"Casa Outra {s}" })).ToHaveCountAsync(0);
        await busca.PressAsync("Enter"); // escolhe a destacada sem enviar o formulário
        await Expect(p.GetByRole(AriaRole.Button, new() { Name = $"Remover", Exact = false })).ToHaveCountAsync(1);
        await Expect(busca).ToHaveValueAsync("");

        await p.GetByLabel("Observações").FillAsync("Prefere andar alto");
        await p.Botao("Criar lead").ClickAsync();
        await p.GetByRole(AriaRole.Button).Filter(new() { HasText = $"Cliente E2E {s}" }).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Link, new() { Name = "IMV-", Exact = false })).ToHaveCountAsync(1);
        await Expect(p.GetByLabel("Observações")).ToHaveValueAsync("Prefere andar alto");
    }

    /// <summary>Consulta o ViaCEP de verdade (precisa de internet).</summary>
    [Fact]
    public async Task Pessoa_cep_completa_endereco_editavel_e_avisa_quando_nao_encontra()
    {
        var p = await app.NovaPaginaAsync();
        await p.EntrarAsync(AdminEmail, SenhaPadrao);
        await p.GotoAsync("/pessoas");
        await p.Interativa();
        await p.Botao("+ Nova pessoa").ClickAsync();

        var cep = p.GetByLabel("CEP");
        await cep.PressSequentiallyAsync("01001000");
        await Expect(cep).ToHaveValueAsync("01001-000");
        await Expect(p.GetByLabel("Logradouro")).ToHaveValueAsync("Praça da Sé");
        await Expect(p.GetByLabel("Cidade")).ToHaveValueAsync("São Paulo");
        await Expect(p.GetByLabel("UF")).ToHaveValueAsync("SP");
        await p.GetByLabel("Logradouro").FillAsync("Praça da Sé, lado ímpar"); // continua editável
        await Expect(p.GetByLabel("Logradouro")).ToHaveValueAsync("Praça da Sé, lado ímpar");

        await cep.FillAsync("");
        await cep.PressSequentiallyAsync("99999999");
        await Expect(p.GetByText("Cep não encontrado")).ToBeVisibleAsync();
        await Expect(p.GetByLabel("Logradouro")).ToHaveValueAsync("Praça da Sé, lado ímpar");
    }

    [Fact]
    public async Task Imovel_checklist_financeiro_e_exportacoes()
    {
        var s = Navegacao.Sufixo();
        var p = await app.NovaPaginaAsync();
        await p.EntrarAsync(AdminEmail, SenhaPadrao);
        await p.CriarImovelAsync($"Casa E2E {s}");

        // despesa mensal começando neste mês: entra no checklist do mês
        await p.Botao("Financeiro").ClickAsync();
        await p.Botao("Cadastrar despesa").ClickAsync();
        await p.GetByLabel("Categoria").SelectOptionAsync(new SelectOptionValue { Label = "IPTU" });
        await p.GetByLabel("Descrição").FillAsync($"IPTU {s}");
        await p.GetByLabel("Valor previsto (R$)").FillAsync("350.00");
        var hoje = DateTime.Today;
        await p.GetByLabel("Início").FillAsync(new DateTime(hoje.Year, hoje.Month, 1).ToString("yyyy-MM-dd"));
        await p.Botao("Salvar").ClickAsync();

        var linha = p.GetByRole(AriaRole.Row).Filter(new() { HasText = $"IPTU {s}" }).First;
        await Expect(linha).ToBeVisibleAsync();

        // REGRA: Não Pago exige justificativa (mín. 10 caracteres)
        await linha.GetByRole(AriaRole.Button, new() { Name = "Não pago" }).ClickAsync();
        var justificativa = p.GetByLabel("Justificativa (obrigatória)");
        await justificativa.FillAsync("curta");
        await p.Botao("Confirmar").ClickAsync();
        await Expect(p.GetByText("Justificativa obrigatória")).ToBeVisibleAsync();
        await justificativa.FillAsync("Inquilino atrasou o repasse");
        await p.Botao("Confirmar").ClickAsync();
        await Expect(p.GetByText("“Inquilino atrasou o repasse”")).ToBeVisibleAsync();

        // exportações: ficha financeira (aba Financeiro) e ficha do imóvel
        var (nomePdf, pdf) = await p.BaixarAsync(p.GetByRole(AriaRole.Link, new() { Name = "Ficha financeira PDF" }));
        Assert.EndsWith(".pdf", nomePdf);
        Assert.Equal("%PDF"u8.ToArray(), pdf);
        var (nomeXlsx, xlsx) = await p.BaixarAsync(p.GetByRole(AriaRole.Link, new() { Name = "Ficha Excel" }));
        Assert.EndsWith(".xlsx", nomeXlsx);
        Assert.Equal("PK"u8.ToArray(), xlsx[..2]); // .xlsx é um zip
    }

    [Fact]
    public async Task Dois_fatores_ativado_pela_conta_passa_a_ser_exigido_no_login()
    {
        var s = Navegacao.Sufixo();
        var email = $"dois.fatores.{s}@inovabens.com.br";
        var senha = "SenhaForte#2026";
        var admin = await app.NovaPaginaAsync();
        await admin.EntrarAsync(AdminEmail, SenhaPadrao);
        await admin.CriarUsuarioAsync($"Usuária 2FA {s}", email, senha, "Financeiro");

        var p = await app.NovaPaginaAsync();
        await p.EntrarAsync(email, senha);
        await p.GotoAsync("/conta");
        await p.Interativa();
        await p.Botao("Configurar 2FA").ClickAsync();
        var segredo = (await p.Locator("code.segredo").TextContentAsync())!.Trim();
        await p.GetByLabel("Código").FillAsync("000000");
        await p.Botao("Ativar").ClickAsync();
        await Expect(p.GetByText("Código inválido")).ToBeVisibleAsync();
        await p.GetByLabel("Código").FillAsync(Navegacao.CodigoTotp(segredo));
        await p.Botao("Ativar").ClickAsync();
        await Expect(p.GetByText("Ativa", new() { Exact = true })).ToBeVisibleAsync();
        await p.Botao("Sair").ClickAsync();

        // senha certa sozinha não basta; código errado é recusado; o certo entra
        await p.GetByLabel("E-mail").FillAsync(email);
        await p.GetByLabel("Senha").FillAsync(senha);
        await p.Botao("Entrar").ClickAsync();
        await Expect(p.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Informe o código do aplicativo autenticador.");
        await p.GetByLabel("Código 2FA").FillAsync("123456" == Navegacao.CodigoTotp(segredo) ? "654321" : "123456");
        await p.GetByLabel("Senha").FillAsync(senha);
        await p.Botao("Entrar").ClickAsync();
        await Expect(p.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Código de verificação inválido.");

        // o código usado na ativação já foi consumido: entra com o do próximo intervalo
        await Navegacao.ProximoCodigoAsync();
        var codigo = Navegacao.CodigoTotp(segredo);
        await p.EntrarAsync(email, senha, codigo);
        await Expect(p.Botao("Sair")).ToBeVisibleAsync();

        // o mesmo código não serve para um segundo login (reuso/replay)
        await p.Botao("Sair").ClickAsync();
        await p.GetByLabel("E-mail").FillAsync(email);
        await p.GetByLabel("Senha").FillAsync(senha);
        await p.Botao("Entrar").ClickAsync();
        await p.GetByLabel("Código 2FA").FillAsync(codigo);
        await p.GetByLabel("Senha").FillAsync(senha);
        await p.Botao("Entrar").ClickAsync();
        await Expect(p.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Código de verificação inválido.");
    }

    [Fact]
    public async Task Corretor_nao_acessa_imovel_de_outro_nem_pela_tela_nem_pelo_download()
    {
        var s = Navegacao.Sufixo();
        var admin = await app.NovaPaginaAsync();
        await admin.EntrarAsync(AdminEmail, SenhaPadrao);
        var doAdmin = await admin.CriarImovelAsync($"Imóvel do admin {s}");
        var email = $"corretor.{s}@inovabens.com.br";
        await admin.CriarUsuarioAsync($"Corretor {s}", email, "SenhaForte#2026", "Corretor");

        var c = await app.NovaPaginaAsync();
        await c.EntrarAsync(email, "SenhaForte#2026");
        var proprio = await c.CriarImovelAsync($"Imóvel do corretor {s}");

        // a lista só mostra o próprio imóvel
        await c.GotoAsync("/imoveis");
        await c.Interativa();
        await Expect(c.GetByText($"Imóvel do corretor {s}")).ToBeVisibleAsync();
        await Expect(c.GetByText($"Imóvel do admin {s}")).ToHaveCountAsync(0);

        // abrir pelo endereço direto não mostra a ficha
        await c.GotoAsync($"/imoveis/{doAdmin}");
        await c.Interativa();
        await Expect(c.GetByText("Sem acesso a este imóvel.")).ToBeVisibleAsync();
        await Expect(c.GetByRole(AriaRole.Heading, new() { Name = $"Imóvel do admin {s}" })).ToHaveCountAsync(0);

        // download direto com a sessão do corretor: proibido no alheio, liberado no próprio
        var alheio = await c.Context.APIRequest.GetAsync($"/imoveis/{doAdmin}/ficha.pdf", new() { MaxRedirects = 0 });
        Assert.Equal((int)HttpStatusCode.Forbidden, alheio.Status);
        var meu = await c.Context.APIRequest.GetAsync($"/imoveis/{proprio}/ficha.pdf", new() { MaxRedirects = 0 });
        Assert.Equal((int)HttpStatusCode.OK, meu.Status);

        // sem sessão: o download não sai
        var anonimo = await app.NovaPaginaAsync();
        var semSessao = await anonimo.Context.APIRequest.GetAsync($"/imoveis/{proprio}/ficha.pdf", new() { MaxRedirects = 0 });
        Assert.NotEqual((int)HttpStatusCode.OK, semSessao.Status);
    }
}
