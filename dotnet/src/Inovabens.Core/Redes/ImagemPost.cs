using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using SkiaSharp;

namespace Inovabens.Core.Redes;

/// <summary>Dados exibidos na faixa da imagem de capa e no cartão final do vídeo.</summary>
public sealed record InfoCapa(string Etiqueta, string Preco, string Detalhes, string Local, string? Codigo, string? Whatsapp, string? Creci);

/// <summary>Bordas a completar pelo Magnific (Flux Pro Expand) e a foto reduzida que vai junto.</summary>
public sealed record PedidoExpansao(byte[] Jpeg, int Cima, int Baixo, int Esquerda, int Direita);

/// <summary>
/// Imagens dos posts com SkiaSharp: formatos de cada rede (feed 4:5 e story/vídeo 9:16), faixa com preço na capa,
/// textos sobrepostos aos clipes e o cartão final do vídeo, com as cores da marca.
/// </summary>
public static class ImagemPost
{
    public const int Largura = 1080;
    private static readonly SKColor Azul = SKColor.Parse("#0A2E96"), AzulEscuro = SKColor.Parse("#03075F"), Ouro = SKColor.Parse("#C8902F");

    /// <summary>Tamanho final: feed do Instagram/Facebook 1080x1350 (4:5); story, Reels e TikTok 1080x1920 (9:16).</summary>
    public static (int W, int H) Dimensoes(TipoMidiaPost tipo) => tipo == TipoMidiaPost.FEED ? (Largura, 1350) : (Largura, 1920);

    /// <summary>Pixels totais pedidos ao Expand (a saída é limitada a ~1,6 MP; abaixo disso só arredonda para múltiplos de 16).</summary>
    private const double AreaExpansao = 1_500_000;

    /// <summary>
    /// Pedido de expansão para a foto chegar à proporção <paramref name="w"/>:<paramref name="h"/> sem cortar o imóvel.
    /// Null quando a foto já é tão alta quanto o formato (basta cortar as laterais, sem custo).
    /// </summary>
    public static PedidoExpansao? PrepararExpansao(byte[] foto, int w, int h)
    {
        using var b = Decodificar(foto);
        if (b is null) return null;
        var alvo = w / (double)h;
        var proporcao = b.Width / (double)b.Height;
        if (proporcao <= alvo * 1.02) return null;

        // largura da tela final dentro do limite de área; a foto é reduzida para essa largura
        var largura = Math.Min(b.Width, (int)(Math.Sqrt(AreaExpansao * alvo) / 16) * 16);
        var altura = (int)Math.Round(largura / proporcao);
        var extra = (int)Math.Round(largura / alvo) - altura;
        var cima = Math.Min(2048, extra / 2);
        var baixo = Math.Min(2048, extra - extra / 2);
        using var reduzida = b.Resize(new SKImageInfo(largura, altura), new SKSamplingOptions(SKCubicResampler.Mitchell));
        return new PedidoExpansao(Jpeg(reduzida, 92), cima, baixo, 0, 0);
    }

    /// <summary>
    /// Foto no formato sem IA: corta as sobras quando a diferença de proporção é pequena; foto bem mais larga que o
    /// formato fica inteira sobre uma cópia desfocada dela mesma (como no site).
    /// </summary>
    public static SKBitmap Enquadrar(SKBitmap foto, int w, int h)
    {
        var r = new SKBitmap(w, h);
        using var c = new SKCanvas(r);
        using var img = SKImage.FromBitmap(foto);
        var alvo = w / (double)h;
        var proporcao = foto.Width / (double)foto.Height;
        var amostra = new SKSamplingOptions(SKCubicResampler.Mitchell);
        if (proporcao <= alvo * 1.3)
        {
            c.DrawImage(img, Cobrir(foto.Width, foto.Height, w, h), new SKRect(0, 0, w, h), amostra, null);
            return r;
        }
        using (var fundo = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(40, 40), ColorFilter = Escurecer(0.7f) })
            c.DrawImage(img, Cobrir(foto.Width, foto.Height, w, h), new SKRect(-60, -60, w + 60, h + 60), amostra, fundo);
        var altura = (float)(w / proporcao);
        var topo = (h - altura) / 2;
        c.DrawImage(img, new SKRect(0, topo, w, topo + altura), amostra);
        return r;
    }

    /// <summary>Imagem pronta para a rede: no tamanho exato, com a faixa da capa quando houver, em JPEG (o Instagram só aceita JPEG).</summary>
    public static byte[] Finalizar(byte[] imagem, int w, int h, InfoCapa? capa, bool jaExpandida)
    {
        using var b = Decodificar(imagem) ?? throw new ArgumentException("Imagem inválida.");
        using var enquadrada = jaExpandida ? Cortar(b, w, h) : Enquadrar(b, w, h);
        if (capa is not null)
        {
            using var c = new SKCanvas(enquadrada);
            DesenharFaixa(c, w, h, capa);
        }
        return Jpeg(enquadrada, 90);
    }

    /// <summary>Base 9:16 do clipe sem IA (foto enquadrada) em JPEG, para o Kling animar.</summary>
    public static byte[] BaseClipe(byte[] foto)
    {
        using var b = Decodificar(foto) ?? throw new ArgumentException("Imagem inválida.");
        using var r = Enquadrar(b, 720, 1280);
        return Jpeg(r, 92);
    }

    /// <summary>Resultado do Expand ajustado ao 9:16 exato (Kling recebe a proporção certa).</summary>
    public static byte[] AjustarClipe(byte[] expandida)
    {
        using var b = Decodificar(expandida) ?? throw new ArgumentException("Imagem inválida.");
        using var r = Cortar(b, 720, 1280);
        return Jpeg(r, 92);
    }

    /// <summary>Camada transparente (PNG) sobreposta a um clipe: marca no topo e o texto da cena no terço inferior.</summary>
    public static byte[] Sobreposicao(string texto, int w = Largura, int h = 1920)
    {
        using var b = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var c = new SKCanvas(b);
        c.Clear(SKColors.Transparent);
        DesenharMarca(c, 60, 150, 40, comFundo: true);
        if (!string.IsNullOrWhiteSpace(texto))
        {
            using var fonte = Fonte(60, negrito: true);
            var linhas = Quebrar(texto.Trim(), fonte, w - 200);
            var alturaLinha = 74f;
            var altura = linhas.Count * alturaLinha + 50;
            var topo = h * 0.66f;
            var largura = linhas.Max(l => fonte.MeasureText(l)) + 90;
            using var caixa = new SKPaint { Color = AzulEscuro.WithAlpha(215), IsAntialias = true };
            c.DrawRoundRect(new SKRoundRect(new SKRect((w - largura) / 2, topo, (w + largura) / 2, topo + altura), 28), caixa);
            using var barra = new SKPaint { Color = Ouro, IsAntialias = true };
            c.DrawRect(new SKRect((w - largura) / 2 + 28, topo + altura - 8, (w + largura) / 2 - 28, topo + altura - 2), barra);
            using var branco = new SKPaint { Color = SKColors.White, IsAntialias = true };
            for (var i = 0; i < linhas.Count; i++)
                c.DrawText(linhas[i], w / 2f, topo + 25 + (i + 1) * alturaLinha - 16, SKTextAlign.Center, fonte, branco);
        }
        return Png(b);
    }

    /// <summary>Cartão final do vídeo (PNG 9:16): marca, chamada, preço, WhatsApp, código e CRECI.</summary>
    public static byte[] CartaoFinal(InfoCapa info, string chamada, int w = Largura, int h = 1920)
    {
        using var b = new SKBitmap(w, h);
        using var c = new SKCanvas(b);
        using (var fundo = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h), [Azul, AzulEscuro], SKShaderTileMode.Clamp),
        })
            c.DrawRect(0, 0, w, h, fundo);
        using (var curva = new SKPaint { Color = Ouro, Style = SKPaintStyle.Stroke, StrokeWidth = 6, IsAntialias = true })
        using (var construtor = new SKPathBuilder())
        {
            construtor.MoveTo(0, h * 0.86f);
            construtor.CubicTo(w * 0.35f, h * 0.84f, w * 0.7f, h * 0.8f, w, h * 0.7f);
            using var caminho = construtor.Detach();
            c.DrawPath(caminho, curva);
        }

        DesenharMarca(c, w / 2f, h * 0.30f, 104, comFundo: false, centralizar: true);
        using var cinza = new SKPaint { Color = new SKColor(255, 255, 255, 190), IsAntialias = true };
        using var branco = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var ouro = new SKPaint { Color = Ouro, IsAntialias = true };
        using (var f = Fonte(30, negrito: true)) c.DrawText("GESTÃO PATRIMONIAL", w / 2f, h * 0.30f + 60, SKTextAlign.Center, f, cinza);

        var y = h * 0.47f;
        using (var f = Fonte(70, negrito: true))
            foreach (var linha in Quebrar(string.IsNullOrWhiteSpace(chamada) ? "Agende sua visita" : chamada.Trim(), f, w - 140))
            {
                c.DrawText(linha, w / 2f, y, SKTextAlign.Center, f, branco);
                y += 86;
            }
        y += 30;
        using (var f = Fonte(46, negrito: true)) c.DrawText($"{info.Etiqueta} · {info.Preco}", w / 2f, y, SKTextAlign.Center, f, ouro);
        y += 110;
        if (info.Whatsapp is { } zap)
            using (var f = Fonte(54, negrito: true)) { c.DrawText($"WhatsApp {zap}", w / 2f, y, SKTextAlign.Center, f, branco); y += 80; }
        using (var f = Fonte(34))
        {
            if (info.Codigo is { } cod) { c.DrawText($"Código {cod}", w / 2f, y, SKTextAlign.Center, f, cinza); y += 52; }
            if (info.Creci is { } creci) c.DrawText($"CRECI {creci}", w / 2f, y, SKTextAlign.Center, f, cinza);
        }
        return Png(b);
    }

    /* ------------------------------ auxiliares ------------------------------ */

    /// <summary>Faixa inferior da capa: etiqueta (venda/locação), preço, características e local; marca no topo.</summary>
    private static void DesenharFaixa(SKCanvas c, int w, int h, InfoCapa capa)
    {
        var inicio = h * (h > 1500 ? 0.55f : 0.5f);
        var fim = h > 1500 ? h * 0.82f : h; // story: deixa livre a área da interface do Instagram
        using (var grad = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, inicio), new SKPoint(0, fim),
                [SKColors.Transparent, AzulEscuro.WithAlpha(235)], SKShaderTileMode.Clamp),
        })
            c.DrawRect(0, inicio, w, h - inicio, grad);
        if (h > 1500)
            using (var resto = new SKPaint { Color = AzulEscuro.WithAlpha(235) }) c.DrawRect(0, fim, w, h - fim, resto);

        DesenharMarca(c, 56, h > 1500 ? 170 : 90, 40, comFundo: true);

        var x = 60f;
        var y = fim - 250;
        using (var f = Fonte(34, negrito: true))
        {
            var largura = f.MeasureText(capa.Etiqueta) + 44;
            using var pilula = new SKPaint { Color = Ouro, IsAntialias = true };
            c.DrawRoundRect(new SKRoundRect(new SKRect(x, y - 44, x + largura, y + 12), 28), pilula);
            using var branco = new SKPaint { Color = SKColors.White, IsAntialias = true };
            c.DrawText(capa.Etiqueta, x + 22, y - 4, SKTextAlign.Left, f, branco);
        }
        using var txt = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var suave = new SKPaint { Color = new SKColor(255, 255, 255, 215), IsAntialias = true };
        using (var f = Fonte(78, negrito: true)) c.DrawText(capa.Preco, x, y + 96, SKTextAlign.Left, f, txt);
        using (var f = Fonte(36, negrito: true)) c.DrawText(Encurtar(capa.Detalhes, f, w - 120), x, y + 156, SKTextAlign.Left, f, txt);
        using (var f = Fonte(32)) c.DrawText(Encurtar(capa.Local, f, w - 120), x, y + 206, SKTextAlign.Left, f, suave);
    }

    /// <summary>Nome da marca "INOVA" (branco) + "BENS" (dourado).</summary>
    private static void DesenharMarca(SKCanvas c, float x, float y, float tamanho, bool comFundo, bool centralizar = false)
    {
        using var f = Fonte(tamanho, negrito: true);
        var a = f.MeasureText("INOVA");
        var total = a + f.MeasureText("BENS");
        var inicio = centralizar ? x - total / 2 : x;
        if (comFundo)
        {
            using var fundo = new SKPaint { Color = AzulEscuro.WithAlpha(170), IsAntialias = true };
            c.DrawRoundRect(new SKRoundRect(new SKRect(inicio - 22, y - tamanho - 6, inicio + total + 22, y + tamanho * 0.35f), 18), fundo);
        }
        using var branco = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var ouro = new SKPaint { Color = Ouro, IsAntialias = true };
        c.DrawText("INOVA", inicio, y, SKTextAlign.Left, f, branco);
        c.DrawText("BENS", inicio + a, y, SKTextAlign.Left, f, ouro);
    }

    private static readonly string[] Familias = ["Montserrat", "Segoe UI", "DejaVu Sans", "Liberation Sans", "Arial"];

    private static SKFont Fonte(float tamanho, bool negrito = false)
    {
        var estilo = negrito ? SKFontStyle.Bold : SKFontStyle.Normal;
        var face = Familias.Select(f => SKTypeface.FromFamilyName(f, estilo))
            .FirstOrDefault(t => t is not null && Familias.Contains(t.FamilyName)) ?? SKTypeface.FromFamilyName(null, estilo);
        return new SKFont(face, tamanho) { Edging = SKFontEdging.Antialias };
    }

    /// <summary>Quebra o texto em linhas que cabem na largura.</summary>
    internal static List<string> Quebrar(string texto, SKFont fonte, float largura)
    {
        var linhas = new List<string>();
        var atual = "";
        foreach (var palavra in texto.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var tentativa = atual.Length == 0 ? palavra : atual + " " + palavra;
            if (atual.Length > 0 && fonte.MeasureText(tentativa) > largura)
            {
                linhas.Add(atual);
                atual = palavra;
            }
            else atual = tentativa;
        }
        if (atual.Length > 0) linhas.Add(atual);
        return linhas.Count == 0 ? [""] : linhas;
    }

    private static string Encurtar(string texto, SKFont fonte, float largura)
    {
        if (fonte.MeasureText(texto) <= largura) return texto;
        while (texto.Length > 1 && fonte.MeasureText(texto + "…") > largura) texto = texto[..^1];
        return texto.TrimEnd() + "…";
    }

    /// <summary>Recorte central da imagem com a proporção do destino.</summary>
    private static SKRect Cobrir(int sw, int sh, int w, int h)
    {
        var escala = Math.Max(w / (double)sw, h / (double)sh);
        float cw = (float)(w / escala), ch = (float)(h / escala);
        return new SKRect((sw - cw) / 2, (sh - ch) / 2, (sw + cw) / 2, (sh + ch) / 2);
    }

    private static SKBitmap Cortar(SKBitmap b, int w, int h)
    {
        var r = new SKBitmap(w, h);
        using var c = new SKCanvas(r);
        using var img = SKImage.FromBitmap(b);
        c.DrawImage(img, Cobrir(b.Width, b.Height, w, h), new SKRect(0, 0, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell), null);
        return r;
    }

    private static SKColorFilter Escurecer(float f) => SKColorFilter.CreateColorMatrix(
    [
        f, 0, 0, 0, 0,
        0, f, 0, 0, 0,
        0, 0, f, 0, 0,
        0, 0, 0, 1, 0,
    ]);

    /// <summary>Foto reduzida (lado maior até <paramref name="lado"/> px) em JPEG, na orientação certa. Null se não decodificar.</summary>
    public static byte[]? Reduzir(byte[] foto, int lado)
    {
        using var b = Decodificar(foto);
        if (b is null) return null;
        var escala = Math.Min(1d, lado / (double)Math.Max(b.Width, b.Height));
        if (escala >= 1) return Jpeg(b, 85);
        using var r = b.Resize(new SKImageInfo((int)Math.Round(b.Width * escala), (int)Math.Round(b.Height * escala)), new SKSamplingOptions(SKCubicResampler.Mitchell));
        return Jpeg(r, 85);
    }

    /// <summary>Decodifica já na orientação certa (EXIF), reduzida a no máximo 2048 px.</summary>
    internal static SKBitmap? Decodificar(byte[] dados)
    {
        var orientada = ImagemFoto.PrepararEntrada(dados);
        return orientada is null ? null : SKBitmap.Decode(orientada);
    }

    internal static byte[] Jpeg(SKBitmap b, int qualidade)
    {
        using var img = SKImage.FromBitmap(b);
        using var d = img.Encode(SKEncodedImageFormat.Jpeg, qualidade);
        return d.ToArray();
    }

    private static byte[] Png(SKBitmap b)
    {
        using var img = SKImage.FromBitmap(b);
        using var d = img.Encode(SKEncodedImageFormat.Png, 100);
        return d.ToArray();
    }
}
