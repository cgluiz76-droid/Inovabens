using Microsoft.Extensions.Configuration;

namespace Inovabens.Core;

/// <summary>
/// Configuração lida com os MESMOS nomes de variável de ambiente da versão Node (.env.example),
/// para que os dois sistemas possam rodar lado a lado apontando para o mesmo banco.
/// </summary>
public sealed class InovabensOpcoes
{
    public required string DatabaseUrl { get; init; }
    public string TokenEncKey { get; init; } = new('0', 64);
    public string? S3Endpoint { get; init; }
    public string S3Region { get; init; } = "auto";
    public string S3Bucket { get; init; } = "inovabens";
    public string? S3AccessKey { get; init; }
    public string? S3SecretKey { get; init; }
    public bool S3ForcePathStyle { get; init; } = true;
    public bool Producao { get; init; }
    /// <summary>DISABLE_JOBS=true desliga as rotinas agendadas (útil quando a versão Node ainda roda os jobs).</summary>
    public bool DesativarJobs { get; init; }

    public string? GoogleClientId { get; init; }
    public string? GoogleClientSecret { get; init; }
    /// <summary>Ex.: https://app.inovabens.com.br/api/v1/integracoes/google/callback (a versão .NET atende o mesmo caminho).</summary>
    public string? GoogleRedirectUri { get; init; }
    public string? GoogleWebhookUrl { get; init; }
    /// <summary>Endereço público da aplicação (volta do OAuth do Google).</summary>
    public string WebUrl { get; init; } = "http://localhost:5051";
    /// <summary>Chave do HMAC dos tokens de canal do Google (JWT_REFRESH_SECRET na versão Node, para os canais continuarem válidos).</summary>
    public string SegredoHmac { get; init; } = "";
    /// <summary>Administrador criado no banco novo (SEED_ADMIN_EMAIL / SEED_ADMIN_PASSWORD, como no seed do Node).</summary>
    public string SeedAdminEmail { get; init; } = "admin@inovabens.com.br";
    public string SeedAdminSenha { get; init; } = Dados.DadosIniciais.SenhaPadrao;

    public bool GoogleConfigurado => GoogleClientId is not null && GoogleClientSecret is not null && GoogleRedirectUri is not null;

    public static InovabensOpcoes De(IConfiguration c, bool producao)
    {
        var op = new InovabensOpcoes
        {
            DatabaseUrl = c["DATABASE_URL"] ?? c.GetConnectionString("Inovabens")
                ?? throw new InvalidOperationException("Configuração inválida: defina DATABASE_URL (ou ConnectionStrings:Inovabens)."),
            TokenEncKey = c["TOKEN_ENC_KEY"] ?? new string('0', 64),
            S3Endpoint = Vazio(c["S3_ENDPOINT"]),
            S3Region = c["S3_REGION"] ?? "auto",
            S3Bucket = c["S3_BUCKET"] ?? "inovabens",
            S3AccessKey = Vazio(c["S3_ACCESS_KEY"]),
            S3SecretKey = Vazio(c["S3_SECRET_KEY"]),
            S3ForcePathStyle = !string.Equals(c["S3_FORCE_PATH_STYLE"], "false", StringComparison.OrdinalIgnoreCase),
            Producao = producao,
            DesativarJobs = string.Equals(c["DISABLE_JOBS"], "true", StringComparison.OrdinalIgnoreCase),
            GoogleClientId = Vazio(c["GOOGLE_CLIENT_ID"]),
            GoogleClientSecret = Vazio(c["GOOGLE_CLIENT_SECRET"]),
            GoogleRedirectUri = Vazio(c["GOOGLE_REDIRECT_URI"]),
            GoogleWebhookUrl = Vazio(c["GOOGLE_WEBHOOK_URL"]),
            WebUrl = (Vazio(c["WEB_URL"]) ?? "http://localhost:5051").TrimEnd('/'),
            SegredoHmac = Vazio(c["JWT_REFRESH_SECRET"]) ?? c["TOKEN_ENC_KEY"] ?? new string('0', 64),
            SeedAdminEmail = Vazio(c["SEED_ADMIN_EMAIL"]) ?? "admin@inovabens.com.br",
            SeedAdminSenha = Vazio(c["SEED_ADMIN_PASSWORD"]) ?? Dados.DadosIniciais.SenhaPadrao,
        };
        if (op.TokenEncKey.Length != 64)
            throw new InvalidOperationException("Configuração inválida: TOKEN_ENC_KEY deve ter 64 caracteres hex.");
        if (producao && op.TokenEncKey.All(ch => ch == '0'))
            throw new InvalidOperationException("Configuração inválida: defina TOKEN_ENC_KEY aleatória em produção (openssl rand -hex 32).");
        // Em produção o segredo das assinaturas não pode cair na chave de cifragem (como exige a versão Node).
        if (producao && (Vazio(c["JWT_REFRESH_SECRET"]) is not { Length: >= 32 } segredo || segredo == op.TokenEncKey))
            throw new InvalidOperationException(
                "Configuração inválida: defina JWT_REFRESH_SECRET em produção (32+ caracteres aleatórios, diferente de TOKEN_ENC_KEY; use o mesmo valor da versão Node).");
        return op;
    }

    private static string? Vazio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
