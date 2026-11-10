namespace Inovabens.Core.Comum;

/// <summary>Valores em reais por extenso, para propostas e contratos (ex.: "trezentos e cinquenta mil reais").</summary>
public static class Extenso
{
    private static readonly string[] Unidades =
        ["", "um", "dois", "três", "quatro", "cinco", "seis", "sete", "oito", "nove", "dez", "onze", "doze", "treze", "quatorze",
         "quinze", "dezesseis", "dezessete", "dezoito", "dezenove"];
    private static readonly string[] Dezenas = ["", "", "vinte", "trinta", "quarenta", "cinquenta", "sessenta", "setenta", "oitenta", "noventa"];
    private static readonly string[] Centenas =
        ["", "cento", "duzentos", "trezentos", "quatrocentos", "quinhentos", "seiscentos", "setecentos", "oitocentos", "novecentos"];

    private static readonly (string Singular, string Plural)[] Escalas =
        [("", ""), ("mil", "mil"), ("milhão", "milhões"), ("bilhão", "bilhões"), ("trilhão", "trilhões")];

    /// <summary>De 0 a 999.</summary>
    private static string Centena(int n)
    {
        if (n == 0) return "";
        if (n == 100) return "cem";
        var partes = new List<string>();
        if (n >= 100) partes.Add(Centenas[n / 100]);
        var resto = n % 100;
        if (resto >= 20)
        {
            partes.Add(Dezenas[resto / 10]);
            if (resto % 10 > 0) partes.Add(Unidades[resto % 10]);
        }
        else if (resto > 0) partes.Add(Unidades[resto]);
        return string.Join(" e ", partes);
    }

    /// <summary>Número inteiro por extenso ("um milhão e duzentos mil").</summary>
    public static string Inteiro(long n)
    {
        if (n == 0) return "zero";
        if (n < 0) return "menos " + Inteiro(-n);
        var grupos = new List<int>();
        for (var x = n; x > 0; x /= 1000) grupos.Add((int)(x % 1000));
        var partes = new List<(string Texto, int Valor)>();
        for (var i = grupos.Count - 1; i >= 0; i--)
        {
            var g = grupos[i];
            if (g == 0) continue;
            string texto;
            if (i == 0) texto = Centena(g);
            else if (i == 1) texto = g == 1 ? "mil" : $"{Centena(g)} mil";
            else texto = $"{Centena(g)} {(g == 1 ? Escalas[i].Singular : Escalas[i].Plural)}";
            partes.Add((texto, g));
        }
        // "e" antes do último grupo quando ele é menor que 100 ou centena redonda (ex.: "mil e duzentos", "dois mil e cinco")
        var s = partes[0].Texto;
        for (var i = 1; i < partes.Count; i++)
        {
            var (t, v) = partes[i];
            var ultimo = i == partes.Count - 1;
            s += ultimo && (v < 100 || v % 100 == 0) ? $" e {t}" : $", {t}";
        }
        return s;
    }

    /// <summary>Valor em reais por extenso ("mil e quinhentos reais e cinquenta centavos").</summary>
    public static string Reais(decimal valor)
    {
        valor = Math.Round(Math.Abs(valor), 2);
        var inteiro = (long)decimal.Truncate(valor);
        var centavos = (int)((valor - inteiro) * 100);
        var partes = new List<string>();
        if (inteiro > 0)
        {
            var t = Inteiro(inteiro);
            // "um milhão de reais", "dois milhões de reais": escala redonda pede "de"
            var de = inteiro >= 1_000_000 && inteiro % 1_000_000 == 0 ? " de" : "";
            partes.Add($"{t}{de} {(inteiro == 1 ? "real" : "reais")}");
        }
        if (centavos > 0) partes.Add($"{Inteiro(centavos)} {(centavos == 1 ? "centavo" : "centavos")}");
        return partes.Count == 0 ? "zero real" : string.Join(" e ", partes);
    }
}
