using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Imoveis;

/// <summary>
/// Módulo Imóveis (port de apps/api/src/imoveis/imoveis.service.ts).
/// Toda operação recebe o usuário atual: permissões e escopo (Corretor só vê os seus) são verificados aqui,
/// não apenas na tela.
/// </summary>
public sealed class ImoveisService(
    IDbContextFactory<InovabensDb> fabrica, AuditService audit, IArmazenamento armazenamento, PastasDriveService pastas,
    PublicacaoFotosService publicacao, MagnificCliente magnific, ILogger<ImoveisService> log)
{
    public const long TamanhoMaximoArquivo = 25 * 1024 * 1024;

    /* ------------------------------- CRUD ------------------------------- */

    public async Task<Paginado<ImovelListaItem>> ListarAsync(UsuarioAtual u, FiltroImoveis f)
    {
        u.Exigir(P.ImoveisVer);
        var pagina = Math.Max(1, f.Pagina);
        var tamanho = Math.Clamp(f.TamanhoPagina, 1, 100);
        await using var db = await fabrica.CreateDbContextAsync();

        var q = db.Imoveis.AsNoTracking().Where(i => i.ExcluidoEm == null).VisiveisPara(u);
        if (!u.EscopoProprio && f.CorretorId is { } c) q = q.Where(i => i.CorretorResponsavelId == c);
        if (f.Situacao is { } s) q = q.Where(i => i.Situacao == s);
        if (f.Finalidade is { } fi) q = q.Where(i => i.Finalidade == fi);
        if (Texto.Limpo(f.Tipo) is { } tipo) q = q.Where(i => EF.Functions.ILike(i.Tipo, Texto.LikeLiteral(tipo), @"\"));
        if (Texto.Limpo(f.Cidade) is { } cidade) q = q.Where(i => EF.Functions.ILike(i.Cidade!, Texto.LikeLiteral(cidade), @"\"));
        if (Texto.Limpo(f.Termo) is { } termo)
        {
            var t = $"%{Texto.LikeLiteral(termo)}%";
            q = q.Where(i => EF.Functions.ILike(i.Titulo, t, @"\") || EF.Functions.ILike(i.Codigo, t, @"\")
                || EF.Functions.ILike(i.Bairro!, t, @"\") || EF.Functions.ILike(i.Cidade!, t, @"\")
                || EF.Functions.ILike(i.Logradouro!, t, @"\") || EF.Functions.ILike(i.Matricula!, t, @"\"));
        }

        var total = await q.CountAsync();
        var itens = await q.OrderByDescending(i => i.CriadoEm)
            .Skip((pagina - 1) * tamanho).Take(tamanho)
            .Select(i => new ImovelListaItem(i.Id, i.Codigo, i.Titulo, i.Tipo, i.Situacao, i.Finalidade, i.Cidade, i.Bairro, i.Uf,
                i.PrecoVenda, i.PrecoLocacao, i.Corretor != null ? i.Corretor.Nome : null))
            .ToListAsync();
        return new Paginado<ImovelListaItem>(itens, total, pagina, tamanho);
    }

    /// <summary>
    /// Imóveis que o site público mostra: disponíveis e marcados para divulgar, com a capa entre as fotos públicas.
    /// Usado pela pré-visualização do site (início e busca por venda/aluguel).
    /// </summary>
    public async Task<IReadOnlyList<VitrineItem>> VitrineAsync(UsuarioAtual u, FiltroVitrine f)
    {
        u.Exigir(P.ImoveisVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var q = db.Imoveis.AsNoTracking().VisiveisPara(u)
            .Where(i => i.ExcluidoEm == null && i.DivulgarSite && i.Situacao == SituacaoImovel.DISPONIVEL);
        if (f.Finalidade is { } fi && fi != FinalidadeImovel.AMBOS)
            q = q.Where(i => i.Finalidade == fi || i.Finalidade == FinalidadeImovel.AMBOS);
        if (Texto.Limpo(f.Tipo) is { } tipo) q = q.Where(i => EF.Functions.ILike(i.Tipo, Texto.LikeLiteral(tipo), @"\"));
        if (Texto.Limpo(f.Cidade) is { } cidade) q = q.Where(i => EF.Functions.ILike(i.Cidade!, Texto.LikeLiteral(cidade), @"\"));
        var imoveis = await q.OrderByDescending(i => i.AtualizadoEm).Take(Math.Clamp(f.Maximo, 1, 200)).ToListAsync();

        var ids = imoveis.Select(i => i.Id).ToList();
        var fotos = await db.ImovelMidias.AsNoTracking()
            .Where(m => ids.Contains(m.ImovelId) && m.Publica && m.Tipo == TipoMidia.FOTO && m.ArquivoChave != null && m.ArquivoChave != "")
            .OrderByDescending(m => m.Capa).ThenBy(m => m.Ordem).ToListAsync();
        var capas = fotos.GroupBy(m => m.ImovelId).ToDictionary(g => g.Key, g => Detalhar(g.First()) is var d ? d.UrlPublica ?? d.Url : null);

        return [.. imoveis.Select(i => new VitrineItem(i.Id, i.Referencia ?? i.Codigo, i.Titulo, i.Subtipo ?? i.Tipo, i.Finalidade,
            i.Cidade, i.Bairro, i.Uf, i.PrecoVenda, i.PrecoLocacao, i.Quartos, i.Suites, i.Vagas,
            i.AreaConstruidaM2 ?? i.AreaTerrenoM2 ?? i.AreaTotalM2 ?? i.AreaUtilM2, capas.GetValueOrDefault(i.Id)))];
    }

    public async Task<ImovelDetalhe> DetalheAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.ImoveisVer);
        await using var db = await fabrica.CreateDbContextAsync();
        return await Detalhe(db, u, id);
    }

    private async Task<ImovelDetalhe> Detalhe(InovabensDb db, UsuarioAtual u, Guid id)
    {
        var im = await Acesso.ImovelComAcesso(db, u, id, rastrear: false);
        var carac = await db.ImovelCaracteristicas.AsNoTracking().Where(c => c.ImovelId == id).OrderBy(c => c.Grupo).ThenBy(c => c.Chave).ToListAsync();
        var midias = await db.ImovelMidias.AsNoTracking().Where(m => m.ImovelId == id).OrderBy(m => m.Ordem).ToListAsync();
        var props = await db.ImovelProprietarios.AsNoTracking().Where(p => p.ImovelId == id)
            .Select(p => new ProprietarioDetalhe(p.PessoaId, p.Pessoa!.NomeRazao, p.Percentual, p.Principal, p.InicioEm, p.FimEm))
            .ToListAsync();
        var envs = await db.ImovelEnvolvidos.AsNoTracking().Where(e => e.ImovelId == id)
            .Select(e => new EnvolvidoDetalhe(e.PessoaId, e.Pessoa!.NomeRazao, e.Papel,
                db.PerfisPessoa.Where(p => p.Codigo == e.Papel).Select(p => p.Nome).FirstOrDefault() ?? e.Papel, e.Observacao))
            .ToListAsync();
        var corretor = im.CorretorResponsavelId is { } cid
            ? await db.Usuarios.Where(x => x.Id == cid).Select(x => x.Nome).FirstOrDefaultAsync()
            : null;
        return new ImovelDetalhe(im, corretor, carac, [.. midias.Select(Detalhar)], props, envs, pastas.Link(im.DrivePastaId));
    }

    /// <summary>O id do arquivo vai na URL: muda quando a foto é trocada (Magnific), então o navegador pode guardar em cache.</summary>
    private MidiaDetalhe Detalhar(ImovelMidia m) =>
        new(m, string.IsNullOrEmpty(m.ArquivoChave) ? null : $"imoveis/midias/{m.Id}/{Uri.EscapeDataString(m.ArquivoChave)}",
            PublicacaoFotosService.Situacao(m) == SituacaoPublicacao.Publicada ? publicacao.Url(m) : null);

    public async Task<Guid> CriarAsync(UsuarioAtual u, ImovelForm form)
    {
        u.Exigir(P.ImoveisCriar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        // Código sequencial IMV-000123; o lock consultivo evita duplicidade em criações simultâneas.
        await db.Database.ExecuteSqlRawAsync("select pg_advisory_xact_lock(hashtext('imoveis_codigo'))");
        var n = await db.Database.SqlQueryRaw<int>(
            "select coalesce(max(substring(codigo from 5)::int), 0) + 1 as \"Value\" from imoveis where codigo ~ '^IMV-[0-9]+$'")
            .SingleAsync();

        await ExigirTipoValido(db, form.Tipo, atual: null);
        var im = new Imovel { Codigo = RegrasImovel.FormatarCodigo(n) };
        form.AplicarEm(im);
        await ExigirNomeDriveLivre(db, im.NomeDrive, exceto: null);
        await ExigirReferenciaLivre(db, im.Referencia, exceto: null);
        im.CorretorResponsavelId = Acesso.ResponsavelAoCriar(u, form.CorretorResponsavelId);
        db.Imoveis.Add(im);
        audit.Registrar(db, u.Id, "imoveis", im.Id, "CRIAR", depois: im);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await TentarCriarPastas(im.Id);
        return im.Id;
    }

    /// <summary>
    /// Cria a pasta do imóvel e as subpastas no Drive. Se o Drive falhar, o cadastro continua valendo: a ficha mostra
    /// o botão "Criar pastas no Drive", e o primeiro envio de arquivo também tenta de novo.
    /// </summary>
    private async Task TentarCriarPastas(Guid id)
    {
        if (!armazenamento.Habilitado) return;
        try { await pastas.GarantirAsync(id); }
        catch (Exception e) { log.LogWarning("Pastas do imóvel {Imovel} não criadas no Drive: {Erro}", id, e.Message); }
    }

    /// <summary>Cria (ou recria) as pastas no Drive pela ficha do imóvel.</summary>
    public async Task CriarPastasDriveAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.ImoveisEditar);
        await using (var db = await fabrica.CreateDbContextAsync())
            await Acesso.ImovelComAcesso(db, u, id, rastrear: false);
        await pastas.GarantirAsync(id);
    }

    /// <summary>Duas pastas com o mesmo nome no Drive confundiriam os arquivos de imóveis diferentes.</summary>
    private static async Task ExigirNomeDriveLivre(InovabensDb db, string? nome, Guid? exceto)
    {
        if (nome is null) return;
        var minusculo = nome.ToLower();
        if (await db.Imoveis.AnyAsync(i => i.ExcluidoEm == null && i.Id != exceto && i.NomeDrive != null && i.NomeDrive.ToLower() == minusculo))
            throw new RegraNegocioException(nameof(ImovelForm.NomeDrive), "Já existe outro imóvel com este nome de pasta no Drive.");
    }

    /// <summary>A referência identifica o anúncio nos portais: não pode repetir.</summary>
    private static async Task ExigirReferenciaLivre(InovabensDb db, string? referencia, Guid? exceto)
    {
        if (referencia is null) return;
        var minuscula = referencia.ToLower();
        if (await db.Imoveis.AnyAsync(i => i.ExcluidoEm == null && i.Id != exceto && i.Referencia != null && i.Referencia.ToLower() == minuscula))
            throw new RegraNegocioException(nameof(ImovelForm.Referencia), "Já existe outro imóvel com esta referência.");
    }

    public async Task AtualizarAsync(UsuarioAtual u, Guid id, ImovelForm form)
    {
        u.Exigir(P.ImoveisEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var im = await Acesso.ImovelComAcesso(db, u, id);
        await ExigirTipoValido(db, form.Tipo, atual: im.Tipo);
        var antes = im.Copia();
        form.AplicarEm(im);
        await ExigirNomeDriveLivre(db, im.NomeDrive, exceto: id);
        await ExigirReferenciaLivre(db, im.Referencia, exceto: id);
        if (Acesso.NovoResponsavel(u, form.CorretorResponsavelId) is { } r) im.CorretorResponsavelId = r;
        // nome mudou: renomeia a pasta no Drive antes de gravar, para os dois não ficarem diferentes
        if (im.NomeDrive is { } nome && nome != antes.NomeDrive && im.DrivePastaId is { } pasta && armazenamento.Habilitado)
            await pastas.RenomearAsync(pasta, nome);
        audit.Registrar(db, u.Id, "imoveis", id, "ATUALIZAR", antes, im);
        await db.SaveChangesAsync();
        if (im.DrivePastaId is null) await TentarCriarPastas(id);
    }

    /// <summary>O tipo precisa estar cadastrado e ativo; um tipo desativado só vale para o imóvel que já o tinha.</summary>
    private static async Task ExigirTipoValido(InovabensDb db, string tipo, string? atual)
    {
        var nome = tipo.Trim();
        if (nome == atual) return;
        if (!await db.TiposImovel.AnyAsync(t => t.Nome == nome && t.Ativo))
            throw new RegraNegocioException(nameof(ImovelForm.Tipo), "Selecione um tipo de imóvel cadastrado.");
    }

    /// <summary>Exclusão lógica (excluido_em), como no sistema original.</summary>
    public async Task ExcluirAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.ImoveisExcluir);
        await using var db = await fabrica.CreateDbContextAsync();
        var im = await Acesso.ImovelComAcesso(db, u, id);
        audit.Registrar(db, u.Id, "imoveis", id, "EXCLUIR", antes: im.Copia());
        im.ExcluidoEm = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /* ---------------------- Conjuntos (substituição) ---------------------- */

    public async Task SalvarCaracteristicasAsync(UsuarioAtual u, Guid id, IReadOnlyList<CaracteristicaEntrada> itens)
    {
        u.Exigir(P.ImoveisEditar);
        if (itens.Count > 200) throw new RegraNegocioException("caracteristicas", "Máximo de 200 características.");
        foreach (var it in itens) Validacao.Validar(it);
        // chaves repetidas (sem diferenciar maiúsculas) ficam com a última ocorrência
        var unicos = itens.GroupBy(i => (Texto.Limpo(i.Grupo)?.ToLowerInvariant(), i.Chave.Trim().ToLowerInvariant())).Select(g => g.Last()).ToList();

        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, id, rastrear: false);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.ImovelCaracteristicas.Where(c => c.ImovelId == id).ExecuteDeleteAsync();
        db.ImovelCaracteristicas.AddRange(unicos.Select(i => new ImovelCaracteristica
        {
            ImovelId = id, Chave = i.Chave.Trim(), Valor = Texto.Limpo(i.Valor), Publico = i.Publico, Grupo = Texto.Limpo(i.Grupo),
        }));
        audit.Registrar(db, u.Id, "imoveis", id, "ATUALIZAR", depois: new { caracteristicas = itens });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>Regra: a soma dos percentuais VIGENTES (hoje) dos proprietários deve ser 100%.</summary>
    public async Task SalvarProprietariosAsync(UsuarioAtual u, Guid id, IReadOnlyList<ProprietarioEntrada> itens)
    {
        u.Exigir(P.ImoveisEditar);
        if (itens.Count > 50) throw new RegraNegocioException("proprietarios", "Máximo de 50 proprietários.");
        RegrasImovel.ValidarProprietarios(itens, DatasBR.Hoje());

        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, id, rastrear: false);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.ImovelProprietarios.Where(p => p.ImovelId == id).ExecuteDeleteAsync();
        db.ImovelProprietarios.AddRange(itens.Select(i => new ImovelProprietario
        {
            ImovelId = id, PessoaId = i.PessoaId!.Value, Percentual = i.Percentual!.Value, Principal = i.Principal,
            InicioEm = i.InicioEm, FimEm = i.FimEm,
        }));
        audit.Registrar(db, u.Id, "imoveis", id, "ATUALIZAR", depois: new { proprietarios = itens });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task SalvarEnvolvidosAsync(UsuarioAtual u, Guid id, IReadOnlyList<EnvolvidoEntrada> itens)
    {
        u.Exigir(P.ImoveisEditar);
        if (itens.Count > 100) throw new RegraNegocioException("envolvidos", "Máximo de 100 envolvidos.");
        var unicos = itens.DistinctBy(i => (i.PessoaId, i.Papel)).ToList();

        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, id, rastrear: false);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.ImovelEnvolvidos.Where(e => e.ImovelId == id).ExecuteDeleteAsync();
        db.ImovelEnvolvidos.AddRange(unicos.Select(i => new ImovelEnvolvido
        {
            ImovelId = id, PessoaId = i.PessoaId, Papel = i.Papel, Observacao = Texto.Limpo(i.Observacao),
        }));
        audit.Registrar(db, u.Id, "imoveis", id, "ATUALIZAR", depois: new { envolvidos = itens });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /* ------------------------------- Mídias ------------------------------- */

    public bool ArmazenamentoHabilitado => armazenamento.Habilitado;
    public bool PublicacaoHabilitada => publicacao.Habilitada;

    /// <summary>
    /// Grava o arquivo na subpasta escolhida da pasta do imóvel no Drive. Só imagens e vídeos em "04 - Fotos e Vídeos"
    /// entram na galeria (capa, limite de fotos, site); o resto é documento.
    /// </summary>
    /// <param name="publica">Só vale para fotos e vídeos em "04": foto pública ganha cópia no R2 (site e portais).</param>
    public async Task EnviarMidiaAsync(UsuarioAtual u, Guid id, Stream conteudo, string nomeOriginal, string contentType,
        int subpasta, bool publica = false, string? legenda = null, CancellationToken ct = default, bool melhorar = true)
    {
        u.Exigir(P.ImoveisEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, id, rastrear: false);
        if (!armazenamento.Habilitado)
            throw new ServicoIndisponivelException("Google Drive não configurado (GOOGLE_DRIVE_ID e GOOGLE_DRIVE_CREDENCIAIS).");
        SubpastasDrive.Nome(subpasta); // valida

        var tipo = SubpastasDrive.TipoNaPasta(subpasta, RegrasImovel.ArquivoSeguro(nomeOriginal, contentType).Tipo);
        if (tipo == TipoMidia.FOTO && await db.ImovelMidias.CountAsync(m => m.ImovelId == id && m.Tipo == TipoMidia.FOTO, ct) >= RegrasImovel.MaximoFotos)
            throw new RegraNegocioException("arquivo", $"Limite de {RegrasImovel.MaximoFotos} fotos por imóvel atingido. Exclua uma foto para enviar outra.");
        var nome = Path.GetFileName(nomeOriginal.Trim());
        if (nome.Length == 0) nome = "arquivo";
        if (nome.Length > 255) nome = nome[..255];

        // no Drive o arquivo fica com o tipo informado (para abrir certo lá); ao servir pelo sistema, ArquivoSeguro filtra
        var pasta = await pastas.SubpastaAsync(id, subpasta, ct);
        var chave = await PastasDriveService.Chamar(() => armazenamento.EnviarAsync(pasta, nome, conteudo, RegrasImovel.TipoInformado(contentType), ct));

        var proximaOrdem = (await db.ImovelMidias.Where(m => m.ImovelId == id).MaxAsync(m => (int?)m.Ordem, ct) ?? -1) + 1;
        var temFoto = await db.ImovelMidias.AnyAsync(m => m.ImovelId == id && m.Tipo == TipoMidia.FOTO, ct);
        db.ImovelMidias.Add(new ImovelMidia
        {
            ImovelId = id, Tipo = tipo, ArquivoChave = chave, Subpasta = (short)subpasta,
            NomeOriginal = nome,
            Legenda = Texto.Limpo(legenda), Ordem = proximaOrdem,
            Capa = tipo == TipoMidia.FOTO && !temFoto, // a primeira foto vira capa
            Publica = publica && tipo is TipoMidia.FOTO or TipoMidia.VIDEO, // documentos nunca vão para o site
            // com o Magnific configurado, a foto é melhorada em segundo plano (MelhoriaFotosService)
            // melhorar: false na importação em lote (cada foto melhorada gasta créditos do Magnific)
            MelhoriaStatus = melhorar && MelhoriaAutomatica(tipo, RegrasImovel.TipoInformado(contentType)) ? StatusMelhoria.PENDENTE : null,
            MelhoriaAtualizadaEm = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    public bool MelhoriaHabilitada => magnific.Configurado && armazenamento.Habilitado;

    private bool MelhoriaAutomatica(TipoMidia tipo, string contentType) =>
        MelhoriaHabilitada && tipo == TipoMidia.FOTO && ImagemFoto.Suportado(contentType);

    /// <summary>Só as mídias do imóvel (a tela recarrega enquanto há fotos sendo melhoradas).</summary>
    public async Task<IReadOnlyList<MidiaDetalhe>> MidiasAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.ImoveisVer);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, id, rastrear: false);
        var midias = await db.ImovelMidias.AsNoTracking().Where(m => m.ImovelId == id).OrderBy(m => m.Ordem).ToListAsync();
        return [.. midias.Select(Detalhar)];
    }

    /// <summary>
    /// Arquivo para o navegador (o Drive é privado; o sistema confere o acesso e repassa). <paramref name="arquivoId"/>
    /// tem de ser o arquivo atual da mídia ou o original guardado pela melhoria.
    /// </summary>
    public async Task<ArquivoMidia> ArquivoMidiaAsync(UsuarioAtual u, Guid midiaId, string arquivoId, CancellationToken ct = default)
    {
        u.Exigir(P.ImoveisVer);
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var m = await db.ImovelMidias.AsNoTracking().SingleOrDefaultAsync(x => x.Id == midiaId, ct)
            ?? throw new NaoEncontradoException("Arquivo não encontrado.");
        await Acesso.ImovelComAcesso(db, u, m.ImovelId, rastrear: false);
        if (arquivoId != m.ArquivoChave && arquivoId != m.ArquivoOriginalChave) throw new NaoEncontradoException("Arquivo não encontrado.");
        var conteudo = await armazenamento.BaixarAsync(arquivoId, ct) ?? throw new NaoEncontradoException("Arquivo não encontrado no Google Drive.");
        // só imagem, vídeo e PDF abrem no navegador; o resto (HTML, SVG...) é sempre baixado
        var seguro = RegrasImovel.ArquivoSeguro(m.NomeOriginal ?? "arquivo", conteudo.Tipo);
        return new ArquivoMidia(conteudo.Dados, seguro.ContentType, m.NomeOriginal ?? "arquivo", seguro.Download);
    }

    public async Task AtualizarMidiaAsync(UsuarioAtual u, Guid midiaId, MidiaAlteracao b)
    {
        u.Exigir(P.ImoveisEditar);
        Validacao.Validar(b);
        await using var db = await fabrica.CreateDbContextAsync();
        var m = await MidiaComAcesso(db, u, midiaId);
        await using var tx = await db.Database.BeginTransactionAsync();
        if (b.Capa == true)
            await db.ImovelMidias.Where(x => x.ImovelId == m.ImovelId && x.Id != m.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Capa, false));
        if (b.Legenda is not null) m.Legenda = Texto.Limpo(b.Legenda);
        if (b.Ordem is { } o) m.Ordem = Math.Max(0, o);
        if (b.Capa is { } capa) m.Capa = capa;
        if (b.Publica is { } pub && pub != m.Publica)
        {
            if (pub && m.Tipo is not (TipoMidia.FOTO or TipoMidia.VIDEO))
                throw new RegraNegocioException("publica", "Só fotos e vídeos podem ser públicos.");
            m.Publica = pub;
            // marcada de novo: tenta publicar outra vez, mesmo que tenha falhado antes (a rotina publica ou retira)
            if (m.PublicaChave is null) m.PublicadaArquivo = null;
            m.PublicacaoErro = null;
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task RemoverMidiaAsync(UsuarioAtual u, Guid midiaId)
    {
        u.Exigir(P.ImoveisEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var m = await MidiaComAcesso(db, u, midiaId);
        db.ImovelMidias.Remove(m);
        await db.SaveChangesAsync();
        await armazenamento.RemoverAsync(m.ArquivoChave);
        if (m.ArquivoOriginalChave is { } original) await armazenamento.RemoverAsync(original);
        await publicacao.RemoverCopiaAsync(m.PublicaChave);
    }

    private static async Task<ImovelMidia> MidiaComAcesso(InovabensDb db, UsuarioAtual u, Guid midiaId)
    {
        var m = await db.ImovelMidias.SingleOrDefaultAsync(x => x.Id == midiaId)
            ?? throw new NaoEncontradoException("Mídia não encontrada.");
        await Acesso.ImovelComAcesso(db, u, m.ImovelId, rastrear: false);
        return m;
    }

    /* --------------------------- Regularizações --------------------------- */

    public async Task<IReadOnlyList<RegularizacaoDetalhe>> ListarRegularizacoesAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.ImoveisVer);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        return await db.Regularizacoes.AsNoTracking()
            .Where(r => r.ImovelId == imovelId && r.ExcluidoEm == null)
            .OrderByDescending(r => r.CriadoEm)
            .Select(r => new RegularizacaoDetalhe(r, r.Profissional != null ? r.Profissional.NomeRazao : null))
            .ToListAsync();
    }

    public async Task CriarRegularizacaoAsync(UsuarioAtual u, Guid imovelId, RegularizacaoForm form)
    {
        u.Exigir(P.ImoveisEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var r = new Regularizacao { ImovelId = imovelId };
        form.AplicarEm(r);
        db.Regularizacoes.Add(r);
        audit.Registrar(db, u.Id, "regularizacoes", r.Id, "CRIAR", depois: r);
        await db.SaveChangesAsync();
    }

    public async Task AtualizarRegularizacaoAsync(UsuarioAtual u, Guid regId, RegularizacaoForm form)
    {
        u.Exigir(P.ImoveisEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var r = await RegComAcesso(db, u, regId);
        var antes = r.Copia();
        form.AplicarEm(r);
        audit.Registrar(db, u.Id, "regularizacoes", regId, "ATUALIZAR", antes, r);
        await db.SaveChangesAsync();
    }

    public async Task ExcluirRegularizacaoAsync(UsuarioAtual u, Guid regId)
    {
        u.Exigir(P.ImoveisEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var r = await RegComAcesso(db, u, regId);
        audit.Registrar(db, u.Id, "regularizacoes", regId, "EXCLUIR", antes: r.Copia());
        r.ExcluidoEm = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private static async Task<Regularizacao> RegComAcesso(InovabensDb db, UsuarioAtual u, Guid regId)
    {
        var r = await db.Regularizacoes.SingleOrDefaultAsync(x => x.Id == regId && x.ExcluidoEm == null)
            ?? throw new NaoEncontradoException("Regularização não encontrada.");
        await Acesso.ImovelComAcesso(db, u, r.ImovelId, rastrear: false);
        return r;
    }
}
