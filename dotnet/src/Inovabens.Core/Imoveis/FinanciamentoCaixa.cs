namespace Inovabens.Core.Imoveis;

/// <summary>Resultado de uma simulação (valores sem TR, seguros MIP/DFI e tarifa de administração).</summary>
public sealed record SimulacaoFinanciamento(
    decimal ValorFinanciado, decimal Entrada, int Prazo, decimal TaxaNominalAnual, decimal TaxaEfetivaAnual,
    decimal PrimeiraParcela, decimal UltimaParcela, decimal TotalJuros, decimal RendaMinima, bool Sfh);

/// <summary>
/// Parâmetros do crédito imobiliário da Caixa (SBPE, aquisição de imóvel residencial) usados no simulador do site.
/// Fonte: condições divulgadas pela Caixa a partir de 13/10/2025 (Resolução CMN 5.255) e tabelas publicadas em 2026.
/// Revisar sempre que a Caixa mudar as condições: <see cref="ConferidoEm"/> aparece no aviso do simulador.
/// </summary>
public static class FinanciamentoCaixa
{
    public const string ConferidoEm = "novembro de 2026";

    /// <summary>Teto do imóvel no SFH; acima disso o financiamento é pelo SFI.</summary>
    public const decimal TetoSfh = 2_250_000m;

    /// <summary>Cota máxima financiada (fração do valor do imóvel): SAC 80%, Price 70%.</summary>
    public const decimal CotaSac = 0.80m;
    public const decimal CotaPrice = 0.70m;

    /// <summary>Prazo máximo, em meses (35 anos).</summary>
    public const int PrazoMaximo = 420;

    /// <summary>Idade do proponente mais velho + prazo não pode passar de 80 anos e 6 meses.</summary>
    public const int IdadeMaximaMeses = 80 * 12 + 6;

    /// <summary>A prestação não pode passar de 30% da renda bruta familiar.</summary>
    public const decimal ComprometimentoRenda = 0.30m;

    /// <summary>Juros nominais ao ano + TR. SFH: 10,99% (com relacionamento) a 11,49% (balcão); SFI: 11,80% a 12%.</summary>
    public const decimal TaxaSfhMinima = 0.1099m;
    public const decimal TaxaSfhBalcao = 0.1149m;
    public const decimal TaxaSfiMinima = 0.1180m;
    public const decimal TaxaSfiBalcao = 0.1200m;

    /// <summary>Até este valor, linhas como Minha Casa, Minha Vida e Pró-Cotista podem ter juros menores.</summary>
    public const decimal TetoLinhasSociais = 500_000m;

    /// <summary>Taxa de administração mensal cobrada pela Caixa (não entra na simulação; aparece no aviso).</summary>
    public const decimal TarifaAdministracao = 25m;

    public const string SimuladorOficial = "https://habitacao.caixa.gov.br/siopiweb-web/simulaOperacaoInternet.do?method=inicializarCasoUso";

    public static bool Sfh(decimal valorImovel) => valorImovel <= TetoSfh;

    public static decimal Cota(bool sac) => sac ? CotaSac : CotaPrice;

    public static decimal TaxaPadrao(decimal valorImovel) => Sfh(valorImovel) ? TaxaSfhBalcao : TaxaSfiBalcao;

    /// <summary>Prazo máximo para a idade informada (null = sem limite de idade aplicado).</summary>
    public static int PrazoMaximoPara(int? idadeAnos) =>
        idadeAnos is { } i ? Math.Clamp(IdadeMaximaMeses - i * 12, 0, PrazoMaximo) : PrazoMaximo;

    /// <summary>
    /// Simula como a Caixa calcula: juros mensais = taxa nominal anual / 12; SAC com amortização constante,
    /// Price com parcela fixa. A entrada é ajustada para o mínimo exigido pela cota do sistema escolhido.
    /// </summary>
    public static SimulacaoFinanciamento Simular(decimal valorImovel, decimal entrada, int prazoMeses, decimal taxaNominalAnual, bool sac, int? idadeAnos = null)
    {
        if (valorImovel <= 0) throw new ArgumentOutOfRangeException(nameof(valorImovel));
        var entradaMinima = decimal.Round(valorImovel * (1 - Cota(sac)), 2);
        entrada = Math.Clamp(entrada, entradaMinima, valorImovel);
        var n = Math.Clamp(prazoMeses, 1, Math.Max(1, PrazoMaximoPara(idadeAnos)));
        var p = valorImovel - entrada;
        var i = (double)taxaNominalAnual / 12;
        decimal primeira, ultima, juros;
        if (sac)
        {
            var amort = p / n;
            primeira = amort + p * (decimal)i;
            ultima = amort + amort * (decimal)i;
            juros = p * (decimal)i * (n + 1) / 2;
        }
        else
        {
            var pmt = i == 0 ? (double)p / n : (double)p * i / (1 - Math.Pow(1 + i, -n));
            primeira = ultima = (decimal)pmt;
            juros = (decimal)pmt * n - p;
        }
        var efetiva = (decimal)(Math.Pow(1 + i, 12) - 1);
        return new SimulacaoFinanciamento(p, entrada, n, taxaNominalAnual, decimal.Round(efetiva, 4),
            decimal.Round(primeira, 2), decimal.Round(ultima, 2), decimal.Round(juros, 2),
            decimal.Round(primeira / ComprometimentoRenda, 2), Sfh(valorImovel));
    }
}
