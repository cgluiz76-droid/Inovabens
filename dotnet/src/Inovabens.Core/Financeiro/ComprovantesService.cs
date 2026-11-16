using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Financeiro;

public sealed record ComprovanteDetalhe(Guid Id, string Nome, string Tipo, long Tamanho, DateTime CriadoEm);

/// <summary>
/// Comprovantes dos lançamentos financeiros, gravados no Google Drive em
/// "&lt;pasta do imóvel&gt;/07 - Financeiro/&lt;ano&gt;". Ver, anexar e remover exige editar o financeiro.
/// </summary>
public sealed class ComprovantesService(
    IDbContextFactory<InovabensDb> fabrica, IArmazenamento armazenamento, PastasDriveService pastas, AuditService audit)
{
    public const long TamanhoMaximo = ImoveisService.TamanhoMaximoArquivo;
    public const int MaximoPorEnvio = 10;

    public bool Habilitado => armazenamento.Habilitado;

    public async Task<IReadOnlyList<ComprovanteDetalhe>> ListarAsync(UsuarioAtual u, Guid lancamentoId)
    {
        u.Exigir(P.FinanceiroEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        await Resolver(db, u, lancamentoId);
        return await db.ComprovantesFinanceiros.AsNoTracking().Where(c => c.LancamentoId == lancamentoId).OrderBy(c => c.CriadoEm)
            .Select(c => new ComprovanteDetalhe(c.Id, c.NomeOriginal, c.TipoConteudo, c.Tamanho, c.CriadoEm))
            .ToListAsync();
    }

    /// <summary>Quantos comprovantes cada lançamento do imóvel tem (para o ícone de anexo).</summary>
    public async Task<IReadOnlyDictionary<Guid, int>> ContagemAsync(UsuarioAtual u, Guid imovelId)
    {
        if (!u.Pode(P.FinanceiroEditar)) return new Dictionary<Guid, int>();
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        return await db.ComprovantesFinanceiros.AsNoTracking().Where(c => c.ImovelId == imovelId)
            .GroupBy(c => c.LancamentoId)
            .ToDictionaryAsync(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Grava o arquivo no Drive e o vincula. Nome no Drive: "AAAA-MM-DD CA0013-0004 Condomínio - boleto.pdf",
    /// na pasta do ano do lançamento.
    /// </summary>
    public async Task<Guid> EnviarAsync(UsuarioAtual u, Guid lancamentoId, Stream conteudo, string nomeOriginal, string contentType,
        long tamanho, CancellationToken ct = default)
    {
        u.Exigir(P.FinanceiroEditar);
        if (!armazenamento.Habilitado)
            throw new ServicoIndisponivelException("Google Drive não configurado: configure em Administração → Configurações.");
        if (tamanho > TamanhoMaximo)
            throw new RegraNegocioException("arquivo", $"Arquivo maior que {TamanhoMaximo / 1024 / 1024} MB.");
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var (imovelId, data, rotulo) = await Resolver(db, u, lancamentoId);

        var nome = Path.GetFileName(nomeOriginal.Trim());
        if (nome.Length == 0) nome = "comprovante";
        if (nome.Length > 200) nome = nome[..200];
        var nomeDrive = $"{data:yyyy-MM-dd} {rotulo} - {nome}";
        if (nomeDrive.Length > 255) nomeDrive = nomeDrive[..255];

        var tipo = RegrasImovel.TipoInformado(contentType);
        var pasta = await pastas.PastaFinanceiroAsync(imovelId, data.Year, ct);
        var chave = await PastasDriveService.Chamar(() => armazenamento.EnviarAsync(pasta, nomeDrive, conteudo, tipo, ct));

        var c = new ComprovanteFinanceiro
        {
            ImovelId = imovelId, LancamentoId = lancamentoId, ArquivoChave = chave,
            NomeOriginal = nome, TipoConteudo = tipo, Tamanho = tamanho, CriadoPor = u.Id,
        };
        db.ComprovantesFinanceiros.Add(c);
        audit.Registrar(db, u.Id, "financeiro_comprovantes", c.Id, "CRIAR", depois: c);
        await db.SaveChangesAsync(ct);
        return c.Id;
    }

    /// <summary>Arquivo para o navegador: o Drive é privado, o sistema confere o acesso e repassa.</summary>
    public async Task<ArquivoMidia> ArquivoAsync(UsuarioAtual u, Guid id, CancellationToken ct = default)
    {
        u.Exigir(P.FinanceiroEditar);
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var c = await ComAcesso(db, u, id);
        var conteudo = await armazenamento.BaixarAsync(c.ArquivoChave, ct) ?? throw new NaoEncontradoException("Arquivo não encontrado no Google Drive.");
        // só imagem, vídeo e PDF abrem no navegador; o resto é baixado
        var seguro = RegrasImovel.ArquivoSeguro(c.NomeOriginal, conteudo.Tipo);
        return new ArquivoMidia(conteudo.Dados, seguro.ContentType, c.NomeOriginal, seguro.Download);
    }

    /// <summary>Desvincula e manda o arquivo para a lixeira do Drive (recuperável por 30 dias).</summary>
    public async Task RemoverAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.FinanceiroEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await ComAcesso(db, u, id);
        db.ComprovantesFinanceiros.Remove(c);
        audit.Registrar(db, u.Id, "financeiro_comprovantes", id, "EXCLUIR", antes: c);
        await db.SaveChangesAsync();
        await armazenamento.RemoverAsync(c.ArquivoChave);
    }

    private static async Task<ComprovanteFinanceiro> ComAcesso(InovabensDb db, UsuarioAtual u, Guid id)
    {
        var c = await db.ComprovantesFinanceiros.SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Comprovante não encontrado.");
        await Acesso.ImovelComAcesso(db, u, c.ImovelId, rastrear: false);
        return c;
    }

    /// <summary>Imóvel, data (define o ano da pasta) e rótulo do nome do arquivo, conferindo o acesso ao imóvel.</summary>
    private static async Task<(Guid ImovelId, DateOnly Data, string Rotulo)> Resolver(InovabensDb db, UsuarioAtual u, Guid lancamentoId)
    {
        var x = await db.LancamentosFinanceiros.AsNoTracking().Where(l => l.Id == lancamentoId && l.ExcluidoEm == null)
            .Select(l => new { l.ImovelId, l.Data, l.Numero, l.Descricao, Codigo = db.Imoveis.Where(i => i.Id == l.ImovelId).Select(i => i.Codigo).First() })
            .SingleOrDefaultAsync() ?? throw new NaoEncontradoException("Lançamento não encontrado.");
        await Acesso.ImovelComAcesso(db, u, x.ImovelId, rastrear: false);
        var r = $"{LancamentosFinanceirosService.FormatarNumero(x.Codigo, x.Numero)} {x.Descricao}";
        // nome de arquivo no Drive: sem barras nem caracteres que o Windows recusa ao baixar a pasta
        var rotulo = string.Concat(r.Select(ch => "\\/:*?\"<>|".Contains(ch) ? '-' : ch));
        return (x.ImovelId, x.Data, rotulo.Length > 60 ? rotulo[..60].TrimEnd() : rotulo);
    }
}
