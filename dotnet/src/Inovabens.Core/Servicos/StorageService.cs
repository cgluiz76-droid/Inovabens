using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

namespace Inovabens.Core.Servicos;

/// <summary>Armazenamento S3-compatível (AWS S3, Cloudflare R2, MinIO). Desabilitado se não houver credenciais.</summary>
public sealed class StorageService : IDisposable
{
    private readonly AmazonS3Client? _s3;
    private readonly string _bucket;
    private readonly bool _http;
    private readonly ILogger<StorageService> _log;

    public StorageService(InovabensOpcoes op, ILogger<StorageService> log)
    {
        _log = log;
        _bucket = op.S3Bucket;
        if (op.S3AccessKey is null || op.S3SecretKey is null)
        {
            log.LogWarning("S3 não configurado: upload de fotos/documentos desabilitado.");
            return;
        }
        var cfg = new AmazonS3Config
        {
            ForcePathStyle = op.S3ForcePathStyle,
            AuthenticationRegion = op.S3Region,
            // compatibilidade com S3-like (R2, GCS, MinIO): sem checksums extras que eles rejeitam
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (op.S3Endpoint is not null)
        {
            cfg.ServiceURL = op.S3Endpoint;
            _http = op.S3Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        }
        _s3 = new AmazonS3Client(new BasicAWSCredentials(op.S3AccessKey, op.S3SecretKey), cfg);
    }

    public bool Habilitado => _s3 is not null;

    /// <summary>Em desenvolvimento cria o bucket (MinIO local) se ainda não existir.</summary>
    public async Task GarantirBucketAsync()
    {
        if (_s3 is null) return;
        try
        {
            if (await Amazon.S3.Util.AmazonS3Util.DoesS3BucketExistV2Async(_s3, _bucket)) return;
            await _s3.PutBucketAsync(_bucket);
            _log.LogInformation("Bucket \"{Bucket}\" criado.", _bucket);
        }
        catch (Exception e)
        {
            _log.LogWarning("Não foi possível acessar/criar o bucket \"{Bucket}\": {Erro}", _bucket, e.Message);
        }
    }

    public async Task EnviarAsync(string chave, Stream conteudo, string contentType, CancellationToken ct = default)
    {
        if (_s3 is null) throw new InvalidOperationException("Armazenamento desabilitado.");
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = chave,
            InputStream = conteudo,
            ContentType = contentType,
            AutoCloseStream = false,
        }, ct);
    }

    public async Task RemoverAsync(string chave)
    {
        if (_s3 is null) return;
        try { await _s3.DeleteObjectAsync(_bucket, chave); }
        catch (Exception e) { _log.LogWarning("Falha ao remover {Chave}: {Erro}", chave, e.Message); }
    }

    /// <summary>Conteúdo do arquivo, ou null se o armazenamento estiver desligado ou a leitura falhar (uso em relatórios).</summary>
    public async Task<byte[]?> BaixarAsync(string? chave, CancellationToken ct = default)
    {
        if (_s3 is null || string.IsNullOrEmpty(chave)) return null;
        try
        {
            using var r = await _s3.GetObjectAsync(_bucket, chave, ct);
            using var ms = new MemoryStream();
            await r.ResponseStream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
        catch (Exception e)
        {
            _log.LogWarning("Falha ao baixar {Chave}: {Erro}", chave, e.Message);
            return null;
        }
    }

    /// <summary>URL temporária de leitura (1 h). Nunca expomos o bucket publicamente.</summary>
    public string? UrlAssinada(string? chave, int segundos = 3600)
    {
        if (_s3 is null || string.IsNullOrEmpty(chave)) return null;
        return _s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = chave,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddSeconds(segundos),
            Protocol = _http ? Protocol.HTTP : Protocol.HTTPS,
        });
    }

    public void Dispose() => _s3?.Dispose();
}
