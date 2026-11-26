using Inovabens.Core.Agenda;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Locacao;

/// <summary>
/// Vistoria terceirizada (locação, fase 4): a empresa contratada faz a vistoria e entrega o laudo; o sistema registra o
/// agendamento (agenda e chave), o laudo no Drive (subpasta "Vistorias" do contrato), as leituras dos medidores, o custo, o
/// prazo de contestação/aceite e as pendências do laudo, que viram chamado de manutenção ou débito na rescisão.
/// </summary>
public sealed class VistoriaService(IDbContextFactory<InovabensDb> fabrica, AuditService audit, AgendaService agenda, ChavesService chaves,
    LocacaoDocumentosService documentos, ManutencaoService manutencao, PastasDriveService pastas, IArmazenamento armazenamento)
{
    public const string SubpastaVistorias = "Vistorias";
    public const string CategoriaVistoriaImobiliaria = "Vistoria de imóveis";
    /// <summary>Prazo sugerido para o inquilino contestar o laudo (o contrato pode fixar outro).</summary>
    public const int DiasContestacao = 7;

    public bool DriveHabilitado => armazenamento.Habilitado;

    /* ------------------------------ consultas ------------------------------ */

    public async Task<IReadOnlyList<VistoriaLinha>> ListarAsync(UsuarioAtual u, Guid? contratoId = null, bool soAgendadas = false)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var q = db.Vistorias.AsNoTracking().Where(v => db.Imoveis.VisiveisPara(u).Any(i => i.Id == v.ImovelId));
        if (contratoId is { } cid) q = q.Where(v => v.ContratoId == cid);
        if (soAgendadas) q = q.Where(v => v.Status == StatusVistoria.AGENDADA
            || (v.Status == StatusVistoria.REALIZADA && db.PendenciasVistoria.Any(p => p.VistoriaId == v.Id && p.Status == StatusPendenciaVistoria.ABERTA)));
        var linhas = await q.OrderByDescending(v => v.AgendadaPara ?? v.CriadoEm).Take(300)
            .Select(v => new
            {
                v,
                Contrato = db.ContratosLocacao.Where(c => c.Id == v.ContratoId).Select(c => c.Numero).First(),
                Imovel = v.Imovel!.Codigo,
                Empresa = db.Pessoas.Where(p => p.Id == v.EmpresaId).Select(p => p.NomeRazao).FirstOrDefault(),
                Abertas = db.PendenciasVistoria.Count(p => p.VistoriaId == v.Id && p.Status == StatusPendenciaVistoria.ABERTA),
            }).ToListAsync();
        return [.. linhas.Select(x => new VistoriaLinha(x.v.Id, x.v.Codigo, x.v.Tipo, x.v.Status, x.v.ContratoId, ContratoLocacao.CodigoDe(x.Contrato),
            x.Imovel, x.Empresa, x.v.AgendadaPara, x.v.RealizadaEm, x.v.LaudoDriveId is not null, x.Abertas, x.v.PrazoContestacao, x.v.AceiteEm,
            x.v.ContestadaEm))];
    }

    public async Task<VistoriaDetalhe> ObterAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var v = await Vistoria(db, u, id, rastrear: false);
        var im = await db.Imoveis.AsNoTracking().SingleAsync(i => i.Id == v.ImovelId);
        var c = await db.ContratosLocacao.AsNoTracking().SingleAsync(x => x.Id == v.ContratoId);
        var empresa = v.EmpresaId is { } eid
            ? await db.Pessoas.AsNoTracking().Where(p => p.Id == eid).Select(p => new { p.NomeRazao, Fone = p.Whatsapp ?? p.Telefone }).SingleOrDefaultAsync()
            : null;
        var inquilino = await Locatario(db, c.Id);
        var anteriores = new List<VistoriaResumo>();
        foreach (var a in await db.Vistorias.AsNoTracking()
                     .Where(x => x.ContratoId == c.Id && x.Id != v.Id && x.Status == StatusVistoria.REALIZADA && x.RealizadaEm <= (v.RealizadaEm ?? DateOnly.MaxValue))
                     .OrderBy(x => x.RealizadaEm).ToListAsync())
            anteriores.Add(new VistoriaResumo(a.Id, a.Codigo, a.Tipo, a.RealizadaEm, a.LeituraAgua, a.LeituraLuz, a.LeituraGas, a.Chaves, a.LaudoNome,
                await Pendencias(db, a.Id)));
        var abatido = await db.ManutencaoAbatimentos.Where(a => a.VistoriaId == id
                && db.FaturaLocacaoPagamentos.Any(p => p.Id == a.PagamentoId && p.EstornadoEm == null))
            .SumAsync(a => (decimal?)a.Valor) ?? 0;
        return new VistoriaDetalhe(v, im.Codigo, ManutencaoService.Endereco(im), c.Codigo, c.Modalidade, c.Status, empresa?.NomeRazao, empresa?.Fone,
            inquilino?.Nome, inquilino?.Fone, await Pendencias(db, id), anteriores, abatido, documentos.LinkPasta(c));
    }

    private static async Task<IReadOnlyList<PendenciaLinha>> Pendencias(InovabensDb db, Guid vistoriaId) =>
        (await db.PendenciasVistoria.AsNoTracking().Where(p => p.VistoriaId == vistoriaId).OrderBy(p => p.CriadoEm)
            .Select(p => new { p, Chamado = db.ChamadosManutencao.Where(c => c.Id == p.ChamadoId).Select(c => (int?)c.Numero).FirstOrDefault() })
            .ToListAsync())
        .Select(x => new PendenciaLinha(x.p.Id, x.p.Comodo, x.p.Descricao, x.p.Responsavel, x.p.Valor, x.p.Status, x.p.ChamadoId,
            x.Chamado is { } n ? ChamadoManutencao.CodigoDe(n) : null)).ToList();

    private static async Task<(string Nome, string? Fone)?> Locatario(InovabensDb db, Guid contratoId)
    {
        var l = await db.ContratoLocacaoPartes.AsNoTracking().Where(p => p.ContratoId == contratoId && p.Papel == PapelContratoLocacao.LOCATARIO)
            .Select(p => new { p.Pessoa!.NomeRazao, Fone = p.Pessoa.Whatsapp ?? p.Pessoa.Telefone }).FirstOrDefaultAsync();
        return l is null ? null : (l.NomeRazao, l.Fone);
    }

    /* ------------------------------ cadastro ------------------------------ */

    public async Task<Guid> CriarAsync(UsuarioAtual u, VistoriaForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await db.ContratosLocacao.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == form.ContratoId && db.Imoveis.VisiveisPara(u).Any(i => i.Id == x.ImovelId))
            ?? throw new NaoEncontradoException("Contrato não encontrado.");
        if (c.Status is StatusContratoLocacao.CANCELADO)
            throw new RegraNegocioException(nameof(form.ContratoId), "Contrato cancelado.");
        if (form.Tipo == TipoVistoria.ENTRADA && await db.Vistorias.AnyAsync(x => x.ContratoId == c.Id && x.Tipo == TipoVistoria.ENTRADA
                && x.Status != StatusVistoria.CANCELADA))
            throw new RegraNegocioException(nameof(form.Tipo), "Este contrato já tem vistoria de entrada.");
        var v = new Vistoria { ImovelId = c.ImovelId, ContratoId = c.Id, CriadoPor = u.Id };
        Preencher(v, form);
        db.Vistorias.Add(v);
        audit.Registrar(db, u.Id, "vistorias", v.Id, "CRIAR", depois: v);
        await db.SaveChangesAsync();
        return v.Id;
    }

    public async Task AtualizarAsync(UsuarioAtual u, Guid id, VistoriaForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var v = await Vistoria(db, u, id);
        if (v.Status == StatusVistoria.CANCELADA) throw new RegraNegocioException("_", "Vistoria cancelada.");
        Preencher(v, form);
        v.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "vistorias", v.Id, "ATUALIZAR", depois: new { v.Tipo, v.EmpresaId, v.Vistoriador });
        await db.SaveChangesAsync();
    }

    private static void Preencher(Vistoria v, VistoriaForm f)
    {
        v.Tipo = f.Tipo;
        v.EmpresaId = f.EmpresaId;
        v.Vistoriador = Texto.Limpo(f.Vistoriador);
        v.Observacao = Texto.Limpo(f.Observacao);
    }

    public async Task<VistoriaForm> FormularioAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var v = await Vistoria(db, u, id, rastrear: false);
        return new VistoriaForm { ContratoId = v.ContratoId, Tipo = v.Tipo, EmpresaId = v.EmpresaId, Vistoriador = v.Vistoriador, Observacao = v.Observacao };
    }

    public async Task CancelarAsync(UsuarioAtual u, Guid id, string? motivo)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var v = await Vistoria(db, u, id);
        if (v.Status != StatusVistoria.AGENDADA) throw new RegraNegocioException("_", "Só vistoria ainda não realizada pode ser cancelada.");
        v.Status = StatusVistoria.CANCELADA;
        v.Motivo = Texto.Limpo(motivo) ?? throw new RegraNegocioException(nameof(motivo), "Informe o motivo do cancelamento.");
        v.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "vistorias", v.Id, "CANCELAR", depois: new { v.Motivo });
        await db.SaveChangesAsync();
    }

    /* ------------------------------ agenda ------------------------------ */

    /// <summary>Agenda com a empresa (compromisso na agenda), entrega a chave se escolhida e prepara os avisos no WhatsApp.</summary>
    public async Task<AvisosAgendamento> AgendarAsync(UsuarioAtual u, Guid id, AgendamentoVistoriaForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        Vistoria v;
        string? empresa, foneEmpresa, imovel, endereco, contrato;
        (string Nome, string? Fone)? inquilino;
        await using (var db = await fabrica.CreateDbContextAsync())
        {
            v = await Vistoria(db, u, id, rastrear: false);
            if (v.Status != StatusVistoria.AGENDADA) throw new RegraNegocioException("_", "A vistoria já foi realizada ou cancelada.");
            if (form.ChaveId is not null && v.EmpresaId is null)
                throw new RegraNegocioException(nameof(form.ChaveId), "Escolha a empresa de vistoria para registrar a entrega da chave.");
            var e = v.EmpresaId is { } eid
                ? await db.Pessoas.AsNoTracking().Where(x => x.Id == eid).Select(x => new { x.NomeRazao, Fone = x.Whatsapp ?? x.Telefone }).SingleOrDefaultAsync()
                : null;
            (empresa, foneEmpresa) = (e?.NomeRazao ?? v.Vistoriador, e?.Fone);
            var im = await db.Imoveis.AsNoTracking().SingleAsync(i => i.Id == v.ImovelId);
            (imovel, endereco) = (im.Codigo, ManutencaoService.Endereco(im));
            contrato = (await db.ContratosLocacao.AsNoTracking().SingleAsync(x => x.Id == v.ContratoId)).Codigo;
            inquilino = await Locatario(db, v.ContratoId);
        }
        var inicio = form.Inicio!.Value;
        var tipo = Texto.Rotulo(v.Tipo).ToLowerInvariant();
        var compromisso = await agenda.CriarAsync(u, v.ImovelId, new CompromissoForm
        {
            Tipo = TipoCompromisso.TAREFA, Titulo = $"Vistoria de {tipo} {v.Codigo}{(empresa is null ? "" : " · " + empresa)}", Local = endereco,
            Descricao = $"Contrato {contrato}\nEmpresa: {empresa ?? "—"}{(foneEmpresa is null ? "" : " · " + foneEmpresa)}"
                + (inquilino is { } iq ? $"\nInquilino: {iq.Nome} {iq.Fone}" : ""),
            Inicio = inicio, Fim = inicio.AddMinutes(form.DuracaoMinutos), ResponsavelId = form.ResponsavelId,
        });
        if (form.ChaveId is { } chave)
            await chaves.EntregarAsync(u, chave, new EntregaChaveForm
            {
                PessoaId = v.EmpresaId, Motivo = "Vistoria", MotivoDetalhe = $"{v.Codigo} · vistoria de {tipo}",
                DevolverAte = form.DevolverChaveAte ?? inicio.AddMinutes(form.DuracaoMinutos).Date.AddHours(18),
            });

        await using var db2 = await fabrica.CreateDbContextAsync();
        var vv = await db2.Vistorias.SingleAsync(x => x.Id == id);
        vv.AgendadaPara = DatasBR.ParaUtc(inicio);
        vv.CompromissoId = compromisso;
        vv.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db2, u.Id, "vistorias", id, "AGENDAR", depois: new { inicio, form.ChaveId });
        await db2.SaveChangesAsync();

        var quando = $"{inicio:dd/MM} às {inicio:HH:mm}";
        return new AvisosAgendamento(
            ManutencaoService.Whatsapp(foneEmpresa, $"Olá! Aqui é da Inova Bens. Confirmando a vistoria de {tipo} do imóvel {imovel}, {endereco} " +
                $"(contrato {contrato}), em {quando}."),
            ManutencaoService.Whatsapp(inquilino?.Fone, $"Olá{(inquilino is { } i ? ", " + ManutencaoService.PrimeiroNome(i.Nome) : "")}! Aqui é da Inova Bens. " +
                $"A vistoria de {tipo} do imóvel foi agendada para {quando}{(empresa is null ? "" : $" com {empresa}")}. Qualquer imprevisto, avise por aqui."));
    }

    /* ------------------------------ realização, laudo e custo ------------------------------ */

    public async Task RegistrarRealizacaoAsync(UsuarioAtual u, Guid id, RealizacaoVistoriaForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var v = await Vistoria(db, u, id);
        if (v.Status == StatusVistoria.CANCELADA) throw new RegraNegocioException("_", "Vistoria cancelada.");
        var data = form.Data!.Value;
        if (data > DatasBR.Hoje()) throw new RegraNegocioException(nameof(form.Data), "A data não pode ser futura.");
        if (form.PrazoContestacao < data) throw new RegraNegocioException(nameof(form.PrazoContestacao), "O prazo não pode ser antes da vistoria.");
        v.Status = StatusVistoria.REALIZADA;
        v.RealizadaEm = data;
        v.LeituraAgua = Texto.Limpo(form.LeituraAgua);
        v.LeituraLuz = Texto.Limpo(form.LeituraLuz);
        v.LeituraGas = Texto.Limpo(form.LeituraGas);
        v.Chaves = form.Chaves;
        v.Vistoriador = Texto.Limpo(form.Vistoriador) ?? v.Vistoriador;
        v.PrazoContestacao = form.PrazoContestacao ?? data.AddDays(DiasContestacao);
        v.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "vistorias", v.Id, "REALIZAR", depois: new { data, v.LeituraAgua, v.LeituraLuz, v.LeituraGas, v.Chaves, v.PrazoContestacao });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Laudo da empresa (PDF) na subpasta "Vistorias" da pasta do contrato, com nome padronizado
    /// ("VIS-0001 - Laudo de entrada - 25-11-2026.pdf"). Enviar de novo troca o laudo ligado (o anterior fica no Drive).
    /// </summary>
    public async Task EnviarLaudoAsync(UsuarioAtual u, Guid id, string nome, byte[] conteudo, string contentType)
    {
        u.Exigir(P.LocacaoEditar);
        if (conteudo.Length == 0) throw new RegraNegocioException("arquivo", "Arquivo vazio.");
        if (conteudo.Length > ImoveisService.TamanhoMaximoArquivo) throw new RegraNegocioException("arquivo", "Arquivo acima de 25 MB.");
        if (!armazenamento.Habilitado) throw new ServicoIndisponivelException("Google Drive não configurado.");
        await using var db = await fabrica.CreateDbContextAsync();
        var v = await Vistoria(db, u, id);
        if (v.Status == StatusVistoria.CANCELADA) throw new RegraNegocioException("_", "Vistoria cancelada.");
        var pastaContrato = await documentos.GarantirPastaAsync(u, v.ContratoId);
        var pasta = await PastasDriveService.Chamar(async () =>
            await armazenamento.BuscarPastaAsync(SubpastaVistorias, pastaContrato) ?? await armazenamento.CriarPastaAsync(SubpastaVistorias, pastaContrato));
        var data = v.RealizadaEm ?? DatasBR.Hoje();
        var arquivo = $"{v.Codigo} - Laudo de {Texto.Rotulo(v.Tipo).ToLowerInvariant()} - {data:dd-MM-yyyy}{Path.GetExtension(Path.GetFileName(nome))}";
        v.LaudoDriveId = await PastasDriveService.Chamar(async () =>
        {
            using var s = new MemoryStream(conteudo);
            return await armazenamento.EnviarAsync(pasta, arquivo, s, contentType);
        });
        v.LaudoNome = arquivo;
        v.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "vistorias", v.Id, "LAUDO", depois: new { arquivo });
        await db.SaveChangesAsync();
    }

    public async Task<Relatorios.Arquivo> LaudoAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var v = await Vistoria(db, u, id, rastrear: false);
        if (v.LaudoDriveId is null) throw new NaoEncontradoException("Laudo não enviado.");
        var c = await PastasDriveService.Chamar(() => armazenamento.BaixarAsync(v.LaudoDriveId))
            ?? throw new NaoEncontradoException("Laudo não encontrado no Drive.");
        return new Relatorios.Arquivo(c.Dados, v.LaudoNome ?? "laudo.pdf", c.Tipo);
    }

    /// <summary>Custo da vistoria, lançado uma vez (mesmas formas da manutenção: repasse, fatura, imóvel, imobiliária ou direto).</summary>
    public async Task LancarCustoAsync(UsuarioAtual u, Guid id, CustoVistoriaForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var v = await Vistoria(db, u, id);
        if (v.Status != StatusVistoria.REALIZADA) throw new RegraNegocioException("_", "Registre a realização da vistoria antes do custo.");
        if (v.Cobranca is not null) throw new RegraNegocioException("_", "O custo desta vistoria já foi lançado.");
        var contrato = await db.ContratosLocacao.SingleAsync(x => x.Id == v.ContratoId);
        var cobranca = form.Cobranca!.Value;
        ManutencaoService.ValidarCobranca(cobranca, contrato);
        var empresa = v.EmpresaId is { } eid ? await db.Pessoas.AsNoTracking().Where(p => p.Id == eid).Select(p => p.NomeRazao).SingleOrDefaultAsync() : null;
        var desc = $"Vistoria de {Texto.Rotulo(v.Tipo).ToLowerInvariant()} {v.Codigo} — {contrato.Codigo}";
        var data = v.RealizadaEm!.Value;
        var custo = await ManutencaoService.LancarCustoAsync(db, audit, u, v.ImovelId, contrato, cobranca, form.Valor!.Value, data,
            form.Vencimento ?? data, desc, v.EmpresaId, empresa, null, CategoriaVistoriaImobiliaria);
        (v.LancamentoEmpresaId, v.LancamentoImovelId, v.FaturaItemId) = (custo.LancamentoEmpresaId, custo.LancamentoImovelId, custo.FaturaItemId);
        v.Valor = form.Valor;
        v.Cobranca = cobranca;
        v.VencimentoPrestador = cobranca == CobrancaManutencao.DIRETO ? null : form.Vencimento ?? data;
        v.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "vistorias", v.Id, "CUSTO", depois: new { v.Valor, cobranca });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /* ------------------------------ aceite e contestação ------------------------------ */

    public async Task RegistrarAceiteAsync(UsuarioAtual u, Guid id, DateOnly data)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var v = await Vistoria(db, u, id);
        if (v.Status != StatusVistoria.REALIZADA) throw new RegraNegocioException("_", "Registre a realização da vistoria antes.");
        v.AceiteEm = data;
        v.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "vistorias", v.Id, "ACEITE", depois: new { data });
        await db.SaveChangesAsync();
    }

    public async Task RegistrarContestacaoAsync(UsuarioAtual u, Guid id, DateOnly data, string? texto)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var v = await Vistoria(db, u, id);
        if (v.Status != StatusVistoria.REALIZADA) throw new RegraNegocioException("_", "Registre a realização da vistoria antes.");
        var t = Texto.Limpo(texto) ?? throw new RegraNegocioException("contestacao", "Descreva o que o inquilino contestou.");
        v.Contestacao = t.Length > 2000 ? t[..2000] : t;
        v.ContestadaEm = data;
        v.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "vistorias", v.Id, "CONTESTACAO", depois: new { data, v.Contestacao });
        await db.SaveChangesAsync();
    }

    /* ------------------------------ pendências do laudo ------------------------------ */

    public async Task<Guid> AdicionarPendenciaAsync(UsuarioAtual u, Guid vistoriaId, PendenciaForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var v = await Vistoria(db, u, vistoriaId, rastrear: false);
        if (v.Status == StatusVistoria.CANCELADA) throw new RegraNegocioException("_", "Vistoria cancelada.");
        var p = new PendenciaVistoria
        {
            VistoriaId = v.Id, Comodo = Texto.Limpo(form.Comodo), Descricao = form.Descricao.Trim(), Responsavel = form.Responsavel, Valor = form.Valor,
        };
        db.PendenciasVistoria.Add(p);
        audit.Registrar(db, u.Id, "vistoria_pendencias", p.Id, "CRIAR", depois: p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    public async Task RemoverPendenciaAsync(UsuarioAtual u, Guid pendenciaId)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var p = await Pendencia(db, u, pendenciaId);
        if (p.Status != StatusPendenciaVistoria.ABERTA) throw new RegraNegocioException("_", "Só pendência em aberto pode ser removida.");
        db.PendenciasVistoria.Remove(p);
        audit.Registrar(db, u.Id, "vistoria_pendencias", p.Id, "EXCLUIR", antes: p);
        await db.SaveChangesAsync();
    }

    public async Task ResolverPendenciaAsync(UsuarioAtual u, Guid pendenciaId)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var p = await Pendencia(db, u, pendenciaId);
        p.Status = StatusPendenciaVistoria.RESOLVIDA;
        audit.Registrar(db, u.Id, "vistoria_pendencias", p.Id, "RESOLVER");
        await db.SaveChangesAsync();
    }

    /// <summary>Abre o chamado de manutenção da pendência (o custo segue o responsável apontado).</summary>
    public async Task<Guid> AbrirChamadoAsync(UsuarioAtual u, Guid pendenciaId)
    {
        u.Exigir(P.LocacaoEditar);
        Guid vistoriaId, imovel, contrato;
        string titulo, descricao;
        ResponsavelManutencao resp;
        await using (var db = await fabrica.CreateDbContextAsync())
        {
            var p = await Pendencia(db, u, pendenciaId);
            if (p.Status != StatusPendenciaVistoria.ABERTA) throw new RegraNegocioException("_", "A pendência já foi tratada.");
            var v = await db.Vistorias.AsNoTracking().SingleAsync(x => x.Id == p.VistoriaId);
            (vistoriaId, imovel, contrato, resp) = (v.Id, v.ImovelId, v.ContratoId, p.Responsavel);
            titulo = $"{(p.Comodo is null ? "" : p.Comodo + ": ")}{p.Descricao}";
            if (titulo.Length > 200) titulo = titulo[..200];
            descricao = $"Pendência da vistoria {v.Codigo} ({Texto.Rotulo(v.Tipo).ToLowerInvariant()}): {p.Descricao}";
        }
        var chamado = await manutencao.CriarAsync(u, new ChamadoForm
        {
            ImovelId = imovel, ContratoId = contrato, Titulo = titulo, Descricao = descricao, Categoria = CategoriaManutencao.OUTROS, Responsavel = resp,
        });
        await using var db2 = await fabrica.CreateDbContextAsync();
        var pp = await db2.PendenciasVistoria.SingleAsync(x => x.Id == pendenciaId);
        pp.Status = StatusPendenciaVistoria.CHAMADO;
        pp.ChamadoId = chamado;
        await db2.SaveChangesAsync();
        return chamado;
    }

    /* ------------------------------ rescisão ------------------------------ */

    /// <summary>Pendências em aberto por conta do inquilino na vistoria de saída (com valor): sugestão de débito no acerto.</summary>
    public async Task<DebitosVistoria> DebitosSaidaAsync(UsuarioAtual u, Guid contratoId)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var lista = await db.PendenciasVistoria.AsNoTracking()
            .Where(p => p.Status == StatusPendenciaVistoria.ABERTA && p.Responsavel == ResponsavelManutencao.LOCATARIO && p.Valor > 0
                && db.Vistorias.Any(v => v.Id == p.VistoriaId && v.ContratoId == contratoId && v.Tipo == TipoVistoria.SAIDA
                    && v.Status == StatusVistoria.REALIZADA && db.Imoveis.VisiveisPara(u).Any(i => i.Id == v.ImovelId)))
            .OrderBy(p => p.CriadoEm).ToListAsync();
        if (lista.Count == 0) return new DebitosVistoria(0, null, []);
        var desc = "Vistoria de saída: " + string.Join("; ", lista.Select(p => p.Descricao));
        return new DebitosVistoria(lista.Sum(p => p.Valor!.Value), desc.Length > 200 ? desc[..197] + "..." : desc, [.. lista.Select(p => p.Id)]);
    }

    /// <summary>Depois do acerto da rescisão: as pendências usadas ficam como cobradas.</summary>
    public async Task MarcarCobradasAsync(UsuarioAtual u, IReadOnlyList<Guid> pendencias)
    {
        u.Exigir(P.LocacaoEditar);
        if (pendencias.Count == 0) return;
        await using var db = await fabrica.CreateDbContextAsync();
        var lista = await db.PendenciasVistoria
            .Where(p => pendencias.Contains(p.Id) && p.Status == StatusPendenciaVistoria.ABERTA
                && db.Vistorias.Any(v => v.Id == p.VistoriaId && db.Imoveis.VisiveisPara(u).Any(i => i.Id == v.ImovelId)))
            .ToListAsync();
        foreach (var p in lista) p.Status = StatusPendenciaVistoria.COBRADA;
        await db.SaveChangesAsync();
    }

    /* ------------------------------ apoio ------------------------------ */

    private static async Task<Vistoria> Vistoria(InovabensDb db, UsuarioAtual u, Guid id, bool rastrear = true)
    {
        var q = rastrear ? db.Vistorias : db.Vistorias.AsNoTracking();
        return await q.SingleOrDefaultAsync(v => v.Id == id && db.Imoveis.VisiveisPara(u).Any(i => i.Id == v.ImovelId))
            ?? throw new NaoEncontradoException("Vistoria não encontrada.");
    }

    private static async Task<PendenciaVistoria> Pendencia(InovabensDb db, UsuarioAtual u, Guid id) =>
        await db.PendenciasVistoria.SingleOrDefaultAsync(p => p.Id == id
            && db.Vistorias.Any(v => v.Id == p.VistoriaId && db.Imoveis.VisiveisPara(u).Any(i => i.Id == v.ImovelId)))
        ?? throw new NaoEncontradoException("Pendência não encontrada.");
}
