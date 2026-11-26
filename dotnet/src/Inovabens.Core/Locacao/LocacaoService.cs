using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Financeiro;
using Inovabens.Core.Relatorios;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Locacao;

/// <summary>
/// Locação, fases 1 e 2 (docs/plano-locacao.md): contrato com partes e garantia, faturas mensais, recebimento com multa
/// e juros, recibo discriminado, repasse ao proprietário e régua de cobrança (mensagens prontas para o WhatsApp).
/// Na administração de terceiros o dinheiro passa pela conta da imobiliária: o aluguel entra em "dinheiro de
/// terceiros" (fora da DRE), a taxa e a intermediação entram como receita e o repasse fica a pagar. No imóvel próprio
/// o recebimento vira receita no financeiro do imóvel.
/// </summary>
public sealed partial class LocacaoService(IDbContextFactory<InovabensDb> fabrica, AuditService audit)
{
    // a outra parte da classe (LocacaoFase3.cs) não enxerga os parâmetros do construtor primário
    private IDbContextFactory<InovabensDb> Fabrica => fabrica;
    private AuditService Audit => audit;

    public const string CategoriaAluguelTerceiros = "Aluguel recebido de inquilino";
    public const string CategoriaTaxaAdministracao = "Taxa de administração de aluguel";
    public const string CategoriaIntermediacao = "Taxa de intermediação de locação";
    public const string CategoriaRepasse = "Repasse ao proprietário";
    public const string ContaAluguelImovel = "Aluguel recebido";

    /// <summary>Faturas são geradas com esta antecedência do vencimento (a rotina diária e a ativação usam).</summary>
    public const int DiasAntecedenciaFatura = 30;

    /* ------------------------------ consultas ------------------------------ */

    public async Task<IReadOnlyList<ContratoLinha>> ListarAsync(UsuarioAtual u, StatusContratoLocacao? status = null, string? termo = null)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var hoje = DatasBR.Hoje();
        var q = db.ContratosLocacao.AsNoTracking().Where(c => db.Imoveis.VisiveisPara(u).Any(i => i.Id == c.ImovelId));
        if (status is { } s) q = q.Where(c => c.Status == s);
        if (Texto.Limpo(termo) is { } t)
        {
            var like = Texto.LikeBusca(t);
            q = q.Where(c => EF.Functions.Like(FuncoesBusca.Normalizar(c.Imovel!.Codigo), like, @"\")
                || EF.Functions.Like(FuncoesBusca.Normalizar(c.Imovel.Titulo), like, @"\")
                || c.Partes.Any(p => EF.Functions.Like(FuncoesBusca.Normalizar(p.Pessoa!.NomeRazao), like, @"\")));
        }
        var linhas = await q.OrderByDescending(c => c.Numero).Select(c => new
        {
            c.Id, c.Numero, c.ImovelId, c.Imovel!.Codigo, c.Imovel.Titulo, c.Status, c.Modalidade, c.ValorAluguel, c.Inicio, c.PrazoMeses,
            Partes = c.Partes.Select(p => new { p.Papel, p.Pessoa!.NomeRazao }).ToList(),
            Atrasadas = db.FaturasLocacao.Count(f => f.ContratoId == c.Id && f.Status == StatusFaturaLocacao.ABERTA && f.Vencimento < hoje),
        }).ToListAsync();
        return [.. linhas.Select(c => new ContratoLinha(c.Id, ContratoLocacao.CodigoDe(c.Numero), c.ImovelId, c.Codigo, c.Titulo, c.Status,
            c.Modalidade, c.ValorAluguel, c.Inicio, c.Inicio.AddMonths(c.PrazoMeses).AddDays(-1),
            Nomes(c.Partes.Where(p => p.Papel == PapelContratoLocacao.LOCATARIO).Select(p => p.NomeRazao)),
            Nomes(c.Partes.Where(p => p.Papel == PapelContratoLocacao.LOCADOR).Select(p => p.NomeRazao)), c.Atrasadas))];
    }

    public async Task<PainelLocacao> PainelAsync(UsuarioAtual u)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var hoje = DatasBR.Hoje();
        var visiveis = db.ContratosLocacao.Where(c => db.Imoveis.VisiveisPara(u).Any(i => i.Id == c.ImovelId));
        var ativos = await visiveis.CountAsync(c => c.Status == StatusContratoLocacao.ATIVO);
        var rascunhos = await visiveis.CountAsync(c => c.Status == StatusContratoLocacao.RASCUNHO);
        var aluguel = await visiveis.Where(c => c.Status == StatusContratoLocacao.ATIVO).SumAsync(c => (decimal?)c.ValorAluguel) ?? 0;
        var atrasadas = await Faturas(db, db.FaturasLocacao.Where(f => visiveis.Any(c => c.Id == f.ContratoId) && f.Status == StatusFaturaLocacao.ABERTA && f.Vencimento < hoje));
        var repasses = await db.RepassesLocacao.AsNoTracking()
            .Where(r => r.PagoEm == null && visiveis.Any(c => c.Id == r.ContratoId)
                && db.FaturaLocacaoPagamentos.Any(p => p.Id == r.PagamentoId && p.EstornadoEm == null))
            .Select(r => r.Liquido).ToListAsync();
        // vacância: imóvel para alugar, ainda na carteira, sem contrato ativo
        var vagos = await db.Imoveis.VisiveisPara(u).CountAsync(i => i.ExcluidoEm == null
            && (i.Finalidade == FinalidadeImovel.LOCACAO || i.Finalidade == FinalidadeImovel.AMBOS)
            && i.Situacao != SituacaoImovel.VENDIDO && i.Situacao != SituacaoImovel.ARQUIVADO
            && !db.ContratosLocacao.Any(c => c.ImovelId == i.Id && c.Status == StatusContratoLocacao.ATIVO));
        return new(ativos, rascunhos, aluguel, atrasadas.Count, atrasadas.Sum(f => f.Saldo), repasses.Count, repasses.Sum(), vagos);
    }

    public async Task<ContratoDetalhe> ObterAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await Contrato(db, u, id, rastrear: false);
        var im = await db.Imoveis.AsNoTracking().Where(i => i.Id == c.ImovelId)
            .Select(i => new { i.Codigo, i.Titulo, i.Logradouro, i.Numero, i.Complemento, i.Bairro, i.Cidade, i.Uf }).SingleAsync();
        var partes = await db.ContratoLocacaoPartes.AsNoTracking().Where(p => p.ContratoId == id)
            .OrderBy(p => p.Papel).ThenByDescending(p => p.Percentual).ThenBy(p => p.Pessoa!.NomeRazao)
            .Select(p => new ParteDetalhe(p.PessoaId, p.Pessoa!.NomeRazao, p.Pessoa.CpfCnpj,
                p.Pessoa.Whatsapp ?? p.Pessoa.Telefone ?? p.Pessoa.Email, p.Papel, p.Percentual))
            .ToListAsync();
        var faturas = await Faturas(db, db.FaturasLocacao.Where(f => f.ContratoId == id));
        var repasses = await Repasses(db, db.RepassesLocacao.Where(r => r.ContratoId == id));
        var retida = repasses.Where(r => !r.Estornado).Sum(r => r.Intermediacao);
        var historico = await db.ContratoLocacaoHistorico.AsNoTracking().Where(h => h.ContratoId == id)
            .OrderByDescending(h => h.Data).ThenByDescending(h => h.CriadoEm).ToListAsync();
        var endereco = string.Join(", ", new[] { im.Logradouro, im.Numero, im.Complemento, im.Bairro, im.Cidade, im.Uf }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        return new(c, im.Codigo, im.Titulo, endereco.Length == 0 ? null : endereco, partes, faturas, repasses, retida, historico);
    }

    /// <summary>Contrato ativo do imóvel (aba do imóvel e site: "locado").</summary>
    public async Task<ContratoLinha?> AtivoDoImovelAsync(UsuarioAtual u, Guid imovelId)
    {
        if (!u.Pode(P.LocacaoVer)) return null;
        var lista = await ListarAsync(u, StatusContratoLocacao.ATIVO);
        return lista.FirstOrDefault(c => c.ImovelId == imovelId);
    }

    /// <summary>Repasses ainda não pagos (de todos os contratos visíveis), pela data prevista.</summary>
    public async Task<IReadOnlyList<RepasseLinha>> RepassesAPagarAsync(UsuarioAtual u)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var visiveis = db.ContratosLocacao.Where(c => db.Imoveis.VisiveisPara(u).Any(i => i.Id == c.ImovelId));
        var lista = await Repasses(db, db.RepassesLocacao.Where(r => r.PagoEm == null && visiveis.Any(c => c.Id == r.ContratoId)));
        return [.. lista.Where(r => !r.Estornado).OrderBy(r => r.PrevistoPara)];
    }

    /// <summary>
    /// Régua de cobrança: faturas que vencem em até 3 dias (lembrete), hoje (vence hoje) ou já venceram (atraso). O
    /// sistema não envia nada sozinho: a equipe abre o WhatsApp com a mensagem pronta e envia.
    /// </summary>
    public async Task<IReadOnlyList<CobrancaLinha>> CobrancasAsync(UsuarioAtual u)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var hoje = DatasBR.Hoje();
        var limite = hoje.AddDays(3);
        var visiveis = db.ContratosLocacao.Where(c => db.Imoveis.VisiveisPara(u).Any(i => i.Id == c.ImovelId));
        var faturas = await Faturas(db, db.FaturasLocacao.Where(f => visiveis.Any(c => c.Id == f.ContratoId) && f.Status == StatusFaturaLocacao.ABERTA && f.Vencimento <= limite));
        var contratos = faturas.Select(f => f.Id).ToList();
        var dados = await db.FaturasLocacao.AsNoTracking().Where(f => contratos.Contains(f.Id)).Select(f => new
        {
            f.Id, f.ContratoId, f.Contrato!.Numero, f.Contrato.MultaAtrasoPct, f.Contrato.JurosMesPct, ImovelCodigo = f.Contrato.Imovel!.Codigo,
            Locatario = f.Contrato.Partes.Where(p => p.Papel == PapelContratoLocacao.LOCATARIO)
                .Select(p => new { p.Pessoa!.NomeRazao, Telefone = p.Pessoa.Whatsapp ?? p.Pessoa.Telefone }).FirstOrDefault(),
        }).ToDictionaryAsync(x => x.Id);

        var linhas = new List<CobrancaLinha>();
        foreach (var f in faturas.OrderBy(f => f.Vencimento))
        {
            var d = dados[f.Id];
            var nome = d.Locatario?.NomeRazao ?? "—";
            var primeiro = nome.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? nome;
            var dias = hoje.DayNumber - f.Vencimento.DayNumber;
            var mes = f.Competencia.ToString("MM/yyyy");
            string etapa, msg;
            if (dias < 0)
            {
                etapa = "Lembrete";
                msg = $"Olá, {primeiro}! Lembrete da Inova Bens: o aluguel de {mes} ({Texto.Moeda(f.Saldo)}) vence em {Texto.Data(f.Vencimento)}. Qualquer dúvida, estamos à disposição.";
            }
            else if (dias == 0)
            {
                etapa = "Vence hoje";
                msg = $"Olá, {primeiro}! O aluguel de {mes} ({Texto.Moeda(f.Saldo)}) vence hoje. Se já pagou, por favor envie o comprovante por aqui. Obrigado!";
            }
            else
            {
                etapa = dias <= 5 ? "Atraso" : dias <= 30 ? "Atraso (cobrança)" : "Atraso grave";
                var a = CalculoLocacao.Atraso(f.Saldo, f.Vencimento, hoje, d.MultaAtrasoPct, d.JurosMesPct);
                msg = $"Olá, {primeiro}. Não identificamos o pagamento do aluguel de {mes}, vencido em {Texto.Data(f.Vencimento)}. "
                    + $"Valor atualizado hoje: {Texto.Moeda(f.Saldo + a.Total)} (multa e juros do contrato). Podemos ajudar a regularizar?";
            }
            var link = Texto.LinkWhatsapp(d.Locatario?.Telefone) is { } w ? $"{w}?text={Uri.EscapeDataString(msg)}" : null;
            linhas.Add(new(f.Id, d.ContratoId, ContratoLocacao.CodigoDe(d.Numero), d.ImovelCodigo, nome, d.Locatario?.Telefone,
                f.Competencia, f.Vencimento, f.Saldo, Math.Max(0, dias), etapa, msg, link));
        }
        return linhas;
    }

    /// <summary>Contas da imobiliária (sem cartões) para registrar recebimentos e repasses.</summary>
    public async Task<IReadOnlyList<Opcao>> ContasBancariasAsync(UsuarioAtual u)
    {
        u.Exigir(P.LocacaoReceber);
        await using var db = await fabrica.CreateDbContextAsync();
        return await db.ContasBancarias.AsNoTracking().Where(c => c.Ativa && c.Tipo != TipoContaBancaria.CARTAO_CREDITO)
            .OrderBy(c => c.Ordem).ThenBy(c => c.Nome).Select(c => new Opcao(c.Id, c.Nome)).ToListAsync();
    }

    /* ------------------------------ contrato ------------------------------ */

    /// <summary>Formulário de um contrato novo para o imóvel: proprietários como locadores e valores do anúncio.</summary>
    public async Task<ContratoForm> NovoParaImovelAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var im = await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var donos = await db.ImovelProprietarios.AsNoTracking().Where(p => p.ImovelId == imovelId && p.FimEm == null).ToListAsync();
        var f = new ContratoForm
        {
            ImovelId = im.Id, ImovelCodigo = im.Codigo, ValorAluguel = im.PrecoLocacao,
            ValorCondominio = im.ValorCondominio ?? 0, Inicio = DatasBR.Hoje(),
        };
        f.Partes.AddRange(donos.Select(d => new ParteForm { PessoaId = d.PessoaId, Papel = PapelContratoLocacao.LOCADOR, Percentual = d.Percentual }));
        return f;
    }

    /// <summary>Rascunho a partir da proposta de locação aceita no CRM: imóvel, inquilino, aluguel, prazo e garantia.</summary>
    public async Task<ContratoForm> NovoDaPropostaAsync(UsuarioAtual u, Guid propostaId)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var p = await db.Propostas.AsNoTracking().SingleOrDefaultAsync(x => x.Id == propostaId)
            ?? throw new NaoEncontradoException("Proposta não encontrada.");
        if (p.Finalidade != FinalidadeLead.LOCACAO) throw new RegraNegocioException("_", "Esta proposta não é de locação.");
        if (p.Status != StatusProposta.ACEITA) throw new RegraNegocioException("_", "Só a proposta aceita vira contrato.");
        var f = await NovoParaImovelAsync(u, p.ImovelId);
        var inquilino = await db.Leads.Where(l => l.Id == p.LeadId).Select(l => l.PessoaId).SingleAsync();
        f.PropostaId = p.Id;
        f.ValorAluguel = p.Aluguel ?? p.ValorTotal;
        if (p.PrazoMeses is > 0 and var prazo) f.PrazoMeses = prazo;
        if (p.Garantia is { } g) f.Garantia = g;
        if (p.IndiceReajuste is { } ind) f.IndiceReajuste = ind.Contains("IPCA", StringComparison.OrdinalIgnoreCase) ? CodigoIndice.IPCA : CodigoIndice.IGPM;
        f.Partes.Add(new ParteForm { PessoaId = inquilino, Papel = PapelContratoLocacao.LOCATARIO });
        return f;
    }

    public async Task<ContratoForm> FormularioAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await Contrato(db, u, id, rastrear: false);
        var codigo = await db.Imoveis.Where(i => i.Id == c.ImovelId).Select(i => i.Codigo).SingleAsync();
        var partes = await db.ContratoLocacaoPartes.AsNoTracking().Where(p => p.ContratoId == id)
            .Select(p => new ParteForm { PessoaId = p.PessoaId, PessoaNome = p.Pessoa!.NomeRazao, Papel = p.Papel, Percentual = p.Percentual })
            .ToListAsync();
        return new ContratoForm
        {
            ImovelId = c.ImovelId, ImovelCodigo = codigo, PropostaId = c.PropostaId, Modalidade = c.Modalidade, Tipo = c.Tipo,
            Inicio = c.Inicio, PrazoMeses = c.PrazoMeses, DiaVencimento = c.DiaVencimento, PagamentoAntecipado = c.PagamentoAntecipado,
            ValorAluguel = c.ValorAluguel, ValorCondominio = c.ValorCondominio, ValorIptu = c.ValorIptu, ValorSeguroIncendio = c.ValorSeguroIncendio,
            IndiceReajuste = c.IndiceReajuste, Garantia = c.Garantia, GarantiaValor = c.GarantiaValor, GarantiaDetalhe = c.GarantiaDetalhe,
            TaxaAdministracaoPct = c.TaxaAdministracaoPct, IntermediacaoValor = c.IntermediacaoValor, MultaAtrasoPct = c.MultaAtrasoPct,
            JurosMesPct = c.JurosMesPct, MultaRescisoriaAlugueis = c.MultaRescisoriaAlugueis, DiasRepasse = c.DiasRepasse, LimiteManutencao = c.LimiteManutencao,
            ClausulaVigencia = c.ClausulaVigencia, Averbado = c.Averbado, Observacoes = c.Observacoes, Partes = partes,
            GarantiaVence = c.GarantiaVence, SeguroIncendioVence = c.SeguroIncendioVence,
        };
    }

    public async Task<Guid> CriarAsync(UsuarioAtual u, ContratoForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, form.ImovelId!.Value, rastrear: false);
        var c = new ContratoLocacao { ImovelId = form.ImovelId.Value, CriadoPor = u.Id, AtualizadoPor = u.Id };
        Aplicar(form, c);
        db.ContratosLocacao.Add(c);
        await SincronizarPartes(db, c, form.Partes);
        audit.Registrar(db, u.Id, "contratos_locacao", c.Id, "CRIAR", depois: c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    /// <summary>
    /// Rascunho: tudo editável. Ativo: imóvel, início e modalidade ficam travados (já há faturas); valores novos valem
    /// para as faturas em aberto, sem recebimento, de agora em diante.
    /// </summary>
    public async Task AtualizarAsync(UsuarioAtual u, Guid id, ContratoForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await Contrato(db, u, id);
        if (c.Status is StatusContratoLocacao.ENCERRADO or StatusContratoLocacao.CANCELADO)
            throw new RegraNegocioException("_", "Contrato encerrado ou cancelado não pode ser alterado.");
        var antes = c.Copia();
        if (c.Status == StatusContratoLocacao.ATIVO)
        {
            if (form.ImovelId != c.ImovelId || form.Inicio != c.Inicio || form.Modalidade != c.Modalidade)
                throw new RegraNegocioException("_", "Com o contrato ativo, imóvel, início e modalidade não mudam.");
        }
        else
        {
            await Acesso.ImovelComAcesso(db, u, form.ImovelId!.Value, rastrear: false);
            c.ImovelId = form.ImovelId.Value;
        }
        Aplicar(form, c);
        c.AtualizadoPor = u.Id;
        c.AtualizadoEm = DateTime.UtcNow;
        await SincronizarPartes(db, c, form.Partes);
        if (c.Status == StatusContratoLocacao.ATIVO)
        {
            ValidarParaAtivar(c, form.Partes);
            await RecalcularAbertas(db, c);
        }
        audit.Registrar(db, u.Id, "contratos_locacao", c.Id, "EDITAR", antes, c);
        await db.SaveChangesAsync();
    }

    /// <summary>Ativa o rascunho: confere as partes, marca o imóvel como locado e gera as primeiras faturas.</summary>
    public async Task AtivarAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var c = await Contrato(db, u, id);
        if (c.Status != StatusContratoLocacao.RASCUNHO) throw new RegraNegocioException("_", "Só o rascunho pode ser ativado.");
        var partes = await db.ContratoLocacaoPartes.Where(p => p.ContratoId == id).ToListAsync();
        ValidarParaAtivar(c, [.. partes.Select(p => new ParteForm { PessoaId = p.PessoaId, Papel = p.Papel, Percentual = p.Percentual })]);
        if (await db.ContratosLocacao.AnyAsync(x => x.ImovelId == c.ImovelId && x.Status == StatusContratoLocacao.ATIVO))
            throw new RegraNegocioException("_", "Este imóvel já tem um contrato de locação ativo.");
        var antes = c.Copia();
        c.Status = StatusContratoLocacao.ATIVO;
        c.AtivadoEm = DateTime.UtcNow;
        c.AtualizadoPor = u.Id;
        c.AtualizadoEm = DateTime.UtcNow;

        // só para alugar: sai da vitrine como LOCADO; à venda (VENDA/AMBOS) continua anunciado, com a marca de locado
        var im = await db.Imoveis.SingleAsync(i => i.Id == c.ImovelId);
        if (im.Finalidade == FinalidadeImovel.LOCACAO && im.Situacao != SituacaoImovel.LOCADO)
        {
            var antesIm = im.Situacao;
            im.Situacao = SituacaoImovel.LOCADO;
            audit.Registrar(db, u.Id, "imoveis", im.Id, "EDITAR", new { situacao = antesIm }, new { situacao = im.Situacao, contrato = c.Codigo });
        }
        await GarantirPapeis(db, partes);
        audit.Registrar(db, u.Id, "contratos_locacao", c.Id, "ATIVAR", antes, c);
        await db.SaveChangesAsync();
        await GerarFaturas(db, c, DatasBR.Hoje().AddDays(DiasAntecedenciaFatura));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>Rascunho descartado (sem faturas): fica como CANCELADO para o histórico.</summary>
    public async Task CancelarRascunhoAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await Contrato(db, u, id);
        if (c.Status != StatusContratoLocacao.RASCUNHO) throw new RegraNegocioException("_", "Só o rascunho pode ser cancelado.");
        var antes = c.Copia();
        c.Status = StatusContratoLocacao.CANCELADO;
        c.AtualizadoPor = u.Id;
        c.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "contratos_locacao", c.Id, "CANCELAR", antes, c);
        await db.SaveChangesAsync();
    }

    /// <summary>Encerramento por acordo, sem multa nem acerto (atalho da <see cref="RescindirAsync"/>).</summary>
    public Task EncerrarAsync(UsuarioAtual u, Guid id, DateOnly data, string? motivo) =>
        RescindirAsync(u, id, new RescisaoForm { EntregaChaves = data, Iniciativa = IniciativaRescisao.ACORDO, Motivo = motivo, DevolverChaves = false });

    /* ------------------------------ faturas ------------------------------ */

    /// <summary>Rotina diária: gera as faturas que vencem nos próximos <see cref="DiasAntecedenciaFatura"/> dias.</summary>
    public async Task<int> GerarFaturasPendentesAsync(CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var ate = DatasBR.Hoje().AddDays(DiasAntecedenciaFatura);
        var ativos = await db.ContratosLocacao.Where(c => c.Status == StatusContratoLocacao.ATIVO).ToListAsync(ct);
        var total = 0;
        foreach (var c in ativos) total += await GerarFaturas(db, c, ate);
        await db.SaveChangesAsync(ct);
        return total;
    }

    /// <summary>Gera agora as faturas do contrato até uma data (botão na tela, para adiantar a cobrança).</summary>
    public async Task<int> GerarFaturasAteAsync(UsuarioAtual u, Guid id, DateOnly ate)
    {
        u.Exigir(P.LocacaoReceber);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await Contrato(db, u, id, rastrear: false);
        if (c.Status != StatusContratoLocacao.ATIVO) throw new RegraNegocioException("_", "O contrato não está ativo.");
        var n = await GerarFaturas(db, c, ate);
        await db.SaveChangesAsync();
        return n;
    }

    public async Task AdicionarItemAsync(UsuarioAtual u, Guid faturaId, ItemAvulsoForm form)
    {
        u.Exigir(P.LocacaoReceber);
        Validacao.Validar(form);
        if (form.Tipo == TipoItemFatura.ALUGUEL) throw new RegraNegocioException(nameof(form.Tipo), "O aluguel do mês já está na fatura.");
        await using var db = await fabrica.CreateDbContextAsync();
        var f = await Fatura(db, u, faturaId);
        if (f.Status != StatusFaturaLocacao.ABERTA) throw new RegraNegocioException("_", "Só a fatura em aberto recebe itens.");
        var valor = form.Tipo == TipoItemFatura.DESCONTO ? -form.Valor!.Value : form.Valor!.Value;
        var total = f.Itens.Sum(i => i.Valor) + valor;
        if (total < Pago(f)) throw new RegraNegocioException(nameof(form.Valor), "O desconto deixaria a fatura menor do que o já recebido.");
        var item = new FaturaLocacaoItem
        {
            FaturaId = f.Id, Tipo = form.Tipo, Descricao = form.Descricao.Trim(), Valor = valor,
            Ordem = f.Itens.Count == 0 ? 1 : f.Itens.Max(i => i.Ordem) + 1,
        };
        db.FaturaLocacaoItens.Add(item);
        audit.Registrar(db, u.Id, "faturas_locacao", f.Id, "EDITAR", depois: new { item = item.Descricao, item.Valor, tipo = item.Tipo });
        await db.SaveChangesAsync();
        await AtualizarStatus(db, f.Id);
    }

    public async Task RemoverItemAsync(UsuarioAtual u, Guid faturaId, Guid itemId)
    {
        u.Exigir(P.LocacaoReceber);
        await using var db = await fabrica.CreateDbContextAsync();
        var f = await Fatura(db, u, faturaId);
        if (f.Status != StatusFaturaLocacao.ABERTA) throw new RegraNegocioException("_", "Só a fatura em aberto pode ser alterada.");
        var item = f.Itens.SingleOrDefault(i => i.Id == itemId) ?? throw new NaoEncontradoException("Item não encontrado.");
        if (item.Tipo == TipoItemFatura.ALUGUEL) throw new RegraNegocioException("_", "O aluguel do mês não sai da fatura (cancele a fatura, se for o caso).");
        if (f.Itens.Sum(i => i.Valor) - item.Valor < Pago(f)) throw new RegraNegocioException("_", "A fatura ficaria menor do que o já recebido.");
        db.FaturaLocacaoItens.Remove(item);
        audit.Registrar(db, u.Id, "faturas_locacao", f.Id, "EDITAR", antes: new { item = item.Descricao, item.Valor, tipo = item.Tipo });
        await db.SaveChangesAsync();
        await AtualizarStatus(db, f.Id);
    }

    public async Task CancelarFaturaAsync(UsuarioAtual u, Guid faturaId, string motivo)
    {
        u.Exigir(P.LocacaoReceber);
        if (Texto.Limpo(motivo) is not { } m) throw new RegraNegocioException("motivo", "Informe o motivo do cancelamento.");
        await using var db = await fabrica.CreateDbContextAsync();
        var f = await Fatura(db, u, faturaId);
        if (f.Status != StatusFaturaLocacao.ABERTA) throw new RegraNegocioException("_", "Só a fatura em aberto pode ser cancelada.");
        if (Pago(f) > 0) throw new RegraNegocioException("_", "A fatura tem recebimento: estorne antes de cancelar.");
        var antes = f.Copia();
        f.Status = StatusFaturaLocacao.CANCELADA;
        f.CanceladaEm = DateTime.UtcNow;
        f.MotivoCancelamento = m;
        audit.Registrar(db, u.Id, "faturas_locacao", f.Id, "CANCELAR", antes, f);
        await db.SaveChangesAsync();
    }

    /// <summary>Sugestão para a tela: saldo da fatura e multa/juros se receber na data informada.</summary>
    public async Task<(decimal Saldo, Acrescimos Atraso)> SimularRecebimentoAsync(UsuarioAtual u, Guid faturaId, DateOnly data)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var f = await Fatura(db, u, faturaId, rastrear: false);
        var saldo = Math.Max(0, f.Itens.Sum(i => i.Valor) - Pago(f));
        return (saldo, CalculoLocacao.Atraso(saldo, f.Vencimento, data, f.Contrato!.MultaAtrasoPct, f.Contrato.JurosMesPct));
    }

    /// <summary>
    /// Registra um recebimento (total ou parcial): grava o pagamento com o número do recibo, lança no financeiro e,
    /// na administração de terceiros, calcula os repasses de cada locador (a pagar em <c>dias_repasse</c> dias).
    /// </summary>
    public async Task<Guid> ReceberAsync(UsuarioAtual u, Guid faturaId, RecebimentoLocacaoForm form)
    {
        u.Exigir(P.LocacaoReceber);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var f = await Fatura(db, u, faturaId);
        await db.Database.ExecuteSqlRawAsync("select 1 from faturas_locacao where id = {0} for update", f.Id);
        var c = f.Contrato!;
        if (f.Status != StatusFaturaLocacao.ABERTA) throw new RegraNegocioException("_", "Esta fatura não está em aberto.");
        var saldo = f.Itens.Sum(i => i.Valor) - Pago(f);
        var data = form.Data!.Value;
        if (form.Principal > saldo) throw new RegraNegocioException(nameof(form.Principal), $"O saldo da fatura é {Texto.Moeda(saldo)}.");
        if (data > DatasBR.Hoje()) throw new RegraNegocioException(nameof(form.Data), "A data do recebimento não pode ser futura.");
        if (c.Modalidade == ModalidadeLocacao.ADMINISTRACAO && form.ContaBancariaId is null)
            throw new RegraNegocioException(nameof(form.ContaBancariaId), "Informe a conta da imobiliária onde o dinheiro entrou.");
        if (form.ContaBancariaId is { } cid && !await db.ContasBancarias.AnyAsync(x => x.Id == cid && x.Ativa && x.Tipo != TipoContaBancaria.CARTAO_CREDITO))
            throw new RegraNegocioException(nameof(form.ContaBancariaId), "Conta bancária inválida.");

        var pg = new FaturaLocacaoPagamento
        {
            FaturaId = f.Id, Data = data, Principal = form.Principal!.Value, Multa = form.Multa, Juros = form.Juros,
            Forma = Texto.Limpo(form.Forma), ContaBancariaId = form.ContaBancariaId, Observacao = Texto.Limpo(form.Observacao),
            RegistradoPor = u.Id,
        };
        db.FaturaLocacaoPagamentos.Add(pg);
        var locatario = await db.ContratoLocacaoPartes.Where(p => p.ContratoId == c.Id && p.Papel == PapelContratoLocacao.LOCATARIO)
            .Select(p => new { p.PessoaId, p.Pessoa!.NomeRazao }).FirstOrDefaultAsync();
        var referencia = $"{c.Codigo} · {f.Competencia:MM/yyyy}";

        if (c.Modalidade == ModalidadeLocacao.ADMINISTRACAO)
        {
            await MesesFechados.Garantir(db, data);
            var locadores = await db.ContratoLocacaoPartes.Where(p => p.ContratoId == c.Id && p.Papel == PapelContratoLocacao.LOCADOR)
                .OrderByDescending(p => p.Percentual).Select(p => new { p.PessoaId, p.Percentual, p.Pessoa!.NomeRazao }).ToListAsync();
            var retida = await db.RepassesLocacao.Where(r => r.ContratoId == c.Id
                && db.FaturaLocacaoPagamentos.Any(p => p.Id == r.PagamentoId && p.EstornadoEm == null)).SumAsync(r => (decimal?)r.Intermediacao) ?? 0;
            var calc = CalculoLocacao.Repasses([.. locadores.Select(l => (l.PessoaId, l.Percentual ?? 0))], f.Itens, pg.Principal, pg.Multa, pg.Juros,
                c.TaxaAdministracaoPct, c.IntermediacaoValor - retida);
            // conserto por conta do locador ainda não abatido: sai deste repasse (a imobiliária já pagou ou vai pagar o prestador)
            var manutencoes = await ManutencaoService.PendentesDeAbaterAsync(db, c.Id);
            calc = CalculoLocacao.AbaterManutencao(calc, manutencoes.Sum(m => m.Pendente));
            var abatido = calc.Sum(r => r.DescontoManutencao);
            foreach (var m in manutencoes)
            {
                if (abatido <= 0) break;
                var v = Math.Min(abatido, m.Pendente);
                db.ManutencaoAbatimentos.Add(new ManutencaoAbatimento { ChamadoId = m.ChamadoId, VistoriaId = m.VistoriaId, PagamentoId = pg.Id, Valor = v });
                abatido -= v;
            }
            var reembolso = CalculoLocacao.Reembolso(f.Itens, pg.Principal);
            var taxa = calc.Sum(r => r.Taxa);
            var interm = calc.Sum(r => r.Intermediacao);
            var mes = CalculoLocacao.MesDe(data);
            var conta = form.ContaBancariaId;

            LancamentoEmpresa Lanc(string categoria, Guid catId, decimal valor, DateOnly venc, DateOnly? pagoEm, Guid? pessoa, string? nome) => new()
            {
                Descricao = $"{categoria} — {referencia}", Valor = valor, Vencimento = venc, DataPagamento = pagoEm, Competencia = CalculoLocacao.MesDe(venc),
                CategoriaId = catId, ContaId = conta, PessoaId = pessoa, PessoaNome = nome, ImovelId = c.ImovelId,
                Documento = $"Recibo de aluguel", CriadoPor = u.Id, AtualizadoPor = u.Id,
            };
            var lancs = new List<LancamentoEmpresa>
            {
                Lanc(CategoriaAluguelTerceiros, await Categoria(db, CategoriaAluguelTerceiros, TipoCategoriaEmpresa.ENTRADA, GrupoCategoriaEmpresa.TERCEIROS),
                    pg.Total - taxa - interm - reembolso, data, data, locatario?.PessoaId, locatario?.NomeRazao),
            };
            if (reembolso > 0) lancs.Add(Lanc(ManutencaoService.CategoriaReembolsoInquilino,
                await Categoria(db, ManutencaoService.CategoriaReembolsoInquilino, TipoCategoriaEmpresa.ENTRADA, GrupoCategoriaEmpresa.TERCEIROS),
                reembolso, data, data, locatario?.PessoaId, locatario?.NomeRazao));
            if (taxa > 0) lancs.Add(Lanc(CategoriaTaxaAdministracao,
                await Categoria(db, CategoriaTaxaAdministracao, TipoCategoriaEmpresa.ENTRADA, GrupoCategoriaEmpresa.RECEITA), taxa, data, data, null, null));
            if (interm > 0) lancs.Add(Lanc(CategoriaIntermediacao,
                await Categoria(db, CategoriaIntermediacao, TipoCategoriaEmpresa.ENTRADA, GrupoCategoriaEmpresa.RECEITA), interm, data, data, null, null));
            var catRepasse = await Categoria(db, CategoriaRepasse, TipoCategoriaEmpresa.SAIDA, GrupoCategoriaEmpresa.TERCEIROS);
            var previsto = CalculoLocacao.DataRepasse(data, c.DiasRepasse);
            foreach (var r in calc)
            {
                var nome = locadores.First(l => l.PessoaId == r.PessoaId).NomeRazao;
                var lr = Lanc(CategoriaRepasse, catRepasse, -r.Liquido, previsto, null, r.PessoaId, nome);
                lancs.Add(lr);
                db.RepassesLocacao.Add(new RepasseLocacao
                {
                    PagamentoId = pg.Id, ContratoId = c.Id, PessoaId = r.PessoaId, Percentual = r.Percentual, Bruto = r.Bruto,
                    TaxaAdministracao = r.Taxa, Intermediacao = r.Intermediacao, Liquido = r.Liquido, DescontoManutencao = r.DescontoManutencao,
                    PrevistoPara = previsto,
                    LancamentoEmpresaId = lr.Id,
                });
            }
            lancs.RemoveAll(l => l.Valor == 0);
            db.LancamentosEmpresa.AddRange(lancs);
            foreach (var l in lancs) audit.Registrar(db, u.Id, "lancamentos_empresa", l.Id, "CRIAR", depois: l);
            pg.LancamentosEmpresaIds = [.. lancs.Select(l => l.Id)];
        }
        else
        {
            // imóvel próprio: receita direto no financeiro do imóvel (entra no retorno do investimento)
            var contaId = await db.ContasOrcamentarias.Where(x => x.Nome == ContaAluguelImovel && x.Tipo == TipoConta.RECEITA)
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync()
                ?? throw new RegraNegocioException("_", $"Crie a conta de receita \"{ContaAluguelImovel}\" no financeiro dos imóveis.");
            var l = new LancamentoFinanceiro
            {
                ImovelId = c.ImovelId, Situacao = SituacaoLancamento.LIQUIDADO, Data = f.Vencimento, DataLiquidacao = data, Valor = pg.Total,
                Descricao = $"Aluguel {referencia}", ContaId = contaId, PessoaId = locatario?.PessoaId, PessoaNome = locatario?.NomeRazao,
                ContaBancariaId = form.ContaBancariaId, Competencia = f.Competencia, CriadoPor = u.Id, AtualizadoPor = u.Id,
            };
            l.Numero = await LancamentosFinanceirosService.ProximoNumero(db, c.ImovelId);
            db.LancamentosFinanceiros.Add(l);
            audit.Registrar(db, u.Id, "lancamentos_financeiros", l.Id, "CRIAR", depois: l);
            pg.LancamentoImovelId = l.Id;
        }
        audit.Registrar(db, u.Id, "faturas_locacao", f.Id, "RECEBER", depois: pg);
        await db.SaveChangesAsync();
        await AtualizarStatus(db, f.Id);
        await tx.CommitAsync();
        return pg.Id;
    }

    /// <summary>Desfaz um recebimento (lançado errado): exclui os lançamentos e os repasses, se nenhum repasse foi pago.</summary>
    public async Task EstornarAsync(UsuarioAtual u, Guid pagamentoId)
    {
        u.Exigir(P.LocacaoReceber);
        await using var db = await fabrica.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var pg = await db.FaturaLocacaoPagamentos.SingleOrDefaultAsync(p => p.Id == pagamentoId && p.EstornadoEm == null)
            ?? throw new NaoEncontradoException("Recebimento não encontrado.");
        await Fatura(db, u, pg.FaturaId, rastrear: false);
        var repasses = await db.RepassesLocacao.Where(r => r.PagamentoId == pg.Id).ToListAsync();
        var idsRep = repasses.Where(r => r.LancamentoEmpresaId != null).Select(r => r.LancamentoEmpresaId!.Value).ToList();
        if (repasses.Any(r => r.PagoEm != null)
            || await db.LancamentosEmpresa.AnyAsync(l => idsRep.Contains(l.Id) && l.ExcluidoEm == null && l.DataPagamento != null))
            throw new RegraNegocioException("_", "O repasse deste recebimento já foi pago: desfaça o pagamento do repasse antes.");
        var lancs = await db.LancamentosEmpresa.Where(l => pg.LancamentosEmpresaIds.Contains(l.Id) && l.ExcluidoEm == null).ToListAsync();
        foreach (var l in lancs)
        {
            await MesesFechados.Garantir(db, l.DataPagamento, l.Competencia);
            if (l.ExtratoItemId is not null)
                throw new RegraNegocioException("_", "Este recebimento já foi conciliado com o extrato: desfaça a conciliação antes.");
            var antes = l.Copia();
            l.ExcluidoEm = DateTime.UtcNow;
            l.AtualizadoPor = u.Id;
            audit.Registrar(db, u.Id, "lancamentos_empresa", l.Id, "EXCLUIR", antes, l);
        }
        if (pg.LancamentoImovelId is { } li && await db.LancamentosFinanceiros.SingleOrDefaultAsync(l => l.Id == li && l.ExcluidoEm == null) is { } lf)
        {
            if (lf.ExtratoItemId is not null)
                throw new RegraNegocioException("_", "Este recebimento já foi conciliado com o extrato: desfaça a conciliação antes.");
            var antes = lf.Copia();
            lf.ExcluidoEm = DateTime.UtcNow;
            lf.AtualizadoPor = u.Id;
            audit.Registrar(db, u.Id, "lancamentos_financeiros", lf.Id, "EXCLUIR", antes, lf);
        }
        var antesPg = pg.Copia();
        pg.EstornadoEm = DateTime.UtcNow;
        pg.EstornadoPor = u.Id;
        audit.Registrar(db, u.Id, "faturas_locacao", pg.FaturaId, "ESTORNAR", antesPg, pg);
        await db.SaveChangesAsync();
        await AtualizarStatus(db, pg.FaturaId);
        await tx.CommitAsync();
    }

    /// <summary>Marca o repasse como pago (e o lançamento "Repasse ao proprietário" no financeiro da imobiliária).</summary>
    public async Task PagarRepasseAsync(UsuarioAtual u, Guid repasseId, DateOnly data, Guid contaBancariaId)
    {
        u.Exigir(P.LocacaoReceber);
        await using var db = await fabrica.CreateDbContextAsync();
        var r = await db.RepassesLocacao.SingleOrDefaultAsync(x => x.Id == repasseId) ?? throw new NaoEncontradoException("Repasse não encontrado.");
        await Contrato(db, u, r.ContratoId, rastrear: false);
        if (r.PagoEm is not null) throw new RegraNegocioException("_", "Este repasse já foi pago.");
        if (!await db.ContasBancarias.AnyAsync(x => x.Id == contaBancariaId && x.Ativa))
            throw new RegraNegocioException("conta", "Conta bancária inválida.");
        await MesesFechados.Garantir(db, data);
        r.PagoEm = data;
        if (r.LancamentoEmpresaId is { } lid && await db.LancamentosEmpresa.SingleOrDefaultAsync(l => l.Id == lid && l.ExcluidoEm == null) is { } l
            && l.DataPagamento is null)
        {
            var antes = l.Copia();
            l.DataPagamento = data;
            l.ContaId = contaBancariaId;
            l.AtualizadoPor = u.Id;
            l.AtualizadoEm = DateTime.UtcNow;
            audit.Registrar(db, u.Id, "lancamentos_empresa", l.Id, "PAGAR", antes, l);
        }
        audit.Registrar(db, u.Id, "repasses_locacao", r.Id, "PAGAR", depois: new { r.PagoEm, r.Liquido, conta = contaBancariaId });
        await db.SaveChangesAsync();
    }

    /* ------------------------------ documentos ------------------------------ */

    /// <summary>Recibo discriminado do recebimento (Lei 8.245, art. 22, VI: cada valor em sua linha, sem quitação genérica).</summary>
    public async Task<Arquivo> ReciboPdfAsync(UsuarioAtual u, Guid pagamentoId)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var pg = await db.FaturaLocacaoPagamentos.AsNoTracking().SingleOrDefaultAsync(p => p.Id == pagamentoId)
            ?? throw new NaoEncontradoException("Recebimento não encontrado.");
        var f = await Fatura(db, u, pg.FaturaId, rastrear: false);
        var c = f.Contrato!;
        var d = await ObterAsync(u, c.Id);
        var locatarios = d.Partes.Where(p => p.Papel == PapelContratoLocacao.LOCATARIO).ToList();
        var locadores = d.Partes.Where(p => p.Papel == PapelContratoLocacao.LOCADOR).ToList();
        var totalFatura = f.Itens.Sum(i => i.Valor);
        var anteriores = f.Pagamentos.Where(p => p.EstornadoEm == null && p.ReciboNumero < pg.ReciboNumero).Sum(p => p.Principal);
        var saldoApos = totalFatura - anteriores - pg.Principal;
        var titulo = pg.EstornadoEm is null ? $"Recibo de aluguel nº {pg.ReciboNumero:000000}" : $"Recibo nº {pg.ReciboNumero:000000} (ESTORNADO)";
        var pagador = string.Join(" e ", locatarios.Select(p => p.CpfCnpj is { } doc ? $"{p.Nome} ({Formatos.Documento(doc)})" : p.Nome));
        var pdf = RelatoriosService.NovoPdf(titulo, $"{d.Codigo} · {d.ImovelCodigo} · {d.ImovelTitulo}",
            $"Inova Bens · recibo emitido em {DatasBR.Agora():dd/MM/yyyy HH:mm}", col =>
            {
                RelatoriosService.Paragrafo(col, $"Recebemos de {pagador} a importância de {Texto.Moeda(pg.Total)} ({Extenso.Reais(pg.Total)}), "
                    + $"referente {(f.Rescisao ? "ao acerto da rescisão" : "ao aluguel e encargos")} de {f.Competencia:MM/yyyy} do imóvel {d.ImovelEndereco ?? d.ImovelCodigo}, "
                    + $"conforme o contrato {d.Codigo}, discriminados abaixo.");
                RelatoriosService.Secao(col, "Valores da fatura");
                RelatoriosService.Tabela(col, [("Descrição", 4), ("Valor", 1.4f)],
                    [.. f.Itens.OrderBy(i => i.Ordem).Select(i => new[] { i.Descricao, Texto.Moeda(i.Valor) }),
                        ["Total da fatura", Texto.Moeda(totalFatura)]], alinharUltimaDireita: true);
                RelatoriosService.Secao(col, "Este recebimento");
                var linhas = new List<string[]> { new[] { "Principal (abatido da fatura)", Texto.Moeda(pg.Principal) } };
                if (pg.Multa > 0) linhas.Add([$"Multa por atraso ({c.MultaAtrasoPct.ToString("0.##", Texto.PtBr)}%)", Texto.Moeda(pg.Multa)]);
                if (pg.Juros > 0) linhas.Add([$"Juros de mora ({c.JurosMesPct.ToString("0.##", Texto.PtBr)}% ao mês)", Texto.Moeda(pg.Juros)]);
                linhas.Add(["Total recebido", Texto.Moeda(pg.Total)]);
                RelatoriosService.Tabela(col, [("Descrição", 4), ("Valor", 1.4f)], linhas, alinharUltimaDireita: true);
                RelatoriosService.Campos(col, 3, ("Vencimento", Texto.Data(f.Vencimento)), ("Pago em", Texto.Data(pg.Data)),
                    ("Forma", pg.Forma ?? "—"), ("Saldo da fatura após este recibo", Texto.Moeda(Math.Max(0, saldoApos))),
                    ("Locador(es)", string.Join(", ", locadores.Select(l => l.Nome))), ("Administradora", c.Modalidade == ModalidadeLocacao.ADMINISTRACAO ? "Inova Bens" : "—"));
                if (saldoApos > 0) RelatoriosService.Paragrafo(col, "Recebimento parcial: este recibo não dá quitação do restante da fatura.");
                RelatoriosService.Paragrafo(col, $"\n\n_____________________________________________\nInova Bens · {Texto.Data(pg.Data)}");
            });
        audit.Registrar(db, u.Id, "faturas_locacao", f.Id, "EXPORTAR", depois: new { recibo = pg.ReciboNumero, formato = "pdf" });
        await db.SaveChangesAsync();
        return new Arquivo(pdf, $"recibo-{pg.ReciboNumero:000000}.pdf", RelatoriosService.TipoPdf);
    }

    /// <summary>Extrato do proprietário num mês: recebimentos do mês, taxa, intermediação e repasses de cada contrato.</summary>
    public async Task<Arquivo> ExtratoProprietarioPdfAsync(UsuarioAtual u, Guid pessoaId, DateOnly mes)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        mes = CalculoLocacao.MesDe(mes);
        var fim = mes.AddMonths(1);
        var nome = await db.Pessoas.Where(p => p.Id == pessoaId).Select(p => p.NomeRazao).SingleOrDefaultAsync()
            ?? throw new NaoEncontradoException("Pessoa não encontrada.");
        var visiveis = db.ContratosLocacao.Where(c => db.Imoveis.VisiveisPara(u).Any(i => i.Id == c.ImovelId));
        var linhas = (await Repasses(db, db.RepassesLocacao.Where(r => r.PessoaId == pessoaId && visiveis.Any(c => c.Id == r.ContratoId)
            && db.FaturaLocacaoPagamentos.Any(p => p.Id == r.PagamentoId && p.Data >= mes && p.Data < fim))))
            .Where(r => !r.Estornado).OrderBy(r => r.ContratoCodigo).ThenBy(r => r.Competencia).ToList();
        var pdf = RelatoriosService.NovoPdf($"Extrato do proprietário · {mes:MM/yyyy}", nome,
            $"Inova Bens · extrato gerado em {DatasBR.Agora():dd/MM/yyyy HH:mm}", col =>
            {
                if (linhas.Count == 0) { RelatoriosService.Paragrafo(col, "Nenhum recebimento neste mês."); return; }
                RelatoriosService.Tabela(col, [("Contrato", 1.2f), ("Imóvel", 1), ("Ref.", 0.9f), ("Recebido", 1.2f), ("Taxa adm.", 1.1f),
                        ("Intermediação", 1.2f), ("Manutenção", 1.1f), ("Líquido", 1.2f), ("Repasse", 1.1f)],
                    [.. linhas.Select(r => new[] { r.ContratoCodigo, r.ImovelCodigo, r.Competencia.ToString("MM/yyyy"), Texto.Moeda(r.Bruto),
                        Texto.Moeda(r.Taxa), Texto.Moeda(r.Intermediacao), Texto.Moeda(r.DescontoManutencao), Texto.Moeda(r.Liquido),
                        r.PagoEm is { } p ? $"pago {Texto.Data(p)}" : $"prev. {Texto.Data(r.PrevistoPara)}" })], colunasDireita: 6);
                RelatoriosService.Campos(col, 4, ("Total recebido", Texto.Moeda(linhas.Sum(r => r.Bruto))),
                    ("Taxa de administração", Texto.Moeda(linhas.Sum(r => r.Taxa))), ("Intermediação", Texto.Moeda(linhas.Sum(r => r.Intermediacao))),
                    ("Manutenção descontada", Texto.Moeda(linhas.Sum(r => r.DescontoManutencao))),
                    ("Líquido a receber", Texto.Moeda(linhas.Sum(r => r.Liquido))));
            }, paisagem: true);
        audit.Registrar(db, u.Id, "repasses_locacao", null, "EXPORTAR", depois: new { relatorio = "extrato-proprietario", pessoa = pessoaId, mes });
        await db.SaveChangesAsync();
        return new Arquivo(pdf, $"extrato-{mes:yyyy-MM}.pdf", RelatoriosService.TipoPdf);
    }

    /* ------------------------------ auxiliares ------------------------------ */

    private static string Nomes(IEnumerable<string> nomes) => string.Join(", ", nomes) is { Length: > 0 } s ? s : "—";

    private static void Aplicar(ContratoForm f, ContratoLocacao c)
    {
        c.PropostaId = f.PropostaId;
        c.Modalidade = f.Modalidade;
        c.Tipo = f.Tipo;
        c.Inicio = f.Inicio!.Value;
        c.PrazoMeses = f.PrazoMeses;
        c.DiaVencimento = f.DiaVencimento;
        c.PagamentoAntecipado = f.PagamentoAntecipado;
        c.ValorAluguel = f.ValorAluguel!.Value;
        c.ValorCondominio = f.ValorCondominio;
        c.ValorIptu = f.ValorIptu;
        c.ValorSeguroIncendio = f.ValorSeguroIncendio;
        c.IndiceReajuste = f.IndiceReajuste;
        c.Garantia = f.Garantia;
        c.GarantiaValor = f.Garantia == GarantiaLocacao.SEM_GARANTIA ? null : f.GarantiaValor;
        c.GarantiaDetalhe = Texto.Limpo(f.GarantiaDetalhe);
        // imóvel próprio: sem taxa nem intermediação (não há terceiro a quem cobrar)
        var proprio = f.Modalidade == ModalidadeLocacao.PROPRIO;
        c.TaxaAdministracaoPct = proprio ? 0 : f.TaxaAdministracaoPct;
        c.IntermediacaoValor = proprio ? 0 : f.IntermediacaoValor ?? f.ValorAluguel!.Value;
        c.MultaAtrasoPct = f.MultaAtrasoPct;
        c.JurosMesPct = f.JurosMesPct;
        c.MultaRescisoriaAlugueis = f.MultaRescisoriaAlugueis;
        c.DiasRepasse = f.DiasRepasse;
        c.LimiteManutencao = f.LimiteManutencao;
        c.ClausulaVigencia = f.ClausulaVigencia;
        c.Averbado = f.Averbado;
        c.Observacoes = Texto.Limpo(f.Observacoes);
        c.GarantiaVence = f.Garantia is GarantiaLocacao.SEGURO_FIANCA or GarantiaLocacao.TITULO_CAPITALIZACAO or GarantiaLocacao.FIADOR
            ? f.GarantiaVence : null;
        c.SeguroIncendioVence = f.SeguroIncendioVence;
    }

    private static void ValidarParaAtivar(ContratoLocacao c, IReadOnlyList<ParteForm> partes)
    {
        if (!partes.Any(p => p.Papel == PapelContratoLocacao.LOCATARIO))
            throw new RegraNegocioException(nameof(ContratoForm.Partes), "Informe ao menos um locatário.");
        var locadores = partes.Where(p => p.Papel == PapelContratoLocacao.LOCADOR).ToList();
        if (c.Modalidade == ModalidadeLocacao.ADMINISTRACAO)
        {
            if (locadores.Count == 0) throw new RegraNegocioException(nameof(ContratoForm.Partes), "Informe o(s) locador(es) que recebem o repasse.");
            if (locadores.Sum(p => p.Percentual ?? 0) != 100)
                throw new RegraNegocioException(nameof(ContratoForm.Partes), "Os percentuais dos locadores precisam somar 100%.");
        }
        if (c.Garantia == GarantiaLocacao.FIADOR && !partes.Any(p => p.Papel == PapelContratoLocacao.FIADOR))
            throw new RegraNegocioException(nameof(ContratoForm.Partes), "Garantia por fiador: informe o fiador.");
        // garantia única (art. 37): fiador só com a garantia de fiança
        if (c.Garantia != GarantiaLocacao.FIADOR && partes.Any(p => p.Papel == PapelContratoLocacao.FIADOR))
            throw new RegraNegocioException(nameof(ContratoForm.Partes), "Fiador só com a garantia \"Fiador\" (a lei proíbe mais de uma garantia).");
    }

    private static async Task SincronizarPartes(InovabensDb db, ContratoLocacao c, List<ParteForm> partes)
    {
        var validas = partes.Where(p => p.PessoaId is not null)
            .GroupBy(p => (p.PessoaId!.Value, p.Papel)).Select(g => g.First()).ToList();
        var ids = validas.Select(p => p.PessoaId!.Value).Distinct().ToList();
        var existentes = await db.Pessoas.Where(p => ids.Contains(p.Id) && p.ExcluidoEm == null).Select(p => p.Id).ToListAsync();
        if (existentes.Count != ids.Count) throw new RegraNegocioException(nameof(ContratoForm.Partes), "Pessoa não encontrada no cadastro.");
        var atuais = await db.ContratoLocacaoPartes.Where(p => p.ContratoId == c.Id).ToListAsync();
        db.ContratoLocacaoPartes.RemoveRange(atuais.Where(a => !validas.Any(v => v.PessoaId == a.PessoaId && v.Papel == a.Papel)));
        foreach (var v in validas)
        {
            var pct = v.Papel == PapelContratoLocacao.LOCADOR ? v.Percentual ?? 0 : (decimal?)null;
            if (atuais.FirstOrDefault(a => a.PessoaId == v.PessoaId && a.Papel == v.Papel) is { } a) a.Percentual = pct;
            else db.ContratoLocacaoPartes.Add(new ContratoLocacaoParte { ContratoId = c.Id, PessoaId = v.PessoaId!.Value, Papel = v.Papel, Percentual = pct });
        }
    }

    /// <summary>Locatário e fiador ganham o perfil correspondente no cadastro de pessoas.</summary>
    private static async Task GarantirPapeis(InovabensDb db, IReadOnlyList<ContratoLocacaoParte> partes)
    {
        foreach (var p in partes)
        {
            var papel = p.Papel switch
            {
                PapelContratoLocacao.LOCATARIO => "LOCATARIO",
                PapelContratoLocacao.FIADOR => "FIADOR",
                _ => "PROPRIETARIO",
            };
            if (!await db.PessoaPapeis.AnyAsync(x => x.PessoaId == p.PessoaId && x.Papel == papel)
                && db.PessoaPapeis.Local.All(x => x.PessoaId != p.PessoaId || x.Papel != papel))
                db.PessoaPapeis.Add(new PessoaPapel { PessoaId = p.PessoaId, Papel = papel });
        }
    }

    /// <summary>Cria as faturas que faltam, das competências cujo vencimento chega até <paramref name="ate"/>.</summary>
    private static async Task<int> GerarFaturas(InovabensDb db, ContratoLocacao c, DateOnly ate)
    {
        var existentes = await db.FaturasLocacao.Where(f => f.ContratoId == c.Id && f.Status != StatusFaturaLocacao.CANCELADA && !f.Rescisao)
            .Select(f => f.Competencia).ToListAsync();
        var n = 0;
        // depois do fim do prazo a cobrança continua (prazo indeterminado) até a entrega das chaves
        var fim = CalculoLocacao.FimCobranca(c);
        foreach (var comp in CalculoLocacao.Competencias(c.Inicio, fim < ate ? fim : ate))
        {
            var venc = CalculoLocacao.Vencimento(comp, c.DiaVencimento, c.PagamentoAntecipado, c.Inicio);
            if (venc > ate) break;
            if (existentes.Contains(comp)) continue;
            var itens = CalculoLocacao.Itens(c, comp, fim);
            if (itens.Count == 0) continue;
            var f = new FaturaLocacao { ContratoId = c.Id, Competencia = comp, Vencimento = venc };
            f.Itens.AddRange(itens.Select((i, k) => new FaturaLocacaoItem { FaturaId = f.Id, Ordem = k + 1, Tipo = i.Tipo, Descricao = i.Descricao, Valor = i.Valor }));
            db.FaturasLocacao.Add(f);
            n++;
        }
        return n;
    }

    /// <summary>
    /// Valores do contrato mudaram: refaz os itens fixos das faturas em aberto sem recebimento, a partir de
    /// <paramref name="desde"/> (padrão: deste mês em diante).
    /// </summary>
    private static async Task RecalcularAbertas(InovabensDb db, ContratoLocacao c, DateOnly? desde = null)
    {
        var mes = CalculoLocacao.MesDe(desde ?? DatasBR.Hoje());
        var abertas = await db.FaturasLocacao.Include(f => f.Itens)
            .Where(f => f.ContratoId == c.Id && f.Status == StatusFaturaLocacao.ABERTA && f.Competencia >= mes && !f.Rescisao
                && !f.Pagamentos.Any(p => p.EstornadoEm == null)).ToListAsync();
        TipoItemFatura[] fixos = [TipoItemFatura.ALUGUEL, TipoItemFatura.CONDOMINIO, TipoItemFatura.IPTU, TipoItemFatura.SEGURO_INCENDIO];
        foreach (var f in abertas)
        {
            db.FaturaLocacaoItens.RemoveRange(f.Itens.Where(i => fixos.Contains(i.Tipo)));
            var ordem = 0;
            foreach (var i in CalculoLocacao.Itens(c, f.Competencia, CalculoLocacao.FimCobranca(c)))
                db.FaturaLocacaoItens.Add(new FaturaLocacaoItem { FaturaId = f.Id, Ordem = ++ordem, Tipo = i.Tipo, Descricao = i.Descricao, Valor = i.Valor });
            f.Vencimento = CalculoLocacao.Vencimento(f.Competencia, c.DiaVencimento, c.PagamentoAntecipado, c.Inicio);
        }
    }

    private static decimal Pago(FaturaLocacao f) => f.Pagamentos.Where(p => p.EstornadoEm == null).Sum(p => p.Principal);

    private static async Task AtualizarStatus(InovabensDb db, Guid faturaId)
    {
        var f = await db.FaturasLocacao.Include(x => x.Itens).Include(x => x.Pagamentos).SingleAsync(x => x.Id == faturaId);
        if (f.Status == StatusFaturaLocacao.CANCELADA) return;
        var novo = Pago(f) >= f.Itens.Sum(i => i.Valor) ? StatusFaturaLocacao.PAGA : StatusFaturaLocacao.ABERTA;
        if (novo == f.Status) return;
        f.Status = novo;
        await db.SaveChangesAsync();
    }

    internal static async Task<Guid> Categoria(InovabensDb db, string nome, TipoCategoriaEmpresa tipo, GrupoCategoriaEmpresa grupo)
    {
        // pelo nome padrão; se foi renomeada, a primeira ativa do mesmo tipo e grupo
        return await db.CategoriasEmpresa.Where(c => c.Nome == nome && c.Tipo == tipo).Select(c => (Guid?)c.Id).FirstOrDefaultAsync()
            ?? await db.CategoriasEmpresa.Where(c => c.Ativa && c.Tipo == tipo && c.Grupo == grupo).OrderBy(c => c.Nome).Select(c => (Guid?)c.Id).FirstOrDefaultAsync()
            ?? throw new RegraNegocioException("_", $"Crie a categoria \"{nome}\" no Financeiro da Imobiliária.");
    }

    private static async Task<ContratoLocacao> Contrato(InovabensDb db, UsuarioAtual u, Guid id, bool rastrear = true)
    {
        var q = rastrear ? db.ContratosLocacao : db.ContratosLocacao.AsNoTracking();
        return await q.Where(c => c.Id == id && db.Imoveis.VisiveisPara(u).Any(i => i.Id == c.ImovelId)).SingleOrDefaultAsync()
            ?? throw new NaoEncontradoException("Contrato não encontrado.");
    }

    private static async Task<FaturaLocacao> Fatura(InovabensDb db, UsuarioAtual u, Guid id, bool rastrear = true)
    {
        var q = db.FaturasLocacao.Include(f => f.Itens).Include(f => f.Pagamentos).Include(f => f.Contrato).AsQueryable();
        if (!rastrear) q = q.AsNoTracking();
        return await q.Where(f => f.Id == id && db.Imoveis.VisiveisPara(u).Any(i => i.Id == f.Contrato!.ImovelId)).SingleOrDefaultAsync()
            ?? throw new NaoEncontradoException("Fatura não encontrada.");
    }

    /// <summary>Faturas com itens e recebimentos (montadas em memória: poucas por contrato), por competência.</summary>
    private static async Task<List<FaturaDetalhe>> Faturas(InovabensDb db, IQueryable<FaturaLocacao> q)
    {
        var faturas = await q.AsNoTracking().Include(f => f.Itens).Include(f => f.Pagamentos).OrderBy(f => f.Competencia).ToListAsync();
        var pagIds = faturas.SelectMany(f => f.Pagamentos).Select(p => p.Id).ToList();
        var contaIds = faturas.SelectMany(f => f.Pagamentos).Where(p => p.ContaBancariaId != null).Select(p => p.ContaBancariaId!.Value).Distinct().ToList();
        var contas = await db.ContasBancarias.AsNoTracking().Where(c => contaIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Nome);
        // repasse pago pela tela da locação ou direto no financeiro da imobiliária
        var comRepassePago = (await db.RepassesLocacao.AsNoTracking().Where(r => pagIds.Contains(r.PagamentoId)
                && (r.PagoEm != null || db.LancamentosEmpresa.Any(l => l.Id == r.LancamentoEmpresaId && l.ExcluidoEm == null && l.DataPagamento != null)))
            .Select(r => r.PagamentoId).ToListAsync()).ToHashSet();
        return [.. faturas.Select(f => new FaturaDetalhe(f.Id, f.Competencia, f.Vencimento, f.Status, f.Itens.Sum(i => i.Valor), Pago(f),
            [.. f.Itens.OrderBy(i => i.Ordem)],
            [.. f.Pagamentos.OrderBy(p => p.ReciboNumero).Select(p => new PagamentoDetalhe(p.Id, p.ReciboNumero, p.Data, p.Principal, p.Multa,
                p.Juros, p.Forma, p.ContaBancariaId is { } c ? contas.GetValueOrDefault(c) : null, p.EstornadoEm != null, comRepassePago.Contains(p.Id)))],
            f.Observacao ?? f.MotivoCancelamento, f.Rescisao))];
    }

    private static async Task<List<RepasseLinha>> Repasses(InovabensDb db, IQueryable<RepasseLocacao> q)
    {
        var linhas = await q.AsNoTracking().OrderBy(r => r.PrevistoPara)
            .Join(db.FaturaLocacaoPagamentos, r => r.PagamentoId, p => p.Id, (r, p) => new { r, p })
            .Select(x => new
            {
                x.r, Nome = x.r.Pessoa!.NomeRazao, x.p.ReciboNumero, Estornado = x.p.EstornadoEm != null,
                Contrato = db.ContratosLocacao.Where(c => c.Id == x.r.ContratoId).Select(c => new { c.Numero, c.Imovel!.Codigo }).First(),
                Competencia = db.FaturasLocacao.Where(f => f.Id == x.p.FaturaId).Select(f => f.Competencia).First(),
                // pago pela tela da locação ou direto no financeiro da imobiliária
                PagoLanc = db.LancamentosEmpresa.Where(l => l.Id == x.r.LancamentoEmpresaId && l.ExcluidoEm == null).Select(l => l.DataPagamento).FirstOrDefault(),
            }).ToListAsync();
        return [.. linhas.Select(x => new RepasseLinha(x.r.Id, x.r.ContratoId, ContratoLocacao.CodigoDe(x.Contrato.Numero), x.Contrato.Codigo,
            x.r.PessoaId, x.Nome, x.r.Percentual, x.r.Bruto, x.r.TaxaAdministracao, x.r.Intermediacao, x.r.Liquido, x.r.PrevistoPara,
            x.r.PagoEm ?? x.PagoLanc, x.Competencia, x.ReciboNumero, x.Estornado, x.r.DescontoManutencao))];
    }
}
