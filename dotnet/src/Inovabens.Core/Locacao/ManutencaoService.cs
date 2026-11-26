using System.Security.Cryptography;
using Inovabens.Core.Agenda;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Financeiro;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Locacao;

/// <summary>
/// Manutenção (fase 5 da locação): chamado aberto pela equipe, pelo inquilino (link do contrato) ou pela rotina de
/// preventivas → orçamentos de prestadores → aprovação do proprietário por link (dispensada até o limite do contrato,
/// quando o conserto não é por conta do locador ou o imóvel é próprio) → agendamento na agenda, com a chave entregue ao
/// prestador → conclusão com o custo lançado: desconto no repasse, item na fatura do inquilino, despesa do imóvel ou da
/// imobiliária. Usa as permissões da locação.
/// </summary>
public sealed class ManutencaoService(IDbContextFactory<InovabensDb> fabrica, AuditService audit, AgendaService agenda, ChavesService chaves,
    PastasDriveService pastas, IArmazenamento armazenamento, InovabensOpcoes op, ILogger<ManutencaoService> log)
{
    public const string CategoriaPagoPeloProprietario = "Despesa paga por conta do proprietário";
    public const string CategoriaPagoPeloInquilino = "Despesa paga por conta do inquilino";
    public const string CategoriaReembolsoInquilino = "Reembolso de despesa do inquilino";
    public const string CategoriaManutencaoImobiliaria = "Manutenção de imóveis";
    public const string ContaManutencaoImovel = "Manutenção";
    public const int MaxFotosInquilino = 5;
    /// <summary>Cotações exigidas quando a escolha vai para o proprietário (link de aprovação).</summary>
    public const int MinimoCotacoes = 3;
    public const string SubpastaCotacoes = "Cotações";
    public const long TamanhoMaximoFoto = 10 * 1024 * 1024;

    /// <summary>Preventiva: o chamado abre esta quantidade de dias antes da data do serviço.</summary>
    public const int DiasAntecedenciaPreventiva = 30;

    /// <summary>
    /// Sugestão de quem arca (Lei 8.245/91): estrutura, telhado e instalações (desgaste, vício) com o locador (art. 22);
    /// limpeza, pintura de uso, jardim e dedetização com o locatário (art. 23). A equipe confirma caso a caso.
    /// </summary>
    public static ResponsavelManutencao ResponsavelSugerido(CategoriaManutencao c) => c switch
    {
        CategoriaManutencao.PINTURA or CategoriaManutencao.LIMPEZA or CategoriaManutencao.JARDIM or CategoriaManutencao.DEDETIZACAO
            or CategoriaManutencao.ELETRODOMESTICO => ResponsavelManutencao.LOCATARIO,
        _ => ResponsavelManutencao.LOCADOR,
    };

    /// <summary>Cobrança padrão na conclusão, conforme quem arca e a modalidade do contrato.</summary>
    public static CobrancaManutencao CobrancaSugerida(ResponsavelManutencao r, ModalidadeLocacao? m) => r switch
    {
        ResponsavelManutencao.LOCATARIO when m is not null => CobrancaManutencao.FATURA,
        ResponsavelManutencao.LOCATARIO => CobrancaManutencao.DIRETO,
        ResponsavelManutencao.IMOBILIARIA => CobrancaManutencao.IMOBILIARIA,
        _ when m == ModalidadeLocacao.ADMINISTRACAO => CobrancaManutencao.REPASSE,
        _ => CobrancaManutencao.IMOVEL,
    };

    private static string NovoToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();

    private string Url(string caminho) => $"{op.WebUrl.TrimEnd('/')}/{caminho}";

    internal static string Endereco(Imovel i) =>
        string.Join(", ", new[] { i.Logradouro is null ? null : $"{i.Logradouro}{(i.Numero is null ? "" : ", " + i.Numero)}", i.Complemento, i.Bairro,
            i.Cidade is null ? null : $"{i.Cidade}{(i.Uf is null ? "" : "/" + i.Uf)}" }.Where(x => !string.IsNullOrWhiteSpace(x)));

    /* ------------------------------ consultas ------------------------------ */

    public async Task<IReadOnlyList<ChamadoLinha>> ListarAsync(UsuarioAtual u, FiltroChamados f)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var q = db.ChamadosManutencao.AsNoTracking().Where(c => db.Imoveis.VisiveisPara(u).Any(i => i.Id == c.ImovelId));
        if (f.SoAbertos) q = q.Where(c => c.Status != StatusChamado.CONCLUIDO && c.Status != StatusChamado.RECUSADO && c.Status != StatusChamado.CANCELADO);
        if (f.Status is { } s) q = q.Where(c => c.Status == s);
        if (f.ImovelId is { } im) q = q.Where(c => c.ImovelId == im);
        if (f.ContratoId is { } ct) q = q.Where(c => c.ContratoId == ct);
        if (Texto.Limpo(f.Termo) is { } t)
        {
            var like = Texto.LikeBusca(t);
            q = q.Where(c => EF.Functions.Like(FuncoesBusca.Normalizar(c.Titulo), like, @"\")
                || EF.Functions.Like(FuncoesBusca.Normalizar(c.Imovel!.Codigo), like, @"\"));
        }
        var linhas = await q.OrderByDescending(c => c.CriadoEm).Take(500)
            .Select(c => new
            {
                c,
                Imovel = c.Imovel!.Codigo,
                Contrato = db.ContratosLocacao.Where(x => x.Id == c.ContratoId).Select(x => (int?)x.Numero).FirstOrDefault(),
                Prestador = db.Pessoas.Where(p => p.Id == c.PrestadorId).Select(p => p.NomeRazao).FirstOrDefault(),
            }).ToListAsync();
        return [.. linhas.Select(x => new ChamadoLinha(x.c.Id, x.c.Codigo, x.c.Titulo, x.c.ImovelId, x.Imovel, x.c.ContratoId,
            x.Contrato is { } n ? ContratoLocacao.CodigoDe(n) : null, x.c.Categoria, x.c.Urgencia, x.c.Status, x.c.Responsavel, x.c.Origem,
            x.Prestador, x.c.ValorFinal ?? x.c.Valor, x.c.AgendadoPara, x.c.CriadoEm))];
    }

    public async Task<ChamadoDetalhe> ObterAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await Chamado(db, u, id, rastrear: false);
        var im = await db.Imoveis.AsNoTracking().SingleAsync(i => i.Id == c.ImovelId);
        var contrato = c.ContratoId is { } cid ? await db.ContratosLocacao.AsNoTracking().SingleOrDefaultAsync(x => x.Id == cid) : null;
        var partes = contrato is null ? [] : await db.ContratoLocacaoPartes.AsNoTracking().Where(p => p.ContratoId == contrato.Id)
            .OrderByDescending(p => p.Percentual)
            .Select(p => new { p.Papel, p.Pessoa!.NomeRazao, Fone = p.Pessoa.Whatsapp ?? p.Pessoa.Telefone }).ToListAsync();
        var locatario = partes.FirstOrDefault(p => p.Papel == PapelContratoLocacao.LOCATARIO);
        var locador = partes.FirstOrDefault(p => p.Papel == PapelContratoLocacao.LOCADOR);
        var orcs = await db.OrcamentosManutencao.AsNoTracking().Where(o => o.ChamadoId == id).OrderBy(o => o.Valor)
            .Select(o => new OrcamentoLinha(o.Id, o.PrestadorId, o.Prestador!.NomeRazao, o.Prestador.Whatsapp ?? o.Prestador.Telefone, o.Valor,
                o.PrazoDias, o.Descricao, o.Id == c.OrcamentoId,
                db.ChamadoArquivos.Where(a => a.Id == o.ArquivoId).Select(a => a.Nome).FirstOrDefault())).ToListAsync();
        var prestador = c.PrestadorId is { } pid
            ? await db.Pessoas.AsNoTracking().Where(p => p.Id == pid).Select(p => new { p.NomeRazao, Fone = p.Whatsapp ?? p.Telefone }).SingleOrDefaultAsync()
            : null;
        var arquivos = await db.ChamadoArquivos.AsNoTracking().Where(a => a.ChamadoId == id).OrderBy(a => a.CriadoEm)
            .Select(a => new ArquivoChamadoLinha(a.Id, a.Nome, a.Tipo, a.PeloInquilino, a.CriadoEm)).ToListAsync();
        var eventos = await db.ChamadoEventos.AsNoTracking().Where(e => e.ChamadoId == id).OrderBy(e => e.CriadoEm)
            .Select(e => new EventoChamadoLinha(e.CriadoEm, e.Descricao, db.Usuarios.Where(x => x.Id == e.UsuarioId).Select(x => x.Nome).FirstOrDefault()))
            .ToListAsync();
        var abatido = await Abatido(db, id);
        return new ChamadoDetalhe(c, im.Codigo, Endereco(im), contrato?.Codigo, contrato?.Modalidade, contrato?.LimiteManutencao ?? 0,
            prestador?.NomeRazao, prestador?.Fone, locatario?.NomeRazao, locatario?.Fone, locador?.NomeRazao, locador?.Fone, orcs, arquivos, eventos,
            abatido, armazenamento.Habilitado ? pastas.Link(c.DrivePastaId) : null,
            c.AprovacaoToken is { } tk && c.Status == StatusChamado.AGUARDANDO_APROVACAO ? Url($"aprovacao/{tk}") : null);
    }

    /// <summary>Pessoas para o orçamento: prestadores (perfil PRESTADOR) primeiro, com a especialidade; depois as demais.</summary>
    public async Task<IReadOnlyList<Opcao>> PrestadoresAsync(UsuarioAtual u)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var lista = await db.Pessoas.AsNoTracking().Where(p => p.ExcluidoEm == null)
            .Select(p => new
            {
                p.Id, p.NomeRazao,
                Esp = p.Papeis.Where(x => x.Papel == PerfilPessoa.Prestador).Select(x => x.Especialidade).FirstOrDefault(),
                Prestador = p.Papeis.Any(x => x.Papel == PerfilPessoa.Prestador),
            })
            .OrderByDescending(p => p.Prestador).ThenBy(p => p.NomeRazao).Take(1000).ToListAsync();
        return [.. lista.Select(p => new Opcao(p.Id, p.Esp is { Length: > 0 } e ? $"{p.NomeRazao} ({e})" : p.NomeRazao))];
    }

    /* ------------------------------ abertura e edição ------------------------------ */

    public async Task<Guid> CriarAsync(UsuarioAtual u, ChamadoForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        if (!await db.Imoveis.VisiveisPara(u).AnyAsync(i => i.Id == form.ImovelId))
            throw new NaoEncontradoException("Imóvel não encontrado.");
        var c = new ChamadoManutencao
        {
            ImovelId = form.ImovelId!.Value, Origem = OrigemChamado.EQUIPE, CriadoPor = u.Id,
            ContratoId = await ContratoDoImovel(db, form.ImovelId.Value, form.ContratoId),
        };
        Preencher(c, form);
        db.ChamadosManutencao.Add(c);
        Evento(db, c.Id, "Chamado aberto pela equipe.", u.Id);
        audit.Registrar(db, u.Id, "chamados_manutencao", c.Id, "CRIAR", depois: c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    public async Task<ChamadoForm> FormularioAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await Chamado(db, u, id, rastrear: false);
        return new ChamadoForm
        {
            ImovelId = c.ImovelId, ContratoId = c.ContratoId, Titulo = c.Titulo, Descricao = c.Descricao, Categoria = c.Categoria,
            Urgencia = c.Urgencia, Responsavel = c.Responsavel, SolicitanteNome = c.SolicitanteNome, SolicitanteTelefone = c.SolicitanteTelefone,
        };
    }

    public async Task AtualizarAsync(UsuarioAtual u, Guid id, ChamadoForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await Chamado(db, u, id);
        if (!c.Aberto) throw new RegraNegocioException("_", "Chamado encerrado não pode ser alterado.");
        var antes = new { c.Titulo, c.Categoria, c.Urgencia, c.Responsavel };
        Preencher(c, form);
        c.AtualizadoEm = DateTime.UtcNow;
        if (antes.Responsavel != c.Responsavel) Evento(db, c.Id, $"Responsável pelo custo: {Texto.Rotulo(c.Responsavel)}.", u.Id);
        audit.Registrar(db, u.Id, "chamados_manutencao", c.Id, "ATUALIZAR", antes, new { c.Titulo, c.Categoria, c.Urgencia, c.Responsavel });
        await db.SaveChangesAsync();
    }

    private static void Preencher(ChamadoManutencao c, ChamadoForm f)
    {
        c.Titulo = f.Titulo.Trim();
        c.Descricao = Texto.Limpo(f.Descricao);
        c.Categoria = f.Categoria;
        c.Urgencia = f.Urgencia;
        c.Responsavel = f.Responsavel;
        c.SolicitanteNome = Texto.Limpo(f.SolicitanteNome);
        c.SolicitanteTelefone = Texto.Limpo(f.SolicitanteTelefone) is { } t ? Formatos.TelefoneFormatado(t) : null;
    }

    /// <summary>Contrato informado (do mesmo imóvel) ou o ativo do imóvel.</summary>
    private static async Task<Guid?> ContratoDoImovel(InovabensDb db, Guid imovelId, Guid? contratoId)
    {
        if (contratoId is { } id)
            return await db.ContratosLocacao.AnyAsync(c => c.Id == id && c.ImovelId == imovelId) ? id
                : throw new RegraNegocioException(nameof(ChamadoForm.ContratoId), "O contrato não é deste imóvel.");
        return await db.ContratosLocacao.Where(c => c.ImovelId == imovelId && c.Status == StatusContratoLocacao.ATIVO)
            .Select(c => (Guid?)c.Id).FirstOrDefaultAsync();
    }

    /* ------------------------------ orçamentos e aprovação ------------------------------ */

    public async Task<Guid> AdicionarOrcamentoAsync(UsuarioAtual u, Guid id, OrcamentoForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await Chamado(db, u, id);
        if (c.Status is not (StatusChamado.ABERTO or StatusChamado.ORCAMENTO or StatusChamado.AGUARDANDO_APROVACAO))
            throw new RegraNegocioException("_", "Orçamentos entram antes da aprovação.");
        var nome = await db.Pessoas.Where(p => p.Id == form.PrestadorId && p.ExcluidoEm == null).Select(p => p.NomeRazao).SingleOrDefaultAsync()
            ?? throw new RegraNegocioException(nameof(form.PrestadorId), "Prestador não encontrado no cadastro de pessoas.");
        var o = new OrcamentoManutencao
        {
            ChamadoId = id, PrestadorId = form.PrestadorId!.Value, Valor = form.Valor!.Value, PrazoDias = form.PrazoDias,
            Descricao = Texto.Limpo(form.Descricao), CriadoPor = u.Id,
        };
        db.OrcamentosManutencao.Add(o);
        if (c.Status == StatusChamado.ABERTO) c.Status = StatusChamado.ORCAMENTO;
        c.AtualizadoEm = DateTime.UtcNow;
        Evento(db, id, $"Orçamento de {nome}: {Texto.Moeda(o.Valor)}{(o.PrazoDias is { } pz ? $", {pz} dia(s)" : "")}.", u.Id);
        audit.Registrar(db, u.Id, "chamados_manutencao", id, "ORCAMENTO", depois: new { nome, o.Valor, o.PrazoDias });
        await db.SaveChangesAsync();
        return o.Id;
    }

    public async Task RemoverOrcamentoAsync(UsuarioAtual u, Guid orcamentoId)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var o = await db.OrcamentosManutencao.SingleOrDefaultAsync(x => x.Id == orcamentoId) ?? throw new NaoEncontradoException("Orçamento não encontrado.");
        var c = await Chamado(db, u, o.ChamadoId);
        if (c.OrcamentoId == o.Id && c.Status != StatusChamado.AGUARDANDO_APROVACAO)
            throw new RegraNegocioException("_", "Este orçamento já foi aprovado.");
        if (c.OrcamentoId == o.Id) (c.OrcamentoId, c.Valor, c.PrestadorId, c.Status) = (null, null, null, StatusChamado.ORCAMENTO);
        db.OrcamentosManutencao.Remove(o);
        Evento(db, c.Id, $"Orçamento de {Texto.Moeda(o.Valor)} removido.", u.Id);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Escolhe o orçamento e pede a aprovação. Dispensada (aprovado na hora) quando o custo não é do locador, o imóvel é
    /// próprio, não há contrato ou o valor cabe no limite pré-aprovado do contrato; senão gera o link para o proprietário.
    /// </summary>
    public async Task<PedidoAprovacao> PedirAprovacaoAsync(UsuarioAtual u, Guid id, Guid orcamentoId, string? justificativa = null)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await Chamado(db, u, id);
        if (c.Status is not (StatusChamado.ORCAMENTO or StatusChamado.AGUARDANDO_APROVACAO or StatusChamado.ABERTO))
            throw new RegraNegocioException("_", "O chamado não está na fase de orçamento.");
        var o = await db.OrcamentosManutencao.Include(x => x.Prestador).SingleOrDefaultAsync(x => x.Id == orcamentoId && x.ChamadoId == id)
            ?? throw new NaoEncontradoException("Orçamento não encontrado.");
        (c.OrcamentoId, c.PrestadorId, c.Valor) = (o.Id, o.PrestadorId, o.Valor);
        c.AtualizadoEm = DateTime.UtcNow;
        var contrato = c.ContratoId is { } cid ? await db.ContratosLocacao.AsNoTracking().SingleAsync(x => x.Id == cid) : null;
        string? dispensa = c.Responsavel != ResponsavelManutencao.LOCADOR ? $"custo por conta do {Texto.Rotulo(c.Responsavel).ToLowerInvariant()}"
            : contrato is null ? "sem contrato de locação"
            : contrato.Modalidade == ModalidadeLocacao.PROPRIO ? "imóvel próprio"
            : o.Valor <= contrato.LimiteManutencao ? $"dentro do limite pré-aprovado de {Texto.Moeda(contrato.LimiteManutencao)}"
            : null;
        if (dispensa is not null)
        {
            Aprovar(c, $"{u.Nome} (aprovação dispensada: {dispensa})", null);
            Evento(db, id, $"Orçamento de {o.Prestador!.NomeRazao} ({Texto.Moeda(o.Valor)}) aprovado: {dispensa}.", u.Id);
            await db.SaveChangesAsync();
            return new PedidoAprovacao(true, dispensa, null, null);
        }
        // o proprietário escolhe entre as cotações: precisa de pelo menos 3
        var cotacoes = await db.OrcamentosManutencao.CountAsync(x => x.ChamadoId == id);
        // menos de 3: só com justificativa, que fica no chamado e aparece para o proprietário
        var just = Texto.Limpo(justificativa) is { } jt ? (jt.Length > 1000 ? jt[..1000] : jt) : null;
        if (cotacoes < MinimoCotacoes && just is null)
            throw new RegraNegocioException("justificativa",
                $"Para o proprietário escolher, cadastre {MinimoCotacoes} cotações (faltam {MinimoCotacoes - cotacoes}) ou informe a justificativa.");
        c.JustificativaCotacoes = cotacoes < MinimoCotacoes ? just : null;
        if (c.JustificativaCotacoes is not null)
            Evento(db, id, $"Enviado com {cotacoes} cotação(ões) (menos de {MinimoCotacoes}). Justificativa: {c.JustificativaCotacoes}", u.Id);
        c.Status = StatusChamado.AGUARDANDO_APROVACAO;
        c.AprovacaoToken ??= NovoToken();
        c.AprovacaoPedidaEm = DateTime.UtcNow;
        Evento(db, id, $"Aprovação pedida ao proprietário: {o.Prestador!.NomeRazao}, {Texto.Moeda(o.Valor)}.", u.Id);
        audit.Registrar(db, u.Id, "chamados_manutencao", id, "PEDIR_APROVACAO", depois: new { o.Valor, prestador = o.Prestador.NomeRazao });
        await db.SaveChangesAsync();
        var link = Url($"aprovacao/{c.AprovacaoToken}");
        var locador = await db.ContratoLocacaoPartes.AsNoTracking().Where(p => p.ContratoId == contrato!.Id && p.Papel == PapelContratoLocacao.LOCADOR)
            .OrderByDescending(p => p.Percentual).Select(p => new { p.Pessoa!.NomeRazao, Fone = p.Pessoa.Whatsapp ?? p.Pessoa.Telefone }).FirstOrDefaultAsync();
        var texto = $"Olá{(locador is null ? "" : ", " + PrimeiroNome(locador.NomeRazao))}! Aqui é da Inova Bens. O imóvel {contrato!.Codigo} precisa de um reparo " +
            $"({c.Titulo}). Orçamento: {Texto.Moeda(o.Valor)}. Para ver os detalhes e aprovar ou recusar: {link}";
        return new PedidoAprovacao(false, null, link, Whatsapp(locador?.Fone, texto));
    }

    /// <summary>A equipe registra a aprovação (proprietário respondeu por telefone, por exemplo).</summary>
    public async Task AprovarAsync(UsuarioAtual u, Guid id, string? obs)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await Chamado(db, u, id);
        if (c.Status != StatusChamado.AGUARDANDO_APROVACAO || c.OrcamentoId is null)
            throw new RegraNegocioException("_", "Escolha o orçamento e peça a aprovação antes.");
        Aprovar(c, $"{u.Nome} (registrado pela equipe)", Texto.Limpo(obs));
        Evento(db, id, $"Aprovação registrada pela equipe{(Texto.Limpo(obs) is { } o ? ": " + o : ".")}", u.Id);
        await db.SaveChangesAsync();
    }

    public async Task RecusarAsync(UsuarioAtual u, Guid id, string? motivo)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await Chamado(db, u, id);
        if (c.Status != StatusChamado.AGUARDANDO_APROVACAO) throw new RegraNegocioException("_", "O chamado não está aguardando aprovação.");
        c.Status = StatusChamado.RECUSADO;
        c.Motivo = Texto.Limpo(motivo) ?? throw new RegraNegocioException(nameof(motivo), "Informe o motivo da recusa.");
        c.AtualizadoEm = DateTime.UtcNow;
        Evento(db, id, $"Recusado (registrado pela equipe): {c.Motivo}", u.Id);
        await db.SaveChangesAsync();
    }

    private static void Aprovar(ChamadoManutencao c, string por, string? obs)
    {
        c.Status = StatusChamado.APROVADO;
        c.AprovadoEm = DateTime.UtcNow;
        c.AprovadoPor = por.Length > 200 ? por[..200] : por;
        c.AprovacaoObs = obs;
        c.AtualizadoEm = DateTime.UtcNow;
    }

    /* ------------------------------ link do proprietário (sem login) ------------------------------ */

    public async Task<AprovacaoPublica?> AprovacaoAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var c = await db.ChamadosManutencao.AsNoTracking().Include(x => x.Imovel).SingleOrDefaultAsync(x => x.AprovacaoToken == token, ct);
        if (c is null) return null;
        var orcs = await db.OrcamentosManutencao.AsNoTracking().Where(o => o.ChamadoId == c.Id).OrderBy(o => o.Valor)
            .Select(o => new OrcamentoPublico(o.Id, o.Prestador!.NomeRazao, o.Valor, o.PrazoDias, o.Descricao, o.ArquivoId != null)).ToListAsync(ct);
        var respondido = c.Status != StatusChamado.AGUARDANDO_APROVACAO;
        return new AprovacaoPublica(c.Codigo, c.Titulo, c.Descricao, $"{c.Imovel!.Codigo} · {Endereco(c.Imovel)}", c.Categoria, c.Urgencia, orcs,
            c.OrcamentoId, c.Status, respondido ? c.AprovadoPor : null, respondido ? c.AprovadoEm ?? c.AtualizadoEm : null,
            respondido ? c.AprovacaoObs ?? c.Motivo : null, c.JustificativaCotacoes);
    }

    /// <summary>Resposta do proprietário no link: aprova um dos orçamentos ou recusa. Avisa a equipe no sino.</summary>
    public async Task<bool> ResponderAprovacaoAsync(string token, bool aprovar, Guid? orcamentoId, string? obs, string? nome, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return false;
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var c = await db.ChamadosManutencao.SingleOrDefaultAsync(x => x.AprovacaoToken == token, ct);
        if (c is null) return false;
        if (c.Status != StatusChamado.AGUARDANDO_APROVACAO) throw new RegraNegocioException("_", "Este pedido já foi respondido.");
        var quem = $"{Texto.Limpo(nome) ?? "Proprietário"} (pelo link)";
        obs = Texto.Limpo(obs) is { } t ? (t.Length > 1000 ? t[..1000] : t) : null;
        if (aprovar)
        {
            if (orcamentoId is { } oid && oid != c.OrcamentoId)
            {
                var o = await db.OrcamentosManutencao.SingleOrDefaultAsync(x => x.Id == oid && x.ChamadoId == c.Id, ct)
                    ?? throw new RegraNegocioException("orcamento", "Orçamento não encontrado.");
                (c.OrcamentoId, c.PrestadorId, c.Valor) = (o.Id, o.PrestadorId, o.Valor);
            }
            Aprovar(c, quem, obs);
            Evento(db, c.Id, $"Aprovado por {quem}: {Texto.Moeda(c.Valor ?? 0)}{(obs is null ? "." : $". Obs.: {obs}")}", null);
        }
        else
        {
            c.Status = StatusChamado.RECUSADO;
            c.Motivo = obs ?? "Recusado pelo proprietário.";
            c.AprovadoPor = quem;
            c.AtualizadoEm = DateTime.UtcNow;
            Evento(db, c.Id, $"Recusado por {quem}{(obs is null ? "." : $": {obs}")}", null);
        }
        await AlertarEquipe(db, c, $"{c.Codigo}: {(aprovar ? "orçamento aprovado" : "orçamento recusado")} pelo proprietário — {c.Titulo}", ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /* ------------------------------ agendamento ------------------------------ */

    /// <summary>Agenda o serviço (compromisso na agenda) e, se escolhida, entrega a chave ao prestador.</summary>
    public async Task<AvisosAgendamento> AgendarAsync(UsuarioAtual u, Guid id, AgendamentoForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        ChamadoManutencao c;
        string? prestador, fonePrestador, imovel, endereco;
        await using (var db = await fabrica.CreateDbContextAsync())
        {
            c = await Chamado(db, u, id, rastrear: false);
            if (c.Status != StatusChamado.APROVADO) throw new RegraNegocioException("_", "Agende depois da aprovação do orçamento.");
            if (c.PrestadorId is null) throw new RegraNegocioException("_", "Escolha o orçamento (prestador) antes.");
            var p = await db.Pessoas.AsNoTracking().Where(x => x.Id == c.PrestadorId).Select(x => new { x.NomeRazao, Fone = x.Whatsapp ?? x.Telefone }).SingleAsync();
            (prestador, fonePrestador) = (p.NomeRazao, p.Fone);
            var im = await db.Imoveis.AsNoTracking().SingleAsync(i => i.Id == c.ImovelId);
            (imovel, endereco) = (im.Codigo, Endereco(im));
        }
        var inicio = form.Inicio!.Value;
        var compromisso = await agenda.CriarAsync(u, c.ImovelId, new CompromissoForm
        {
            Tipo = TipoCompromisso.TAREFA, Titulo = $"Manutenção {c.Codigo} · {prestador}", Local = endereco,
            Descricao = $"{c.Titulo}\nPrestador: {prestador}{(fonePrestador is null ? "" : " · " + fonePrestador)}"
                + (c.SolicitanteNome is null ? "" : $"\nInquilino: {c.SolicitanteNome} {c.SolicitanteTelefone}"),
            Inicio = inicio, Fim = inicio.AddMinutes(form.DuracaoMinutos), ResponsavelId = form.ResponsavelId,
        });
        if (form.ChaveId is { } chave)
            await chaves.EntregarAsync(u, chave, new EntregaChaveForm
            {
                PessoaId = c.PrestadorId, Motivo = "Manutenção / obra", MotivoDetalhe = $"{c.Codigo} · {c.Titulo}",
                DevolverAte = form.DevolverChaveAte ?? inicio.AddMinutes(form.DuracaoMinutos).Date.AddHours(18),
            });

        await using var db2 = await fabrica.CreateDbContextAsync();
        var ch = await db2.ChamadosManutencao.SingleAsync(x => x.Id == id);
        ch.Status = StatusChamado.AGENDADO;
        ch.AgendadoPara = DatasBR.ParaUtc(inicio);
        ch.CompromissoId = compromisso;
        ch.AtualizadoEm = DateTime.UtcNow;
        Evento(db2, id, $"Agendado para {inicio:dd/MM/yyyy HH:mm} com {prestador}{(form.ChaveId is null ? "" : "; chave entregue ao prestador")}.", u.Id);
        await db2.SaveChangesAsync();

        var quando = $"{inicio:dd/MM} às {inicio:HH:mm}";
        var inquilino = await FoneInquilino(db2, ch);
        return new AvisosAgendamento(
            Whatsapp(fonePrestador, $"Olá, {PrimeiroNome(prestador)}! Confirmando o serviço {c.Codigo} ({c.Titulo}) no imóvel {imovel}, {endereco}, em {quando}. Inova Bens."),
            Whatsapp(inquilino, $"Olá! Aqui é da Inova Bens. O reparo \"{c.Titulo}\" foi agendado para {quando} com {prestador}. Qualquer imprevisto, avise por aqui."));
    }

    private static async Task<string?> FoneInquilino(InovabensDb db, ChamadoManutencao c) =>
        c.SolicitanteTelefone ?? (c.ContratoId is { } cid
            ? await db.ContratoLocacaoPartes.AsNoTracking().Where(p => p.ContratoId == cid && p.Papel == PapelContratoLocacao.LOCATARIO)
                .Select(p => p.Pessoa!.Whatsapp ?? p.Pessoa.Telefone).FirstOrDefaultAsync()
            : null);

    /* ------------------------------ conclusão e custo ------------------------------ */

    /// <summary>
    /// Conclui e lança o custo. Quem paga o prestador: a imobiliária (REPASSE, FATURA, IMOBILIARIA em contrato de
    /// administração ou sem contrato) ou o próprio imóvel (IMOVEL, e FATURA em imóvel próprio), como conta a pagar no
    /// vencimento informado. REPASSE fica pendente e sai dos próximos repasses; FATURA vira item na próxima fatura aberta.
    /// </summary>
    public async Task ConcluirAsync(UsuarioAtual u, Guid id, ConclusaoForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var c = await Chamado(db, u, id);
        if (c.Status is not (StatusChamado.APROVADO or StatusChamado.AGENDADO))
            throw new RegraNegocioException("_", "Conclua depois da aprovação do orçamento.");
        var contrato = c.ContratoId is { } cid ? await db.ContratosLocacao.SingleAsync(x => x.Id == cid) : null;
        var cobranca = form.Cobranca!.Value;
        var valor = form.ValorFinal!.Value;
        ValidarCobranca(cobranca, contrato);
        var prestador = c.PrestadorId is { } pid ? await db.Pessoas.AsNoTracking().Where(p => p.Id == pid).Select(p => p.NomeRazao).SingleOrDefaultAsync() : null;
        var desc = $"Manutenção {c.Codigo} — {c.Titulo}";
        if (desc.Length > 200) desc = desc[..200];
        var venc = form.VencimentoPrestador ?? form.Data!.Value;
        var custo = await LancarCustoAsync(db, audit, u, c.ImovelId, contrato, cobranca, valor, form.Data!.Value, venc, desc,
            c.PrestadorId, prestador, Texto.Limpo(form.Observacao), CategoriaManutencaoImobiliaria);
        (c.LancamentoEmpresaId, c.LancamentoImovelId, c.FaturaItemId) = (custo.LancamentoEmpresaId, custo.LancamentoImovelId, custo.FaturaItemId);

        c.Status = StatusChamado.CONCLUIDO;
        c.ConcluidoEm = form.Data;
        c.ValorFinal = valor;
        c.Cobranca = cobranca;
        c.VencimentoPrestador = cobranca == CobrancaManutencao.DIRETO ? null : venc;
        c.Avaliacao = form.Avaliacao;
        c.AtualizadoEm = DateTime.UtcNow;
        var onde = cobranca switch
        {
            CobrancaManutencao.REPASSE => "descontado dos próximos repasses do locador",
            CobrancaManutencao.FATURA => "cobrado do inquilino na próxima fatura",
            CobrancaManutencao.IMOVEL => "despesa do imóvel",
            CobrancaManutencao.IMOBILIARIA => "despesa da imobiliária",
            _ => "pago direto ao prestador (sem lançamento)",
        };
        Evento(db, id, $"Concluído em {Texto.Data(form.Data!.Value)}: {Texto.Moeda(valor)}, {onde}."
            + (form.Avaliacao is { } a ? $" Avaliação do prestador: {a}/5." : "") + (Texto.Limpo(form.Observacao) is { } ob ? $" {ob}" : ""), u.Id);
        audit.Registrar(db, u.Id, "chamados_manutencao", id, "CONCLUIR", depois: new { valor, cobranca, venc });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task CancelarAsync(UsuarioAtual u, Guid id, string? motivo)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await Chamado(db, u, id);
        if (!c.Aberto) throw new RegraNegocioException("_", "O chamado já está encerrado.");
        c.Status = StatusChamado.CANCELADO;
        c.Motivo = Texto.Limpo(motivo) ?? throw new RegraNegocioException(nameof(motivo), "Informe o motivo do cancelamento.");
        c.AtualizadoEm = DateTime.UtcNow;
        Evento(db, id, $"Cancelado: {c.Motivo}", u.Id);
        audit.Registrar(db, u.Id, "chamados_manutencao", id, "CANCELAR", depois: new { c.Motivo });
        await db.SaveChangesAsync();
    }

    /* ------------------------------ custo (manutenção e vistoria) ------------------------------ */

    /// <summary>Formas de lançar o custo que dependem do contrato.</summary>
    internal static void ValidarCobranca(CobrancaManutencao cobranca, ContratoLocacao? contrato)
    {
        switch (cobranca)
        {
            case CobrancaManutencao.REPASSE when contrato?.Modalidade != ModalidadeLocacao.ADMINISTRACAO:
                throw new RegraNegocioException("Cobranca", "Desconto no repasse só em contrato de administração.");
            case CobrancaManutencao.FATURA when contrato?.Status != StatusContratoLocacao.ATIVO:
                throw new RegraNegocioException("Cobranca", "Cobrança na fatura só com contrato ativo.");
        }
    }

    /// <summary>
    /// Lança o custo de um serviço (conserto ou vistoria). Quem paga o prestador: a imobiliária (conta a pagar no Financeiro;
    /// REPASSE e FATURA nas categorias de terceiros, IMOBILIARIA como despesa) ou o imóvel (IMOVEL, e qualquer forma em imóvel
    /// próprio). FATURA também vira item MANUTENCAO na próxima fatura aberta, que volta como reembolso fora do repasse.
    /// </summary>
    internal static async Task<CustoLancado> LancarCustoAsync(InovabensDb db, AuditService audit, UsuarioAtual u, Guid imovelId,
        ContratoLocacao? contrato, CobrancaManutencao cobranca, decimal valor, DateOnly data, DateOnly venc, string desc, Guid? pessoaId,
        string? pessoaNome, string? obs, string categoriaImobiliaria)
    {
        if (valor <= 0 || cobranca == CobrancaManutencao.DIRETO) return new(null, null, null);
        Guid? empresa = null, doImovel = null, item = null;
        if (cobranca == CobrancaManutencao.IMOVEL || contrato?.Modalidade == ModalidadeLocacao.PROPRIO)
        {
            // imóvel próprio/sócios: despesa no financeiro do imóvel (entra no retorno do investimento)
            var conta = await db.ContasOrcamentarias.Where(x => x.Nome == ContaManutencaoImovel && x.Tipo == TipoConta.DESPESA)
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync()
                ?? throw new RegraNegocioException("_", $"Crie a conta de despesa \"{ContaManutencaoImovel}\" no financeiro dos imóveis.");
            var l = new LancamentoFinanceiro
            {
                ImovelId = imovelId, Situacao = SituacaoLancamento.ABERTO, Data = venc, Valor = valor, Descricao = desc, ContaId = conta,
                PessoaId = pessoaId, PessoaNome = pessoaNome, Competencia = CalculoLocacao.MesDe(data), CriadoPor = u.Id, AtualizadoPor = u.Id,
            };
            l.Numero = await LancamentosFinanceirosService.ProximoNumero(db, imovelId);
            db.LancamentosFinanceiros.Add(l);
            audit.Registrar(db, u.Id, "lancamentos_financeiros", l.Id, "CRIAR", depois: l);
            doImovel = l.Id;
        }
        else
        {
            await MesesFechados.Garantir(db, venc);
            var (cat, grupo) = cobranca switch
            {
                CobrancaManutencao.REPASSE => (CategoriaPagoPeloProprietario, GrupoCategoriaEmpresa.TERCEIROS),
                CobrancaManutencao.FATURA => (CategoriaPagoPeloInquilino, GrupoCategoriaEmpresa.TERCEIROS),
                _ => (categoriaImobiliaria, GrupoCategoriaEmpresa.DESPESA),
            };
            var l = new LancamentoEmpresa
            {
                Descricao = desc, Valor = -valor, Vencimento = venc, Competencia = CalculoLocacao.MesDe(venc),
                CategoriaId = await LocacaoService.Categoria(db, cat, TipoCategoriaEmpresa.SAIDA, grupo),
                PessoaId = pessoaId, PessoaNome = pessoaNome, ImovelId = imovelId, Documento = "Nota/recibo do prestador",
                Observacao = obs, CriadoPor = u.Id, AtualizadoPor = u.Id,
            };
            db.LancamentosEmpresa.Add(l);
            audit.Registrar(db, u.Id, "lancamentos_empresa", l.Id, "CRIAR", depois: l);
            empresa = l.Id;
        }
        if (cobranca == CobrancaManutencao.FATURA)
        {
            var f = await db.FaturasLocacao.Include(x => x.Itens)
                .Where(x => x.ContratoId == contrato!.Id && x.Status == StatusFaturaLocacao.ABERTA && !x.Rescisao && x.Vencimento >= data)
                .OrderBy(x => x.Vencimento).FirstOrDefaultAsync()
                ?? throw new RegraNegocioException("Cobranca", "Não há fatura em aberto a vencer: gere a próxima fatura do contrato e tente de novo.");
            var it = new FaturaLocacaoItem
            {
                FaturaId = f.Id, Tipo = TipoItemFatura.MANUTENCAO, Descricao = desc, Valor = valor,
                Ordem = f.Itens.Count == 0 ? 1 : f.Itens.Max(i => i.Ordem) + 1,
            };
            db.FaturaLocacaoItens.Add(it);
            audit.Registrar(db, u.Id, "faturas_locacao", f.Id, "EDITAR", depois: new { item = it.Descricao, it.Valor, tipo = it.Tipo });
            item = it.Id;
        }
        return new(empresa, doImovel, item);
    }

    /// <summary>Custo por conta do locador (conserto ou vistoria) ainda não descontado do repasse; estornos não contam.</summary>
    internal static async Task<IReadOnlyList<ManutencaoPendente>> PendentesDeAbaterAsync(InovabensDb db, Guid contratoId)
    {
        var chamados = await db.ChamadosManutencao.Where(m => m.ContratoId == contratoId && m.Status == StatusChamado.CONCLUIDO
                && m.Cobranca == CobrancaManutencao.REPASSE && m.ValorFinal > 0)
            .OrderBy(m => m.ConcluidoEm).ThenBy(m => m.Numero)
            .Select(m => new
            {
                m.Id, Valor = m.ValorFinal!.Value,
                Abatido = db.ManutencaoAbatimentos.Where(a => a.ChamadoId == m.Id
                    && db.FaturaLocacaoPagamentos.Any(p => p.Id == a.PagamentoId && p.EstornadoEm == null)).Sum(a => (decimal?)a.Valor) ?? 0,
            }).ToListAsync();
        var vistorias = await db.Vistorias.Where(v => v.ContratoId == contratoId && v.Status == StatusVistoria.REALIZADA
                && v.Cobranca == CobrancaManutencao.REPASSE && v.Valor > 0)
            .OrderBy(v => v.RealizadaEm).ThenBy(v => v.Numero)
            .Select(v => new
            {
                v.Id, Valor = v.Valor!.Value,
                Abatido = db.ManutencaoAbatimentos.Where(a => a.VistoriaId == v.Id
                    && db.FaturaLocacaoPagamentos.Any(p => p.Id == a.PagamentoId && p.EstornadoEm == null)).Sum(a => (decimal?)a.Valor) ?? 0,
            }).ToListAsync();
        return [.. chamados.Where(x => x.Valor > x.Abatido).Select(x => new ManutencaoPendente(x.Id, x.Valor - x.Abatido)),
            .. vistorias.Where(x => x.Valor > x.Abatido).Select(x => new ManutencaoPendente(null, x.Valor - x.Abatido, x.Id))];
    }

    private static Task<decimal> Abatido(InovabensDb db, Guid chamadoId) =>
        db.ManutencaoAbatimentos.Where(a => a.ChamadoId == chamadoId && db.FaturaLocacaoPagamentos.Any(p => p.Id == a.PagamentoId && p.EstornadoEm == null))
            .SumAsync(a => (decimal?)a.Valor).ContinueWith(t => t.Result ?? 0);

    /* ------------------------------ arquivos (Drive) ------------------------------ */

    public bool DriveHabilitado => armazenamento.Habilitado;

    public async Task EnviarArquivoAsync(UsuarioAtual u, Guid id, string nome, byte[] conteudo, string contentType, TipoArquivoChamado tipo)
    {
        u.Exigir(P.LocacaoEditar);
        if (conteudo.Length == 0) throw new RegraNegocioException("arquivo", "Arquivo vazio.");
        if (conteudo.Length > ImoveisService.TamanhoMaximoArquivo) throw new RegraNegocioException("arquivo", "Arquivo acima de 25 MB.");
        if (!armazenamento.Habilitado) throw new ServicoIndisponivelException("Google Drive não configurado.");
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await Chamado(db, u, id);
        await Guardar(db, c, Path.GetFileName(nome), conteudo, contentType, tipo, false);
        Evento(db, id, $"Arquivo enviado ({Texto.Rotulo(tipo).ToLowerInvariant()}): {Path.GetFileName(nome)}.", u.Id);
        await db.SaveChangesAsync();
    }

    private async Task<ChamadoArquivo> Guardar(InovabensDb db, ChamadoManutencao c, string nome, byte[] conteudo, string tipoMime,
        TipoArquivoChamado tipo, bool inquilino, string? pastaId = null)
    {
        var pasta = pastaId ?? await Pasta(db, c);
        var driveId = await PastasDriveService.Chamar(async () =>
        {
            using var s = new MemoryStream(conteudo);
            return await armazenamento.EnviarAsync(pasta, nome, s, tipoMime);
        });
        var a = new ChamadoArquivo { ChamadoId = c.Id, Nome = nome, DriveId = driveId, Tipo = tipo, PeloInquilino = inquilino };
        db.ChamadoArquivos.Add(a);
        return a;
    }

    /// <summary>
    /// Sobe o arquivo da cotação de um orçamento para a subpasta "Cotações" da pasta do chamado, com nome padronizado
    /// ("Cotação 1 - Prestador - R$ 480,00.pdf"). Enviar de novo troca a cotação ligada ao orçamento (a anterior fica no Drive).
    /// </summary>
    public async Task AnexarCotacaoAsync(UsuarioAtual u, Guid orcamentoId, string nome, byte[] conteudo, string contentType)
    {
        u.Exigir(P.LocacaoEditar);
        if (conteudo.Length == 0) throw new RegraNegocioException("arquivo", "Arquivo vazio.");
        if (conteudo.Length > ImoveisService.TamanhoMaximoArquivo) throw new RegraNegocioException("arquivo", "Arquivo acima de 25 MB.");
        if (!armazenamento.Habilitado) throw new ServicoIndisponivelException("Google Drive não configurado.");
        await using var db = await fabrica.CreateDbContextAsync();
        var o = await db.OrcamentosManutencao.Include(x => x.Prestador).SingleOrDefaultAsync(x => x.Id == orcamentoId)
            ?? throw new NaoEncontradoException("Orçamento não encontrado.");
        var c = await Chamado(db, u, o.ChamadoId);
        if (!c.Aberto) throw new RegraNegocioException("_", "Chamado encerrado.");
        var ordem = await db.OrcamentosManutencao.CountAsync(x => x.ChamadoId == c.Id && x.CriadoEm <= o.CriadoEm);
        var pastaChamado = await Pasta(db, c);
        var pasta = await PastasDriveService.Chamar(async () =>
            await armazenamento.BuscarPastaAsync(SubpastaCotacoes, pastaChamado) ?? await armazenamento.CriarPastaAsync(SubpastaCotacoes, pastaChamado));
        var ext = Path.GetExtension(Path.GetFileName(nome));
        var arquivo = $"Cotação {ordem} - {o.Prestador!.NomeRazao} - {Texto.Moeda(o.Valor)}".Replace('/', '-').Replace('\\', '-');
        if (arquivo.Length > 150) arquivo = arquivo[..150];
        var a = await Guardar(db, c, arquivo + ext, conteudo, contentType, TipoArquivoChamado.ORCAMENTO, false, pasta);
        o.ArquivoId = a.Id;
        c.AtualizadoEm = DateTime.UtcNow;
        Evento(db, c.Id, $"Cotação de {o.Prestador.NomeRazao} anexada ({a.Nome}).", u.Id);
        await db.SaveChangesAsync();
    }

    /// <summary>Arquivo da cotação para o proprietário, pelo link de aprovação. Null = link ou cotação inexistente.</summary>
    public async Task<CotacaoArquivo?> CotacaoPublicaAsync(string token, Guid orcamentoId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var a = await db.OrcamentosManutencao.AsNoTracking()
            .Where(o => o.Id == orcamentoId && db.ChamadosManutencao.Any(c => c.Id == o.ChamadoId && c.AprovacaoToken == token))
            .Join(db.ChamadoArquivos, o => o.ArquivoId, x => x.Id, (o, x) => new { x.Nome, x.DriveId })
            .SingleOrDefaultAsync(ct);
        if (a is null) return null;
        var conteudo = await PastasDriveService.Chamar(() => armazenamento.BaixarAsync(a.DriveId, ct));
        return conteudo is null ? null : new CotacaoArquivo(a.Nome, conteudo.Dados, conteudo.Tipo);
    }

    /// <summary>Pasta "Manutenção MAN-0001 - título" dentro da pasta do contrato (ou na pasta do imóvel, sem contrato).</summary>
    private async Task<string> Pasta(InovabensDb db, ChamadoManutencao c)
    {
        if (c.DrivePastaId is { } p) return p;
        var pai = c.ContratoId is { } cid ? await db.ContratosLocacao.Where(x => x.Id == cid).Select(x => x.DrivePastaId).SingleOrDefaultAsync() : null;
        pai ??= await pastas.GarantirAsync(c.ImovelId);
        var nome = $"Manutenção {c.Codigo} - {c.Titulo}".Replace('/', '-').Replace('\\', '-');
        if (nome.Length > 120) nome = nome[..120];
        c.DrivePastaId = await PastasDriveService.Chamar(async () =>
            await armazenamento.BuscarPastaAsync(nome, pai) ?? await armazenamento.CriarPastaAsync(nome, pai));
        return c.DrivePastaId;
    }

    /* ------------------------------ link do inquilino (sem login) ------------------------------ */

    /// <summary>Link fixo do contrato para o inquilino abrir chamado (criado na primeira vez) e a mensagem de WhatsApp.</summary>
    public async Task<LinkInquilino> LinkInquilinoAsync(UsuarioAtual u, Guid contratoId)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await db.ContratosLocacao.SingleOrDefaultAsync(x => x.Id == contratoId && db.Imoveis.VisiveisPara(u).Any(i => i.Id == x.ImovelId))
            ?? throw new NaoEncontradoException("Contrato não encontrado.");
        if (c.TokenChamado is null)
        {
            c.TokenChamado = NovoToken();
            audit.Registrar(db, u.Id, "contratos_locacao", c.Id, "LINK_CHAMADO");
            await db.SaveChangesAsync();
        }
        var link = Url($"chamado/{c.TokenChamado}");
        var inquilino = await db.ContratoLocacaoPartes.AsNoTracking().Where(p => p.ContratoId == c.Id && p.Papel == PapelContratoLocacao.LOCATARIO)
            .Select(p => new { p.Pessoa!.NomeRazao, Fone = p.Pessoa.Whatsapp ?? p.Pessoa.Telefone }).FirstOrDefaultAsync();
        return new LinkInquilino(link, Whatsapp(inquilino?.Fone,
            $"Olá{(inquilino is null ? "" : ", " + PrimeiroNome(inquilino.NomeRazao))}! Aqui é da Inova Bens. Precisando de algum reparo no imóvel, " +
            $"abra o pedido por este link (pode mandar fotos): {link}"));
    }

    public async Task<ContextoChamadoPublico?> ContextoInquilinoAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var c = await db.ContratosLocacao.AsNoTracking().Include(x => x.Imovel)
            .SingleOrDefaultAsync(x => x.TokenChamado == token && x.Status == StatusContratoLocacao.ATIVO, ct);
        if (c is null) return null;
        var abertos = await db.ChamadosManutencao.AsNoTracking()
            .Where(m => m.ContratoId == c.Id && m.Status != StatusChamado.CONCLUIDO && m.Status != StatusChamado.CANCELADO && m.Status != StatusChamado.RECUSADO)
            .OrderBy(m => m.Numero).Select(m => new { m.Numero, m.Titulo, m.Status }).ToListAsync(ct);
        return new ContextoChamadoPublico(Endereco(c.Imovel!), c.Codigo, [.. abertos.Select(a => (ChamadoManutencao.CodigoDe(a.Numero), a.Titulo, a.Status))]);
    }

    /// <summary>Inquilino abre o chamado pelo link (com até 5 fotos). Avisa a equipe no sino. Null = link inválido.</summary>
    public async Task<string?> AbrirPeloInquilinoAsync(string token, AberturaInquilinoForm form,
        IReadOnlyList<(string Nome, byte[] Conteudo, string Tipo)> fotos, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;
        if (!string.IsNullOrEmpty(form.Site)) return "";
        Validacao.Validar(form);
        if (fotos.Count > MaxFotosInquilino) throw new RegraNegocioException("fotos", $"Envie até {MaxFotosInquilino} fotos.");
        if (fotos.Any(f => f.Conteudo.Length > TamanhoMaximoFoto)) throw new RegraNegocioException("fotos", "Cada foto pode ter até 10 MB.");
        if (fotos.Any(f => !f.Tipo.StartsWith("image/", StringComparison.OrdinalIgnoreCase))) throw new RegraNegocioException("fotos", "Envie só fotos (imagens).");
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var contrato = await db.ContratosLocacao.AsNoTracking().SingleOrDefaultAsync(x => x.TokenChamado == token && x.Status == StatusContratoLocacao.ATIVO, ct);
        if (contrato is null) return null;
        var c = new ChamadoManutencao
        {
            ImovelId = contrato.ImovelId, ContratoId = contrato.Id, Origem = OrigemChamado.INQUILINO, Titulo = form.Titulo.Trim(),
            Descricao = Texto.Limpo(form.Descricao), Categoria = form.Categoria, Urgencia = form.Urgencia,
            Responsavel = ResponsavelSugerido(form.Categoria), SolicitanteNome = form.Nome.Trim(),
            SolicitanteTelefone = Formatos.TelefoneFormatado(form.Telefone),
        };
        db.ChamadosManutencao.Add(c);
        Evento(db, c.Id, $"Chamado aberto pelo inquilino ({c.SolicitanteNome}, {c.SolicitanteTelefone}) pelo link do contrato.", null);
        await db.SaveChangesAsync(ct);
        if (fotos.Count > 0 && armazenamento.Habilitado)
        {
            try
            {
                foreach (var (nome, conteudo, tipo) in fotos) await Guardar(db, c, Path.GetFileName(nome), conteudo, tipo, TipoArquivoChamado.FOTO, true);
                Evento(db, c.Id, $"{fotos.Count} foto(s) enviada(s) pelo inquilino.", null);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning("Fotos do chamado {Chamado} não foram para o Drive: {Erro}", c.Codigo, e.Message);
                Evento(db, c.Id, "As fotos enviadas pelo inquilino não puderam ser guardadas no Drive; peça de novo pelo WhatsApp.", null);
            }
        }
        await AlertarEquipe(db, c, $"{c.Codigo}: novo chamado do inquilino ({contrato.Codigo}) — {c.Titulo}"
            + (c.Urgencia >= UrgenciaManutencao.ALTA ? $" [{Texto.Rotulo(c.Urgencia).ToUpperInvariant()}]" : ""), ct);
        await db.SaveChangesAsync(ct);
        return c.Codigo;
    }

    /* ------------------------------ preventivas ------------------------------ */

    public async Task<IReadOnlyList<PreventivaLinha>> PreventivasAsync(UsuarioAtual u, Guid? imovelId = null)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var q = db.ManutencoesPreventivas.AsNoTracking().Where(p => db.Imoveis.VisiveisPara(u).Any(i => i.Id == p.ImovelId));
        if (imovelId is { } im) q = q.Where(p => p.ImovelId == im);
        return await q.OrderByDescending(p => p.Ativa).ThenBy(p => p.ProximaEm)
            .Select(p => new PreventivaLinha(p.Id, p.ImovelId, p.Imovel!.Codigo, p.Titulo, p.Categoria, p.Responsavel, p.PeriodicidadeMeses, p.ProximaEm,
                p.PrestadorId, db.Pessoas.Where(x => x.Id == p.PrestadorId).Select(x => x.NomeRazao).FirstOrDefault(), p.Observacao, p.Ativa))
            .ToListAsync();
    }

    public async Task<Guid> SalvarPreventivaAsync(UsuarioAtual u, Guid? id, PreventivaForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        if (!await db.Imoveis.VisiveisPara(u).AnyAsync(i => i.Id == form.ImovelId)) throw new NaoEncontradoException("Imóvel não encontrado.");
        var p = id is { } pid
            ? await db.ManutencoesPreventivas.SingleOrDefaultAsync(x => x.Id == pid) ?? throw new NaoEncontradoException("Preventiva não encontrada.")
            : db.ManutencoesPreventivas.Add(new ManutencaoPreventiva { CriadoPor = u.Id }).Entity;
        p.ImovelId = form.ImovelId!.Value;
        p.Titulo = form.Titulo.Trim();
        p.Categoria = form.Categoria;
        p.Responsavel = form.Responsavel;
        p.PeriodicidadeMeses = form.PeriodicidadeMeses;
        p.ProximaEm = form.ProximaEm!.Value;
        p.PrestadorId = form.PrestadorId;
        p.Observacao = Texto.Limpo(form.Observacao);
        p.Ativa = form.Ativa;
        audit.Registrar(db, u.Id, "manutencoes_preventivas", p.Id, id is null ? "CRIAR" : "ATUALIZAR", depois: p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    /// <summary>
    /// Rotina diária: preventiva ativa a até 30 dias da data, sem chamado aberto dela, ganha o chamado (com o prestador
    /// habitual) e a próxima data avança um período. Avisa a equipe no sino.
    /// </summary>
    public async Task<int> GerarPreventivasAsync(DateOnly? hoje = null, CancellationToken ct = default)
    {
        var dia = hoje ?? DatasBR.Hoje();
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var devidas = await db.ManutencoesPreventivas.Include(p => p.Imovel)
            .Where(p => p.Ativa && p.ProximaEm <= dia.AddDays(DiasAntecedenciaPreventiva)
                && !db.ChamadosManutencao.Any(c => c.PreventivaId == p.Id && c.Status != StatusChamado.CONCLUIDO
                    && c.Status != StatusChamado.CANCELADO && c.Status != StatusChamado.RECUSADO))
            .ToListAsync(ct);
        foreach (var p in devidas)
        {
            var c = new ChamadoManutencao
            {
                ImovelId = p.ImovelId, PreventivaId = p.Id, Origem = OrigemChamado.PREVENTIVA, Titulo = $"{p.Titulo} (preventiva de {Texto.Data(p.ProximaEm)})",
                Descricao = p.Observacao, Categoria = p.Categoria, Urgencia = UrgenciaManutencao.BAIXA, Responsavel = p.Responsavel, PrestadorId = p.PrestadorId,
                ContratoId = await ContratoDoImovel(db, p.ImovelId, null),
            };
            if (c.Titulo.Length > 200) c.Titulo = c.Titulo[..200];
            db.ChamadosManutencao.Add(c);
            Evento(db, c.Id, $"Aberto pela rotina de manutenção preventiva (a cada {p.PeriodicidadeMeses} mês(es)).", null);
            do p.ProximaEm = p.ProximaEm.AddMonths(p.PeriodicidadeMeses); while (p.ProximaEm < dia);
            await db.SaveChangesAsync(ct);
            await AlertarEquipe(db, c, $"{c.Codigo}: manutenção preventiva do imóvel {p.Imovel!.Codigo} — {p.Titulo}", ct);
        }
        await db.SaveChangesAsync(ct);
        return devidas.Count;
    }

    /* ------------------------------ apoio ------------------------------ */

    private static async Task<ChamadoManutencao> Chamado(InovabensDb db, UsuarioAtual u, Guid id, bool rastrear = true)
    {
        var q = rastrear ? db.ChamadosManutencao : db.ChamadosManutencao.AsNoTracking();
        return await q.SingleOrDefaultAsync(c => c.Id == id && db.Imoveis.VisiveisPara(u).Any(i => i.Id == c.ImovelId))
            ?? throw new NaoEncontradoException("Chamado não encontrado.");
    }

    private static void Evento(InovabensDb db, Guid chamadoId, string descricao, Guid? usuario) =>
        db.ChamadoEventos.Add(new ChamadoEvento { ChamadoId = chamadoId, Descricao = descricao.Length > 2000 ? descricao[..2000] : descricao, UsuarioId = usuario });

    /// <summary>Alerta no sino de quem edita a locação.</summary>
    private static async Task AlertarEquipe(InovabensDb db, ChamadoManutencao c, string mensagem, CancellationToken ct)
    {
        var usuarios = await db.Usuarios.Where(x => x.Ativo && x.ExcluidoEm == null
                && db.PerfilPermissoes.Any(pp => pp.PerfilId == x.PerfilId && pp.Permissao!.Codigo == P.LocacaoEditar))
            .Select(x => x.Id).ToListAsync(ct);
        foreach (var uid in usuarios)
            db.Alertas.Add(new Alerta { Id = Guid.NewGuid(), Tipo = TipoAlerta.MANUTENCAO, UsuarioId = uid, ChamadoManutencaoId = c.Id, Mensagem = mensagem });
    }

    internal static string PrimeiroNome(string? nome) => (nome ?? "").Trim().Split(' ', 2)[0];

    internal static string? Whatsapp(string? fone, string texto) =>
        Texto.LinkWhatsapp(fone) is { } link ? $"{link}?text={Uri.EscapeDataString(texto)}" : null;
}
