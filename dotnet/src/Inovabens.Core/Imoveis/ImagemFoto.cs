using SkiaSharp;

namespace Inovabens.Core.Imoveis;

/// <summary>Tratamento de fotos com SkiaSharp: aplica a orientação EXIF, reduz e grava em JPEG.</summary>
public static class ImagemFoto
{
    /// <summary>
    /// Lado maior da foto enviada ao Magnific. Com ampliação 2x o resultado fica com até 4096 px, tamanho bom para
    /// site e impressão, e o custo (cobrado pela área gerada) fica previsível mesmo com fotos de celular de 12+ MP.
    /// </summary>
    public const int LadoMaximoEntrada = 2048;

    /// <summary>Formatos que a melhoria aceita (HEIC/AVIF/GIF ficam como foram enviados).</summary>
    public static bool Suportado(string contentType) => contentType is "image/jpeg" or "image/png" or "image/webp";

    /// <summary>Foto pronta para o Magnific: na orientação correta, com até <see cref="LadoMaximoEntrada"/> px, em JPEG. Null se não decodificar.</summary>
    public static byte[]? PrepararEntrada(byte[] dados) => Converter(dados, LadoMaximoEntrada, qualidade: 92);

    /// <summary>Resultado do Magnific (PNG/JPEG grande) regravado como JPEG para ocupar menos espaço.</summary>
    public static byte[]? PrepararResultado(byte[] dados) => Converter(dados, ladoMaximo: null, qualidade: 90);

    /// <summary>
    /// Versões da foto para o site, em WebP, uma por largura máxima (nunca amplia). A regravação descarta o EXIF,
    /// inclusive a localização GPS que o celular grava na foto. Null se não decodificar.
    /// </summary>
    public static IReadOnlyList<byte[]>? VersoesWeb(byte[] dados, IReadOnlyList<int> larguras, int qualidade = 80)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(dados));
        if (codec is null) return null;
        using var decodificada = SKBitmap.Decode(codec);
        if (decodificada is null) return null;
        using var orientada = Orientar(decodificada, codec.EncodedOrigin);
        var bitmap = orientada ?? decodificada;

        var versoes = new List<byte[]>(larguras.Count);
        foreach (var largura in larguras)
        {
            var escala = Math.Min(1d, largura / (double)bitmap.Width);
            using var reduzida = escala < 1
                ? bitmap.Resize(new SKImageInfo((int)Math.Round(bitmap.Width * escala), Math.Max(1, (int)Math.Round(bitmap.Height * escala))),
                    new SKSamplingOptions(SKCubicResampler.Mitchell))
                : null;
            using var imagem = SKImage.FromBitmap(reduzida ?? bitmap);
            using var webp = imagem.Encode(SKEncodedImageFormat.Webp, qualidade);
            if (webp is null) return null;
            versoes.Add(webp.ToArray());
        }
        return versoes;
    }

    private static byte[]? Converter(byte[] dados, int? ladoMaximo, int qualidade)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(dados));
        if (codec is null) return null;
        using var decodificada = SKBitmap.Decode(codec);
        if (decodificada is null) return null;

        using var orientada = Orientar(decodificada, codec.EncodedOrigin);
        var bitmap = orientada ?? decodificada;
        var escala = ladoMaximo is { } max ? Math.Min(1d, max / (double)Math.Max(bitmap.Width, bitmap.Height)) : 1d;
        using var reduzida = escala < 1
            ? bitmap.Resize(new SKImageInfo((int)Math.Round(bitmap.Width * escala), (int)Math.Round(bitmap.Height * escala)),
                new SKSamplingOptions(SKCubicResampler.Mitchell))
            : null;

        using var imagem = SKImage.FromBitmap(reduzida ?? bitmap);
        using var jpeg = imagem.Encode(SKEncodedImageFormat.Jpeg, qualidade);
        return jpeg?.ToArray();
    }

    /// <summary>Gira a imagem conforme o EXIF (fotos de celular). Espelhamentos, raros, ficam como estão. Null = já está certa.</summary>
    private static SKBitmap? Orientar(SKBitmap b, SKEncodedOrigin origem)
    {
        var graus = origem switch
        {
            SKEncodedOrigin.BottomRight => 180,
            SKEncodedOrigin.RightTop => 90,
            SKEncodedOrigin.LeftBottom => 270,
            _ => 0,
        };
        if (graus == 0) return null;
        var (w, h) = graus == 180 ? (b.Width, b.Height) : (b.Height, b.Width);
        var r = new SKBitmap(w, h, b.ColorType, b.AlphaType);
        using var c = new SKCanvas(r);
        c.Translate(w / 2f, h / 2f);
        c.RotateDegrees(graus);
        c.Translate(-b.Width / 2f, -b.Height / 2f);
        using var imagem = SKImage.FromBitmap(b);
        c.DrawImage(imagem, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest)); // giro de 90/180°: sem interpolação
        return r;
    }
}
