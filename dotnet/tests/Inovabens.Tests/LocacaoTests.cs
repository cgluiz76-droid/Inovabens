using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Financeiro;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Locacao;
using Inovabens.Core.Pessoas;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Cálculos da locação (sem banco): pró-rata, vencimento, multa e juros, rateio do repasse e intermediação.</summary>
public class CalculoLocacaoTests
{
    private static ContratoLocacao Contrato(DateOnly inicio, int meses = 12) => new()
    {
        Inicio = inicio, PrazoMeses = meses, DiaVencimento = 10, ValorAluguel = 3000, ValorCondominio = 600,
    };

    [Fact]
    public void Primeiro_e_ultimo_mes_proporcionais()
    {
        var c = Contrato(new(2026, 9, 21), 2); // 21/09 a 20/11
        Assert.Equal([new(2026, 9, 1), new(2026, 10, 1), new(2026, 11, 1)], CalculoLocacao.Competencias(c.Inicio, c.Fim));

        var set = CalculoLocacao.Itens(c, new(2026, 9, 1)); // 10 de 30 dias
        Assert.Equal(1000, set.Single(i => i.Tipo == TipoItemFatura.ALUGUEL).Valor);
        Assert.Equal(200, set.Single(i => i.Tipo == TipoItemFatura.CONDOMINIO).Valor);
        Assert.Contains("10 de 30 dias", set[0].Descricao);
        Assert.Equal(3000, CalculoLocacao.Itens(c, new(2026, 10, 1))[0].Valor);
        Assert.Equal(2000, CalculoLocacao.Itens(c, new(2026, 11, 1))[0].Valor); // 20 de 30 dias
    }

    [Fact]
    public void Vencimento_no_mes_seguinte_ou_adiantado()
    {
        Assert.Equal(new DateOnly(2026, 10, 10), CalculoLocacao.Vencimento(new(2026, 9, 1), 10, false, new(2026, 9, 21)));
        // adiantado no 1º mês não vence antes do início
        Assert.Equal(new DateOnly(2026, 9, 21), CalculoLocacao.Vencimento(new(2026, 9, 1), 10, true, new(2026, 9, 21)));
        Assert.Equal(new DateOnly(2026, 10, 10), CalculoLocacao.Vencimento(new(2026, 10, 1), 10, true, new(2026, 9, 21)));
    }

    [Fact]
    public void Multa_e_juros_pro_rata_com_fim_de_semana()
    {
        // 10/10/2026 é sábado: pagar na segunda (12/10) não tem acréscimo
        Assert.Equal(0, CalculoLocacao.Atraso(1000, new(2026, 10, 10), new(2026, 10, 12), 10, 1).Total);
        var a = CalculoLocacao.Atraso(1000, new(2026, 10, 10), new(2026, 10, 25), 10, 1);
        Assert.Equal(15, a.DiasAtraso);
        Assert.Equal(100, a.Multa);
        Assert.Equal(5, a.Juros); // 1% ao mês ÷ 30 × 15 dias
    }

    [Fact]
    public void Repasse_rateado_com_intermediacao_e_taxa()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        FaturaLocacaoItem[] itens =
        [
            new() { Tipo = TipoItemFatura.ALUGUEL, Valor = 3000 },
            new() { Tipo = TipoItemFatura.CONDOMINIO, Valor = 600 },
        ];
        // 1º mês: intermediação = 1º aluguel inteiro; taxa só sobre multa/juros
        var r1 = CalculoLocacao.Repasses([(a, 60), (b, 40)], itens, 3600, 100, 10, 10, 3000);
        Assert.Equal(3000, r1.Sum(r => r.Intermediacao));
        Assert.Equal(11, r1.Sum(r => r.Taxa));
        Assert.Equal(3710, r1.Sum(r => r.Bruto));
        Assert.Equal(699, r1.Sum(r => r.Liquido));
        Assert.Equal(2226, r1[0].Bruto); // 60%

        // mês cheio sem intermediação: 10% do aluguel, condomínio passa inteiro
        var r2 = CalculoLocacao.Repasses([(a, 60), (b, 40)], itens, 3600, 0, 0, 10, 0);
        Assert.Equal(300, r2.Sum(r => r.Taxa));
        Assert.Equal(3300, r2.Sum(r => r.Liquido));

        // recebimento parcial: base proporcional
        var r3 = CalculoLocacao.Repasses([(a, 100)], itens, 1800, 0, 0, 10, 0);
        Assert.Equal(150, r3.Single().Taxa);
    }

    [Fact]
    public void Repasse_cai_em_dia_util()
    {
        Assert.Equal(new DateOnly(2026, 10, 19), CalculoLocacao.DataRepasse(new(2026, 10, 13), 5)); // 18/10 domingo → 19/10
    }
}

/// <summary>Fluxo da locação no banco: contrato, ativação, faturas, recebimento com repasse, recibo, estorno e encerramento.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class LocacaoTests : IAsyncLifetime
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
        await S<PessoasService>().CriarAsync(u, new PessoaForm { NomeRazao = Unico(nome), Whatsapp = "(11) 98888-7777" });

    private async Task<Guid> Imovel(UsuarioAtual u, FinalidadeImovel finalidade, params (Guid Pessoa, decimal Pct)[] donos)
    {
        var id = await S<ImoveisService>().CriarAsync(u, new ImovelForm
        {
            NomeDrive = Unico("Locação"), Titulo = Unico("Apto para alugar"), Tipo = "casa", Finalidade = finalidade, PrecoLocacao = 2000,
        });
        if (donos.Length > 0)
            await S<ImoveisService>().SalvarProprietariosAsync(u, id, [.. donos.Select(d => new ProprietarioEntrada { PessoaId = d.Pessoa, Percentual = d.Pct })]);
        return id;
    }

    [Fact]
    public async Task Administracao_receber_repassar_estornar_e_encerrar()
    {
        var admin = await Admin();
        var loc = S<LocacaoService>();
        var fin = S<FinanceiroEmpresaService>();
        var conta = await fin.CriarContaAsync(admin, new ContaBancariaForm { Nome = Unico("Cora"), DataSaldoInicial = new(2020, 1, 1) });
        Guid dono1 = await Pessoa(admin, "Ana Locadora"), dono2 = await Pessoa(admin, "Bia Locadora"), inquilino = await Pessoa(admin, "Caio Inquilino");
        var imovel = await Imovel(admin, FinalidadeImovel.LOCACAO, (dono1, 60), (dono2, 40));

        var hoje = DatasBR.Hoje();
        var inicio = new DateOnly(hoje.Year, hoje.Month, 15).AddMonths(-2);
        var form = await loc.NovoParaImovelAsync(admin, imovel);
        Assert.Equal(2, form.Partes.Count(p => p.Papel == PapelContratoLocacao.LOCADOR)); // locadores = proprietários
        form.Inicio = inicio;
        form.PrazoMeses = 30;
        form.ValorCondominio = 400;
        form.Partes.Add(new ParteForm { PessoaId = inquilino, Papel = PapelContratoLocacao.LOCATARIO });
        var id = await loc.CriarAsync(admin, form);

        // percentuais errados barram a ativação
        var errado = await loc.FormularioAsync(admin, id);
        errado.Partes.First(p => p.Papel == PapelContratoLocacao.LOCADOR).Percentual = 50;
        await loc.AtualizarAsync(admin, id, errado);
        await Assert.ThrowsAsync<RegraNegocioException>(() => loc.AtivarAsync(admin, id));
        await loc.AtualizarAsync(admin, id, form);

        await loc.AtivarAsync(admin, id);
        var d = await loc.ObterAsync(admin, id);
        Assert.Equal(StatusContratoLocacao.ATIVO, d.Contrato.Status);
        Assert.Equal(2000, d.Contrato.IntermediacaoValor); // padrão: 1º aluguel
        Assert.True(d.Faturas.Count >= 2);
        var f1 = d.Faturas[0];
        Assert.Equal(inicio.AddDays(-14), f1.Competencia);
        Assert.Equal(new DateOnly(inicio.Year, inicio.Month, 10).AddMonths(1), f1.Vencimento);
        var esperado = CalculoLocacao.Itens(d.Contrato, f1.Competencia);
        Assert.Equal(esperado.Sum(i => i.Valor), f1.Total);
        Assert.True(f1.Total < 2400); // proporcional (começou no dia 15)
        Assert.Equal(SituacaoImovel.LOCADO, (await S<ImoveisService>().DetalheAsync(admin, imovel)).Dados.Situacao);
        Assert.Contains("LOCATARIO", (await S<PessoasService>().ObterAsync(admin, inquilino)).Papeis.Select(p => p.Papel));

        // régua de cobrança: a 1ª fatura está em atraso, com link do WhatsApp
        var cobranca = (await loc.CobrancasAsync(admin)).Single(c => c.FaturaId == f1.Id);
        Assert.StartsWith("Atraso", cobranca.Etapa);
        Assert.StartsWith("https://wa.me/5511988887777?text=", cobranca.LinkWhatsapp);

        // recebe a 1ª com 5 dias de atraso: multa e juros do contrato
        var data1 = f1.Vencimento.AddDays(5);
        var (saldo1, atraso1) = await loc.SimularRecebimentoAsync(admin, f1.Id, data1);
        Assert.Equal(f1.Total, saldo1);
        Assert.True(atraso1.Total > 0);
        var pg1 = await loc.ReceberAsync(admin, f1.Id, new RecebimentoLocacaoForm
        {
            Data = data1, Principal = saldo1, Multa = atraso1.Multa, Juros = atraso1.Juros, ContaBancariaId = conta,
        });

        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
        {
            var pg = await db.FaturaLocacaoPagamentos.SingleAsync(p => p.Id == pg1);
            var lancs = await db.LancamentosEmpresa.Include(l => l.Categoria).Where(l => pg.LancamentosEmpresaIds.Contains(l.Id)).ToListAsync();
            var repasses = await db.RepassesLocacao.Where(r => r.PagamentoId == pg1).ToListAsync();
            var aluguel1 = esperado.Single(i => i.Tipo == TipoItemFatura.ALUGUEL).Valor;
            // 1º mês: todo o aluguel fica como intermediação; taxa só sobre multa e juros
            Assert.Equal(aluguel1, repasses.Sum(r => r.Intermediacao));
            Assert.Equal(decimal.Round((atraso1.Multa + atraso1.Juros) * 0.10m, 2, MidpointRounding.AwayFromZero), repasses.Sum(r => r.TaxaAdministracao));
            Assert.Equal(pg.Total, repasses.Sum(r => r.Liquido + r.TaxaAdministracao + r.Intermediacao));
            Assert.Equal([dono1, dono2], repasses.OrderByDescending(r => r.Percentual).Select(r => r.PessoaId));
            // entradas pagas na conta = total recebido; repasses a pagar = líquido dos locadores
            Assert.Equal(pg.Total, lancs.Where(l => l.Valor > 0).Sum(l => l.Valor));
            Assert.All(lancs.Where(l => l.Valor > 0), l => Assert.Equal(data1, l.DataPagamento));
            Assert.Contains(lancs, l => l.Categoria!.Nome == LocacaoService.CategoriaIntermediacao && l.Valor == aluguel1);
            Assert.Equal(-repasses.Sum(r => r.Liquido), lancs.Where(l => l.Categoria!.Nome == LocacaoService.CategoriaRepasse).Sum(l => l.Valor));
            Assert.All(lancs.Where(l => l.Valor < 0), l => Assert.Null(l.DataPagamento));
        }

        // 2ª fatura (mês cheio): o resto da intermediação e 10% sobre o que sobrou do aluguel
        var f2 = (await loc.ObterAsync(admin, id)).Faturas[1];
        var pg2 = await loc.ReceberAsync(admin, f2.Id, new RecebimentoLocacaoForm { Data = f2.Vencimento < hoje ? f2.Vencimento : hoje, Principal = f2.Total, ContaBancariaId = conta });
        d = await loc.ObterAsync(admin, id);
        Assert.Equal(2000, d.IntermediacaoRetida);
        Assert.Equal(StatusFaturaLocacao.PAGA, d.Faturas[0].Status);
        Assert.Equal(StatusFaturaLocacao.PAGA, d.Faturas[1].Status);
        var rep2 = d.Repasses.Where(r => d.Faturas[1].Pagamentos.Any(p => p.ReciboNumero == r.ReciboNumero)).ToList();
        Assert.Equal(decimal.Round((2000 - rep2.Sum(r => r.Intermediacao)) * 0.10m, 2), rep2.Sum(r => r.Taxa));

        // recibo em PDF
        var recibo = await loc.ReciboPdfAsync(admin, pg1);
        Assert.True(recibo.Conteudo.Length > 1000);
        Assert.Equal(Inovabens.Core.Relatorios.RelatoriosService.TipoPdf, recibo.Tipo);

        // repasse pago trava o estorno daquele recebimento; o outro pode ser estornado
        var rep1 = d.Repasses.First(r => d.Faturas[0].Pagamentos.Any(p => p.ReciboNumero == r.ReciboNumero));
        await loc.PagarRepasseAsync(admin, rep1.Id, hoje, conta);
        await Assert.ThrowsAsync<RegraNegocioException>(() => loc.EstornarAsync(admin, pg1));
        await loc.EstornarAsync(admin, pg2);
        d = await loc.ObterAsync(admin, id);
        Assert.Equal(StatusFaturaLocacao.ABERTA, d.Faturas[1].Status);
        Assert.All(d.Repasses.Where(r => r.ReciboNumero == d.Faturas[1].Pagamentos.Single().ReciboNumero), r => Assert.True(r.Estornado));
        Assert.Equal(d.Repasses.Where(r => !r.Estornado).Sum(r => r.Intermediacao), d.IntermediacaoRetida);

        // encerrado: o imóvel volta a ficar disponível
        await loc.EncerrarAsync(admin, id, hoje, "Entrega das chaves");
        Assert.Equal(SituacaoImovel.DISPONIVEL, (await S<ImoveisService>().DetalheAsync(admin, imovel)).Dados.Situacao);
        Assert.DoesNotContain(await loc.ListarAsync(admin, StatusContratoLocacao.ATIVO), c => c.Id == id);
    }

    [Fact]
    public async Task Imovel_proprio_vira_receita_no_financeiro_do_imovel()
    {
        var admin = await Admin();
        var loc = S<LocacaoService>();
        var inquilino = await Pessoa(admin, "Davi Inquilino");
        var imovel = await Imovel(admin, FinalidadeImovel.AMBOS);
        var hoje = DatasBR.Hoje();
        var id = await loc.CriarAsync(admin, new ContratoForm
        {
            ImovelId = imovel, Modalidade = ModalidadeLocacao.PROPRIO, Inicio = new DateOnly(hoje.Year, hoje.Month, 1).AddMonths(-1),
            ValorAluguel = 1500, TaxaAdministracaoPct = 10,
            Partes = [new ParteForm { PessoaId = inquilino, Papel = PapelContratoLocacao.LOCATARIO }],
        });
        await loc.AtivarAsync(admin, id);
        var d = await loc.ObterAsync(admin, id);
        Assert.Equal(0, d.Contrato.TaxaAdministracaoPct); // próprio: sem taxa nem intermediação
        // à venda e locado: a situação comercial não muda
        Assert.NotEqual(SituacaoImovel.LOCADO, (await S<ImoveisService>().DetalheAsync(admin, imovel)).Dados.Situacao);
        Assert.NotNull(await loc.AtivoDoImovelAsync(admin, imovel));

        var f = d.Faturas[0];
        Assert.Equal(1500, f.Total);
        var pg = await loc.ReceberAsync(admin, f.Id, new RecebimentoLocacaoForm { Data = hoje < f.Vencimento ? hoje : f.Vencimento, Principal = 1500 });
        await using var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync();
        var p = await db.FaturaLocacaoPagamentos.SingleAsync(x => x.Id == pg);
        var l = await db.LancamentosFinanceiros.SingleAsync(x => x.Id == p.LancamentoImovelId);
        Assert.Equal(imovel, l.ImovelId);
        Assert.Equal(1500, l.Valor);
        Assert.Equal(SituacaoLancamento.LIQUIDADO, l.Situacao);
        Assert.Empty(await db.RepassesLocacao.Where(r => r.ContratoId == id).ToListAsync());
    }

    [Fact]
    public async Task Garantia_unica_e_caucao_limitada()
    {
        var admin = await Admin();
        var loc = S<LocacaoService>();
        var imovel = await Imovel(admin, FinalidadeImovel.LOCACAO);
        var e = await Assert.ThrowsAsync<RegraNegocioException>(() => loc.CriarAsync(admin, new ContratoForm
        {
            ImovelId = imovel, Inicio = DatasBR.Hoje(), ValorAluguel = 1000, Garantia = GarantiaLocacao.CAUCAO, GarantiaValor = 3500,
        }));
        Assert.Contains("3 aluguéis", e.Message);
        await Assert.ThrowsAsync<RegraNegocioException>(() => loc.CriarAsync(admin, new ContratoForm
        {
            ImovelId = imovel, Inicio = DatasBR.Hoje(), ValorAluguel = 1000, Garantia = GarantiaLocacao.FIADOR, PagamentoAntecipado = true,
        }));
    }
}
