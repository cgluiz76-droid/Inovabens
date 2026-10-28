using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Financeiro;

/// <summary>Carrega os dados do imóvel e delega o cálculo a <see cref="CalculoRetorno"/>.</summary>
public sealed class RetornoService(IDbContextFactory<InovabensDb> fabrica, IndicesService indices)
{
    public async Task<ResultadoRetorno> CalcularAsync(UsuarioAtual u, Guid imovelId, CodigoIndice indice = CodigoIndice.IPCA, DateOnly? ate = null)
    {
        u.Exigir(P.FinanceiroVer);
        var corte = ate ?? DatasBR.Hoje();
        await using var db = await fabrica.CreateDbContextAsync();
        await ImoveisService.ImovelComAcesso(db, u, imovelId, rastrear: false);

        var aq = await db.Aquisicoes.AsNoTracking().SingleOrDefaultAsync(a => a.ImovelId == imovelId);
        var itens = aq is null ? [] : await db.AquisicaoItens.AsNoTracking().Where(i => i.AquisicaoId == aq.Id).ToListAsync();
        var lancs = await db.DespesaLancamentos.AsNoTracking()
            .Where(l => l.Despesa!.ImovelId == imovelId && l.Despesa.ExcluidoEm == null && l.Competencia <= corte)
            .Select(l => new LancamentoRetorno(l.Competencia, l.Vencimento, l.DataPagamento, l.Status, l.Valor, l.Despesa!.Descricao, l.Despesa.Categoria!.Nome))
            .ToListAsync();
        var receitas = await db.Receitas.AsNoTracking()
            .Where(r => r.ImovelId == imovelId && r.ExcluidoEm == null && r.Data <= corte)
            .ToListAsync();

        return CalculoRetorno.Calcular(new DadosRetorno(imovelId, indice, corte, aq, itens, lancs, receitas, await indices.FatoresAsync(indice)));
    }
}
