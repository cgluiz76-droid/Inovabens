using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Financeiro;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Relatorios;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>
/// Extrato do imóvel (livro caixa) e comprovantes financeiros no Drive em memória, contra PostgreSQL real.
/// </summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class ExtratoComprovantesTests : IAsyncLifetime
{
    // um provider só para a classe: cada provider novo conta no limite de 20 do EF (ManyServiceProvidersCreatedWarning)
    private static readonly ArmazenamentoMemoria _drive = new();
    private static readonly Lazy<ServiceProvider> _sp = new(Criar);
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

    [Fact]
    public async Task Extrato_soma_receitas_menos_despesas_pagas_com_saldo_linha_a_linha_e_pendentes_a_parte()
    {
        var (admin, id, _) = await Cenario();
        var despesas = S<DespesasService>();
        var receitas = S<ReceitasService>();
        var extrato = S<ExtratoService>();
        var cats = await despesas.CategoriasAsync();
        var cat = cats[0];

        // a aquisição não entra no extrato (fica na apuração de resultado)
        await S<AquisicaoService>().SalvarAsync(admin, id, new AquisicaoForm { DataAquisicao = D(2025, 1, 5), ValorPago = 200000m });

        var aluguel = await despesas.CriarAsync(admin, id, new DespesaForm
        { CategoriaId = cat.Id, Descricao = "Condomínio", ValorPrevisto = 500m, DiaVencimento = 10, InicioEm = D(2025, 1, 1), FimEm = D(2025, 4, 30) });
        for (var m = 1; m <= 4; m++) await despesas.ChecklistAsync(admin, id, D(2025, m, 1));
        var lanc = (await despesas.HistoricoAsync(admin, id)).OrderBy(l => l.Dados.Competencia).ToList();
        Assert.Equal(4, lanc.Count);
        await despesas.AtualizarStatusAsync(admin, lanc[0].Dados.Id, new StatusLancamentoEntrada { Status = StatusLancamento.PAGO, DataPagamento = D(2025, 1, 12) });
        await despesas.AtualizarStatusAsync(admin, lanc[1].Dados.Id, new StatusLancamentoEntrada { Status = StatusLancamento.PAGO, DataPagamento = D(2025, 2, 10), Valor = 550m });
        await despesas.AtualizarStatusAsync(admin, lanc[2].Dados.Id, new StatusLancamentoEntrada { Status = StatusLancamento.NAO_PAGO, Justificativa = "Boleto não chegou" });
        // abril fica "a marcar"

        await receitas.CriarAsync(admin, id, new ReceitaForm { Tipo = TipoReceita.LOCACAO, Data = D(2025, 1, 20), ValorBruto = 2000m });
        await receitas.CriarAsync(admin, id, new ReceitaForm { Tipo = TipoReceita.LOCACAO, Data = D(2025, 2, 20), ValorBruto = 2000m, Tributos = 100m });

        // todo o período: 2000 − 500 + 1900 − 550 = 2850
        var tudo = await extrato.GerarAsync(admin, id, new FiltroExtrato());
        Assert.Equal(2850m, tudo.SaldoAtual);
        Assert.Equal(2850m, tudo.SaldoFinal);
        Assert.Equal(0m, tudo.SaldoAnterior);
        Assert.Equal(new[] { D(2025, 1, 12), D(2025, 1, 20), D(2025, 2, 10), D(2025, 2, 20) }, tudo.Movimentos.Select(m => m.Data));
        Assert.Equal(new[] { -500m, 1500m, 950m, 2850m }, tudo.Movimentos.Select(m => m.Saldo));
        Assert.Equal(3900m, tudo.Entradas);
        Assert.Equal(1050m, tudo.Saidas);
        Assert.Contains("tributos", tudo.Movimentos[3].Detalhe);
        // a pagar: março (não pago) e abril (a marcar), fora do saldo
        Assert.Equal(new[] { StatusLancamento.NAO_PAGO, (StatusLancamento?)null }, tudo.Pendentes.Select(p => p.Status));
        Assert.Equal(1000m, tudo.TotalPendente);
        Assert.Equal(1850m, tudo.SaldoProjetado);
        Assert.All(tudo.Pendentes, p => Assert.True(p.Vencida));

        // de fevereiro em diante: saldo anterior = janeiro (1500)
        var fev = await extrato.GerarAsync(admin, id, new FiltroExtrato { De = D(2025, 2, 1), Ate = D(2025, 2, 28) });
        Assert.Equal(1500m, fev.SaldoAnterior);
        Assert.Equal(2, fev.Movimentos.Count);
        Assert.Equal(2850m, fev.SaldoFinal);
        Assert.Empty(fev.Pendentes); // março e abril estão fora do período
        Assert.Equal(2850m, fev.SaldoAtual); // o saldo atual ignora os filtros

        // filtros de tipo, categoria e busca
        var soReceitas = await extrato.GerarAsync(admin, id, new FiltroExtrato { Tipo = TipoExtrato.RECEITAS });
        Assert.Equal(3900m, soReceitas.SaldoFinal);
        Assert.Empty(soReceitas.Pendentes);
        var soDespesas = await extrato.GerarAsync(admin, id, new FiltroExtrato { Tipo = TipoExtrato.DESPESAS });
        Assert.Equal(-1050m, soDespesas.SaldoFinal);
        var porCategoria = await extrato.GerarAsync(admin, id, new FiltroExtrato { CategoriaId = cat.Id });
        Assert.All(porCategoria.Movimentos, m => Assert.NotNull(m.LancamentoId));
        if (cats.Count > 1)
            Assert.Empty((await extrato.GerarAsync(admin, id, new FiltroExtrato { CategoriaId = cats[1].Id })).Movimentos);
        var busca = await extrato.GerarAsync(admin, id, new FiltroExtrato { Busca = "condom" });
        Assert.Equal(2, busca.Movimentos.Count);

        await Assert.ThrowsAsync<RegraNegocioException>(() =>
            extrato.GerarAsync(admin, id, new FiltroExtrato { De = D(2025, 3, 1), Ate = D(2025, 2, 1) }));

        // despesa excluída sai do extrato
        await despesas.ExcluirAsync(admin, aluguel);
        Assert.Equal(3900m, (await extrato.GerarAsync(admin, id, new FiltroExtrato())).SaldoAtual);

        // PDF e Excel com os mesmos filtros
        var rel = S<RelatoriosService>();
        var pdf = await rel.ExtratoPdfAsync(admin, id, new FiltroExtrato { De = D(2025, 1, 1) });
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf.Conteudo, 0, 4));
        var xlsx = await rel.ExtratoXlsxAsync(admin, id, new FiltroExtrato());
        Assert.Equal(RelatoriosService.TipoXlsx, xlsx.Tipo);
        Assert.True(xlsx.Conteudo.Length > 1000);
    }

    [Fact]
    public async Task Comprovantes_vao_para_07_Financeiro_do_ano_e_ficam_vinculados()
    {
        var (admin, id, nome) = await Cenario();
        var comprovantes = S<ComprovantesService>();
        var receita = await S<ReceitasService>().CriarAsync(admin, id, new ReceitaForm { Tipo = TipoReceita.LOCACAO, Data = D(2025, 3, 5), ValorBruto = 1500m });

        var despesas = S<DespesasService>();
        var cat = (await despesas.CategoriasAsync())[0];
        var despesaId = await despesas.CriarAsync(admin, id, new DespesaForm
        { CategoriaId = cat.Id, Descricao = "IPTU/2026", Recorrencia = Recorrencia.UNICA, ValorPrevisto = 300m, InicioEm = D(2026, 1, 1) });
        var lanc = (await despesas.ChecklistAsync(admin, id, D(2026, 1, 1))).Itens.Single();
        await despesas.AtualizarStatusAsync(admin, lanc.LancamentoId, new StatusLancamentoEntrada { Status = StatusLancamento.PAGO, DataPagamento = D(2026, 1, 8) });

        var r1 = await comprovantes.EnviarAsync(admin, OrigemComprovante.DaReceita(receita.Id), new MemoryStream([1, 2, 3]), "recibo.pdf", "application/pdf", 3);
        await comprovantes.EnviarAsync(admin, OrigemComprovante.DaReceita(receita.Id), new MemoryStream([4]), "contrato.jpg", "image/jpeg", 1);
        var d1 = await comprovantes.EnviarAsync(admin, OrigemComprovante.DoLancamento(lanc.LancamentoId), new MemoryStream([5, 6]), "boleto.pdf", "application/pdf", 2);

        // pasta do imóvel > 07 - Financeiro > ano da receita / do pagamento
        var raiz = _drive.Filhos(null).Single(i => i.Nome == nome);
        var financeiro = _drive.Filhos(raiz.Id).Single(i => i.Nome == SubpastasDrive.Financeiro);
        Assert.Equal(new[] { "2025", "2026" }, _drive.Filhos(financeiro.Id).Select(i => i.Nome).Order());
        var ano2025 = _drive.Filhos(financeiro.Id).Single(i => i.Nome == "2025");
        Assert.Contains(_drive.Filhos(ano2025.Id), a => a.Nome == "2025-03-05 Receita Locação - recibo.pdf");
        var ano2026 = _drive.Filhos(financeiro.Id).Single(i => i.Nome == "2026");
        Assert.Equal("2026-01-08 Despesa IPTU-2026 - boleto.pdf", _drive.Filhos(ano2026.Id).Single().Nome); // "/" vira "-"

        var lista = await comprovantes.ListarAsync(admin, OrigemComprovante.DaReceita(receita.Id));
        Assert.Equal(new[] { "recibo.pdf", "contrato.jpg" }, lista.Select(c => c.Nome));
        var contagem = await comprovantes.ContagemAsync(admin, id);
        Assert.Equal(2, contagem[receita.Id]);
        Assert.Equal(1, contagem[lanc.LancamentoId]);

        // a própria despesa também recebe anexos (qualquer documento), na pasta do ano de início
        const string docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        await comprovantes.EnviarAsync(admin, OrigemComprovante.DaDespesa(despesaId), new MemoryStream([7]), "contrato.docx", docx, 1);
        Assert.Contains(_drive.Filhos(ano2026.Id), a => a.Nome == "2026-01-01 Despesa IPTU-2026 - contrato.docx");
        Assert.Equal("contrato.docx", (await comprovantes.ListarAsync(admin, OrigemComprovante.DaDespesa(despesaId))).Single().Nome);
        Assert.Equal(1, (await comprovantes.ContagemAsync(admin, id))[despesaId]);

        var arquivo = await comprovantes.ArquivoAsync(admin, r1);
        Assert.Equal(new byte[] { 1, 2, 3 }, arquivo.Dados);
        Assert.Equal("recibo.pdf", arquivo.Nome);

        // remover: some da lista e o arquivo vai para a lixeira do Drive
        var chave = await ChaveDo(d1);
        await comprovantes.RemoverAsync(admin, d1);
        Assert.Empty(await comprovantes.ListarAsync(admin, OrigemComprovante.DoLancamento(lanc.LancamentoId)));
        Assert.True(_drive.Itens[chave].NaLixeira);

        // sem receita nem lançamento (ou com os dois): recusado
        await Assert.ThrowsAsync<RegraNegocioException>(() =>
            comprovantes.EnviarAsync(admin, new OrigemComprovante(null, null), new MemoryStream([1]), "x.pdf", "application/pdf", 1));
        await Assert.ThrowsAsync<RegraNegocioException>(() =>
            comprovantes.EnviarAsync(admin, new OrigemComprovante(receita.Id, null, despesaId), new MemoryStream([1]), "x.pdf", "application/pdf", 1));
        await Assert.ThrowsAsync<RegraNegocioException>(() =>
            comprovantes.EnviarAsync(admin, OrigemComprovante.DaReceita(receita.Id), new MemoryStream([1]), "grande.pdf", "application/pdf",
                ComprovantesService.TamanhoMaximo + 1));
    }

    private async Task<string> ChaveDo(Guid comprovante)
    {
        await using var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync();
        return await db.ComprovantesFinanceiros.Where(c => c.Id == comprovante).Select(c => c.ArquivoChave).SingleAsync();
    }
}
