using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Financeiro;

/* ------------------------------ Consultas ------------------------------ */

public sealed record ResultadoImportacao(int Lidos, int Novos, int Repetidos, int Sugeridos, string? AvisoConta);

public enum TipoSugestao { NENHUMA, LANCAMENTO, LANCAMENTO_IMOVEL, REGRA }

/// <summary>O que o sistema acha que é a linha do extrato.</summary>
/// <param name="Id">Lançamento (da imobiliária ou do imóvel) já existente; na REGRA, a categoria.</param>
/// <param name="Grupo">Vários lançamentos da imobiliária criados juntos que, somados, dão o valor do banco (ex.: aluguel + taxa).</param>
public sealed record SugestaoConciliacao(TipoSugestao Tipo, Guid? Id, string Texto, bool EmAberto = false, string? PessoaNome = null,
    IReadOnlyList<Guid>? Grupo = null);

public sealed record ItemConciliacao(
    Guid Id, DateOnly Data, decimal Valor, string Descricao, string? Documento, SugestaoConciliacao Sugestao);

public sealed record ItemConciliado(
    Guid Id, DateOnly Data, decimal Valor, string Descricao, bool Ignorado, IReadOnlyList<string> Lancamentos);

/// <param name="SaldoBanco">Saldo informado pelo banco no último OFX importado (para conferir com o do sistema).</param>
public sealed record ConciliacaoConta(
    ContaComSaldo Conta, IReadOnlyList<ItemConciliacao> Pendentes, IReadOnlyList<ItemConciliado> Conciliados,
    decimal? SaldoBanco, DateOnly? DataSaldoBanco, decimal? SaldoSistemaNaData, DateTime? UltimaImportacao);

/// <summary>Conferência antes de fechar o mês.</summary>
public sealed record ResumoFechamento(
    DateOnly Mes, bool Fechado, DateTime? FechadoEm, string? FechadoPor, int ExtratoPendente, int EmAbertoVencidos,
    IReadOnlyList<(string Conta, decimal Saldo)> SaldosNoFim);

/* ------------------------------ Entradas ------------------------------ */

public sealed class ParteExtrato
{
    [Required(ErrorMessage = "Escolha a categoria.")] public Guid? CategoriaId { get; set; }
    [Required(ErrorMessage = "Informe o valor.")] public decimal? Valor { get; set; }
    [StringLength(200, ErrorMessage = "Descrição com até 200 caracteres.")] public string? Descricao { get; set; }
}

/// <summary>Cria lançamento(s) pagos a partir da linha do extrato. Mais de uma parte = dividir (ex.: aluguel + taxa).</summary>
public sealed class CriarDoExtratoForm : IValidatableObject
{
    public List<ParteExtrato> Partes { get; set; } = [new()];
    [StringLength(200, ErrorMessage = "Nome com até 200 caracteres.")] public string? PessoaNome { get; set; }
    [StringLength(30, ErrorMessage = "Código com até 30 caracteres.")] public string? ImovelCodigo { get; set; }
    /// <summary>Guardar a regra: na próxima vez, a mesma descrição já vem com esta categoria.</summary>
    public bool Aprender { get; set; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (Partes.Count == 0) yield return new("Informe ao menos uma categoria.", [nameof(Partes)]);
        foreach (var p in Partes)
        {
            if (p.CategoriaId is null) yield return new("Escolha a categoria de cada parte.", [nameof(Partes)]);
            if (p.Valor is null or <= 0 || decimal.Round(p.Valor.Value, 2) != p.Valor) yield return new("Cada parte precisa de um valor positivo (até 2 casas).", [nameof(Partes)]);
        }
    }
}

/* ------------------------------ Meses fechados ------------------------------ */

internal static class MesesFechados
{
    public static DateOnly Mes(DateOnly d) => new(d.Year, d.Month, 1);

    /// <summary>Barra a gravação se alguma das datas cai num mês fechado.</summary>
    public static async Task Garantir(InovabensDb db, params DateOnly?[] datas)
    {
        var meses = datas.Where(d => d is not null).Select(d => Mes(d!.Value)).Distinct().ToList();
        if (meses.Count == 0) return;
        var fechado = await db.FechamentosFinanceiros.Where(f => meses.Contains(f.Mes)).OrderBy(f => f.Mes)
            .Select(f => (DateOnly?)f.Mes).FirstOrDefaultAsync();
        if (fechado is { } m)
            throw new RegraNegocioException("", $"O mês {m:MM/yyyy} está fechado. Reabra o mês para alterar lançamentos dele.");
    }
}

/* ------------------------------ Serviço ------------------------------ */

/// <summary>
/// Conciliação bancária: importa o OFX, sugere o par de cada linha (lançamento já existente, lançamento de imóvel pago
/// pela conta, ou categoria aprendida), confirma, cria, divide, transfere ou ignora. Também o fechamento do mês.
/// </summary>
public sealed class ConciliacaoService(
    IDbContextFactory<InovabensDb> fabrica, AuditService audit, FinanceiroEmpresaService financeiro, LancamentosFinanceirosService imoveis)
{
    /// <summary>Folga de datas entre o banco e o sistema: pago (já com data) e em aberto (pelo vencimento).</summary>
    public const int DiasPago = 3, DiasAberto = 7;

    public async Task<ResultadoImportacao> ImportarAsync(UsuarioAtual u, Guid contaId, string arquivo, byte[] conteudo)
    {
        u.Exigir(P.FinanceiroEmpresaEditar);
        if (conteudo.Length == 0) throw new RegraNegocioException("arquivo", "Arquivo vazio.");
        var ofx = OfxLeitor.Ler(conteudo);
        await using var db = await fabrica.CreateDbContextAsync();
        var conta = await db.ContasBancarias.FirstOrDefaultAsync(c => c.Id == contaId) ?? throw new NaoEncontradoException("Conta não encontrada.");

        // confere se o arquivo é mesmo desta conta (número da conta no OFX x cadastro), só avisa
        string? aviso = null;
        if (ofx.Cartao != conta.Cartao)
            aviso = ofx.Cartao ? "O arquivo é de cartão de crédito e a conta escolhida não é cartão." : "O arquivo é de conta bancária e a conta escolhida é cartão.";
        else if (Texto.Digitos(conta.Numero) is { Length: >= 4 } num && Texto.Digitos(ofx.Conta) is { Length: >= 4 } doArquivo
                 && !doArquivo.Contains(num) && !num.Contains(doArquivo))
            aviso = $"O número da conta no arquivo ({ofx.Conta}) é diferente do cadastrado ({conta.Numero}). Confira se escolheu a conta certa.";

        var fits = ofx.Transacoes.Select(t => t.FitId).ToList();
        var existentes = (await db.ExtratoItens.Where(i => i.ContaId == contaId && fits.Contains(i.FitId)).Select(i => i.FitId).ToListAsync()).ToHashSet();
        var candidatos = ofx.Transacoes.Where(t => !existentes.Contains(t.FitId)).ToList();

        // bancos que mudam o FITID entre downloads: a mesma transação (data, valor, descrição) já importada com outro
        // FITID não entra de novo; conta quantas iguais existem para não engolir duas transações idênticas legítimas
        var repetidos = ofx.Transacoes.Count - candidatos.Count;
        if (candidatos.Count > 0)
        {
            var de = candidatos.Min(t => t.Data);
            var ate = candidatos.Max(t => t.Data);
            var jaTem = await db.ExtratoItens.Where(i => i.ContaId == contaId && i.Data >= de && i.Data <= ate && !fits.Contains(i.FitId))
                .Select(i => new { i.Data, i.Valor, i.Descricao }).ToListAsync();
            var sobra = jaTem.GroupBy(i => (i.Data, i.Valor, i.Descricao)).ToDictionary(g => g.Key, g => g.Count());
            var filtrados = new List<OfxTransacao>();
            foreach (var t in candidatos)
            {
                var k = (t.Data, t.Valor, t.Descricao);
                if (sobra.TryGetValue(k, out var n) && n > 0) { sobra[k] = n - 1; repetidos++; continue; }
                filtrados.Add(t);
            }
            candidatos = filtrados;
        }

        var imp = new ExtratoImportacao
        {
            ContaId = contaId, Arquivo = Path.GetFileName(arquivo) is { Length: > 0 } a ? (a.Length > 200 ? a[..200] : a) : "extrato.ofx",
            De = ofx.De, Ate = ofx.Ate, SaldoBanco = ofx.Saldo, DataSaldo = ofx.DataSaldo,
            Itens = ofx.Transacoes.Count, Novos = candidatos.Count, ImportadoPor = u.Id,
        };
        db.ExtratoImportacoes.Add(imp);
        db.ExtratoItens.AddRange(candidatos.Select(t => new ExtratoItem
        {
            ContaId = contaId, ImportacaoId = imp.Id, FitId = t.FitId, Data = t.Data, Valor = t.Valor, Descricao = t.Descricao, Documento = t.Documento,
        }));
        audit.Registrar(db, u.Id, "extrato_importacoes", imp.Id, "IMPORTAR", depois: imp);
        await db.SaveChangesAsync();

        var sugeridos = (await ConciliacaoAsync(u, contaId)).Pendentes.Count(p => p.Sugestao.Tipo != TipoSugestao.NENHUMA);
        return new ResultadoImportacao(ofx.Transacoes.Count, candidatos.Count, repetidos, sugeridos, aviso);
    }

    public async Task<ConciliacaoConta> ConciliacaoAsync(UsuarioAtual u, Guid contaId, int diasConciliados = 60)
    {
        u.Exigir(P.FinanceiroEmpresaVer);
        var conta = (await financeiro.ContasAsync(u, incluirInativas: true)).FirstOrDefault(c => c.Conta.Id == contaId)
            ?? throw new NaoEncontradoException("Conta não encontrada.");
        await using var db = await fabrica.CreateDbContextAsync();
        var pendentes = await db.ExtratoItens.AsNoTracking()
            .Where(i => i.ContaId == contaId && i.ConciliadoEm == null && i.IgnoradoEm == null)
            .OrderBy(i => i.Data).ThenBy(i => i.Valor).ToListAsync();
        var sugestoes = await Sugerir(db, contaId, pendentes);

        var desde = DatasBR.Hoje().AddDays(-diasConciliados);
        var feitos = await db.ExtratoItens.AsNoTracking()
            .Where(i => i.ContaId == contaId && (i.ConciliadoEm != null || i.IgnoradoEm != null) && i.Data >= desde)
            .OrderByDescending(i => i.Data).Take(300).ToListAsync();
        var ids = feitos.Select(f => f.Id).ToList();
        var daEmpresa = await db.LancamentosEmpresa.AsNoTracking().Where(l => l.ExtratoItemId != null && ids.Contains(l.ExtratoItemId.Value) && l.ExcluidoEm == null)
            .Select(l => new { Item = l.ExtratoItemId!.Value, Texto = l.Categoria != null ? l.Categoria.Nome + " · " + l.Descricao : "⇄ " + l.Descricao }).ToListAsync();
        var doImovel = await db.LancamentosFinanceiros.AsNoTracking().Where(l => l.ExtratoItemId != null && ids.Contains(l.ExtratoItemId.Value) && l.ExcluidoEm == null)
            .Select(l => new { Item = l.ExtratoItemId!.Value, Texto = "Imóvel " + l.Imovel!.Codigo + " · " + l.Descricao }).ToListAsync();
        var textos = daEmpresa.Concat(doImovel).ToLookup(x => x.Item, x => x.Texto);

        var imp = await db.ExtratoImportacoes.AsNoTracking().Where(i => i.ContaId == contaId)
            .OrderByDescending(i => i.ImportadoEm).FirstOrDefaultAsync();
        var comSaldo = await db.ExtratoImportacoes.AsNoTracking().Where(i => i.ContaId == contaId && i.SaldoBanco != null && i.DataSaldo != null)
            .OrderByDescending(i => i.DataSaldo).ThenByDescending(i => i.ImportadoEm).FirstOrDefaultAsync();
        decimal? saldoNaData = comSaldo is null ? null : await financeiro.SaldoEmAsync(db, conta.Conta, comSaldo.DataSaldo!.Value.AddDays(1));

        return new ConciliacaoConta(conta,
            [.. pendentes.Select(i => new ItemConciliacao(i.Id, i.Data, i.Valor, i.Descricao, i.Documento,
                sugestoes.GetValueOrDefault(i.Id) ?? new SugestaoConciliacao(TipoSugestao.NENHUMA, null, "")))],
            [.. feitos.Select(i => new ItemConciliado(i.Id, i.Data, i.Valor, i.Descricao, i.IgnoradoEm != null, [.. textos[i.Id]]))],
            comSaldo?.SaldoBanco, comSaldo?.DataSaldo, saldoNaData, imp?.ImportadoEm);
    }

    /// <summary>
    /// Para cada linha pendente: 1º um lançamento existente com o mesmo valor e data próxima (pago nesta conta, ou em
    /// aberto vencendo perto); 2º o mesmo para lançamentos de imóvel; 3º a categoria aprendida pela descrição.
    /// Cada lançamento serve a uma linha só (a de data mais próxima).
    /// </summary>
    private static async Task<Dictionary<Guid, SugestaoConciliacao>> Sugerir(InovabensDb db, Guid contaId, List<ExtratoItem> itens)
    {
        var r = new Dictionary<Guid, SugestaoConciliacao>();
        if (itens.Count == 0) return r;
        var de = itens.Min(i => i.Data).AddDays(-DiasAberto);
        var ate = itens.Max(i => i.Data).AddDays(DiasAberto);
        var valores = itens.Select(i => i.Valor).Distinct().ToList();

        var daEmpresa = await db.LancamentosEmpresa.AsNoTracking()
            .Where(l => l.ExcluidoEm == null && l.ExtratoItemId == null && valores.Contains(l.Valor)
                && ((l.DataPagamento != null && l.ContaId == contaId && l.DataPagamento >= de && l.DataPagamento <= ate)
                    || (l.DataPagamento == null && (l.ContaId == null || l.ContaId == contaId) && l.Vencimento >= de && l.Vencimento <= ate)))
            .Select(l => new Candidato(l.Id, false, l.Valor, l.DataPagamento ?? l.Vencimento, l.DataPagamento == null,
                (l.Categoria != null ? l.Categoria.Nome : "Transferência") + " · " + l.Descricao)).ToListAsync();
        var doImovel = await db.LancamentosFinanceiros.AsNoTracking()
            .Where(l => l.ExcluidoEm == null && l.ExtratoItemId == null && valores.Contains(l.Valor)
                && ((l.Situacao == SituacaoLancamento.LIQUIDADO && l.ContaBancariaId == contaId && l.DataLiquidacao >= de && l.DataLiquidacao <= ate)
                    || (l.Situacao == SituacaoLancamento.ABERTO && (l.ContaBancariaId == null || l.ContaBancariaId == contaId) && l.Data >= de && l.Data <= ate)))
            .Select(l => new Candidato(l.Id, true, l.Valor, l.DataLiquidacao ?? l.Data, l.Situacao == SituacaoLancamento.ABERTO,
                "Imóvel " + l.Imovel!.Codigo + " · " + l.Descricao)).ToListAsync();

        // pares possíveis, do mais provável (pago, data igual) ao menos provável; guloso: cada lado usado uma vez
        var pares = (from i in itens
                     from c in daEmpresa.Concat(doImovel)
                     where c.Valor == i.Valor
                     let dias = Math.Abs(c.Data.DayNumber - i.Data.DayNumber)
                     where dias <= (c.EmAberto ? DiasAberto : DiasPago)
                     orderby c.EmAberto, dias, c.DoImovel
                     select (Item: i.Id, C: c)).ToList();
        var usados = new HashSet<Guid>();
        foreach (var (item, c) in pares)
        {
            if (r.ContainsKey(item) || !usados.Add(c.Id)) continue;
            r[item] = new SugestaoConciliacao(c.DoImovel ? TipoSugestao.LANCAMENTO_IMOVEL : TipoSugestao.LANCAMENTO, c.Id,
                c.Texto + (c.EmAberto ? " (em aberto: será baixado)" : ""), c.EmAberto);
        }

        // lançamentos criados juntos (aluguel dividido em terceiros + taxa, por exemplo) que somam o valor do banco
        var semPar = itens.Where(i => !r.ContainsKey(i.Id)).ToList();
        if (semPar.Count > 0)
        {
            var soltos = (await db.LancamentosEmpresa.AsNoTracking()
                .Where(l => l.ExcluidoEm == null && l.ExtratoItemId == null && l.TransferenciaId == null
                    && ((l.DataPagamento != null && l.ContaId == contaId && l.DataPagamento >= de && l.DataPagamento <= ate)
                        || (l.DataPagamento == null && (l.ContaId == null || l.ContaId == contaId) && l.Vencimento >= de && l.Vencimento <= ate)))
                .Select(l => new { l.Id, l.Valor, Data = l.DataPagamento ?? l.Vencimento, Aberto = l.DataPagamento == null, l.CriadoPor, l.CriadoEm, l.Descricao })
                .ToListAsync()).Where(l => !usados.Contains(l.Id)).ToList();
            // lote = mesmo autor, mesma data e criados com menos de 2 s de diferença
            var lotes = new List<List<Guid>>();
            foreach (var g in soltos.GroupBy(l => (l.Data, l.Aberto, l.CriadoPor)))
            {
                var atual = new List<(Guid Id, DateTime Em)>();
                foreach (var l in g.OrderBy(x => x.CriadoEm))
                {
                    if (atual.Count > 0 && (l.CriadoEm - atual[^1].Em).TotalSeconds > 2)
                    {
                        if (atual.Count > 1) lotes.Add([.. atual.Select(a => a.Id)]);
                        atual.Clear();
                    }
                    atual.Add((l.Id, l.CriadoEm));
                }
                if (atual.Count > 1) lotes.Add([.. atual.Select(a => a.Id)]);
            }
            var porId = soltos.ToDictionary(l => l.Id);
            foreach (var i in semPar)
            {
                var lote = lotes.FirstOrDefault(ids => !ids.Any(usados.Contains) && ids.Sum(id => porId[id].Valor) == i.Valor
                    && Math.Abs(porId[ids[0]].Data.DayNumber - i.Data.DayNumber) <= (porId[ids[0]].Aberto ? DiasAberto : DiasPago));
                if (lote is null) continue;
                foreach (var id in lote) usados.Add(id);
                var aberto = porId[lote[0]].Aberto;
                r[i.Id] = new SugestaoConciliacao(TipoSugestao.LANCAMENTO, lote[0],
                    $"{lote.Count} lançamentos juntos · {porId[lote[0]].Descricao}" + (aberto ? " (em aberto: serão baixados)" : ""), aberto, Grupo: lote);
            }
        }

        // regras aprendidas para o resto
        var resto = itens.Where(i => !r.ContainsKey(i.Id)).ToList();
        if (resto.Count == 0) return r;
        var chaves = resto.Select(i => OfxLeitor.Chave(i.Descricao)).Distinct().ToList();
        var regras = await db.RegrasConciliacao.AsNoTracking().Where(g => chaves.Contains(g.Chave))
            .Join(db.CategoriasEmpresa.Where(c => c.Ativa), g => g.CategoriaId, c => c.Id, (g, c) => new { g.Chave, g.Tipo, g.CategoriaId, c.Nome, g.PessoaNome })
            .ToListAsync();
        foreach (var i in resto)
        {
            var tipo = i.Valor > 0 ? TipoCategoriaEmpresa.ENTRADA : TipoCategoriaEmpresa.SAIDA;
            var chave = OfxLeitor.Chave(i.Descricao);
            if (regras.FirstOrDefault(g => g.Chave == chave && g.Tipo == tipo) is { } g)
                r[i.Id] = new SugestaoConciliacao(TipoSugestao.REGRA, g.CategoriaId, $"Criar em {g.Nome}", PessoaNome: g.PessoaNome);
        }
        return r;
    }


    public sealed record Candidato(Guid Id, bool DoImovel, decimal Valor, DateOnly Data, bool EmAberto, string Texto);

    /// <summary>Busca manual: lançamentos ainda não conciliados com o mesmo valor (qualquer data), mais próximos primeiro.</summary>
    public async Task<IReadOnlyList<Candidato>> CandidatosAsync(UsuarioAtual u, Guid itemId)
    {
        u.Exigir(P.FinanceiroEmpresaVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var item = await Pendente(db, itemId);
        var daEmpresa = await db.LancamentosEmpresa.AsNoTracking()
            .Where(l => l.ExcluidoEm == null && l.ExtratoItemId == null && l.Valor == item.Valor
                && (l.DataPagamento == null ? (l.ContaId == null || l.ContaId == item.ContaId) : l.ContaId == item.ContaId))
            .Select(l => new Candidato(l.Id, false, l.Valor, l.DataPagamento ?? l.Vencimento, l.DataPagamento == null,
                (l.Categoria != null ? l.Categoria.Nome : "Transferência") + " · " + l.Descricao)).ToListAsync();
        var doImovel = await db.LancamentosFinanceiros.AsNoTracking()
            .Where(l => l.ExcluidoEm == null && l.ExtratoItemId == null && l.Valor == item.Valor
                && ((l.Situacao == SituacaoLancamento.LIQUIDADO && (l.ContaBancariaId == null || l.ContaBancariaId == item.ContaId))
                    || (l.Situacao == SituacaoLancamento.ABERTO && (l.ContaBancariaId == null || l.ContaBancariaId == item.ContaId))))
            .Select(l => new Candidato(l.Id, true, l.Valor, l.DataLiquidacao ?? l.Data, l.Situacao == SituacaoLancamento.ABERTO,
                "Imóvel " + l.Imovel!.Codigo + " · " + l.Descricao)).ToListAsync();
        return [.. daEmpresa.Concat(doImovel).OrderBy(c => Math.Abs(c.Data.DayNumber - item.Data.DayNumber)).Take(30)];
    }

    /// <summary>Confirma a linha com um lançamento existente da imobiliária (em aberto: baixa na data e conta do banco).</summary>
    public Task ConciliarAsync(UsuarioAtual u, Guid itemId, Guid lancamentoId) => ConciliarAsync(u, itemId, [lancamentoId]);

    /// <summary>Confirma a linha com um ou mais lançamentos da imobiliária que, somados, dão o valor do banco.</summary>
    public async Task ConciliarAsync(UsuarioAtual u, Guid itemId, IReadOnlyList<Guid> lancamentoIds)
    {
        u.Exigir(P.FinanceiroEmpresaEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var item = await Pendente(db, itemId);
        var ids = lancamentoIds.Distinct().ToList();
        var lancs = await db.LancamentosEmpresa.Where(x => ids.Contains(x.Id) && x.ExcluidoEm == null).ToListAsync();
        if (lancs.Count != ids.Count || ids.Count == 0) throw new NaoEncontradoException("Lançamento não encontrado.");
        if (lancs.Any(l => l.ExtratoItemId is not null)) throw new RegraNegocioException("", "Este lançamento já está conciliado com outra linha do extrato.");
        if (lancs.Sum(l => l.Valor) != item.Valor) throw new RegraNegocioException("", "O valor do lançamento é diferente do extrato. Use \"Criar\" para dividir.");
        if (lancs.Any(l => l.DataPagamento is not null && l.ContaId != item.ContaId)) throw new RegraNegocioException("", "Este lançamento foi pago por outra conta.");
        foreach (var l in lancs)
        {
            await MesesFechados.Garantir(db, item.Data, l.DataPagamento, l.Competencia);
            var antes = l.Copia();
            l.DataPagamento ??= item.Data;
            l.ContaId = item.ContaId;
            l.ExtratoItemId = item.Id;
            l.AtualizadoPor = u.Id;
            l.AtualizadoEm = DateTime.UtcNow;
            audit.Registrar(db, u.Id, "lancamentos_empresa", l.Id, "CONCILIAR", antes, l);
        }
        Marcar(item, u);
        await db.SaveChangesAsync();
    }

    /// <summary>Confirma a linha com um lançamento de imóvel (em aberto: liquida pela data e valor do banco).</summary>
    public async Task ConciliarImovelAsync(UsuarioAtual u, Guid itemId, Guid lancamentoId)
    {
        u.Exigir(P.FinanceiroEmpresaEditar);
        ExtratoItem item;
        await using (var db0 = await fabrica.CreateDbContextAsync())
        {
            item = await Pendente(db0, itemId);
            var l0 = await db0.LancamentosFinanceiros.AsNoTracking().FirstOrDefaultAsync(x => x.Id == lancamentoId && x.ExcluidoEm == null)
                ?? throw new NaoEncontradoException("Lançamento do imóvel não encontrado.");
            if (l0.ExtratoItemId is not null) throw new RegraNegocioException("", "Este lançamento já está conciliado com outra linha do extrato.");
            if (l0.Valor != item.Valor) throw new RegraNegocioException("", "O valor do lançamento do imóvel é diferente do extrato.");
            // em aberto: a baixa passa pelo serviço do imóvel (permissão, acesso ao imóvel e alertas)
            if (l0.Situacao == SituacaoLancamento.ABERTO)
                await imoveis.LiquidarAsync(u, lancamentoId, item.Data, Math.Abs(item.Valor), item.ContaId);
            else if (l0.Situacao != SituacaoLancamento.LIQUIDADO)
                throw new RegraNegocioException("", "Só lançamento em aberto ou liquidado pode ser conciliado.");
        }
        await using var db = await fabrica.CreateDbContextAsync();
        item = await Pendente(db, itemId);
        var l = await db.LancamentosFinanceiros.FirstAsync(x => x.Id == lancamentoId);
        var antes = l.Copia();
        l.ContaBancariaId = item.ContaId;
        l.ExtratoItemId = item.Id;
        l.AtualizadoPor = u.Id;
        l.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "lancamentos_financeiros", l.Id, "CONCILIAR", antes, l);
        Marcar(item, u);
        await db.SaveChangesAsync();
    }

    /// <summary>Cria lançamento(s) já pagos na data/conta da linha; várias partes dividem o valor. Aprende a categoria.</summary>
    public async Task<IReadOnlyList<Guid>> CriarAsync(UsuarioAtual u, Guid itemId, CriarDoExtratoForm form)
    {
        u.Exigir(P.FinanceiroEmpresaEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var item = await Pendente(db, itemId);
        await MesesFechados.Garantir(db, item.Data);
        var total = form.Partes.Sum(p => p.Valor!.Value);
        if (total != Math.Abs(item.Valor))
            throw new RegraNegocioException(nameof(form.Partes), $"As partes somam {Texto.Moeda(total)}, mas o extrato é {Texto.Moeda(Math.Abs(item.Valor))}.");
        var tipo = item.Valor > 0 ? TipoCategoriaEmpresa.ENTRADA : TipoCategoriaEmpresa.SAIDA;
        var catIds = form.Partes.Select(p => p.CategoriaId!.Value).Distinct().ToList();
        var categorias = await db.CategoriasEmpresa.Where(c => catIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id);
        foreach (var id in catIds)
        {
            if (!categorias.TryGetValue(id, out var c) || !c.Ativa) throw new RegraNegocioException(nameof(form.Partes), "Categoria não encontrada ou inativa.");
            if (c.Tipo != tipo) throw new RegraNegocioException(nameof(form.Partes),
                tipo == TipoCategoriaEmpresa.ENTRADA ? "Entrada no banco: escolha categorias de entrada." : "Saída no banco: escolha categorias de saída.");
        }
        var imovelId = await FinanceiroEmpresaService.ImovelPeloCodigo(db, form.ImovelCodigo);
        var sinal = Math.Sign(item.Valor);
        var criados = form.Partes.Select(p => new LancamentoEmpresa
        {
            Descricao = Texto.Limpo(p.Descricao) ?? item.Descricao[..Math.Min(item.Descricao.Length, 200)],
            Valor = sinal * p.Valor!.Value, Vencimento = item.Data, DataPagamento = item.Data, Competencia = MesesFechados.Mes(item.Data),
            CategoriaId = p.CategoriaId, ContaId = item.ContaId, PessoaNome = Texto.Limpo(form.PessoaNome), ImovelId = imovelId,
            Documento = item.Documento, ExtratoItemId = item.Id, CriadoPor = u.Id, AtualizadoPor = u.Id,
        }).ToList();
        db.LancamentosEmpresa.AddRange(criados);
        foreach (var l in criados) audit.Registrar(db, u.Id, "lancamentos_empresa", l.Id, "CRIAR_DO_EXTRATO", depois: l);
        Marcar(item, u);
        // aprende só quando é uma categoria só (dividido é caso a caso)
        if (form.Aprender && catIds.Count == 1) await Aprender(db, item.Descricao, tipo, catIds[0], form.PessoaNome);
        await db.SaveChangesAsync();
        return [.. criados.Select(l => l.Id)];
    }

    /// <summary>A linha é dinheiro indo/vindo de outra conta da imobiliária (inclui pagar a fatura do cartão).</summary>
    public async Task TransferirAsync(UsuarioAtual u, Guid itemId, Guid outraContaId)
    {
        u.Exigir(P.FinanceiroEmpresaEditar);
        Guid conta, transferencia;
        DateOnly data;
        decimal valor;
        await using (var db0 = await fabrica.CreateDbContextAsync())
        {
            var item = await Pendente(db0, itemId);
            (conta, data, valor) = (item.ContaId, item.Data, item.Valor);
        }
        if (outraContaId == conta) throw new RegraNegocioException("", "Escolha a outra conta da transferência.");
        transferencia = await financeiro.TransferirAsync(u, new TransferenciaForm
        {
            OrigemId = valor < 0 ? conta : outraContaId, DestinoId = valor < 0 ? outraContaId : conta, Valor = Math.Abs(valor), Data = data,
        });
        await using var db = await fabrica.CreateDbContextAsync();
        var it = await Pendente(db, itemId);
        var lado = await db.LancamentosEmpresa.FirstAsync(l => l.TransferenciaId == transferencia && l.ContaId == conta);
        lado.ExtratoItemId = it.Id;
        Marcar(it, u);
        await db.SaveChangesAsync();
    }

    public async Task IgnorarAsync(UsuarioAtual u, Guid itemId)
    {
        u.Exigir(P.FinanceiroEmpresaEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var item = await Pendente(db, itemId);
        var antes = item.Copia();
        item.IgnoradoEm = DateTime.UtcNow;
        item.ConciliadoPor = u.Id;
        audit.Registrar(db, u.Id, "extrato_itens", item.Id, "IGNORAR", antes, item);
        await db.SaveChangesAsync();
    }

    /// <summary>Volta a linha para pendente. Os lançamentos continuam (só perdem o vínculo); exclua-os se estavam errados.</summary>
    public async Task DesfazerAsync(UsuarioAtual u, Guid itemId)
    {
        u.Exigir(P.FinanceiroEmpresaEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var item = await db.ExtratoItens.FirstOrDefaultAsync(i => i.Id == itemId) ?? throw new NaoEncontradoException("Linha do extrato não encontrada.");
        await MesesFechados.Garantir(db, item.Data);
        var antes = item.Copia();
        await db.LancamentosEmpresa.Where(l => l.ExtratoItemId == itemId).ExecuteUpdateAsync(s => s.SetProperty(l => l.ExtratoItemId, (Guid?)null));
        await db.LancamentosFinanceiros.Where(l => l.ExtratoItemId == itemId).ExecuteUpdateAsync(s => s.SetProperty(l => l.ExtratoItemId, (Guid?)null));
        item.ConciliadoEm = null;
        item.IgnoradoEm = null;
        item.ConciliadoPor = null;
        audit.Registrar(db, u.Id, "extrato_itens", item.Id, "DESFAZER_CONCILIACAO", antes, item);
        await db.SaveChangesAsync();
    }

    /// <summary>Aceita de uma vez todas as sugestões da conta. Devolve quantas linhas foram conciliadas.</summary>
    public async Task<int> AceitarSugestoesAsync(UsuarioAtual u, Guid contaId)
    {
        u.Exigir(P.FinanceiroEmpresaEditar);
        var c = await ConciliacaoAsync(u, contaId);
        var feitas = 0;
        foreach (var p in c.Pendentes.Where(p => p.Sugestao.Tipo != TipoSugestao.NENHUMA))
        {
            try
            {
                switch (p.Sugestao.Tipo)
                {
                    case TipoSugestao.LANCAMENTO: await ConciliarAsync(u, p.Id, p.Sugestao.Grupo ?? [p.Sugestao.Id!.Value]); break;
                    case TipoSugestao.LANCAMENTO_IMOVEL: await ConciliarImovelAsync(u, p.Id, p.Sugestao.Id!.Value); break;
                    case TipoSugestao.REGRA:
                        await CriarAsync(u, p.Id, new CriarDoExtratoForm
                        {
                            Partes = [new() { CategoriaId = p.Sugestao.Id, Valor = Math.Abs(p.Valor) }], PessoaNome = p.Sugestao.PessoaNome,
                        });
                        break;
                }
                feitas++;
            }
            // mês fechado, categoria inativada etc.: a linha fica pendente para tratar à mão
            catch (RegraNegocioException) { }
        }
        return feitas;
    }

    private static async Task Aprender(InovabensDb db, string descricao, TipoCategoriaEmpresa tipo, Guid categoriaId, string? pessoa)
    {
        var chave = OfxLeitor.Chave(descricao);
        if (chave.Length < 3) return;
        var r = await db.RegrasConciliacao.FirstOrDefaultAsync(g => g.Chave == chave && g.Tipo == tipo);
        if (r is null) db.RegrasConciliacao.Add(new RegraConciliacao { Chave = chave, Tipo = tipo, CategoriaId = categoriaId, PessoaNome = Texto.Limpo(pessoa) });
        else
        {
            r.CategoriaId = categoriaId;
            r.PessoaNome = Texto.Limpo(pessoa) ?? r.PessoaNome;
            r.Usos++;
            r.AtualizadoEm = DateTime.UtcNow;
        }
    }

    private static void Marcar(ExtratoItem item, UsuarioAtual u)
    {
        item.ConciliadoEm = DateTime.UtcNow;
        item.ConciliadoPor = u.Id;
    }

    private static async Task<ExtratoItem> Pendente(InovabensDb db, Guid id)
    {
        var item = await db.ExtratoItens.FirstOrDefaultAsync(i => i.Id == id) ?? throw new NaoEncontradoException("Linha do extrato não encontrada.");
        if (item.ConciliadoEm is not null || item.IgnoradoEm is not null)
            throw new RegraNegocioException("", "Esta linha do extrato já foi tratada.");
        return item;
    }

    /* ---------- Fechamento do mês ---------- */

    public async Task<ResumoFechamento> ResumoFechamentoAsync(UsuarioAtual u, DateOnly mes)
    {
        u.Exigir(P.FinanceiroEmpresaVer);
        mes = MesesFechados.Mes(mes);
        var fim = mes.AddMonths(1).AddDays(-1);
        await using var db = await fabrica.CreateDbContextAsync();
        var f = await db.FechamentosFinanceiros.AsNoTracking().FirstOrDefaultAsync(x => x.Mes == mes);
        var quem = f?.FechadoPor is { } id ? await db.Usuarios.Where(x => x.Id == id).Select(x => x.Nome).FirstOrDefaultAsync() : null;
        var pendentes = await db.ExtratoItens.CountAsync(i => i.Data >= mes && i.Data <= fim && i.ConciliadoEm == null && i.IgnoradoEm == null);
        var abertos = await db.LancamentosEmpresa.CountAsync(l => l.ExcluidoEm == null && l.DataPagamento == null && l.Vencimento >= mes && l.Vencimento <= fim);
        var saldos = new List<(string, decimal)>();
        // conta que começou depois do mês não tem saldo a conferir nele
        foreach (var c in await db.ContasBancarias.AsNoTracking().Where(c => c.Ativa && c.DataSaldoInicial <= fim).OrderBy(c => c.Nome).ToListAsync())
            saldos.Add((c.Nome, await financeiro.SaldoEmAsync(db, c, fim.AddDays(1))));
        return new ResumoFechamento(mes, f is not null, f?.FechadoEm, quem, pendentes, abertos, saldos);
    }

    public async Task<IReadOnlyList<DateOnly>> MesesFechadosAsync(UsuarioAtual u)
    {
        u.Exigir(P.FinanceiroEmpresaVer);
        await using var db = await fabrica.CreateDbContextAsync();
        return await db.FechamentosFinanceiros.AsNoTracking().OrderByDescending(f => f.Mes).Select(f => f.Mes).Take(36).ToListAsync();
    }

    public async Task FecharMesAsync(UsuarioAtual u, DateOnly mes)
    {
        u.Exigir(P.FinanceiroEmpresaFechar);
        mes = MesesFechados.Mes(mes);
        await using var db = await fabrica.CreateDbContextAsync();
        if (await db.FechamentosFinanceiros.AnyAsync(f => f.Mes == mes)) throw new RegraNegocioException("", "Este mês já está fechado.");
        var f = new FechamentoFinanceiro { Mes = mes, FechadoPor = u.Id };
        db.FechamentosFinanceiros.Add(f);
        audit.Registrar(db, u.Id, "fechamentos_financeiros", null, "FECHAR", depois: new { mes });
        await db.SaveChangesAsync();
    }

    public async Task ReabrirMesAsync(UsuarioAtual u, DateOnly mes)
    {
        u.Exigir(P.FinanceiroEmpresaFechar);
        mes = MesesFechados.Mes(mes);
        await using var db = await fabrica.CreateDbContextAsync();
        var f = await db.FechamentosFinanceiros.FirstOrDefaultAsync(x => x.Mes == mes) ?? throw new RegraNegocioException("", "Este mês não está fechado.");
        db.FechamentosFinanceiros.Remove(f);
        audit.Registrar(db, u.Id, "fechamentos_financeiros", null, "REABRIR", antes: new { mes });
        await db.SaveChangesAsync();
    }
}
