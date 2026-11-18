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

    /// <summary>
    /// Teto do JPEG enviado ao Magnific. A foto vai em base64 dentro do JSON (+33%) e o Magnific responde 413 a
    /// corpos grandes: fotos com muito detalhe (ex.: PNG de 2048 px) passavam de 2 MB e eram recusadas.
    /// </summary>
    public const int TamanhoMaximoEntrada = 1_500_000;

    /// <summary>
    /// Foto pronta para o Magnific: na orientação correta, com até <see cref="LadoMaximoEntrada"/> px, em JPEG e com até
    /// <see cref="TamanhoMaximoEntrada"/> bytes (baixa a qualidade e, se preciso, o tamanho). Null se não decodificar.
    /// </summary>
    public static byte[]? PrepararEntrada(byte[] dados)
    {
        // primeiro baixa a qualidade (quase invisível); depois reduz o lado em 20% por vez, até 512 px
        int lado = LadoMaximoEntrada, qualidade = 92;
        while (true)
        {
            var jpeg = Converter(dados, lado, qualidade);
            if (jpeg is null || jpeg.Length <= TamanhoMaximoEntrada || lado <= 512) return jpeg;
            if (qualidade > 75) qualidade -= 8;
            else lado = Math.Max(512, (int)(lado * 0.8));
        }
    }

    /// <summary>
    /// Resultado do Magnific regravado no mesmo formato da foto original (JPEG, PNG ou WebP), para que volte ao
    /// sistema com o mesmo nome de arquivo. Outros formatos viram JPEG.
    /// </summary>
    public static byte[]? PrepararResultado(byte[] dados, string contentTypeOriginal) =>
        Converter(dados, ladoMaximo: null, qualidade: 90, FormatoDe(contentTypeOriginal));

    /// <summary>Content-type gravado por <see cref="PrepararResultado"/>.</summary>
    public static string ContentTypeResultado(string contentTypeOriginal) => FormatoDe(contentTypeOriginal) switch
    {
        SKEncodedImageFormat.Png => "image/png",
        SKEncodedImageFormat.Webp => "image/webp",
        _ => "image/jpeg",
    };

    private static SKEncodedImageFormat FormatoDe(string contentType) => contentType switch
    {
        "image/png" => SKEncodedImageFormat.Png,
        "image/webp" => SKEncodedImageFormat.Webp,
        _ => SKEncodedImageFormat.Jpeg,
    };

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

    private static byte[]? Converter(byte[] dados, int? ladoMaximo, int qualidade, SKEncodedImageFormat formato = SKEncodedImageFormat.Jpeg)
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
        using var codificada = imagem.Encode(formato, qualidade);
        return codificada?.ToArray();
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
