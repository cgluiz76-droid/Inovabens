using Microsoft.Extensions.Configuration;

namespace Inovabens.Core;

public enum TipoCampoConfig { Texto, Url, Email, Telefone, Multilinha, Booleano }

/// <summary>
/// Uma configuração editável em Administração → Configurações. <see cref="Variavel"/> é a variável de ambiente
/// equivalente: vale enquanto ninguém definir o valor pela tela (o valor salvo no banco tem prioridade).
/// </summary>
public sealed record ItemConfiguracao(
    string Chave, string Grupo, string Rotulo, string? Ajuda = null, bool Secreto = false,
    string? Variavel = null, TipoCampoConfig Tipo = TipoCampoConfig.Texto, string? Padrao = null);

public sealed record GrupoConfiguracao(string Codigo, string Titulo, string Descricao, bool Testavel = false);

/// <summary>Todas as integrações configuráveis pela tela, na ordem em que aparecem.</summary>
public static class CatalogoConfiguracoes
{
    public const string GoogleDrive = "google_drive", R2 = "r2", Magnific = "magnific", GoogleAgenda = "google_agenda",
        GoogleMaps = "google_maps", Site = "site", Imobibrasil = "imobibrasil";

    /// <summary>Portais que recebem os anúncios (código, nome). Cada um ganha o grupo "portal_{código}".</summary>
    public static readonly IReadOnlyList<(string Codigo, string Nome)> Portais =
    [
        ("zap", "ZAP Imóveis / Viva Real"),
        ("imovelweb", "ImovelWeb"),
        ("olx", "OLX"),
        ("creci", "Creci Brasil"),
        ("facebook", "Facebook Marketplace"),
    ];

    public static readonly IReadOnlyList<GrupoConfiguracao> Grupos =
    [
        new(GoogleDrive, "Google Drive (fotos e documentos)",
            "Drive compartilhado do Google Workspace onde ficam as pastas dos imóveis, acessado por uma conta de serviço " +
            "com papel \"Administrador de conteúdo\".", Testavel: true),
        new(R2, "Cloudflare R2 (fotos públicas do site)",
            "Cópia em WebP das fotos marcadas como públicas, servida ao site e aos portais.", Testavel: true),
        new(Magnific, "Magnific (melhoria de fotos)",
            "Com a chave, toda foto enviada é ampliada e melhorada automaticamente. A API cobra créditos à parte.", Testavel: true),
        new(GoogleAgenda, "Google Calendar (agenda)",
            "Credenciais OAuth do Google Cloud. Cada usuário conecta a própria agenda em Agenda → Conectar Google Calendar."),
        new(GoogleMaps, "Google Maps (localização pelo endereço)",
            "Sem a chave, a localização usa o OpenStreetMap (gratuito, menos preciso).", Testavel: true),
        new(Site, "Site da Inovabens", "Dados de contato exibidos no site e usados nos anúncios."),
        new(Imobibrasil, "Imobibrasil (site atual)",
            "Endereço do site atual, de onde a importação lê os anúncios publicados.", Testavel: true),
        .. Portais.Select(p => new GrupoConfiguracao("portal_" + p.Codigo, p.Nome,
            "Dados da conta da Inovabens no portal. O envio automático dos anúncios (feed XML) usará estes dados.")),
    ];

    public static readonly IReadOnlyList<ItemConfiguracao> Itens =
    [
        new("google_drive.id", GoogleDrive, "ID do drive compartilhado", "Trecho da URL do drive depois de /folders/.", Variavel: "GOOGLE_DRIVE_ID"),
        new("google_drive.credenciais_json", GoogleDrive, "Chave da conta de serviço (JSON)",
            "Conteúdo completo do arquivo .json baixado no Google Cloud (IAM → Contas de serviço → Chaves).",
            Secreto: true, Variavel: "GOOGLE_DRIVE_CREDENCIAIS_JSON", Tipo: TipoCampoConfig.Multilinha),

        new("r2.endpoint", R2, "Endpoint S3", "Ex.: https://<conta>.r2.cloudflarestorage.com", Variavel: "R2_ENDPOINT", Tipo: TipoCampoConfig.Url),
        new("r2.access_key_id", R2, "Access Key ID", Variavel: "R2_ACCESS_KEY_ID"),
        new("r2.secret_access_key", R2, "Secret Access Key", Secreto: true, Variavel: "R2_SECRET_ACCESS_KEY"),
        new("r2.bucket", R2, "Bucket", Variavel: "R2_BUCKET", Padrao: "inovabens-site"),
        new("r2.url_publica", R2, "Endereço público das fotos", "Domínio próprio (ex.: https://fotos.inovabens.com.br) ou o r2.dev do bucket.",
            Variavel: "R2_URL_PUBLICA", Tipo: TipoCampoConfig.Url),

        new("magnific.api_key", Magnific, "Chave da API", Secreto: true, Variavel: "MAGNIFIC_API_KEY"),

        new("google_agenda.client_id", GoogleAgenda, "Client ID", Variavel: "GOOGLE_CLIENT_ID"),
        new("google_agenda.client_secret", GoogleAgenda, "Client secret", Secreto: true, Variavel: "GOOGLE_CLIENT_SECRET"),
        new("google_agenda.redirect_uri", GoogleAgenda, "URI de redirecionamento",
            "Cadastre a mesma no Google Cloud: https://SEU-SITE/api/v1/integracoes/google/callback", Variavel: "GOOGLE_REDIRECT_URI", Tipo: TipoCampoConfig.Url),
        new("google_agenda.webhook_url", GoogleAgenda, "URL do webhook (opcional)",
            "Sincronização quase em tempo real: https://SEU-SITE/api/v1/webhooks/google/calendar. Sem ela, sincroniza a cada 10 minutos.",
            Variavel: "GOOGLE_WEBHOOK_URL", Tipo: TipoCampoConfig.Url),

        new("google_maps.api_key", GoogleMaps, "Chave da Geocoding API", Secreto: true, Variavel: "GOOGLE_MAPS_API_KEY"),

        new("site.url", Site, "Endereço do site", Variavel: "SITE_URL", Tipo: TipoCampoConfig.Url, Padrao: "https://www.inovabens.com.br"),
        new("site.whatsapp", Site, "WhatsApp", "Com DDD, só números (ex.: 17999990000).", Variavel: "SITE_WHATSAPP", Tipo: TipoCampoConfig.Telefone),
        new("site.email_contato", Site, "E-mail de contato", Variavel: "SITE_EMAIL_CONTATO", Tipo: TipoCampoConfig.Email),
        new("site.telefone", Site, "Telefone", Variavel: "SITE_TELEFONE", Tipo: TipoCampoConfig.Telefone),
        new("site.creci", Site, "CRECI da imobiliária", Variavel: "SITE_CRECI"),

        new("imobibrasil.site", Imobibrasil, "Endereço do site no Imobibrasil", Variavel: "IMOBIBRASIL_SITE",
            Tipo: TipoCampoConfig.Url, Padrao: "https://www.inovabens.com.br"),

        .. Portais.SelectMany(p => new ItemConfiguracao[]
        {
            new($"portal_{p.Codigo}.ativo", "portal_" + p.Codigo, "Publicar neste portal", Tipo: TipoCampoConfig.Booleano, Padrao: "false"),
            new($"portal_{p.Codigo}.codigo_anunciante", "portal_" + p.Codigo, "Código do anunciante / cliente"),
            new($"portal_{p.Codigo}.email", "portal_" + p.Codigo, "E-mail da conta no portal", Tipo: TipoCampoConfig.Email),
            new($"portal_{p.Codigo}.token", "portal_" + p.Codigo, "Token ou senha de integração", Secreto: true),
            new($"portal_{p.Codigo}.observacoes", "portal_" + p.Codigo, "Observações", Tipo: TipoCampoConfig.Multilinha),
        }),
    ];

    private static readonly Dictionary<string, ItemConfiguracao> PorChave = Itens.ToDictionary(i => i.Chave);

    public static ItemConfiguracao? Item(string chave) => PorChave.GetValueOrDefault(chave);

    /// <summary>
    /// Valores vindos das variáveis de ambiente. Os arquivos de credenciais (GOOGLE_DRIVE_CREDENCIAIS e R2_CREDENCIAIS,
    /// caminhos usados em desenvolvimento) são lidos aqui e viram os mesmos campos que a tela edita.
    /// </summary>
    public static Integracoes DoAmbiente(IConfiguration c)
    {
        var v = new Dictionary<string, string>();
        foreach (var i in Itens)
            if (i.Variavel is not null && Vazio(c[i.Variavel]) is { } valor) v[i.Chave] = valor;

        if (!v.ContainsKey("google_drive.credenciais_json") && Vazio(c["GOOGLE_DRIVE_CREDENCIAIS"]) is { } caminho && File.Exists(caminho))
            v["google_drive.credenciais_json"] = File.ReadAllText(caminho);
        if (Vazio(c["R2_CREDENCIAIS"]) is { } arquivo && File.Exists(arquivo))
        {
            var (endpoint, chave, segredo) = Servicos.R2ArmazenamentoPublico.LerArquivoCloudflare(File.ReadAllLines(arquivo));
            if (endpoint is not null) v.TryAdd("r2.endpoint", endpoint);
            if (chave is not null) v.TryAdd("r2.access_key_id", chave);
            if (segredo is not null) v.TryAdd("r2.secret_access_key", segredo);
        }
        return new Integracoes(v);
    }

    private static string? Vazio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// Valores em vigor das integrações (imutável: ao salvar na tela, um objeto novo substitui o anterior, e quem guarda
/// cliente pronto, como Drive e R2, percebe a troca pela referência).
/// </summary>
public sealed class Integracoes(IReadOnlyDictionary<string, string> valores)
{
    public static readonly Integracoes Vazias = new(new Dictionary<string, string>());

    public IReadOnlyDictionary<string, string> Valores => valores;

    /// <summary>Valor definido, ou o padrão do catálogo.</summary>
    public string? this[string chave] =>
        valores.TryGetValue(chave, out var v) && v.Length > 0 ? v : CatalogoConfiguracoes.Item(chave)?.Padrao;

    public string? GoogleDriveId => this["google_drive.id"];
    public string? GoogleDriveCredenciaisJson => this["google_drive.credenciais_json"];

    public string? R2Endpoint => this["r2.endpoint"];
    public string? R2AccessKeyId => this["r2.access_key_id"];
    public string? R2SecretAccessKey => this["r2.secret_access_key"];
    public string R2Bucket => this["r2.bucket"] ?? "inovabens-site";
    public string? R2UrlPublica => this["r2.url_publica"]?.TrimEnd('/');
    public bool R2Configurado => R2Endpoint is not null && R2AccessKeyId is not null && R2SecretAccessKey is not null;

    public string? MagnificApiKey => this["magnific.api_key"];
    public string? GoogleMapsApiKey => this["google_maps.api_key"];

    public string? GoogleClientId => this["google_agenda.client_id"];
    public string? GoogleClientSecret => this["google_agenda.client_secret"];
    public string? GoogleRedirectUri => this["google_agenda.redirect_uri"];
    public string? GoogleWebhookUrl => this["google_agenda.webhook_url"];
    public bool GoogleConfigurado => GoogleClientId is not null && GoogleClientSecret is not null && GoogleRedirectUri is not null;

    public string ImobibrasilSite => (this["imobibrasil.site"] ?? "https://www.inovabens.com.br").TrimEnd('/');

    /// <summary>Valores do ambiente com os salvos na tela por cima.</summary>
    public Integracoes Com(IReadOnlyDictionary<string, string> salvos)
    {
        var v = new Dictionary<string, string>(valores);
        foreach (var (k, valor) in salvos) v[k] = valor;
        return new Integracoes(v);
    }
}
