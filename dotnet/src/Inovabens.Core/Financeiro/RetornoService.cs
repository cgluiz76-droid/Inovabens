using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Financeiro;

/// <summary>
/// Os dois cenários da apuração: com e sem o saldo dos lançamentos financeiros. <see cref="Decisao"/> é o interruptor do
/// fechamento (null = ainda não decidido); <see cref="Oficial"/> segue a decisão (sem decisão, inclui os lançamentos).
/// </summary>
public sealed record RetornoCenarios(ResultadoRetorno ComLancamentos, ResultadoRetorno SemLancamentos, bool? Decisao)
{
    public ResultadoRetorno Oficial => Decisao == false ? SemLancamentos : ComLancamentos;
}

/// <summary>Carrega os dados do imóvel e delega o cálculo a <see cref="CalculoRetorno"/>. Aba Investimento (só administrador).</summary>
public sealed class RetornoService(IDbContextFactory<InovabensDb> fabrica, IndicesService indices)
{
    public async Task<ResultadoRetorno> CalcularAsync(UsuarioAtual u, Guid imovelId, CodigoIndice indice = CodigoIndice.IPCA, DateOnly? ate = null) =>
        (await CenariosAsync(u, imovelId, indice, ate)).Oficial;

    public async Task<RetornoCenarios> CenariosAsync(UsuarioAtual u, Guid imovelId, CodigoIndice indice = CodigoIndice.IPCA, DateOnly? ate = null)
    {
        u.Exigir(P.FinanceiroInvestimento);
        var corte = ate ?? DatasBR.Hoje();
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);

        var aq = await db.Aquisicoes.AsNoTracking().SingleOrDefaultAsync(a => a.ImovelId == imovelId);
        var itens = aq is null ? [] : await db.AquisicaoItens.AsNoTracking().Where(i => i.AquisicaoId == aq.Id).ToListAsync();
        var lancs = await db.LancamentosFinanceiros.AsNoTracking()
            .Where(l => l.ImovelId == imovelId && l.ExcluidoEm == null && l.Situacao != SituacaoLancamento.IGNORADO)
            .Select(l => new LancamentoRetorno(l.DataLiquidacao ?? l.Data, l.Valor, l.Situacao, l.Descricao, l.Conta!.Nome))
            .ToListAsync();
        var fechamento = await db.FechamentosInvestimento.AsNoTracking().SingleOrDefaultAsync(f => f.ImovelId == imovelId);
        var fatores = await indices.FatoresAsync(indice);

        var dados = new DadosRetorno(imovelId, indice, corte, aq, itens, lancs, fechamento, fatores, IncluirLancamentos: true);
        return new RetornoCenarios(CalculoRetorno.Calcular(dados), CalculoRetorno.Calcular(dados with { IncluirLancamentos = false }),
            fechamento?.IncluirLancamentos);
    }
}

/// <summary>Fechamento da operação (venda) na aba Investimento: um por imóvel; salvar marca o imóvel como Vendido.</summary>
public sealed class FechamentoService(IDbContextFactory<InovabensDb> fabrica, AuditService audit)
{
    public async Task<FechamentoInvestimento?> ObterAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.FinanceiroInvestimento);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        return await db.FechamentosInvestimento.AsNoTracking().SingleOrDefaultAsync(f => f.ImovelId == imovelId);
    }

    public async Task SalvarAsync(UsuarioAtual u, Guid imovelId, FechamentoForm form)
    {
        u.Exigir(P.FinanceiroInvestimento);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var im = await Acesso.ImovelComAcesso(db, u, imovelId);
        var f = await db.FechamentosInvestimento.SingleOrDefaultAsync(x => x.ImovelId == imovelId);
        if (f is null)
        {
            f = new FechamentoInvestimento { ImovelId = imovelId };
            form.AplicarEm(f);
            db.FechamentosInvestimento.Add(f);
            audit.Registrar(db, u.Id, "fechamentos_investimento", imovelId, "CRIAR", depois: ComLiquido(f));
        }
        else
        {
            var antes = f.Copia();
            form.AplicarEm(f);
            f.AtualizadoEm = DateTime.UtcNow;
            audit.Registrar(db, u.Id, "fechamentos_investimento", imovelId, "ATUALIZAR", antes, ComLiquido(f));
        }
        f.AtualizadoPor = u.Id;
        if (im.Situacao != SituacaoImovel.VENDIDO)
        {
            var antesImovel = im.Copia();
            im.Situacao = SituacaoImovel.VENDIDO;
            audit.Registrar(db, u.Id, "imoveis", im.Id, "ATUALIZAR", antesImovel, im);
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Desfaz o fechamento (a situação do imóvel não volta sozinha: ajuste na ficha).</summary>
    public async Task ExcluirAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.FinanceiroInvestimento);
        await using var db = await fabrica.CreateDbContextAsync();
        var im = await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var f = await db.FechamentosInvestimento.SingleOrDefaultAsync(x => x.ImovelId == imovelId)
            ?? throw new NaoEncontradoException("Fechamento não encontrado.");
        audit.RegistrarExclusao(db, u.Id, "fechamentos_investimento", imovelId,
            $"Fechamento do imóvel {im.Codigo} (venda de {Texto.Moeda(f.ValorVenda)} em {f.Data:dd/MM/yyyy})", f.Copia());
        db.FechamentosInvestimento.Remove(f);
        await db.SaveChangesAsync();
    }

    private static FechamentoInvestimento ComLiquido(FechamentoInvestimento f)
    {
        var c = f.Copia();
        c.ValorLiquido = c.ValorVenda - c.Comissao - c.Impostos - c.OutrosCustos;
        return c;
    }
}
