using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Financeiro;

public enum TipoExtrato { TODOS, RECEITAS, DESPESAS }

/// <summary>Filtro de situação dos títulos. EM_ATRASO = aberto ou previsto com vencimento antes de hoje.</summary>
public enum FiltroSituacao { TODAS, LIQUIDADO, ABERTO, EM_ATRASO, PREVISTO }

/// <summary>Filtros da aba Lançamentos Financeiros (os mesmos do PDF e do Excel). Datas inclusivas.</summary>
public sealed class FiltroLancamentos
{
    public DateOnly? De { get; set; }
    public DateOnly? Ate { get; set; }
    public TipoExtrato Tipo { get; set; } = TipoExtrato.TODOS;
    public Guid? ContaId { get; set; }
    public string? Busca { get; set; }

    /// <summary>
    /// false (padrão) = saldo REAL: só títulos liquidados, na data da liquidação. true = saldo PROJETADO: também os abertos e
    /// os previstos, na data de vencimento.
    /// </summary>
    public bool Projetado { get; set; }

    /// <summary>Situação dos títulos. Aberto, em atraso e previsto nunca entram no saldo real: com eles o extrato é sempre projetado.</summary>
    public FiltroSituacao Situacao { get; set; } = FiltroSituacao.TODAS;

    /// <summary>Só os lançamentos ainda não enviados ao responsável financeiro (BPO); previstos não contam (ainda não existem).</summary>
    public bool NaoEnviadosBpo { get; set; }

    /// <summary>A situação escolhida só existe no saldo projetado.</summary>
    public bool SituacaoExigeProjetado => Situacao is FiltroSituacao.ABERTO or FiltroSituacao.EM_ATRASO or FiltroSituacao.PREVISTO;

    /// <summary>Padrão da tela: o mês corrente (do dia 1 ao último dia).</summary>
    public static FiltroLancamentos MesAtual()
    {
        var hoje = DatasBR.Hoje();
        var inicio = new DateOnly(hoje.Year, hoje.Month, 1);
        return new FiltroLancamentos { De = inicio, Ate = inicio.AddMonths(1).AddDays(-1) };
    }
}

/// <summary>
/// Linha do extrato, com o saldo acumulado até ela. <see cref="Data"/>: liquidação (liquidado) ou vencimento (aberto/previsto,
/// só no saldo projetado).
/// </summary>
public sealed record LinhaLancamento(
    Guid Id, string Numero, SituacaoLancamento Situacao, DateOnly Data, DateOnly Vencimento, string Descricao, string Conta, string? Observacao,
    decimal Valor, decimal Saldo, bool Recorrente, bool DeRecorrencia, string? Pessoa = null, DateTime? BpoEnviadoEm = null);

/// <summary>Título ABERTO (a pagar/receber) ou PREVISTO (recorrência): fora do saldo até ser liquidado.</summary>
public sealed record TituloPendente(
    Guid Id, string Numero, SituacaoLancamento Situacao, DateOnly Vencimento, string Descricao, string Conta, decimal Valor, int DiasAtraso,
    bool Recorrente, string? Pessoa = null, DateTime? BpoEnviadoEm = null)
{
    public bool Atrasado => DiasAtraso > 0;
}

public sealed record ExtratoLancamentos(
    FiltroLancamentos Filtro, string CodigoImovel,
    decimal SaldoAtual, decimal SaldoAnterior, decimal Entradas, decimal Saidas, decimal SaldoFinal,
    IReadOnlyList<LinhaLancamento> Linhas,
    IReadOnlyList<TituloPendente> Abertos, decimal TotalAberto,
    IReadOnlyList<TituloPendente> Previstos, decimal TotalPrevisto);

/// <summary>Lançamento para edição: os dados e o nº exibido.</summary>
public sealed record LancamentoDetalhe(LancamentoFinanceiro Dados, string Numero, string Conta);

/// <summary>Resumo dos lançamentos liquidados (OPEX) por conta, para a aba Investimento.</summary>
public sealed record ResumoConta(string Conta, TipoConta Tipo, int Quantidade, decimal Total);

/// <summary>Título em atraso de qualquer imóvel (ícone de alerta do financeiro e tela "Títulos em atraso").</summary>
public sealed record TituloAtrasado(
    Guid Id, Guid ImovelId, string CodigoImovel, string TituloImovel, string Numero, SituacaoLancamento Situacao,
    DateOnly Vencimento, string Descricao, string Conta, decimal Valor, int DiasAtraso);

/// <summary>
/// Lançamentos Financeiros do imóvel: títulos a receber (+) e a pagar (−) correntes — manutenção, reembolsos,
/// administração de aluguel. Situação: PREVISTO (gerado pela recorrência) → ABERTO (título com vencimento) →
/// LIQUIDADO (pago/recebido, entra no saldo). Título aberto (ou previsto) com vencimento passado = em atraso.
/// </summary>
public sealed class LancamentosFinanceirosService(IDbContextFactory<InovabensDb> fabrica, AuditService audit)
{
    /// <summary>A recorrência gera previstos até o mês do filtro, mas nunca além de 12 meses à frente.</summary>
    public const int MesesFuturosMaximo = 12;

    public static string FormatarNumero(string codigoImovel, int? numero) =>
        numero is { } n ? $"{(string.IsNullOrWhiteSpace(codigoImovel) ? "#" : codigoImovel + "-")}{n:0000}" : "—";

    public static string NomeSituacao(SituacaoLancamento s) => s switch
    {
        SituacaoLancamento.PREVISTO => "Previsto",
        SituacaoLancamento.ABERTO => "Aberto",
        SituacaoLancamento.LIQUIDADO => "Liquidado",
        _ => "Ignorado",
    };

    /* ------------------------- Contas orçamentárias ------------------------- */

    public async Task<IReadOnlyList<ContaOrcamentaria>> ContasAsync(TipoConta? tipo = null, bool incluirInativas = false)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        return await db.ContasOrcamentarias.AsNoTracking()
            .Where(c => (incluirInativas || c.Ativo) && (tipo == null || c.Tipo == tipo))
            .OrderBy(c => c.Tipo).ThenBy(c => c.Nome)
            .ToListAsync();
    }

    /// <summary>Administração ou quem edita o financeiro (pelo "+ Nova" do lançamento); renomear e desativar só na Administração.</summary>
    public async Task<ContaOrcamentaria> CriarContaAsync(UsuarioAtual u, ContaForm form)
    {
        if (!u.Pode(P.AdminConfig)) u.Exigir(P.FinanceiroEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var nome = form.Nome.Trim();
        if (await db.ContasOrcamentarias.AnyAsync(c => c.Nome == nome))
            throw new RegraNegocioException(nameof(form.Nome), "Já existe uma conta com esse nome.");
        var c = new ContaOrcamentaria { Nome = nome, Tipo = form.Tipo, Ativo = form.Ativo };
        db.ContasOrcamentarias.Add(c);
        audit.Registrar(db, u.Id, "contas_orcamentarias", c.Id, "CRIAR", depois: c);
        await db.SaveChangesAsync();
        return c;
    }

    /// <summary>Nome e ativo são livres; o tipo só muda se nenhum lançamento usa a conta (o sinal dos valores depende dele).</summary>
    public async Task AtualizarContaAsync(UsuarioAtual u, Guid id, ContaForm form)
    {
        u.Exigir(P.AdminConfig);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await db.ContasOrcamentarias.SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Conta não encontrada.");
        var nome = form.Nome.Trim();
        if (await db.ContasOrcamentarias.AnyAsync(x => x.Nome == nome && x.Id != id))
            throw new RegraNegocioException(nameof(form.Nome), "Já existe uma conta com esse nome.");
        if (c.Tipo != form.Tipo && await db.LancamentosFinanceiros.AnyAsync(l => l.ContaId == id))
            throw new RegraNegocioException(nameof(form.Tipo), "Esta conta já tem lançamentos: o tipo (receita/despesa) não pode mudar.");
        var antes = new { c.Nome, c.Tipo, c.Ativo };
        c.Nome = nome;
        c.Tipo = form.Tipo;
        c.Ativo = form.Ativo;
        audit.Registrar(db, u.Id, "contas_orcamentarias", id, "ATUALIZAR", antes, c);
        await db.SaveChangesAsync();
    }

    /* ------------------------------- Extrato ------------------------------- */

    /// <summary>
    /// Extrato do período com saldo linha a linha. Saldo REAL (padrão): só títulos LIQUIDADOS, na data da liquidação.
    /// Saldo PROJETADO (<see cref="FiltroLancamentos.Projetado"/>): também abertos e previstos, na data de vencimento.
    /// Abertos e previstos vêm ainda em listas de controle à parte (os do período e todos os já atrasados).
    /// </summary>
    public async Task<ExtratoLancamentos> ExtratoAsync(UsuarioAtual u, Guid imovelId, FiltroLancamentos f)
    {
        u.Exigir(P.FinanceiroVer);
        if (f.De is { } de0 && f.Ate is { } ate0 && ate0 < de0)
            throw new RegraNegocioException(nameof(f.Ate), "A data final não pode ser antes da inicial.");
        string codigo;
        await using (var db0 = await fabrica.CreateDbContextAsync())
            codigo = (await Acesso.ImovelComAcesso(db0, u, imovelId, rastrear: false)).Codigo;
        await GerarPrevistosAsync(LimitePrevistos(f.Ate), imovelId);
        if (f.SituacaoExigeProjetado) f.Projetado = true;
        var hoje = DatasBR.Hoje();

        await using var db = await fabrica.CreateDbContextAsync();
        var todos = await db.LancamentosFinanceiros.AsNoTracking()
            .Where(l => l.ImovelId == imovelId && l.ExcluidoEm == null && l.Situacao != SituacaoLancamento.IGNORADO)
            .Select(l => new
            {
                l.Id, l.Numero, l.Situacao, l.Data, l.DataLiquidacao, l.Valor, l.Descricao, l.ContaId, Conta = l.Conta!.Nome, l.Observacao,
                l.Recorrente, l.OrigemId, l.PessoaNome, l.BpoEnviadoEm,
            })
            .ToListAsync();

        bool Passa(SituacaoLancamento situacao, DateOnly vencimento, decimal valor, Guid conta, DateTime? bpo, params string?[] campos) =>
            (!f.NaoEnviadosBpo || (bpo is null && situacao != SituacaoLancamento.PREVISTO))
            && (f.Tipo != TipoExtrato.RECEITAS || valor > 0) && (f.Tipo != TipoExtrato.DESPESAS || valor < 0)
            && (f.ContaId is null || conta == f.ContaId) && ContemBusca(f.Busca, campos)
            && f.Situacao switch
            {
                FiltroSituacao.LIQUIDADO => situacao == SituacaoLancamento.LIQUIDADO,
                FiltroSituacao.ABERTO => situacao == SituacaoLancamento.ABERTO,
                FiltroSituacao.PREVISTO => situacao == SituacaoLancamento.PREVISTO,
                FiltroSituacao.EM_ATRASO => situacao is SituacaoLancamento.ABERTO or SituacaoLancamento.PREVISTO && vencimento < hoje,
                _ => true,
            };

        var saldoAtual = todos.Where(l => l.Situacao == SituacaoLancamento.LIQUIDADO).Sum(l => l.Valor);
        var noSaldo = todos.Where(l => l.Situacao == SituacaoLancamento.LIQUIDADO || f.Projetado)
            .Select(l => new { L = l, Quando = l.Situacao == SituacaoLancamento.LIQUIDADO ? l.DataLiquidacao ?? l.Data : l.Data })
            .OrderBy(x => x.Quando).ThenBy(x => x.L.Situacao).ThenBy(x => x.L.Numero).ToList();
        var filtrados = noSaldo.Where(x => Passa(x.L.Situacao, x.L.Data, x.L.Valor, x.L.ContaId, x.L.BpoEnviadoEm, x.L.Descricao, x.L.Conta, x.L.Observacao, x.L.PessoaNome, FormatarNumero(codigo, x.L.Numero))).ToList();
        var saldoAnterior = f.De is { } de ? filtrados.Where(x => x.Quando < de).Sum(x => x.L.Valor) : 0;

        var saldo = saldoAnterior;
        var linhas = new List<LinhaLancamento>();
        foreach (var x in filtrados.Where(x => NoPeriodo(x.Quando, f)))
        {
            saldo += x.L.Valor;
            linhas.Add(new LinhaLancamento(x.L.Id, FormatarNumero(codigo, x.L.Numero), x.L.Situacao, x.Quando, x.L.Data, x.L.Descricao, x.L.Conta, x.L.Observacao,
                x.L.Valor, saldo, x.L.Recorrente, x.L.OrigemId is not null, x.L.PessoaNome, x.L.BpoEnviadoEm));
        }

        // pendentes do período e, além deles, os já atrasados de antes do início (não podem sumir da tela)
        bool Pendente(DateOnly venc) => (f.Ate is null || venc <= f.Ate) && (f.De is null || venc >= f.De || venc < hoje);
        List<TituloPendente> Lista(SituacaoLancamento s) => todos
            .Where(l => l.Situacao == s && Pendente(l.Data) && Passa(l.Situacao, l.Data, l.Valor, l.ContaId, l.BpoEnviadoEm, l.Descricao, l.Conta, l.Observacao, l.PessoaNome, FormatarNumero(codigo, l.Numero)))
            .OrderBy(l => l.Data).ThenBy(l => l.Descricao)
            .Select(l => new TituloPendente(l.Id, FormatarNumero(codigo, l.Numero), l.Situacao, l.Data, l.Descricao, l.Conta, l.Valor,
                Math.Max(0, hoje.DayNumber - l.Data.DayNumber), l.Recorrente, l.PessoaNome, l.BpoEnviadoEm))
            .ToList();
        var abertos = Lista(SituacaoLancamento.ABERTO);
        var previstos = Lista(SituacaoLancamento.PREVISTO);
        var totalAberto = abertos.Sum(p => p.Valor);
        var totalPrevisto = previstos.Sum(p => p.Valor);

        return new ExtratoLancamentos(f, codigo, saldoAtual, saldoAnterior,
            linhas.Where(l => l.Valor > 0).Sum(l => l.Valor), -linhas.Where(l => l.Valor < 0).Sum(l => l.Valor), saldo,
            linhas, abertos, totalAberto, previstos, totalPrevisto);
    }

    /// <summary>OPEX para a aba Investimento: lançamentos liquidados agrupados por conta.</summary>
    public async Task<IReadOnlyList<ResumoConta>> ResumoPorContaAsync(UsuarioAtual u, Guid imovelId, DateOnly? ate = null)
    {
        u.Exigir(P.FinanceiroVer);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var linhas = await db.LancamentosFinanceiros.AsNoTracking()
            .Where(l => l.ImovelId == imovelId && l.ExcluidoEm == null && l.Situacao == SituacaoLancamento.LIQUIDADO
                        && (ate == null || l.DataLiquidacao <= ate))
            .GroupBy(l => new { l.Conta!.Nome, l.Conta.Tipo })
            .Select(g => new { g.Key.Nome, g.Key.Tipo, Quantidade = g.Count(), Total = g.Sum(l => l.Valor) })
            .ToListAsync();
        return linhas.OrderBy(l => l.Tipo).ThenBy(l => l.Nome).Select(l => new ResumoConta(l.Nome, l.Tipo, l.Quantidade, l.Total)).ToList();
    }

    /* --------------------------- Títulos em atraso --------------------------- */

    private static IQueryable<LancamentoFinanceiro> Atrasados(InovabensDb db, UsuarioAtual u, DateOnly hoje) =>
        db.LancamentosFinanceiros.AsNoTracking()
            .Where(l => l.ExcluidoEm == null && (l.Situacao == SituacaoLancamento.ABERTO || l.Situacao == SituacaoLancamento.PREVISTO) && l.Data < hoje)
            .Where(l => db.Imoveis.VisiveisPara(u).Any(i => i.Id == l.ImovelId && i.ExcluidoEm == null));

    /// <summary>Quantos títulos (a pagar ou a receber) estão em atraso: o número do ícone do financeiro no topo.</summary>
    public async Task<int> ContagemAtrasadosAsync(UsuarioAtual u)
    {
        if (!u.Pode(P.FinanceiroVer)) return 0;
        await using var db = await fabrica.CreateDbContextAsync();
        return await Atrasados(db, u, DatasBR.Hoje()).CountAsync();
    }

    public async Task<IReadOnlyList<TituloAtrasado>> AtrasadosAsync(UsuarioAtual u)
    {
        u.Exigir(P.FinanceiroVer);
        var hoje = DatasBR.Hoje();
        await using var db = await fabrica.CreateDbContextAsync();
        var lista = await Atrasados(db, u, hoje)
            .Select(l => new { l.Id, l.ImovelId, l.Imovel!.Codigo, l.Imovel.Titulo, l.Numero, l.Situacao, l.Data, l.Descricao, Conta = l.Conta!.Nome, l.Valor })
            .ToListAsync();
        return lista.OrderBy(l => l.Data).ThenBy(l => l.Codigo)
            .Select(l => new TituloAtrasado(l.Id, l.ImovelId, l.Codigo, l.Titulo, FormatarNumero(l.Codigo, l.Numero), l.Situacao, l.Data,
                l.Descricao, l.Conta, l.Valor, hoje.DayNumber - l.Data.DayNumber))
            .ToList();
    }

    /* --------------------------------- CRUD -------------------------------- */

    public async Task<LancamentoDetalhe> ObterAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.FinanceiroVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await ComAcesso(db, u, id, rastrear: false);
        var codigo = await db.Imoveis.Where(i => i.Id == l.ImovelId).Select(i => i.Codigo).SingleAsync();
        var conta = await db.ContasOrcamentarias.Where(c => c.Id == l.ContaId).Select(c => c.Nome).SingleAsync();
        return new LancamentoDetalhe(l, FormatarNumero(codigo, l.Numero), conta);
    }

    /// <summary>Novo título: ABERTO (a pagar/receber) ou já LIQUIDADO; recebe o próximo nº do imóvel.</summary>
    public async Task<Guid> CriarAsync(UsuarioAtual u, Guid imovelId, LancamentoForm form)
    {
        u.Exigir(P.FinanceiroEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        await ConferirConta(db, form);
        await using var tx = await db.Database.BeginTransactionAsync();
        var l = new LancamentoFinanceiro { ImovelId = imovelId, CriadoPor = u.Id, AtualizadoPor = u.Id };
        form.AplicarEm(l);
        l.PessoaId = await PessoaCadastrada(db, l.PessoaNome);
        l.Numero = await ProximoNumero(db, imovelId);
        db.LancamentosFinanceiros.Add(l);
        audit.Registrar(db, u.Id, "lancamentos_financeiros", l.Id, "CRIAR", depois: l);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        if (l.Recorrente) await GerarPrevistosAsync(LimitePrevistos(null), imovelId);
        return l.Id;
    }

    /// <summary>
    /// Edita um título aberto ou liquidado (inclusive desfazer a liquidação, desmarcando "pago/recebido"). Se a recorrência
    /// mudar, os previstos ainda não confirmados dela são refeitos; os confirmados ou ignorados ficam como estão.
    /// </summary>
    public async Task AtualizarAsync(UsuarioAtual u, Guid id, LancamentoForm form)
    {
        u.Exigir(P.FinanceiroEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await ComAcesso(db, u, id);
        if (l.Situacao is not (SituacaoLancamento.ABERTO or SituacaoLancamento.LIQUIDADO))
            throw new RegraNegocioException("situacao", "Lançamento previsto: confirme ou ignore.");
        if (l.OrigemId is not null && form.Recorrente)
            throw new RegraNegocioException(nameof(form.Recorrente), "Este lançamento veio de uma recorrência: altere a recorrência no lançamento de origem.");
        await ConferirConta(db, form);
        var antes = l.Copia();
        form.AplicarEm(l);
        // conciliado com o extrato da imobiliária: valor, data de pagamento e conta bancária são os do banco
        if (antes.ExtratoItemId is not null && (l.Valor != antes.Valor || l.Situacao != antes.Situacao
                || l.DataLiquidacao != antes.DataLiquidacao || l.ContaBancariaId != antes.ContaBancariaId))
            throw new RegraNegocioException("", "Lançamento conciliado com o extrato bancário: desfaça a conciliação (Financeiro → Conciliação) antes de mudar valor, pagamento ou conta.");
        l.PessoaId = antes.PessoaNome == l.PessoaNome && antes.PessoaId is not null ? antes.PessoaId : await PessoaCadastrada(db, l.PessoaNome);
        l.AtualizadoPor = u.Id;
        l.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "lancamentos_financeiros", id, "ATUALIZAR", antes, l);
        var recorrenciaMudou = antes.Recorrente != l.Recorrente || antes.DiaRecorrencia != l.DiaRecorrencia || antes.ValorEstimado != l.ValorEstimado
                               || antes.Descricao != l.Descricao || antes.ContaId != l.ContaId || antes.Data != l.Data;
        await db.SaveChangesAsync();
        if (antes.Recorrente && recorrenciaMudou)
            await db.LancamentosFinanceiros.Where(x => x.OrigemId == id && x.Situacao == SituacaoLancamento.PREVISTO).ExecuteDeleteAsync();
        if (l.Recorrente && recorrenciaMudou) await GerarPrevistosAsync(LimitePrevistos(null), l.ImovelId);
        if (l.Situacao == SituacaoLancamento.LIQUIDADO) await ResolverAlertas(db, id);
    }

    /// <summary>Exclusão lógica, registrada na Auditoria (quem excluiu e o quê). Os previstos da recorrência dele somem.</summary>
    public async Task ExcluirAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.FinanceiroEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await ComAcesso(db, u, id);
        if (l.ExtratoItemId is not null)
            throw new RegraNegocioException("", "Lançamento conciliado com o extrato bancário: desfaça a conciliação (Financeiro → Conciliação) antes de excluir.");
        var codigo = await db.Imoveis.Where(i => i.Id == l.ImovelId).Select(i => i.Codigo).SingleAsync();
        audit.RegistrarExclusao(db, u.Id, "lancamentos_financeiros", id,
            $"Lançamento {FormatarNumero(codigo, l.Numero)} – {l.Descricao} ({Texto.Moeda(l.Valor)}, venc. {l.Data:dd/MM/yyyy}, {NomeSituacao(l.Situacao).ToLowerInvariant()})",
            l.Copia());
        l.ExcluidoEm = DateTime.UtcNow;
        l.AtualizadoPor = u.Id;
        await db.SaveChangesAsync();
        await db.LancamentosFinanceiros.Where(x => x.OrigemId == id && x.Situacao == SituacaoLancamento.PREVISTO).ExecuteDeleteAsync();
        await ResolverAlertas(db, id);
    }

    /// <summary>O previsto vira título ABERTO (ou já LIQUIDADO), com vencimento e valor reais e o próximo nº do imóvel.</summary>
    public async Task ConfirmarAsync(UsuarioAtual u, Guid id, ConfirmacaoPrevistoForm form)
    {
        u.Exigir(P.FinanceiroEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await ComAcesso(db, u, id);
        if (l.Situacao != SituacaoLancamento.PREVISTO) throw new RegraNegocioException("situacao", "Este lançamento não está previsto.");
        await using var tx = await db.Database.BeginTransactionAsync();
        var antes = l.Copia();
        l.Situacao = form.Liquidado ? SituacaoLancamento.LIQUIDADO : SituacaoLancamento.ABERTO;
        l.DataLiquidacao = form.Liquidado ? form.DataLiquidacao : null;
        l.Data = form.Data!.Value;
        l.Valor = Math.Sign(l.Valor) * form.Valor!.Value;
        l.Numero = await ProximoNumero(db, l.ImovelId);
        l.ContaBancariaId = form.ContaBancariaId ?? l.ContaBancariaId;
        l.AtualizadoPor = u.Id;
        l.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "lancamentos_financeiros", id, "CONFIRMAR", antes, l);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        // aberto e ainda atrasado: o alerta continua (agora como título aberto)
        if (form.Liquidado) await ResolverAlertas(db, id);
    }

    /// <summary>Baixa de um título aberto: pago/recebido na data informada, pelo valor efetivo. Entra no saldo.</summary>
    /// <param name="contaBancariaId">Conta da imobiliária por onde o dinheiro passou (opcional; nulo mantém a atual).</param>
    public async Task LiquidarAsync(UsuarioAtual u, Guid id, DateOnly data, decimal valor, Guid? contaBancariaId = null)
    {
        u.Exigir(P.FinanceiroEditar);
        if (valor <= 0 || decimal.Round(valor, 2) != valor)
            throw new RegraNegocioException("Valor", "Informe um valor positivo com no máximo 2 casas decimais.");
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await ComAcesso(db, u, id);
        if (l.Situacao != SituacaoLancamento.ABERTO) throw new RegraNegocioException("situacao", "Só um título em aberto pode ser liquidado.");
        var antes = l.Copia();
        l.Situacao = SituacaoLancamento.LIQUIDADO;
        l.DataLiquidacao = data;
        l.Valor = Math.Sign(l.Valor) * valor;
        l.ContaBancariaId = contaBancariaId ?? l.ContaBancariaId;
        l.AtualizadoPor = u.Id;
        l.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "lancamentos_financeiros", id, "LIQUIDAR", antes, l);
        await db.SaveChangesAsync();
        await ResolverAlertas(db, id);
    }

    /// <summary>O previsto não aconteceu neste mês: sai da lista (não volta a ser gerado).</summary>
    public async Task IgnorarAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.FinanceiroEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await ComAcesso(db, u, id);
        if (l.Situacao != SituacaoLancamento.PREVISTO) throw new RegraNegocioException("situacao", "Este lançamento não está previsto.");
        var antes = l.Copia();
        l.Situacao = SituacaoLancamento.IGNORADO;
        l.AtualizadoPor = u.Id;
        l.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "lancamentos_financeiros", id, "IGNORAR", antes, l);
        await db.SaveChangesAsync();
        await ResolverAlertas(db, id);
    }

    /* ----------------------------- Recorrência ----------------------------- */

    /// <summary>
    /// Cria (idempotente) os previstos das recorrências, do mês seguinte ao do lançamento de origem até o mês de
    /// <paramref name="ateMes"/>. Dia = dia da recorrência, limitado ao último dia do mês. Devolve quantos foram criados.
    /// </summary>
    public async Task<int> GerarPrevistosAsync(DateOnly ateMes, Guid? imovelId = null)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        return await db.Database.ExecuteSqlRawAsync("""
            insert into lancamentos_financeiros (imovel_id, situacao, data, valor, descricao, conta_id, pessoa_id, pessoa_nome, origem_id, competencia, criado_por, conta_bancaria_id)
            select t.imovel_id, 'PREVISTO'::situacao_lancamento,
                   make_date(extract(year from m.mes)::int, extract(month from m.mes)::int,
                     least(t.dia_recorrencia, extract(day from (m.mes + interval '1 month' - interval '1 day'))::int)),
                   t.valor_estimado, t.descricao, t.conta_id, t.pessoa_id, t.pessoa_nome, t.id, m.mes::date, t.criado_por, t.conta_bancaria_id
              from lancamentos_financeiros t
              join imoveis i on i.id = t.imovel_id
              cross join lateral generate_series(date_trunc('month', t.data) + interval '1 month', @ate::timestamp, interval '1 month') as m(mes)
             where t.recorrente and t.situacao in ('ABERTO', 'LIQUIDADO') and t.excluido_em is null and i.excluido_em is null
               and (@imovel is null or t.imovel_id = @imovel)
            on conflict (origem_id, competencia) where origem_id is not null do nothing
            """,
            new NpgsqlParameter("ate", NpgsqlDbType.Date) { Value = new DateOnly(ateMes.Year, ateMes.Month, 1) },
            new NpgsqlParameter("imovel", NpgsqlDbType.Uuid) { Value = (object?)imovelId ?? DBNull.Value });
    }

    /// <summary>Até onde gerar: o mês do filtro (ou o mês atual), no máximo 12 meses à frente.</summary>
    public static DateOnly LimitePrevistos(DateOnly? ate)
    {
        var hoje = DatasBR.Hoje();
        var atual = new DateOnly(hoje.Year, hoje.Month, 1);
        var alvo = ate is { } a && a > atual ? new DateOnly(a.Year, a.Month, 1) : atual;
        var maximo = atual.AddMonths(MesesFuturosMaximo);
        return alvo > maximo ? maximo : alvo;
    }

    /* ------------------------------- Apoio -------------------------------- */

    /// <summary>Próximo nº do imóvel, com a linha do imóvel travada até o fim da transação (dois cadastros ao mesmo tempo não repetem).</summary>
    internal static async Task<int> ProximoNumero(InovabensDb db, Guid imovelId)
    {
        await db.Database.ExecuteSqlRawAsync("select 1 from imoveis where id = {0} for update", imovelId);
        return (await db.LancamentosFinanceiros.Where(l => l.ImovelId == imovelId).MaxAsync(l => (int?)l.Numero) ?? 0) + 1;
    }

    private static async Task ConferirConta(InovabensDb db, LancamentoForm form)
    {
        var conta = await db.ContasOrcamentarias.AsNoTracking().SingleOrDefaultAsync(c => c.Id == form.ContaId)
            ?? throw new RegraNegocioException(nameof(form.ContaId), "Conta orçamentária não encontrada.");
        if (conta.Tipo != form.Tipo)
            throw new RegraNegocioException(nameof(form.ContaId),
                form.Tipo == TipoConta.RECEITA ? "Escolha uma conta de receita." : "Escolha uma conta de despesa.");
    }

    private static async Task<LancamentoFinanceiro> ComAcesso(InovabensDb db, UsuarioAtual u, Guid id, bool rastrear = true)
    {
        var q = rastrear ? db.LancamentosFinanceiros : db.LancamentosFinanceiros.AsNoTracking();
        var l = await q.SingleOrDefaultAsync(x => x.Id == id && x.ExcluidoEm == null) ?? throw new NaoEncontradoException("Lançamento não encontrado.");
        await Acesso.ImovelComAcesso(db, u, l.ImovelId, rastrear: false);
        return l;
    }

    private static Task ResolverAlertas(InovabensDb db, Guid lancamentoId) =>
        db.Alertas.Where(a => a.LancamentoId == lancamentoId && a.ResolvidoEm == null)
            .ExecuteUpdateAsync(x => x.SetProperty(a => a.ResolvidoEm, DateTime.UtcNow));

    private static bool NoPeriodo(DateOnly d, FiltroLancamentos f) => (f.De is null || d >= f.De) && (f.Ate is null || d <= f.Ate);

    /// <summary>
    /// Vincula o nome digitado à pessoa cadastrada com o mesmo nome (sem diferenciar maiúsculas), só quando há exatamente uma;
    /// nome novo ou homônimos ficam só como texto.
    /// </summary>
    private static async Task<Guid?> PessoaCadastrada(InovabensDb db, string? nome)
    {
        if (nome is null) return null;
        var achadas = await db.Pessoas.AsNoTracking()
            .Where(p => p.ExcluidoEm == null && p.NomeRazao.Trim().ToLower() == nome.ToLower())
            .Select(p => p.Id).Take(2).ToListAsync();
        return achadas.Count == 1 ? achadas[0] : null;
    }

    /// <summary>Rótulo do filtro de situação (null = todas).</summary>
    public static string? NomeFiltroSituacao(FiltroSituacao s) => s switch
    {
        FiltroSituacao.LIQUIDADO => "Liquidado",
        FiltroSituacao.ABERTO => "Aberto",
        FiltroSituacao.EM_ATRASO => "Em atraso",
        FiltroSituacao.PREVISTO => "Previsto",
        _ => null,
    };

    private static bool ContemBusca(string? busca, params string?[] campos) =>
        Texto.Limpo(busca) is not { } b || campos.Any(c => Texto.Contem(c, b)); // sem maiúsculas nem acentos
}
