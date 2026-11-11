using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Inovabens.Core.Notion;

/// <summary>Arquivo anexado no Notion (bloco de imagem/arquivo ou propriedade "Arquivos").</summary>
public sealed record ArquivoNotion(string Id, string Nome, string Url, bool HospedadoNoNotion);

/// <summary>Linha datada de uma tabela de histórico ("Data | Atualização") de uma página.</summary>
public sealed record LinhaHistorico(string Id, DateOnly Data, string Texto);

/// <summary>Conteúdo de uma página em texto simples, com o histórico datado e os arquivos separados.</summary>
public sealed class ConteudoPagina
{
    public StringBuilder Texto { get; } = new();
    public List<LinhaHistorico> Historico { get; } = [];
    public List<ArquivoNotion> Arquivos { get; } = [];
    /// <summary>Bases dentro da página (ex.: "Agenda mensal") com o número de linhas.</summary>
    public List<(string Titulo, int Linhas)> Bases { get; } = [];
    public string TextoFinal => Texto.ToString().Trim();
}

/// <summary>Leitura das propriedades e do conteúdo das páginas do Notion.</summary>
public static class NotionLeitor
{
    /// <summary>Id sem hífens (32 caracteres), como aparece nos links do Notion.</summary>
    public static string IdCurto(string? id) => (id ?? "").Replace("-", "");

    public static string Id(JsonElement pagina) => IdCurto(pagina.GetProperty("id").GetString());

    public static string? Link(JsonElement pagina) => pagina.TryGetProperty("url", out var u) ? u.GetString() : null;

    public static DateTime EditadoEm(JsonElement pagina) =>
        pagina.GetProperty("last_edited_time").GetDateTime().ToUniversalTime();

    public static DateTime CriadoEm(JsonElement pagina) =>
        pagina.GetProperty("created_time").GetDateTime().ToUniversalTime();

    /// <summary>Propriedade pelo nome, ignorando espaços nas pontas (o Notion tem "Cliente ", "Ação ", "Projeto ").</summary>
    public static JsonElement? Propriedade(JsonElement pagina, string nome)
    {
        foreach (var p in pagina.GetProperty("properties").EnumerateObject())
            if (string.Equals(p.Name.Trim(), nome.Trim(), StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    /// <summary>Texto da propriedade de título.</summary>
    public static string Titulo(JsonElement pagina)
    {
        foreach (var p in pagina.GetProperty("properties").EnumerateObject())
            if (p.Value.GetProperty("type").GetString() == "title") return TextoRico(p.Value.GetProperty("title")).Trim();
        return "";
    }

    /// <summary>Valor de uma propriedade como texto (texto, título, seleção, status, telefone, e-mail, link, número).</summary>
    public static string? Texto(JsonElement pagina, string nome)
    {
        if (Propriedade(pagina, nome) is not { } p) return null;
        var tipo = p.GetProperty("type").GetString()!;
        var v = p.GetProperty(tipo);
        var s = tipo switch
        {
            "title" or "rich_text" => TextoRico(v),
            "select" or "status" => v.ValueKind == JsonValueKind.Object ? v.GetProperty("name").GetString() : null,
            "phone_number" or "email" or "url" => v.ValueKind == JsonValueKind.String ? v.GetString() : null,
            "number" => v.ValueKind == JsonValueKind.Number ? v.GetDecimal().ToString(CultureInfo.GetCultureInfo("pt-BR")) : null,
            "checkbox" => v.GetBoolean() ? "Sim" : "Não",
            "multi_select" => string.Join(", ", v.EnumerateArray().Select(o => o.GetProperty("name").GetString())),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    public static List<string> Multipla(JsonElement pagina, string nome) =>
        Propriedade(pagina, nome) is { } p && p.GetProperty("type").GetString() == "multi_select"
            ? [.. p.GetProperty("multi_select").EnumerateArray().Select(o => o.GetProperty("name").GetString()!)]
            : [];

    public static bool Marcado(JsonElement pagina, string nome) =>
        Propriedade(pagina, nome) is { } p && p.GetProperty("type").GetString() == "checkbox" && p.GetProperty("checkbox").GetBoolean();

    /// <summary>Início e fim de uma data (fim null se for um dia só); hora só quando a data tem horário.</summary>
    public static (DateTime? Inicio, DateTime? Fim, bool ComHora) Data(JsonElement pagina, string nome)
    {
        if (Propriedade(pagina, nome) is not { } p || p.GetProperty("type").GetString() != "date") return (null, null, false);
        var d = p.GetProperty("date");
        if (d.ValueKind != JsonValueKind.Object) return (null, null, false);
        static DateTime? Ler(JsonElement e, string campo) =>
            e.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.String
                ? DateTime.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null;
        var inicio = d.GetProperty("start").GetString() ?? "";
        return (Ler(d, "start"), Ler(d, "end"), inicio.Length > 10);
    }

    /// <summary>Ids da relação; <c>Incompleta</c> quando o Notion cortou a lista (buscar com <see cref="NotionCliente.RelacaoAsync"/>).</summary>
    public static (List<string> Ids, bool Incompleta, string? PropriedadeId) Relacao(JsonElement pagina, string nome)
    {
        if (Propriedade(pagina, nome) is not { } p || p.GetProperty("type").GetString() != "relation") return ([], false, null);
        var ids = p.GetProperty("relation").EnumerateArray().Select(r => IdCurto(r.GetProperty("id").GetString())).ToList();
        var incompleta = p.TryGetProperty("has_more", out var m) && m.GetBoolean();
        return (ids, incompleta, p.GetProperty("id").GetString());
    }

    public static List<ArquivoNotion> Arquivos(JsonElement pagina, string nome)
    {
        if (Propriedade(pagina, nome) is not { } p || p.GetProperty("type").GetString() != "files") return [];
        var r = new List<ArquivoNotion>();
        var i = 0;
        foreach (var f in p.GetProperty("files").EnumerateArray())
        {
            var tipo = f.GetProperty("type").GetString()!;
            var url = f.GetProperty(tipo).GetProperty("url").GetString() ?? "";
            r.Add(new ArquivoNotion($"{Id(pagina)}:{nome.Trim()}:{i++}", f.GetProperty("name").GetString() ?? NomeDaUrl(url), url, tipo == "file"));
        }
        return r;
    }

    /// <summary>Texto puro de um rich_text; links aparecem entre parênteses quando o texto não é o próprio link.</summary>
    public static string TextoRico(JsonElement itens)
    {
        if (itens.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var t in itens.EnumerateArray())
        {
            var texto = t.TryGetProperty("plain_text", out var pt) ? pt.GetString() ?? "" : "";
            sb.Append(texto);
            if (t.TryGetProperty("href", out var h) && h.ValueKind == JsonValueKind.String && h.GetString() is { } href
                && !string.Equals(href.TrimEnd('/'), texto.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                && t.GetProperty("type").GetString() == "text")
                sb.Append(" (").Append(href).Append(')');
        }
        return sb.ToString();
    }

    public static string NomeDaUrl(string url)
    {
        try { return Uri.UnescapeDataString(Path.GetFileName(new Uri(url).AbsolutePath)); }
        catch (UriFormatException) { return "arquivo"; }
    }

    private static readonly string[] FormatosData = ["dd/MM/yyyy", "d/M/yyyy", "dd/MM/yy", "d/M/yy"];

    /// <summary>Data dd/MM/aaaa (aceita "." ou "-" no lugar da barra, como aparece no Notion: 25.02.2026).</summary>
    public static DateOnly? DataBr(string? s) =>
        DateOnly.TryParseExact((s ?? "").Trim().Replace('.', '/').Replace('-', '/'), FormatosData, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;

    /// <summary>Lê o conteúdo da página (blocos e sub-blocos) em texto simples.</summary>
    public static async Task<ConteudoPagina> ConteudoAsync(NotionCliente notion, string paginaId, CancellationToken ct = default)
    {
        var c = new ConteudoPagina();
        await BlocosAsync(notion, paginaId, c, 0, ct);
        return c;
    }

    private static async Task BlocosAsync(NotionCliente notion, string paiId, ConteudoPagina c, int nivel, CancellationToken ct)
    {
        var numero = 0;
        foreach (var b in await notion.FilhosAsync(paiId, ct))
        {
            var tipo = b.GetProperty("type").GetString()!;
            var id = IdCurto(b.GetProperty("id").GetString());
            var dados = b.TryGetProperty(tipo, out var d) ? d : default;
            var filhos = b.TryGetProperty("has_children", out var hc) && hc.GetBoolean();
            var recuo = new string(' ', nivel * 2);
            string Rico(string campo = "rich_text") => dados.ValueKind == JsonValueKind.Object && dados.TryGetProperty(campo, out var r) ? TextoRico(r) : "";
            numero = tipo == "numbered_list_item" ? numero + 1 : 0;

            switch (tipo)
            {
                case "paragraph": Linha(c, recuo + Rico()); break;
                case "heading_1": Linha(c, ""); Linha(c, recuo + "# " + Rico()); break;
                case "heading_2": Linha(c, ""); Linha(c, recuo + "## " + Rico()); break;
                case "heading_3": Linha(c, ""); Linha(c, recuo + "### " + Rico()); break;
                case "bulleted_list_item": Linha(c, recuo + "• " + Rico()); break;
                case "numbered_list_item": Linha(c, recuo + numero + ". " + Rico()); break;
                case "to_do": Linha(c, recuo + (dados.GetProperty("checked").GetBoolean() ? "[x] " : "[ ] ") + Rico()); break;
                case "toggle": Linha(c, recuo + "▸ " + Rico()); break;
                case "quote": Linha(c, recuo + "> " + Rico()); break;
                case "code": Linha(c, recuo + Rico()); break;
                case "equation": Linha(c, recuo + (dados.TryGetProperty("expression", out var ex) ? ex.GetString() : "")); break;
                case "divider": Linha(c, recuo + "———"); break;
                case "callout":
                    var icone = dados.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.Object && ic.TryGetProperty("emoji", out var em)
                        ? em.GetString() + " " : "";
                    Linha(c, ""); Linha(c, recuo + icone + Rico()); break;
                case "bookmark" or "embed" or "link_preview":
                    var link = dados.TryGetProperty("url", out var lu) ? lu.GetString() : null;
                    var leg = Rico("caption");
                    Linha(c, recuo + (leg.Length > 0 ? leg + ": " : "") + link); break;
                case "image" or "file" or "pdf" or "video" or "audio":
                    var t = dados.GetProperty("type").GetString()!;
                    var url = dados.TryGetProperty(t, out var fo) && fo.TryGetProperty("url", out var fu) ? fu.GetString() ?? "" : "";
                    var nome = dados.TryGetProperty("name", out var nm) && nm.GetString() is { Length: > 0 } n ? n : NomeDaUrl(url);
                    var legenda = Rico("caption");
                    c.Arquivos.Add(new ArquivoNotion(id, nome, url, t == "file"));
                    Linha(c, recuo + $"[{(tipo == "image" ? "Imagem" : "Arquivo")}: {nome}]" + (legenda.Length > 0 ? " " + legenda : "")
                             + (t == "external" ? " " + url : ""));
                    break;
                case "child_page":
                    Linha(c, ""); Linha(c, recuo + "## Subpágina: " + dados.GetProperty("title").GetString());
                    await BlocosAsync(notion, id, c, nivel, ct);
                    continue;
                case "child_database":
                    var titulo = dados.GetProperty("title").GetString() ?? "Base";
                    List<JsonElement> linhas;
                    try { linhas = await notion.LinhasAsync(id, ct); }
                    catch (NotionException) // vista vinculada de outra base ou base sem acesso pela integração
                    {
                        c.Bases.Add((titulo, -1));
                        Linha(c, recuo + $"[Base \"{titulo}\": sem acesso pela integração (consultar no Notion)]");
                        continue;
                    }
                    c.Bases.Add((titulo, linhas.Count));
                    Linha(c, recuo + $"[Base \"{titulo}\": {linhas.Count} itens]");
                    foreach (var l in linhas) Linha(c, recuo + "  - " + ResumoLinha(l));
                    continue;
                case "table":
                    await TabelaAsync(notion, id, dados, c, recuo, ct);
                    continue;
                case "column_list" or "column" or "synced_block" or "template":
                    break; // só os filhos interessam
                case "link_to_page": Linha(c, recuo + "[Link para outra página do Notion]"); break;
                case "breadcrumb" or "table_of_contents": break;
                default: Linha(c, recuo + $"[bloco \"{tipo}\" do Notion]"); break;
            }
            if (filhos) await BlocosAsync(notion, id, c, tipo is "column_list" or "column" or "synced_block" ? nivel : nivel + 1, ct);
        }
    }

    /// <summary>Uma linha de base filha: título e as demais propriedades preenchidas.</summary>
    private static string ResumoLinha(JsonElement linha)
    {
        var partes = new List<string> { Titulo(linha) };
        foreach (var p in linha.GetProperty("properties").EnumerateObject())
        {
            if (p.Value.GetProperty("type").GetString() == "title") continue;
            var (ini, fim, _) = Data(linha, p.Name);
            var v = ini is { } i ? i.ToString("dd/MM/yyyy") + (fim is { } f ? " a " + f.ToString("dd/MM/yyyy") : "") : Texto(linha, p.Name);
            if (v is not null) partes.Add($"{p.Name.Trim()}: {v}");
        }
        return string.Join(" · ", partes);
    }

    /// <summary>Tabela: se a 1ª coluna é data (histórico "Data | Atualização"), cada linha vira um item datado; senão vira texto.</summary>
    private static async Task TabelaAsync(NotionCliente notion, string id, JsonElement dados, ConteudoPagina c, string recuo, CancellationToken ct)
    {
        var linhas = (await notion.FilhosAsync(id, ct)).Where(l => l.GetProperty("type").GetString() == "table_row")
            .Select(l => (Id: IdCurto(l.GetProperty("id").GetString()),
                          Celulas: l.GetProperty("table_row").GetProperty("cells").EnumerateArray().Select(TextoRico).Select(s => s.Trim()).ToList()))
            .ToList();
        // cabeçalho marcado ou não: a 1ª linha sem data na 1ª coluna ("Data | Atualização") é o cabeçalho
        var temCabecalho = linhas.Count > 0 && ((dados.TryGetProperty("has_column_header", out var h) && h.GetBoolean())
                                                || (DataBr(linhas[0].Celulas.FirstOrDefault()) is null && linhas.Skip(1).Any(l => DataBr(l.Celulas.FirstOrDefault()) is not null)));
        var corpo = temCabecalho ? linhas.Skip(1).ToList() : linhas;
        if (corpo.Any(l => l.Celulas.Count >= 2 && DataBr(l.Celulas[0]) is not null))
        {
            var semData = new List<string>();
            foreach (var l in corpo)
            {
                if (l.Celulas.All(s => s.Length == 0)) continue;
                if (l.Celulas.Count >= 2 && DataBr(l.Celulas[0]) is { } data)
                    c.Historico.Add(new LinhaHistorico(l.Id, data, string.Join("\n", l.Celulas.Skip(1).Where(s => s.Length > 0))));
                else semData.Add(string.Join(" | ", l.Celulas)); // linha sem data reconhecível: fica no texto
            }
            if (semData.Count > 0)
            {
                Linha(c, "");
                Linha(c, recuo + "Histórico (linhas sem data reconhecida):");
                foreach (var s in semData) Linha(c, recuo + s);
            }
            return;
        }
        Linha(c, "");
        foreach (var l in linhas) Linha(c, recuo + string.Join(" | ", l.Celulas));
    }

    private static void Linha(ConteudoPagina c, string texto)
    {
        // no máximo uma linha em branco seguida
        if (texto.Trim().Length == 0 && (c.Texto.Length == 0 || c.Texto.ToString().EndsWith("\n\n", StringComparison.Ordinal))) return;
        c.Texto.Append(texto.TrimEnd()).Append('\n');
    }
}
