using Inovabens.Core;
using Inovabens.Core.Agenda;
using Inovabens.Core.Comum;
using Inovabens.Core.Financeiro;
using Inovabens.Core.Imoveis;

namespace Inovabens.Web.Jobs;

/// <summary>
/// Base das rotinas agendadas (equivalentes aos @Cron do Node). Horários no fuso de Brasília;
/// todas ficam desligadas com DISABLE_JOBS=true. Uma falha é registrada no log e a rotina segue na próxima vez.
/// </summary>
public abstract class Rotina(IServiceScopeFactory escopos, InovabensOpcoes opcoes, ILogger log) : BackgroundService
{
    protected abstract string Nome { get; }
    protected InovabensOpcoes Opcoes => opcoes;
    protected ILogger Log => log;

    /// <summary>Próxima execução (horário de Brasília) depois de <paramref name="agoraBR"/>.</summary>
    protected internal abstract DateTime ProximaBR(DateTime agoraBR);

    protected abstract Task Rodar(IServiceProvider sp, CancellationToken ct);

    protected virtual bool Habilitada => true;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (opcoes.DesativarJobs || !Habilitada) return;
        while (!ct.IsCancellationRequested)
        {
            var espera = DatasBR.ParaUtc(ProximaBR(DatasBR.Agora())) - DateTime.UtcNow;
            if (espera > TimeSpan.FromDays(1))
            {
                await Task.Delay(TimeSpan.FromDays(1), ct);
                continue;
            }
            if (espera > TimeSpan.Zero) await Task.Delay(espera, ct);
            try
            {
                using var escopo = escopos.CreateScope();
                await Rodar(escopo.ServiceProvider, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Falha na rotina {Rotina}", Nome);
            }
        }
    }

    /// <summary>Próximo múltiplo de <paramref name="minutos"/> minutos (ex.: 5 → :00, :05, :10…).</summary>
    internal static DateTime ACada(DateTime agoraBR, int minutos)
    {
        var inicio = new DateTime(agoraBR.Year, agoraBR.Month, agoraBR.Day, agoraBR.Hour, 0, 0);
        return inicio.AddMinutes((agoraBR.Minute / minutos + 1) * minutos);
    }

    internal static DateTime Diaria(DateTime agoraBR, int hora)
    {
        var alvo = agoraBR.Date.AddHours(hora);
        return alvo > agoraBR ? alvo : alvo.AddDays(1);
    }
}

/// <summary>A cada 5 min: compromissos pendentes que já terminaram viram alerta "não cumprido".</summary>
public sealed class AlertasCompromissosJob(IServiceScopeFactory e, InovabensOpcoes o, ILogger<AlertasCompromissosJob> log) : Rotina(e, o, log)
{
    protected override string Nome => "alertas de compromissos";
    protected internal override DateTime ProximaBR(DateTime agoraBR) => ACada(agoraBR, 5);

    protected override async Task Rodar(IServiceProvider sp, CancellationToken ct)
    {
        var r = await sp.GetRequiredService<AlertasService>().VerificarCompromissosAsync(ct);
        if (r.Criados + r.Resolvidos > 0) Log.LogInformation("Alertas de compromissos: {Criados} criado(s), {Resolvidos} resolvido(s)", r.Criados, r.Resolvidos);
    }
}

/// <summary>Todo dia às 06:00: gera os lançamentos do mês (checklist) e alerta despesas vencidas não pagas.</summary>
public sealed class RotinaFinanceiraDiariaJob(IServiceScopeFactory e, InovabensOpcoes o, ILogger<RotinaFinanceiraDiariaJob> log) : Rotina(e, o, log)
{
    protected override string Nome => "financeiro diário";
    protected internal override DateTime ProximaBR(DateTime agoraBR) => Diaria(agoraBR, 6);

    protected override async Task Rodar(IServiceProvider sp, CancellationToken ct)
    {
        var hoje = DatasBR.Hoje();
        var gerados = await sp.GetRequiredService<DespesasService>().GerarCompetenciaAsync(new DateOnly(hoje.Year, hoje.Month, 1));
        var r = await sp.GetRequiredService<AlertasService>().VerificarDespesasAsync(ct);
        Log.LogInformation("Rotina financeira: {Gerados} lançamento(s) gerado(s); alertas {Criados} criado(s), {Resolvidos} resolvido(s)",
            gerados, r.Criados, r.Resolvidos);
    }
}

/// <summary>A cada 10 min: puxa alterações das agendas Google conectadas (complementa o webhook).</summary>
public sealed class SincronizacaoGoogleJob(IServiceScopeFactory e, InovabensOpcoes o, ILogger<SincronizacaoGoogleJob> log) : Rotina(e, o, log)
{
    protected override string Nome => "sincronização Google";
    protected override bool Habilitada => Opcoes.GoogleConfigurado;
    protected internal override DateTime ProximaBR(DateTime agoraBR) => ACada(agoraBR, 10);

    protected override Task Rodar(IServiceProvider sp, CancellationToken ct) => sp.GetRequiredService<GoogleService>().SincronizarTodasAsync(ct);
}

/// <summary>Todo dia às 04:00: renova os canais de notificação (webhook) do Google que estão para expirar.</summary>
public sealed class RenovacaoCanaisGoogleJob(IServiceScopeFactory e, InovabensOpcoes o, ILogger<RenovacaoCanaisGoogleJob> log) : Rotina(e, o, log)
{
    protected override string Nome => "renovação de canais Google";
    protected override bool Habilitada => Opcoes.GoogleConfigurado;
    protected internal override DateTime ProximaBR(DateTime agoraBR) => Diaria(agoraBR, 4);

    protected override Task Rodar(IServiceProvider sp, CancellationToken ct) => sp.GetRequiredService<GoogleService>().RenovarCanaisAsync(ct);
}

/// <summary>A cada minuto: envia ao Magnific as fotos novas e recolhe as que ficaram prontas (só com MAGNIFIC_API_KEY).</summary>
public sealed class MelhoriaFotosJob(IServiceScopeFactory e, InovabensOpcoes o, ILogger<MelhoriaFotosJob> log) : Rotina(e, o, log)
{
    protected override string Nome => "melhoria de fotos (Magnific)";
    protected override bool Habilitada => Opcoes.MagnificApiKey is not null;
    protected internal override DateTime ProximaBR(DateTime agoraBR) => ACada(agoraBR, 1);

    protected override async Task Rodar(IServiceProvider sp, CancellationToken ct)
    {
        var n = await sp.GetRequiredService<MelhoriaFotosService>().ProcessarAsync(ct);
        if (n > 0) Log.LogInformation("Magnific: {Qtd} foto(s) melhorada(s)", n);
    }
}
