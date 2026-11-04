using Microsoft.Extensions.Configuration;

namespace Inovabens.Core;

/// <summary>
/// Configuração lida das variáveis de ambiente (os mesmos nomes da antiga versão Node, para que um ambiente
/// já configurado continue valendo: DATABASE_URL, TOKEN_ENC_KEY, GOOGLE_*...).
/// </summary>
public sealed class InovabensOpcoes
{
    public required string DatabaseUrl { get; init; }
    public string TokenEncKey { get; init; } = new('0', 64);
    /// <summary>ID do drive compartilhado onde ficam as pastas dos imóveis (trecho da URL depois de /folders/).</summary>
    public string? GoogleDriveId { get; init; }
    /// <summary>Caminho do arquivo JSON da chave da conta de serviço (membro "Administrador de conteúdo" do drive).</summary>
    public string? GoogleDriveCredenciais { get; init; }
    /// <summary>O mesmo JSON, direto na variável (útil em contêiner/nuvem). Tem prioridade sobre o caminho.</summary>
    public string? GoogleDriveCredenciaisJson { get; init; }

    /// <summary>Cloudflare R2: cópia pública (WebP) das fotos marcadas como públicas, para o site e os portais.</summary>
    public string? R2Endpoint { get; init; }
    public string? R2AccessKeyId { get; init; }
    public string? R2SecretAccessKey { get; init; }
    public string R2Bucket { get; init; } = "inovabens-site";
    /// <summary>Endereço público do bucket (domínio próprio, ex.: https://fotos.inovabens.com.br, ou o r2.dev).</summary>
    public string? R2UrlPublica { get; init; }
    /// <summary>Alternativa às três variáveis acima: texto copiado do painel da Cloudflare ao criar o token.</summary>
    public string? R2Credenciais { get; init; }
    public bool Producao { get; init; }
    /// <summary>DISABLE_JOBS=true desliga as rotinas agendadas (use em todas as instâncias menos uma, ao escalar).</summary>
    public bool DesativarJobs { get; init; }

    public string? GoogleClientId { get; init; }
    public string? GoogleClientSecret { get; init; }
    /// <summary>Ex.: https://app.inovabens.com.br/api/v1/integracoes/google/callback (a versão .NET atende o mesmo caminho).</summary>
    public string? GoogleRedirectUri { get; init; }
    public string? GoogleWebhookUrl { get; init; }
    /// <summary>Chave da API do Magnific: com ela, toda foto enviada é melhorada automaticamente (upscaler Precision V2).</summary>
    public string? MagnificApiKey { get; init; }
    /// <summary>Chave da Geocoding API do Google (localizar imóvel pelo endereço). Sem ela, usa o OpenStreetMap.</summary>
    public string? GoogleMapsApiKey { get; init; }
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
            GoogleDriveId = Vazio(c["GOOGLE_DRIVE_ID"]),
            GoogleDriveCredenciais = Vazio(c["GOOGLE_DRIVE_CREDENCIAIS"]),
            GoogleDriveCredenciaisJson = Vazio(c["GOOGLE_DRIVE_CREDENCIAIS_JSON"]),
            R2Endpoint = Vazio(c["R2_ENDPOINT"]),
            R2AccessKeyId = Vazio(c["R2_ACCESS_KEY_ID"]),
            R2SecretAccessKey = Vazio(c["R2_SECRET_ACCESS_KEY"]),
            R2Bucket = Vazio(c["R2_BUCKET"]) ?? "inovabens-site",
            R2UrlPublica = Vazio(c["R2_URL_PUBLICA"])?.TrimEnd('/'),
            R2Credenciais = Vazio(c["R2_CREDENCIAIS"]),
            Producao = producao,
            DesativarJobs = string.Equals(c["DISABLE_JOBS"], "true", StringComparison.OrdinalIgnoreCase),
            GoogleClientId = Vazio(c["GOOGLE_CLIENT_ID"]),
            GoogleClientSecret = Vazio(c["GOOGLE_CLIENT_SECRET"]),
            GoogleRedirectUri = Vazio(c["GOOGLE_REDIRECT_URI"]),
            GoogleWebhookUrl = Vazio(c["GOOGLE_WEBHOOK_URL"]),
            GoogleMapsApiKey = Vazio(c["GOOGLE_MAPS_API_KEY"]),
            MagnificApiKey = Vazio(c["MAGNIFIC_API_KEY"]),
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
                "Configuração inválida: defina JWT_REFRESH_SECRET em produção (32+ caracteres aleatórios, diferente de TOKEN_ENC_KEY; ao migrar da versão Node, use o mesmo valor dela).");
        return op;
    }

    private static string? Vazio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
