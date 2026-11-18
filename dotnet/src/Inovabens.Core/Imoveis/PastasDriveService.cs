using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;

namespace Inovabens.Core.Imoveis;

/// <summary>Subpastas fixas da pasta de cada imóvel no Google Drive.</summary>
public static class SubpastasDrive
{
    public const int Matricula = 1;
    public const int Projetos = 2;
    public const int Comercial = 3;
    public const int FotosVideos = 4;
    public const int Documentos = 5;
    public const int Protocolos = 6;

    public static readonly IReadOnlyList<(int Numero, string Nome)> Todas =
    [
        (Matricula, "01 - Matrícula"),
        (Projetos, "02 - Projetos"),
        (Comercial, "03 - Comercial"),
        (FotosVideos, "04 - Fotos e Vídeos"),
        (Documentos, "05 - Documentos em Geral"),
        (Protocolos, "06 - Protocolos"),
    ];

    /// <summary>Comprovantes de receitas e despesas (por ano). Só o financeiro grava aqui; não é opção no envio de mídias.</summary>
    public const string Financeiro = "07 - Financeiro";

    public static bool Valida(int numero) => numero is >= Matricula and <= Protocolos;

    public static string Nome(int numero) =>
        Valida(numero) ? Todas[numero - 1].Nome : throw new RegraNegocioException("subpasta", "Selecione a pasta de destino.");

    /// <summary>
    /// Permissão para ver e enviar arquivos da subpasta. "04 - Fotos e Vídeos" não tem (basta imoveis:ver); as demais
    /// guardam documentos (matrícula, contratos, protocolos) e só aparecem para quem tem a permissão da pasta.
    /// </summary>
    public static string? Permissao(int numero) => numero switch
    {
        Matricula => Permissoes.ImoveisPastaMatricula,
        Projetos => Permissoes.ImoveisPastaProjetos,
        Comercial => Permissoes.ImoveisPastaComercial,
        Documentos => Permissoes.ImoveisPastaDocumentos,
        Protocolos => Permissoes.ImoveisPastaProtocolos,
        _ => null,
    };

    public static bool PodeVer(UsuarioAtual u, int numero) => Permissao(numero) is not { } p || u.Pode(p);

    /// <summary>Subpastas que o usuário enxerga, na ordem do Drive ("04" sempre).</summary>
    public static IReadOnlyList<(int Numero, string Nome)> Visiveis(UsuarioAtual u) => [.. Todas.Where(s => PodeVer(u, s.Numero))];

    /// <summary>Subpasta da mídia; as antigas, sem subpasta gravada, contam pelo tipo (foto/vídeo em "04", o resto em "05").</summary>
    public static int DaMidia(ImovelMidia m) => m.Subpasta ?? Sugerida(m.Tipo);

    public static bool PodeVer(UsuarioAtual u, ImovelMidia m) => PodeVer(u, DaMidia(m));

    /// <summary>Pasta sugerida para o arquivo: fotos e vídeos em "04", o resto em "05".</summary>
    public static int Sugerida(TipoMidia tipoArquivo) =>
        tipoArquivo is TipoMidia.FOTO or TipoMidia.VIDEO ? FotosVideos : Documentos;

    /// <summary>
    /// Só o que vai para "04 - Fotos e Vídeos" entra na galeria do imóvel (foto/vídeo, capa, site). Uma imagem em
    /// outra pasta (ex.: matrícula escaneada em JPG) é documento.
    /// </summary>
    public static TipoMidia TipoNaPasta(int numero, TipoMidia tipoArquivo) =>
        numero == FotosVideos && tipoArquivo is TipoMidia.FOTO or TipoMidia.VIDEO ? tipoArquivo : TipoMidia.DOCUMENTO;
}

/// <summary>
/// Cria e encontra a pasta de cada imóvel no drive compartilhado: "&lt;Nome para Drive&gt;/01 - Matrícula" etc.
/// Se já existir uma pasta com o mesmo nome na raiz do drive, ela é reaproveitada (e as subpastas que faltarem, criadas).
/// </summary>
public sealed class PastasDriveService(IDbContextFactory<InovabensDb> fabrica, IArmazenamento armazenamento)
{
    /// <summary>Pastas criadas junto com a do imóvel: as seis de documentos e a "07 - Financeiro" (comprovantes).</summary>
    private static IEnumerable<string> PastasPadrao => SubpastasDrive.Todas.Select(s => s.Nome).Append(SubpastasDrive.Financeiro);

    /// <summary>Garante a pasta principal e as subpastas (01 a 07); devolve o id da pasta principal.</summary>
    public async Task<string> GarantirAsync(Guid imovelId, CancellationToken ct = default)
    {
        ExigirHabilitado();
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var im = await db.Imoveis.SingleOrDefaultAsync(i => i.Id == imovelId, ct) ?? throw new NaoEncontradoException("Imóvel não encontrado.");
        if (im.DrivePastaId is { } existente) return existente;
        if (Texto.Limpo(im.NomeDrive) is not { } nome)
            throw new RegraNegocioException(nameof(Imovel.NomeDrive),
                "Preencha o campo \"Nome do Imóvel\" nos dados do imóvel antes de enviar arquivos.");

        var raiz = await Chamar(async () =>
        {
            var existente = await armazenamento.BuscarPastaAsync(nome, null, ct);
            var id = existente ?? await armazenamento.CriarPastaAsync(nome, null, ct);
            // em paralelo (cada chamada ao Drive leva ~0,5 s); pasta recém-criada não precisa de busca
            await Task.WhenAll(PastasPadrao.Select(async s =>
            {
                if (existente is null || await armazenamento.BuscarPastaAsync(s, id, ct) is null)
                    await armazenamento.CriarPastaAsync(s, id, ct);
            }));
            return id;
        });
        im.DrivePastaId = raiz;
        await db.SaveChangesAsync(ct);
        return raiz;
    }

    /// <summary>Id da subpasta do imóvel (criada de novo se alguém a apagou no Drive).</summary>
    public async Task<string> SubpastaAsync(Guid imovelId, int numero, CancellationToken ct = default)
    {
        var nome = SubpastasDrive.Nome(numero);
        var raiz = await GarantirAsync(imovelId, ct);
        return await Chamar(async () =>
            await armazenamento.BuscarPastaAsync(nome, raiz, ct) ?? await armazenamento.CriarPastaAsync(nome, raiz, ct));
    }

    /// <summary>
    /// Pasta dos comprovantes financeiros do ano: "&lt;pasta do imóvel&gt;/07 - Financeiro/&lt;ano&gt;" (criada quando
    /// falta). Fica depois das seis subpastas de documentos e não aparece no envio de mídias.
    /// </summary>
    public async Task<string> PastaFinanceiroAsync(Guid imovelId, int ano, CancellationToken ct = default)
    {
        var raiz = await GarantirAsync(imovelId, ct);
        return await Chamar(async () =>
        {
            var financeiro = await armazenamento.BuscarPastaAsync(SubpastasDrive.Financeiro, raiz, ct)
                ?? await armazenamento.CriarPastaAsync(SubpastasDrive.Financeiro, raiz, ct);
            var nomeAno = ano.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return await armazenamento.BuscarPastaAsync(nomeAno, financeiro, ct)
                ?? await armazenamento.CriarPastaAsync(nomeAno, financeiro, ct);
        });
    }

    /// <summary>
    /// Cria a "07 - Financeiro" nos imóveis que já têm pasta no Drive e ainda não a têm (comando
    /// <c>criar-pasta-financeiro</c>). Pode rodar de novo: quem já tem é pulado. Devolve (criadas, já existiam, falhas).
    /// </summary>
    public async Task<(int Criadas, int Existentes, int Falhas)> CriarPastaFinanceiroExistentesAsync(Action<string>? progresso = null,
        CancellationToken ct = default)
    {
        ExigirHabilitado();
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var imoveis = await db.Imoveis.AsNoTracking().Where(i => i.ExcluidoEm == null && i.DrivePastaId != null)
            .OrderBy(i => i.Codigo).Select(i => new { i.Codigo, Pasta = i.DrivePastaId! }).ToListAsync(ct);
        int criadas = 0, existentes = 0, falhas = 0;
        foreach (var im in imoveis)
        {
            try
            {
                if (await armazenamento.BuscarPastaAsync(SubpastasDrive.Financeiro, im.Pasta, ct) is not null) { existentes++; continue; }
                await armazenamento.CriarPastaAsync(SubpastasDrive.Financeiro, im.Pasta, ct);
                criadas++;
                progresso?.Invoke($"{im.Codigo}: pasta \"{SubpastasDrive.Financeiro}\" criada.");
            }
            catch (Exception e) when (e is Google.GoogleApiException or HttpRequestException)
            {
                // ex.: pasta do imóvel apagada no Drive; segue com os demais
                falhas++;
                progresso?.Invoke($"{im.Codigo}: falhou ({e.Message}).");
            }
        }
        return (criadas, existentes, falhas);
    }

    public Task RenomearAsync(string pastaId, string nome, CancellationToken ct = default) =>
        Chamar(async () => { await armazenamento.RenomearAsync(pastaId, nome, ct); return true; });

    public string? Link(string? pastaId) => armazenamento.LinkPasta(pastaId);

    public bool Habilitado => armazenamento.Habilitado;

    /// <summary>
    /// Pastas que ficaram com o nome antigo depois da troca de código (migração 0027): ganham o nome do cadastro
    /// ("CA0060 - Casa São Francisco"). Uma pasta que falhar fica marcada e é tentada de novo na próxima rodada.
    /// </summary>
    public async Task<int> RenomearPendentesAsync(CancellationToken ct = default)
    {
        if (!armazenamento.Habilitado) return 0;
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var pendentes = await db.Imoveis.Where(i => i.DriveRenomear).ToListAsync(ct);
        var feitos = 0;
        foreach (var im in pendentes)
        {
            if (im.DrivePastaId is { } pasta && Texto.Limpo(im.NomeDrive) is { } nome)
            {
                try { await armazenamento.RenomearAsync(pasta, nome, ct); }
                catch (Exception e) when (e is not OperationCanceledException) { continue; }
            }
            im.DriveRenomear = false;
            feitos++;
        }
        await db.SaveChangesAsync(ct);
        return feitos;
    }

    private void ExigirHabilitado()
    {
        if (!armazenamento.Habilitado)
            throw new ServicoIndisponivelException("Google Drive não configurado (GOOGLE_DRIVE_ID e GOOGLE_DRIVE_CREDENCIAIS).");
    }

    /// <summary>Falhas do Google viram mensagem para o usuário em vez de erro genérico.</summary>
    internal static async Task<T> Chamar<T>(Func<Task<T>> acao)
    {
        try
        {
            return await acao();
        }
        catch (Google.GoogleApiException e)
        {
            throw new ServicoIndisponivelException($"Google Drive recusou a operação: {e.Error?.Message ?? e.Message}");
        }
        catch (HttpRequestException e)
        {
            throw new ServicoIndisponivelException($"Google Drive indisponível: {e.Message}");
        }
    }
}
