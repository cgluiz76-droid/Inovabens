using System.Globalization;
using System.Net.Http.Json;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Financeiro;

/// <summary>Séries de índices econômicos (variação mensal %), sincronizadas do Banco Central (SGS). Port de indices.module.ts.</summary>
public sealed class IndicesService(IDbContextFactory<InovabensDb> fabrica)
{
    /// <summary>Séries SGS do Banco Central: variação mensal em %.</summary>
    private static readonly Dictionary<CodigoIndice, int> Sgs = new()
    {
        [CodigoIndice.IPCA] = 433, [CodigoIndice.INCC] = 192, [CodigoIndice.IGPM] = 189, [CodigoIndice.SELIC] = 4390,
    };

    // O servidor do BCB não responde quando a conexão keep-alive é reaproveitada (a 2ª série trava até o timeout):
    // cada consulta abre uma conexão nova.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30), DefaultRequestHeaders = { ConnectionClose = true } };

    public async Task<IReadOnlyList<IndiceSerie>> SerieAsync(CodigoIndice codigo, DateOnly? de = null, DateOnly? ate = null)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var q = db.IndicesSerie.AsNoTracking().Where(s => s.Codigo == codigo);
        if (de is { } d1) q = q.Where(s => s.Competencia >= d1);
        if (ate is { } d2) q = q.Where(s => s.Competencia <= d2);
        return await q.OrderBy(s => s.Competencia).ToListAsync();
    }

    public async Task<FatoresIndice> FatoresAsync(CodigoIndice codigo) =>
        new((await SerieAsync(codigo)).Select(s => (s.Competencia, s.VariacaoMensalPct)));

    /// <summary>Lançamento manual (índice ainda não publicado pelo BCB ou correção de valor).</summary>
    public async Task LancarManualAsync(UsuarioAtual u, CodigoIndice codigo, DateOnly competencia, decimal variacaoPct)
    {
        u.Exigir(P.AdminConfig);
        await GravarAsync(codigo, [(new DateOnly(competencia.Year, competencia.Month, 1), variacaoPct)], "MANUAL");
    }

    /// <summary>Busca as séries no BCB a partir do último mês gravado. Devolve quantos pontos vieram por índice.</summary>
    public async Task<IReadOnlyDictionary<CodigoIndice, int>> SincronizarAsync(UsuarioAtual u, IEnumerable<CodigoIndice>? codigos = null)
    {
        u.Exigir(P.AdminConfig);
        return await SincronizarAsync(codigos);
    }

    /// <summary>Versão sem usuário, para a rotina mensal automática.</summary>
    public async Task<IReadOnlyDictionary<CodigoIndice, int>> SincronizarAsync(IEnumerable<CodigoIndice>? codigos = null, CancellationToken ct = default)
    {
        var resultado = new Dictionary<CodigoIndice, int>();
        foreach (var codigo in codigos ?? Enum.GetValues<CodigoIndice>())
        {
            DateOnly? ultimo;
            await using (var db = await fabrica.CreateDbContextAsync(ct))
                ultimo = await db.IndicesSerie.Where(s => s.Codigo == codigo).MaxAsync(s => (DateOnly?)s.Competencia, ct);
            var ini = ultimo is { } u ? $"01/{u:MM}/{u:yyyy}" : "01/01/2000";
            var url = $"https://api.bcb.gov.br/dados/serie/bcdata.sgs.{Sgs[codigo]}/dados?formato=json&dataInicial={ini}";

            List<PontoBcb> json;
            try
            {
                json = await Http.GetFromJsonAsync<List<PontoBcb>>(url, ct) ?? [];
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                throw new ServicoIndisponivelException($"Falha ao consultar o Banco Central ({codigo}): {e.Message}");
            }
            var pontos = LerPontos(json);
            await GravarAsync(codigo, pontos, "BCB-SGS", ct);
            resultado[codigo] = pontos.Count;
        }
        return resultado;
    }

    private async Task GravarAsync(CodigoIndice codigo, IReadOnlyList<(DateOnly Competencia, decimal Pct)> pontos, string fonte, CancellationToken ct = default)
    {
        if (pontos.Count == 0) return;
        await using var db = await fabrica.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlRawAsync("""
            insert into indices_serie (codigo, competencia, variacao_mensal_pct, fonte)
            select @codigo::codigo_indice, t.c, t.v, @fonte from unnest(@datas, @valores) as t(c, v)
            on conflict (codigo, competencia) do update
               set variacao_mensal_pct = excluded.variacao_mensal_pct, fonte = excluded.fonte, atualizado_em = now()
            """,
            [
                new NpgsqlParameter("codigo", NpgsqlDbType.Text) { Value = codigo.ToString() },
                new NpgsqlParameter("fonte", NpgsqlDbType.Text) { Value = fonte },
                new NpgsqlParameter("datas", NpgsqlDbType.Array | NpgsqlDbType.Date) { Value = pontos.Select(p => p.Competencia).ToArray() },
                new NpgsqlParameter("valores", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = pontos.Select(p => p.Pct).ToArray() },
            ], ct);
    }

    // formato do SGS: { "data": "01/01/2025", "valor": "0.16" }
    internal sealed record PontoBcb(string data, string valor);

    /// <summary>Converte a resposta do SGS em (competência = 1º dia do mês, variação %).</summary>
    internal static List<(DateOnly Competencia, decimal Pct)> LerPontos(IEnumerable<PontoBcb> json)
    {
        try
        {
            return json.Select(j => (Data: j.data ?? "", Valor: j.valor ?? "")).Select(j => (
                new DateOnly(int.Parse(j.Data[6..10]), int.Parse(j.Data[3..5]), 1),
                decimal.Parse(j.Valor, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture))).ToList();
        }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
        {
            throw new ServicoIndisponivelException($"Resposta inesperada do Banco Central: {e.Message}");
        }
    }
}
