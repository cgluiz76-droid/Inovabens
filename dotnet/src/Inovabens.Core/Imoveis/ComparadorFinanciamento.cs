using System.Globalization;
using System.Text.Json;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Inovabens.Core.Imoveis;

/// <summary>
/// Banco do comparativo de financiamento do site. A taxa vem da média que o banco praticou no mês, publicada pelo
/// Banco Central; <see cref="TaxaReferenciaAno"/> é usada enquanto o sistema ainda não tem a série gravada.
/// </summary>
public sealed record BancoFinanciamento(
    string Codigo, string Nome, string Cnpj8, string Sigla, string Cor, string CorTexto,
    string LinkSimulacao, decimal TaxaReferenciaAno, bool Cooperativa = false)
{
    /// <summary>Logo oficial em wwwroot/bancos (Wikimedia Commons, domínio público).</summary>
    public string Logo => Codigo == "bradesco" ? "bancos/bradesco.png" : $"bancos/{Codigo}.svg";
}

/// <summary>Faixa do Minha Casa, Minha Vida: renda familiar bruta máxima, teto do imóvel e juros nominais ao ano.</summary>
public sealed record FaixaMcmv(int Numero, decimal RendaMaxima, decimal TetoImovel, decimal TaxaMinima, decimal TaxaMaxima);

/// <summary>Taxa em vigor de um banco: média do mês (Banco Central) ou a de referência do catálogo.</summary>
public sealed record TaxaBanco(BancoFinanciamento Banco, decimal TaxaAno, DateOnly? Competencia);

/// <summary>Bancos, série do Banco Central e faixas do MCMV usados no comparativo do simulador.</summary>
public static class ComparadorFinanciamento
{
    /// <summary>Série do BC usada para todos os bancos (as taxas reguladas incluem linhas sociais e distorcem a média).</summary>
    public const string Modalidade = "Financiamento imobiliário com taxas de mercado - Pós-fixado referenciado em TR";

    /// <summary>Taxas de juros por instituição, médias mensais (API Olinda do Banco Central, sem chave).</summary>
    public const string UrlBancoCentral =
        "https://olinda.bcb.gov.br/olinda/servico/taxaJuros/versao/v2/odata/TaxasJurosMensalPorMes?$top=20000&$format=json";

    /// <summary>Mês das taxas de referência abaixo (valores do BC de set/2026).</summary>
    public static readonly DateOnly CompetenciaReferencia = new(2026, 9, 1);

    public static readonly IReadOnlyList<BancoFinanciamento> Bancos =
    [
        new("caixa", "Caixa Econômica Federal", "00360305", "CAIXA", "#005CA9", "#ffffff", "https://simuladorhabitacao.caixa.gov.br/", 0.1205m),
        new("itau", "Itaú Unibanco", "60701190", "itaú", "#EC7000", "#ffffff", "https://www.itau.com.br/emprestimos-financiamentos/credito-imobiliario", 0.1214m),
        new("bradesco", "Bradesco", "60746948", "bradesco", "#CC092F", "#ffffff",
            "https://banco.bradesco/html/classic/produtos-servicos/emprestimo-e-financiamento/encontre-seu-credito/simuladores-imoveis.shtm", 0.1224m),
        new("santander", "Santander", "90400888", "santander", "#EC0000", "#ffffff", "https://www.santander.com.br/hotsite/credito-financiamento-imobiliario/", 0.1231m),
        new("bb", "Banco do Brasil", "00000000", "BB", "#FCF800", "#0038A8", "https://www.bb.com.br/site/pra-voce/financiamentos/financiamento-imobiliario/", 0.1449m),
        new("inter", "Banco Inter", "00416968", "inter", "#FF7A00", "#ffffff", "https://inter.co/pra-voce/financiamento-imobiliario/residencial/", 0.1291m),
        new("sicoob", "Sicoob", "02038232", "sicoob", "#003641", "#7DB61C", "https://www.sicoob.com.br/web/sicoob/credito-voce/financiamentoimobiliario", 0.1079m, Cooperativa: true),
        new("sicredi", "Sicredi", "01181521", "sicredi", "#3FA110", "#ffffff", "https://www.sicredi.com.br/site/credito/para-voce/credito-imobiliario/", 0.1156m, Cooperativa: true),
    ];

    /// <summary>
    /// Minha Casa, Minha Vida (regras em vigor desde 22/04/2026; conferir sempre que o Conselho Curador do FGTS mudar).
    /// Juros nominais ao ano, do menor (Norte/Nordeste, cotista do FGTS) ao maior da faixa. Faixas 1 e 2 têm teto de
    /// R$ 275 mil (pode variar com o município) e podem ter subsídio na entrada, que a simulação não considera.
    /// </summary>
    public static readonly IReadOnlyList<FaixaMcmv> FaixasMcmv =
    [
        new(1, 3_200m, 275_000m, 0.0400m, 0.0450m),
        new(2, 5_000m, 275_000m, 0.0475m, 0.0700m),
        new(3, 9_600m, 400_000m, 0.0766m, 0.0816m),
        new(4, 13_000m, 600_000m, 0.1000m, 0.1050m),
    ];

    public const string McmvConferidoEm = "novembro de 2026";
    public const string McmvLink = "https://simuladorhabitacao.caixa.gov.br/";
    public const string McmvLogo = "bancos/mcmv.png";

    public static decimal TetoMcmv => FaixasMcmv.Max(f => f.TetoImovel);

    /// <summary>Taxas de referência do catálogo (antes da primeira consulta ao Banco Central).</summary>
    public static IReadOnlyList<TaxaBanco> TaxasDeReferencia() =>
        [.. Bancos.Select(b => new TaxaBanco(b, b.TaxaReferenciaAno, CompetenciaReferencia))];

    /// <summary>Faixa pela renda (null = renda acima do programa ou imóvel acima do teto da faixa).</summary>
    public static FaixaMcmv? FaixaPorRenda(decimal renda, decimal valorImovel) =>
        FaixasMcmv.FirstOrDefault(f => renda <= f.RendaMaxima) is { } f && valorImovel <= f.TetoImovel ? f : null;

    /// <summary>Juros nominais ao ano equivalentes a uma taxa efetiva anual (a Caixa calcula juros mensais = nominal / 12).</summary>
    public static decimal NominalDeEfetiva(decimal efetivaAno) =>
        decimal.Round((decimal)(12 * (Math.Pow(1 + (double)efetivaAno, 1.0 / 12) - 1)), 6);

    /// <summary>Lê o JSON da API do BC e devolve as taxas (efetivas ao ano) da modalidade, só dos bancos do catálogo.</summary>
    public static List<(string Cnpj8, DateOnly Competencia, string Instituicao, decimal TaxaMes, decimal TaxaAno)> LerBancoCentral(string json)
    {
        var cnpjs = Bancos.Select(b => b.Cnpj8).ToHashSet();
        var r = new List<(string, DateOnly, string, decimal, decimal)>();
        using var doc = JsonDocument.Parse(json);
        foreach (var x in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            if (x.GetProperty("Modalidade").GetString() != Modalidade) continue;
            var cnpj = x.GetProperty("cnpj8").GetString() ?? "";
            if (!cnpjs.Contains(cnpj)) continue;
            if (!DateOnly.TryParseExact((x.GetProperty("anoMes").GetString() ?? "") + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var mes))
                continue;
            r.Add((cnpj, mes, x.GetProperty("InstituicaoFinanceira").GetString() ?? "",
                x.GetProperty("TaxaJurosAoMes").GetDecimal() / 100, x.GetProperty("TaxaJurosAoAno").GetDecimal() / 100));
        }
        return r;
    }
}

/// <summary>Taxas médias de financiamento imobiliário por banco, gravadas a partir do Banco Central.</summary>
public sealed class TaxasFinanciamentoService(IDbContextFactory<InovabensDb> fabrica, ILogger<TaxasFinanciamentoService> log, HttpClient? http = null)
{
    // O BC trava quando a conexão keep-alive é reaproveitada (ver IndicesService): uma conexão por consulta.
    private static readonly HttpClient Padrao = new() { Timeout = TimeSpan.FromSeconds(90), DefaultRequestHeaders = { ConnectionClose = true } };

    /// <summary>A série é mensal; a rotina confere uma vez por semana se saiu mês novo.</summary>
    public static readonly TimeSpan Intervalo = TimeSpan.FromDays(7);

    /// <summary>Última conferência no Banco Central (null = nunca).</summary>
    public async Task<DateTime?> UltimaAtualizacaoAsync(CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        return await db.TaxasFinanciamento.MaxAsync(t => (DateTime?)t.AtualizadoEm, ct);
    }

    /// <summary>Baixa as médias mensais do BC e grava (insere ou atualiza) as dos bancos do catálogo. Devolve quantas vieram.</summary>
    public async Task<int> AtualizarAsync(CancellationToken ct = default)
    {
        var json = await (http ?? Padrao).GetStringAsync(ComparadorFinanciamento.UrlBancoCentral, ct);
        var linhas = ComparadorFinanciamento.LerBancoCentral(json);
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var agora = DateTime.UtcNow;
        foreach (var (cnpj8, mes, nome, taxaMes, taxaAno) in linhas)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO taxas_financiamento (cnpj8, competencia, instituicao, taxa_mes, taxa_ano, atualizado_em)
                VALUES ({cnpj8}, {mes}, {nome}, {taxaMes}, {taxaAno}, {agora})
                ON CONFLICT (cnpj8, competencia) DO UPDATE
                SET instituicao = EXCLUDED.instituicao, taxa_mes = EXCLUDED.taxa_mes, taxa_ano = EXCLUDED.taxa_ano, atualizado_em = EXCLUDED.atualizado_em
                """, ct);
        // marca a conferência mesmo sem mês novo, para a rotina não consultar de novo antes do intervalo
        if (linhas.Count > 0)
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE taxas_financiamento SET atualizado_em = {agora}", ct);
        log.LogInformation("Taxas de financiamento do Banco Central: {Linhas} registros", linhas.Count);
        return linhas.Count;
    }

    /// <summary>Taxa mais recente de cada banco do catálogo (sem série gravada, a de referência).</summary>
    public async Task<IReadOnlyList<TaxaBanco>> AtuaisAsync(CancellationToken ct = default)
    {
        List<TaxaFinanciamento> ultimas;
        try
        {
            await using var db = await fabrica.CreateDbContextAsync(ct);
            ultimas = await db.TaxasFinanciamento.AsNoTracking()
                .GroupBy(t => t.Cnpj8)
                .Select(g => g.OrderByDescending(t => t.Competencia).First())
                .ToListAsync(ct);
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
        {
            log.LogWarning(e, "Taxas de financiamento indisponíveis; usando as de referência");
            ultimas = [];
        }
        return [.. ComparadorFinanciamento.TaxasDeReferencia().Select(r =>
            ultimas.FirstOrDefault(t => t.Cnpj8 == r.Banco.Cnpj8) is { } t ? r with { TaxaAno = t.TaxaAno, Competencia = t.Competencia } : r)];
    }
}
