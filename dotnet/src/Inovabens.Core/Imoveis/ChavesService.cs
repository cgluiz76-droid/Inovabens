using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Relatorios;
using QuestPDF.Fluent;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Imoveis;

public enum SituacaoChave { DISPONIVEL, FORA, ATRASADA }

/// <summary>Saída em andamento ou já devolvida (instantes em UTC).</summary>
public sealed record MovimentoChave(
    Guid Id, Guid ChaveId, string ChaveCodigo, string ComQuem, string? Telefone, bool UsuarioDaEquipe, string Motivo,
    DateTime RetiradaEm, DateTime PrevisaoDevolucao, DateTime? DevolvidaEm, string? ObservacaoDevolucao,
    string? RegistradoPor, string? RecebidoPor)
{
    public bool Atrasada(DateTime agoraUtc) => DevolvidaEm is null && PrevisaoDevolucao < agoraUtc;
}

public sealed record ChaveResumo(
    Guid Id, Guid ImovelId, string ImovelCodigo, string ImovelTitulo, string Codigo, string Descricao, string? Observacao,
    MovimentoChave? Fora, SituacaoChave Situacao);

public sealed record ChavesDoImovel(IReadOnlyList<ChaveResumo> Chaves, IReadOnlyList<MovimentoChave> Historico);

public sealed class ChaveForm
{
    [Required(ErrorMessage = "Informe o código da etiqueta.")]
    [StringLength(20, ErrorMessage = "Código com até 20 caracteres.")]
    public string Codigo { get; set; } = "";

    [Required(ErrorMessage = "Descreva a chave (ex.: portão, apartamento, garagem).")]
    [StringLength(120, ErrorMessage = "Descrição com até 120 caracteres.")]
    public string Descricao { get; set; } = "";

    [StringLength(500, ErrorMessage = "Observação com até 500 caracteres.")]
    public string? Observacao { get; set; }
}

/// <summary>Saída da chave. Horário de devolução no fuso de Brasília (é o que a tela mostra).</summary>
public sealed class EntregaChaveForm : IValidatableObject
{
    /// <summary>Usuário da equipe que leva a chave.</summary>
    public Guid? UsuarioId { get; set; }

    /// <summary>Pessoa do cadastro que leva a chave (escolhida na busca).</summary>
    public Guid? PessoaId { get; set; }

    /// <summary>Nome digitado que não está no cadastro: com <see cref="CadastrarPessoa"/>, vira uma pessoa nova.</summary>

    [StringLength(200, ErrorMessage = "Nome com até 200 caracteres.")]
    public string? PessoaNome { get; set; }

    [StringLength(30, ErrorMessage = "Telefone com até 30 caracteres.")]
    public string? PessoaTelefone { get; set; }

    /// <summary>Cadastra o nome digitado em Pessoas (com o telefone) na mesma hora em que registra a saída.</summary>
    public bool CadastrarPessoa { get; set; }

    public string Motivo { get; set; } = ChavesService.Motivos[0];

    [StringLength(200, ErrorMessage = "Detalhe com até 200 caracteres.")]
    public string? MotivoDetalhe { get; set; }

    public DateTime? DevolverAte { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (UsuarioId is null && PessoaId is null && string.IsNullOrWhiteSpace(PessoaNome))
            yield return new("Informe com quem a chave vai ficar.", [nameof(PessoaNome)]);
        if (!ChavesService.Motivos.Contains(Motivo))
            yield return new("Escolha o motivo.", [nameof(Motivo)]);
        if (Motivo == ChavesService.MotivoOutro && string.IsNullOrWhiteSpace(MotivoDetalhe))
            yield return new("Descreva o motivo.", [nameof(MotivoDetalhe)]);
        if (DevolverAte is null)
            yield return new("Informe até quando a chave deve voltar.", [nameof(DevolverAte)]);
        else if (DatasBR.ParaUtc(DevolverAte.Value) <= DateTime.UtcNow)
            yield return new("O prazo de devolução precisa ser no futuro.", [nameof(DevolverAte)]);
    }
}

/// <summary>
/// Controle de chaves dentro do imóvel (inspirado no módulo do Imobibrasil): cadastro das chaves com código de
/// etiqueta, saída (com quem, motivo, prazo) e devolução. Atrasada = fora e com o prazo vencido.
/// </summary>
/// <summary>Sugestão do campo "Com quem vai ficar?": alguém da equipe (usuário) ou uma pessoa do cadastro.</summary>
public sealed record ResponsavelChave(Guid Id, string Nome, string? Telefone, bool Equipe);

public sealed class ChavesService(IDbContextFactory<InovabensDb> fabrica, AuditService audit, Pessoas.PessoasService pessoas)
{
    public const string MotivoOutro = "Outro";
    public static readonly IReadOnlyList<string> Motivos =
        ["Visita", "Vistoria", "Manutenção / obra", "Fotos e vídeo", "Avaliação", "Entrega ao cliente", MotivoOutro];

    public async Task<ChavesDoImovel> DoImovelAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.ChavesVer);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var chaves = await Resumos(db, db.Chaves.Where(c => c.ImovelId == imovelId && c.ExcluidoEm == null));
        // ordena antes de projetar (o EF não ordena pela propriedade do record montado no Select)
        var historico = await Movimentos(db, db.ChaveMovimentos.Where(m => m.Chave!.ImovelId == imovelId)
            .OrderByDescending(m => m.RetiradaEm).Take(100)).ToListAsync();
        return new(chaves.OrderBy(c => c.Codigo).ToList(), historico);
    }

    /// <summary>Chaves fora de todos os imóveis visíveis: atrasadas primeiro, depois pelo prazo.</summary>
    public async Task<IReadOnlyList<ChaveResumo>> ForaAsync(UsuarioAtual u)
    {
        u.Exigir(P.ChavesVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var visiveis = db.Imoveis.VisiveisPara(u).Where(i => i.ExcluidoEm == null).Select(i => i.Id);
        var fora = await Resumos(db, db.Chaves.Where(c => c.ExcluidoEm == null && visiveis.Contains(c.ImovelId)
            && db.ChaveMovimentos.Any(m => m.ChaveId == c.Id && m.DevolvidaEm == null)));
        return fora.OrderByDescending(c => c.Situacao == SituacaoChave.ATRASADA).ThenBy(c => c.Fora!.PrevisaoDevolucao).ToList();
    }

    /// <summary>Quantas chaves estão fora e quantas atrasadas (atalho na lista de imóveis).</summary>
    public async Task<(int Fora, int Atrasadas)> ContagemAsync(UsuarioAtual u)
    {
        if (!u.Pode(P.ChavesVer)) return (0, 0);
        await using var db = await fabrica.CreateDbContextAsync();
        var visiveis = db.Imoveis.VisiveisPara(u).Where(i => i.ExcluidoEm == null).Select(i => i.Id);
        var prazos = await db.ChaveMovimentos.AsNoTracking()
            .Where(m => m.DevolvidaEm == null && m.Chave!.ExcluidoEm == null && visiveis.Contains(m.Chave.ImovelId))
            .Select(m => m.PrevisaoDevolucao).ToListAsync();
        var agora = DateTime.UtcNow;
        return (prazos.Count, prazos.Count(p => p < agora));
    }

    /// <summary>Sugestão de código: CH + o próximo número (contando as excluídas, para a etiqueta nunca repetir).</summary>
    public async Task<string> ProximoCodigoAsync()
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var codigos = await db.Chaves.AsNoTracking().Where(c => c.Codigo.StartsWith("CH")).Select(c => c.Codigo).ToListAsync();
        var maior = codigos.Select(c => int.TryParse(c[2..], out var n) ? n : 0).DefaultIfEmpty(0).Max();
        return $"CH{maior + 1:0000}";
    }

    /// <summary>
    /// Busca para "Com quem vai ficar?": usuários ativos e pessoas do cadastro (se puder ver Pessoas) pelo nome ou
    /// telefone, a equipe primeiro. Vazio = ninguém encontrado (a tela oferece cadastrar o nome digitado).
    /// </summary>
    public async Task<IReadOnlyList<ResponsavelChave>> BuscarResponsaveisAsync(UsuarioAtual u, string? termo, int limite = 8)
    {
        // mesma comparação da busca global: sem acento e sem maiúsculas; telefone só pelos dígitos
        var t = Busca.BuscaGlobalService.Normalizar(termo);
        if (t.Length < 2) return [];
        var like = $"%{Texto.LikeLiteral(t)}%";
        var digitos = new string(t.Where(char.IsAsciiDigit).ToArray());
        var likeFone = digitos.Length >= 4 ? $"%{digitos}%" : null;
        await using var db = await fabrica.CreateDbContextAsync();
        var equipe = await db.Usuarios.AsNoTracking()
            .Where(x => x.Ativo && EF.Functions.Like(FuncoesBusca.Normalizar(x.Nome), like, @"\"))
            .OrderBy(x => x.Nome).Take(limite).Select(x => new ResponsavelChave(x.Id, x.Nome, null, true)).ToListAsync();
        if (!u.Pode(P.PessoasVer)) return equipe;
        var cadastro = await db.Pessoas.AsNoTracking()
            .Where(x => x.ExcluidoEm == null && (EF.Functions.Like(FuncoesBusca.Normalizar(x.NomeRazao), like, @"\")
                || (likeFone != null && (EF.Functions.Like(FuncoesBusca.Digitos(x.Telefone), likeFone)
                    || EF.Functions.Like(FuncoesBusca.Digitos(x.Whatsapp), likeFone)))))
            .OrderBy(x => x.NomeRazao).Take(limite)
            .Select(x => new ResponsavelChave(x.Id, x.NomeRazao, x.Whatsapp ?? x.Telefone, false)).ToListAsync();
        return [.. equipe, .. cadastro.Take(Math.Max(0, limite - equipe.Count))];
    }

    public async Task<Guid> CriarAsync(UsuarioAtual u, Guid imovelId, ChaveForm form)
    {
        u.Exigir(P.ChavesEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var codigo = Codigo(form.Codigo);
        await CodigoLivre(db, codigo, null);
        var c = new Chave
        {
            ImovelId = imovelId, Codigo = codigo, Descricao = form.Descricao.Trim(),
            Observacao = Texto.Limpo(form.Observacao), CriadoPor = u.Id,
        };
        db.Chaves.Add(c);
        audit.Registrar(db, u.Id, "chaves", c.Id, "CRIAR", depois: c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    public async Task AtualizarAsync(UsuarioAtual u, Guid id, ChaveForm form)
    {
        u.Exigir(P.ChavesEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await ChaveComAcesso(db, u, id);
        var codigo = Codigo(form.Codigo);
        await CodigoLivre(db, codigo, id);
        var antes = c.Copia();
        c.Codigo = codigo;
        c.Descricao = form.Descricao.Trim();
        c.Observacao = Texto.Limpo(form.Observacao);
        audit.Registrar(db, u.Id, "chaves", id, "ATUALIZAR", antes, c);
        await db.SaveChangesAsync();
    }

    /// <summary>Exclusão lógica (o histórico fica); não exclui chave que está fora.</summary>
    public async Task ExcluirAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.ChavesEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await ChaveComAcesso(db, u, id);
        if (await db.ChaveMovimentos.AnyAsync(m => m.ChaveId == id && m.DevolvidaEm == null))
            throw new RegraNegocioException("Chave", "A chave está fora: registre a devolução antes de excluir.");
        var antes = c.Copia();
        c.ExcluidoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "chaves", id, "EXCLUIR", antes);
        await db.SaveChangesAsync();
    }

    /// <summary>Registra a saída da chave agora.</summary>
    public async Task EntregarAsync(UsuarioAtual u, Guid chaveId, EntregaChaveForm form)
    {
        u.Exigir(P.ChavesMovimentar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await ChaveComAcesso(db, u, chaveId);
        if (await db.ChaveMovimentos.AnyAsync(m => m.ChaveId == chaveId && m.DevolvidaEm == null))
            throw new RegraNegocioException("Chave", "Esta chave já está fora. Registre a devolução primeiro.");
        if (form.UsuarioId is { } uid && !await db.Usuarios.AnyAsync(x => x.Id == uid && x.Ativo))
            throw new RegraNegocioException(nameof(form.UsuarioId), "Usuário não encontrado ou inativo.");

        // com quem: usuário > pessoa do cadastro > nome digitado (cadastrado agora, se pedido). Nome e telefone ficam
        // gravados no movimento como estavam na saída.
        Guid? pessoaId = null;
        string? nome = null, telefone = null;
        if (form.UsuarioId is null)
        {
            telefone = string.IsNullOrWhiteSpace(form.PessoaTelefone) ? null : Formatos.TelefoneFormatado(form.PessoaTelefone);
            if (form.PessoaId is { } pid)
            {
                var p = await db.Pessoas.AsNoTracking().Where(x => x.Id == pid && x.ExcluidoEm == null)
                    .Select(x => new { x.NomeRazao, Fone = x.Whatsapp ?? x.Telefone }).SingleOrDefaultAsync()
                    ?? throw new RegraNegocioException(nameof(form.PessoaId), "Pessoa não encontrada.");
                (pessoaId, nome, telefone) = (pid, p.NomeRazao, p.Fone); // o telefone do cadastro vale
            }
            else
            {
                nome = form.PessoaNome!.Trim();
                if (form.CadastrarPessoa)
                    pessoaId = await pessoas.CriarAsync(u, new Pessoas.PessoaForm { NomeRazao = nome, Telefone = telefone, Whatsapp = telefone });
            }
        }

        var m = new ChaveMovimento
        {
            ChaveId = chaveId,
            UsuarioId = form.UsuarioId,
            PessoaId = pessoaId,
            PessoaNome = nome,
            PessoaTelefone = telefone,
            Motivo = form.Motivo,
            MotivoDetalhe = Texto.Limpo(form.MotivoDetalhe),
            RetiradaEm = DateTime.UtcNow,
            PrevisaoDevolucao = DatasBR.ParaUtc(form.DevolverAte!.Value),
            RegistradoPor = u.Id,
        };
        db.ChaveMovimentos.Add(m);
        audit.Registrar(db, u.Id, "chave_movimentos", m.Id, "ENTREGAR", depois: m);
        await db.SaveChangesAsync();
    }

    /// <summary>Registra a devolução da chave que está fora.</summary>
    public async Task ReceberAsync(UsuarioAtual u, Guid chaveId, string? observacao)
    {
        u.Exigir(P.ChavesMovimentar);
        await using var db = await fabrica.CreateDbContextAsync();
        await ChaveComAcesso(db, u, chaveId);
        var m = await db.ChaveMovimentos.SingleOrDefaultAsync(x => x.ChaveId == chaveId && x.DevolvidaEm == null)
            ?? throw new RegraNegocioException("Chave", "Esta chave não está fora.");
        m.DevolvidaEm = DateTime.UtcNow;
        m.DevolucaoRegistradaPor = u.Id;
        var obs = Texto.Limpo(observacao);
        m.ObservacaoDevolucao = obs is { Length: > 300 } ? obs[..300] : obs;
        audit.Registrar(db, u.Id, "chave_movimentos", m.Id, "RECEBER", depois: m);
        await db.SaveChangesAsync();
    }

    /* ------------------------------ exportação ------------------------------ */

    static ChavesService() => QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

    private sealed record LinhaExport(ChaveResumo Chave, string? Endereco);

    /// <summary>Chaves (de um imóvel ou de todos os visíveis), com endereço; uma etiqueta só, se <paramref name="chaveId"/>.</summary>
    private static async Task<List<LinhaExport>> ParaExportar(InovabensDb db, UsuarioAtual u, Guid? imovelId, Guid? chaveId)
    {
        if (imovelId is { } im) await Acesso.ImovelComAcesso(db, u, im, rastrear: false);
        var visiveis = db.Imoveis.VisiveisPara(u).Where(i => i.ExcluidoEm == null).Select(i => i.Id);
        var q = db.Chaves.Where(c => c.ExcluidoEm == null && visiveis.Contains(c.ImovelId));
        if (imovelId is { } i2) q = q.Where(c => c.ImovelId == i2);
        if (chaveId is { } ch) q = q.Where(c => c.Id == ch);
        var chaves = await Resumos(db, q);
        var ids = chaves.Select(c => c.ImovelId).Distinct().ToList();
        var enderecos = (await db.Imoveis.AsNoTracking().Where(i => ids.Contains(i.Id))
                .Select(i => new { i.Id, i.Logradouro, i.Numero, i.Bairro, i.Cidade }).ToListAsync())
            .ToDictionary(i => i.Id, i => string.Join(" · ", new[]
            {
                string.Join(", ", new[] { i.Logradouro, i.Numero }.Where(s => !string.IsNullOrWhiteSpace(s))), i.Bairro, i.Cidade,
            }.Where(s => !string.IsNullOrWhiteSpace(s))));
        return chaves.OrderBy(c => c.ImovelCodigo).ThenBy(c => c.Codigo)
            .Select(c => new LinhaExport(c, enderecos.GetValueOrDefault(c.ImovelId) is { Length: > 0 } e ? e : null)).ToList();
    }

    public static string NomeSituacao(SituacaoChave s) => s switch
    {
        SituacaoChave.DISPONIVEL => "Disponível",
        SituacaoChave.FORA => "Fora",
        _ => "Atrasada",
    };

    /// <summary>
    /// Planilha para a conferência do chaveiro: aba "Chaves" com a situação atual e aba "Histórico" com as saídas.
    /// Sem <paramref name="imovelId"/>, todas as chaves dos imóveis visíveis.
    /// </summary>
    public async Task<Arquivo> ExcelAsync(UsuarioAtual u, Guid? imovelId)
    {
        u.Exigir(P.ChavesVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var linhas = await ParaExportar(db, u, imovelId, null);
        var chaveIds = linhas.Select(l => l.Chave.Id).ToList();
        var historico = await Movimentos(db, db.ChaveMovimentos.Where(m => chaveIds.Contains(m.ChaveId))
            .OrderByDescending(m => m.RetiradaEm)).ToListAsync();
        var imovelDaChave = linhas.ToDictionary(l => l.Chave.Id, l => l.Chave.ImovelCodigo);

        using var wb = new ClosedXML.Excel.XLWorkbook();
        static ClosedXML.Excel.XLCellValue T(string? s) => string.IsNullOrEmpty(s) ? ClosedXML.Excel.Blank.Value : s;
        static ClosedXML.Excel.XLCellValue D(DateTime? utc) => utc is { } d ? DatasBR.DeUtc(d) : ClosedXML.Excel.Blank.Value;
        var ws = RelatoriosService.Aba(wb, "Chaves",
        [
            ("Chave", 10), ("Descrição", 26), ("Imóvel", 10), ("Título do imóvel", 36), ("Endereço", 40), ("Situação", 11),
            ("Com quem", 24), ("Telefone", 16), ("Motivo", 20), ("Saiu em", 16), ("Devolver até", 16), ("Observação", 30),
        ],
        linhas.Select(l => new ClosedXML.Excel.XLCellValue[]
        {
            l.Chave.Codigo, l.Chave.Descricao, l.Chave.ImovelCodigo, l.Chave.ImovelTitulo, T(l.Endereco), NomeSituacao(l.Chave.Situacao),
            T(l.Chave.Fora?.ComQuem), T(l.Chave.Fora?.Telefone), T(l.Chave.Fora?.Motivo), D(l.Chave.Fora?.RetiradaEm),
            D(l.Chave.Fora?.PrevisaoDevolucao), T(l.Chave.Observacao),
        }));
        ws.Columns(10, 11).Style.NumberFormat.Format = "dd/mm/yyyy hh:mm";
        if (linhas.Count > 0) ws.Range(1, 1, linhas.Count + 1, 12).SetAutoFilter();

        var wh = RelatoriosService.Aba(wb, "Histórico",
        [
            ("Chave", 10), ("Imóvel", 10), ("Com quem", 24), ("Telefone", 16), ("Motivo", 22), ("Saiu em", 16), ("Devolver até", 16),
            ("Voltou em", 16), ("Atraso", 9), ("Registrou a saída", 22), ("Recebeu", 22), ("Observação da devolução", 30),
        ],
        historico.Select(m => new ClosedXML.Excel.XLCellValue[]
        {
            m.ChaveCodigo, imovelDaChave.GetValueOrDefault(m.ChaveId) ?? "", m.ComQuem, T(m.Telefone), m.Motivo, D(m.RetiradaEm),
            D(m.PrevisaoDevolucao), D(m.DevolvidaEm),
            (m.DevolvidaEm ?? DateTime.UtcNow) > m.PrevisaoDevolucao ? "Sim" : "Não",
            T(m.RegistradoPor), T(m.RecebidoPor), T(m.ObservacaoDevolucao),
        }));
        wh.Columns(6, 8).Style.NumberFormat.Format = "dd/mm/yyyy hh:mm";
        if (historico.Count > 0) wh.Range(1, 1, historico.Count + 1, 12).SetAutoFilter();

        audit.Registrar(db, u.Id, "chaves", null, "EXPORTAR", depois: new
        {
            relatorio = "chaves", formato = "xlsx", imovelId, linhas = linhas.Count, movimentos = historico.Count,
        });
        await db.SaveChangesAsync();
        var nome = imovelId is null ? "chaves" : $"chaves-{(linhas.FirstOrDefault()?.Chave.ImovelCodigo ?? "imovel")}";
        return new Arquivo(RelatoriosService.Salvar(wb), $"{nome}-{DatasBR.Hoje():yyyy-MM-dd}.xlsx", RelatoriosService.TipoXlsx);
    }

    // Etiqueta de chaveiro em folha A4: 3 colunas x 8 linhas (63 x 33 mm), com linha tracejada para recortar
    private const int ColunasEtiqueta = 3, LinhasEtiqueta = 8;

    /// <summary>
    /// PDF de etiquetas para recortar e prender no chaveiro: código grande, imóvel e descrição da chave.
    /// Uma chave (<paramref name="chaveId"/>), as de um imóvel ou todas as visíveis.
    /// </summary>
    public async Task<Arquivo> EtiquetasPdfAsync(UsuarioAtual u, Guid? imovelId, Guid? chaveId)
    {
        u.Exigir(P.ChavesVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var linhas = await ParaExportar(db, u, imovelId, chaveId);
        if (linhas.Count == 0) throw new RegraNegocioException("Chave", "Nenhuma chave para gerar etiqueta.");

        var pdf = QuestPDF.Fluent.Document.Create(doc =>
        {
            foreach (var folha in linhas.Chunk(ColunasEtiqueta * LinhasEtiqueta))
                doc.Page(p =>
                {
                    p.Size(QuestPDF.Helpers.PageSizes.A4);
                    p.Margin(10, QuestPDF.Infrastructure.Unit.Millimetre);
                    p.DefaultTextStyle(t => t.FontSize(8).FontColor("#0F172A"));
                    p.Content().Table(t =>
                    {
                        t.ColumnsDefinition(c => { for (var i = 0; i < ColunasEtiqueta; i++) c.RelativeColumn(); });
                        foreach (var l in folha)
                            t.Cell().Height(33, QuestPDF.Infrastructure.Unit.Millimetre)
                                .Border(0.6f).BorderColor("#94A3B8")
                                .Padding(3, QuestPDF.Infrastructure.Unit.Millimetre).Column(col =>
                                {
                                    col.Spacing(1);
                                    col.Item().Row(r =>
                                    {
                                        r.RelativeItem().Text(l.Chave.Codigo).FontSize(20).Bold().FontColor("#1E3A8A");
                                        r.AutoItem().AlignMiddle().Text("INOVABENS").FontSize(6.5f).Bold().FontColor("#64748B");
                                    });
                                    col.Item().Text($"Imóvel {l.Chave.ImovelCodigo}").FontSize(10).Bold();
                                    col.Item().Text(l.Chave.Descricao).FontSize(8.5f).ClampLines(1);
                                    if (l.Endereco is not null) col.Item().Text(l.Endereco).FontSize(6.5f).FontColor("#64748B").ClampLines(2);
                                });
                    });
                });
        }).WithMetadata(new QuestPDF.Infrastructure.DocumentMetadata { Title = "Etiquetas de chaves", Author = "Inovabens" }).GeneratePdf();

        audit.Registrar(db, u.Id, "chaves", chaveId, "EXPORTAR", depois: new
        {
            relatorio = "etiquetas-chaves", formato = "pdf", imovelId, etiquetas = linhas.Count,
        });
        await db.SaveChangesAsync();
        var nome = chaveId is not null ? $"etiqueta-{linhas[0].Chave.Codigo}"
            : imovelId is not null ? $"etiquetas-chaves-{linhas[0].Chave.ImovelCodigo}" : "etiquetas-chaves";
        return new Arquivo(pdf, $"{nome}.pdf", RelatoriosService.TipoPdf);
    }

    /* ------------------------------ auxiliares ------------------------------ */

    private static string Codigo(string s) => s.Trim().ToUpperInvariant();

    private static async Task CodigoLivre(InovabensDb db, string codigo, Guid? atual)
    {
        if (await db.Chaves.AnyAsync(c => c.Codigo.ToUpper() == codigo && c.Id != atual))
            throw new RegraNegocioException(nameof(ChaveForm.Codigo), $"Já existe uma chave com o código {codigo}.");
    }

    private static async Task<Chave> ChaveComAcesso(InovabensDb db, UsuarioAtual u, Guid id)
    {
        var c = await db.Chaves.SingleOrDefaultAsync(x => x.Id == id && x.ExcluidoEm == null)
            ?? throw new NaoEncontradoException("Chave não encontrada.");
        await Acesso.ImovelComAcesso(db, u, c.ImovelId, rastrear: false);
        return c;
    }

    private static async Task<List<ChaveResumo>> Resumos(InovabensDb db, IQueryable<Chave> q)
    {
        var chaves = await q.AsNoTracking()
            .Select(c => new { c.Id, c.ImovelId, ImCod = c.Imovel!.Codigo, ImTit = c.Imovel.Titulo, c.Codigo, c.Descricao, c.Observacao })
            .ToListAsync();
        var ids = chaves.Select(c => c.Id).ToList();
        var abertos = await Movimentos(db, db.ChaveMovimentos.Where(m => ids.Contains(m.ChaveId) && m.DevolvidaEm == null))
            .ToDictionaryAsync(m => m.ChaveId);
        var agora = DateTime.UtcNow;
        return chaves.Select(c =>
        {
            var fora = abertos.GetValueOrDefault(c.Id);
            var sit = fora is null ? SituacaoChave.DISPONIVEL : fora.Atrasada(agora) ? SituacaoChave.ATRASADA : SituacaoChave.FORA;
            return new ChaveResumo(c.Id, c.ImovelId, c.ImCod, c.ImTit, c.Codigo, c.Descricao, c.Observacao, fora, sit);
        }).ToList();
    }

    private static IQueryable<MovimentoChave> Movimentos(InovabensDb db, IQueryable<ChaveMovimento> q) =>
        q.AsNoTracking().Select(m => new MovimentoChave(
            m.Id, m.ChaveId, m.Chave!.Codigo,
            m.Usuario != null ? m.Usuario.Nome : m.PessoaNome ?? "—",
            m.PessoaTelefone, m.UsuarioId != null,
            m.MotivoDetalhe != null ? (m.Motivo == MotivoOutro ? m.MotivoDetalhe : m.Motivo + " · " + m.MotivoDetalhe) : m.Motivo,
            m.RetiradaEm, m.PrevisaoDevolucao, m.DevolvidaEm, m.ObservacaoDevolucao,
            db.Usuarios.Where(x => x.Id == m.RegistradoPor).Select(x => x.Nome).FirstOrDefault(),
            db.Usuarios.Where(x => x.Id == m.DevolucaoRegistradaPor).Select(x => x.Nome).FirstOrDefault()));
}
