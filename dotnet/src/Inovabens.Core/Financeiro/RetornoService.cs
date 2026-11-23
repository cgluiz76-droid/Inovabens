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
        var im = await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);

        var aq = await db.Aquisicoes.AsNoTracking().SingleOrDefaultAsync(a => a.ImovelId == imovelId);
        var itens = aq is null ? [] : await db.AquisicaoItens.AsNoTracking().Where(i => i.AquisicaoId == aq.Id).ToListAsync();
        var lancs = await db.LancamentosFinanceiros.AsNoTracking()
            .Where(l => l.ImovelId == imovelId && l.ExcluidoEm == null && l.Situacao != SituacaoLancamento.IGNORADO)
            .Select(l => new LancamentoRetorno(l.DataLiquidacao ?? l.Data, l.Valor, l.Situacao, l.Descricao, l.Conta!.Nome))
            .ToListAsync();
        var fechamento = await db.FechamentosInvestimento.AsNoTracking().SingleOrDefaultAsync(f => f.ImovelId == imovelId);
        var fatores = await indices.FatoresAsync(indice);

        // empreendimento: o lote recebe o rateio do custo das matrículas acima; o empreendimento (e a remanescente
        // desmembrada) somam os custos e as vendas das matrículas abaixo
        var arvore = await ArvoreFinanceira.CarregarAsync(db, im, corte);
        var (adicionais, dataFinal) = arvore?.ParaRetorno(imovelId, corte) ?? ([], null);
        var dados = new DadosRetorno(imovelId, indice, corte, aq, itens, lancs, fechamento, fatores, IncluirLancamentos: true)
        {
            Adicionais = adicionais, DataFinal = dataFinal,
        };
        return new RetornoCenarios(CalculoRetorno.Calcular(dados), CalculoRetorno.Calcular(dados with { IncluirLancamentos = false }),
            fechamento?.IncluirLancamentos);
    }

    /// <summary>Painel do empreendimento (VGV, vendido, custo, lucro e recebimentos por fase). Null se o imóvel não foi desmembrado.</summary>
    public async Task<PainelEmpreendimento?> PainelAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.FinanceiroInvestimento);
        await using var db = await fabrica.CreateDbContextAsync();
        var im = await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        if (im.EmpreendimentoId is not null) return null;
        return (await ArvoreFinanceira.CarregarAsync(db, im, DatasBR.Hoje()))?.Painel();
    }

    /// <summary>Lugar do imóvel no empreendimento (para a aba Investimento explicar o que entra no retorno). Null = imóvel comum.</summary>
    public async Task<PapelNoEmpreendimento?> PapelAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.FinanceiroInvestimento);
        await using var db = await fabrica.CreateDbContextAsync();
        var im = await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var raizId = im.EmpreendimentoId ?? im.Id;
        if (!await db.Imoveis.AnyAsync(i => i.EmpreendimentoId == raizId && i.ExcluidoEm == null)) return null;
        var desmembrado = await db.Imoveis.AnyAsync(i => i.OrigemId == im.Id && i.ExcluidoEm == null);
        var codigoRaiz = raizId == im.Id ? im.Codigo : await db.Imoveis.Where(i => i.Id == raizId).Select(i => i.Codigo).SingleAsync();
        return new(codigoRaiz, raizId == im.Id, desmembrado);
    }
}

/// <summary>Lugar do imóvel na árvore do empreendimento: a raiz, uma remanescente desmembrada ou uma unidade que recebe rateio.</summary>
public sealed record PapelNoEmpreendimento(string EmpreendimentoCodigo, bool Raiz, bool Desmembrado);

/// <summary>Fechamento da operação (venda) na aba Investimento: um por imóvel; salvar marca o imóvel como Vendido.</summary>
public sealed class FechamentoService(IDbContextFactory<InovabensDb> fabrica, AuditService audit, ImoveisService imoveis,
    AquisicaoService aquisicoes)
{
    public async Task<FechamentoInvestimento?> ObterAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.FinanceiroInvestimento);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        return await db.FechamentosInvestimento.AsNoTracking().SingleOrDefaultAsync(f => f.ImovelId == imovelId);
    }

    /// <summary>Formulário do fechamento com as formas de recebimento (null = operação ainda não fechada).</summary>
    public async Task<FechamentoForm?> FormAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.FinanceiroInvestimento);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var f = await db.FechamentosInvestimento.AsNoTracking().SingleOrDefaultAsync(x => x.ImovelId == imovelId);
        if (f is null) return null;
        var rs = await db.FechamentoRecebimentos.AsNoTracking().Where(r => r.ImovelId == imovelId).ToListAsync();
        var recebidos = rs.Where(r => r.ImovelRecebidoId is not null).Select(r => r.ImovelRecebidoId!.Value).ToList();
        var codigos = await db.Imoveis.Where(i => recebidos.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Codigo);
        return FechamentoForm.De(f, rs, codigos);
    }

    public async Task SalvarAsync(UsuarioAtual u, Guid imovelId, FechamentoForm form)
    {
        u.Exigir(P.FinanceiroInvestimento);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var im = await Acesso.ImovelComAcesso(db, u, imovelId);
        // empreendimento desmembrado: cada lote tem a sua venda (o custo do empreendimento é rateado entre eles)
        if (await db.Imoveis.AnyAsync(i => i.OrigemId == imovelId && i.ExcluidoEm == null))
            throw new RegraNegocioException("Fechamento", $"{im.Codigo} foi desmembrado: registre a venda em cada lote.");
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
            Imoveis.EmpreendimentosService.ExigirVendaPermitida(im);
            audit.Registrar(db, u.Id, "imoveis", im.Id, "ATUALIZAR", antesImovel, im);
        }
        // o fechamento vai antes: as formas de recebimento apontam para ele (o EF não conhece essa chave e não ordena)
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.SaveChangesAsync();
        await SalvarRecebimentos(db, u, imovelId, form.Recebimentos);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>Grava as formas de recebimento: atualiza as existentes, inclui as novas e apaga as retiradas.</summary>
    private async Task SalvarRecebimentos(InovabensDb db, UsuarioAtual u, Guid imovelId, IReadOnlyList<RecebimentoForm> itens)
    {
        var atuais = await db.FechamentoRecebimentos.Where(r => r.ImovelId == imovelId).ToListAsync();
        foreach (var r in atuais.Where(r => !itens.Any(x => x.Id == r.Id)))
        {
            if (r.ImovelRecebidoId is not null)
                throw new RegraNegocioException(nameof(FechamentoForm.Recebimentos),
                    "A permuta com imóvel recebido já cadastrado não pode ser retirada. Ajuste o valor, se for o caso.");
            db.FechamentoRecebimentos.Remove(r);
            audit.Registrar(db, u.Id, "fechamento_recebimentos", r.Id, "EXCLUIR", antes: r);
        }
        for (var i = 0; i < itens.Count; i++)
        {
            var x = itens[i];
            var r = x.Id is { } id ? atuais.SingleOrDefault(a => a.Id == id) : null;
            if (r is null)
            {
                r = new FechamentoRecebimento { ImovelId = imovelId, Ordem = i };
                x.AplicarEm(r);
                db.FechamentoRecebimentos.Add(r);
                audit.Registrar(db, u.Id, "fechamento_recebimentos", r.Id, "CRIAR", depois: r);
                continue;
            }
            if (r.ImovelRecebidoId is not null && x.Tipo != TipoRecebimento.PERMUTA_IMOVEL)
                throw new RegraNegocioException(nameof(FechamentoForm.Recebimentos), "A permuta com imóvel recebido já cadastrado não pode mudar de tipo.");
            var antes = r.Copia();
            x.AplicarEm(r);
            r.Ordem = i;
            audit.Registrar(db, u.Id, "fechamento_recebimentos", r.Id, "ATUALIZAR", antes, r);
        }
    }

    /// <summary>
    /// Cadastra o imóvel recebido em permuta: imóvel novo (código próprio) com a aquisição já criada pelo valor combinado
    /// (a parte da venda paga por ele + a torna paga). Assim o lucro dele passa a contar desse valor.
    /// </summary>
    public async Task<Guid> RegistrarImovelRecebidoAsync(UsuarioAtual u, Guid imovelId, Guid recebimentoId, ImovelRecebidoForm form)
    {
        u.Exigir(P.FinanceiroInvestimento); u.Exigir(P.ImoveisCriar);
        form.Nome = Texto.Limpo(form.Nome) ?? "";
        Validacao.Validar(form);
        string origem;
        FechamentoRecebimento r;
        DateOnly data;
        await using (var db = await fabrica.CreateDbContextAsync())
        {
            var im = await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
            origem = im.Codigo;
            r = await db.FechamentoRecebimentos.AsNoTracking().SingleOrDefaultAsync(x => x.Id == recebimentoId && x.ImovelId == imovelId)
                ?? throw new NaoEncontradoException("Forma de recebimento não encontrada.");
            if (r.Tipo != TipoRecebimento.PERMUTA_IMOVEL) throw new RegraNegocioException("Recebimento", "Só a permuta com imóvel gera um imóvel novo.");
            if (r.ImovelRecebidoId is not null) throw new RegraNegocioException("Recebimento", "O imóvel recebido já foi cadastrado.");
            data = await db.FechamentosInvestimento.Where(f => f.ImovelId == imovelId).Select(f => f.Data).SingleAsync();
        }

        var novoId = await imoveis.CriarAsync(u, new ImovelForm
        {
            NomeDrive = form.Nome, Titulo = form.Nome, Tipo = form.Tipo, Cidade = Texto.Limpo(form.Cidade), Uf = Texto.Limpo(form.Uf)?.ToUpperInvariant(),
            DescricaoInterna = $"Recebido em permuta na venda de {origem}." + (r.Descricao is { } d ? $" {d}" : ""),
        });
        await aquisicoes.SalvarAsync(u, novoId, new AquisicaoForm
        {
            DataAquisicao = data, ValorPago = r.ValorBem ?? r.Valor,
            FormaPagamento = Corta(r.TornaPaga > 0 ? $"Permuta ({origem}) + torna {Texto.Moeda(r.TornaPaga)}" : $"Permuta ({origem})", 80),
            Observacao = $"Recebido na venda de {origem}: {Texto.Moeda(r.Valor)} em permuta" +
                (r.TornaPaga > 0 ? $" + {Texto.Moeda(r.TornaPaga)} de torna paga pela imobiliária." : ".") +
                " ITBI e escritura entram como itens desta aquisição.",
        });

        await using (var db = await fabrica.CreateDbContextAsync())
        {
            var linha = await db.FechamentoRecebimentos.SingleAsync(x => x.Id == recebimentoId);
            var antes = linha.Copia();
            linha.ImovelRecebidoId = novoId;
            audit.Registrar(db, u.Id, "fechamento_recebimentos", linha.Id, "ATUALIZAR", antes, linha);
            await db.SaveChangesAsync();
        }
        return novoId;
    }

    private static string Corta(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Desfaz o fechamento (a situação do imóvel não volta sozinha: ajuste na ficha).</summary>
    public async Task ExcluirAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.FinanceiroInvestimento);
        await using var db = await fabrica.CreateDbContextAsync();
        var im = await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var f = await db.FechamentosInvestimento.SingleOrDefaultAsync(x => x.ImovelId == imovelId)
            ?? throw new NaoEncontradoException("Fechamento não encontrado.");
        var recebido = await db.FechamentoRecebimentos.Where(r => r.ImovelId == imovelId && r.ImovelRecebidoId != null)
            .Select(r => r.ImovelRecebidoId).FirstOrDefaultAsync();
        if (recebido is not null)
            throw new RegraNegocioException("Fechamento",
                $"Esta venda tem imóvel recebido em permuta já cadastrado ({await db.Imoveis.Where(i => i.Id == recebido).Select(i => i.Codigo).SingleAsync()}). Ajuste o fechamento em vez de desfazê-lo.");
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
