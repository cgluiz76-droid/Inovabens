using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

namespace Inovabens.Core.Servicos;

/// <summary>
/// Armazenamento público das fotos do site e dos portais (Cloudflare R2). Só recebe cópias em WebP das fotos marcadas
/// como públicas; os originais continuam no Google Drive privado.
/// </summary>
public interface IArmazenamentoPublico
{
    bool Habilitado { get; }

    Task EnviarAsync(string chave, byte[] dados, string contentType, CancellationToken ct = default);

    /// <summary>Falhas só são registradas no log.</summary>
    Task RemoverAsync(string chave);

    /// <summary>URL pública do arquivo, ou null enquanto o endereço público (R2_URL_PUBLICA) não estiver configurado.</summary>
    string? Url(string? chave);
}

/// <summary>Cloudflare R2 pela API compatível com S3. Desabilitado sem endpoint e chaves.</summary>
public sealed class R2ArmazenamentoPublico : IArmazenamentoPublico, IDisposable
{
    private readonly AmazonS3Client? _s3;
    private readonly string _bucket;
    private readonly string? _urlPublica;
    private readonly ILogger<R2ArmazenamentoPublico> _log;

    public R2ArmazenamentoPublico(InovabensOpcoes op, ILogger<R2ArmazenamentoPublico> log)
    {
        _log = log;
        _bucket = op.R2Bucket;
        _urlPublica = op.R2UrlPublica;
        var (endpoint, chave, segredo) = (op.R2Endpoint, op.R2AccessKeyId, op.R2SecretAccessKey);
        if (op.R2Credenciais is { } arquivo && File.Exists(arquivo))
        {
            var lido = LerArquivoCloudflare(File.ReadAllLines(arquivo));
            endpoint ??= lido.Endpoint; chave ??= lido.Chave; segredo ??= lido.Segredo;
        }
        if (endpoint is null || chave is null || segredo is null)
        {
            log.LogWarning("Cloudflare R2 não configurado (R2_*): fotos públicas não vão para o site.");
            return;
        }
        _s3 = new AmazonS3Client(new BasicAWSCredentials(chave, segredo), new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "auto",
            // o R2 não aceita os checksums extras nem o envio em blocos (aws-chunked) do SDK
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });
    }

    public bool Habilitado => _s3 is not null;

    public async Task EnviarAsync(string chave, byte[] dados, string contentType, CancellationToken ct = default)
    {
        if (_s3 is null) throw new InvalidOperationException("Armazenamento público desabilitado.");
        using var conteudo = new MemoryStream(dados);
        var req = new PutObjectRequest
        {
            BucketName = _bucket, Key = chave, InputStream = conteudo, ContentType = contentType,
            UseChunkEncoding = false, AutoCloseStream = false,
        };
        // a chave muda quando a foto muda: o navegador e a CDN podem guardar para sempre
        req.Headers.CacheControl = "public, max-age=31536000, immutable";
        await _s3.PutObjectAsync(req, ct);
    }

    public async Task RemoverAsync(string chave)
    {
        if (_s3 is null) return;
        try { await _s3.DeleteObjectAsync(_bucket, chave); }
        catch (Exception e) { _log.LogWarning("Falha ao remover {Chave} do R2: {Erro}", chave, e.Message); }
    }

    public string? Url(string? chave) =>
        _urlPublica is null || string.IsNullOrEmpty(chave) ? null : $"{_urlPublica}/{chave}";

    /// <summary>Texto do painel da Cloudflare ("Access Key ID", "Secret Access Key", "S3 API endpoint"): o valor vem na linha seguinte.</summary>
    internal static (string? Endpoint, string? Chave, string? Segredo) LerArquivoCloudflare(IReadOnlyList<string> linhas)
    {
        var l = linhas.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        string? Valor(string rotulo) => l.IndexOf(rotulo) is var i and >= 0 && i + 1 < l.Count ? l[i + 1] : null;
        return (Valor("S3 API endpoint"), Valor("Access Key ID"), Valor("Secret Access Key"));
    }

    public void Dispose() => _s3?.Dispose();
}
