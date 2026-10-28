using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Financeiro;

namespace Inovabens.Web.Jobs;

/// <summary>
/// Todo dia 12 às 06:00 (Brasília), após as divulgações do IPCA/INCC: busca os índices no Banco Central.
/// Equivale ao <c>@Cron('0 6 12 * *')</c> do Node. Desligado com DISABLE_JOBS=true.
/// A gravação é um upsert, então rodar junto com o job do Node não duplica dados.
/// </summary>
public sealed class SincronizacaoIndicesJob(IServiceScopeFactory escopos, InovabensOpcoes opcoes, ILogger<SincronizacaoIndicesJob> log)
    : BackgroundService
{
    internal static DateTime ProximaExecucaoBR(DateTime agoraBR)
    {
        var alvo = new DateTime(agoraBR.Year, agoraBR.Month, 12, 6, 0, 0);
        return alvo > agoraBR ? alvo : alvo.AddMonths(1);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (opcoes.DesativarJobs) return;
        while (!ct.IsCancellationRequested)
        {
            var proxima = DatasBR.ParaUtc(ProximaExecucaoBR(DatasBR.Agora()));
            var espera = proxima - DateTime.UtcNow;
            // Task.Delay aceita no máximo ~24,8 dias: espera em etapas e recalcula.
            if (espera > TimeSpan.FromDays(1))
            {
                await Task.Delay(TimeSpan.FromDays(1), ct);
                continue;
            }
            if (espera > TimeSpan.Zero) await Task.Delay(espera, ct);

            try
            {
                using var escopo = escopos.CreateScope();
                var r = await escopo.ServiceProvider.GetRequiredService<IndicesService>().SincronizarAsync(ct: ct);
                log.LogInformation("Sync BCB: {Resultado}", string.Join(", ", r.Select(x => $"{x.Key}={x.Value}")));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Falha na sincronização dos índices");
            }
        }
    }
}
