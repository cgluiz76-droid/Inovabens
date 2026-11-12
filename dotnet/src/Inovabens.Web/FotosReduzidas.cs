using Inovabens.Core.Imoveis;
using Microsoft.Extensions.Caching.Memory;

namespace Inovabens.Web;

/// <summary>
/// Fotos do Drive reduzidas para a tela (?l=400 nas listas e miniaturas, ?l=1200 nas fotos grandes), em WebP.
/// A foto original pode ter vários MB; a reduzida tem dezenas de KB. O resultado fica em memória (até 64 MB):
/// o id do arquivo do Drive faz parte da chave, então foto trocada nunca reaproveita a versão antiga.
/// </summary>
public sealed class FotosReduzidas : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 64L * 1024 * 1024 });

    public static bool Reduzivel(string contentType) => ImagemFoto.Suportado(contentType);

    /// <summary>Versão WebP com a largura pedida (nunca amplia); null se a imagem não decodificar.</summary>
    public byte[]? Obter(string arquivoId, int largura, Func<byte[]> original)
    {
        var chave = (arquivoId, largura);
        if (cache.TryGetValue(chave, out byte[]? pronto)) return pronto;
        var versao = ImagemFoto.VersoesWeb(original(), [largura])?[0];
        if (versao is not null)
            cache.Set(chave, versao, new MemoryCacheEntryOptions { Size = versao.Length, SlidingExpiration = TimeSpan.FromHours(12) });
        return versao;
    }

    /// <summary>Já reduzida e em memória? (evita baixar o original do Drive).</summary>
    public byte[]? EmMemoria(string arquivoId, int largura) => cache.TryGetValue((arquivoId, largura), out byte[]? v) ? v : null;

    public void Dispose() => cache.Dispose();
}
