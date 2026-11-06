using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Financeiro;

public enum TipoExtrato { TODOS, RECEITAS, DESPESAS }

/// <summary>Filtros do extrato. Datas inclusivas; categoria vale para as despesas (com ela, as receitas saem).</summary>
public sealed class FiltroExtrato
{
    public DateOnly? De { get; set; }
    public DateOnly? Ate { get; set; }
    public TipoExtrato Tipo { get; set; } = TipoExtrato.TODOS;
    public Guid? CategoriaId { get; set; }
    public string? Busca { get; set; }
}

/// <summary>Linha do extrato (regime de caixa): receita na data dela, despesa na data do pagamento.</summary>
public sealed record MovimentoExtrato(
    DateOnly Data, string Descricao, string Categoria, string? Detalhe, decimal Entrada, decimal Saida, decimal Saldo,
    Guid? ReceitaId, Guid? LancamentoId);

/// <summary>Despesa a pagar (ainda não marcada ou marcada como não paga): fora do saldo, entra no saldo projetado.</summary>
public sealed record PendenciaExtrato(
    DateOnly Competencia, DateOnly? Vencimento, string Descricao, string Categoria, decimal Valor, StatusLancamento? Status,
    string? Justificativa, bool Vencida, Guid LancamentoId);

public sealed record Extrato(
    FiltroExtrato Filtro,
    decimal SaldoAtual, decimal SaldoAnterior, decimal Entradas, decimal Saidas, decimal SaldoFinal,
    IReadOnlyList<MovimentoExtrato> Movimentos,
    IReadOnlyList<PendenciaExtrato> Pendentes, decimal TotalPendente, decimal SaldoProjetado);

/// <summary>
/// Extrato do imóvel como livro caixa: receitas (valor líquido) menos despesas pagas, com saldo linha a linha.
/// A aquisição não entra (fica na apuração de resultado / retorno do investimento).
/// Saldo atual = todas as receitas − todas as despesas pagas, sem filtro.
/// </summary>
public sealed class ExtratoService(IDbContextFactory<InovabensDb> fabrica, DespesasService despesas)
{
    private const int MesesFuturosMaximo = 12;
    private const int MesesGeradosMaximo = 36;

    private sealed record Linha(DateOnly Data, string Descricao, string Categoria, Guid? CategoriaId, string? Detalhe, decimal Entrada, decimal Saida,
        Guid? ReceitaId, Guid? LancamentoId);

    public async Task<Extrato> GerarAsync(UsuarioAtual u, Guid imovelId, FiltroExtrato f)
    {
        u.Exigir(P.FinanceiroVer);
        if (f.De is { } de0 && f.Ate is { } ate0 && ate0 < de0)
            throw new RegraNegocioException(nameof(f.Ate), "A data final não pode ser antes da inicial.");
        await using (var db0 = await fabrica.CreateDbContextAsync())
            await Acesso.ImovelComAcesso(db0, u, imovelId, rastrear: false);
        await GerarCompetencias(imovelId, f);

        await using var db = await fabrica.CreateDbContextAsync();
        var receitas = await db.Receitas.AsNoTracking()
            .Where(r => r.ImovelId == imovelId && r.ExcluidoEm == null)
            .Select(r => new { r.Id, r.Tipo, r.Data, r.ValorBruto, r.CustosVenda, r.Tributos, r.ValorLiquido, r.Observacao })
            .ToListAsync();
        var lancamentos = await db.DespesaLancamentos.AsNoTracking()
            .Where(l => l.Despesa!.ImovelId == imovelId && l.Despesa.ExcluidoEm == null)
            .Select(l => new
            {
                l.Id, l.Competencia, l.Vencimento, l.Valor, l.Status, l.DataPagamento, l.Justificativa,
                l.Despesa!.Descricao, l.Despesa.CategoriaId, Categoria = l.Despesa.Categoria!.Nome, l.Despesa.Recorrencia,
            })
            .ToListAsync();

        // todas as linhas realizadas, em ordem; o filtro de data separa o "saldo anterior"
        var todas = receitas.Select(r => new Linha(r.Data, $"Receita de {Texto.Rotulo(r.Tipo).ToLowerInvariant()}", Texto.Rotulo(r.Tipo), null,
                DetalheReceita(r.ValorBruto, r.CustosVenda, r.Tributos, r.Observacao), r.ValorLiquido, 0, r.Id, null))
            .Concat(lancamentos.Where(l => l.Status == StatusLancamento.PAGO).Select(l => new Linha(
                l.DataPagamento ?? l.Vencimento ?? l.Competencia, l.Descricao, l.Categoria, l.CategoriaId,
                l.Recorrencia == Recorrencia.UNICA ? null : $"Competência {l.Competencia:MM/yyyy}", 0, l.Valor, null, l.Id)))
            .OrderBy(x => x.Data).ThenByDescending(x => x.Entrada).ThenBy(x => x.Descricao)
            .ToList();

        var saldoAtual = todas.Sum(x => x.Entrada - x.Saida);
        var filtradas = todas.Where(x => Passa(x, f)).ToList();
        var saldoAnterior = f.De is { } de ? filtradas.Where(x => x.Data < de).Sum(x => x.Entrada - x.Saida) : 0;

        var saldo = saldoAnterior;
        var movimentos = new List<MovimentoExtrato>();
        foreach (var x in filtradas.Where(x => NoPeriodo(x.Data, f)))
        {
            saldo += x.Entrada - x.Saida;
            movimentos.Add(new MovimentoExtrato(x.Data, x.Descricao, x.Categoria, x.Detalhe, x.Entrada, x.Saida, saldo, x.ReceitaId, x.LancamentoId));
        }

        var hoje = DatasBR.Hoje();
        List<PendenciaExtrato> pendentes = f.Tipo == TipoExtrato.RECEITAS ? [] : lancamentos
            .Where(l => l.Status is null or StatusLancamento.NAO_PAGO)
            .Where(l => f.CategoriaId is null || l.CategoriaId == f.CategoriaId)
            .Where(l => ContemBusca(f.Busca, l.Descricao, l.Categoria))
            .Where(l => NoPeriodo(l.Vencimento ?? l.Competencia, f))
            .OrderBy(l => l.Vencimento ?? l.Competencia).ThenBy(l => l.Descricao)
            .Select(l => new PendenciaExtrato(l.Competencia, l.Vencimento, l.Descricao, l.Categoria, l.Valor, l.Status, l.Justificativa,
                (l.Vencimento ?? l.Competencia) < hoje, l.Id))
            .ToList();
        var totalPendente = pendentes.Sum(p => p.Valor);

        var entradas = movimentos.Sum(m => m.Entrada);
        var saidas = movimentos.Sum(m => m.Saida);
        return new Extrato(f, saldoAtual, saldoAnterior, entradas, saidas, saldo, movimentos, pendentes, totalPendente, saldo - totalPendente);
    }

    /// <summary>
    /// Cria (idempotente) os lançamentos dos meses do filtro, para as despesas a pagar aparecerem mesmo nos meses em que
    /// o checklist não foi aberto: no máximo 36 meses, e no máximo 12 à frente de hoje.
    /// </summary>
    private async Task GerarCompetencias(Guid imovelId, FiltroExtrato f)
    {
        var hoje = DatasBR.Hoje();
        var mesAtual = new DateOnly(hoje.Year, hoje.Month, 1);
        var inicio = f.De is { } de ? new DateOnly(de.Year, de.Month, 1) : mesAtual;
        var fim = f.Ate is { } ate ? new DateOnly(ate.Year, ate.Month, 1) : mesAtual;
        if (fim > mesAtual.AddMonths(MesesFuturosMaximo)) fim = mesAtual.AddMonths(MesesFuturosMaximo);
        if (inicio < fim.AddMonths(-MesesGeradosMaximo + 1)) inicio = fim.AddMonths(-MesesGeradosMaximo + 1);
        for (var m = inicio; m <= fim; m = m.AddMonths(1))
            await despesas.GerarCompetenciaAsync(m, imovelId);
    }

    private static bool Passa(Linha x, FiltroExtrato f) =>
        (f.Tipo != TipoExtrato.RECEITAS || x.ReceitaId is not null)
        && (f.Tipo != TipoExtrato.DESPESAS || x.LancamentoId is not null)
        && (f.CategoriaId is null || x.CategoriaId == f.CategoriaId)
        && ContemBusca(f.Busca, x.Descricao, x.Categoria, x.Detalhe);

    private static bool NoPeriodo(DateOnly d, FiltroExtrato f) => (f.De is null || d >= f.De) && (f.Ate is null || d <= f.Ate);

    private static bool ContemBusca(string? busca, params string?[] campos) =>
        Texto.Limpo(busca) is not { } b || campos.Any(c => c?.Contains(b, StringComparison.CurrentCultureIgnoreCase) == true);

    private static string? DetalheReceita(decimal bruto, decimal custos, decimal tributos, string? obs)
    {
        var partes = new List<string>();
        if (custos > 0 || tributos > 0)
            partes.Add($"bruto {Texto.Moeda(bruto)}" + (custos > 0 ? $" − custos {Texto.Moeda(custos)}" : "") + (tributos > 0 ? $" − tributos {Texto.Moeda(tributos)}" : ""));
        if (Texto.Limpo(obs) is { } o) partes.Add(o);
        return partes.Count == 0 ? null : string.Join(" · ", partes);
    }
}
