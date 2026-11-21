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
        var painel = p.Locator(".painel-lateral");
        await painel.GetByRole(AriaRole.Tab, new() { Name = "Imóveis", Exact = false }).ClickAsync();
        await Expect(painel.GetByRole(AriaRole.Link, new() { NameRegex = new System.Text.RegularExpressions.Regex(@"^[A-Z]{2}\d{4}") })).ToHaveCountAsync(1);
        await painel.GetByRole(AriaRole.Tab, new() { Name = "Dados" }).ClickAsync();
        await Expect(painel.GetByLabel("Observações")).ToHaveValueAsync("Prefere andar alto");
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

        // painel lateral: registrar ação sem o próximo passo não grava; com ele, vai para a agenda
        await cartao.Locator(".link-cartao").ClickAsync();
        var painel = p.Locator(".painel-lateral");
        await Expect(painel.Locator(".painel-proximo")).ToContainTextAsync("Sem próximo passo");
        await painel.Botao("📝 Registrar ação…").ClickAsync();
        await painel.GetByPlaceholder("O que aconteceu?", new() { Exact = false }).FillAsync("Primeiro contato por telefone");
        await painel.Botao("Registrar").ClickAsync();
        await Expect(painel.GetByText("Informe o próximo passo")).ToBeVisibleAsync();
        await painel.Locator(".tipos-atividade").GetByRole(AriaRole.Button, new() { Name = "Visita", Exact = false }).ClickAsync();
        await painel.GetByLabel("O que fazer *").FillAsync($"Visita guiada {s}");
        await painel.Botao("Registrar").ClickAsync();
        await Expect(painel.Locator(".painel-proximo")).ToContainTextAsync($"Visita guiada {s}");
        await Expect(painel.Locator(".linha-tempo")).ToContainTextAsync("Primeiro contato por telefone");
        await Expect(cartao.Locator(".situacao")).ToHaveTextAsync("Agendado");
        if (Environment.GetEnvironmentVariable("E2E_CAPTURA_CRM") is { } pasta)
            await p.ScreenshotAsync(new() { Path = Path.Combine(pasta, "painel.png") });

        // mudar de etapa pela barra do painel pede o próximo passo
        await painel.GetByRole(AriaRole.Button, new() { Name = "Contato Realizado" }).ClickAsync();
        await Expect(dlg).ToContainTextAsync("Manter o já agendado");
        await dlg.Botao("Mover").ClickAsync();
        await Expect(dlg).ToHaveCountAsync(0);
        await Expect(painel.Locator(".etapa.atual")).ToHaveTextAsync("Contato Realizado");
        await Expect(painel.Locator(".linha-tempo")).ToContainTextAsync("Novo Lead → Contato Realizado");
        await painel.GetByRole(AriaRole.Button, new() { Name = "Fechar", Exact = true }).ClickAsync();
        await Expect(painel).ToHaveCountAsync(0);
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

    [Fact]
    public async Task Crm_proposta_com_formas_de_pagamento_aceita_vai_para_fechamento()
    {
        var s = Navegacao.Sufixo();
        var p = await app.NovaPaginaAsync();
        await p.EntrarAsync(AdminEmail, SenhaPadrao);
        var imovel = await p.CriarImovelAsync($"Casa Proposta {s}");
        await p.GotoAsync($"crm?novo=1&imovel={imovel}");
        await p.Interativa();
        var dlg = p.GetByRole(AriaRole.Dialog);
        await dlg.GetByLabel("Nome *").FillAsync($"Cliente Proposta {s}");
        await p.Botao("Criar lead").ClickAsync();
        await Expect(dlg).ToHaveCountAsync(0);

        var cartao = p.Locator(".cartao-lead").Filter(new() { HasText = $"Cliente Proposta {s}" });
        await cartao.Locator(".link-cartao").ClickAsync();
        var painel = p.Locator(".painel-lateral");
        await painel.GetByRole(AriaRole.Tab, new() { Name = "Propostas", Exact = false }).ClickAsync();
        await Expect(painel.GetByText("Nenhuma proposta registrada.")).ToBeVisibleAsync();

        // entrada + financiamento; o total soma as partes
        await painel.Botao("+ Nova proposta").ClickAsync();
        await dlg.GetByLabel("Valor da parte 1").FillAsync("100000");
        await dlg.GetByRole(AriaRole.Group, new() { Name = "Adicionar forma de pagamento" }).Botao("+ Financiamento bancário").ClickAsync();
        await dlg.GetByLabel("Valor da parte 2").FillAsync("250000");
        await dlg.GetByLabel("Banco").FillAsync("Caixa");
        await dlg.GetByLabel("O que fazer *").FillAsync($"Levar proposta ao proprietário {s}");
        await dlg.GetByLabel("O que fazer *").BlurAsync();
        await Expect(dlg.Locator(".total-proposta")).ToContainTextAsync("350.000");
        await dlg.Botao("Registrar proposta").ClickAsync();
        await Expect(dlg).ToHaveCountAsync(0);
        var proposta = painel.Locator(".lista-propostas > li").First;
        await Expect(proposta).ToContainTextAsync("Em análise");
        await Expect(proposta).ToContainTextAsync("Financiamento bancário (Caixa)");
        await Expect(cartao.Locator(".proposta-cartao")).ToContainTextAsync("Proposta");
        await Expect(painel.Locator(".painel-proximo")).ToContainTextAsync($"Levar proposta ao proprietário {s}");

        // o administrador aceita: o lead vai para Documentação/Fechamento, com a assinatura agendada
        await proposta.Botao("✔ Aceitar").ClickAsync();
        await Expect(dlg.GetByLabel("O que fazer *")).ToHaveValueAsync("Assinatura do contrato");
        await dlg.Botao("Aceitar proposta").ClickAsync();
        await Expect(dlg).ToHaveCountAsync(0);
        await Expect(painel.Locator(".etapa.atual")).ToHaveTextAsync("Documentação/Fechamento");
        await Expect(proposta).ToContainTextAsync("Aceita");
        await Expect(cartao.Locator(".proposta-cartao")).ToContainTextAsync("✔ Aceita");
        if (Environment.GetEnvironmentVariable("E2E_CAPTURA_CRM") is { } pasta)
            await p.ScreenshotAsync(new() { Path = Path.Combine(pasta, "propostas.png") });

        // PDF padrão da proposta
        await proposta.Locator("summary", new() { HasText = "Documento" }).ClickAsync();
        var download = await p.RunAndWaitForDownloadAsync(() => proposta.GetByRole(AriaRole.Link, new() { Name = "PDF padrão" }).ClickAsync());
        Assert.EndsWith("-n1.pdf", download.SuggestedFilename);
        if (Environment.GetEnvironmentVariable("E2E_CAPTURA_CRM") is { } pasta2)
            await download.SaveAsAsync(Path.Combine(pasta2, "proposta.pdf"));
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

        // despesa em aberto (fora do saldo real) e receita já recebida
        await p.Botao("Financeiro").ClickAsync();
        var hoje = DateTime.Today.ToString("yyyy-MM-dd");
        await p.Botao("+ Nova despesa").ClickAsync();
        var modal = p.GetByRole(AriaRole.Dialog);
        await modal.GetByLabel("Vencimento").FillAsync(hoje);
        await modal.GetByLabel("Valor (R$)").FillAsync("350.00");
        await modal.GetByLabel("Descrição").FillAsync($"IPTU {s}");
        await modal.GetByLabel("Pessoa").FillAsync($"Prefeitura {s}");
        await modal.GetByLabel("Conta orçamentária").SelectOptionAsync(new SelectOptionValue { Label = "IPTU" });
        await modal.GetByLabel("Já pago (liquidado)").UncheckAsync();
        await modal.Botao("Salvar").ClickAsync();

        var aberto = p.GetByRole(AriaRole.Row).Filter(new() { HasText = $"IPTU {s}" });
        await Expect(aberto).ToContainTextAsync("a vencer");
        await Expect(aberto).ToContainTextAsync($"Prefeitura {s}");
        await Expect(p.GetByText("Nenhum título liquidado no período.")).ToBeVisibleAsync();

        await p.Botao("+ Nova receita").ClickAsync();
        await modal.GetByLabel("Vencimento").FillAsync(hoje);
        await modal.GetByLabel("Valor (R$)").FillAsync("1000");
        await modal.GetByLabel("Descrição").FillAsync($"Sinal {s}");
        await modal.GetByLabel("Conta orçamentária").SelectOptionAsync(new SelectOptionValue { Label = "Outras receitas" });
        await modal.Botao("Salvar").ClickAsync();
        var extrato = p.Locator("table.extrato");
        await Expect(extrato.GetByRole(AriaRole.Row).Filter(new() { HasText = $"Sinal {s}" })).ToContainTextAsync("R$ 1.000,00");

        // pagar o título em aberto: entra no saldo real (1.000 − 350)
        await aberto.Botao("Pagar").ClickAsync();
        await modal.Botao("Confirmar").ClickAsync();
        await Expect(extrato.GetByRole(AriaRole.Row).Filter(new() { HasText = $"IPTU {s}" })).ToContainTextAsync("R$ 350,00");
        await Expect(extrato.GetByRole(AriaRole.Row).Filter(new() { HasText = "Totais do período" })).ToContainTextAsync("R$ 650,00");
        var (nomeExtrato, pdfExtrato) = await p.BaixarAsync(p.GetByRole(AriaRole.Link, new() { Name = "Extrato PDF" }));
        Assert.EndsWith(".pdf", nomeExtrato);
        Assert.Equal("%PDF"u8.ToArray(), pdfExtrato);
        if (Environment.GetEnvironmentVariable("E2E_CAPTURA_EXTRATO") is { } capturaExtrato)
            await extrato.ScreenshotAsync(new() { Path = capturaExtrato });

        // exportações: ficha financeira (aba Investimento) e ficha do imóvel
        await p.Botao("Investimento").ClickAsync();
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

        // página do simulador: abre na Caixa, SAC com 20% de entrada, 420 meses e a taxa média do Banco Central
        // (sem a série gravada, a de referência: 12,05% efetiva ao ano = 11,43% nominal)
        await p.GotoAsync($"/site/imovel/{id}/simulador");
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Simule o financiamento" })).ToBeVisibleAsync();
        await Expect(p.GetByText("Isto é uma simulação.")).ToBeVisibleAsync();
        await Expect(p.GetByText("depende de análise de crédito e aprovação da instituição financeira", new() { Exact = false })).ToBeVisibleAsync();
        await Expect(p.Locator("#dNome")).ToHaveTextAsync("Caixa Econômica Federal");
        await Expect(p.Locator("#rPrimeira")).ToHaveTextAsync("R$ 4.762,91");
        await Expect(p.Locator("#rFinanciado")).ToHaveTextAsync("R$ 400.000,00");
        await Expect(p.Locator("#dLink")).ToHaveAttributeAsync("href", "https://simuladorhabitacao.caixa.gov.br/");

        // comparativo: 8 bancos do menor para o maior valor de parcela; o primeiro leva o selo
        var cartoes = p.Locator("#simBancos .banco-cartao");
        await Expect(cartoes).ToHaveCountAsync(8);
        await Expect(cartoes.First).ToContainTextAsync("Menor parcela");
        await Expect(cartoes.First.Locator("img")).ToHaveAttributeAsync("alt", "Sicoob");
        await Expect(cartoes.Last.Locator("img")).ToHaveAttributeAsync("alt", "Banco do Brasil");
        await Expect(p.Locator("#simBancos [aria-checked=true] img")).ToHaveAttributeAsync("alt", "Caixa Econômica Federal");
        // logos oficiais carregados (wwwroot/bancos)
        Assert.True(await p.Locator("#simBancos img").EvaluateAllAsync<bool>("imgs => imgs.every(i => i.complete && i.naturalWidth > 0)"));
        // outro banco: troca o detalhe e o botão de simulação oficial
        await cartoes.First.ClickAsync();
        await Expect(p.Locator("#dNome")).ToHaveTextAsync("Sicoob");
        await Expect(p.Locator("#rPrimeira")).ToHaveTextAsync("R$ 4.382,55");
        await Expect(p.Locator("#dLink")).ToHaveTextAsync("Simular no site do Sicoob ↗");
        await Expect(p.Locator("#simNotas")).ToContainTextAsync("Cooperativa de crédito");
        // Minha Casa, Minha Vida: imóvel de R$ 500 mil cabe na faixa 4; a renda define a faixa
        await Expect(p.Locator("#simMcmv")).ToContainTextAsync("Faixa 4");
        await p.Locator("#simMcmv .banco-cartao").ClickAsync();
        await Expect(p.Locator("#dNome")).ToHaveTextAsync("Minha Casa, Minha Vida");
        await p.Locator("#simRenda").FillAsync("900000"); // R$ 9.000,00: faixa 3, que vai só até R$ 400 mil
        await Expect(p.Locator("#simMcmv")).ToContainTextAsync("o imóvel pode custar até");
        await Expect(p.Locator("#dNome")).ToHaveTextAsync("Caixa Econômica Federal");
        await p.Locator("#simRenda").FillAsync("");

        // Price: os bancos financiam até 70%, então a entrada sobe para 30% e a parcela fica fixa
        await p.GetByText("Price", new() { Exact = true }).ClickAsync();
        await Expect(p.Locator("#rPrimeiraRotulo")).ToHaveTextAsync("Parcela fixa");
        await Expect(p.Locator("#rFinanciado")).ToHaveTextAsync("R$ 350.000,00");
        await Expect(p.Locator("#simEntrada")).ToHaveValueAsync("150.000,00");
        // valor do imóvel alterado: a entrada volta a 20% (Price exige 30%) e o financiado é valor menos entrada
        await p.GetByText("SAC", new() { Exact = true }).ClickAsync();
        await p.Locator("#simValor").FillAsync("40000000"); // digitado em centavos: 400.000,00
        await Expect(p.Locator("#simValor")).ToHaveValueAsync("400.000,00");
        await Expect(p.Locator("#simEntrada")).ToHaveValueAsync("80.000,00");
        await Expect(p.Locator("#rFinanciado")).ToHaveTextAsync("R$ 320.000,00");
        // idade + prazo até 80 anos e 6 meses
        await p.Locator("#simIdade").FillAsync("60");
        await Expect(p.Locator("#simPrazoTxt")).ToHaveTextAsync("20 anos e 6 meses (246 meses)");

        // logo volta ao início; "Comprar" lista todos à venda; o cartão abre a ficha
        await p.GetByRole(AriaRole.Banner).GetByRole(AriaRole.Link, new() { Name = "Inovabens" }).ClickAsync();
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

        // a lista só mostra o próprio imóvel (o filtro começa em "Disponível"; o imóvel novo está em captação)
        await c.GotoAsync("/imoveis");
        await c.Interativa();
        var situacao = c.GetByLabel("Situação", new() { Exact = true });
        await Expect(situacao).ToHaveValueAsync("DISPONIVEL");
        await situacao.SelectOptionAsync("");
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
