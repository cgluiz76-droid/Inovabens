using Microsoft.Extensions.Configuration;

namespace Inovabens.Core;

public enum TipoCampoConfig { Texto, Url, Email, Telefone, Multilinha, Booleano, Numero, Selecao }

/// <summary>Opção de um campo <see cref="TipoCampoConfig.Selecao"/> (valor gravado, texto mostrado).</summary>
public sealed record OpcaoConfig(string Valor, string Rotulo);

/// <summary>
/// Uma configuração editável em Administração → Configurações. <see cref="Variavel"/> é a variável de ambiente
/// equivalente: vale enquanto ninguém definir o valor pela tela (o valor salvo no banco tem prioridade).
/// </summary>
public sealed record ItemConfiguracao(
    string Chave, string Grupo, string Rotulo, string? Ajuda = null, bool Secreto = false,
    string? Variavel = null, TipoCampoConfig Tipo = TipoCampoConfig.Texto, string? Padrao = null,
    IReadOnlyList<OpcaoConfig>? Opcoes = null);

public sealed record GrupoConfiguracao(string Codigo, string Titulo, string Descricao, bool Testavel = false);

/// <summary>Todas as integrações configuráveis pela tela, na ordem em que aparecem.</summary>
public static class CatalogoConfiguracoes
{
    public const string GoogleDrive = "google_drive", R2 = "r2", Magnific = "magnific", GoogleAgenda = "google_agenda",
        GoogleMaps = "google_maps", Site = "site", Imobibrasil = "imobibrasil", Whatsapp = "whatsapp",
        Claude = "claude", Meta = "meta", Crm = "crm", GeradorPaginas = "gerador_paginas", Cora = "cora";

    /// <summary>Distribuição dos leads novos do WhatsApp: fila aberta (padrão) ou rodízio entre os corretores.</summary>
    public const string DistribuicaoFila = "fila", DistribuicaoRodizio = "rodizio";

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
        new(Crm, "CRM (distribuição de leads)",
            "Como os leads novos que chegam pelo WhatsApp ganham um responsável. Na fila aberta, o lead aparece no funil de " +
            "todos os corretores até um deles clicar em \"Pegar atendimento\". No rodízio, cada lead novo vai direto para o " +
            "próximo corretor da vez (quem recebeu lead há mais tempo)."),
        new(GoogleDrive, "Google Drive (fotos e documentos)",
            "Drive compartilhado do Google Workspace onde ficam as pastas dos imóveis, acessado por uma conta de serviço " +
            "com papel \"Administrador de conteúdo\".", Testavel: true),
        new(R2, "Cloudflare R2 (fotos públicas do site)",
            "Cópia em WebP das fotos marcadas como públicas, servida ao site e aos portais.", Testavel: true),
        new(Magnific, "Magnific (melhoria de fotos)",
            "Com a chave, toda foto enviada é ampliada e melhorada automaticamente. A API cobra créditos à parte.", Testavel: true),
        new(Claude, "Claude (textos dos posts para redes sociais)",
            "IA da Anthropic que escreve as legendas de cada rede social e o roteiro dos vídeos curtos, a partir dos dados e das " +
            "fotos do imóvel. Chave criada em console.anthropic.com → API Keys. A API cobra por uso (centavos por post).", Testavel: true),
        new(Meta, "Meta (publicar no Facebook e no Instagram)",
            "Publica os posts na página do Facebook e no perfil comercial do Instagram ligado a ela. No app da Meta (o mesmo do " +
            "WhatsApp), gere um token de acesso da página com as permissões pages_manage_posts, pages_read_engagement, " +
            "instagram_basic e instagram_content_publish. As imagens e vídeos vão pelo endereço público do R2.", Testavel: true),
        new(GoogleAgenda, "Google Calendar (agenda)",
            "Credenciais OAuth do Google Cloud. Cada usuário conecta a própria agenda em Agenda → Conectar Google Calendar."),
        new(GoogleMaps, "Google Maps (mapa e localização pelo endereço)",
            "O sistema usa só o Google Maps. No Google Cloud (APIs e serviços), ative a Geocoding API e a Maps JavaScript API e crie " +
            "duas chaves: a da Geocoding API (fica só no servidor; restrinja à Geocoding API) e a do mapa (vai para o navegador; " +
            "restrinja à Maps JavaScript API e aos sites https://SEU-SITE/*). Sem elas, o mapa e o botão Localizar não funcionam.", Testavel: true),
        new(Cora, "Banco Cora (boleto e Pix da locação)",
            "Emite boleto com QR Code Pix para as faturas de aluguel e dá baixa sozinho quando o inquilino paga. Exige a " +
            "Integração Direta do Cora (plano Cora Pro ou cobrança por boleto): no Cora Web, Conta → Integrações via APIs, gere o " +
            "certificado, a chave privada e o Client ID (o ambiente de testes tem credenciais próprias). Depois de salvar, use " +
            "\"Testar conexão\" e, na Locação, \"Registrar webhook\" para o Cora avisar os pagamentos.", Testavel: true),
        new(Site, "Site da Inovabens", "Dados de contato exibidos no site e usados nos anúncios."),
        new(Whatsapp, "WhatsApp Business (leads no CRM)",
            "Cada mensagem recebida no WhatsApp da empresa cria um lead na primeira coluna do funil (ou entra no histórico do lead " +
            "aberto do mesmo contato). No app da Meta (WhatsApp → Configuração → Webhooks), cadastre a URL " +
            "https://SEU-SITE/api/v1/webhooks/whatsapp com o mesmo token de verificação daqui e assine os campos " +
            "\"messages\" e \"smb_message_echoes\". Parceiro (BSP) que repassa as mensagens: use a URL com ?token=<token de verificação>."),
        new(GeradorPaginas, "GeradorPaginas (ferramenta local de páginas com IA)",
            "A ferramenta GeradorPaginas, que roda no computador do administrador, busca os dados públicos do imóvel pelo código " +
            "(endereço /api/v1/gerador/imovel/CÓDIGO). Crie aqui uma chave longa e aleatória e coloque a mesma no arquivo " +
            "appsettings.Local.json da ferramenta. Sem chave, o endereço fica desligado."),
        new(Imobibrasil, "Imobibrasil (site atual)",
            "Endereço do site atual, de onde a importação lê os anúncios publicados.", Testavel: true),
        .. Portais.Select(p => new GrupoConfiguracao("portal_" + p.Codigo, p.Nome,
            "Dados da conta da Inovabens no portal. O envio automático dos anúncios (feed XML) usará estes dados.")),
    ];

    public static readonly IReadOnlyList<ItemConfiguracao> Itens =
    [
        new("crm.distribuicao_leads", Crm, "Distribuição dos leads novos", Tipo: TipoCampoConfig.Selecao, Padrao: DistribuicaoFila,
            Opcoes: [new(DistribuicaoFila, "Fila aberta: aparece para todos os corretores até alguém pegar"),
                     new(DistribuicaoRodizio, "Rodízio: vai direto para o próximo corretor da vez")]),

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
        new("r2.alerta_gb", R2, "Avisar quando o bucket passar de (GB)",
            $"O R2 é grátis até {Servicos.R2ArmazenamentoPublico.GratuitoGb} GB guardados por mês. Uma vez por dia o sistema mede o bucket e avisa os administradores no sino.",
            Variavel: "R2_ALERTA_GB", Tipo: TipoCampoConfig.Numero, Padrao: "8"),

        new("magnific.api_key", Magnific, "Chave da API", Secreto: true, Variavel: "MAGNIFIC_API_KEY"),

        new("claude.api_key", Claude, "Chave da API", Secreto: true, Variavel: "ANTHROPIC_API_KEY"),
        new("claude.modelo", Claude, "Modelo", "Padrão: claude-opus-5-5.", Variavel: "CLAUDE_MODELO", Padrao: "claude-opus-5-5"),

        new("meta.page_id", Meta, "ID da página do Facebook", Variavel: "META_PAGE_ID"),
        new("meta.instagram_id", Meta, "ID da conta do Instagram (comercial)",
            "Graph API: GET /{id-da-página}?fields=instagram_business_account.", Variavel: "META_INSTAGRAM_ID"),
        new("meta.page_token", Meta, "Token de acesso da página", "Use um token de longa duração (não expira).",
            Secreto: true, Variavel: "META_PAGE_TOKEN"),
        new("meta.versao", Meta, "Versão da Graph API", Variavel: "META_GRAPH_VERSAO", Padrao: "v23.0"),

        new("google_agenda.client_id", GoogleAgenda, "Client ID", Variavel: "GOOGLE_CLIENT_ID"),
        new("google_agenda.client_secret", GoogleAgenda, "Client secret", Secreto: true, Variavel: "GOOGLE_CLIENT_SECRET"),
        new("google_agenda.redirect_uri", GoogleAgenda, "URI de redirecionamento",
            "Cadastre a mesma no Google Cloud: https://SEU-SITE/api/v1/integracoes/google/callback", Variavel: "GOOGLE_REDIRECT_URI", Tipo: TipoCampoConfig.Url),
        new("google_agenda.webhook_url", GoogleAgenda, "URL do webhook (opcional)",
            "Sincronização quase em tempo real: https://SEU-SITE/api/v1/webhooks/google/calendar. Sem ela, sincroniza a cada 10 minutos.",
            Variavel: "GOOGLE_WEBHOOK_URL", Tipo: TipoCampoConfig.Url),

        new("google_maps.api_key", GoogleMaps, "Chave da Geocoding API", Secreto: true, Variavel: "GOOGLE_MAPS_API_KEY"),
        new("google_maps.chave_mapa", GoogleMaps, "Chave do mapa (Maps JavaScript API)",
            "Aparece no navegador de quem abre o mapa: restrinja no Google Cloud aos endereços do sistema.", Variavel: "GOOGLE_MAPS_CHAVE_MAPA"),

        new("site.url", Site, "Endereço do site", Variavel: "SITE_URL", Tipo: TipoCampoConfig.Url, Padrao: "https://www.inovabens.com.br"),
        new("site.whatsapp", Site, "WhatsApp", "Com DDD, só números (ex.: 17999990000).", Variavel: "SITE_WHATSAPP", Tipo: TipoCampoConfig.Telefone),
        new("site.email_contato", Site, "E-mail de contato", Variavel: "SITE_EMAIL_CONTATO", Tipo: TipoCampoConfig.Email),
        new("site.telefone", Site, "Telefone", Variavel: "SITE_TELEFONE", Tipo: TipoCampoConfig.Telefone),
        new("site.creci", Site, "CRECI da imobiliária", Variavel: "SITE_CRECI"),

        new("whatsapp.verify_token", Whatsapp, "Token de verificação do webhook",
            "Texto longo e aleatório criado por você; o mesmo vai no campo \"Verificar token\" da Meta.",
            Secreto: true, Variavel: "WHATSAPP_VERIFY_TOKEN"),
        new("whatsapp.app_secret", Whatsapp, "Chave secreta do app (App Secret)",
            "Painel da Meta → Configurações do app → Básico. Confere a assinatura de cada aviso da Meta.",
            Secreto: true, Variavel: "WHATSAPP_APP_SECRET"),
        new("whatsapp.phone_number_id", Whatsapp, "ID do número de telefone (opcional)",
            "Com ele, só as mensagens desse número entram no CRM.", Variavel: "WHATSAPP_PHONE_NUMBER_ID"),

        new("gerador_paginas.chave", GeradorPaginas, "Chave de acesso da ferramenta",
            "Texto longo e aleatório (40 caracteres ou mais); o mesmo vai no appsettings.Local.json do GeradorPaginas.",
            Secreto: true, Variavel: "GERADOR_PAGINAS_CHAVE"),

        new("cora.ambiente", Cora, "Ambiente", Tipo: TipoCampoConfig.Selecao, Padrao: "stage",
            Opcoes: [new("stage", "Testes (stage): boletos sem valor"), new("producao", "Produção: boletos reais")]),
        new("cora.client_id", Cora, "Client ID", Variavel: "CORA_CLIENT_ID"),
        new("cora.certificado", Cora, "Certificado (arquivo .pem)", "Conteúdo completo, de -----BEGIN CERTIFICATE----- a -----END CERTIFICATE-----.",
            Variavel: "CORA_CERTIFICADO", Tipo: TipoCampoConfig.Multilinha),
        new("cora.chave_privada", Cora, "Chave privada (arquivo .key)", "Conteúdo completo, de -----BEGIN ... PRIVATE KEY----- até o fim.",
            Secreto: true, Variavel: "CORA_CHAVE_PRIVADA", Tipo: TipoCampoConfig.Multilinha),
        new("cora.webhook_token", Cora, "Token do webhook",
            "Texto longo e aleatório (40 caracteres ou mais) criado por você: vai no endereço que o Cora chama ao confirmar um pagamento.",
            Secreto: true, Variavel: "CORA_WEBHOOK_TOKEN"),
        new("cora.conta", Cora, "Conta no Financeiro", "Nome da conta bancária (Financeiro da Imobiliária) onde entram os pagamentos.",
            Padrao: "Cora"),

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
    /// <summary>Tamanho do bucket (GB) a partir do qual os administradores recebem alerta.</summary>
    public decimal R2AlertaGb =>
        decimal.TryParse(this["r2.alerta_gb"], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var gb) && gb > 0 ? gb : 8m;
    public bool R2Configurado => R2Endpoint is not null && R2AccessKeyId is not null && R2SecretAccessKey is not null;

    public string? MagnificApiKey => this["magnific.api_key"];
    public string? GoogleMapsApiKey => this["google_maps.api_key"];
    /// <summary>Chave da Maps JavaScript API (pública, restrita por site no Google Cloud) para desenhar o mapa.</summary>
    public string? GoogleMapsChaveMapa => this["google_maps.chave_mapa"];

    public string? ClaudeApiKey => this["claude.api_key"];
    public string ClaudeModelo => this["claude.modelo"] ?? "claude-opus-5-5";

    public string? MetaPageId => this["meta.page_id"];
    public string? MetaInstagramId => this["meta.instagram_id"];
    public string? MetaPageToken => this["meta.page_token"];
    public string MetaVersao => this["meta.versao"] ?? "v23.0";
    public bool MetaConfigurado => MetaPageToken is not null && (MetaPageId is not null || MetaInstagramId is not null);

    public bool CoraProducao => this["cora.ambiente"] == "producao";
    public string? CoraClientId => this["cora.client_id"];
    public string? CoraCertificado => this["cora.certificado"];
    public string? CoraChavePrivada => this["cora.chave_privada"];
    public string? CoraWebhookToken => this["cora.webhook_token"];
    public string CoraConta => this["cora.conta"] ?? "Cora";
    public bool CoraConfigurado => CoraClientId is not null && CoraCertificado is not null && CoraChavePrivada is not null;

    public string? GoogleClientId => this["google_agenda.client_id"];
    public string? GoogleClientSecret => this["google_agenda.client_secret"];
    public string? GoogleRedirectUri => this["google_agenda.redirect_uri"];
    public string? GoogleWebhookUrl => this["google_agenda.webhook_url"];
    public bool GoogleConfigurado => GoogleClientId is not null && GoogleClientSecret is not null && GoogleRedirectUri is not null;

    public string? WhatsappVerifyToken => this["whatsapp.verify_token"];
    /// <summary>Chave da ferramenta GeradorPaginas; curta demais conta como não configurada.</summary>
    public string? GeradorPaginasChave => this["gerador_paginas.chave"] is { Length: >= 24 } c ? c : null;
    public string? WhatsappAppSecret => this["whatsapp.app_secret"];
    public string? WhatsappPhoneNumberId => this["whatsapp.phone_number_id"];

    /// <summary>Leads novos do WhatsApp vão direto para o próximo corretor (senão, ficam na fila aberta).</summary>
    public bool CrmRodizio => this["crm.distribuicao_leads"] == CatalogoConfiguracoes.DistribuicaoRodizio;

    public string ImobibrasilSite => (this["imobibrasil.site"] ?? "https://www.inovabens.com.br").TrimEnd('/');

    /// <summary>Valores do ambiente com os salvos na tela por cima.</summary>
    public Integracoes Com(IReadOnlyDictionary<string, string> salvos)
    {
        var v = new Dictionary<string, string>(valores);
        foreach (var (k, valor) in salvos) v[k] = valor;
        return new Integracoes(v);
    }
}
