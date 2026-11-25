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

    [Fact]
    public async Task Reajuste_aditivo_preferencia_avisos_e_rescisao()
    {
        var admin = await Admin();
        var loc = S<LocacaoService>();
        var fabrica = S<IDbContextFactory<InovabensDb>>();
        Guid dono = await Pessoa(admin, "Edu Locador"), inquilino = await Pessoa(admin, "Fábio Inquilino");
        var imovel = await Imovel(admin, FinalidadeImovel.LOCACAO, (dono, 100));
        var hoje = DatasBR.Hoje();
        // aniversário no dia 1º do mês que vem; o prazo de 12 meses termina no fim deste mês
        var inicio = new DateOnly(hoje.Year, hoje.Month, 1).AddMonths(-11);
        var form = await loc.NovoParaImovelAsync(admin, imovel);
        form.Inicio = inicio;
        form.PrazoMeses = 12;
        form.ValorAluguel = 2000;
        form.Garantia = GarantiaLocacao.CAUCAO;
        form.GarantiaValor = 1000;
        form.Partes.Add(new ParteForm { PessoaId = inquilino, Papel = PapelContratoLocacao.LOCATARIO });
        var id = await loc.CriarAsync(admin, form);
        await loc.AtivarAsync(admin, id);

        // índice do período: usa o que já existe no banco e completa os meses que faltam com 0,5%
        var (de, ate) = CalculoLocacao.PeriodoReajuste(inicio.AddMonths(12));
        await using (var db = await fabrica.CreateDbContextAsync())
        {
            var existentes = await db.IndicesSerie.Where(s => s.Codigo == CodigoIndice.IGPM && s.Competencia >= de && s.Competencia <= ate)
                .Select(s => s.Competencia).ToListAsync();
            for (var m = de; m <= ate; m = m.AddMonths(1))
                if (!existentes.Contains(m)) db.IndicesSerie.Add(new IndiceSerie { Codigo = CodigoIndice.IGPM, Competencia = m, VariacaoMensalPct = 0.5m, Fonte = "TESTE" });
            await db.SaveChangesAsync();
        }
        var previa = await loc.ReajustePreviaAsync(admin, id);
        Assert.Equal(inicio.AddMonths(12), previa.Aniversario);
        Assert.NotNull(previa.Percentual);
        await using (var db = await fabrica.CreateDbContextAsync())
        {
            var vars = await db.IndicesSerie.Where(s => s.Codigo == CodigoIndice.IGPM && s.Competencia >= de && s.Competencia <= ate)
                .OrderBy(s => s.Competencia).Select(s => s.VariacaoMensalPct).ToListAsync();
            Assert.Equal(CalculoLocacao.Reajuste(2000, vars).Novo, previa.ValorNovo);
        }
        Assert.True((await loc.CartaReajustePdfAsync(admin, id)).Conteudo.Length > 1000);

        await loc.AplicarReajusteAsync(admin, id);
        var d = await loc.ObterAsync(admin, id);
        Assert.Equal(previa.ValorNovo, d.Contrato.ValorAluguel);
        Assert.Equal(previa.Aniversario, d.Contrato.UltimoReajuste);
        var hr = Assert.Single(d.Historico, h => h.Tipo == TipoHistoricoLocacao.REAJUSTE);
        Assert.Equal(2000, hr.ValorAnterior);
        Assert.True((await loc.CartaReajustePdfAsync(admin, id, hr.Id)).Conteudo.Length > 1000);
        // a fatura do mês do aniversário já sai com o novo valor; antes dele, o antigo
        await loc.GerarFaturasAteAsync(admin, id, hoje.AddMonths(3));
        d = await loc.ObterAsync(admin, id);
        var aniv = d.Faturas.Single(f => f.Competencia == previa.Aniversario);
        Assert.Equal(previa.ValorNovo, aniv.Itens.Single(i => i.Tipo == TipoItemFatura.ALUGUEL).Valor);

        // fim do prazo em menos de 60 dias: alerta no sino e tarefa na agenda (o reajuste já foi aplicado: sem aviso)
        await loc.VerificarVencimentosAsync(hoje);
        await using (var db = await fabrica.CreateDbContextAsync())
        {
            var avisos = await db.LocacaoAvisos.Where(a => a.ContratoId == id).ToListAsync();
            var fim = Assert.Single(avisos);
            Assert.Equal(TipoAvisoLocacao.FIM, fim.Tipo);
            Assert.True(await db.Alertas.AnyAsync(a => a.LocacaoAvisoId == fim.Id && a.UsuarioId == admin.Id && a.ResolvidoEm == null));
            var tarefa = await db.Compromissos.SingleAsync(c => c.Id == fim.CompromissoId);
            Assert.Equal(imovel, tarefa.ImovelId);
        }
        await loc.VerificarVencimentosAsync(hoje); // idempotente
        await using (var db = await fabrica.CreateDbContextAsync())
            Assert.Single(await db.LocacaoAvisos.Where(a => a.ContratoId == id).ToListAsync());
        Assert.Contains(await loc.VencimentosAsync(admin), v => v.ContratoId == id && v.Tipo == TipoAvisoLocacao.FIM);

        // renovação: novo fim resolve o aviso antigo
        await loc.AditivoAsync(admin, id, new AditivoForm { Tipo = TipoHistoricoLocacao.RENOVACAO, Data = hoje, MesesAdicionais = 12, Descricao = "Renovado por mais 12 meses" });
        d = await loc.ObterAsync(admin, id);
        Assert.Equal(24, d.Contrato.PrazoMeses);
        await loc.VerificarVencimentosAsync(hoje);
        await using (var db = await fabrica.CreateDbContextAsync())
            Assert.False(await db.Alertas.AnyAsync(a => a.ResolvidoEm == null && db.LocacaoAvisos.Any(v => v.Id == a.LocacaoAvisoId && v.ContratoId == id)));

        // preferência na venda (art. 27)
        await loc.NotificarPreferenciaAsync(admin, id, new PreferenciaForm { Valor = 450000, Condicoes = "À vista, escritura em 60 dias" });
        Assert.True((await loc.NotificacaoPreferenciaPdfAsync(admin, id)).Conteudo.Length > 1000);
        await loc.ResponderPreferenciaAsync(admin, id, RespostaPreferencia.RECUSADA);
        d = await loc.ObterAsync(admin, id);
        Assert.Equal(RespostaPreferencia.RECUSADA, d.Contrato.PreferenciaResposta);
        Assert.Equal(2, d.Historico.Count(h => h.Tipo == TipoHistoricoLocacao.PREFERENCIA));

        // chave com o locatário volta na rescisão
        var chaves = S<ChavesService>();
        var chave = await chaves.CriarAsync(admin, imovel, new ChaveForm { Codigo = await chaves.ProximoCodigoAsync(), Descricao = "Porta da sala" });
        await chaves.EntregarAsync(admin, chave, new EntregaChaveForm { PessoaId = inquilino, DevolverAte = DatasBR.Agora().AddDays(30) });

        // rescisão pelo locatário antes do fim: multa proporcional + vistoria, caução abate
        var resc = new RescisaoForm { EntregaChaves = hoje, Iniciativa = IniciativaRescisao.LOCATARIO, DebitosVistoria = 300, DescricaoVistoria = "Pintura" };
        var calc = await loc.SimularRescisaoAsync(admin, id, resc);
        Assert.True(calc.MultaRescisoria > 0);
        Assert.Equal(1000, calc.CaucaoAbatida);
        await loc.RescindirAsync(admin, id, resc);
        d = await loc.ObterAsync(admin, id);
        Assert.Equal(StatusContratoLocacao.ENCERRADO, d.Contrato.Status);
        Assert.Equal(hoje, d.Contrato.EncerradoEm);
        var acerto = Assert.Single(d.Faturas, f => f.Rescisao);
        Assert.Equal(calc.MultaRescisoria + 300 - 1000, acerto.Total);
        Assert.Contains(acerto.Itens, i => i.Tipo == TipoItemFatura.MULTA_RESCISORIA);
        Assert.All(d.Faturas.Where(f => !f.Rescisao && f.Competencia > CalculoLocacao.MesDe(hoje)), f => Assert.Equal(StatusFaturaLocacao.CANCELADA, f.Status));
        // mês da entrega: só os dias usados
        var ultimo = d.Faturas.Single(f => !f.Rescisao && f.Competencia == CalculoLocacao.MesDe(hoje));
        var diasMes = DateTime.DaysInMonth(hoje.Year, hoje.Month);
        if (hoje.Day < diasMes) Assert.Contains($"{hoje.Day} de {diasMes} dias", ultimo.Itens[0].Descricao);
        Assert.Equal(SituacaoImovel.DISPONIVEL, (await S<ImoveisService>().DetalheAsync(admin, imovel)).Dados.Situacao);
        await using (var db = await fabrica.CreateDbContextAsync())
            Assert.NotNull((await db.ChaveMovimentos.SingleAsync(m => m.ChaveId == chave)).DevolvidaEm);
        Assert.True((await loc.TermoRescisaoPdfAsync(admin, id)).Conteudo.Length > 1000);
        await Assert.ThrowsAsync<RegraNegocioException>(() => loc.RescindirAsync(admin, id, resc));
    }
}

/// <summary>Cálculos da fase 3: reajuste pelo índice acumulado, rescisão e avisos de vencimento.</summary>
public class CalculoLocacaoFase3Tests
{
    [Fact]
    public void Reajuste_acumulado_e_indice_negativo()
    {
        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2027, 8, 1)), CalculoLocacao.PeriodoReajuste(new(2027, 9, 15)));
        var (pct, novo) = CalculoLocacao.Reajuste(1000, [1, 1]);
        Assert.Equal(2.01m, pct);
        Assert.Equal(1020.10m, novo);
        var (neg, mantido) = CalculoLocacao.Reajuste(1000, [-1, 0.5m]);
        Assert.True(neg < 0);
        Assert.Equal(1000, mantido);
    }

    [Fact]
    public void Rescisao_multa_proporcional_aviso_previo_e_caucao()
    {
        var c = new ContratoLocacao
        {
            Inicio = new(2026, 1, 1), PrazoMeses = 30, ValorAluguel = 2000, ValorCondominio = 400, MultaRescisoriaAlugueis = 3,
            Garantia = GarantiaLocacao.CAUCAO, GarantiaValor = 6000,
        };
        var entrega = new DateOnly(2027, 1, 1);
        var r = CalculoLocacao.Rescisao(c, new RescisaoForm { EntregaChaves = entrega, Iniciativa = IniciativaRescisao.LOCATARIO, DebitosVistoria = 500 });
        var diasPrazo = c.Fim.DayNumber - c.Inicio.DayNumber + 1;
        var diasRest = c.Fim.DayNumber - entrega.DayNumber;
        Assert.Equal(decimal.Round(6000m * diasRest / diasPrazo, 2, MidpointRounding.AwayFromZero), r.MultaRescisoria);
        Assert.Equal(r.MultaRescisoria + 500, r.Debitos);
        Assert.Equal(r.Debitos, r.CaucaoAbatida); // caução maior que os débitos: abate tudo
        Assert.Equal(0, r.TotalAcerto);
        Assert.Equal(6000 - r.Debitos, r.ADevolver);

        // isento ou por acordo: sem multa
        Assert.Empty(CalculoLocacao.Rescisao(c, new RescisaoForm { EntregaChaves = entrega, Iniciativa = IniciativaRescisao.ACORDO }).Itens);
        Assert.Empty(CalculoLocacao.Rescisao(c, new RescisaoForm { EntregaChaves = entrega, Iniciativa = IniciativaRescisao.LOCATARIO, IsentarMulta = true }).Itens);

        // prazo indeterminado: aviso de 10 dias → faltam 20 de 30 dias de aluguel e encargos
        var depois = c.Fim.AddDays(40);
        var a = CalculoLocacao.Rescisao(c, new RescisaoForm { EntregaChaves = depois, AvisoEm = depois.AddDays(-10), Iniciativa = IniciativaRescisao.LOCATARIO });
        Assert.Equal(0, a.MultaRescisoria);
        Assert.Equal(20, a.DiasAvisoFaltando);
        Assert.Equal(1600, a.AvisoPrevio); // 2400 × 20/30
    }

    [Fact]
    public void Avisos_60_e_30_dias_antes()
    {
        var c = new ContratoLocacao { Inicio = new(2026, 1, 10), PrazoMeses = 12, ValorAluguel = 1000, Status = StatusContratoLocacao.ATIVO };
        // reajuste em 10/01/2027 e fim em 09/01/2027
        Assert.Empty(CalculoLocacao.AvisosDevidos(c, new(2026, 10, 1)));
        var a60 = CalculoLocacao.AvisosDevidos(c, new(2026, 11, 20));
        Assert.Equal(2, a60.Count);
        Assert.All(a60, x => Assert.Equal(60, x.Antecedencia));
        Assert.All(CalculoLocacao.AvisosDevidos(c, new(2026, 12, 20)), x => Assert.Equal(30, x.Antecedencia));
        // fim do prazo deixa de avisar 30 dias depois; reajuste não aplicado continua
        var tarde = CalculoLocacao.AvisosDevidos(c, new(2027, 3, 1));
        Assert.Equal(TipoAvisoLocacao.REAJUSTE, Assert.Single(tarde).Tipo);
        c.Status = StatusContratoLocacao.ENCERRADO;
        Assert.Empty(CalculoLocacao.AvisosDevidos(c, new(2026, 12, 20)));
    }
}
