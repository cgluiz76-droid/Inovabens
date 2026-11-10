using System.Globalization;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Inovabens.Core.Comum;
using Inovabens.Core.Dominio;

namespace Inovabens.Core.Redes;

/// <summary>Textos de cada rede e roteiro do vídeo curto. Gravado em JSON (snake_case) na coluna posts_redes.textos.</summary>
public sealed class TextosPost
{
    public string Instagram { get; set; } = "";
    public string Facebook { get; set; } = "";
    public string Tiktok { get; set; } = "";
    public string Linkedin { get; set; } = "";
    public string Whatsapp { get; set; } = "";
    public string YoutubeTitulo { get; set; } = "";
    public string YoutubeDescricao { get; set; } = "";
    /// <summary>Uma cena por clipe do vídeo, na ordem de exibição.</summary>
    public List<CenaVideo> Cenas { get; set; } = [];
    /// <summary>Chamada do cartão final do vídeo (ex.: "Agende sua visita").</summary>
    public string TextoFinal { get; set; } = "";
    /// <summary>Descrição da trilha, em inglês, para o gerador de música.</summary>
    public string Musica { get; set; } = "";

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = false };

    public string ParaJson() => JsonSerializer.Serialize(this, Json);

    public static TextosPost De(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<TextosPost>(json, Json) ?? new();
}

public sealed class CenaVideo
{
    /// <summary>Número da foto (1 = primeira foto escolhida).</summary>
    public int Foto { get; set; } = 1;
    /// <summary>Texto curto sobreposto ao clipe.</summary>
    public string TextoTela { get; set; } = "";
    /// <summary>Movimento de câmera em inglês para o Kling (ex.: "slow dolly in").</summary>
    public string Movimento { get; set; } = "";
}

/// <summary>Dados do imóvel enviados à IA. <see cref="Fotos"/>: JPEGs reduzidos, na ordem escolhida pelo usuário.</summary>
public sealed record DadosPost(
    FinalidadeImovel Finalidade, string Tipo, string? Subtipo, string Titulo, string? Descricao,
    string? Bairro, string? Cidade, string? Uf,
    int? Quartos, int? Suites, int? Banheiros, int? Vagas, decimal? Area,
    decimal? Preco, decimal? Condominio, decimal? Iptu, bool AceitaPermuta,
    IReadOnlyList<string> Caracteristicas, string? Referencia, string? LinkAnuncio, string? Whatsapp, string? Creci,
    IReadOnlyList<byte[]> Fotos);

/// <summary>Escreve os textos do post (implementado pelo Claude; os testes usam um falso).</summary>
public interface IGeradorTextosPost
{
    bool Configurado { get; }

    /// <param name="cenas">Quantidade de cenas do vídeo (0 = sem vídeo).</param>
    Task<TextosPost> GerarAsync(DadosPost dados, int cenas, CancellationToken ct = default);
}

/// <summary>
/// Textos pelo Claude (API da Anthropic), com as fotos anexadas para a IA descrever o que realmente aparece nelas.
/// A resposta vem em JSON validado pelo esquema (structured outputs). Recusas do filtro de segurança caem no modelo
/// reserva automaticamente (fallbacks "default" do servidor).
/// </summary>
public sealed class ClaudeTextosPost(InovabensOpcoes op) : IGeradorTextosPost
{
    public bool Configurado => op.Integracoes.ClaudeApiKey is not null;

    public async Task<TextosPost> GerarAsync(DadosPost dados, int cenas, CancellationToken ct = default)
    {
        var chave = op.Integracoes.ClaudeApiKey ?? throw new ServicoIndisponivelException("Configure a chave do Claude em Administração → Configurações.");
        var cliente = new AnthropicClient { ApiKey = chave, Timeout = TimeSpan.FromMinutes(3) };

        var conteudo = new List<BetaContentBlockParam>();
        for (var i = 0; i < dados.Fotos.Count; i++)
        {
            conteudo.Add(new BetaTextBlockParam { Text = $"Foto {i + 1}:" });
            conteudo.Add(new BetaImageBlockParam
            {
                Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(dados.Fotos[i]), MediaType = MediaType.ImageJpeg },
            });
        }
        conteudo.Add(new BetaTextBlockParam { Text = PedidoUsuario(dados, cenas) });

        BetaMessage resp;
        try
        {
            resp = await cliente.Beta.Messages.Create(new MessageCreateParams
            {
                Model = op.Integracoes.ClaudeModelo,
                MaxTokens = 16000,
                System = Sistema,
                Messages = [new BetaUserMessageParam { Content = conteudo }],
                OutputConfig = new BetaOutputConfig { Format = new BetaJsonOutputFormat { Schema = Esquema() } },
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new Default(),
            }, ct);
        }
        catch (AnthropicApiException e)
        {
            throw new ServicoIndisponivelException("O Claude não respondeu: " + (e.Message.Length > 300 ? e.Message[..300] : e.Message));
        }

        if (resp.StopReason == "refusal")
            throw new RegraNegocioException("textos", "O Claude recusou gerar os textos deste imóvel. Revise a descrição e tente de novo.");
        if (resp.StopReason == "max_tokens")
            throw new ServicoIndisponivelException("A resposta do Claude veio incompleta. Tente de novo.");
        var texto = string.Concat(resp.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
        return Interpretar(texto, cenas, dados.Fotos.Count);
    }

    /// <summary>Confere a chave e o modelo (consulta gratuita à lista de modelos).</summary>
    public async Task<string> TestarAsync(CancellationToken ct = default)
    {
        var chave = op.Integracoes.ClaudeApiKey ?? throw new ServicoIndisponivelException("Informe a chave da API.");
        var cliente = new AnthropicClient { ApiKey = chave };
        try
        {
            var m = await cliente.Models.Retrieve(op.Integracoes.ClaudeModelo, new Anthropic.Models.Models.ModelRetrieveParams(), ct);
            return $"Chave do Claude aceita; modelo {m.DisplayName} disponível.";
        }
        catch (AnthropicApiException e)
        {
            throw new ServicoIndisponivelException("O Claude recusou: " + (e.Message.Length > 300 ? e.Message[..300] : e.Message));
        }
    }

    /// <summary>Lê o JSON da resposta e corrige o que a tela não aceitaria (fotos fora da faixa, cenas a mais).</summary>
    public static TextosPost Interpretar(string json, int cenas, int fotos)
    {
        TextosPost t;
        try
        {
            t = JsonSerializer.Deserialize<TextosPost>(json, TextosPost.Json) ?? throw new JsonException("vazio");
        }
        catch (JsonException)
        {
            throw new ServicoIndisponivelException("A resposta do Claude não veio no formato esperado. Tente de novo.");
        }
        t.Cenas = [.. t.Cenas.Where(c => fotos > 0).Take(cenas).Select(c => { c.Foto = Math.Clamp(c.Foto, 1, Math.Max(fotos, 1)); return c; })];
        return t;
    }

    public const string Sistema = """
        Você é o redator de marketing imobiliário da Inovabens Gestão Patrimonial. Escreve posts que geram contatos
        (mensagens no WhatsApp e visitas) para imóveis à venda ou para alugar, em português do Brasil.

        Regras que valem para todos os textos:
        - Use só informações dos dados e do que aparece nas fotos. Nunca invente cômodos, acabamentos, vista, lazer,
          distâncias ou condições de pagamento. Na dúvida, deixe de fora.
        - Preço exatamente como informado (R$ com separador de milhar). Sem preço, use "valor sob consulta".
        - Venda: destaque patrimônio, espaço, localização, possibilidade de permuta e financiamento só se informados.
          Locação: destaque praticidade, custo mensal total (aluguel + condomínio + IPTU quando informados) e mudança rápida.
        - Linguagem respeitosa e inclusiva: nada que restrinja ou prefira perfis de pessoas (estado civil, filhos,
          religião, origem, gênero, idade, deficiência).
        - Não prometa valorização, rentabilidade ou "melhor preço". Não use caixa alta em frases inteiras.
        - Endereço só até o bairro e a cidade. Quando houver CRECI, inclua "CRECI <número>" no Facebook, Instagram e LinkedIn.
        - Sempre termine com uma chamada para ação clara (WhatsApp com o número informado e/ou link do anúncio).

        Formato de cada rede (o que mais funciona em cada uma para imóveis):
        - instagram: carrossel do feed. A primeira linha é um gancho de até 120 caracteres (é o que aparece antes do
          "mais"). Depois, de 3 a 6 linhas curtas com emojis moderados (📍 🛏️ 🚗 📐 ✨), o preço, a chamada
          ("Chame no WhatsApp" + número) e, por último, de 8 a 15 hashtags: locais (cidade e bairro sem espaços nem
          acentos), do tipo de imóvel e do negócio (#imovelavenda ou #aluguel).
        - facebook: texto mais completo, de 5 a 10 linhas, com os destaques em tópicos, preço, link do anúncio e
          WhatsApp. No máximo 3 hashtags.
        - tiktok: legenda curta (até 150 caracteres) com gancho e de 3 a 5 hashtags, incluindo a cidade.
        - linkedin: tom profissional, foco em patrimônio/investimento (venda) ou em praticidade para quem se muda a
          trabalho (locação); de 4 a 7 linhas, sem gírias, até 3 hashtags.
        - whatsapp: mensagem curta para Status e listas de transmissão: 3 a 5 linhas com emojis, preço e link do anúncio.
        - youtube_titulo: título do Shorts com até 70 caracteres; youtube_descricao: 2 a 4 linhas com link e WhatsApp.

        Roteiro do vídeo vertical (Reels, TikTok e Shorts), quando pedido:
        - cenas: exatamente a quantidade pedida, cada uma com uma foto diferente. Ordem: comece pela foto mais
          impactante (fachada ou ambiente principal), siga um percurso natural pelo imóvel e deixe área externa ou
          lazer para o fim. "foto" é o número da foto informada.
        - texto_tela: até 32 caracteres, informativo (ex.: "3 quartos, 1 suíte", "Cozinha planejada", o preço).
          A primeira cena tem o gancho principal.
        - movimento: em inglês, só o movimento de câmera suave e realista para animar a foto, sem pessoas, sem objetos
          novos e sem mudar a arquitetura. Ex.: "slow cinematic dolly in, steady camera, natural light".
        - texto_final: chamada do cartão final, até 40 caracteres (ex.: "Agende sua visita").
        - musica: em inglês, descrição de uma trilha instrumental de 30 segundos sem vozes, adequada ao imóvel
          (ex.: "warm modern acoustic pop, light percussion, uplifting, no vocals").
        Sem vídeo pedido: devolva cenas vazia e texto_final e musica como texto vazio.
        """;

    /// <summary>Dados do imóvel em texto para o pedido (sem dados pessoais de proprietários).</summary>
    public static string PedidoUsuario(DadosPost d, int cenas)
    {
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        var sb = new StringBuilder();
        sb.AppendLine(d.Finalidade == FinalidadeImovel.LOCACAO ? "Objetivo: post de LOCAÇÃO (para alugar)." : "Objetivo: post de VENDA.");
        sb.AppendLine($"Imóvel: {d.Titulo}");
        sb.AppendLine($"Tipo: {d.Tipo}{(d.Subtipo is { } s ? " / " + s : "")}");
        var local = string.Join(", ", new[] { d.Bairro, d.Cidade is { } c ? c + (d.Uf is { } uf ? "/" + uf : "") : null }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (local.Length > 0) sb.AppendLine($"Localização: {local}");
        if (d.Quartos is > 0) sb.AppendLine($"Quartos: {d.Quartos}{(d.Suites is > 0 ? $" ({d.Suites} suíte{(d.Suites > 1 ? "s" : "")})" : "")}");
        if (d.Banheiros is > 0) sb.AppendLine($"Banheiros: {d.Banheiros}");
        if (d.Vagas is > 0) sb.AppendLine($"Vagas de garagem: {d.Vagas}");
        if (d.Area is > 0) sb.AppendLine($"Área: {d.Area.Value.ToString("0.##", pt)} m²");
        sb.AppendLine(d.Preco is > 0
            ? $"{(d.Finalidade == FinalidadeImovel.LOCACAO ? "Aluguel" : "Preço")}: {Texto.Moeda(d.Preco)}{(d.Finalidade == FinalidadeImovel.LOCACAO ? " por mês" : "")}"
            : "Preço: sob consulta");
        if (d.Condominio is > 0) sb.AppendLine($"Condomínio: {Texto.Moeda(d.Condominio)} por mês");
        if (d.Iptu is > 0) sb.AppendLine($"IPTU: {Texto.Moeda(d.Iptu)}");
        if (d.AceitaPermuta && d.Finalidade != FinalidadeImovel.LOCACAO) sb.AppendLine("Aceita permuta.");
        if (d.Caracteristicas.Count > 0) sb.AppendLine("Características: " + string.Join("; ", d.Caracteristicas));
        if (!string.IsNullOrWhiteSpace(d.Descricao)) sb.AppendLine("Descrição do anúncio: " + d.Descricao.Trim());
        if (d.Referencia is { } r) sb.AppendLine($"Código do anúncio: {r}");
        if (d.LinkAnuncio is { } l) sb.AppendLine($"Link do anúncio: {l}");
        if (d.Whatsapp is { } w) sb.AppendLine($"WhatsApp da imobiliária: {w}");
        if (d.Creci is { } cr) sb.AppendLine($"CRECI: {cr}");
        sb.AppendLine($"Fotos anexadas: {d.Fotos.Count}.");
        sb.AppendLine(cenas > 0 ? $"Vídeo: roteiro com {cenas} cenas." : "Vídeo: não pedido.");
        return sb.ToString();
    }

    /// <summary>Esquema JSON da resposta (structured outputs exige additionalProperties=false e todos os campos obrigatórios).</summary>
    public static Dictionary<string, JsonElement> Esquema()
    {
        static object Txt() => new { type = "string" };
        var cena = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "foto", "texto_tela", "movimento" },
            properties = new Dictionary<string, object> { ["foto"] = new { type = "integer" }, ["texto_tela"] = Txt(), ["movimento"] = Txt() },
        };
        var campos = new[] { "instagram", "facebook", "tiktok", "linkedin", "whatsapp", "youtube_titulo", "youtube_descricao", "texto_final", "musica" };
        var props = campos.ToDictionary(c => c, _ => Txt());
        props["cenas"] = new { type = "array", items = cena };
        var esquema = new { type = "object", additionalProperties = false, required = campos.Append("cenas").ToArray(), properties = props };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(esquema));
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }
}
