using System.Security.Cryptography;
using System.Text;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inovabens.Core.Imoveis;

/// <summary>
/// Mantém no Cloudflare R2 a cópia pública das fotos marcadas como públicas (site e portais). A rotina
/// <c>PublicacaoFotosJob</c> (a cada minuto) compara o que deveria estar publicado com o que está:
/// publica fotos novas ou trocadas (ex.: melhoradas pelo Magnific) e retira as que deixaram de ser públicas ou cujo
/// imóvel foi excluído. Os originais nunca saem do Google Drive privado.
/// </summary>
public sealed class PublicacaoFotosService(
    IDbContextFactory<InovabensDb> fabrica, IArmazenamento drive, IArmazenamentoPublico publico,
    ILogger<PublicacaoFotosService> log)
{
    /// <summary>Larguras máximas geradas: miniatura (listas), ficha e tela cheia.</summary>
    public static readonly IReadOnlyList<int> Larguras = [400, 1200, 1920];
    public const int LarguraFicha = 1200;
    private const int PorRodada = 10;

    public bool Habilitada => publico.Habilitado && drive.Habilitado;

    public static string ChaveVersao(string prefixo, int largura) => $"{prefixo}-{largura}.webp";

    /// <summary>URL pública da versão pedida, se a foto estiver publicada e o endereço público configurado.</summary>
    public string? Url(ImovelMidia m, int largura = LarguraFicha) =>
        m.PublicaChave is { } p ? publico.Url(ChaveVersao(p, largura)) : null;

    /// <summary>Estado da foto em relação ao site.</summary>
    public static SituacaoPublicacao Situacao(ImovelMidia m) => m switch
    {
        { Tipo: not TipoMidia.FOTO } or { Publica: false } => SituacaoPublicacao.Privada,
        { PublicacaoErro: not null } when m.PublicadaArquivo == m.ArquivoChave => SituacaoPublicacao.Falha,
        { PublicaChave: not null } when m.PublicadaArquivo == m.ArquivoChave => SituacaoPublicacao.Publicada,
        _ => SituacaoPublicacao.Pendente,
    };

    /// <summary>Uma rodada: publica e retira. Devolve quantas fotos foram publicadas.</summary>
    public async Task<int> ProcessarAsync(CancellationToken ct = default)
    {
        if (!Habilitada) return 0;
        await using var db = await fabrica.CreateDbContextAsync(ct);

        var aPublicar = await db.ImovelMidias
            .Where(m => m.Tipo == TipoMidia.FOTO && m.Publica && m.ArquivoChave != ""
                && m.PublicadaArquivo != m.ArquivoChave
                // espera o Magnific terminar para não publicar duas vezes
                && (m.MelhoriaStatus == null || (m.MelhoriaStatus != StatusMelhoria.PENDENTE && m.MelhoriaStatus != StatusMelhoria.PROCESSANDO))
                && db.Imoveis.Any(i => i.Id == m.ImovelId && i.ExcluidoEm == null))
            .OrderBy(m => m.CriadoEm)
            .Take(PorRodada)
            .ToListAsync(ct);

        var publicadas = 0;
        foreach (var m in aPublicar)
        {
            string? anterior = m.PublicaChave, nova = null;
            try
            {
                nova = await Publicar(m, ct);
            }
            catch (PublicacaoRecusadaException e)
            {
                // problema da própria foto: não tenta de novo até ela mudar ou ser marcada como pública outra vez
                log.LogWarning("Foto {Midia} não publicada: {Motivo}", m.Id, e.Message);
                m.PublicacaoErro = e.Message;
                m.PublicadaArquivo = m.ArquivoChave;
                anterior = null;
            }
            catch (Exception e) when (e is HttpRequestException or Amazon.S3.AmazonS3Exception or Amazon.Runtime.AmazonServiceException
                                          or Google.GoogleApiException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                log.LogWarning("Publicação da foto {Midia} adiada: {Erro}", m.Id, e.Message); // tenta na próxima rodada
                continue;
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // a foto foi excluída enquanto era publicada: retira o que foi gerado
                db.Entry(m).State = EntityState.Detached;
                await RemoverCopiaAsync(nova);
                continue;
            }
            if (nova is null) continue;
            publicadas++;
            if (anterior is not null && anterior != nova) await RemoverCopiaAsync(anterior);
        }

        var aRetirar = await db.ImovelMidias
            .Where(m => m.PublicaChave != null
                && (!m.Publica || m.Tipo != TipoMidia.FOTO || !db.Imoveis.Any(i => i.Id == m.ImovelId && i.ExcluidoEm == null)))
            .Take(PorRodada * 5)
            .ToListAsync(ct);
        foreach (var m in aRetirar)
        {
            await RemoverCopiaAsync(m.PublicaChave);
            m.PublicaChave = m.PublicadaArquivo = m.PublicacaoErro = null;
            m.PublicadaEm = null;
        }
        if (aRetirar.Count > 0) await db.SaveChangesAsync(ct);
        return publicadas;
    }

    /// <summary>Gera as versões WebP e grava no R2; devolve o prefixo novo (o banco é gravado por quem chamou).</summary>
    private async Task<string> Publicar(ImovelMidia m, CancellationToken ct)
    {
        var original = await drive.BaixarAsync(m.ArquivoChave, ct)
            ?? throw new PublicacaoRecusadaException("A foto não foi encontrada no Google Drive.");
        var versoes = ImagemFoto.VersoesWeb(original.Dados, Larguras)
            ?? throw new PublicacaoRecusadaException("Formato de imagem não suportado no site. Envie em JPG, PNG ou WebP.");

        // o id do arquivo do Drive entra na chave: foto trocada = endereço novo (cache do navegador/CDN não atrapalha)
        var prefixo = $"imoveis/{m.ImovelId:N}/{m.Id:N}-{Resumo(m.ArquivoChave)}";
        for (var i = 0; i < Larguras.Count; i++)
            await publico.EnviarAsync(ChaveVersao(prefixo, Larguras[i]), versoes[i], "image/webp", ct);

        m.PublicaChave = prefixo;
        m.PublicadaArquivo = m.ArquivoChave;
        m.PublicadaEm = DateTime.UtcNow;
        m.PublicacaoErro = null;
        log.LogInformation("Foto {Midia} publicada no R2", m.Id);
        return prefixo;
    }

    /// <summary>Apaga as versões públicas da foto (sem erro se não existirem).</summary>
    public async Task RemoverCopiaAsync(string? prefixo)
    {
        if (prefixo is null || !publico.Habilitado) return;
        foreach (var largura in Larguras) await publico.RemoverAsync(ChaveVersao(prefixo, largura));
    }

    private static string Resumo(string texto) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(texto)))[..10];
}

public enum SituacaoPublicacao { Privada, Pendente, Publicada, Falha }

/// <summary>A foto em si não pode ser publicada (não existe mais no Drive, formato não suportado...).</summary>
public sealed class PublicacaoRecusadaException(string mensagem) : Exception(mensagem);
