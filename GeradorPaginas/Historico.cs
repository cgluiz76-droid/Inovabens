using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GeradorPaginas;

/// <summary>Uma geração salva, para a lista da tela.</summary>
public sealed record ItemHistorico(string Nome, string Resumo, DateTime Quando);

/// <summary>
/// Histórico local em ./historico/AAAA-MM-DD_HHMMSS/: pedido.txt, pagina.html, estilo.css e dados.json (o que a tela
/// precisa para reabrir: HTML e CSS separados, formato e prefixo).
/// </summary>
public static partial class Historico
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}_\d{6}(-\d{1,3})?$")]
    private static partial Regex NomeValido();

    private static string Raiz(string baseDir) => Path.Combine(baseDir, "historico");

    public static string Salvar(string baseDir, string pedido, string pagina, string css, object dados)
    {
        var nome = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var pasta = Path.Combine(Raiz(baseDir), nome);
        for (var n = 2; Directory.Exists(pasta); n++) pasta = Path.Combine(Raiz(baseDir), $"{nome}-{n}");
        Directory.CreateDirectory(pasta);
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(Path.Combine(pasta, "pedido.txt"), pedido, utf8);
        File.WriteAllText(Path.Combine(pasta, "pagina.html"), pagina, utf8);
        File.WriteAllText(Path.Combine(pasta, "estilo.css"), css, utf8);
        File.WriteAllText(Path.Combine(pasta, "dados.json"), JsonSerializer.Serialize(dados), utf8);
        return Path.GetFileName(pasta);
    }

    public static IReadOnlyList<ItemHistorico> Listar(string baseDir, int maximo = 100)
    {
        var raiz = Raiz(baseDir);
        if (!Directory.Exists(raiz)) return [];
        return [.. new DirectoryInfo(raiz).GetDirectories()
            .Where(d => NomeValido().IsMatch(d.Name) && File.Exists(Path.Combine(d.FullName, "dados.json")))
            .OrderByDescending(d => d.Name).Take(maximo)
            .Select(d =>
            {
                var pedido = File.Exists(Path.Combine(d.FullName, "pedido.txt")) ? File.ReadAllText(Path.Combine(d.FullName, "pedido.txt")) : "";
                var linha = pedido.Split('\n', 2)[0].Trim();
                return new ItemHistorico(d.Name, linha.Length > 90 ? linha[..90] + "…" : linha, d.CreationTime);
            })];
    }

    /// <summary>Conteúdo salvo de uma geração; null se o nome não é de uma pasta do histórico (protege contra "..\").</summary>
    public static object? Abrir(string baseDir, string nome)
    {
        if (!NomeValido().IsMatch(nome)) return null;
        var pasta = Path.Combine(Raiz(baseDir), nome);
        var arquivo = Path.Combine(pasta, "dados.json");
        if (!File.Exists(arquivo)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(arquivo));
        var pedido = File.Exists(Path.Combine(pasta, "pedido.txt")) ? File.ReadAllText(Path.Combine(pasta, "pedido.txt")) : "";
        return new { nome, pedido, dados = doc.RootElement.Clone() };
    }
}
