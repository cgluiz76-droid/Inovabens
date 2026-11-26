using System.Collections.Concurrent;
using Inovabens.Core;
using Inovabens.Core.Admin;
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

/// <summary>Banco Cora simulado: guarda os boletos em memória; <see cref="Pagar"/> faz o papel do inquilino pagando.</summary>
public sealed class CoraSimulado : ICobrancaBancaria
{
    public static readonly CoraSimulado Instancia = new();
    public readonly ConcurrentDictionary<string, BoletoBanco> Boletos = new();
    public readonly ConcurrentQueue<BoletoPedido> Pedidos = new();
    public bool Habilitado => true;

    public Task<BoletoBanco> EmitirAsync(BoletoPedido p, CancellationToken ct = default)
    {
        Pedidos.Enqueue(p);
        var id = "inv_" + Guid.NewGuid().ToString("N");
        var b = new BoletoBanco(id, "OPEN", p.Valor, p.Vencimento, "23793.38128 " + id[4..14], "2379" + id[4..20], "00020126pix" + id, $"https://cora.test/{id}.pdf");
        Boletos[id] = b;
        return Task.FromResult(b);
    }

    public Task<BoletoBanco?> ConsultarAsync(string id, CancellationToken ct = default) => Task.FromResult(Boletos.TryGetValue(id, out var b) ? b : null);

    public Task CancelarAsync(string id, CancellationToken ct = default)
    {
        if (Boletos.TryGetValue(id, out var b))
        {
            if (b.Status == "PAID") throw new CoraRecusouException("PAY-0005: boleto pago não pode ser cancelado");
            Boletos[id] = b with { Status = "CANCELLED" };
        }
        return Task.CompletedTask;
    }

    public void Pagar(string id, decimal valor, DateOnly data, string forma = "PIX") =>
        Boletos[id] = Boletos[id] with { Status = "PAID", TotalPago = valor, PagoEm = data, Forma = forma };

    public Task<string> RegistrarWebhookAsync(string url, CancellationToken ct = default) => Task.FromResult("ok " + url);
    public Task<string> TestarAsync(CancellationToken ct = default) => Task.FromResult("ok");
}

/// <summary>Locação, fase 7: boleto/Pix com baixa automática pelo aviso do banco e portal do inquilino e do proprietário.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class CobrancaPortalTests : IAsyncLifetime
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

    /// <summary>CPF válido aleatório (dígitos verificadores calculados).</summary>
    private static string Cpf()
    {
        var d = Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10)).ToList();
        for (var n = 9; n < 11; n++)
        {
            var soma = d.Select((x, i) => x * (n + 1 - i)).Sum();
            var r = soma * 10 % 11;
            d.Add(r == 10 ? 0 : r);
        }
        return string.Concat(d);
    }

    /// <summary>Contrato ativo com inquilino (com CPF) e locador; início 3 meses atrás (faturas vencidas e a vencer).</summary>
    private async Task<(UsuarioAtual Admin, Guid Contrato, Guid Dono, string Token)> Cenario()
    {
        var admin = await Admin();
        var loc = S<LocacaoService>();
        var dono = await S<PessoasService>().CriarAsync(admin, new PessoaForm { NomeRazao = Unico("Olga Locadora") });
        var inquilino = await S<PessoasService>().CriarAsync(admin, new PessoaForm { NomeRazao = Unico("Caio Inquilino"), CpfCnpj = Cpf() });
        var imovel = await S<ImoveisService>().CriarAsync(admin, new ImovelForm
        {
            NomeDrive = Unico("Cora"), Titulo = Unico("Casa"), Tipo = "casa", Finalidade = FinalidadeImovel.LOCACAO, PrecoLocacao = 1000,
        });
        await S<ImoveisService>().SalvarProprietariosAsync(admin, imovel, [new ProprietarioEntrada { PessoaId = dono, Percentual = 100 }]);
        var form = await loc.NovoParaImovelAsync(admin, imovel);
        var hoje = DatasBR.Hoje();
        form.Inicio = new DateOnly(hoje.Year, hoje.Month, 1).AddMonths(-3);
        form.ValorAluguel = 1000;
        form.IntermediacaoValor = 0;
        form.TaxaAdministracaoPct = 10;
        form.Partes.Add(new ParteForm { PessoaId = inquilino, Papel = PapelContratoLocacao.LOCATARIO });
        var contrato = await loc.CriarAsync(admin, form);
        await loc.AtivarAsync(admin, contrato);

        // conta do Cora no financeiro e o token do webhook (configuração salva pela tela)
        var conta = Unico("Conta Cora");
        await S<FinanceiroEmpresaService>().CriarContaAsync(admin, new ContaBancariaForm { Nome = conta, DataSaldoInicial = new(2020, 1, 1) });
        var token = "tok" + Guid.NewGuid().ToString("N");
        await S<ConfiguracoesService>().SalvarAsync(admin, CatalogoConfiguracoes.Cora,
            new Dictionary<string, string> { ["cora.webhook_token"] = token, ["cora.conta"] = conta });
        Assert.Equal(token, S<InovabensOpcoes>().Integracoes.CoraWebhookToken);
        return (admin, contrato, dono, token);
    }

    [Fact]
    public async Task Boleto_pago_da_baixa_automatica_com_multa_e_repasse()
    {
        var (admin, contrato, _, token) = await Cenario();
        var loc = S<LocacaoService>();
        var cob = S<CobrancaBancariaService>();
        var hoje = DatasBR.Hoje();
        var vencida = (await loc.ObterAsync(admin, contrato)).Faturas.First(f => f.Atrasada(hoje));

        // vencida: boleto para hoje com multa e juros até hoje; reemitir cancela o anterior no banco
        var b1 = await cob.EmitirAsync(admin, vencida.Id);
        Assert.Equal(hoje, b1.Vencimento);
        Assert.True(b1.Valor > vencida.Saldo);
        var b2 = await cob.EmitirAsync(admin, vencida.Id);
        Assert.Equal("CANCELLED", CoraSimulado.Instancia.Boletos[b1.ExternoId].Status);
        Assert.Single(await cob.BoletosAsync(admin, contrato), b => b.Ativo && b.FaturaId == vencida.Id);

        // token errado: ignorado; aviso certo antes de pagar: nada muda
        Assert.False(await cob.ProcessarWebhookAsync("errado", b2.ExternoId));
        Assert.True(await cob.ProcessarWebhookAsync(token, b2.ExternoId));
        Assert.Equal(StatusFaturaLocacao.ABERTA, (await loc.ObterAsync(admin, contrato)).Faturas.Single(f => f.Id == vencida.Id).Status);

        // inquilino paga pelo Pix: o aviso dá baixa (principal = saldo; o resto vira multa e juros), uma vez só
        CoraSimulado.Instancia.Pagar(b2.ExternoId, b2.Valor, hoje);
        Assert.True(await cob.ProcessarWebhookAsync(token, b2.ExternoId));
        Assert.True(await cob.ProcessarWebhookAsync(token, b2.ExternoId));
        var d = await loc.ObterAsync(admin, contrato);
        var f = d.Faturas.Single(x => x.Id == vencida.Id);
        Assert.Equal(StatusFaturaLocacao.PAGA, f.Status);
        var pg = Assert.Single(f.Pagamentos);
        Assert.Equal(vencida.Saldo, pg.Principal);
        Assert.Equal(b2.Valor, pg.Total);
        Assert.True(pg.Multa > 0);
        Assert.Equal("Pix (Cora)", pg.Forma);
        Assert.Contains(d.Repasses, r => r.ReciboNumero == pg.ReciboNumero && !r.Estornado);
        var b = (await cob.BoletosAsync(admin, contrato)).Single(x => x.Id == b2.Id);
        Assert.Equal(pg.Id, b.PagamentoId);
        Assert.Null(b.Erro);

        // outra fatura quitada à mão com boleto aberto: a conferência cancela o boleto no banco
        var aberta = d.Faturas.First(x => x.Status == StatusFaturaLocacao.ABERTA && !x.Atrasada(hoje));
        var b3 = await cob.EmitirAsync(admin, aberta.Id);
        var contaId = await Conta(S<InovabensOpcoes>().Integracoes.CoraConta);
        await loc.ReceberAsync(admin, aberta.Id, new RecebimentoLocacaoForm { Data = hoje, Principal = aberta.Saldo, ContaBancariaId = contaId });
        await cob.SincronizarAsync();
        Assert.Equal("CANCELLED", CoraSimulado.Instancia.Boletos[b3.ExternoId].Status);
    }

    private async Task<Guid> Conta(string nome)
    {
        await using var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync();
        return await db.ContasBancarias.Where(c => c.Nome == nome).Select(c => c.Id).SingleAsync();
    }

    [Fact]
    public async Task Portal_mostra_so_o_que_e_de_quem_abriu_e_revoga()
    {
        var (admin, contrato, dono, _) = await Cenario();
        var loc = S<LocacaoService>();
        var portal = S<PortalService>();
        var hoje = DatasBR.Hoje();
        var faturas = (await loc.ObterAsync(admin, contrato)).Faturas;
        var contaId = await Conta(S<InovabensOpcoes>().Integracoes.CoraConta);
        var pg = await loc.ReceberAsync(admin, faturas[0].Id, new RecebimentoLocacaoForm { Data = hoje, Principal = faturas[0].Total, ContaBancariaId = contaId });
        var aberta = faturas.First(f => f.Status == StatusFaturaLocacao.ABERTA && f.Id != faturas[0].Id);
        await S<CobrancaBancariaService>().EmitirAsync(admin, aberta.Id);

        // inquilino: faturas, boleto, recibo e link de reparo; recibo de outro contrato não abre
        var li = await portal.GerarLinkAsync(admin, contrato);
        Assert.Equal(li.Id, (await portal.GerarLinkAsync(admin, contrato)).Id); // reaproveita o link ativo
        var tokenI = li.Link.Split('/').Last();
        var dados = await portal.PortalAsync(tokenI);
        Assert.NotNull(dados);
        Assert.Equal(TipoPortal.INQUILINO, dados!.Tipo);
        Assert.NotNull(dados.Faturas.Single(f => f.Id == aberta.Id).Boleto?.PixCopiaCola);
        Assert.NotNull(dados.LinkChamado);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await portal.ReciboAsync(tokenI, pg))!.Conteudo[..4]));
        Assert.NotNull(await portal.SegundaViaAsync(tokenI, aberta.Id));
        Assert.Null(await portal.ExtratoAsync(tokenI, hoje)); // extrato é do proprietário

        var (_, outro, _, _) = await Cenario();
        var fOutro = (await loc.ObterAsync(admin, outro)).Faturas[0];
        var pgOutro = await loc.ReceberAsync(admin, fOutro.Id, new RecebimentoLocacaoForm { Data = hoje, Principal = fOutro.Total, ContaBancariaId = await Conta(S<InovabensOpcoes>().Integracoes.CoraConta) });
        Assert.Null(await portal.ReciboAsync(tokenI, pgOutro));
        Assert.Null(await portal.SegundaViaAsync(tokenI, fOutro.Id));

        // proprietário: repasses, extrato e informe; o recibo do contrato dele abre
        var lp = await portal.GerarLinkAsync(admin, contrato, dono);
        var tokenP = lp.Link.Split('/').Last();
        var dp = await portal.PortalAsync(tokenP);
        Assert.Equal(TipoPortal.PROPRIETARIO, dp!.Tipo);
        Assert.NotEmpty(dp.Repasses);
        Assert.Contains(hoje.Year, dp.Anos);
        Assert.NotNull(await portal.ExtratoAsync(tokenP, hoje));
        Assert.NotNull(await portal.InformeAsync(tokenP, hoje.Year));
        Assert.NotNull(await portal.ReciboAsync(tokenP, pg));
        Assert.Null(await portal.ReciboAsync(tokenP, pgOutro));
        Assert.Null(await portal.SegundaViaAsync(tokenP, aberta.Id));

        // links e contagem de acessos na tela; revogado deixa de abrir
        var links = await portal.LinksAsync(admin, contrato);
        Assert.Equal(2, links.Count);
        Assert.True(links.Single(l => l.Tipo == TipoPortal.INQUILINO).Acessos >= 1);
        await portal.RevogarAsync(admin, contrato, li.Id);
        Assert.Null(await portal.PortalAsync(tokenI));
        Assert.Null(await portal.ReciboAsync(tokenI, pg));
        Assert.NotEqual(tokenI, (await portal.GerarLinkAsync(admin, contrato)).Link.Split('/').Last());
        Assert.Null(await portal.PortalAsync("x"));
    }
}
