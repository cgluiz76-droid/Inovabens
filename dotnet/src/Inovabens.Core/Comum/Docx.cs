using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Inovabens.Core.Comum;

/// <summary>
/// Preenchimento de modelos Word (.docx) com marcadores <c>{{campo}}</c>. O Word costuma quebrar um marcador em vários
/// trechos de texto (por revisão ou formatação); aqui o parágrafo é lido inteiro, então o marcador funciona mesmo partido,
/// e o valor herda a formatação do trecho onde o marcador começa. Quebras de linha no valor viram quebras no Word.
/// Corpo, cabeçalhos e rodapés são preenchidos. Marcador desconhecido fica como está (aparece no documento, fácil de notar).
/// </summary>
public static partial class Docx
{
    public const string Tipo = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.]+)\s*\}\}")]
    private static partial Regex Marcador();

    private static bool Parte(string nome) =>
        nome == "word/document.xml" || (nome.StartsWith("word/header") || nome.StartsWith("word/footer")) && nome.EndsWith(".xml");

    /// <summary>Confere se é um .docx válido (zip com word/document.xml).</summary>
    public static bool Valido(byte[] arquivo)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(arquivo), ZipArchiveMode.Read);
            var doc = zip.GetEntry("word/document.xml");
            if (doc is null) return false;
            using var s = doc.Open();
            XDocument.Load(s);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException) { return false; }
    }

    /// <summary>Marcadores usados no modelo (em minúsculas, sem repetição, na ordem em que aparecem).</summary>
    public static IReadOnlyList<string> Marcadores(byte[] modelo)
    {
        var achados = new List<string>();
        using var zip = new ZipArchive(new MemoryStream(modelo), ZipArchiveMode.Read);
        foreach (var e in zip.Entries.Where(e => Parte(e.FullName)))
        {
            XDocument doc;
            using (var s = e.Open()) doc = XDocument.Load(s);
            foreach (var p in doc.Descendants(W + "p"))
                foreach (Match m in Marcador().Matches(string.Concat(TextosDo(p).Select(t => t.Value))))
                    achados.Add(m.Groups[1].Value.ToLowerInvariant());
        }
        return [.. achados.Distinct()];
    }

    /// <summary>Preenche os marcadores com os valores (chaves em minúsculas).</summary>
    public static byte[] Preencher(byte[] modelo, IReadOnlyDictionary<string, string> valores)
    {
        using var ms = new MemoryStream();
        ms.Write(modelo);
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
        {
            foreach (var e in zip.Entries.Where(e => Parte(e.FullName)).ToList())
            {
                var nome = e.FullName;
                XDocument doc;
                using (var s = e.Open()) doc = XDocument.Load(s, LoadOptions.PreserveWhitespace);
                if (!Substituir(doc, valores)) continue;
                e.Delete();
                using var saida = zip.CreateEntry(nome, CompressionLevel.Optimal).Open();
                doc.Save(saida, SaveOptions.DisableFormatting);
            }
        }
        return ms.ToArray();
    }

    /// <summary>Textos (w:t) do próprio parágrafo, sem os de parágrafos aninhados (caixas de texto).</summary>
    private static List<XElement> TextosDo(XElement p) =>
        [.. p.Descendants(W + "t").Where(t => t.Parent?.Name == W + "r" && t.Ancestors(W + "p").First() == p)];

    private static bool Substituir(XDocument doc, IReadOnlyDictionary<string, string> valores)
    {
        var mudou = false;
        foreach (var p in doc.Descendants(W + "p").ToList())
        {
            var textos = TextosDo(p);
            if (textos.Count == 0) continue;
            var inicio = new int[textos.Count];
            for (int i = 0, pos = 0; i < textos.Count; pos += textos[i].Value.Length, i++) inicio[i] = pos;
            var completo = string.Concat(textos.Select(t => t.Value));
            var achados = Marcador().Matches(completo);
            if (achados.Count == 0) continue;

            var tocados = new HashSet<XElement>();
            // de trás para a frente: as posições dos marcadores anteriores não mudam
            foreach (var m in achados.Reverse())
            {
                if (!valores.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out var valor)) continue;
                int No(int pos) => Array.FindLastIndex(inicio, x => x <= pos);
                var a = No(m.Index);
                var b = No(m.Index + m.Length - 1);
                var ta = textos[a];
                var offA = m.Index - inicio[a];
                var offB = m.Index + m.Length - inicio[b];
                if (a == b)
                    ta.Value = ta.Value[..offA] + valor + ta.Value[offB..];
                else
                {
                    ta.Value = ta.Value[..offA] + valor;
                    for (var i = a + 1; i < b; i++) textos[i].Value = "";
                    textos[b].Value = textos[b].Value[offB..];
                    tocados.Add(textos[b]);
                }
                tocados.Add(ta);
                mudou = true;
            }
            foreach (var t in tocados)
            {
                t.SetAttributeValue(XNamespace.Xml + "space", "preserve");
                if (t.Value.Contains('\n')) QuebrarLinhas(t);
            }
        }
        return mudou;
    }

    /// <summary>"a\nb" num w:t vira w:t(a) w:br w:t(b) dentro do mesmo trecho (mantém a formatação).</summary>
    private static void QuebrarLinhas(XElement t)
    {
        var linhas = t.Value.Replace("\r\n", "\n").Split('\n');
        var novos = new List<XElement>();
        for (var i = 0; i < linhas.Length; i++)
        {
            if (i > 0) novos.Add(new XElement(W + "br"));
            novos.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), linhas[i]));
        }
        t.ReplaceWith(novos);
    }
}
