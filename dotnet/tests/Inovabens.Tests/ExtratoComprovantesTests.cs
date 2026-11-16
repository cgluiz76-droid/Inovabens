using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Financeiro;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Pessoas;
using Inovabens.Core.Relatorios;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>
/// Lançamentos Financeiros (extrato real/projetado, títulos em atraso, recorrência) e comprovantes no Drive em memória, contra PostgreSQL real.
/// </summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class ExtratoComprovantesTests : IAsyncLifetime
{
    // um provider só para a classe: cada provider novo conta no limite de 20 do EF (ManyServiceProvidersCreatedWarning)
    private static readonly ArmazenamentoMemoria _drive = new();
    private static readonly Lazy<ServiceProvider> _sp = new(Criar);
    /// <summary>Usado também por outras classes de integração (WhatsApp), para não criar mais providers do EF.</summary>
    internal static ServiceProvider Compartilhado => _sp.Value;
    private IServiceScope _escopo = default!;

    private static DateOnly D(int a, int m, int d) => new(a, m, d);
    private T S<T>() where T : notnull => _escopo.ServiceProvider.GetRequiredService<T>();

    private static ServiceProvider Criar()
    {
        var op = InovabensOpcoes.De(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = BancoFixture.Url,
        }).Build(), producao: false);
        var servicos = new ServiceCollection().AddLogging().AddInovabensCore(op);
        servicos.AddSingleton<IArmazenamento>(_drive); // substitui o Google Drive
        return servicos.BuildServiceProvider();
    }

    public Task InitializeAsync()
    {
        _escopo = _sp.Value.CreateScope();
        _escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _escopo.Dispose();
        return Task.CompletedTask;
    }

    private async Task<(UsuarioAtual Admin, Guid Imovel, string Nome)> Cenario()
    {
        var admin = (await S<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var nome = "Extrato " + Guid.NewGuid().ToString("N")[..8];
        var id = await S<ImoveisService>().CriarAsync(admin, new ImovelForm { NomeDrive = nome, Titulo = "Casa do extrato", Tipo = "casa" });
        return (admin, id, nome);
    }

    private async Task<Guid> Conta(string nome)
    {
        await using var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync();
        return await db.ContasOrcamentarias.Where(c => c.Nome == nome).Select(c => c.Id).SingleAsync();
    }

    private static LancamentoForm Despesa(DateOnly venc, decimal valor, string descricao, Guid conta, DateOnly? pago = null) => new()
    {
        Tipo = TipoConta.DESPESA, Data = venc, Valor = valor, Descricao = descricao, ContaId = conta, Liquidado = pago is not null, DataLiquidacao = pago,
    };

    private static LancamentoForm Receita(DateOnly venc, decimal valor, string descricao, Guid conta, DateOnly? recebido = null) => new()
    {
        Tipo = TipoConta.RECEITA, Data = venc, Valor = valor, Descricao = descricao, ContaId = conta, Liquidado = recebido is not null, DataLiquidacao = recebido,
    };

    [Fact]
    public async Task Extrato_real_so_com_liquidados_projetado_com_abertos_e_previstos()
    {
        var (admin, id, _) = await Cenario();
        var lanc = S<LancamentosFinanceirosService>();
        var codigo = (await S<ImoveisService>().DetalheAsync(admin, id)).Dados.Codigo;
        var condominio = await Conta("Condomínio");
        var aluguel = await Conta("Aluguel recebido");

        // a aquisição não entra (fica na aba Investimento)
        await S<AquisicaoService>().SalvarAsync(admin, id, new AquisicaoForm { DataAquisicao = D(2025, 1, 5), ValorPago = 200000m });

        var c1 = await lanc.CriarAsync(admin, id, Despesa(D(2025, 1, 10), 500m, "Condomínio jan", condominio, pago: D(2025, 1, 12)));
        await lanc.CriarAsync(admin, id, Receita(D(2025, 1, 20), 2000m, "Aluguel jan", aluguel, recebido: D(2025, 1, 20)));
        var c2 = await lanc.CriarAsync(admin, id, Despesa(D(2025, 2, 10), 550m, "Condomínio fev", condominio));       // aberto
        await lanc.LiquidarAsync(admin, c2, D(2025, 2, 11), 560m);                                                   // pago com juros
        await lanc.CriarAsync(admin, id, Receita(D(2025, 2, 20), 1900m, "Aluguel fev", aluguel, recebido: D(2025, 2, 22)));
        var aberto = await lanc.CriarAsync(admin, id, Despesa(D(2025, 3, 10), 500m, "Condomínio mar", condominio)); // em atraso

        // nº sequencial por imóvel, no formato CODIGO-0001
        Assert.Equal($"{codigo}-0001", (await lanc.ObterAsync(admin, c1)).Numero);
        Assert.Equal($"{codigo}-0005", (await lanc.ObterAsync(admin, aberto)).Numero);

        // saldo REAL: só liquidados, na data da liquidação: −500 + 2000 − 560 + 1900 = 2840
        var tudo = await lanc.ExtratoAsync(admin, id, new FiltroLancamentos());
        Assert.Equal(2840m, tudo.SaldoAtual);
        Assert.Equal(2840m, tudo.SaldoFinal);
        Assert.Equal(new[] { D(2025, 1, 12), D(2025, 1, 20), D(2025, 2, 11), D(2025, 2, 22) }, tudo.Linhas.Select(l => l.Data));
        Assert.Equal(new[] { -500m, 1500m, 940m, 2840m }, tudo.Linhas.Select(l => l.Saldo));
        Assert.All(tudo.Linhas, l => Assert.Equal(SituacaoLancamento.LIQUIDADO, l.Situacao));
        Assert.Equal(3900m, tudo.Entradas);
        Assert.Equal(1060m, tudo.Saidas);
        var emAberto = Assert.Single(tudo.Abertos);
        Assert.True(emAberto.Atrasado);
        Assert.Equal(-500m, tudo.TotalAberto);

        // saldo PROJETADO: o aberto entra na data de vencimento
        var proj = await lanc.ExtratoAsync(admin, id, new FiltroLancamentos { Projetado = true });
        Assert.Equal(2340m, proj.SaldoFinal);
        Assert.Equal(2840m, proj.SaldoAtual); // o saldo atual é sempre o real
        Assert.Equal(SituacaoLancamento.ABERTO, proj.Linhas[^1].Situacao);

        // fevereiro: saldo anterior = janeiro (1500); o aberto de março está fora do período e ainda não venceu nele
        var fev = await lanc.ExtratoAsync(admin, id, new FiltroLancamentos { De = D(2025, 2, 1), Ate = D(2025, 2, 28) });
        Assert.Equal(1500m, fev.SaldoAnterior);
        Assert.Equal(2, fev.Linhas.Count);
        Assert.Equal(2840m, fev.SaldoFinal);
        Assert.Empty(fev.Abertos);

        // filtros de tipo, conta e busca
        Assert.Equal(3900m, (await lanc.ExtratoAsync(admin, id, new FiltroLancamentos { Tipo = TipoExtrato.RECEITAS })).SaldoFinal);
        Assert.Equal(-1060m, (await lanc.ExtratoAsync(admin, id, new FiltroLancamentos { Tipo = TipoExtrato.DESPESAS })).SaldoFinal);
        Assert.Equal(2, (await lanc.ExtratoAsync(admin, id, new FiltroLancamentos { ContaId = aluguel })).Linhas.Count);
        Assert.Single((await lanc.ExtratoAsync(admin, id, new FiltroLancamentos { Busca = "fev" })).Linhas.Where(l => l.Valor < 0));
        Assert.Single((await lanc.ExtratoAsync(admin, id, new FiltroLancamentos { Busca = $"{codigo}-0002" })).Linhas);
        await Assert.ThrowsAsync<RegraNegocioException>(() =>
            lanc.ExtratoAsync(admin, id, new FiltroLancamentos { De = D(2025, 3, 1), Ate = D(2025, 2, 1) }));

        // conta precisa ser do mesmo tipo do lançamento
        await Assert.ThrowsAsync<RegraNegocioException>(() => lanc.CriarAsync(admin, id, Receita(D(2025, 3, 1), 10m, "Errada", condominio)));

        // título em atraso: aparece na contagem/lista do financeiro e gera alerta; liquidar resolve
        Assert.Contains(await lanc.AtrasadosAsync(admin), t => t.Id == aberto && t.DiasAtraso > 0);
        Assert.True(await lanc.ContagemAtrasadosAsync(admin) >= 1);
        var alertas = S<Inovabens.Core.Agenda.AlertasService>();
        await alertas.VerificarAtrasadosAsync();
        var alerta = (await alertas.ListarAsync(admin, false)).Single(a => a.Dados.LancamentoId == aberto);
        Assert.Equal(TipoAlerta.TITULO_EM_ATRASO, alerta.Dados.Tipo);
        Assert.Equal(id, alerta.ImovelId);
        Assert.Contains($"{codigo}-0005", alerta.Dados.Mensagem);
        Assert.Equal(0, (await alertas.VerificarAtrasadosAsync()).Criados); // idempotente
        await lanc.LiquidarAsync(admin, aberto, D(2025, 3, 15), 500m);
        Assert.DoesNotContain(await alertas.ListarAsync(admin, false), a => a.Dados.LancamentoId == aberto);
        Assert.DoesNotContain(await lanc.AtrasadosAsync(admin), t => t.Id == aberto);
        await Assert.ThrowsAsync<RegraNegocioException>(() => lanc.LiquidarAsync(admin, aberto, D(2025, 3, 15), 500m));

        // exclusão: some do extrato e fica na Auditoria com quem excluiu e o quê
        await lanc.ExcluirAsync(admin, c1);
        Assert.Equal(2840m + 500m - 500m, (await lanc.ExtratoAsync(admin, id, new FiltroLancamentos())).SaldoAtual);
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
        {
            var log = await db.AuditLog.SingleAsync(a => a.Entidade == "lancamentos_financeiros" && a.EntidadeId == c1 && a.Acao == "EXCLUIR");
            Assert.Equal(admin.Id, log.UsuarioId);
            Assert.Contains($"{codigo}-0001", Inovabens.Core.Admin.AuditoriaService.Resumir(log.ValoresAntes));
        }

        // PDF e Excel com os mesmos filtros
        var rel = S<RelatoriosService>();
        var pdf = await rel.ExtratoPdfAsync(admin, id, new FiltroLancamentos { De = D(2025, 1, 1) });
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf.Conteudo, 0, 4));
        var xlsx = await rel.ExtratoXlsxAsync(admin, id, new FiltroLancamentos { Projetado = true });
        Assert.Equal(RelatoriosService.TipoXlsx, xlsx.Tipo);
        using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(xlsx.Conteudo));
        var aba = wb.Worksheet("Lançamentos");
        Assert.Equal($"{codigo}-0002", aba.Cell(2, 1).GetString());
        Assert.Contains(aba.RowsUsed(), r => r.Cell(4).GetString() == "Saldo no fim do período");
    }

    [Fact]
    public async Task Recorrencia_gera_previstos_que_sao_confirmados_ou_ignorados()
    {
        var (admin, id, _) = await Cenario();
        var lanc = S<LancamentosFinanceirosService>();
        var condominio = await Conta("Condomínio");
        var hoje = DatasBR.Hoje();
        var inicio = new DateOnly(hoje.Year, hoje.Month, 1).AddMonths(-3);

        var origem = await lanc.CriarAsync(admin, id, new LancamentoForm
        {
            Tipo = TipoConta.DESPESA, Data = inicio.AddDays(9), Valor = 480m, Descricao = "Condomínio", ContaId = condominio, Liquidado = true,
            DataLiquidacao = inicio.AddDays(9), Recorrente = true, DiaRecorrencia = 31, ValorEstimado = 500m,
        });

        // 3 meses passados até o atual: um previsto por mês, no último dia quando o mês não tem dia 31
        var ex = await lanc.ExtratoAsync(admin, id, new FiltroLancamentos { De = inicio, Ate = inicio.AddMonths(4).AddDays(-1) });
        Assert.Equal(3, ex.Previstos.Count);
        Assert.All(ex.Previstos, p => Assert.Equal(-500m, p.Valor));
        Assert.All(ex.Previstos, p => Assert.Equal(p.Vencimento.AddDays(1).Day, 1)); // último dia do mês
        Assert.Equal(0, await lanc.GerarPrevistosAsync(LancamentosFinanceirosService.LimitePrevistos(null), id)); // idempotente
        Assert.Equal(-480m, ex.SaldoFinal); // previstos fora do saldo real

        // confirmar: vira título aberto com nº; ignorar: some e não volta
        var p1 = ex.Previstos[0];
        await lanc.ConfirmarAsync(admin, p1.Id, new ConfirmacaoPrevistoForm { Data = p1.Vencimento, Valor = 510m });
        var p2 = ex.Previstos[1];
        await lanc.IgnorarAsync(admin, p2.Id);
        var p3 = ex.Previstos[2];
        await lanc.ConfirmarAsync(admin, p3.Id, new ConfirmacaoPrevistoForm { Data = p3.Vencimento, Valor = 500m, Liquidado = true, DataLiquidacao = p3.Vencimento });

        ex = await lanc.ExtratoAsync(admin, id, new FiltroLancamentos { De = inicio, Ate = inicio.AddMonths(4).AddDays(-1) });
        Assert.Empty(ex.Previstos);
        var aberto = Assert.Single(ex.Abertos);
        Assert.Equal(-510m, aberto.Valor);
        Assert.EndsWith("-0002", aberto.Numero);
        Assert.Equal(-980m, ex.SaldoFinal);
        Assert.Equal(0, await lanc.GerarPrevistosAsync(LancamentosFinanceirosService.LimitePrevistos(null), id));

        // previsto futuro só até 12 meses à frente
        var longe = await lanc.ExtratoAsync(admin, id, new FiltroLancamentos { De = inicio, Ate = hoje.AddYears(3) });
        Assert.Equal(12, longe.Previstos.Count);

        // mudar a recorrência refaz os previstos não confirmados; desligar remove
        var form = LancamentoForm.De((await lanc.ObterAsync(admin, origem)).Dados);
        form.ValorEstimado = 600m;
        await lanc.AtualizarAsync(admin, origem, form);
        longe = await lanc.ExtratoAsync(admin, id, new FiltroLancamentos { De = inicio, Ate = hoje.AddYears(3) });
        Assert.All(longe.Previstos, p => Assert.Equal(-600m, p.Valor));
        Assert.DoesNotContain(longe.Previstos, p => p.Id == p2.Id); // o ignorado não volta
        form.Recorrente = false;
        await lanc.AtualizarAsync(admin, origem, form);
        Assert.Empty((await lanc.ExtratoAsync(admin, id, new FiltroLancamentos { De = inicio, Ate = hoje.AddYears(3) })).Previstos);
        Assert.Single((await lanc.ExtratoAsync(admin, id, new FiltroLancamentos { De = inicio, Ate = hoje.AddYears(3) })).Abertos); // o confirmado fica
    }

    [Fact]
    public async Task Pessoa_do_lancamento_vincula_cadastro_por_nome_e_vai_para_previstos_busca_e_exportacao()
    {
        var (admin, id, _) = await Cenario();
        var lanc = S<LancamentosFinanceirosService>();
        var condominio = await Conta("Condomínio");
        var nome = $"Síndico Pz{Guid.NewGuid():N}"[..20];
        var pessoaId = await S<PessoasService>().CriarAsync(admin, new PessoaForm { NomeRazao = nome });
        var hoje = DatasBR.Hoje();
        var mes = new DateOnly(hoje.Year, hoje.Month, 1);

        // nome digitado igual ao cadastro (sem diferenciar maiúsculas): vincula a pessoa; recorrência leva a pessoa aos previstos
        var form = Despesa(mes.AddMonths(-1), 300m, "Condomínio", condominio, mes.AddMonths(-1));
        form.Pessoa = "  " + nome.ToUpperInvariant() + " ";
        form.Recorrente = true;
        form.DiaRecorrencia = 1;
        form.ValorEstimado = 300m;
        var l1 = await lanc.CriarAsync(admin, id, form);
        var d1 = await lanc.ObterAsync(admin, l1);
        Assert.Equal(pessoaId, d1.Dados.PessoaId);
        Assert.Equal(nome.ToUpperInvariant(), d1.Dados.PessoaNome);

        // nome novo (sem cadastro): fica só o texto
        var l2 = await lanc.CriarAsync(admin, id, Com(Receita(mes, 50m, "Reembolso", await Conta("Reembolso"), mes), "Fulano Sem Cadastro"));
        Assert.Null((await lanc.ObterAsync(admin, l2)).Dados.PessoaId);

        var ex = await lanc.ExtratoAsync(admin, id, new FiltroLancamentos { De = mes.AddMonths(-1), Ate = mes.AddMonths(1).AddDays(-1), Projetado = true });
        Assert.Contains(ex.Linhas, x => x.Id == l1 && x.Pessoa == nome.ToUpperInvariant());
        Assert.All(ex.Previstos, p => Assert.Equal(nome.ToUpperInvariant(), p.Pessoa));
        Assert.NotEmpty(ex.Previstos);

        // a busca também procura pela pessoa
        var busca = await lanc.ExtratoAsync(admin, id, new FiltroLancamentos { De = mes.AddMonths(-1), Ate = mes.AddMonths(1).AddDays(-1), Busca = "fulano sem" });
        Assert.Equal(l2, Assert.Single(busca.Linhas).Id);

        // editar sem mudar o nome mantém o vínculo; apagar o nome desfaz
        var f1 = LancamentoForm.De(d1.Dados);
        f1.Observacao = "ajuste";
        await lanc.AtualizarAsync(admin, l1, f1);
        Assert.Equal(pessoaId, (await lanc.ObterAsync(admin, l1)).Dados.PessoaId);
        f1.Pessoa = null;
        f1.Recorrente = false;
        await lanc.AtualizarAsync(admin, l1, f1);
        var d1b = (await lanc.ObterAsync(admin, l1)).Dados;
        Assert.Null(d1b.PessoaId);
        Assert.Null(d1b.PessoaNome);

        // exportação: a pessoa sai junto da descrição
        var xlsx = await S<RelatoriosService>().ExtratoXlsxAsync(admin, id, new FiltroLancamentos { De = mes, Ate = mes.AddMonths(1).AddDays(-1) });
        using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(xlsx.Conteudo));
        Assert.Contains(wb.Worksheet("Lançamentos").CellsUsed(), c => c.GetString() == "Reembolso · Fulano Sem Cadastro");
    }

    private static LancamentoForm Com(LancamentoForm f, string pessoa)
    {
        f.Pessoa = pessoa;
        return f;
    }

    [Fact]
    public async Task Comprovantes_vao_para_07_Financeiro_do_ano_e_ficam_vinculados()
    {
        var (admin, id, nome) = await Cenario();
        var comprovantes = S<ComprovantesService>();
        var lanc = S<LancamentosFinanceirosService>();
        var codigo = (await S<ImoveisService>().DetalheAsync(admin, id)).Dados.Codigo;
        var receita = await lanc.CriarAsync(admin, id, Receita(D(2025, 3, 5), 1500m, "Aluguel março", await Conta("Aluguel recebido"), D(2025, 3, 5)));
        var despesa = await lanc.CriarAsync(admin, id, Despesa(D(2026, 1, 8), 300m, "IPTU/2026", await Conta("IPTU"), D(2026, 1, 8)));

        var r1 = await comprovantes.EnviarAsync(admin, receita, new MemoryStream([1, 2, 3]), "recibo.pdf", "application/pdf", 3);
        await comprovantes.EnviarAsync(admin, receita, new MemoryStream([4]), "contrato.jpg", "image/jpeg", 1);
        var d1 = await comprovantes.EnviarAsync(admin, despesa, new MemoryStream([5, 6]), "boleto.pdf", "application/pdf", 2);

        // pasta do imóvel > 07 - Financeiro > ano do vencimento
        var raiz = _drive.Filhos(null).Single(i => i.Nome.EndsWith(" - " + nome));
        var financeiro = _drive.Filhos(raiz.Id).Single(i => i.Nome == SubpastasDrive.Financeiro);
        Assert.Equal(new[] { "2025", "2026" }, _drive.Filhos(financeiro.Id).Select(i => i.Nome).Order());
        var ano2025 = _drive.Filhos(financeiro.Id).Single(i => i.Nome == "2025");
        Assert.Contains(_drive.Filhos(ano2025.Id), a => a.Nome == $"2025-03-05 {codigo}-0001 Aluguel março - recibo.pdf");
        var ano2026 = _drive.Filhos(financeiro.Id).Single(i => i.Nome == "2026");
        Assert.Equal($"2026-01-08 {codigo}-0002 IPTU-2026 - boleto.pdf", _drive.Filhos(ano2026.Id).Single().Nome); // "/" vira "-"

        var lista = await comprovantes.ListarAsync(admin, receita);
        Assert.Equal(new[] { "recibo.pdf", "contrato.jpg" }, lista.Select(c => c.Nome));
        var contagem = await comprovantes.ContagemAsync(admin, id);
        Assert.Equal(2, contagem[receita]);
        Assert.Equal(1, contagem[despesa]);

        var arquivo = await comprovantes.ArquivoAsync(admin, r1);
        Assert.Equal(new byte[] { 1, 2, 3 }, arquivo.Dados);
        Assert.Equal("recibo.pdf", arquivo.Nome);

        // remover: some da lista e o arquivo vai para a lixeira do Drive
        var chave = await ChaveDo(d1);
        await comprovantes.RemoverAsync(admin, d1);
        Assert.Empty(await comprovantes.ListarAsync(admin, despesa));
        Assert.True(_drive.Itens[chave].NaLixeira);

        await Assert.ThrowsAsync<NaoEncontradoException>(() =>
            comprovantes.EnviarAsync(admin, Guid.NewGuid(), new MemoryStream([1]), "x.pdf", "application/pdf", 1));
        await Assert.ThrowsAsync<RegraNegocioException>(() =>
            comprovantes.EnviarAsync(admin, receita, new MemoryStream([1]), "grande.pdf", "application/pdf", ComprovantesService.TamanhoMaximo + 1));
    }
    private async Task<string> ChaveDo(Guid comprovante)
    {
        await using var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync();
        return await db.ComprovantesFinanceiros.Where(c => c.Id == comprovante).Select(c => c.ArquivoChave).SingleAsync();
    }
}
