using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Inovabens.Core.Financeiro;

public sealed record OfxTransacao(string FitId, DateOnly Data, decimal Valor, string Descricao, string? Documento);

/// <param name="Saldo">Saldo do banco no arquivo (LEDGERBAL), na data <paramref name="DataSaldo"/>.</param>
public sealed record OfxExtrato(
    string? Banco, string? Conta, bool Cartao, DateOnly? De, DateOnly? Ate, decimal? Saldo, DateOnly? DataSaldo,
    IReadOnlyList<OfxTransacao> Transacoes);

/// <summary>
/// Lê o extrato OFX dos bancos (Itaú, Sicoob, Sicredi e outros): versão 1 (SGML, tags sem fechamento) e 2 (XML),
/// conta corrente ou cartão de crédito. Tolerante ao que os bancos brasileiros variam: acentos em Windows-1252 ou
/// UTF-8, valor com vírgula ou ponto e FITID repetido ou ausente.
/// </summary>
public static partial class OfxLeitor
{
    static OfxLeitor() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static OfxExtrato Ler(byte[] conteudo)
    {
        var texto = Decodificar(conteudo);
        if (texto.IndexOf("<OFX>", StringComparison.OrdinalIgnoreCase) < 0)
            throw new Comum.RegraNegocioException("arquivo", "O arquivo não parece um extrato OFX. No banco, exporte o extrato no formato OFX (Money).");

        var transacoes = new List<OfxTransacao>();
        var vistos = new Dictionary<string, int>();
        foreach (var bloco in Blocos(texto))
        {
            var data = Data(Tag(bloco, "DTPOSTED"));
            var valor = Valor(Tag(bloco, "TRNAMT"));
            if (data is null || valor is null || valor == 0) continue;
            var memo = Limpo(Tag(bloco, "MEMO"));
            var nome = Limpo(Tag(bloco, "NAME"));
            var descricao = memo is null ? nome
                : nome is null || memo.Contains(nome, StringComparison.OrdinalIgnoreCase) ? memo
                : $"{memo} {nome}";
            descricao = Cortar(descricao ?? Tag(bloco, "TRNTYPE") ?? "Sem descrição", 300);
            var documento = Limpo(Tag(bloco, "CHECKNUM")) ?? Limpo(Tag(bloco, "REFNUM"));
            // sem FITID: um identificador estável a partir do conteúdo; repetido no mesmo arquivo: numera
            var fit = Limpo(Tag(bloco, "FITID")) ?? Hash($"{data:yyyyMMdd}|{valor}|{descricao}|{documento}");
            vistos[fit] = vistos.GetValueOrDefault(fit) + 1;
            if (vistos[fit] > 1) fit = $"{fit}#{vistos[fit]}";
            transacoes.Add(new OfxTransacao(Cortar(fit, 120), data.Value, valor.Value, descricao, documento is null ? null : Cortar(documento, 60)));
        }

        return new OfxExtrato(
            Limpo(Tag(texto, "BANKID")) ?? Limpo(Tag(texto, "ORG")), Limpo(Tag(texto, "ACCTID")),
            texto.Contains("<CCSTMTRS>", StringComparison.OrdinalIgnoreCase),
            Data(Tag(texto, "DTSTART")), Data(Tag(texto, "DTEND")),
            Valor(Tag(Secao(texto, "LEDGERBAL"), "BALAMT")), Data(Tag(Secao(texto, "LEDGERBAL"), "DTASOF")),
            transacoes);
    }

    /// <summary>UTF-8 quando o arquivo é UTF-8 válido; senão Windows-1252 (o padrão dos OFX 1.x dos bancos).</summary>
    private static string Decodificar(byte[] b)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(b).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1252).GetString(b); }
    }

    private static IEnumerable<string> Blocos(string texto)
    {
        var partes = Regex.Split(texto, "<STMTTRN>", RegexOptions.IgnoreCase);
        foreach (var p in partes.Skip(1))
        {
            var fim = p.IndexOf("</STMTTRN>", StringComparison.OrdinalIgnoreCase);
            var fimLista = p.IndexOf("</BANKTRANLIST>", StringComparison.OrdinalIgnoreCase);
            if (fim < 0 || (fimLista >= 0 && fimLista < fim)) fim = fimLista;
            yield return fim < 0 ? p : p[..fim];
        }
    }

    private static string Secao(string texto, string tag)
    {
        var i = texto.IndexOf($"<{tag}>", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        var f = texto.IndexOf($"</{tag}>", i, StringComparison.OrdinalIgnoreCase);
        return f < 0 ? texto[i..Math.Min(texto.Length, i + 300)] : texto[i..f];
    }

    /// <summary>Valor da tag: até o próximo "&lt;" ou fim de linha (SGML não fecha as tags).</summary>
    private static string? Tag(string texto, string tag)
    {
        var m = Regex.Match(texto, $@"<{tag}>([^<\r\n]*)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static DateOnly? Data(string? s)
    {
        if (s is null || s.Length < 8) return null;
        return DateOnly.TryParseExact(s[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private static decimal? Valor(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Replace(" ", "");
        if (s.Contains(',') && s.Contains('.')) s = s.Replace(".", "").Replace(',', '.');
        else s = s.Replace(',', '.');
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? decimal.Round(v, 2) : null;
    }

    private static string? Limpo(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = System.Net.WebUtility.HtmlDecode(Espacos().Replace(s, " ").Trim());
        return s.Length == 0 ? null : s;
    }

    private static string Cortar(string s, int max) => s.Length <= max ? s : s[..max];

    private static string Hash(string s) => "h" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..32];

    [GeneratedRegex(@"\s+")] private static partial Regex Espacos();

    /// <summary>
    /// Chave de aprendizado da descrição do banco: minúsculas, sem acento, sem números e pontuação
    /// ("PIX ENVIADO 12/11 JOÃO" e "PIX ENVIADO 03/12 JOÃO" viram a mesma chave).
    /// </summary>
    public static string Chave(string descricao)
    {
        var sem = Busca.BuscaGlobalService.Normalizar(descricao);
        var so = new string([.. sem.Select(c => char.IsLetter(c) ? c : ' ')]);
        var k = Espacos().Replace(so, " ").Trim();
        return k.Length <= 120 ? k : k[..120];
    }
}
