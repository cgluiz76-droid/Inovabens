using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Agenda;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Crm;

public sealed record PropostaDetalhe(Proposta Dados, string ImovelCodigo, string ImovelTitulo, string? CriadoPorNome, string? DecididoPorNome)
{
    public bool EmAnalise => Dados.Status == StatusProposta.EM_ANALISE;

    /// <summary>Diferença para o preço anunciado no dia da proposta, em % (negativa = abaixo do anunciado).</summary>
    public decimal? DiferencaPct => PropostasService.DiferencaPct(Dados.ValorTotal, Dados.ValorAnunciado);
}

/// <summary>Imóvel oferecido em permuta, para pré-preencher o cadastro dele.</summary>
public sealed record PermutaParaCadastro(Guid ItemId, Guid LeadId, string LeadNome, int PropostaNumero, string ImovelCodigo,
    decimal Valor, string? Descricao, Guid? ImovelPermutaId);

public sealed class PropostaItemForm
{
    public TipoItemProposta Tipo { get; set; } = TipoItemProposta.ENTRADA;
    [Required(ErrorMessage = "Informe o valor."), Range(0.01, 9_999_999_999_999.99, ErrorMessage = "Informe um valor maior que zero.")]
    public decimal? Valor { get; set; }
    [StringLength(300, ErrorMessage = "No máximo 300 caracteres.")] public string? Descricao { get; set; }
    [StringLength(120, ErrorMessage = "No máximo 120 caracteres.")] public string? Instituicao { get; set; }
    [Range(1, 600, ErrorMessage = "De 1 a 600 parcelas.")] public int? Parcelas { get; set; }
    public DateOnly? DataPrevista { get; set; }
    public bool? CreditoAprovado { get; set; }
    public Guid? ImovelPermutaId { get; set; }

    public static PropostaItemForm De(PropostaItem i) => new()
    {
        Tipo = i.Tipo, Valor = i.Valor, Descricao = i.Descricao, Instituicao = i.Instituicao, Parcelas = i.Parcelas,
        DataPrevista = i.DataPrevista, CreditoAprovado = i.CreditoAprovado, ImovelPermutaId = i.ImovelPermutaId,
    };
}

/// <summary>
/// Proposta ou contraproposta. Registrar é uma ação no lead: pede o próximo passo (herdado de <see cref="ProximoPassoForm"/>).
/// Venda: o total é a soma das partes do pagamento. Locação: aluguel, garantia, prazo e reajuste.
/// </summary>
public sealed class PropostaForm : ProximoPassoForm
{
    public PropostaForm() => Limpar(TipoCompromisso.LIGACAO);

    /// <summary>Imóvel da proposta; também é o imóvel da atividade do próximo passo (esconde o de <see cref="ProximoPassoForm"/>).</summary>
    [Required(ErrorMessage = "Escolha o imóvel.")] public new Guid? ImovelId { get; set; }
    public AutorProposta Autor { get; set; } = AutorProposta.CLIENTE;
    public DateOnly? ValidadeEm { get; set; }
    [StringLength(4000, ErrorMessage = "No máximo 4000 caracteres.")] public string? Condicoes { get; set; }
    public List<PropostaItemForm> Itens { get; set; } = [];

    [Range(0.01, 9_999_999_999_999.99, ErrorMessage = "Informe um aluguel maior que zero.")] public decimal? Aluguel { get; set; }
    public GarantiaLocacao? Garantia { get; set; }
    [Range(1, 600, ErrorMessage = "Prazo de 1 a 600 meses.")] public int? PrazoMeses { get; set; }
    [StringLength(30)] public string? IndiceReajuste { get; set; }
    [Range(0, 120, ErrorMessage = "Carência de 0 a 120 meses.")] public int? CarenciaMeses { get; set; }

    public decimal Total(FinalidadeLead f) => f == FinalidadeLead.COMPRA ? Itens.Sum(i => i.Valor ?? 0) : Aluguel ?? 0;

    public override IEnumerable<ValidationResult> Validate(ValidationContext ctx)
    {
        foreach (var r in base.Validate(ctx)) yield return r;
        if (ValidadeEm is { } v && v < DatasBR.Hoje()) yield return new ValidationResult("A validade já passou", [nameof(ValidadeEm)]);
        if (Itens.Count > 30) yield return new ValidationResult("No máximo 30 partes no pagamento", [nameof(Itens)]);
    }

    /// <summary>Contraproposta a partir de uma proposta: mesmos valores, autor invertido.</summary>
    public static PropostaForm ContrapropostaDe(Proposta p) => new()
    {
        ImovelId = p.ImovelId, Autor = p.Autor == AutorProposta.CLIENTE ? AutorProposta.PROPRIETARIO : AutorProposta.CLIENTE,
        ValidadeEm = p.ValidadeEm is { } v && v >= DatasBR.Hoje() ? v : null, Condicoes = p.Condicoes,
        Itens = [.. p.Itens.OrderBy(i => i.Ordem).Select(PropostaItemForm.De)],
        Aluguel = p.Aluguel, Garantia = p.Garantia, PrazoMeses = p.PrazoMeses, IndiceReajuste = p.IndiceReajuste, CarenciaMeses = p.CarenciaMeses,
    };
}

/// <summary>Propostas e contrapropostas do CRM. Aceitar ou recusar exige <see cref="P.CrmPropostasDecidir"/> (administrador).</summary>
public sealed class PropostasService(IDbContextFactory<InovabensDb> fabrica, AuditService audit, GoogleService google)
{
    public static decimal? DiferencaPct(decimal total, decimal? anunciado) =>
        anunciado is { } a && a > 0 ? Math.Round((total - a) / a * 100, 1) : null;

    public static string Rotulo(TipoItemProposta t) => t switch
    {
        TipoItemProposta.ENTRADA => "Entrada à vista",
        TipoItemProposta.PARCELAS_PROPRIETARIO => "Parcelas com o proprietário",
        TipoItemProposta.FINANCIAMENTO => "Financiamento bancário",
        TipoItemProposta.FGTS => "FGTS",
        TipoItemProposta.CONSORCIO => "Consórcio",
        TipoItemProposta.PERMUTA_IMOVEL => "Permuta de imóvel",
        TipoItemProposta.PERMUTA_VEICULO => "Permuta de veículo",
        _ => "Outro",
    };

    public static string Rotulo(GarantiaLocacao g) => g switch
    {
        GarantiaLocacao.FIADOR => "Fiador",
        GarantiaLocacao.CAUCAO => "Caução",
        GarantiaLocacao.SEGURO_FIANCA => "Seguro-fiança",
        GarantiaLocacao.TITULO_CAPITALIZACAO => "Título de capitalização",
        _ => "Sem garantia",
    };

    public static string Rotulo(StatusProposta s) => s switch
    {
        StatusProposta.EM_ANALISE => "Em análise",
        StatusProposta.ACEITA => "Aceita",
        StatusProposta.RECUSADA => "Recusada",
        StatusProposta.SUBSTITUIDA => "Substituída",
        StatusProposta.CANCELADA => "Cancelada",
        _ => "Expirada",
    };

    private static IQueryable<Lead> Leads(InovabensDb db, UsuarioAtual u) => db.Leads.Where(l => l.ExcluidoEm == null).VisiveisPara(u);

    public async Task<IReadOnlyList<PropostaDetalhe>> DoLeadAsync(UsuarioAtual u, Guid leadId)
    {
        u.Exigir(P.CrmVer);
        await using var db = await fabrica.CreateDbContextAsync();
        if (!await Leads(db, u).AnyAsync(l => l.Id == leadId)) throw new NaoEncontradoException("Lead não encontrado.");
        return await Detalhar(db, db.Propostas.Where(p => p.LeadId == leadId));
    }

    public async Task<PropostaDetalhe> ObterAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.CrmVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var leadId = await db.Propostas.Where(p => p.Id == id).Select(p => (Guid?)p.LeadId).SingleOrDefaultAsync();
        if (leadId is null || !await Leads(db, u).AnyAsync(l => l.Id == leadId)) throw new NaoEncontradoException("Proposta não encontrada.");
        return (await Detalhar(db, db.Propostas.Where(p => p.Id == id))).Single();
    }

    private static async Task<List<PropostaDetalhe>> Detalhar(InovabensDb db, IQueryable<Proposta> q)
    {
        var linhas = await q.AsNoTracking().Include(p => p.Itens).OrderByDescending(p => p.CriadoEm)
            .Select(p => new
            {
                p,
                Imovel = db.Imoveis.Where(i => i.Id == p.ImovelId).Select(i => new { i.Codigo, i.Titulo }).First(),
                Criou = db.Usuarios.Where(x => x.Id == p.CriadoPor).Select(x => x.Nome).FirstOrDefault(),
                Decidiu = db.Usuarios.Where(x => x.Id == p.DecididoPor).Select(x => x.Nome).FirstOrDefault(),
            })
            .ToListAsync();
        foreach (var l in linhas) l.p.Itens = [.. l.p.Itens.OrderBy(i => i.Ordem)];
        return [.. linhas.Select(l => new PropostaDetalhe(l.p, l.Imovel.Codigo, l.Imovel.Titulo, l.Criou, l.Decidiu))];
    }

    private static string Resumo(Proposta p, string codigo, bool contra)
    {
        var dif = DiferencaPct(p.ValorTotal, p.ValorAnunciado) is { } d ? $" ({(d > 0 ? "+" : "")}{d.ToString("0.#", Texto.PtBr)}% do anunciado)" : "";
        var valor = p.Finalidade == FinalidadeLead.LOCACAO ? $"aluguel de {Texto.Moeda(p.ValorTotal)}" : Texto.Moeda(p.ValorTotal);
        var quem = p.Autor == AutorProposta.CLIENTE ? "do cliente" : "do proprietário";
        return $"{(contra ? "Contraproposta" : "Proposta")} nº {p.Numero} {quem}: {valor}{dif} · {codigo}";
    }

    /// <summary>
    /// Registra uma proposta. Se já há uma em análise para o mesmo imóvel, esta é a contraproposta: a anterior fica
    /// substituída. O imóvel entra nos interesses do lead, se ainda não estiver.
    /// </summary>
    public async Task<Guid> CriarAsync(UsuarioAtual u, Guid leadId, PropostaForm f)
    {
        u.Exigir(P.CrmEditar);
        Validacao.Validar(f);
        foreach (var i in f.Itens) Validacao.Validar(i);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await Leads(db, u).Include(x => x.Estagio).SingleOrDefaultAsync(x => x.Id == leadId)
            ?? throw new NaoEncontradoException("Lead não encontrado.");
        if (l.Estagio!.Tipo == TipoEstagio.PERDIDO) throw new RegraNegocioException("_", "Lead perdido não recebe proposta");
        if (l.Finalidade == FinalidadeLead.CAPTACAO) throw new RegraNegocioException("_", "Lead de captação não recebe proposta de compra ou aluguel");
        var venda = l.Finalidade == FinalidadeLead.COMPRA;
        if (venda && f.Itens.Count == 0) throw new RegraNegocioException(nameof(f.Itens), "Informe ao menos uma forma de pagamento");
        if (!venda && f.Aluguel is null) throw new RegraNegocioException(nameof(f.Aluguel), "Informe o valor do aluguel");

        var imovelId = f.ImovelId!.Value;
        var noInteresse = await db.LeadImoveisInteresse.AnyAsync(i => i.LeadId == leadId && i.ImovelId == imovelId);
        var im = await db.Imoveis.AsNoTracking().Where(i => i.Id == imovelId && i.ExcluidoEm == null)
            .Where(i => noInteresse || !u.EscopoProprio || i.CorretorResponsavelId == u.Id)
            .Select(i => new { i.Codigo, i.Situacao, i.PrecoVenda, i.PrecoLocacao }).SingleOrDefaultAsync()
            ?? throw new NaoEncontradoException("Imóvel não encontrado.");
        if (!noInteresse && CrmService.SituacoesSemInteresse.Contains(im.Situacao))
            throw new RegraNegocioException(nameof(f.ImovelId), "Imóvel vendido ou arquivado não recebe proposta");

        await using var tx = await db.Database.BeginTransactionAsync();
        var anterior = await db.Propostas.Where(p => p.LeadId == leadId && p.ImovelId == imovelId && p.Status == StatusProposta.EM_ANALISE)
            .Select(p => (Guid?)p.Id).SingleOrDefaultAsync();
        if (anterior is { } ant)
            await db.Propostas.Where(p => p.Id == ant).ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, StatusProposta.SUBSTITUIDA).SetProperty(p => p.AtualizadoEm, DateTime.UtcNow));
        var numero = (await db.Propostas.Where(p => p.LeadId == leadId && p.ImovelId == imovelId).MaxAsync(p => (int?)p.Numero) ?? 0) + 1;

        var p = new Proposta
        {
            Id = Guid.NewGuid(), LeadId = leadId, ImovelId = imovelId, Numero = numero, AnteriorId = anterior, Autor = f.Autor,
            Finalidade = l.Finalidade, ValorAnunciado = venda ? im.PrecoVenda : im.PrecoLocacao, ValorTotal = f.Total(l.Finalidade),
            ValidadeEm = f.ValidadeEm, Condicoes = Texto.Limpo(f.Condicoes), CriadoPor = u.Id,
        };
        if (venda)
            p.Itens = [.. f.Itens.Select((i, n) => new PropostaItem
            {
                Id = Guid.NewGuid(), PropostaId = p.Id, Ordem = n, Tipo = i.Tipo, Valor = i.Valor!.Value, Descricao = Texto.Limpo(i.Descricao),
                Instituicao = Texto.Limpo(i.Instituicao), Parcelas = i.Parcelas, DataPrevista = i.DataPrevista, CreditoAprovado = i.CreditoAprovado,
                ImovelPermutaId = i.Tipo == TipoItemProposta.PERMUTA_IMOVEL ? i.ImovelPermutaId : null,
            })];
        else
            (p.Aluguel, p.Garantia, p.PrazoMeses, p.IndiceReajuste, p.CarenciaMeses) =
                (f.Aluguel, f.Garantia, f.PrazoMeses, Texto.Limpo(f.IndiceReajuste), f.CarenciaMeses);
        db.Propostas.Add(p);
        if (!noInteresse) db.LeadImoveisInteresse.Add(new LeadImovelInteresse { LeadId = leadId, ImovelId = imovelId });

        var acao = new LeadInteracao { LeadId = leadId, UsuarioId = u.Id, Canal = CanalInteracao.OUTRO, Resumo = Resumo(p, im.Codigo, anterior is not null), PropostaId = p.Id };
        ((ProximoPassoForm)f).ImovelId = imovelId;
        var proximo = await CrmService.ProximoPasso(db, u, l, f, acao);
        db.LeadInteracoes.Add(acao);
        l.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "propostas", p.Id, "CRIAR", depois: new { p.LeadId, p.ImovelId, p.Numero, p.Autor, p.ValorTotal, p.AnteriorId });
        await db.SaveChangesAsync();
        if (!noInteresse) await WhatsappService.ResolverAlertasDoLead(db, leadId);
        await db.Alertas.Where(a => a.LeadId == leadId && a.Tipo == TipoAlerta.PROPOSTA_EXPIRADA && a.ResolvidoEm == null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ResolvidoEm, DateTime.UtcNow));
        await tx.CommitAsync();
        if (proximo is { } c) google.Empurrar(c);
        return p.Id;
    }

    /// <summary>
    /// REGRA: só o administrador aceita ou recusa; só proposta em análise; recusa exige motivo. Aceitar leva o lead para a
    /// última etapa aberta antes do ganho (Documentação/Fechamento). As duas pedem o próximo passo.
    /// </summary>
    public async Task DecidirAsync(UsuarioAtual u, Guid id, bool aceitar, string? motivo, ProximoPassoForm proximo)
    {
        u.Exigir(P.CrmPropostasDecidir);
        var m = Texto.Limpo(motivo);
        if (!aceitar && m is null) throw new RegraNegocioException(nameof(motivo), "Informe o motivo da recusa");
        await Encerrar(u, id, aceitar ? StatusProposta.ACEITA : StatusProposta.RECUSADA, m, proximo, moverParaFechamento: aceitar);
    }

    /// <summary>Retira a proposta (o cliente desistiu dela, valores errados…). Qualquer um que edita o CRM.</summary>
    /// <summary>
    /// Exclui a proposta e os itens dela (só quem tem crm:excluir). A proposta completa fica guardada na auditoria;
    /// as ações do lead que citam a proposta continuam na linha do tempo.
    /// </summary>
    public async Task ExcluirAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.CrmExcluir);
        await using var db = await fabrica.CreateDbContextAsync();
        var p = await db.Propostas.Include(x => x.Itens).SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Proposta não encontrada.");
        var nome = await Leads(db, u).Where(l => l.Id == p.LeadId).Select(l => l.Pessoa!.NomeRazao).SingleOrDefaultAsync()
            ?? throw new NaoEncontradoException("Proposta não encontrada.");
        var codigo = await db.Imoveis.Where(i => i.Id == p.ImovelId).Select(i => i.Codigo).SingleOrDefaultAsync();
        audit.RegistrarExclusao(db, u.Id, "propostas", id,
            $"Proposta nº {p.Numero} ({codigo}, {Texto.Moeda(p.ValorTotal)}, {Rotulo(p.Status).ToLowerInvariant()}) do lead de {nome}", p);
        db.Propostas.Remove(p);
        await db.SaveChangesAsync();
    }

    public async Task CancelarAsync(UsuarioAtual u, Guid id, string? motivo, ProximoPassoForm proximo)
    {
        u.Exigir(P.CrmEditar);
        var m = Texto.Limpo(motivo) ?? throw new RegraNegocioException(nameof(motivo), "Informe o motivo do cancelamento");
        await Encerrar(u, id, StatusProposta.CANCELADA, m, proximo, moverParaFechamento: false);
    }

    private async Task Encerrar(UsuarioAtual u, Guid id, StatusProposta status, string? motivo, ProximoPassoForm proximo, bool moverParaFechamento)
    {
        if (motivo is { Length: > 500 }) throw new RegraNegocioException(nameof(motivo), "No máximo 500 caracteres");
        Validacao.Validar(proximo);
        await using var db = await fabrica.CreateDbContextAsync();
        var p = await db.Propostas.SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Proposta não encontrada.");
        var l = await Leads(db, u).Include(x => x.Estagio).SingleOrDefaultAsync(x => x.Id == p.LeadId)
            ?? throw new NaoEncontradoException("Proposta não encontrada.");
        if (p.Status != StatusProposta.EM_ANALISE) throw new RegraNegocioException("_", "Só proposta em análise pode ser aceita, recusada ou cancelada");
        var codigo = await db.Imoveis.Where(i => i.Id == p.ImovelId).Select(i => i.Codigo).SingleAsync();

        await using var tx = await db.Database.BeginTransactionAsync();
        var antes = p.Copia();
        (p.Status, p.MotivoDecisao, p.DecididoPor, p.DecididoEm, p.AtualizadoEm) = (status, motivo, u.Id, DateTime.UtcNow, DateTime.UtcNow);
        audit.Registrar(db, u.Id, "propostas", id, "ATUALIZAR", new { status = antes.Status.ToString() }, new { status = status.ToString(), motivo });

        var verbo = status switch { StatusProposta.ACEITA => "aceita", StatusProposta.RECUSADA => "recusada", _ => "cancelada" };
        var acao = new LeadInteracao
        {
            LeadId = l.Id, UsuarioId = u.Id, Canal = CanalInteracao.OUTRO, PropostaId = id,
            Resumo = $"Proposta nº {p.Numero} ({codigo}) {verbo}{(motivo is null ? "" : $": {motivo}")}",
        };
        if (moverParaFechamento) await MoverParaFechamento(db, u, l, acao);
        var c = await CrmService.ProximoPasso(db, u, l, proximo, acao);
        db.LeadInteracoes.Add(acao);
        l.AtualizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        if (c is { } cid) google.Empurrar(cid);
    }

    /// <summary>
    /// REGRA (rotina diária): proposta em análise com a validade vencida (antes de hoje) vira EXPIRADA, fica registrada no lead
    /// e avisa no sino o responsável do lead (ou quem registrou a proposta). Idempotente: só pega as que ainda estão em análise.
    /// </summary>
    public async Task<int> ExpirarVencidasAsync(CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var hoje = DatasBR.Hoje();
        var vencidas = await db.Propostas
            .Where(p => p.Status == StatusProposta.EM_ANALISE && p.ValidadeEm != null && p.ValidadeEm < hoje)
            .Join(db.Leads, p => p.LeadId, l => l.Id, (p, l) => new { p, l.ResponsavelId, l.ExcluidoEm })
            .Where(x => x.ExcluidoEm == null)
            .Select(x => new { x.p, x.ResponsavelId, Codigo = db.Imoveis.Where(i => i.Id == x.p.ImovelId).Select(i => i.Codigo).First() })
            .ToListAsync(ct);
        if (vencidas.Count == 0) return 0;
        var agora = DateTime.UtcNow;
        foreach (var v in vencidas)
        {
            var p = v.p;
            var validade = p.ValidadeEm!.Value.ToString("dd/MM/yyyy");
            (p.Status, p.MotivoDecisao, p.DecididoEm, p.AtualizadoEm) = (StatusProposta.EXPIRADA, $"Validade vencida em {validade}", agora, agora);
            var resumo = $"Proposta nº {p.Numero} ({v.Codigo}) expirou: validade até {validade}";
            db.LeadInteracoes.Add(new LeadInteracao { LeadId = p.LeadId, Canal = CanalInteracao.OUTRO, Resumo = resumo, PropostaId = p.Id });
            if ((v.ResponsavelId ?? p.CriadoPor) is { } quem)
                db.Alertas.Add(new Alerta { Id = Guid.NewGuid(), Tipo = TipoAlerta.PROPOSTA_EXPIRADA, UsuarioId = quem, LeadId = p.LeadId, Mensagem = resumo });
            audit.Registrar(db, null, "propostas", p.Id, "ATUALIZAR", new { status = "EM_ANALISE" }, new { status = "EXPIRADA" });
        }
        await db.SaveChangesAsync(ct);
        return vencidas.Count;
    }

    /// <summary>Dados do imóvel oferecido em permuta, para abrir o cadastro já preenchido.</summary>
    public async Task<PermutaParaCadastro> PermutaAsync(UsuarioAtual u, Guid itemId)
    {
        u.Exigir(P.CrmVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var r = await (from i in db.PropostaItens
                       join p in db.Propostas on i.PropostaId equals p.Id
                       join l in Leads(db, u) on p.LeadId equals l.Id
                       where i.Id == itemId && i.Tipo == TipoItemProposta.PERMUTA_IMOVEL
                       select new PermutaParaCadastro(i.Id, l.Id, l.Pessoa!.NomeRazao, p.Numero,
                           db.Imoveis.Where(x => x.Id == p.ImovelId).Select(x => x.Codigo).First(), i.Valor, i.Descricao, i.ImovelPermutaId))
            .SingleOrDefaultAsync();
        return r ?? throw new NaoEncontradoException("Permuta não encontrada.");
    }

    /// <summary>Liga o imóvel cadastrado à parte "permuta de imóvel" da proposta e registra no lead.</summary>
    public async Task VincularPermutaAsync(UsuarioAtual u, Guid itemId, Guid imovelId)
    {
        u.Exigir(P.CrmEditar);
        var dados = await PermutaAsync(u, itemId);
        if (dados.ImovelPermutaId is not null) throw new RegraNegocioException("_", "Esta permuta já tem imóvel cadastrado");
        await using var db = await fabrica.CreateDbContextAsync();
        var codigo = await db.Imoveis.Where(i => i.Id == imovelId && i.ExcluidoEm == null).Select(i => i.Codigo).SingleOrDefaultAsync()
            ?? throw new NaoEncontradoException("Imóvel não encontrado.");
        var item = await db.PropostaItens.SingleAsync(i => i.Id == itemId);
        item.ImovelPermutaId = imovelId;
        db.LeadInteracoes.Add(new LeadInteracao
        {
            LeadId = dados.LeadId, UsuarioId = u.Id, Canal = CanalInteracao.OUTRO, PropostaId = item.PropostaId,
            Resumo = $"Imóvel da permuta (proposta nº {dados.PropostaNumero}) cadastrado: {codigo}",
        });
        audit.Registrar(db, u.Id, "proposta_itens", itemId, "ATUALIZAR", null, new { imovel_permuta_id = imovelId });
        await db.SaveChangesAsync();
    }

    /// <summary>Etapa de fechamento = a última aberta antes do ganho. Só avança (lead já depois dela não volta).</summary>
    private static async Task MoverParaFechamento(InovabensDb db, UsuarioAtual u, Lead l, LeadInteracao acao)
    {
        var funil = CrmService.FunilDa(l.Finalidade);
        var estagios = await db.FunilEstagios.Where(e => e.Funil == funil && e.Ativo).OrderBy(e => e.Ordem).ToListAsync();
        var ganho = estagios.FirstOrDefault(e => e.Tipo == TipoEstagio.GANHO);
        var alvo = estagios.LastOrDefault(e => e.Tipo == TipoEstagio.ABERTO && (ganho is null || e.Ordem < ganho.Ordem));
        if (alvo is null || l.Estagio!.Tipo != TipoEstagio.ABERTO || l.Estagio.Ordem >= alvo.Ordem) return;
        var anterior = l.Estagio;
        l.EstagioId = alvo.Id;
        l.Estagio = alvo;
        l.Ordem = (await db.Leads.Where(x => x.EstagioId == alvo.Id).MaxAsync(x => (int?)x.Ordem) ?? -1) + 1;
        db.LeadHistoricoEstagios.Add(new LeadHistoricoEstagio { LeadId = l.Id, EstagioAnteriorId = anterior.Id, EstagioNovoId = alvo.Id, UsuarioId = u.Id });
        acao.Resumo += $" · lead movido para {alvo.Nome}";
    }
}
