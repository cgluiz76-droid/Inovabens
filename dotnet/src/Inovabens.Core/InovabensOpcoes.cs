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
    public bool Producao { get; init; }
    /// <summary>DISABLE_JOBS=true desliga as rotinas agendadas (use em todas as instâncias menos uma, ao escalar).</summary>
    public bool DesativarJobs { get; init; }

    /// <summary>Endereço público da aplicação (volta do OAuth do Google).</summary>
    public string WebUrl { get; init; } = "http://localhost:5051";
    /// <summary>Chave do HMAC dos tokens de canal do Google (JWT_REFRESH_SECRET na versão Node, para os canais continuarem válidos).</summary>
    public string SegredoHmac { get; init; } = "";
    /// <summary>Administrador criado no banco novo (SEED_ADMIN_EMAIL / SEED_ADMIN_PASSWORD, como no seed do Node).</summary>
    public string SeedAdminEmail { get; init; } = "admin@inovabens.com.br";
    public string SeedAdminSenha { get; init; } = Dados.DadosIniciais.SenhaPadrao;

    /// <summary>Integrações vindas das variáveis de ambiente (o que vale quando nada foi salvo pela tela).</summary>
    public Integracoes IntegracoesAmbiente { get; init; } = Integracoes.Vazias;

    private volatile Integracoes? _integracoes;

    /// <summary>Integrações em vigor: as do ambiente com as salvas em Administração → Configurações por cima.</summary>
    public Integracoes Integracoes
    {
        get => _integracoes ?? IntegracoesAmbiente;
        set => _integracoes = value;
    }

    public static InovabensOpcoes De(IConfiguration c, bool producao)
    {
        var op = new InovabensOpcoes
        {
            DatabaseUrl = c["DATABASE_URL"] ?? c.GetConnectionString("Inovabens")
                ?? throw new InvalidOperationException("Configuração inválida: defina DATABASE_URL (ou ConnectionStrings:Inovabens)."),
            TokenEncKey = c["TOKEN_ENC_KEY"] ?? new string('0', 64),
            IntegracoesAmbiente = CatalogoConfiguracoes.DoAmbiente(c),
            Producao = producao,
            DesativarJobs = string.Equals(c["DISABLE_JOBS"], "true", StringComparison.OrdinalIgnoreCase),
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
