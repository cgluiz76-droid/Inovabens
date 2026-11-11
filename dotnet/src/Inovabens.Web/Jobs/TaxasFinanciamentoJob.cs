using Inovabens.Core;
using Inovabens.Core.Imoveis;

namespace Inovabens.Web.Jobs;

/// <summary>
/// Taxas médias de financiamento imobiliário por banco (comparativo do simulador do site). O Banco Central publica a
/// série por mês, com 1 a 2 meses de atraso: a rotina confere uma vez por semana (e logo na subida, se nunca conferiu)
/// e grava no banco, então as páginas não consultam o BC. Desligado com DISABLE_JOBS=true.
/// </summary>
public sealed class TaxasFinanciamentoJob(IServiceScopeFactory escopos, InovabensOpcoes opcoes, ILogger<TaxasFinanciamentoJob> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (opcoes.DesativarJobs) return;
        await Task.Delay(TimeSpan.FromMinutes(1), ct); // deixa a aplicação subir antes
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var escopo = escopos.CreateScope();
                var taxas = escopo.ServiceProvider.GetRequiredService<TaxasFinanciamentoService>();
                var ultima = await taxas.UltimaAtualizacaoAsync(ct);
                if (ultima is null || DateTime.UtcNow - ultima >= TaxasFinanciamentoService.Intervalo)
                    await taxas.AtualizarAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Falha ao buscar as taxas de financiamento no Banco Central");
            }
            await Task.Delay(TimeSpan.FromHours(6), ct);
        }
    }
}
