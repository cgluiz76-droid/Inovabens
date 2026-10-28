using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Imoveis;

/// <summary>
/// Módulo Imóveis (port de apps/api/src/imoveis/imoveis.service.ts).
/// Toda operação recebe o usuário atual: permissões e escopo (Corretor só vê os seus) são verificados aqui,
/// não apenas na tela.
/// </summary>
public sealed class ImoveisService(IDbContextFactory<InovabensDb> fabrica, AuditService audit, StorageService storage)
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

    public async Task<ImovelDetalhe> DetalheAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.ImoveisVer);
        await using var db = await fabrica.CreateDbContextAsync();
        return await Detalhe(db, u, id);
    }

    private async Task<ImovelDetalhe> Detalhe(InovabensDb db, UsuarioAtual u, Guid id)
    {
        var im = await Acesso.ImovelComAcesso(db, u, id, rastrear: false);
        var carac = await db.ImovelCaracteristicas.AsNoTracking().Where(c => c.ImovelId == id).OrderBy(c => c.Chave).ToListAsync();
        var midias = await db.ImovelMidias.AsNoTracking().Where(m => m.ImovelId == id).OrderBy(m => m.Ordem).ToListAsync();
        var props = await db.ImovelProprietarios.AsNoTracking().Where(p => p.ImovelId == id)
            .Select(p => new ProprietarioDetalhe(p.PessoaId, p.Pessoa!.NomeRazao, p.Percentual, p.Principal, p.InicioEm, p.FimEm))
            .ToListAsync();
        var envs = await db.ImovelEnvolvidos.AsNoTracking().Where(e => e.ImovelId == id)
            .Select(e => new EnvolvidoDetalhe(e.PessoaId, e.Pessoa!.NomeRazao, e.Papel, e.Observacao))
            .ToListAsync();
        var corretor = im.CorretorResponsavelId is { } cid
            ? await db.Usuarios.Where(x => x.Id == cid).Select(x => x.Nome).FirstOrDefaultAsync()
            : null;
        return new ImovelDetalhe(im, corretor, carac,
            midias.Select(m => new MidiaDetalhe(m, storage.UrlAssinada(m.ArquivoChave))).ToList(), props, envs);
    }

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

        var im = new Imovel { Codigo = RegrasImovel.FormatarCodigo(n) };
        form.AplicarEm(im);
        im.CorretorResponsavelId = Acesso.ResponsavelAoCriar(u, form.CorretorResponsavelId);
        db.Imoveis.Add(im);
        audit.Registrar(db, u.Id, "imoveis", im.Id, "CRIAR", depois: im);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return im.Id;
    }

    public async Task AtualizarAsync(UsuarioAtual u, Guid id, ImovelForm form)
    {
        u.Exigir(P.ImoveisEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var im = await Acesso.ImovelComAcesso(db, u, id);
        var antes = im.Copia();
        form.AplicarEm(im);
        if (Acesso.NovoResponsavel(u, form.CorretorResponsavelId) is { } r) im.CorretorResponsavelId = r;
        audit.Registrar(db, u.Id, "imoveis", id, "ATUALIZAR", antes, im);
        await db.SaveChangesAsync();
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
        var unicos = itens.GroupBy(i => i.Chave.Trim().ToLowerInvariant()).Select(g => g.Last()).ToList();

        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, id, rastrear: false);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.ImovelCaracteristicas.Where(c => c.ImovelId == id).ExecuteDeleteAsync();
        db.ImovelCaracteristicas.AddRange(unicos.Select(i => new ImovelCaracteristica
        {
            ImovelId = id, Chave = i.Chave.Trim(), Valor = Texto.Limpo(i.Valor), Publico = i.Publico,
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

    public bool ArmazenamentoHabilitado => storage.Habilitado;

    public async Task EnviarMidiaAsync(UsuarioAtual u, Guid id, Stream conteudo, string nomeOriginal, string contentType,
        TipoMidia tipo, string? legenda = null, bool publica = true, CancellationToken ct = default)
    {
        u.Exigir(P.ImoveisEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, id, rastrear: false);
        if (!storage.Habilitado) throw new ServicoIndisponivelException("Armazenamento de arquivos não configurado (S3_*).");

        var arquivo = RegrasImovel.ArquivoSeguro(nomeOriginal, contentType);
        if (tipo != TipoMidia.DOCUMENTO && tipo != arquivo.Tipo) tipo = TipoMidia.DOCUMENTO; // "foto" que não é imagem permitida
        var chave = $"imoveis/{id}/{Guid.NewGuid()}{arquivo.Extensao}";
        await storage.EnviarAsync(chave, conteudo, arquivo.ContentType, arquivo.Download ? nomeOriginal : null, ct);

        var proximaOrdem = (await db.ImovelMidias.Where(m => m.ImovelId == id).MaxAsync(m => (int?)m.Ordem, ct) ?? -1) + 1;
        var temFoto = await db.ImovelMidias.AnyAsync(m => m.ImovelId == id && m.Tipo == TipoMidia.FOTO, ct);
        db.ImovelMidias.Add(new ImovelMidia
        {
            ImovelId = id, Tipo = tipo, ArquivoChave = chave,
            NomeOriginal = nomeOriginal.Length > 255 ? nomeOriginal[..255] : nomeOriginal,
            Legenda = Texto.Limpo(legenda), Ordem = proximaOrdem,
            Capa = tipo == TipoMidia.FOTO && !temFoto, // a primeira foto vira capa
            Publica = publica,
        });
        await db.SaveChangesAsync(ct);
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
        if (b.Publica is { } pub) m.Publica = pub;
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
        await storage.RemoverAsync(m.ArquivoChave);
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
