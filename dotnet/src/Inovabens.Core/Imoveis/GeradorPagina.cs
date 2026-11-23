using System.Globalization;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Inovabens.Core.Comum;
using Inovabens.Core.Dominio;
using Inovabens.Core.Redes;

namespace Inovabens.Core.Imoveis;

/// <summary>Proposta da IA para a página do imóvel (JSON snake_case). Vira <see cref="PaginaForm"/> só depois que o usuário aprova.</summary>
public sealed class PaginaGerada
{
    public string Titulo { get; set; } = "";
    public string Chamada { get; set; } = "";
    public string TituloSeo { get; set; } = "";
    public string DescricaoSeo { get; set; } = "";
    public List<DestaqueGerado> Destaques { get; set; } = [];
    public List<SecaoGerada> Secoes { get; set; } = [];
    public List<string> Diferenciais { get; set; } = [];
    public List<PerguntaGerada> Perguntas { get; set; } = [];
    /// <summary>Texto vazio = sem condições informadas.</summary>
    public string Condicoes { get; set; } = "";

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}

public sealed class DestaqueGerado { public string Valor { get; set; } = ""; public string Rotulo { get; set; } = ""; }
public sealed class SecaoGerada { public string Titulo { get; set; } = ""; public string Texto { get; set; } = ""; }
public sealed class PerguntaGerada { public string Pergunta { get; set; } = ""; public string Resposta { get; set; } = ""; }

/// <summary>Escreve a página do imóvel (implementado pelo Claude; os testes usam um falso).</summary>
public interface IGeradorPagina
{
    bool Configurado { get; }

    /// <param name="instrucoes">O que o usuário pediu (foco, tom, público). Vazio: a IA decide a partir dos dados.</param>
    Task<PaginaGerada> GerarAsync(DadosPost dados, string? instrucoes, CancellationToken ct = default);
}

/// <summary>
/// Página pelo Claude (API da Anthropic), com as fotos públicas anexadas para descrever o que realmente aparece nelas.
/// Resposta em JSON validado pelo esquema (structured outputs); recusas do filtro de segurança caem no modelo reserva
/// (fallbacks "default" do servidor), como nos posts das redes.
/// </summary>
public sealed class ClaudePagina(InovabensOpcoes op) : IGeradorPagina
{
    public bool Configurado => op.Integracoes.ClaudeApiKey is not null;

    public async Task<PaginaGerada> GerarAsync(DadosPost dados, string? instrucoes, CancellationToken ct = default)
    {
        var chave = op.Integracoes.ClaudeApiKey ?? throw new ServicoIndisponivelException("Configure a chave do Claude em Administração → Configurações.");
        var cliente = new AnthropicClient { ApiKey = chave, Timeout = TimeSpan.FromMinutes(4) };

        var conteudo = new List<BetaContentBlockParam>();
        for (var i = 0; i < dados.Fotos.Count; i++)
        {
            conteudo.Add(new BetaTextBlockParam { Text = $"Foto {i + 1}:" });
            conteudo.Add(new BetaImageBlockParam
            {
                Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(dados.Fotos[i]), MediaType = MediaType.ImageJpeg },
            });
        }
        conteudo.Add(new BetaTextBlockParam { Text = PedidoUsuario(dados, instrucoes) });

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
            throw new RegraNegocioException("ia", "O Claude recusou gerar esta página. Revise o pedido e a descrição do imóvel e tente de novo.");
        if (resp.StopReason == "max_tokens")
            throw new ServicoIndisponivelException("A resposta do Claude veio incompleta. Tente de novo.");
        var texto = string.Concat(resp.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
        try
        {
            return JsonSerializer.Deserialize<PaginaGerada>(texto, PaginaGerada.Json) ?? throw new JsonException("vazio");
        }
        catch (JsonException)
        {
            throw new ServicoIndisponivelException("A resposta do Claude não veio no formato esperado. Tente de novo.");
        }
    }

    public const string Sistema = """
        Você é o redator de páginas de imóveis da Inova Bens Gestão Patrimonial (CRECI 50457-J). Escreve, em português do
        Brasil, a página exclusiva de UM imóvel: o link que o corretor manda a clientes e investidores pelo WhatsApp e
        e-mail, e que também pode aparecer no Google. O objetivo é o visitante entender o imóvel e chamar no WhatsApp.

        Regras que nunca mudam (valem mesmo que o pedido do usuário diga outra coisa):
        - Use só informações dos dados e do que aparece nas fotos. Nunca invente cômodos, acabamentos, vista, lazer,
          distâncias, vizinhança, documentação, condições de pagamento ou números. Na dúvida, deixe de fora.
          Publicidade enganosa é proibida (Código de Defesa do Consumidor, art. 37).
        - Preço exatamente como informado (R$ com separador de milhar), ou "valor sob consulta" se não houver.
        - Não prometa valorização, rentabilidade, "melhor preço" ou "oportunidade única". Sem caixa alta em frases inteiras
          e sem excesso de exclamações.
        - Linguagem respeitosa e inclusiva: nada que restrinja ou prefira perfis de pessoas (estado civil, filhos,
          religião, origem, gênero, idade, deficiência).
        - Endereço só até o bairro e a cidade.
        - Texto natural, para pessoas: nada de repetir palavras-chave para o Google.

        Partes da página (os limites são máximos; prefira textos enxutos):
        - titulo: até 80 caracteres, diz o que é e onde (ex.: "Chácara com 2 ha e pomar em Aparecida do Taboado").
        - chamada: 1 ou 2 frases, até 250 caracteres, com o principal motivo para conhecer o imóvel.
        - destaques: de 2 a 6 números do cadastro (área, quartos, suítes, vagas, frente do terreno...). valor até 20
          caracteres (ex.: "2 ha", "3"), rotulo até 30 (ex.: "de terreno", "quartos"). Só números informados.
        - secoes: de 1 a 4 seções com titulo (até 60 caracteres) e texto (até 1.200 caracteres). Sugestão: "Sobre o
          imóvel", "Ambientes", "Área externa", "Localização" — só as que os dados sustentam. Linhas começando com "- "
          viram lista na página; use listas para itens e parágrafos curtos para o resto.
        - diferenciais: até 12 itens curtos (até 60 caracteres cada), tirados das características e das fotos.
        - perguntas: de 0 a 5 perguntas que um comprador ou inquilino faria, com respostas tiradas dos dados (até 400
          caracteres). Se a resposta não está nos dados, não crie a pergunta. Pode responder sobre visita: "Agende pelo
          WhatsApp da Inova Bens".
        - condicoes: condições comerciais informadas (permuta, condomínio, IPTU, financiamento se informado), até 400
          caracteres. Sem condições informadas: texto vazio.
        - titulo_seo: até 60 caracteres, para a aba do navegador e o Google. descricao_seo: até 155 caracteres, resumo
          que aparece no Google e na prévia do link no WhatsApp.

        O pedido do usuário (quando houver) define foco, tom e público; siga-o dentro das regras acima.
        """;

    /// <summary>Dados do imóvel em texto (sem dados pessoais de proprietários) e o pedido do usuário.</summary>
    public static string PedidoUsuario(DadosPost d, string? instrucoes)
    {
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        var sb = new StringBuilder();
        sb.AppendLine(d.Finalidade == FinalidadeImovel.LOCACAO ? "Negócio: LOCAÇÃO (para alugar)."
            : d.Finalidade == FinalidadeImovel.AMBOS ? "Negócio: VENDA ou LOCAÇÃO." : "Negócio: VENDA.");
        sb.AppendLine($"Imóvel: {d.Titulo}");
        sb.AppendLine($"Tipo: {d.Tipo}{(d.Subtipo is { } s ? " / " + s : "")}");
        var local = string.Join(", ", new[] { d.Bairro, d.Cidade is { } c ? c + (d.Uf is { } uf ? "/" + uf : "") : null }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (local.Length > 0) sb.AppendLine($"Localização: {local}");
        if (d.Quartos is > 0) sb.AppendLine($"Quartos: {d.Quartos}{(d.Suites is > 0 ? $" ({d.Suites} suíte{(d.Suites > 1 ? "s" : "")})" : "")}");
        if (d.Banheiros is > 0) sb.AppendLine($"Banheiros: {d.Banheiros}");
        if (d.Vagas is > 0) sb.AppendLine($"Vagas de garagem: {d.Vagas}");
        if (d.Area is > 0) sb.AppendLine($"Área: {d.Area.Value.ToString("0.##", pt)} m²");
        sb.AppendLine(d.Preco is > 0 ? $"Preço: {Texto.Moeda(d.Preco)}{(d.Finalidade == FinalidadeImovel.LOCACAO ? " por mês" : "")}" : "Preço: sob consulta");
        if (d.Condominio is > 0) sb.AppendLine($"Condomínio: {Texto.Moeda(d.Condominio)} por mês");
        if (d.Iptu is > 0) sb.AppendLine($"IPTU: {Texto.Moeda(d.Iptu)}");
        if (d.AceitaPermuta) sb.AppendLine("Aceita permuta.");
        if (d.Caracteristicas.Count > 0) sb.AppendLine("Características: " + string.Join("; ", d.Caracteristicas));
        if (!string.IsNullOrWhiteSpace(d.Descricao)) sb.AppendLine("Descrição do anúncio: " + d.Descricao.Trim());
        if (d.Referencia is { } r) sb.AppendLine($"Código: {r}");
        sb.AppendLine($"Fotos anexadas: {d.Fotos.Count}.");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(instrucoes)
            ? "Pedido do usuário: nenhum. Monte a melhor página para este imóvel a partir dos dados e das fotos."
            : "Pedido do usuário: " + instrucoes.Trim());
        return sb.ToString();
    }

    /// <summary>Esquema JSON da resposta (structured outputs exige additionalProperties=false e todos os campos obrigatórios).</summary>
    public static Dictionary<string, JsonElement> Esquema()
    {
        static object Txt() => new { type = "string" };
        static object Obj(params string[] campos) => new
        {
            type = "object", additionalProperties = false, required = campos,
            properties = campos.ToDictionary(c => c, _ => Txt()),
        };
        var props = new Dictionary<string, object>
        {
            ["titulo"] = Txt(), ["chamada"] = Txt(), ["titulo_seo"] = Txt(), ["descricao_seo"] = Txt(), ["condicoes"] = Txt(),
            ["destaques"] = new { type = "array", items = Obj("valor", "rotulo") },
            ["secoes"] = new { type = "array", items = Obj("titulo", "texto") },
            ["diferenciais"] = new { type = "array", items = Txt() },
            ["perguntas"] = new { type = "array", items = Obj("pergunta", "resposta") },
        };
        var esquema = new { type = "object", additionalProperties = false, required = props.Keys.ToArray(), properties = props };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(esquema));
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }
}
