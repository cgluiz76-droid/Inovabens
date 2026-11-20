using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Servicos;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Financeiro;

/* ------------------------------ Consultas ------------------------------ */

/// <param name="Saldo">Saldo atual. No cartão é negativo: o que a imobiliária deve.</param>
/// <param name="FaturaAtual">Cartão: compras desde o último fechamento (positivo).</param>
public sealed record ContaComSaldo(
    ContaBancaria Conta, decimal Saldo, decimal? FaturaAtual, DateOnly? ProximoFechamento, DateOnly? ProximoVencimento);

public sealed record LancamentoEmpresaLinha(
    Guid Id, string Descricao, decimal Valor, DateOnly Vencimento, DateOnly? DataPagamento, DateOnly Competencia,
    Guid? CategoriaId, string? Categoria, GrupoCategoriaEmpresa? Grupo, Guid? ContaId, string? Conta,
    Guid? TransferenciaId, string? PessoaNome, Guid? ImovelId, string? ImovelCodigo, string? Documento, string? Parcela,
    string? Observacao, bool Conciliado = false, bool DoImovel = false)
{
    public bool Pago => DataPagamento is not null;
    public bool Atrasado(DateOnly hoje) => DataPagamento is null && Vencimento < hoje;
    public bool Transferencia => TransferenciaId is not null;
}

public enum SituacaoFiltroEmpresa { TODOS, EM_ABERTO, PAGOS, ATRASADOS }

public sealed class FiltroLancamentosEmpresa
{
    /// <summary>Período pelo vencimento (em aberto) ou pela data de pagamento (pagos).</summary>
    public DateOnly De { get; set; } = new(DatasBR.Hoje().Year, DatasBR.Hoje().Month, 1);
    public DateOnly Ate { get; set; } = new DateOnly(DatasBR.Hoje().Year, DatasBR.Hoje().Month, 1).AddMonths(1).AddDays(-1);
    public Guid? ContaId { get; set; }
    public Guid? CategoriaId { get; set; }
    public SituacaoFiltroEmpresa Situacao { get; set; } = SituacaoFiltroEmpresa.TODOS;
    public string? Busca { get; set; }
}

/// <summary>Saldo de terceiros de um imóvel: aluguel recebido e ainda não repassado.</summary>
public sealed record TerceirosPorImovel(Guid? ImovelId, string? ImovelCodigo, decimal Saldo);

public sealed record PainelFinanceiroEmpresa(
    IReadOnlyList<ContaComSaldo> Contas,
    decimal Disponivel, decimal DividaCartoes, decimal Terceiros, decimal Proprio,
    decimal AReceber30, decimal APagar30, int Atrasados, decimal AtrasadosValor,
    IReadOnlyList<LancamentoEmpresaLinha> Proximos, IReadOnlyList<TerceirosPorImovel> TerceirosImoveis,
    decimal ReceitaMes, decimal DespesaMes, decimal ResultadoMes);

public enum NivelDre { GRUPO, CATEGORIA, TOTAL }

/// <param name="Meses">12 valores (jan..dez), com sinal: receita +, despesa −.</param>
public sealed record LinhaDre(string Rotulo, NivelDre Nivel, decimal[] Meses)
{
    public decimal Total => Meses.Sum();
}

public sealed record DreEmpresa(int Ano, IReadOnlyList<LinhaDre> Linhas, IReadOnlyList<LinhaDre> ForaDoResultado);

/* ------------------------------ Entradas ------------------------------ */

public sealed class ContaBancariaForm : IValidatableObject
{
    [Required(ErrorMessage = "Dê um nome à conta (ex.: Itaú PJ, Sicoob, Cartão Sicredi).")]
    [StringLength(80, ErrorMessage = "Nome com até 80 caracteres.")]
    public string Nome { get; set; } = "";

    public TipoContaBancaria Tipo { get; set; } = TipoContaBancaria.CORRENTE;

    [StringLength(60, ErrorMessage = "Banco com até 60 caracteres.")] public string? Banco { get; set; }
    [StringLength(20, ErrorMessage = "Agência com até 20 caracteres.")] public string? Agencia { get; set; }
    [StringLength(30, ErrorMessage = "Número com até 30 caracteres.")] public string? Numero { get; set; }

    /// <summary>Saldo no início do dia <see cref="DataSaldoInicial"/>. Cartão: o que já se devia (positivo).</summary>
    public decimal SaldoInicial { get; set; }

    [Required(ErrorMessage = "Informe a data do saldo inicial.")]
    public DateOnly? DataSaldoInicial { get; set; } = DatasBR.Hoje();

    public short? DiaFechamento { get; set; }
    public short? DiaVencimento { get; set; }
    public decimal? Limite { get; set; }
    public bool Ativa { get; set; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (Tipo == TipoContaBancaria.CARTAO_CREDITO)
        {
            if (DiaFechamento is null or < 1 or > 31) yield return new("Informe o dia de fechamento da fatura (1 a 31).", [nameof(DiaFechamento)]);
            if (DiaVencimento is null or < 1 or > 31) yield return new("Informe o dia de vencimento da fatura (1 a 31).", [nameof(DiaVencimento)]);
            if (SaldoInicial < 0) yield return new("No cartão, informe o valor devido como positivo.", [nameof(SaldoInicial)]);
        }
        if (Limite < 0) yield return new("O limite não pode ser negativo.", [nameof(Limite)]);
        if (decimal.Round(SaldoInicial, 2) != SaldoInicial) yield return new("Use no máximo 2 casas decimais.", [nameof(SaldoInicial)]);
    }

    public static ContaBancariaForm De(ContaBancaria c) => new()
    {
        Nome = c.Nome, Tipo = c.Tipo, Banco = c.Banco, Agencia = c.Agencia, Numero = c.Numero,
        SaldoInicial = c.Cartao ? -c.SaldoInicial : c.SaldoInicial, DataSaldoInicial = c.DataSaldoInicial,
        DiaFechamento = c.DiaFechamento, DiaVencimento = c.DiaVencimento, Limite = c.Limite, Ativa = c.Ativa,
    };

    internal void AplicarEm(ContaBancaria c)
    {
        var cartao = Tipo == TipoContaBancaria.CARTAO_CREDITO;
        c.Nome = Nome.Trim();
        c.Tipo = Tipo;
        c.Banco = Texto.Limpo(Banco);
        c.Agencia = Texto.Limpo(Agencia);
        c.Numero = Texto.Limpo(Numero);
        // no banco o cartão guarda a dívida como saldo negativo: assim todas as contas somam do mesmo jeito
        c.SaldoInicial = cartao ? -SaldoInicial : SaldoInicial;
        c.DataSaldoInicial = DataSaldoInicial!.Value;
        c.DiaFechamento = cartao ? DiaFechamento : null;
        c.DiaVencimento = cartao ? DiaVencimento : null;
        c.Limite = cartao ? Limite : null;
        c.Ativa = Ativa;
    }
}

public sealed class CategoriaEmpresaForm
{
    [Required(ErrorMessage = "Informe o nome da categoria.")]
    [StringLength(80, MinimumLength = 2, ErrorMessage = "Nome de 2 a 80 caracteres.")]
    public string Nome { get; set; } = "";
    public TipoCategoriaEmpresa Tipo { get; set; } = TipoCategoriaEmpresa.SAIDA;
    public GrupoCategoriaEmpresa Grupo { get; set; } = GrupoCategoriaEmpresa.DESPESA;
    public bool Ativa { get; set; } = true;
}

/// <summary>
/// Nova entrada/saída. O usuário digita o valor POSITIVO; o sinal vem do tipo. <see cref="Valor"/> é o total:
/// com <see cref="Parcelas"/> > 1 vira N lançamentos mensais. <see cref="TaxaRetida"/> (aluguel recebido) separa a
/// parte que é receita da imobiliária da parte que é do proprietário.
/// </summary>
public sealed class LancamentoEmpresaForm : IValidatableObject
{
    public TipoCategoriaEmpresa Tipo { get; set; } = TipoCategoriaEmpresa.SAIDA;

    [Required(ErrorMessage = "Informe a descrição.")]
    [StringLength(200, MinimumLength = 2, ErrorMessage = "A descrição deve ter de 2 a 200 caracteres.")]
    public string Descricao { get; set; } = "";

    [Required(ErrorMessage = "Informe o valor.")]
    public decimal? Valor { get; set; }

    [Required(ErrorMessage = "Escolha a categoria.")]
    public Guid? CategoriaId { get; set; }

    public Guid? ContaId { get; set; }

    [Required(ErrorMessage = "Informe o vencimento.")]
    public DateOnly? Vencimento { get; set; } = DatasBR.Hoje();

    public bool Pago { get; set; }
    public DateOnly? DataPagamento { get; set; }

    /// <summary>Mês de competência (qualquer dia do mês). Vazio = mês do vencimento.</summary>
    public DateOnly? Competencia { get; set; }

    [StringLength(200, ErrorMessage = "Nome com até 200 caracteres.")] public string? PessoaNome { get; set; }
    [StringLength(30, ErrorMessage = "Código com até 30 caracteres.")] public string? ImovelCodigo { get; set; }
    [StringLength(60, ErrorMessage = "Documento com até 60 caracteres.")] public string? Documento { get; set; }
    [StringLength(1000, ErrorMessage = "Observação com até 1000 caracteres.")] public string? Observacao { get; set; }

    [Range(1, 48, ErrorMessage = "Parcelas de 1 a 48.")]
    public int Parcelas { get; set; } = 1;

    public decimal? TaxaRetida { get; set; }
    public Guid? CategoriaTaxaId { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        foreach (var r in Dinheiro.Validar((nameof(Valor), Valor), (nameof(TaxaRetida), TaxaRetida))) yield return r;
        if (Valor == 0) yield return new("O valor não pode ser zero.", [nameof(Valor)]);
        if (Pago && DataPagamento is null) yield return new("Informe a data do pagamento.", [nameof(DataPagamento)]);
        if (Pago && ContaId is null) yield return new("Escolha a conta em que o dinheiro entrou/saiu.", [nameof(ContaId)]);
        if (TaxaRetida > 0)
        {
            if (TaxaRetida >= Valor) yield return new("A taxa deve ser menor que o valor recebido.", [nameof(TaxaRetida)]);
            if (CategoriaTaxaId is null) yield return new("Escolha a categoria da taxa.", [nameof(CategoriaTaxaId)]);
            if (Parcelas > 1) yield return new("Taxa retida não combina com parcelas.", [nameof(TaxaRetida)]);
        }
    }

    public static LancamentoEmpresaForm De(LancamentoEmpresa l, string? imovelCodigo) => new()
    {
        Tipo = l.Valor >= 0 ? TipoCategoriaEmpresa.ENTRADA : TipoCategoriaEmpresa.SAIDA,
        Descricao = l.Descricao, Valor = Math.Abs(l.Valor), CategoriaId = l.CategoriaId, ContaId = l.ContaId,
        Vencimento = l.Vencimento, Pago = l.DataPagamento is not null, DataPagamento = l.DataPagamento,
        Competencia = l.Competencia, PessoaNome = l.PessoaNome, ImovelCodigo = imovelCodigo, Documento = l.Documento,
        Observacao = l.Observacao,
    };
}

public sealed class TransferenciaForm : IValidatableObject
{
    [Required(ErrorMessage = "Escolha a conta de origem.")] public Guid? OrigemId { get; set; }
    [Required(ErrorMessage = "Escolha a conta de destino.")] public Guid? DestinoId { get; set; }
    [Required(ErrorMessage = "Informe o valor.")] public decimal? Valor { get; set; }
    [Required(ErrorMessage = "Informe a data.")] public DateOnly? Data { get; set; } = DatasBR.Hoje();
    [StringLength(200, ErrorMessage = "Descrição com até 200 caracteres.")] public string? Descricao { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        foreach (var r in Dinheiro.Validar((nameof(Valor), Valor))) yield return r;
        if (Valor == 0) yield return new("O valor não pode ser zero.", [nameof(Valor)]);
        if (OrigemId is not null && OrigemId == DestinoId) yield return new("Origem e destino devem ser contas diferentes.", [nameof(DestinoId)]);
    }
}

/* ------------------------------ Serviço ------------------------------ */

/// <summary>
/// Financeiro da Imobiliária: contas bancárias e cartões, plano de contas, contas a pagar/receber, transferências,
/// dinheiro de terceiros (aluguel que passa pela conta e é repassado) e DRE por competência.
/// </summary>
public sealed class FinanceiroEmpresaService(IDbContextFactory<InovabensDb> fabrica, AuditService audit)
{
    public const int LimiteLista = 2000;

    public static string RotuloTipoConta(TipoContaBancaria t) => t switch
    {
        TipoContaBancaria.CORRENTE => "Conta corrente",
        TipoContaBancaria.POUPANCA => "Poupança",
        TipoContaBancaria.APLICACAO => "Aplicação",
        TipoContaBancaria.CAIXA => "Caixa (dinheiro)",
        TipoContaBancaria.CARTAO_CREDITO => "Cartão de crédito",
        _ => t.ToString(),
    };

    public static string RotuloGrupo(GrupoCategoriaEmpresa g) => g switch
    {
        GrupoCategoriaEmpresa.RECEITA => "Receitas",
        GrupoCategoriaEmpresa.DEDUCAO => "Deduções (impostos sobre a receita)",
        GrupoCategoriaEmpresa.CUSTO => "Custos (comissões pagas)",
        GrupoCategoriaEmpresa.DESPESA => "Despesas operacionais",
        GrupoCategoriaEmpresa.FINANCEIRO => "Resultado financeiro",
        GrupoCategoriaEmpresa.TERCEIROS => "Dinheiro de terceiros (fora da DRE)",
        GrupoCategoriaEmpresa.PATRIMONIAL => "Sócios, empréstimos e investimentos (fora da DRE)",
        _ => g.ToString(),
    };

    /* ---------- Contas bancárias ---------- */

    public async Task<IReadOnlyList<ContaComSaldo>> ContasAsync(UsuarioAtual u, bool incluirInativas = false)
    {
        u.Exigir(P.FinanceiroEmpresaVer);
        await using var db = await fabrica.CreateDbContextAsync();
        return await ContasComSaldo(db, incluirInativas, DatasBR.Hoje());
    }

    private static async Task<List<ContaComSaldo>> ContasComSaldo(InovabensDb db, bool incluirInativas, DateOnly hoje)
    {
        var contas = await db.ContasBancarias.AsNoTracking().Where(c => incluirInativas || c.Ativa)
            .OrderBy(c => c.Tipo == TipoContaBancaria.CARTAO_CREDITO).ThenBy(c => c.Ordem).ThenBy(c => c.Nome).ToListAsync();
        // só o que foi pago a partir da data do saldo inicial mexe no saldo
        var movimentos = await (
            from l in db.LancamentosEmpresa
            join c in db.ContasBancarias on l.ContaId equals c.Id
            where l.ExcluidoEm == null && l.DataPagamento != null && l.DataPagamento >= c.DataSaldoInicial
            group l by l.ContaId into g
            select new { ContaId = g.Key!.Value, Soma = g.Sum(x => x.Valor) }).ToDictionaryAsync(x => x.ContaId, x => x.Soma);
        // lançamentos de imóvel pagos/recebidos pela conta da imobiliária também passam pelo banco
        var dosImoveis = await (
            from l in db.LancamentosFinanceiros
            join c in db.ContasBancarias on l.ContaBancariaId equals c.Id
            where l.ExcluidoEm == null && l.Situacao == SituacaoLancamento.LIQUIDADO && l.DataLiquidacao >= c.DataSaldoInicial
            group l by l.ContaBancariaId into g
            select new { ContaId = g.Key!.Value, Soma = g.Sum(x => x.Valor) }).ToDictionaryAsync(x => x.ContaId, x => x.Soma);

        var resultado = new List<ContaComSaldo>();
        foreach (var c in contas)
        {
            var saldo = c.SaldoInicial + movimentos.GetValueOrDefault(c.Id) + dosImoveis.GetValueOrDefault(c.Id);
            if (!c.Cartao) { resultado.Add(new(c, saldo, null, null, null)); continue; }
            var (ultimo, proximo) = Fechamentos(c.DiaFechamento ?? 1, hoje);
            // compras (saídas que não são pagamento de fatura) desde o último fechamento
            var fatura = -await db.LancamentosEmpresa.Where(l => l.ContaId == c.Id && l.ExcluidoEm == null
                    && l.TransferenciaId == null && l.DataPagamento > ultimo && l.DataPagamento <= proximo)
                .SumAsync(l => l.Valor)
                - await db.LancamentosFinanceiros.Where(l => l.ContaBancariaId == c.Id && l.ExcluidoEm == null
                    && l.Situacao == SituacaoLancamento.LIQUIDADO && l.DataLiquidacao > ultimo && l.DataLiquidacao <= proximo)
                .SumAsync(l => l.Valor);
            resultado.Add(new(c, saldo, fatura, proximo, Vencimento(proximo, c.DiaVencimento ?? 1)));
        }
        return resultado;
    }

    /// <summary>Saldo da conta no início do dia <paramref name="dia"/> (o que foi pago antes dele).</summary>
    internal async Task<decimal> SaldoEmAsync(InovabensDb db, ContaBancaria c, DateOnly dia)
    {
        var daEmpresa = await db.LancamentosEmpresa.Where(l => l.ContaId == c.Id && l.ExcluidoEm == null
            && l.DataPagamento >= c.DataSaldoInicial && l.DataPagamento < dia).SumAsync(l => l.Valor);
        var dosImoveis = await db.LancamentosFinanceiros.Where(l => l.ContaBancariaId == c.Id && l.ExcluidoEm == null
            && l.Situacao == SituacaoLancamento.LIQUIDADO && l.DataLiquidacao >= c.DataSaldoInicial && l.DataLiquidacao < dia).SumAsync(l => l.Valor);
        return c.SaldoInicial + daEmpresa + dosImoveis;
    }

    /// <summary>Último fechamento (antes de hoje) e próximo fechamento (hoje ou depois) para o dia informado.</summary>
    internal static (DateOnly Ultimo, DateOnly Proximo) Fechamentos(int dia, DateOnly hoje)
    {
        var desteMes = DiaNoMes(hoje.Year, hoje.Month, dia);
        if (desteMes >= hoje) return (DiaNoMes(hoje.AddMonths(-1).Year, hoje.AddMonths(-1).Month, dia), desteMes);
        var seguinte = hoje.AddMonths(1);
        return (desteMes, DiaNoMes(seguinte.Year, seguinte.Month, dia));
    }

    /// <summary>Vencimento da fatura que fecha em <paramref name="fechamento"/>: o dia de vencimento seguinte.</summary>
    internal static DateOnly Vencimento(DateOnly fechamento, int dia)
    {
        var v = DiaNoMes(fechamento.Year, fechamento.Month, dia);
        if (v <= fechamento) { var m = fechamento.AddMonths(1); v = DiaNoMes(m.Year, m.Month, dia); }
        return v;
    }

    private static DateOnly DiaNoMes(int ano, int mes, int dia) => new(ano, mes, Math.Min(dia, DateTime.DaysInMonth(ano, mes)));

    public async Task<ContaBancaria> ObterContaAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.FinanceiroEmpresaVer);
        await using var db = await fabrica.CreateDbContextAsync();
        return await db.ContasBancarias.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id)
            ?? throw new NaoEncontradoException("Conta não encontrada.");
    }

    public async Task<Guid> CriarContaAsync(UsuarioAtual u, ContaBancariaForm form)
    {
        u.Exigir(P.FinanceiroEmpresaEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await NomeContaLivre(db, form.Nome, null);
        var c = new ContaBancaria();
        form.AplicarEm(c);
        c.Ordem = await db.ContasBancarias.CountAsync();
        db.ContasBancarias.Add(c);
        audit.Registrar(db, u.Id, "contas_bancarias", c.Id, "CRIAR", depois: c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    public async Task AtualizarContaAsync(UsuarioAtual u, Guid id, ContaBancariaForm form)
    {
        u.Exigir(P.FinanceiroEmpresaEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await db.ContasBancarias.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Conta não encontrada.");
        await NomeContaLivre(db, form.Nome, id);
        if (c.Cartao != (form.Tipo == TipoContaBancaria.CARTAO_CREDITO)
            && await db.LancamentosEmpresa.AnyAsync(l => l.ContaId == id && l.ExcluidoEm == null))
            throw new RegraNegocioException(nameof(form.Tipo), "A conta já tem lançamentos: não dá para trocar entre cartão e conta bancária.");
        var antes = c.Copia();
        form.AplicarEm(c);
        audit.Registrar(db, u.Id, "contas_bancarias", c.Id, "ATUALIZAR", antes, c);
        await db.SaveChangesAsync();
    }

    private static async Task NomeContaLivre(InovabensDb db, string nome, Guid? exceto)
    {
        var n = nome.Trim().ToUpper();
        if (await db.ContasBancarias.AnyAsync(c => c.Nome.ToUpper() == n && c.Id != exceto))
            throw new RegraNegocioException(nameof(ContaBancariaForm.Nome), "Já existe uma conta com esse nome.");
    }

    /* ---------- Plano de contas ---------- */

    public async Task<IReadOnlyList<CategoriaEmpresa>> CategoriasAsync(UsuarioAtual u, bool incluirInativas = false)
    {
        u.Exigir(P.FinanceiroEmpresaVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var lista = await db.CategoriasEmpresa.AsNoTracking().Where(c => incluirInativas || c.Ativa).ToListAsync();
        return [.. lista.OrderBy(c => c.Grupo).ThenBy(c => c.Tipo).ThenBy(c => c.Nome, StringComparer.Create(Texto.PtBr, true))];
    }

    public async Task<Guid> CriarCategoriaAsync(UsuarioAtual u, CategoriaEmpresaForm form)
    {
        u.Exigir(P.FinanceiroEmpresaEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await NomeCategoriaLivre(db, form.Nome, null);
        var c = new CategoriaEmpresa { Nome = form.Nome.Trim(), Tipo = form.Tipo, Grupo = form.Grupo, Ativa = form.Ativa };
        db.CategoriasEmpresa.Add(c);
        audit.Registrar(db, u.Id, "categorias_empresa", c.Id, "CRIAR", depois: c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    public async Task AtualizarCategoriaAsync(UsuarioAtual u, Guid id, CategoriaEmpresaForm form)
    {
        u.Exigir(P.FinanceiroEmpresaEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await db.CategoriasEmpresa.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Categoria não encontrada.");
        await NomeCategoriaLivre(db, form.Nome, id);
        // trocar entrada/saída mudaria o sinal dos lançamentos já feitos
        if (c.Tipo != form.Tipo && await db.LancamentosEmpresa.AnyAsync(l => l.CategoriaId == id && l.ExcluidoEm == null))
            throw new RegraNegocioException(nameof(form.Tipo), "A categoria já tem lançamentos: não dá para trocar entre entrada e saída.");
        var antes = c.Copia();
        c.Nome = form.Nome.Trim();
        c.Tipo = form.Tipo;
        c.Grupo = form.Grupo;
        c.Ativa = form.Ativa;
        audit.Registrar(db, u.Id, "categorias_empresa", c.Id, "ATUALIZAR", antes, c);
        await db.SaveChangesAsync();
    }

    private static async Task NomeCategoriaLivre(InovabensDb db, string nome, Guid? exceto)
    {
        var n = nome.Trim().ToUpper();
        if (await db.CategoriasEmpresa.AnyAsync(c => c.Nome.ToUpper() == n && c.Id != exceto))
            throw new RegraNegocioException(nameof(CategoriaEmpresaForm.Nome), "Já existe uma categoria com esse nome.");
    }

    /* ---------- Lançamentos ---------- */

    public async Task<IReadOnlyList<LancamentoEmpresaLinha>> LancamentosAsync(UsuarioAtual u, FiltroLancamentosEmpresa f)
    {
        u.Exigir(P.FinanceiroEmpresaVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var hoje = DatasBR.Hoje();
        var q = db.LancamentosEmpresa.AsNoTracking().Where(l => l.ExcluidoEm == null);
        // pago conta pela data do pagamento; em aberto, pelo vencimento
        q = f.Situacao switch
        {
            SituacaoFiltroEmpresa.EM_ABERTO => q.Where(l => l.DataPagamento == null && l.Vencimento >= f.De && l.Vencimento <= f.Ate),
            SituacaoFiltroEmpresa.PAGOS => q.Where(l => l.DataPagamento >= f.De && l.DataPagamento <= f.Ate),
            // atrasado não respeita o período: tudo o que venceu e não foi pago
            SituacaoFiltroEmpresa.ATRASADOS => q.Where(l => l.DataPagamento == null && l.Vencimento < hoje),
            _ => q.Where(l => (l.DataPagamento ?? l.Vencimento) >= f.De && (l.DataPagamento ?? l.Vencimento) <= f.Ate),
        };
        if (f.ContaId is { } conta) q = q.Where(l => l.ContaId == conta);
        if (f.CategoriaId is { } cat) q = q.Where(l => l.CategoriaId == cat);
        if (Texto.Limpo(f.Busca) is { } busca)
        {
            var like = $"%{Texto.LikeLiteral(busca)}%";
            q = q.Where(l => EF.Functions.ILike(l.Descricao, like, @"\") || EF.Functions.ILike(l.PessoaNome ?? "", like, @"\")
                || EF.Functions.ILike(l.Documento ?? "", like, @"\") || (l.Imovel != null && EF.Functions.ILike(l.Imovel.Codigo, like, @"\")));
        }
        var linhas = await Linhas(q.OrderBy(l => l.DataPagamento ?? l.Vencimento).ThenBy(l => l.CriadoEm).Take(LimiteLista));
        // lançamentos de imóvel que passaram (ou vão passar) por uma conta da imobiliária: aparecem para o extrato fechar
        if (f.CategoriaId is null) linhas.AddRange(await DosImoveis(db, f, hoje));
        return [.. linhas.OrderBy(l => l.DataPagamento ?? l.Vencimento)];
    }

    private static async Task<List<LancamentoEmpresaLinha>> DosImoveis(InovabensDb db, FiltroLancamentosEmpresa f, DateOnly hoje)
    {
        var q = db.LancamentosFinanceiros.AsNoTracking().Where(l => l.ExcluidoEm == null && l.ContaBancariaId != null
            && (l.Situacao == SituacaoLancamento.LIQUIDADO || l.Situacao == SituacaoLancamento.ABERTO));
        q = f.Situacao switch
        {
            SituacaoFiltroEmpresa.EM_ABERTO => q.Where(l => l.Situacao == SituacaoLancamento.ABERTO && l.Data >= f.De && l.Data <= f.Ate),
            SituacaoFiltroEmpresa.PAGOS => q.Where(l => l.Situacao == SituacaoLancamento.LIQUIDADO && l.DataLiquidacao >= f.De && l.DataLiquidacao <= f.Ate),
            SituacaoFiltroEmpresa.ATRASADOS => q.Where(l => l.Situacao == SituacaoLancamento.ABERTO && l.Data < hoje),
            _ => q.Where(l => (l.DataLiquidacao ?? l.Data) >= f.De && (l.DataLiquidacao ?? l.Data) <= f.Ate),
        };
        if (f.ContaId is { } conta) q = q.Where(l => l.ContaBancariaId == conta);
        if (Texto.Limpo(f.Busca) is { } busca)
        {
            var like = $"%{Texto.LikeLiteral(busca)}%";
            q = q.Where(l => EF.Functions.ILike(l.Descricao, like, @"") || EF.Functions.ILike(l.PessoaNome ?? "", like, @"")
                || EF.Functions.ILike(l.Imovel!.Codigo, like, @""));
        }
        return await q.OrderBy(l => l.DataLiquidacao ?? l.Data).Take(LimiteLista).Select(l => new LancamentoEmpresaLinha(
            l.Id, l.Descricao, l.Valor, l.Data, l.Situacao == SituacaoLancamento.LIQUIDADO ? l.DataLiquidacao : null,
            new DateOnly(l.Data.Year, l.Data.Month, 1), null, "Imóvel · " + l.Conta!.Nome, null, l.ContaBancariaId,
            db.ContasBancarias.Where(c => c.Id == l.ContaBancariaId).Select(c => c.Nome).FirstOrDefault(), null, l.PessoaNome,
            l.ImovelId, l.Imovel!.Codigo, null, null, l.Observacao, l.ExtratoItemId != null, true)).ToListAsync();
    }

    private static Task<List<LancamentoEmpresaLinha>> Linhas(IQueryable<LancamentoEmpresa> q) =>
        q.Select(l => new LancamentoEmpresaLinha(
            l.Id, l.Descricao, l.Valor, l.Vencimento, l.DataPagamento, l.Competencia,
            l.CategoriaId, l.Categoria != null ? l.Categoria.Nome : null, l.Categoria != null ? l.Categoria.Grupo : null,
            l.ContaId, l.Conta != null ? l.Conta.Nome : null, l.TransferenciaId, l.PessoaNome,
            l.ImovelId, l.Imovel != null ? l.Imovel.Codigo : null, l.Documento, l.Parcela, l.Observacao,
            l.ExtratoItemId != null)).ToListAsync();

    public async Task<LancamentoEmpresaForm> FormularioAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.FinanceiroEmpresaVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await db.LancamentosEmpresa.AsNoTracking().Include(x => x.Imovel)
            .FirstOrDefaultAsync(x => x.Id == id && x.ExcluidoEm == null) ?? throw new NaoEncontradoException("Lançamento não encontrado.");
        if (l.TransferenciaId is not null) throw new RegraNegocioException("", "Transferência não se edita: exclua e lance de novo.");
        return LancamentoEmpresaForm.De(l, l.Imovel?.Codigo);
    }

    /// <returns>Os lançamentos criados (mais de um com parcelas ou taxa retida).</returns>
    public async Task<IReadOnlyList<Guid>> CriarAsync(UsuarioAtual u, LancamentoEmpresaForm form)
    {
        u.Exigir(P.FinanceiroEmpresaEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var categoria = await CategoriaValida(db, form.CategoriaId!.Value, form.Tipo);
        await ContaValida(db, form.ContaId);
        var imovelId = await ImovelPeloCodigo(db, form.ImovelCodigo);
        var sinal = form.Tipo == TipoCategoriaEmpresa.ENTRADA ? 1 : -1;
        var total = form.Valor!.Value;
        var criados = new List<LancamentoEmpresa>();

        if (form.TaxaRetida is > 0 and var taxa)
        {
            // aluguel recebido: a taxa é receita da imobiliária, o resto é do proprietário (terceiros)
            var catTaxa = await CategoriaValida(db, form.CategoriaTaxaId!.Value, form.Tipo);
            var pagoEm = form.Pago ? form.DataPagamento : null;
            criados.Add(Novo(form, u, categoria.Id, imovelId, sinal * (total - taxa), form.Vencimento!.Value, pagoEm, null, form.Descricao.Trim(), null));
            criados.Add(Novo(form, u, catTaxa.Id, imovelId, sinal * taxa, form.Vencimento!.Value, pagoEm, null, $"{catTaxa.Nome} — {form.Descricao.Trim()}", null));
        }
        else
        {
            var n = form.Parcelas;
            var parcela = decimal.Round(total / n, 2);
            // compra parcelada no cartão: cada parcela cai na fatura do seu mês; fora do cartão, só a 1ª pode já estar paga
            var cartao = form.ContaId is { } cid && await EhCartao(db, cid);
            for (var i = 0; i < n; i++)
            {
                // a última parcela leva a sobra do arredondamento
                var valor = i == n - 1 ? total - parcela * (n - 1) : parcela;
                var venc = form.Vencimento!.Value.AddMonths(i);
                DateOnly? pagoEm = !form.Pago ? null : i == 0 ? form.DataPagamento : cartao ? form.DataPagamento!.Value.AddMonths(i) : null;
                criados.Add(Novo(form, u, categoria.Id, imovelId, sinal * valor, venc, pagoEm,
                    n > 1 ? $"{i + 1}/{n}" : null, form.Descricao.Trim(), n > 1 ? venc : null));
            }
        }
        foreach (var l in criados) await MesesFechados.Garantir(db, l.DataPagamento, l.Competencia);
        db.LancamentosEmpresa.AddRange(criados);
        foreach (var l in criados) audit.Registrar(db, u.Id, "lancamentos_empresa", l.Id, "CRIAR", depois: l);
        await db.SaveChangesAsync();
        return [.. criados.Select(l => l.Id)];
    }

    /// <param name="competencia">Parcela: o mês da própria parcela. Nulo = o do formulário (ou do vencimento).</param>
    private static LancamentoEmpresa Novo(LancamentoEmpresaForm f, UsuarioAtual u, Guid categoriaId, Guid? imovelId, decimal valor,
        DateOnly vencimento, DateOnly? pagoEm, string? parcela, string descricao, DateOnly? competencia) => new()
    {
        Descricao = descricao, Valor = valor, Vencimento = vencimento, DataPagamento = pagoEm,
        Competencia = MesDe(competencia ?? f.Competencia ?? vencimento),
        CategoriaId = categoriaId, ContaId = f.ContaId, PessoaNome = Texto.Limpo(f.PessoaNome), ImovelId = imovelId,
        Documento = Texto.Limpo(f.Documento), Observacao = Texto.Limpo(f.Observacao), Parcela = parcela,
        CriadoPor = u.Id, AtualizadoPor = u.Id,
    };

    public async Task AtualizarAsync(UsuarioAtual u, Guid id, LancamentoEmpresaForm form)
    {
        u.Exigir(P.FinanceiroEmpresaEditar);
        form.Parcelas = 1;
        form.TaxaRetida = null;
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await Lancamento(db, id);
        if (l.TransferenciaId is not null) throw new RegraNegocioException("", "Transferência não se edita: exclua e lance de novo.");
        var categoria = await CategoriaValida(db, form.CategoriaId!.Value, form.Tipo, aceitarInativa: form.CategoriaId == l.CategoriaId);
        await ContaValida(db, form.ContaId, aceitarInativa: form.ContaId == l.ContaId);
        var novoValor = (form.Tipo == TipoCategoriaEmpresa.ENTRADA ? 1 : -1) * form.Valor!.Value;
        var novoPagamento = form.Pago ? form.DataPagamento : null;
        // conciliado: valor, conta e data do pagamento são os do banco
        if (l.ExtratoItemId is not null && (novoValor != l.Valor || form.ContaId != l.ContaId || novoPagamento != l.DataPagamento))
            throw new RegraNegocioException("", "Lançamento conciliado com o extrato: valor, conta e data de pagamento não mudam. Desfaça a conciliação antes.");
        var novaCompetencia = MesDe(form.Competencia ?? form.Vencimento!.Value);
        await MesesFechados.Garantir(db, l.DataPagamento, l.Competencia, novoPagamento, novaCompetencia);
        var antes = l.Copia();
        l.Descricao = form.Descricao.Trim();
        l.Valor = (form.Tipo == TipoCategoriaEmpresa.ENTRADA ? 1 : -1) * form.Valor!.Value;
        l.CategoriaId = categoria.Id;
        l.ContaId = form.ContaId;
        l.Vencimento = form.Vencimento!.Value;
        l.DataPagamento = form.Pago ? form.DataPagamento : null;
        l.Competencia = MesDe(form.Competencia ?? form.Vencimento!.Value);
        l.PessoaNome = Texto.Limpo(form.PessoaNome);
        l.ImovelId = await ImovelPeloCodigo(db, form.ImovelCodigo);
        l.Documento = Texto.Limpo(form.Documento);
        l.Observacao = Texto.Limpo(form.Observacao);
        l.AtualizadoPor = u.Id;
        l.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "lancamentos_empresa", l.Id, "ATUALIZAR", antes, l);
        await db.SaveChangesAsync();
    }

    /// <summary>Baixa: marca como pago/recebido na data e conta informadas.</summary>
    public async Task PagarAsync(UsuarioAtual u, Guid id, DateOnly data, Guid contaId)
    {
        u.Exigir(P.FinanceiroEmpresaEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await Lancamento(db, id);
        if (l.DataPagamento is not null) throw new RegraNegocioException("", "Este lançamento já está pago.");
        await ContaValida(db, contaId);
        await MesesFechados.Garantir(db, data, l.Competencia);
        var antes = l.Copia();
        l.DataPagamento = data;
        l.ContaId = contaId;
        l.AtualizadoPor = u.Id;
        l.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "lancamentos_empresa", l.Id, "PAGAR", antes, l);
        await db.SaveChangesAsync();
    }

    /// <summary>Desfaz a baixa (volta a ficar em aberto).</summary>
    public async Task DesfazerPagamentoAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.FinanceiroEmpresaEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await Lancamento(db, id);
        if (l.TransferenciaId is not null) throw new RegraNegocioException("", "Transferência não fica em aberto: exclua se estiver errada.");
        if (l.ExtratoItemId is not null) throw new RegraNegocioException("", "Lançamento conciliado com o extrato: desfaça a conciliação antes.");
        await MesesFechados.Garantir(db, l.DataPagamento, l.Competencia);
        var antes = l.Copia();
        l.DataPagamento = null;
        l.AtualizadoPor = u.Id;
        l.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "lancamentos_empresa", l.Id, "DESFAZER_PAGAMENTO", antes, l);
        await db.SaveChangesAsync();
    }

    /// <summary>Exclusão lógica. Transferência sai inteira (os dois lados).</summary>
    public async Task ExcluirAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.FinanceiroEmpresaEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await Lancamento(db, id);
        var todos = l.TransferenciaId is { } t
            ? await db.LancamentosEmpresa.Where(x => x.TransferenciaId == t && x.ExcluidoEm == null).ToListAsync()
            : [l];
        if (todos.Any(x => x.ExtratoItemId is not null))
            throw new RegraNegocioException("", "Lançamento conciliado com o extrato: desfaça a conciliação (aba Conciliação) antes de excluir.");
        foreach (var x in todos) await MesesFechados.Garantir(db, x.DataPagamento, x.Competencia);
        foreach (var x in todos)
        {
            var antes = x.Copia();
            x.ExcluidoEm = DateTime.UtcNow;
            x.AtualizadoPor = u.Id;
            audit.Registrar(db, u.Id, "lancamentos_empresa", x.Id, "EXCLUIR", antes, x);
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Dinheiro que muda de conta (inclusive pagar a fatura do cartão): sai da origem e entra no destino na mesma data.
    /// Não é receita nem despesa, por isso não tem categoria nem aparece na DRE.
    /// </summary>
    public async Task<Guid> TransferirAsync(UsuarioAtual u, TransferenciaForm form)
    {
        u.Exigir(P.FinanceiroEmpresaEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var origem = await ContaValida(db, form.OrigemId) ?? throw new NaoEncontradoException("Conta não encontrada.");
        var destino = await ContaValida(db, form.DestinoId) ?? throw new NaoEncontradoException("Conta não encontrada.");
        var id = Guid.NewGuid();
        var data = form.Data!.Value;
        await MesesFechados.Garantir(db, data);
        var descricao = Texto.Limpo(form.Descricao) ?? (destino.Cartao ? $"Pagamento da fatura {destino.Nome}" : $"Transferência {origem.Nome} → {destino.Nome}");
        LancamentoEmpresa Lado(ContaBancaria c, decimal v) => new()
        {
            Descricao = descricao, Valor = v, Vencimento = data, DataPagamento = data, Competencia = MesDe(data),
            ContaId = c.Id, TransferenciaId = id, CriadoPor = u.Id, AtualizadoPor = u.Id,
        };
        var saida = Lado(origem, -form.Valor!.Value);
        var entrada = Lado(destino, form.Valor!.Value);
        db.LancamentosEmpresa.AddRange(saida, entrada);
        audit.Registrar(db, u.Id, "lancamentos_empresa", saida.Id, "TRANSFERIR", depois: saida);
        audit.Registrar(db, u.Id, "lancamentos_empresa", entrada.Id, "TRANSFERIR", depois: entrada);
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Nome da conta do outro lado de cada transferência (para mostrar "Itaú → Cartão").</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> ContrapartesAsync(UsuarioAtual u, IEnumerable<Guid> transferencias)
    {
        u.Exigir(P.FinanceiroEmpresaVer);
        var ids = transferencias.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();
        await using var db = await fabrica.CreateDbContextAsync();
        var lados = await db.LancamentosEmpresa.AsNoTracking()
            .Where(l => l.TransferenciaId != null && ids.Contains(l.TransferenciaId.Value) && l.ExcluidoEm == null)
            .Select(l => new { l.Id, T = l.TransferenciaId!.Value, Conta = l.Conta!.Nome }).ToListAsync();
        return lados.ToDictionary(l => l.Id, l => lados.FirstOrDefault(o => o.T == l.T && o.Id != l.Id)?.Conta ?? "—");
    }

    /* ---------- Painel e DRE ---------- */

    public async Task<PainelFinanceiroEmpresa> PainelAsync(UsuarioAtual u)
    {
        u.Exigir(P.FinanceiroEmpresaVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var hoje = DatasBR.Hoje();
        var contas = await ContasComSaldo(db, false, hoje);
        var disponivel = contas.Where(c => !c.Conta.Cartao).Sum(c => c.Saldo);
        var divida = -contas.Where(c => c.Conta.Cartao).Sum(c => c.Saldo);

        var ativos = db.LancamentosEmpresa.AsNoTracking().Where(l => l.ExcluidoEm == null);
        // dinheiro de terceiros: aluguel que entrou menos o que já foi repassado (só o que de fato passou pela conta)
        var terceiros = await ativos.Where(l => l.DataPagamento != null && l.Categoria!.Grupo == GrupoCategoriaEmpresa.TERCEIROS)
            .GroupBy(l => new { l.ImovelId, Codigo = l.Imovel != null ? l.Imovel.Codigo : null })
            .Select(g => new TerceirosPorImovel(g.Key.ImovelId, g.Key.Codigo, g.Sum(x => x.Valor))).ToListAsync();
        var totalTerceiros = terceiros.Sum(t => t.Saldo);

        var limite = hoje.AddDays(30);
        var abertos = await ativos.Where(l => l.DataPagamento == null && l.Vencimento <= limite)
            .Select(l => new { l.Valor, l.Vencimento }).ToListAsync();
        var proximos = await Linhas(ativos.Where(l => l.DataPagamento == null && l.Vencimento <= limite)
            .OrderBy(l => l.Vencimento).Take(15));

        var mes = MesDe(hoje);
        var doMes = await ativos.Where(l => l.Competencia == mes && l.Categoria != null
                && l.Categoria.Grupo != GrupoCategoriaEmpresa.TERCEIROS && l.Categoria.Grupo != GrupoCategoriaEmpresa.PATRIMONIAL)
            .Select(l => l.Valor).ToListAsync();
        var receita = doMes.Where(v => v > 0).Sum();
        var despesa = -doMes.Where(v => v < 0).Sum();

        return new(contas, disponivel, divida, totalTerceiros, disponivel - totalTerceiros,
            abertos.Where(a => a.Valor > 0 && a.Vencimento >= hoje).Sum(a => a.Valor),
            -abertos.Where(a => a.Valor < 0 && a.Vencimento >= hoje).Sum(a => a.Valor),
            abertos.Count(a => a.Vencimento < hoje), abertos.Where(a => a.Vencimento < hoje).Sum(a => a.Valor),
            proximos, [.. terceiros.Where(t => t.Saldo != 0).OrderByDescending(t => t.Saldo)],
            receita, despesa, receita - despesa);
    }

    /// <summary>DRE por competência (inclui o que ainda não foi pago): receitas, deduções, custos, despesas e financeiro.</summary>
    public async Task<DreEmpresa> DreAsync(UsuarioAtual u, int ano)
    {
        u.Exigir(P.FinanceiroEmpresaVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var de = new DateOnly(ano, 1, 1);
        var ate = new DateOnly(ano, 12, 1);
        var linhas = await db.LancamentosEmpresa.AsNoTracking()
            .Where(l => l.ExcluidoEm == null && l.CategoriaId != null && l.Competencia >= de && l.Competencia <= ate)
            .GroupBy(l => new { l.Categoria!.Nome, l.Categoria.Grupo, l.Competencia.Month })
            .Select(g => new { g.Key.Nome, g.Key.Grupo, Mes = g.Key.Month, Soma = g.Sum(x => x.Valor) }).ToListAsync();

        decimal[] Meses(IEnumerable<(int Mes, decimal Soma)> itens)
        {
            var m = new decimal[12];
            foreach (var (mes, soma) in itens) m[mes - 1] += soma;
            return m;
        }
        decimal[] DoGrupo(GrupoCategoriaEmpresa g) => Meses(linhas.Where(l => l.Grupo == g).Select(l => (l.Mes, l.Soma)));
        decimal[] Somar(params decimal[][] partes) => [.. Enumerable.Range(0, 12).Select(i => partes.Sum(p => p[i]))];
        IEnumerable<LinhaDre> Grupo(GrupoCategoriaEmpresa g, string rotulo)
        {
            yield return new LinhaDre(rotulo, NivelDre.GRUPO, DoGrupo(g));
            foreach (var cat in linhas.Where(l => l.Grupo == g).GroupBy(l => l.Nome).OrderBy(c => c.Key, StringComparer.Create(Texto.PtBr, true)))
                yield return new LinhaDre(cat.Key, NivelDre.CATEGORIA, Meses(cat.Select(l => (l.Mes, l.Soma))));
        }

        var receita = DoGrupo(GrupoCategoriaEmpresa.RECEITA);
        var liquida = Somar(receita, DoGrupo(GrupoCategoriaEmpresa.DEDUCAO));
        var bruto = Somar(liquida, DoGrupo(GrupoCategoriaEmpresa.CUSTO));
        var operacional = Somar(bruto, DoGrupo(GrupoCategoriaEmpresa.DESPESA));
        var final = Somar(operacional, DoGrupo(GrupoCategoriaEmpresa.FINANCEIRO));

        List<LinhaDre> dre =
        [
            .. Grupo(GrupoCategoriaEmpresa.RECEITA, "Receita bruta"),
            .. Grupo(GrupoCategoriaEmpresa.DEDUCAO, "(−) Deduções e impostos"),
            new("= Receita líquida", NivelDre.TOTAL, liquida),
            .. Grupo(GrupoCategoriaEmpresa.CUSTO, "(−) Custos (comissões pagas)"),
            new("= Lucro bruto", NivelDre.TOTAL, bruto),
            .. Grupo(GrupoCategoriaEmpresa.DESPESA, "(−) Despesas operacionais"),
            new("= Resultado operacional", NivelDre.TOTAL, operacional),
            .. Grupo(GrupoCategoriaEmpresa.FINANCEIRO, "(+/−) Resultado financeiro"),
            new("= Resultado líquido", NivelDre.TOTAL, final),
        ];
        // livro caixa dos imóveis pago/recebido pelas contas da imobiliária: mexe no caixa, não no resultado dela
        var fimAno = new DateOnly(ano, 12, 31);
        var imoveis = await db.LancamentosFinanceiros.AsNoTracking()
            .Where(l => l.ExcluidoEm == null && l.ContaBancariaId != null && l.Situacao == SituacaoLancamento.LIQUIDADO
                && l.DataLiquidacao >= de && l.DataLiquidacao <= fimAno)
            .GroupBy(l => l.DataLiquidacao!.Value.Month).Select(g => new { Mes = g.Key, Soma = g.Sum(x => x.Valor) }).ToListAsync();
        List<LinhaDre> fora =
        [
            .. Grupo(GrupoCategoriaEmpresa.TERCEIROS, "Dinheiro de terceiros (aluguéis e repasses)"),
            .. Grupo(GrupoCategoriaEmpresa.PATRIMONIAL, "Sócios, empréstimos e investimentos"),
            new("Imóveis (livro caixa) pagos pelas contas da imobiliária", NivelDre.GRUPO, Meses(imoveis.Select(i => (i.Mes, i.Soma)))),
        ];
        return new(ano, dre, fora);
    }

    private static readonly string[] NomesMeses = ["jan", "fev", "mar", "abr", "mai", "jun", "jul", "ago", "set", "out", "nov", "dez"];

    /// <summary>DRE do ano em Excel: aba DRE (com o "fora do resultado" abaixo) e aba com os lançamentos que a compõem.</summary>
    public async Task<Relatorios.Arquivo> DreExcelAsync(UsuarioAtual u, int ano)
    {
        u.Exigir(P.FinanceiroEmpresaExportar);
        var dre = await DreAsync(u, ano);
        await using var db = await fabrica.CreateDbContextAsync();
        var lancs = await Linhas(db.LancamentosEmpresa.AsNoTracking()
            .Where(l => l.ExcluidoEm == null && l.CategoriaId != null && l.Competencia >= new DateOnly(ano, 1, 1) && l.Competencia <= new DateOnly(ano, 12, 1))
            .OrderBy(l => l.Competencia).ThenBy(l => l.Vencimento));

        using var wb = new ClosedXML.Excel.XLWorkbook();
        static ClosedXML.Excel.XLCellValue T(string? s) => string.IsNullOrEmpty(s) ? ClosedXML.Excel.Blank.Value : s;
        ClosedXML.Excel.XLCellValue[] Linha(LinhaDre l) =>
            [l.Nivel == NivelDre.CATEGORIA ? "    " + l.Rotulo : l.Rotulo, .. l.Meses.Select(v => (ClosedXML.Excel.XLCellValue)v), l.Total];
        var linhasDre = dre.Linhas.Select(Linha)
            .Append([""]).Append(["Fora do resultado (só caixa)"]).Concat(dre.ForaDoResultado.Select(Linha)).ToList();
        var ws = Relatorios.RelatoriosService.Aba(wb, $"DRE {ano}",
            [("Linha", 46), .. NomesMeses.Select(m => (m, 12.0)), ("Total", 14)], linhasDre);
        ws.Range(2, 2, linhasDre.Count + 1, 14).Style.NumberFormat.Format = "#,##0.00;[Red]-#,##0.00";
        var todas = dre.Linhas.Concat([null, null]).Concat(dre.ForaDoResultado).ToList();
        for (var i = 0; i < todas.Count; i++)
            if (todas[i] is { Nivel: not NivelDre.CATEGORIA } || (todas[i] is null && i == dre.Linhas.Count + 1))
                ws.Row(i + 2).Style.Font.SetBold();

        var wl = Relatorios.RelatoriosService.Aba(wb, "Lançamentos",
        [
            ("Competência", 12), ("Vencimento", 12), ("Pagamento", 12), ("Descrição", 40), ("Categoria", 32), ("Grupo", 30),
            ("Conta", 20), ("Pessoa", 24), ("Imóvel", 10), ("Documento", 14), ("Valor", 14),
        ],
        lancs.Select(l => new ClosedXML.Excel.XLCellValue[]
        {
            l.Competencia.ToDateTime(TimeOnly.MinValue), l.Vencimento.ToDateTime(TimeOnly.MinValue),
            l.DataPagamento is { } d ? d.ToDateTime(TimeOnly.MinValue) : ClosedXML.Excel.Blank.Value,
            l.Descricao, T(l.Categoria), l.Grupo is { } g ? RotuloGrupo(g) : ClosedXML.Excel.Blank.Value, T(l.Conta), T(l.PessoaNome),
            T(l.ImovelCodigo), T(l.Documento), l.Valor,
        }));
        wl.Column(1).Style.NumberFormat.Format = "mm/yyyy";
        wl.Columns(2, 3).Style.NumberFormat.Format = "dd/mm/yyyy";
        wl.Column(11).Style.NumberFormat.Format = "#,##0.00;[Red]-#,##0.00";
        if (lancs.Count > 0) wl.Range(1, 1, lancs.Count + 1, 11).SetAutoFilter();

        audit.Registrar(db, u.Id, "lancamentos_empresa", null, "EXPORTAR", depois: new { relatorio = "dre", formato = "xlsx", ano, lancamentos = lancs.Count });
        await db.SaveChangesAsync();
        return new(Relatorios.RelatoriosService.Salvar(wb), $"dre-{ano}.xlsx", Relatorios.RelatoriosService.TipoXlsx);
    }

    /// <summary>DRE do ano em PDF (A4 deitado, 12 meses + total).</summary>
    public async Task<Relatorios.Arquivo> DrePdfAsync(UsuarioAtual u, int ano)
    {
        u.Exigir(P.FinanceiroEmpresaExportar);
        var dre = await DreAsync(u, ano);
        static string N(decimal v) => v == 0 ? "–" : v.ToString("N2", Texto.PtBr);
        string[] Linha(LinhaDre l) => [l.Nivel == NivelDre.CATEGORIA ? "   " + l.Rotulo : l.Rotulo, .. l.Meses.Select(N), N(l.Total)];
        (string, float)[] colunas = [("", 4.2f), .. NomesMeses.Select(m => (m, 1f)), ("Total", 1.3f)];
        var pdf = Relatorios.RelatoriosService.NovoPdf($"DRE {ano}", "Inova Bens · regime de competência",
            $"Gerado em {DatasBR.Agora():dd/MM/yyyy HH:mm}", c =>
            {
                Relatorios.RelatoriosService.Tabela(c, colunas, [.. dre.Linhas.Select(Linha)], colunasDireita: 13);
                Relatorios.RelatoriosService.Secao(c, "Fora do resultado (só caixa)");
                Relatorios.RelatoriosService.Tabela(c, colunas, [.. dre.ForaDoResultado.Select(Linha)], colunasDireita: 13);
            }, paisagem: true);
        await using var db = await fabrica.CreateDbContextAsync();
        audit.Registrar(db, u.Id, "lancamentos_empresa", null, "EXPORTAR", depois: new { relatorio = "dre", formato = "pdf", ano });
        await db.SaveChangesAsync();
        return new(pdf, $"dre-{ano}.pdf", Relatorios.RelatoriosService.TipoPdf);
    }

    /* ---------- Apoio ---------- */

    private static DateOnly MesDe(DateOnly d) => new(d.Year, d.Month, 1);

    private static async Task<LancamentoEmpresa> Lancamento(InovabensDb db, Guid id) =>
        await db.LancamentosEmpresa.FirstOrDefaultAsync(l => l.Id == id && l.ExcluidoEm == null)
        ?? throw new NaoEncontradoException("Lançamento não encontrado.");

    private static async Task<CategoriaEmpresa> CategoriaValida(InovabensDb db, Guid id, TipoCategoriaEmpresa tipo, bool aceitarInativa = false)
    {
        var c = await db.CategoriasEmpresa.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id)
            ?? throw new RegraNegocioException(nameof(LancamentoEmpresaForm.CategoriaId), "Categoria não encontrada.");
        if (!c.Ativa && !aceitarInativa) throw new RegraNegocioException(nameof(LancamentoEmpresaForm.CategoriaId), "Categoria inativa.");
        if (c.Tipo != tipo)
            throw new RegraNegocioException(nameof(LancamentoEmpresaForm.CategoriaId),
                tipo == TipoCategoriaEmpresa.ENTRADA ? "Escolha uma categoria de entrada." : "Escolha uma categoria de saída.");
        return c;
    }

    private static async Task<ContaBancaria?> ContaValida(InovabensDb db, Guid? id, bool aceitarInativa = false)
    {
        if (id is null) return null;
        var c = await db.ContasBancarias.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id)
            ?? throw new RegraNegocioException(nameof(LancamentoEmpresaForm.ContaId), "Conta não encontrada.");
        if (!c.Ativa && !aceitarInativa) throw new RegraNegocioException(nameof(LancamentoEmpresaForm.ContaId), "Conta inativa.");
        return c;
    }

    private static Task<bool> EhCartao(InovabensDb db, Guid contaId) =>
        db.ContasBancarias.AnyAsync(c => c.Id == contaId && c.Tipo == TipoContaBancaria.CARTAO_CREDITO);

    internal static async Task<Guid?> ImovelPeloCodigo(InovabensDb db, string? codigo)
    {
        if (Texto.Limpo(codigo) is not { } c) return null;
        var cod = c.ToUpper();
        return await db.Imoveis.Where(i => i.Codigo.ToUpper() == cod).Select(i => (Guid?)i.Id).FirstOrDefaultAsync()
            ?? throw new RegraNegocioException(nameof(LancamentoEmpresaForm.ImovelCodigo), $"Imóvel {c} não encontrado.");
    }
}
