using System.Net;
using System.Text;
using Inovabens.Core.Dados;
using Inovabens.Core.Imoveis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Inovabens.Tests;

/// <summary>Comparativo de bancos do simulador: leitura da API do Banco Central, conversão de taxa e faixas do MCMV.</summary>
public class ComparadorFinanciamentoTests
{
    internal const string JsonBancoCentral = """
        {"value":[
          {"Mes":"Set-2026","Modalidade":"Financiamento imobiliário com taxas de mercado - Pós-fixado referenciado em TR","Posicao":8,"InstituicaoFinanceira":"CAIXA ECONOMICA FEDERAL","TaxaJurosAoMes":0.95,"TaxaJurosAoAno":12.05,"cnpj8":"00360305","anoMes":"2026-09"},
          {"Mes":"Ago-2026","Modalidade":"Financiamento imobiliário com taxas de mercado - Pós-fixado referenciado em TR","Posicao":9,"InstituicaoFinanceira":"CAIXA ECONOMICA FEDERAL","TaxaJurosAoMes":0.96,"TaxaJurosAoAno":12.10,"cnpj8":"00360305","anoMes":"2026-08"},
          {"Mes":"Set-2026","Modalidade":"Financiamento imobiliário com taxas reguladas - Pós-fixado referenciado em TR","Posicao":1,"InstituicaoFinanceira":"CAIXA ECONOMICA FEDERAL","TaxaJurosAoMes":0.64,"TaxaJurosAoAno":7.98,"cnpj8":"00360305","anoMes":"2026-09"},
          {"Mes":"Set-2026","Modalidade":"Financiamento imobiliário com taxas de mercado - Pós-fixado referenciado em TR","Posicao":1,"InstituicaoFinanceira":"BCO DO ESTADO DO RS S.A.","TaxaJurosAoMes":0.75,"TaxaJurosAoAno":9.39,"cnpj8":"92702067","anoMes":"2026-09"},
          {"Mes":"Set-2026","Modalidade":"Financiamento imobiliário com taxas de mercado - Pós-fixado referenciado em TR","Posicao":13,"InstituicaoFinanceira":"BCO DO BRASIL S.A.","TaxaJurosAoMes":1.13,"TaxaJurosAoAno":14.49,"cnpj8":"00000000","anoMes":"2026-09"},
          {"Mes":"Set-2026","Modalidade":"Cartão de crédito - rotativo total","Posicao":1,"InstituicaoFinanceira":"BCO DO BRASIL S.A.","TaxaJurosAoMes":12.0,"TaxaJurosAoAno":290.0,"cnpj8":"00000000","anoMes":"2026-09"}
        ]}
        """;

    [Fact]
    public void Le_so_a_modalidade_de_mercado_com_TR_e_so_os_bancos_do_catalogo()
    {
        var r = ComparadorFinanciamento.LerBancoCentral(JsonBancoCentral);
        Assert.Equal(3, r.Count); // Caixa set e ago + BB; fora: regulada, Banrisul (regional) e cartão
        Assert.Contains(r, x => x.Cnpj8 == "00360305" && x.Competencia == new DateOnly(2026, 9, 1) && x.TaxaAno == 0.1205m);
        Assert.Contains(r, x => x.Cnpj8 == "00000000" && x.TaxaAno == 0.1449m && x.TaxaMes == 0.0113m);
        Assert.DoesNotContain(r, x => x.Cnpj8 == "92702067");
    }

    [Fact]
    public void Taxa_efetiva_anual_vira_nominal_para_juros_mensais()
    {
        // 12,05% efetiva ao ano = 0,9527% ao mês = 11,43% nominal ao ano
        var nominal = ComparadorFinanciamento.NominalDeEfetiva(0.1205m);
        Assert.InRange(nominal, 0.1142m, 0.1144m);
        var s = FinanciamentoCaixa.Simular(100_000, 20_000, 120, nominal, sac: true);
        Assert.InRange(s.TaxaEfetivaAnual, 0.1204m, 0.1206m);
    }

    [Theory]
    [InlineData(3_000, 250_000, 1)]
    [InlineData(5_000, 270_000, 2)]
    [InlineData(9_000, 390_000, 3)]
    [InlineData(12_000, 590_000, 4)]
    public void Faixa_do_mcmv_pela_renda(decimal renda, decimal valor, int faixa) =>
        Assert.Equal(faixa, ComparadorFinanciamento.FaixaPorRenda(renda, valor)?.Numero);

    [Theory]
    [InlineData(14_000, 300_000)] // renda acima de R$ 13 mil
    [InlineData(3_000, 300_000)]  // faixa 1 só até R$ 275 mil
    [InlineData(12_000, 650_000)] // acima do teto do programa
    public void Fora_do_mcmv(decimal renda, decimal valor) => Assert.Null(ComparadorFinanciamento.FaixaPorRenda(renda, valor));

    [Fact]
    public void Catalogo_tem_os_bancos_nacionais_e_link_https()
    {
        Assert.Equal(["caixa", "itau", "bradesco", "santander", "bb", "inter", "sicoob", "sicredi"], ComparadorFinanciamento.Bancos.Select(b => b.Codigo));
        Assert.All(ComparadorFinanciamento.Bancos, b => Assert.StartsWith("https://", b.LinkSimulacao));
        Assert.Equal(600_000m, ComparadorFinanciamento.TetoMcmv);
    }
}

/// <summary>Gravação das taxas do Banco Central e a taxa em vigor de cada banco, contra PostgreSQL real.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class TaxasFinanciamentoIntegracaoTests
{
    private sealed class BancoCentralFalso : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ComparadorFinanciamentoTests.JsonBancoCentral, Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public async Task Grava_as_taxas_e_usa_a_mais_recente_ou_a_de_referencia()
    {
        var fabrica = ExtratoComprovantesTests.Compartilhado.GetRequiredService<IDbContextFactory<InovabensDb>>();
        await using (var db = await fabrica.CreateDbContextAsync())
            await db.Database.ExecuteSqlRawAsync("DELETE FROM taxas_financiamento");
        var s = new TaxasFinanciamentoService(fabrica, NullLogger<TaxasFinanciamentoService>.Instance, new HttpClient(new BancoCentralFalso()));

        Assert.Null(await s.UltimaAtualizacaoAsync());
        var semSerie = await s.AtuaisAsync();
        Assert.Equal(0.1205m, semSerie.Single(t => t.Banco.Codigo == "caixa").TaxaAno); // referência do catálogo

        Assert.Equal(3, await s.AtualizarAsync());
        Assert.Equal(3, await s.AtualizarAsync()); // de novo: atualiza, não duplica
        Assert.NotNull(await s.UltimaAtualizacaoAsync());

        var atuais = await s.AtuaisAsync();
        Assert.Equal(8, atuais.Count);
        var caixa = atuais.Single(t => t.Banco.Codigo == "caixa");
        Assert.Equal(new DateOnly(2026, 9, 1), caixa.Competencia); // a mais recente (set), não ago
        Assert.Equal(0.1205m, caixa.TaxaAno);
        Assert.Equal(0.1449m, atuais.Single(t => t.Banco.Codigo == "bb").TaxaAno);
        await using (var db = await fabrica.CreateDbContextAsync())
            Assert.Equal(3, await db.TaxasFinanciamento.CountAsync());
    }
}
