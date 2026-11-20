using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Dominio;
using Inovabens.Core.Financeiro;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Financeiro da Imobiliária: saldos, cartão, transferência, dinheiro de terceiros, DRE e acesso restrito.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class FinanceiroEmpresaTests : IAsyncLifetime
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

    private async Task<UsuarioAtual> Entrar(string email) => (await S<AuthService>().LoginAsync(email, BancoFixture.Senha, null)).Usuario!;
    private static string Unico(string nome) => $"{nome} {Guid.NewGuid().ToString("N")[..6]}";

    private async Task<Guid> Categoria(UsuarioAtual u, string nome) =>
        (await S<FinanceiroEmpresaService>().CategoriasAsync(u)).Single(c => c.Nome == nome).Id;

    private async Task<decimal> Saldo(UsuarioAtual u, Guid conta) =>
        (await S<FinanceiroEmpresaService>().ContasAsync(u)).Single(c => c.Conta.Id == conta).Saldo;

    [Fact]
    public async Task Saldo_cartao_parcelado_e_transferencia_para_pagar_a_fatura()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var svc = S<FinanceiroEmpresaService>();
        var inicio = new DateOnly(2026, 1, 1);
        var itau = await svc.CriarContaAsync(admin, new ContaBancariaForm { Nome = Unico("Itaú"), SaldoInicial = 1000, DataSaldoInicial = inicio });
        var cartao = await svc.CriarContaAsync(admin, new ContaBancariaForm
        {
            Nome = Unico("Cartão"), Tipo = TipoContaBancaria.CARTAO_CREDITO, DiaFechamento = 10, DiaVencimento = 20, DataSaldoInicial = inicio,
        });
        var despesa = await Categoria(admin, "Sistemas e assinaturas");

        // pago depois do saldo inicial entra; pago antes não (já está dentro do saldo inicial)
        await svc.CriarAsync(admin, new LancamentoEmpresaForm
        {
            Descricao = "Internet", Valor = 200, CategoriaId = despesa, ContaId = itau, Vencimento = new(2026, 2, 5), Pago = true, DataPagamento = new(2026, 2, 5),
        });
        await svc.CriarAsync(admin, new LancamentoEmpresaForm
        {
            Descricao = "Antigo", Valor = 50, CategoriaId = despesa, ContaId = itau, Vencimento = new(2025, 12, 5), Pago = true, DataPagamento = new(2025, 12, 5),
        });
        Assert.Equal(800, await Saldo(admin, itau));

        // compra de 300 em 3x no cartão: as 3 parcelas comprometem o cartão, uma por mês
        var ids = await svc.CriarAsync(admin, new LancamentoEmpresaForm
        {
            Descricao = "Notebook", Valor = 300, CategoriaId = despesa, ContaId = cartao, Vencimento = new(2026, 3, 1), Pago = true,
            DataPagamento = new(2026, 3, 1), Parcelas = 3,
        });
        Assert.Equal(3, ids.Count);
        Assert.Equal(-300, await Saldo(admin, cartao));
        var parcelas = await svc.LancamentosAsync(admin, new FiltroLancamentosEmpresa { De = new(2026, 3, 1), Ate = new(2026, 5, 31), ContaId = cartao });
        Assert.Equal(["1/3", "2/3", "3/3"], parcelas.Select(p => p.Parcela));
        Assert.Equal([new DateOnly(2026, 3, 1), new(2026, 4, 1), new(2026, 5, 1)], parcelas.Select(p => p.Competencia));

        // pagar a fatura = transferência: sai do banco, abate a dívida do cartão, não é despesa
        var t = await svc.TransferirAsync(admin, new TransferenciaForm { OrigemId = itau, DestinoId = cartao, Valor = 100, Data = new(2026, 3, 20) });
        Assert.Equal(700, await Saldo(admin, itau));
        Assert.Equal(-200, await Saldo(admin, cartao));
        var lado = (await svc.LancamentosAsync(admin, new FiltroLancamentosEmpresa { De = new(2026, 3, 20), Ate = new(2026, 3, 20), ContaId = itau })).Single();
        Assert.Equal(t, lado.TransferenciaId);
        Assert.StartsWith("Pagamento da fatura", lado.Descricao);

        // excluir um lado tira a transferência inteira
        await svc.ExcluirAsync(admin, lado.Id);
        Assert.Equal(800, await Saldo(admin, itau));
        Assert.Equal(-300, await Saldo(admin, cartao));
    }

    [Fact]
    public async Task Aluguel_com_taxa_separa_terceiros_da_receita_e_entra_na_DRE()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var svc = S<FinanceiroEmpresaService>();
        var imovel = await S<ImoveisService>().CriarAsync(admin, new ImovelForm
        {
            NomeDrive = Unico("Aluguel"), Titulo = "Apto alugado", Tipo = "apartamento", PrecoVenda = 300_000,
        });
        var codigo = (await S<ImoveisService>().DetalheAsync(admin, imovel)).Dados.Codigo;
        // ano isolado: a DRE soma tudo do ano e outros testes usam o mesmo banco
        var ano = 2300 + Random.Shared.Next(5000);
        var dia = new DateOnly(ano, 4, 10);
        var conta = await svc.CriarContaAsync(admin, new ContaBancariaForm { Nome = Unico("Sicoob"), DataSaldoInicial = new(ano, 1, 1) });
        var aluguel = await Categoria(admin, "Aluguel recebido de inquilino");
        var taxa = await Categoria(admin, "Taxa de administração de aluguel");
        var repasse = await Categoria(admin, "Repasse ao proprietário");

        var ids = await svc.CriarAsync(admin, new LancamentoEmpresaForm
        {
            Tipo = TipoCategoriaEmpresa.ENTRADA, Descricao = "Aluguel abril", Valor = 1000, TaxaRetida = 100, CategoriaTaxaId = taxa,
            CategoriaId = aluguel, ContaId = conta, Vencimento = dia, Pago = true, DataPagamento = dia, ImovelCodigo = codigo.ToLowerInvariant(),
        });
        Assert.Equal(2, ids.Count);
        Assert.Equal(1000, await Saldo(admin, conta));
        Assert.Equal(900, (await svc.PainelAsync(admin)).TerceirosImoveis.Single(t => t.ImovelId == imovel).Saldo);

        // repasse lançado em aberto e depois baixado: zera o que é do proprietário
        var r = (await svc.CriarAsync(admin, new LancamentoEmpresaForm
        {
            Descricao = "Repasse abril", Valor = 900, CategoriaId = repasse, Vencimento = dia.AddDays(5), ImovelCodigo = codigo,
        })).Single();
        Assert.Equal(900, (await svc.PainelAsync(admin)).TerceirosImoveis.Single(t => t.ImovelId == imovel).Saldo);
        await svc.PagarAsync(admin, r, dia.AddDays(5), conta);
        Assert.DoesNotContain((await svc.PainelAsync(admin)).TerceirosImoveis, t => t.ImovelId == imovel);
        Assert.Equal(100, await Saldo(admin, conta));

        // DRE: só a taxa é receita; aluguel e repasse ficam fora do resultado
        var dre = await svc.DreAsync(admin, ano);
        Assert.Equal(100, dre.Linhas.Single(l => l.Rotulo == "Receita bruta").Meses[3]);
        Assert.Equal(100, dre.Linhas.Single(l => l.Rotulo == "= Resultado líquido").Total);
        Assert.Equal(0, dre.ForaDoResultado.First().Total);

        // categoria de saída não serve para entrada
        await Assert.ThrowsAsync<RegraNegocioException>(() => svc.CriarAsync(admin, new LancamentoEmpresaForm
        {
            Tipo = TipoCategoriaEmpresa.ENTRADA, Descricao = "Errado", Valor = 10, CategoriaId = repasse, Vencimento = dia,
        }));
    }

    [Fact]
    public async Task Corretor_nao_ve_o_financeiro_da_imobiliaria()
    {
        var carla = await Entrar("carla@inovabens.com.br");
        await Assert.ThrowsAsync<AcessoNegadoException>(() => S<FinanceiroEmpresaService>().PainelAsync(carla));
    }

    [Theory]
    [InlineData("2026-11-20", 10, 20, "2026-11-10", "2026-12-10", "2026-12-20")]
    [InlineData("2026-11-05", 10, 20, "2026-10-10", "2026-11-10", "2026-11-20")]
    [InlineData("2026-11-20", 25, 5, "2026-10-25", "2026-11-25", "2026-12-05")]
    [InlineData("2027-02-15", 31, 10, "2027-01-31", "2027-02-28", "2027-03-10")]
    public void Fatura_fecha_e_vence_nos_dias_do_cartao(string hoje, int fechamento, int vencimento, string ultimo, string proximo, string vence)
    {
        var (u, p) = FinanceiroEmpresaService.Fechamentos(fechamento, DateOnly.Parse(hoje));
        Assert.Equal(DateOnly.Parse(ultimo), u);
        Assert.Equal(DateOnly.Parse(proximo), p);
        Assert.Equal(DateOnly.Parse(vence), FinanceiroEmpresaService.Vencimento(p, vencimento));
    }
}
