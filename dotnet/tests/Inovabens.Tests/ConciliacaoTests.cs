using System.Text;
using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Dominio;
using Inovabens.Core.Financeiro;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>OFX dos bancos, conciliação (sugestão, regra aprendida, divisão), fechamento do mês, imóvel pago pela conta e DRE exportada.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class ConciliacaoTests : IAsyncLifetime
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

    private async Task<UsuarioAtual> Admin() => (await S<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
    private static string Unico(string nome) => $"{nome} {Guid.NewGuid().ToString("N")[..6]}";
    private async Task<Guid> Categoria(UsuarioAtual u, string nome) =>
        (await S<FinanceiroEmpresaService>().CategoriasAsync(u)).Single(c => c.Nome == nome).Id;

    /// <summary>OFX 1.x como o Sicoob/Itaú exportam: SGML sem fechar tags, Windows-1252, valor com vírgula.</summary>
    private static byte[] OfxSgml(DateOnly saldoEm, decimal saldo, params (string Fit, DateOnly Data, string Valor, string Memo)[] t)
    {
        var sb = new StringBuilder("OFXHEADER:100\r\nDATA:OFXSGML\r\nVERSION:102\r\nCHARSET:1252\r\n\r\n<OFX>\r\n<BANKMSGSRSV1><STMTTRNRS><STMTRS>\r\n")
            .Append("<BANKACCTFROM>\r\n<BANKID>0756\r\n<ACCTID>12345-6\r\n</BANKACCTFROM>\r\n<BANKTRANLIST>\r\n<DTSTART>20260101\r\n<DTEND>20261231\r\n");
        foreach (var (fit, data, valor, memo) in t)
            sb.Append($"<STMTTRN>\r\n<TRNTYPE>OTHER\r\n<DTPOSTED>{data:yyyyMMdd}120000[-3:BRT]\r\n<TRNAMT>{valor}\r\n<FITID>{fit}\r\n<MEMO>{memo}\r\n</STMTTRN>\r\n");
        sb.Append($"</BANKTRANLIST>\r\n<LEDGERBAL>\r\n<BALAMT>{saldo.ToString(System.Globalization.CultureInfo.InvariantCulture)}\r\n<DTASOF>{saldoEm:yyyyMMdd}\r\n</LEDGERBAL>\r\n</STMTRS></STMTTRNRS></BANKMSGSRSV1>\r\n</OFX>");
        return Encoding.GetEncoding(1252).GetBytes(sb.ToString());
    }

    [Fact]
    public void Le_OFX_sgml_1252_e_xml_de_cartao()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var sgml = OfxLeitor.Ler(OfxSgml(new(2026, 11, 30), 1234.5m,
            ("A1", new(2026, 11, 3), "-1.234,56", "PAGTO CONCESSIONÁRIA"), ("A1", new(2026, 11, 4), "200.00", "PIX RECEBIDO JOÃO")));
        Assert.False(sgml.Cartao);
        Assert.Equal("12345-6", sgml.Conta);
        Assert.Equal(1234.5m, sgml.Saldo);
        Assert.Equal(new DateOnly(2026, 11, 30), sgml.DataSaldo);
        Assert.Equal([-1234.56m, 200m], sgml.Transacoes.Select(t => t.Valor));
        Assert.Equal("PAGTO CONCESSIONÁRIA", sgml.Transacoes[0].Descricao);
        // FITID repetido no mesmo arquivo não se perde
        Assert.Equal(["A1", "A1#2"], sgml.Transacoes.Select(t => t.FitId));

        var xml = """
            <?xml version="1.0" encoding="UTF-8"?><?OFX OFXHEADER="200" VERSION="211"?>
            <OFX><CREDITCARDMSGSRSV1><CCSTMTTRNRS><CCSTMTRS><BANKTRANLIST>
            <STMTTRN><TRNTYPE>DEBIT</TRNTYPE><DTPOSTED>20261105</DTPOSTED><TRNAMT>-59.90</TRNAMT><NAME>NETFLIX</NAME></STMTTRN>
            </BANKTRANLIST></CCSTMTRS></CCSTMTTRNRS></CREDITCARDMSGSRSV1></OFX>
            """;
        var cartao = OfxLeitor.Ler(Encoding.UTF8.GetBytes(xml));
        Assert.True(cartao.Cartao);
        var t = Assert.Single(cartao.Transacoes);
        Assert.Equal(("NETFLIX", -59.90m), (t.Descricao, t.Valor));
        Assert.StartsWith("h", t.FitId);

        Assert.Equal(OfxLeitor.Chave("PIX ENVIADO 12/11 JOÃO"), OfxLeitor.Chave("Pix enviado 03/12 Joao"));
        Assert.Throws<RegraNegocioException>(() => OfxLeitor.Ler(Encoding.UTF8.GetBytes("isto não é ofx")));
    }

    [Fact]
    public async Task Importa_sugere_aprende_e_concilia()
    {
        var admin = await Admin();
        var fin = S<FinanceiroEmpresaService>();
        var conc = S<ConciliacaoService>();
        var ano = 3000 + Random.Shared.Next(5000);
        var d = new DateOnly(ano, 3, 10);
        var conta = await fin.CriarContaAsync(admin, new ContaBancariaForm { Nome = Unico("Sicoob"), SaldoInicial = 1000, DataSaldoInicial = new(ano, 1, 1) });
        var energia = await Categoria(admin, "Energia, água, telefone e internet");
        var tarifa = await Categoria(admin, "Tarifas bancárias");
        var aluguel = await Categoria(admin, "Aluguel recebido de inquilino");
        var taxa = await Categoria(admin, "Taxa de administração de aluguel");

        // conta de luz lançada em aberto: o banco paga um dia depois do vencimento
        var luz = (await fin.CriarAsync(admin, new LancamentoEmpresaForm { Descricao = "Luz", Valor = 150, CategoriaId = energia, Vencimento = d })).Single();
        var ofx = OfxSgml(d.AddDays(5), 1000 - 150 - 12.5m + 1000,
            ("L1", d.AddDays(1), "-150,00", "PAGTO ENERGIA"), ("T1", d.AddDays(2), "-12,50", $"TARIFA PACOTE 03/{ano}"),
            ("P1", d.AddDays(3), "1000,00", "PIX RECEBIDO INQUILINO"));
        var r = await conc.ImportarAsync(admin, conta, "sicoob.ofx", ofx);
        Assert.Equal((3, 3, 0), (r.Lidos, r.Novos, r.Repetidos));
        Assert.Null(r.AvisoConta);
        // o mesmo arquivo de novo não duplica
        Assert.Equal(0, (await conc.ImportarAsync(admin, conta, "sicoob.ofx", ofx)).Novos);

        var c = await conc.ConciliacaoAsync(admin, conta);
        Assert.Equal(3, c.Pendentes.Count);
        var linhaLuz = c.Pendentes.Single(p => p.Valor == -150);
        Assert.Equal((TipoSugestao.LANCAMENTO, luz), (linhaLuz.Sugestao.Tipo, linhaLuz.Sugestao.Id!.Value));
        await conc.ConciliarAsync(admin, linhaLuz.Id, luz);
        // conciliado: baixado na data do banco; não exclui sem desfazer
        var lancLuz = (await fin.LancamentosAsync(admin, new FiltroLancamentosEmpresa { De = d, Ate = d.AddDays(5), ContaId = conta })).Single(l => l.Id == luz);
        Assert.Equal((d.AddDays(1), true), (lancLuz.DataPagamento!.Value, lancLuz.Conciliado));
        await Assert.ThrowsAsync<RegraNegocioException>(() => fin.ExcluirAsync(admin, luz));

        // tarifa: cria e aprende; aluguel: divide entre terceiros e taxa
        await conc.CriarAsync(admin, c.Pendentes.Single(p => p.Valor == -12.5m).Id,
            new CriarDoExtratoForm { Partes = [new() { CategoriaId = tarifa, Valor = 12.5m }] });
        await Assert.ThrowsAsync<RegraNegocioException>(() => conc.CriarAsync(admin, c.Pendentes.Single(p => p.Valor == 1000).Id,
            new CriarDoExtratoForm { Partes = [new() { CategoriaId = aluguel, Valor = 900 }] }));
        await conc.CriarAsync(admin, c.Pendentes.Single(p => p.Valor == 1000).Id, new CriarDoExtratoForm
        {
            Partes = [new() { CategoriaId = aluguel, Valor = 900 }, new() { CategoriaId = taxa, Valor = 100 }], Aprender = false,
        });

        c = await conc.ConciliacaoAsync(admin, conta);
        Assert.Empty(c.Pendentes);
        Assert.Equal(3, c.Conciliados.Count);
        // o saldo do sistema bate com o que o banco informou no arquivo
        Assert.Equal(c.SaldoBanco, c.SaldoSistemaNaData);

        // mês seguinte: a mesma tarifa já vem com a categoria aprendida e é aceita em lote
        await conc.ImportarAsync(admin, conta, "sicoob-abril.ofx", OfxSgml(d.AddMonths(1), 0,
            ("T2", d.AddMonths(1), "-12,50", $"TARIFA PACOTE 04/{ano}")));
        var p2 = Assert.Single((await conc.ConciliacaoAsync(admin, conta)).Pendentes);
        Assert.Equal((TipoSugestao.REGRA, tarifa), (p2.Sugestao.Tipo, p2.Sugestao.Id!.Value));
        Assert.Equal(1, await conc.AceitarSugestoesAsync(admin, conta));

        // desfazer volta a linha para pendente e libera o lançamento
        await conc.DesfazerAsync(admin, linhaLuz.Id);
        Assert.Contains((await conc.ConciliacaoAsync(admin, conta)).Pendentes, p => p.Id == linhaLuz.Id);
        await fin.ExcluirAsync(admin, luz);
    }

    [Fact]
    public async Task Aluguel_com_taxa_casa_com_um_credito_so_no_banco()
    {
        var admin = await Admin();
        var fin = S<FinanceiroEmpresaService>();
        var conc = S<ConciliacaoService>();
        var ano = 3000 + Random.Shared.Next(5000);
        var d = new DateOnly(ano, 7, 5);
        var conta = await fin.CriarContaAsync(admin, new ContaBancariaForm { Nome = Unico("Sicredi"), DataSaldoInicial = new(ano, 1, 1) });
        // lançado antes do extrato: 2.000 recebidos viram 1.800 do proprietário + 200 de taxa
        await fin.CriarAsync(admin, new LancamentoEmpresaForm
        {
            Tipo = TipoCategoriaEmpresa.ENTRADA, Descricao = "Aluguel julho", Valor = 2000, TaxaRetida = 200,
            CategoriaId = await Categoria(admin, "Aluguel recebido de inquilino"), CategoriaTaxaId = await Categoria(admin, "Taxa de administração de aluguel"),
            Vencimento = d,
        });
        await conc.ImportarAsync(admin, conta, "sicredi.ofx", OfxSgml(d, 2000, ("X1", d.AddDays(1), "2000,00", "PIX RECEBIDO INQUILINO")));
        var p = Assert.Single((await conc.ConciliacaoAsync(admin, conta)).Pendentes);
        Assert.Equal(2, p.Sugestao.Grupo?.Count);
        Assert.Equal(1, await conc.AceitarSugestoesAsync(admin, conta));
        var lancs = await fin.LancamentosAsync(admin, new FiltroLancamentosEmpresa { De = d, Ate = d.AddDays(1), ContaId = conta });
        Assert.All(lancs, l => Assert.True(l.Conciliado && l.DataPagamento == d.AddDays(1)));
        Assert.Equal(2000, (await fin.ContasAsync(admin)).Single(c => c.Conta.Id == conta).Saldo);
    }

    [Fact]
    public async Task Mes_fechado_trava_lancamentos_ate_reabrir()
    {
        var admin = await Admin();
        var fin = S<FinanceiroEmpresaService>();
        var conc = S<ConciliacaoService>();
        var mes = new DateOnly(8000 + Random.Shared.Next(1900), 6, 1);
        var cat = await Categoria(admin, "Outras despesas");
        var id = (await fin.CriarAsync(admin, new LancamentoEmpresaForm { Descricao = "Antes", Valor = 10, CategoriaId = cat, Vencimento = mes.AddDays(4) })).Single();

        await conc.FecharMesAsync(admin, mes.AddDays(10));
        Assert.True((await conc.ResumoFechamentoAsync(admin, mes)).Fechado);
        Assert.Contains(mes, await conc.MesesFechadosAsync(admin));
        var e = await Assert.ThrowsAsync<RegraNegocioException>(() =>
            fin.CriarAsync(admin, new LancamentoEmpresaForm { Descricao = "Depois", Valor = 10, CategoriaId = cat, Vencimento = mes.AddDays(5) }));
        Assert.Contains("fechado", e.Message);
        await Assert.ThrowsAsync<RegraNegocioException>(() => fin.ExcluirAsync(admin, id));

        await conc.ReabrirMesAsync(admin, mes);
        await fin.ExcluirAsync(admin, id);
    }

    [Fact]
    public async Task Lancamento_do_imovel_pago_pela_conta_entra_no_saldo_e_concilia()
    {
        var admin = await Admin();
        var fin = S<FinanceiroEmpresaService>();
        var conc = S<ConciliacaoService>();
        var livro = S<LancamentosFinanceirosService>();
        var ano = 3000 + Random.Shared.Next(5000);
        var d = new DateOnly(ano, 5, 15);
        var conta = await fin.CriarContaAsync(admin, new ContaBancariaForm { Nome = Unico("Itaú"), SaldoInicial = 500, DataSaldoInicial = new(ano, 1, 1) });
        var imovel = await S<ImoveisService>().CriarAsync(admin, new ImovelForm
        {
            NomeDrive = Unico("Reforma"), Titulo = "Casa em reforma", Tipo = "casa", PrecoVenda = 200_000,
        });
        var orcamentaria = (await livro.ContasAsync(TipoConta.DESPESA)).First().Id;

        // pago pela conta da imobiliária: o saldo dela cai, mas a DRE não muda
        await livro.CriarAsync(admin, imovel, new LancamentoForm
        {
            Tipo = TipoConta.DESPESA, Data = d, Valor = 100, Descricao = "Pintura", ContaId = orcamentaria,
            Liquidado = true, DataLiquidacao = d, ContaBancariaId = conta,
        });
        Assert.Equal(400, (await fin.ContasAsync(admin)).Single(c => c.Conta.Id == conta).Saldo);
        var linha = Assert.Single(await fin.LancamentosAsync(admin, new FiltroLancamentosEmpresa { De = d, Ate = d, ContaId = conta }));
        Assert.True(linha.DoImovel);
        Assert.Equal(0, (await fin.DreAsync(admin, ano)).Linhas.Last().Total);
        Assert.Equal(-100, (await fin.DreAsync(admin, ano)).ForaDoResultado.Last().Total);

        // título em aberto do imóvel: a conciliação liquida pela data e conta do banco
        var aberto = await livro.CriarAsync(admin, imovel, new LancamentoForm
        {
            Tipo = TipoConta.DESPESA, Data = d.AddDays(10), Valor = 80, Descricao = "Material", ContaId = orcamentaria, Liquidado = false,
        });
        await conc.ImportarAsync(admin, conta, "itau.ofx", OfxSgml(d.AddDays(20), 0, ("M1", d.AddDays(12), "-80,00", "COMPRA MATERIAL")));
        var p = Assert.Single((await conc.ConciliacaoAsync(admin, conta)).Pendentes);
        Assert.Equal((TipoSugestao.LANCAMENTO_IMOVEL, aberto), (p.Sugestao.Tipo, p.Sugestao.Id!.Value));
        await conc.ConciliarImovelAsync(admin, p.Id, aberto);
        Assert.Equal(320, (await fin.ContasAsync(admin)).Single(c => c.Conta.Id == conta).Saldo);
        await Assert.ThrowsAsync<RegraNegocioException>(() => livro.ExcluirAsync(admin, aberto));
    }

    [Fact]
    public async Task DRE_sai_em_Excel_e_PDF()
    {
        var admin = await Admin();
        var fin = S<FinanceiroEmpresaService>();
        var xlsx = await fin.DreExcelAsync(admin, 2026);
        Assert.Equal("dre-2026.xlsx", xlsx.Nome);
        Assert.True(xlsx.Conteudo.Length > 1000);
        var pdf = await fin.DrePdfAsync(admin, 2026);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf.Conteudo, 0, 4));
    }
}
