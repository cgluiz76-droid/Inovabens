using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Imoveis;

/* ------------------------------ Modelos ------------------------------ */

/// <summary>Uma unidade (lote ou área remanescente) na aba Lotes do empreendimento.</summary>
public sealed record UnidadeLinha(Guid Id, string Codigo, string Tipo, string? Identificacao, int Fase, string OrigemCodigo,
    string? Matricula, decimal? AreaM2, decimal? Preco, SituacaoImovel Situacao, bool Encerrada)
{
    /// <summary>Sem matrícula própria ainda: não pode ser vendido (Lei 6.766/79, art. 37).</summary>
    public bool Prevista => string.IsNullOrWhiteSpace(Matricula);
    public bool Remanescente => Tipo == TipoUnidade.Remanescente;
}

/// <summary>Uma fase (desmembramento) com a conferência de áreas: origem = unidades + área doada.</summary>
public sealed record FaseDetalhe(Desmembramento Dados, string OrigemCodigo, decimal? AreaOrigemM2, decimal AreaUnidadesM2,
    int Lotes, bool UnidadesSemArea)
{
    /// <summary>Área da matrícula de origem que não foi distribuída (positivo) ou que sobra (negativo); null sem área de origem.</summary>
    public decimal? Diferenca => AreaOrigemM2 is { } a && !UnidadesSemArea ? a - AreaUnidadesM2 - (Dados.AreaDoadaM2 ?? 0) : null;
}

/// <summary>Matrícula que ainda pode ser desmembrada: o empreendimento (se nunca foi) ou uma área remanescente ativa.</summary>
public sealed record OrigemDesmembravel(Guid Id, string Codigo, decimal? AreaM2);

public sealed record EmpreendimentoDetalhe(Guid Id, string Codigo, string? Matricula, decimal? AreaTotalM2,
    IReadOnlyList<FaseDetalhe> Fases, IReadOnlyList<UnidadeLinha> Unidades, IReadOnlyList<OrigemDesmembravel> Desmembraveis)
{
    public IEnumerable<UnidadeLinha> Lotes => Unidades.Where(x => !x.Remanescente);
    public int Disponiveis => Lotes.Count(x => x.Situacao == SituacaoImovel.DISPONIVEL);
    public int Vendidos => Lotes.Count(x => x.Situacao == SituacaoImovel.VENDIDO);
    /// <summary>Preço "a partir de" do anúncio: o menor entre os lotes disponíveis.</summary>
    public decimal? APartirDe => Lotes.Where(x => x.Situacao == SituacaoImovel.DISPONIVEL && x.Preco > 0).Min(x => x.Preco);
}

/// <summary>Lote ou remanescente: a que empreendimento pertence e de que matrícula saiu.</summary>
public sealed record VinculoUnidade(Guid EmpreendimentoId, string EmpreendimentoCodigo, Guid OrigemId, string OrigemCodigo, int Fase,
    string Tipo, string? Identificacao);

/// <summary>"Desmembrar": quantos lotes gerar (com área e preço iguais, ajustáveis depois) e a nova área remanescente.</summary>
public sealed class DesmembramentoForm : IValidatableObject
{
    [Range(1, 300, ErrorMessage = "Informe de 1 a 300 lotes.")]
    public int Quantidade { get; set; } = 1;

    /// <summary>Nome-base dos lotes: "Lote" gera "Lote 1", "Lote 2"... (continua a numeração das fases anteriores).</summary>
    [Required(ErrorMessage = "Informe como os lotes são chamados (ex.: Lote).")]
    [MaxLength(40, ErrorMessage = "No máximo 40 caracteres.")]
    public string Identificacao { get; set; } = "Lote";

    [Range(typeof(decimal), "0", "100000000", ErrorMessage = "Área inválida.")]
    public decimal? AreaLoteM2 { get; set; }

    [Range(typeof(decimal), "0", "10000000000", ErrorMessage = "Preço inválido.")]
    public decimal? PrecoLote { get; set; }

    public bool Remanescente { get; set; }

    [Range(typeof(decimal), "0", "100000000", ErrorMessage = "Área inválida.")]
    public decimal? AreaRemanescenteM2 { get; set; }

    public FaseForm Fase { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Remanescente && AreaRemanescenteM2 is > 0)
            yield return new("Marque \"Gerar área remanescente\" ou apague a área dela.", [nameof(AreaRemanescenteM2)]);
    }
}

/// <summary>Andamento de uma fase (desmembramento).</summary>
public sealed class FaseForm
{
    public SituacaoDesmembramento Situacao { get; set; } = SituacaoDesmembramento.PROJETO;
    [MaxLength(60, ErrorMessage = "No máximo 60 caracteres.")]
    public string? Protocolo { get; set; }
    public DateOnly? DataRegistro { get; set; }
    [Range(typeof(decimal), "0", "100000000", ErrorMessage = "Área inválida.")]
    public decimal? AreaDoadaM2 { get; set; }
    [MaxLength(2000, ErrorMessage = "No máximo 2.000 caracteres.")]
    public string? Observacao { get; set; }

    public static FaseForm De(Desmembramento d) => new()
    {
        Situacao = d.Situacao, Protocolo = d.Protocolo, DataRegistro = d.DataRegistro, AreaDoadaM2 = d.AreaDoadaM2, Observacao = d.Observacao,
    };
}

/// <summary>Edição rápida de uma unidade na tabela de lotes (matrícula, área, preço e situação de cada um).</summary>
public sealed class UnidadeEdicao
{
    public Guid Id { get; set; }
    [MaxLength(60, ErrorMessage = "Identificação com no máximo 60 caracteres.")]
    public string? Identificacao { get; set; }
    [MaxLength(40, ErrorMessage = "Matrícula com no máximo 40 caracteres.")]
    public string? Matricula { get; set; }
    public decimal? AreaM2 { get; set; }
    public decimal? Preco { get; set; }
    public SituacaoImovel Situacao { get; set; }

    public static UnidadeEdicao De(UnidadeLinha l) => new()
    {
        Id = l.Id, Identificacao = l.Identificacao, Matricula = l.Matricula, AreaM2 = l.AreaM2, Preco = l.Preco, Situacao = l.Situacao,
    };
}

/* ------------------------------ Serviço ------------------------------ */

/// <summary>
/// Empreendimento: uma área (matrícula de origem) desmembrada em lotes com matrícula, preço e situação próprios. O
/// empreendimento é o imóvel de origem (anúncio, fotos, centro de custo); cada lote ou área remanescente é um imóvel
/// ligado a ele (TE0043-01, TE0043-R1). A remanescente pode ser desmembrada de novo em outra fase, e a numeração continua.
/// </summary>
public sealed class EmpreendimentosService(IDbContextFactory<InovabensDb> fabrica, AuditService audit)
{
    /// <summary>Código da unidade: TE0043-07 (lote) ou TE0043-R2 (área remanescente).</summary>
    public static string CodigoUnidade(string codigoEmpreendimento, string tipo, int numero) =>
        tipo == TipoUnidade.Remanescente ? $"{codigoEmpreendimento}-R{numero}" : $"{codigoEmpreendimento}-{numero:D2}";

    /// <summary>
    /// Lote ainda sem matrícula própria não pode ser vendido: a Lei 6.766/79 (art. 37) proíbe vender parcela de
    /// desmembramento não registrado. Vale para a ficha, a tabela de lotes e o fechamento do investimento.
    /// </summary>
    public static void ExigirVendaPermitida(Imovel im)
    {
        if (im.EmpreendimentoId is not null && im.Situacao == SituacaoImovel.VENDIDO && string.IsNullOrWhiteSpace(im.Matricula))
            throw new RegraNegocioException(nameof(Imovel.Matricula),
                $"{im.Codigo} ainda não tem matrícula própria: lote de desmembramento não registrado não pode ser vendido (Lei 6.766/79, art. 37). Cadastre a matrícula antes.");
    }

    /// <summary>Lote ou remanescente: empreendimento e matrícula de origem. Null para imóvel comum ou o próprio empreendimento.</summary>
    public async Task<VinculoUnidade?> VinculoAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.ImoveisVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var im = await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        if (im.EmpreendimentoId is not { } e || im.OrigemId is not { } o) return null;
        var codigos = await db.Imoveis.Where(i => i.Id == e || i.Id == o).ToDictionaryAsync(i => i.Id, i => i.Codigo);
        var fase = await db.Desmembramentos.Where(d => d.Id == im.DesmembramentoId).Select(d => d.Fase).FirstOrDefaultAsync();
        return new(e, codigos.GetValueOrDefault(e, "?"), o, codigos.GetValueOrDefault(o, "?"), fase, im.UnidadeTipo ?? TipoUnidade.Lote,
            im.UnidadeIdentificacao);
    }

    /// <summary>Quadro público de lotes (site e página do imóvel); null se o imóvel não é empreendimento com lotes.</summary>
    public async Task<QuadroLotes?> QuadroAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.ImoveisVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var im = await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        return im.EmpreendimentoId is null ? await QuadroEmpreendimento.CarregarAsync(db, im.Id) : null;
    }

    /// <summary>Aba Lotes do empreendimento (também de um imóvel comum, que ainda pode ser desmembrado). Null para lote ou remanescente.</summary>
    public async Task<EmpreendimentoDetalhe?> DetalheAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.ImoveisVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var raiz = await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        if (raiz.EmpreendimentoId is not null) return null;
        return await Detalhe(db, raiz);
    }

    private static async Task<EmpreendimentoDetalhe> Detalhe(InovabensDb db, Imovel raiz)
    {
        var unidades = await db.Imoveis.AsNoTracking().Where(i => i.EmpreendimentoId == raiz.Id && i.ExcluidoEm == null).ToListAsync();
        var fases = await db.Desmembramentos.AsNoTracking().Where(d => d.EmpreendimentoId == raiz.Id).OrderBy(d => d.Fase).ToListAsync();
        var porId = unidades.ToDictionary(i => i.Id);
        porId[raiz.Id] = raiz;
        var faseDe = fases.ToDictionary(d => d.Id, d => d.Fase);
        // encerrada = remanescente cujo desmembramento já foi registrado (a matrícula dela deixou de existir)
        var encerradas = fases.Where(d => d.Situacao == SituacaoDesmembramento.REGISTRADO).Select(d => d.OrigemId).ToHashSet();
        string Codigo(Guid? id) => id is { } x && porId.TryGetValue(x, out var i) ? i.Codigo : "?";

        var linhas = unidades
            .OrderBy(i => i.UnidadeTipo == TipoUnidade.Remanescente).ThenBy(i => i.UnidadeNumero).ThenBy(i => i.Codigo)
            .Select(i => new UnidadeLinha(i.Id, i.Codigo, i.UnidadeTipo ?? TipoUnidade.Lote, i.UnidadeIdentificacao,
                i.DesmembramentoId is { } d ? faseDe.GetValueOrDefault(d) : 0, Codigo(i.OrigemId), i.Matricula, i.AreaTerrenoM2,
                i.PrecoVenda, i.Situacao, encerradas.Contains(i.Id)))
            .ToList();
        var detalhesFases = fases.Select(d =>
        {
            var filhos = unidades.Where(i => i.DesmembramentoId == d.Id).ToList();
            return new FaseDetalhe(d, Codigo(d.OrigemId), porId.GetValueOrDefault(d.OrigemId)?.AreaTerrenoM2,
                filhos.Sum(i => i.AreaTerrenoM2 ?? 0), filhos.Count(i => i.UnidadeTipo != TipoUnidade.Remanescente),
                filhos.Any(i => i.AreaTerrenoM2 is null));
        }).ToList();

        var jaDivididas = fases.Select(d => d.OrigemId).ToHashSet();
        var desmembraveis = new List<OrigemDesmembravel>();
        if (!jaDivididas.Contains(raiz.Id)) desmembraveis.Add(new(raiz.Id, raiz.Codigo, raiz.AreaTerrenoM2));
        desmembraveis.AddRange(unidades
            .Where(i => i.UnidadeTipo == TipoUnidade.Remanescente && !jaDivididas.Contains(i.Id) && i.Situacao != SituacaoImovel.VENDIDO)
            .OrderBy(i => i.UnidadeNumero).Select(i => new OrigemDesmembravel(i.Id, i.Codigo, i.AreaTerrenoM2)));
        return new(raiz.Id, raiz.Codigo, raiz.Matricula, raiz.AreaTerrenoM2, detalhesFases, linhas, desmembraveis);
    }

    /// <summary>
    /// Divide a matrícula <paramref name="origemId"/> (o empreendimento ou uma área remanescente) em lotes e, se pedido,
    /// numa nova área remanescente. Os lotes copiam tipo, finalidade, endereço, cartório e corretor do empreendimento e
    /// continuam a numeração das fases anteriores. Ficam "em regularização" até o desmembramento ser registrado.
    /// </summary>
    public async Task<Guid> DesmembrarAsync(UsuarioAtual u, Guid origemId, DesmembramentoForm f)
    {
        u.Exigir(P.ImoveisCriar); u.Exigir(P.ImoveisEditar);
        f.Identificacao = Texto.Limpo(f.Identificacao) ?? "";
        f.Fase.Protocolo = Texto.Limpo(f.Fase.Protocolo);
        f.Fase.Observacao = Texto.Limpo(f.Fase.Observacao);
        Validacao.Validar(f);
        Validacao.Validar(f.Fase);
        await using var db = await fabrica.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var origem = await Acesso.ImovelComAcesso(db, u, origemId, rastrear: true);
        // um desmembramento por vez no mesmo empreendimento: numeração e fase não se repetem
        var raizId = origem.EmpreendimentoId ?? origem.Id;
        await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtext({"empreendimento:" + raizId}))");

        if (origem.UnidadeTipo == TipoUnidade.Lote)
            throw new RegraNegocioException("Origem", $"{origem.Codigo} é um lote. Só o empreendimento ou uma área remanescente podem ser desmembrados.");
        if (await db.Desmembramentos.AnyAsync(d => d.OrigemId == origem.Id))
            throw new RegraNegocioException("Origem", $"{origem.Codigo} já foi desmembrado. Desmembre a área remanescente gerada por ele.");
        if (origem.Situacao == SituacaoImovel.VENDIDO)
            throw new RegraNegocioException("Origem", $"{origem.Codigo} está vendido.");
        var raiz = origem.EmpreendimentoId is { } r ? await db.Imoveis.SingleAsync(i => i.Id == r) : origem;

        var unidades = await db.Imoveis.Where(i => i.EmpreendimentoId == raiz.Id).ToListAsync();
        var proximoLote = (unidades.Where(i => i.UnidadeTipo != TipoUnidade.Remanescente).Max(i => i.UnidadeNumero) ?? 0) + 1;
        var proximaRem = (unidades.Where(i => i.UnidadeTipo == TipoUnidade.Remanescente).Max(i => i.UnidadeNumero) ?? 0) + 1;
        var fase = (await db.Desmembramentos.Where(d => d.EmpreendimentoId == raiz.Id).MaxAsync(d => (int?)d.Fase) ?? 0) + 1;

        var desm = new Desmembramento
        {
            EmpreendimentoId = raiz.Id, OrigemId = origem.Id, Fase = fase, CriadoPor = u.Id, AtualizadoPor = u.Id,
        };
        Aplicar(f.Fase, desm);
        db.Desmembramentos.Add(desm);
        audit.Registrar(db, u.Id, "desmembramentos", desm.Id, "CRIAR", depois: new
        {
            empreendimento = raiz.Codigo, origem = origem.Codigo, desm.Fase, lotes = f.Quantidade, remanescente = f.Remanescente,
        });

        var registrado = desm.Situacao == SituacaoDesmembramento.REGISTRADO;
        var novas = Enumerable.Range(proximoLote, f.Quantidade)
            .Select(n => (Tipo: TipoUnidade.Lote, Numero: n, Nome: $"{f.Identificacao} {n}", Area: f.AreaLoteM2, Preco: f.PrecoLote))
            .ToList();
        if (f.Remanescente)
            novas.Add((TipoUnidade.Remanescente, proximaRem, proximaRem == 1 ? "Área remanescente" : $"Área remanescente {proximaRem}",
                f.AreaRemanescenteM2, null));
        var codigos = novas.Select(x => CodigoUnidade(raiz.Codigo, x.Tipo, x.Numero)).ToList();
        if (await db.Imoveis.Where(i => codigos.Contains(i.Codigo)).Select(i => i.Codigo).FirstOrDefaultAsync() is { } ocupado)
            throw new RegraNegocioException("Origem", $"Já existe um imóvel com o código {ocupado}.");

        var nomeRaiz = RegrasImovel.NomeSemCodigo(raiz.Codigo, raiz.NomeDrive) ?? raiz.Titulo;
        foreach (var (x, codigo) in novas.Zip(codigos))
        {
            var im = new Imovel
            {
                Codigo = codigo, Referencia = codigo, EmpreendimentoId = raiz.Id, OrigemId = origem.Id, DesmembramentoId = desm.Id,
                UnidadeTipo = x.Tipo, UnidadeNumero = x.Numero, UnidadeIdentificacao = x.Nome,
                Titulo = Corta($"{x.Nome} – {nomeRaiz}", 200), NomeDrive = RegrasImovel.NomeDriveComCodigo(codigo, x.Nome),
                Tipo = raiz.Tipo, Subtipo = raiz.Subtipo, Finalidade = raiz.Finalidade,
                Situacao = registrado ? SituacaoImovel.DISPONIVEL : SituacaoImovel.EM_REGULARIZACAO,
                Cep = raiz.Cep, Logradouro = raiz.Logradouro, Bairro = raiz.Bairro, Cidade = raiz.Cidade, Uf = raiz.Uf,
                Latitude = raiz.Latitude, Longitude = raiz.Longitude, EnderecoPublico = raiz.EnderecoPublico, Zona = raiz.Zona,
                Regiao = raiz.Regiao, Cartorio = raiz.Cartorio, CorretorResponsavelId = raiz.CorretorResponsavelId,
                AreaTerrenoM2 = x.Area, PrecoVenda = x.Preco,
                // anúncio é o do empreendimento; o lote vai ao site e aos portais só se alguém marcar
                DivulgarSite = false,
            };
            db.Imoveis.Add(im);
            audit.Registrar(db, u.Id, "imoveis", im.Id, "CRIAR", depois: im);
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return desm.Id;
    }

    /// <summary>
    /// Andamento da fase. Ao registrar no cartório: os lotes em regularização ficam disponíveis e a área remanescente
    /// que foi dividida é arquivada (a matrícula dela foi encerrada; o histórico fica).
    /// </summary>
    public async Task AtualizarFaseAsync(UsuarioAtual u, Guid desmembramentoId, FaseForm f)
    {
        u.Exigir(P.ImoveisEditar);
        f.Protocolo = Texto.Limpo(f.Protocolo);
        f.Observacao = Texto.Limpo(f.Observacao);
        Validacao.Validar(f);
        await using var db = await fabrica.CreateDbContextAsync();
        var d = await db.Desmembramentos.SingleOrDefaultAsync(x => x.Id == desmembramentoId)
            ?? throw new NaoEncontradoException("Desmembramento não encontrado.");
        await Acesso.ImovelComAcesso(db, u, d.EmpreendimentoId, rastrear: false);
        var antes = d.Copia();
        Aplicar(f, d);
        d.AtualizadoPor = u.Id;
        d.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "desmembramentos", d.Id, "ATUALIZAR", antes, d);

        if (d.Situacao == SituacaoDesmembramento.REGISTRADO && antes.Situacao != SituacaoDesmembramento.REGISTRADO)
        {
            foreach (var im in await db.Imoveis.Where(i => i.DesmembramentoId == d.Id && i.ExcluidoEm == null
                && i.Situacao == SituacaoImovel.EM_REGULARIZACAO).ToListAsync())
                Mudar(db, u, im, x => x.Situacao = SituacaoImovel.DISPONIVEL);
            var origem = await db.Imoveis.SingleAsync(i => i.Id == d.OrigemId);
            if (origem.UnidadeTipo == TipoUnidade.Remanescente && origem.Situacao != SituacaoImovel.ARQUIVADO)
                Mudar(db, u, origem, x => x.Situacao = SituacaoImovel.ARQUIVADO);
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Grava a tabela de lotes: identificação, matrícula, área, preço e situação de cada unidade.</summary>
    public async Task SalvarUnidadesAsync(UsuarioAtual u, Guid empreendimentoId, IReadOnlyList<UnidadeEdicao> itens)
    {
        u.Exigir(P.ImoveisEditar);
        foreach (var x in itens)
        {
            x.Identificacao = Texto.Limpo(x.Identificacao);
            x.Matricula = Texto.Limpo(x.Matricula);
            Validacao.Validar(x);
            if (x.AreaM2 is < 0 || x.Preco is < 0) throw new RegraNegocioException("Unidades", "Área e preço não podem ser negativos.");
        }
        await using var db = await fabrica.CreateDbContextAsync();
        var raiz = await Acesso.ImovelComAcesso(db, u, empreendimentoId, rastrear: false);
        var ids = itens.Select(x => x.Id).ToList();
        var unidades = await db.Imoveis.Where(i => ids.Contains(i.Id) && i.EmpreendimentoId == raiz.Id && i.ExcluidoEm == null)
            .ToDictionaryAsync(i => i.Id);
        var nomeRaiz = RegrasImovel.NomeSemCodigo(raiz.Codigo, raiz.NomeDrive) ?? raiz.Titulo;
        foreach (var x in itens)
        {
            if (!unidades.TryGetValue(x.Id, out var im)) throw new NaoEncontradoException("Lote não encontrado neste empreendimento.");
            if (im.UnidadeIdentificacao == x.Identificacao && im.Matricula == x.Matricula && im.AreaTerrenoM2 == x.AreaM2
                && im.PrecoVenda == x.Preco && im.Situacao == x.Situacao) continue;
            Mudar(db, u, im, i =>
            {
                if (x.Identificacao is { } nome && nome != i.UnidadeIdentificacao)
                {
                    i.UnidadeIdentificacao = nome;
                    i.Titulo = Corta($"{nome} – {nomeRaiz}", 200);
                    if (i.DrivePastaId is null) i.NomeDrive = RegrasImovel.NomeDriveComCodigo(i.Codigo, nome);
                }
                i.Matricula = x.Matricula;
                i.AreaTerrenoM2 = x.AreaM2;
                i.PrecoVenda = x.Preco;
                i.Situacao = x.Situacao;
                ExigirVendaPermitida(i);
            });
        }
        await db.SaveChangesAsync();
    }

    private void Mudar(InovabensDb db, UsuarioAtual u, Imovel im, Action<Imovel> mudanca)
    {
        var antes = im.Copia();
        mudanca(im);
        im.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "imoveis", im.Id, "ATUALIZAR", antes, im);
    }

    private static void Aplicar(FaseForm f, Desmembramento d)
    {
        d.Situacao = f.Situacao;
        d.Protocolo = f.Protocolo;
        d.DataRegistro = f.DataRegistro;
        d.AreaDoadaM2 = f.AreaDoadaM2;
        d.Observacao = f.Observacao;
    }

    private static string Corta(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd();
}
