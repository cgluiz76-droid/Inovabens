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
        var opcao = p.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Option).Filter(new() { HasText = $"Apartamento Gonçalves {s}" });
        await Expect(opcao).ToBeVisibleAsync();
        await Expect(p.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Option).Filter(new() { HasText = $"Casa Outra {s}" })).ToHaveCountAsync(0);
        await busca.PressAsync("Enter"); // escolhe a destacada sem enviar o formulário
        await Expect(p.GetByRole(AriaRole.Button, new() { Name = $"Remover", Exact = false })).ToHaveCountAsync(1);
        await Expect(busca).ToHaveValueAsync("");

        await p.GetByLabel("Observações").FillAsync("Prefere andar alto");
        await p.Botao("Criar lead").ClickAsync();
        await p.GetByRole(AriaRole.Button).Filter(new() { HasText = $"Cliente E2E {s}" }).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Link, new() { Name = "IMV-", Exact = false })).ToHaveCountAsync(1);
        await Expect(p.GetByLabel("Observações")).ToHaveValueAsync("Prefere andar alto");
    }

    [Fact]
    public async Task Crm_arrastar_cartao_muda_de_coluna_e_de_posicao()
    {
        var s = Navegacao.Sufixo();
        var p = await app.NovaPaginaAsync();
        await p.EntrarAsync(AdminEmail, SenhaPadrao);
        await p.GotoAsync("/crm");
        await p.Interativa();
        foreach (var n in new[] { "Um", "Dois" })
        {
            await p.Botao("+ Novo lead").ClickAsync();
            await p.GetByLabel("Nome *").FillAsync($"Arraste {n} {s}");
            await p.Botao("Criar lead").ClickAsync();
            await Expect(p.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
        }

        var colunas = p.Locator(".kanban .coluna");
        ILocator Cartao(string n) => p.Locator(".cartao-lead").Filter(new() { HasText = $"Arraste {n} {s}" });
        await Expect(colunas.First.Locator(".cartao-lead").Filter(new() { HasText = $"Arraste Um {s}" })).ToHaveCountAsync(1);

        // mudar de estágio pede o próximo passo (vai para a agenda)
        async Task InformarProximoPasso()
        {
            var dlg = p.GetByRole(AriaRole.Dialog);
            await dlg.GetByLabel("O que fazer *").FillAsync("Ligar para saber a opinião");
            await dlg.GetByRole(AriaRole.Button, new() { Name = "Mover", Exact = true }).ClickAsync();
            await Expect(dlg).ToHaveCountAsync(0);
        }

        // "Um" vai da 1ª para a 2ª coluna
        await Cartao("Um").DragToAsync(colunas.Nth(1));
        await InformarProximoPasso();
        await Expect(colunas.Nth(1).Locator(".cartao-lead").Filter(new() { HasText = $"Arraste Um {s}" })).ToHaveCountAsync(1);
        await Expect(colunas.First.Locator(".cartao-lead").Filter(new() { HasText = $"Arraste Um {s}" })).ToHaveCountAsync(0);

        // "Dois" solto sobre "Um": fica antes dele na 2ª coluna
        await Cartao("Dois").DragToAsync(Cartao("Um"));
        await InformarProximoPasso();
        var nomes = colunas.Nth(1).Locator(".cartao-lead .forte");
        await Expect(nomes.Filter(new() { HasText = s })).ToHaveTextAsync([$"Arraste Dois {s}", $"Arraste Um {s}"]);
        await Expect(Cartao("Um").Locator(".situacao")).ToHaveTextAsync("Agendado");
        if (Environment.GetEnvironmentVariable("E2E_CAPTURA_CRM") is { } pasta)
            await p.Locator(".kanban").ScreenshotAsync(new() { Path = Path.Combine(pasta, "arrastar.png") });
    }

    [Fact]
    public async Task Crm_acao_com_proximo_passo_minhas_atividades_e_interessados_do_imovel()
    {
        var s = Navegacao.Sufixo();
        var p = await app.NovaPaginaAsync();
        await p.EntrarAsync(AdminEmail, SenhaPadrao);
        var imovel = await p.CriarImovelAsync($"Casa Interessados {s}");

        // ficha do imóvel → Interessados → novo lead já com o imóvel de interesse
        await p.GotoAsync($"/imoveis/{imovel}");
        await p.Interativa();
        await p.Botao("Interessados").ClickAsync();
        await Expect(p.GetByText("Nenhum lead interessado neste imóvel.")).ToBeVisibleAsync();
        await p.GetByRole(AriaRole.Link, new() { Name = "+ Lead para este imóvel" }).ClickAsync();
        await p.Interativa();
        var dlg = p.GetByRole(AriaRole.Dialog);
        await dlg.GetByLabel("Nome *").FillAsync($"Cliente Passo {s}");
        await Expect(dlg.GetByRole(AriaRole.Button, new() { Name = "Remover", Exact = false })).ToHaveCountAsync(1);
        await p.Botao("Criar lead").ClickAsync();
        await Expect(dlg).ToHaveCountAsync(0);

        // o funil está filtrado pelo imóvel; o cartão novo não tem próximo passo
        var cartao = p.Locator(".cartao-lead").Filter(new() { HasText = $"Cliente Passo {s}" });
        await Expect(p.Locator(".cartao-lead")).ToHaveCountAsync(1);
        await Expect(cartao.Locator(".situacao")).ToHaveTextAsync("Sem próximo passo");

        // registrar ação: sem o próximo passo não grava; com ele, vai para a agenda
        await cartao.Locator(".link-cartao").ClickAsync();
        await dlg.GetByPlaceholder("O que aconteceu?", new() { Exact = false }).FillAsync("Primeiro contato por telefone");
        await dlg.Botao("Registrar").ClickAsync();
        await Expect(dlg.GetByText("Informe o próximo passo")).ToBeVisibleAsync();
        await dlg.GetByRole(AriaRole.Button, new() { Name = "Visita", Exact = false }).ClickAsync();
        await dlg.GetByLabel("O que fazer *").FillAsync($"Visita guiada {s}");
        await dlg.Botao("Registrar").ClickAsync();
        await Expect(dlg.Locator("li").Filter(new() { HasText = $"Visita guiada {s}" })).ToBeVisibleAsync(); // em "Atividades pendentes"
        if (Environment.GetEnvironmentVariable("E2E_CAPTURA_CRM") is { } pasta)
            await dlg.ScreenshotAsync(new() { Path = Path.Combine(pasta, "lead.png") });
        await dlg.GetByRole(AriaRole.Button, new() { Name = "Fechar", Exact = false }).First.ClickAsync();
        await Expect(cartao.Locator(".situacao")).ToHaveTextAsync("Agendado");
        if (Environment.GetEnvironmentVariable("E2E_CAPTURA_CRM") is { } pasta2)
            await p.Locator(".kanban").ScreenshotAsync(new() { Path = Path.Combine(pasta2, "funil.png") });

        // minhas atividades: concluir pede o resultado e o próximo passo (aqui pulado com motivo)
        await p.GotoAsync("/atividades");
        await p.Interativa();
        var item = p.Locator(".atividade").Filter(new() { HasText = $"Visita guiada {s}" });
        await Expect(item).ToHaveCountAsync(1);
        if (Environment.GetEnvironmentVariable("E2E_CAPTURA_CRM") is { } pasta3)
            await p.ScreenshotAsync(new() { Path = Path.Combine(pasta3, "atividades.png"), FullPage = true });
        await item.GetByRole(AriaRole.Button, new() { Name = "✔ Concluir" }).ClickAsync();
        await dlg.GetByPlaceholder("Resultado", new() { Exact = false }).FillAsync("Cliente gostou, vai pensar");
        await dlg.GetByLabel("Não agendar agora").CheckAsync();
        await dlg.GetByLabel("Por que não agendar? *").FillAsync("Cliente vai conversar com a família");
        await dlg.Botao("Concluir").ClickAsync();
        await Expect(dlg).ToHaveCountAsync(0);
        await Expect(item).ToHaveCountAsync(0);

        // a ficha do imóvel lista o interessado
        await p.GotoAsync($"/imoveis/{imovel}");
        await p.Interativa();
        await p.Botao("Interessados").ClickAsync();
        await Expect(p.GetByRole(AriaRole.Link, new() { Name = $"Cliente Passo {s}" })).ToBeVisibleAsync();
        await Expect(p.GetByText("Sem próximo passo")).ToBeVisibleAsync();
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
        // o filtro do extrato também tem "Categoria": usa o campo do modal
        await p.GetByRole(AriaRole.Dialog).GetByLabel("Categoria").SelectOptionAsync(new SelectOptionValue { Label = "IPTU" });
        await p.GetByLabel("Descrição").FillAsync($"IPTU {s}");
        await p.GetByLabel("Valor previsto (R$)").FillAsync("350.00");
        var hoje = DateTime.Today;
        await p.GetByLabel("Início").FillAsync(new DateTime(hoje.Year, hoje.Month, 1).ToString("yyyy-MM-dd"));
        await p.Botao("Salvar").ClickAsync();

        // a despesa aparece no checklist e também no extrato ("a pagar"): usa a linha do checklist
        var checklist = p.Locator("section").Filter(new() { Has = p.GetByRole(AriaRole.Heading, new() { Name = "Despesas recorrentes (OPEX) — checklist mensal" }) });
        var linha = checklist.GetByRole(AriaRole.Row).Filter(new() { HasText = $"IPTU {s}" });
        await Expect(linha).ToBeVisibleAsync();

        // REGRA: Não Pago exige justificativa (mín. 10 caracteres)
        await linha.GetByRole(AriaRole.Button, new() { Name = "Não pago" }).ClickAsync();
        var justificativa = p.GetByLabel("Justificativa (obrigatória)");
        await justificativa.FillAsync("curta");
        await p.Botao("Confirmar").ClickAsync();
        await Expect(p.GetByText("Justificativa obrigatória")).ToBeVisibleAsync();
        await justificativa.FillAsync("Inquilino atrasou o repasse");
        await p.Botao("Confirmar").ClickAsync();
        await Expect(linha.GetByText("“Inquilino atrasou o repasse”")).ToBeVisibleAsync();

        // extrato (livro caixa): a despesa não paga fica "a pagar", fora do saldo; a receita entra no saldo
        var extrato = p.Locator("section").Filter(new() { Has = p.GetByRole(AriaRole.Heading, new() { Name = "Extrato do imóvel (livro caixa)" }) });
        await Expect(extrato.GetByRole(AriaRole.Row).Filter(new() { HasText = $"IPTU {s}" })).ToContainTextAsync("Não pago");
        await p.GetByLabel("Bruto").FillAsync("1000");
        await p.Botao("Lançar").ClickAsync();
        await Expect(extrato.GetByRole(AriaRole.Row).Filter(new() { HasText = "Receita de venda" })).ToContainTextAsync("R$ 1.000,00");
        await Expect(extrato.GetByRole(AriaRole.Row).Filter(new() { HasText = "Total a pagar" })).ToContainTextAsync("saldo projetado R$ 650,00");
        var (nomeExtrato, pdfExtrato) = await p.BaixarAsync(p.GetByRole(AriaRole.Link, new() { Name = "Extrato PDF" }));
        Assert.EndsWith(".pdf", nomeExtrato);
        Assert.Equal("%PDF"u8.ToArray(), pdfExtrato);
        if (Environment.GetEnvironmentVariable("E2E_CAPTURA_EXTRATO") is { } capturaExtrato)
            await extrato.ScreenshotAsync(new() { Path = capturaExtrato });

        // exportações: ficha financeira (aba Financeiro) e ficha do imóvel
        var (nomePdf, pdf) = await p.BaixarAsync(p.GetByRole(AriaRole.Link, new() { Name = "Ficha financeira PDF" }));
        Assert.EndsWith(".pdf", nomePdf);
        Assert.Equal("%PDF"u8.ToArray(), pdf);
        var (nomeXlsx, xlsx) = await p.BaixarAsync(p.GetByRole(AriaRole.Link, new() { Name = "Ficha Excel" }));
        Assert.EndsWith(".xlsx", nomeXlsx);
        Assert.Equal("PK"u8.ToArray(), xlsx[..2]); // .xlsx é um zip
    }

    [Fact]
    public async Task Botao_ver_no_site_abre_a_previa_publica_do_imovel()
    {
        var s = Navegacao.Sufixo();
        var p = await app.NovaPaginaAsync();
        await p.EntrarAsync(AdminEmail, SenhaPadrao);
        await p.CriarImovelAsync($"Casa Site {s}");

        var previa = await p.Context.RunAndWaitForPageAsync(() => p.GetByRole(AriaRole.Link, new() { Name = "Ver no site" }).ClickAsync());
        await Expect(previa.GetByRole(AriaRole.Heading, new() { Name = $"Casa Site {s}", Level = 1 })).ToBeVisibleAsync();
        await Expect(previa.GetByText("Pré-visualização")).ToBeVisibleAsync();
        // sem fotos públicas e sem "divulgar no site": a faixa avisa
        await Expect(previa.GetByText("Nenhuma foto marcada como pública.")).ToBeVisibleAsync();
        await Expect(previa.GetByRole(AriaRole.Link, new() { Name = "Conversar no WhatsApp" })).ToHaveAttributeAsync("href", new System.Text.RegularExpressions.Regex(@"^https://wa\.me/5517981850171\?text="));
        if (Environment.GetEnvironmentVariable("E2E_CAPTURA") is { } arquivo)
            await previa.ScreenshotAsync(new() { Path = arquivo, FullPage = true });
    }

    [Fact]
    public async Task Site_tem_logo_e_menus_navegaveis_e_simulador_com_regras_da_caixa()
    {
        var s = Navegacao.Sufixo();
        var titulo = $"Casa Vitrine {s}";
        var p = await app.NovaPaginaAsync();
        await p.EntrarAsync(AdminEmail, SenhaPadrao);
        var id = await p.CriarImovelAsync(titulo);
        await p.GetByLabel("Situação").SelectOptionAsync("DISPONIVEL");
        await p.GetByLabel("Preço de venda (R$)").FillAsync("500000");
        await p.GetByLabel("Divulgar no site").CheckAsync();
        await p.Botao("Salvar").ClickAsync();
        await Expect(p.GetByText("Dados salvos.")).ToBeVisibleAsync();

        // a ficha no site não mostra o simulador; o botão "Simular financiamento" abre a página dele em nova aba
        await p.GotoAsync($"/site/imovel/{id}");
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Simule o financiamento" })).ToHaveCountAsync(0);
        var aba = await p.Context.RunAndWaitForPageAsync(() => p.GetByRole(AriaRole.Link, new() { Name = "Simular financiamento" }).ClickAsync());
        await aba.WaitForLoadStateAsync();
        await Expect(aba).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"/site/imovel/{id}/simulador$"));
        await Expect(aba.GetByRole(AriaRole.Heading, new() { Name = titulo, Level = 1 })).ToBeVisibleAsync();
        await aba.CloseAsync();

        // página do simulador: SAC com 20% de entrada, 420 meses, 11,49% a.a. nominal (Caixa, balcão)
        await p.GotoAsync($"/site/imovel/{id}/simulador");
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Simule o financiamento" })).ToBeVisibleAsync();
        await Expect(p.GetByText("Isto é uma simulação.")).ToBeVisibleAsync();
        await Expect(p.GetByText("depende de análise de crédito e aprovação da instituição financeira", new() { Exact = false })).ToBeVisibleAsync();
        await Expect(p.Locator("#rPrimeira")).ToHaveTextAsync("R$ 4.782,38");
        await Expect(p.Locator("#rFinanciado")).ToHaveTextAsync("R$ 400.000,00");
        // Price: a Caixa financia até 70%, então a entrada sobe para 30% e a parcela fica fixa
        await p.GetByText("Price", new() { Exact = true }).ClickAsync();
        await Expect(p.Locator("#rPrimeiraRotulo")).ToHaveTextAsync("Parcela fixa");
        await Expect(p.Locator("#rFinanciado")).ToHaveTextAsync("R$ 350.000,00");
        await Expect(p.Locator("#simEntrada")).ToHaveValueAsync("150000");
        // idade + prazo até 80 anos e 6 meses
        await p.Locator("#simIdade").FillAsync("60");
        await Expect(p.Locator("#simPrazoTxt")).ToHaveTextAsync("20 anos e 6 meses (246 meses)");

        // logo volta ao início; "Comprar" lista todos à venda; o cartão abre a ficha
        await p.GetByRole(AriaRole.Banner).GetByRole(AriaRole.Link, new() { Name = "Inovabens: página inicial" }).ClickAsync();
        await Expect(p).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/site$"));
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Imóveis à venda" })).ToBeVisibleAsync();
        await p.GetByRole(AriaRole.Navigation, new() { Name = "Principal" }).GetByRole(AriaRole.Link, new() { Name = "Comprar" }).ClickAsync();
        await Expect(p).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(@"/site/imoveis\?finalidade=venda$"));
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Imóveis à venda", Level = 1 })).ToBeVisibleAsync();
        await p.GetByRole(AriaRole.Link, new() { Name = titulo }).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = titulo, Level = 1 })).ToBeVisibleAsync();
        // "Alugar" não mostra o imóvel que é só de venda
        await p.GetByRole(AriaRole.Navigation, new() { Name = "Principal" }).GetByRole(AriaRole.Link, new() { Name = "Alugar" }).ClickAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Imóveis para alugar", Level = 1 })).ToBeVisibleAsync();
        await Expect(p.GetByRole(AriaRole.Link, new() { Name = titulo })).ToHaveCountAsync(0);
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
