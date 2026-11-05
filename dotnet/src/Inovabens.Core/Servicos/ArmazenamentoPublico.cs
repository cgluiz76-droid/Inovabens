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
    private readonly InovabensOpcoes _op;
    private readonly ILogger<R2ArmazenamentoPublico> _log;
    private readonly Lock _trava = new();
    // cliente montado com as integrações em vigor; refeito quando a configuração é salva na tela
    private (Integracoes? Fonte, AmazonS3Client? S3) _atual;

    public R2ArmazenamentoPublico(InovabensOpcoes op, ILogger<R2ArmazenamentoPublico> log)
    {
        _op = op;
        _log = log;
        if (S3 is null) log.LogWarning("Cloudflare R2 não configurado (R2_*): fotos públicas não vão para o site.");
    }

    private AmazonS3Client? S3
    {
        get
        {
            var fonte = _op.Integracoes;
            if (ReferenceEquals(_atual.Fonte, fonte)) return _atual.S3;
            lock (_trava)
            {
                if (ReferenceEquals(_atual.Fonte, fonte)) return _atual.S3;
                var s3 = fonte.R2Configurado ? Criar(fonte.R2Endpoint!, fonte.R2AccessKeyId!, fonte.R2SecretAccessKey!) : null;
                // o cliente anterior não é descartado: pode haver envio em andamento com ele
                _atual = (fonte, s3);
                return s3;
            }
        }
    }

    private string Bucket => _op.Integracoes.R2Bucket;

    internal static AmazonS3Client Criar(string endpoint, string chave, string segredo) =>
        new(new BasicAWSCredentials(chave, segredo), new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "auto",
            // o R2 não aceita os checksums extras nem o envio em blocos (aws-chunked) do SDK
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    public bool Habilitado => S3 is not null;

    /// <summary>Confere o acesso ao bucket (lista no máximo um objeto).</summary>
    public async Task TestarAsync(CancellationToken ct = default)
    {
        var s3 = S3 ?? throw new InvalidOperationException("Cloudflare R2 não configurado.");
        await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket, MaxKeys = 1 }, ct);
    }

    public async Task EnviarAsync(string chave, byte[] dados, string contentType, CancellationToken ct = default)
    {
        var s3 = S3 ?? throw new InvalidOperationException("Armazenamento público desabilitado.");
        using var conteudo = new MemoryStream(dados);
        var req = new PutObjectRequest
        {
            BucketName = Bucket, Key = chave, InputStream = conteudo, ContentType = contentType,
            UseChunkEncoding = false, AutoCloseStream = false,
        };
        // a chave muda quando a foto muda: o navegador e a CDN podem guardar para sempre
        req.Headers.CacheControl = "public, max-age=31536000, immutable";
        await s3.PutObjectAsync(req, ct);
    }

    public async Task RemoverAsync(string chave)
    {
        if (S3 is not { } s3) return;
        try { await s3.DeleteObjectAsync(Bucket, chave); }
        catch (Exception e) { _log.LogWarning("Falha ao remover {Chave} do R2: {Erro}", chave, e.Message); }
    }

    public string? Url(string? chave) =>
        _op.Integracoes.R2UrlPublica is not { } url || string.IsNullOrEmpty(chave) ? null : $"{url}/{chave}";

    /// <summary>
    /// Texto do painel da Cloudflare ("Access Key ID", "Secret Access Key" e o endpoint): o valor vem na linha seguinte ao
    /// rótulo. O endpoint aparece como "S3 API endpoint" ou, nos tokens mais novos, como "Default" (endpoint sem jurisdição).
    /// </summary>
    internal static (string? Endpoint, string? Chave, string? Segredo) LerArquivoCloudflare(IReadOnlyList<string> linhas)
    {
        var l = linhas.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        string? Valor(string rotulo) => l.IndexOf(rotulo) is var i and >= 0 && i + 1 < l.Count ? l[i + 1] : null;
        var endpoint = Valor("S3 API endpoint") ?? Valor("Default")
            ?? l.FirstOrDefault(x => x.StartsWith("https://", StringComparison.Ordinal) && x.Contains(".r2.cloudflarestorage.com", StringComparison.Ordinal));
        return (endpoint, Valor("Access Key ID"), Valor("Secret Access Key"));
    }

    public void Dispose() => _atual.S3?.Dispose();
}
