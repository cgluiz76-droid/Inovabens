using ClosedXML.Excel;
using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Dominio;
using Inovabens.Core.Financeiro;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Locacao;
using Inovabens.Core.Pessoas;
using Inovabens.Core.Servicos;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Divisão do recebimento por natureza (sem banco): base do informe, da DIMOB e da declaração ao inquilino.</summary>
public class PartilhaRecebimentoTests
{
    private static List<FaturaLocacaoItem> Itens() =>
    [
        new() { Tipo = TipoItemFatura.ALUGUEL, Valor = 1000 },
        new() { Tipo = TipoItemFatura.CONDOMINIO, Valor = 300 },
        new() { Tipo = TipoItemFatura.IPTU, Valor = 100 },
        new() { Tipo = TipoItemFatura.MANUTENCAO, Valor = 200 },
    ];

    [Fact]
    public void Separa_aluguel_de_encargos_e_tira_o_reembolso_de_manutencao()
    {
        var p = LocacaoRelatoriosService.Partilha(Itens(), 1600, 20, 5);
        Assert.Equal(1025, p.Aluguel); // aluguel + multa + juros
        Assert.Equal(300, p.Condominio);
        Assert.Equal(100, p.Iptu);
        Assert.Equal(1425, p.Total); // = bruto do repasse (sem os 200 do conserto)
    }

    [Fact]
    public void Pagamento_parcial_divide_na_proporcao_e_parte_do_locador_fecha_no_aluguel()
    {
        var p = LocacaoRelatoriosService.Partilha(Itens(), 800, 0, 0); // metade da fatura
        Assert.Equal(150, p.Condominio);
        Assert.Equal(50, p.Iptu);
        Assert.Equal(500, p.Aluguel);
        var parte = p.Parte(233.33m); // ~1/3 do bruto (700)
        Assert.Equal(233.33m, parte.Total);
        Assert.Equal(50, parte.Condominio);
        Assert.Equal(16.67m, parte.Iptu);
        Assert.Equal("1 a 30 dias", LocacaoRelatoriosService.FaixaAtraso(30));
        Assert.Equal("mais de 90 dias", LocacaoRelatoriosService.FaixaAtraso(91));
    }
}

/// <summary>Locação, fase 6: informe, DIMOB, relatório gerencial e documentos de cobrança/quitação.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class LocacaoRelatoriosTests : IAsyncLifetime
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

    private async Task<Guid> Pessoa(UsuarioAtual u, string nome) =>
        await S<PessoasService>().CriarAsync(u, new PessoaForm { NomeRazao = Unico(nome) });

    [Fact]
    public async Task Informe_dimob_gerencial_e_documentos_do_contrato()
    {
        var admin = await Admin();
        var loc = S<LocacaoService>();
        var rel = S<LocacaoRelatoriosService>();
        var dono = await Pessoa(admin, "Clara Locadora");
        var inquilino = await Pessoa(admin, "Davi Inquilino");
        var imovel = await S<ImoveisService>().CriarAsync(admin, new ImovelForm
        {
            NomeDrive = Unico("Rel"), Titulo = Unico("Sala"), Tipo = "casa", Finalidade = FinalidadeImovel.LOCACAO, PrecoLocacao = 1000, PrecoVenda = 200000,
        });
        await S<ImoveisService>().SalvarProprietariosAsync(admin, imovel, [new ProprietarioEntrada { PessoaId = dono, Percentual = 100 }]);
        var form = await loc.NovoParaImovelAsync(admin, imovel);
        var hoje = DatasBR.Hoje();
        form.Inicio = new DateOnly(hoje.Year, hoje.Month, 1).AddMonths(-3);
        form.ValorAluguel = 1000;
        form.ValorCondominio = 300;
        form.IntermediacaoValor = 0;
        form.TaxaAdministracaoPct = 10;
        form.Partes.Add(new ParteForm { PessoaId = inquilino, Papel = PapelContratoLocacao.LOCATARIO });
        var contrato = await loc.CriarAsync(admin, form);
        await loc.AtivarAsync(admin, contrato);

        // faturas vencidas: cobrança e notificação saem; 2ª via com atualização
        var faturas = (await loc.ObterAsync(admin, contrato)).Faturas;
        var vencida = faturas.First(f => f.Atrasada(hoje));
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await rel.CobrancaPdfAsync(admin, contrato, notificacao: true)).Conteudo[..4]));
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await rel.SegundaViaPdfAsync(admin, vencida.Id)).Conteudo[..4]));

        // recebe a primeira fatura cheia (sem atraso): 1000 de aluguel + 300 de condomínio
        var f = faturas[0];
        var conta = await S<FinanceiroEmpresaService>().CriarContaAsync(admin, new ContaBancariaForm { Nome = Unico("Cora"), DataSaldoInicial = new(2020, 1, 1) });
        var data = hoje;
        await loc.ReceberAsync(admin, f.Id, new RecebimentoLocacaoForm { Data = data, Principal = f.Total, Multa = 0, Juros = 0, ContaBancariaId = conta });
        var recebido = f.Itens.Where(i => i.Tipo is TipoItemFatura.ALUGUEL or TipoItemFatura.DESCONTO).Sum(i => i.Valor); // pode ser pró-rata

        Assert.Contains(await rel.LocadoresAsync(admin, data.Year), o => o.Id == dono);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await rel.InformeRendimentosPdfAsync(admin, dono, data.Year)).Conteudo[..4]));
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await rel.QuitacaoAnualPdfAsync(admin, contrato, data.Year)).Conteudo[..4]));

        // DIMOB: aluguel do mês sem o condomínio; comissão = taxa de 10%
        using (var wb = new XLWorkbook(new MemoryStream((await rel.DimobExcelAsync(admin, data.Year)).Conteudo)))
        {
            var ws = wb.Worksheet("Locações");
            var codigo = (await loc.ObterAsync(admin, contrato)).Codigo;
            var linha = ws.RowsUsed().Single(r => r.Cell(1).GetString() == codigo);
            var col = 12 + (data.Month - 1) * 3;
            Assert.Equal(recebido, linha.Cell(col).GetValue<decimal>());
            Assert.Equal(decimal.Round(recebido * 0.10m, 2), linha.Cell(col + 1).GetValue<decimal>());
            Assert.Contains(wb.Worksheet("Conferir").RowsUsed(), r => r.Cell(1).GetString() == codigo && r.Cell(3).GetString().Contains("CPF/CNPJ do locador"));
        }

        // relatório gerencial: abas e a inadimplência deste contrato
        using (var wb = new XLWorkbook(new MemoryStream((await rel.GerencialExcelAsync(admin, data.Year)).Conteudo)))
        {
            foreach (var aba in new[] { "Inadimplência", "Vencimentos", "Repasses", "Receita da administração", "Vacância", "Tempo para alugar",
                         "Chamados", "Manutenção por imóvel", "Rentabilidade", "Carteira por proprietário" })
                Assert.True(wb.Worksheets.Contains(aba), aba);
            var codigo = (await loc.ObterAsync(admin, contrato)).Codigo;
            Assert.Contains(wb.Worksheet("Inadimplência").RowsUsed(), r => r.Cell(1).GetString() == codigo);
            var rent = wb.Worksheet("Rentabilidade").RowsUsed().Single(r => r.Cell(1).GetString() == codigo);
            Assert.Equal(0.06, rent.Cell(6).GetDouble(), 4); // 12.000 / 200.000
        }

        var painel = await loc.PainelAsync(admin);
        Assert.True(painel.FaturasAtrasadas > 0);
    }

    [Fact]
    public async Task Cobranca_sem_atraso_avisa()
    {
        var admin = await Admin();
        var loc = S<LocacaoService>();
        var imovel = await S<ImoveisService>().CriarAsync(admin, new ImovelForm
        {
            NomeDrive = Unico("Rel2"), Titulo = Unico("Casa"), Tipo = "casa", Finalidade = FinalidadeImovel.LOCACAO, PrecoLocacao = 900,
        });
        var form = await loc.NovoParaImovelAsync(admin, imovel);
        form.Modalidade = ModalidadeLocacao.PROPRIO;
        form.Inicio = DatasBR.Hoje();
        form.Partes.Add(new ParteForm { PessoaId = await Pessoa(admin, "Eva Inquilina"), Papel = PapelContratoLocacao.LOCATARIO });
        var id = await loc.CriarAsync(admin, form);
        await Assert.ThrowsAsync<RegraNegocioException>(() => S<LocacaoRelatoriosService>().CobrancaPdfAsync(admin, id, false));
    }
}
