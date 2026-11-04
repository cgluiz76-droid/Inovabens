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

    public static bool Valida(int numero) => numero is >= Matricula and <= Protocolos;

    public static string Nome(int numero) =>
        Valida(numero) ? Todas[numero - 1].Nome : throw new RegraNegocioException("subpasta", "Selecione a pasta de destino.");

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
    /// <summary>Garante a pasta principal e as seis subpastas; devolve o id da pasta principal.</summary>
    public async Task<string> GarantirAsync(Guid imovelId, CancellationToken ct = default)
    {
        ExigirHabilitado();
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var im = await db.Imoveis.SingleOrDefaultAsync(i => i.Id == imovelId, ct) ?? throw new NaoEncontradoException("Imóvel não encontrado.");
        if (im.DrivePastaId is { } existente) return existente;
        if (Texto.Limpo(im.NomeDrive) is not { } nome)
            throw new RegraNegocioException(nameof(Imovel.NomeDrive),
                "Preencha o campo \"Nome para Drive do Empreendimento\" nos dados do imóvel antes de enviar arquivos.");

        var raiz = await Chamar(async () =>
        {
            var existente = await armazenamento.BuscarPastaAsync(nome, null, ct);
            var id = existente ?? await armazenamento.CriarPastaAsync(nome, null, ct);
            // em paralelo (cada chamada ao Drive leva ~0,5 s); pasta recém-criada não precisa de busca
            await Task.WhenAll(SubpastasDrive.Todas.Select(async s =>
            {
                if (existente is null || await armazenamento.BuscarPastaAsync(s.Nome, id, ct) is null)
                    await armazenamento.CriarPastaAsync(s.Nome, id, ct);
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

    public Task RenomearAsync(string pastaId, string nome, CancellationToken ct = default) =>
        Chamar(async () => { await armazenamento.RenomearAsync(pastaId, nome, ct); return true; });

    public string? Link(string? pastaId) => armazenamento.LinkPasta(pastaId);

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
