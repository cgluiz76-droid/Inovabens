using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Microsoft.EntityFrameworkCore;

namespace Inovabens.Core.Imoveis;

/// <summary>Resultado de uma rodada: quantos foram localizados e em que nível (endereço, rua ou bairro).</summary>
public sealed record ResultadoLocalizacao(int Pendentes, int PeloEndereco, int PelaRua, int PeloBairro, IReadOnlyList<string> NaoEncontrados);

/// <summary>Resultado do refinamento: quantos ganharam o ponto exato e quais ficaram como estavam.</summary>
public sealed record ResultadoRefino(int Total, int Refinados, IReadOnlyList<string> Mantidos);

/// <summary>
/// Preenche as coordenadas dos imóveis que ainda não têm (ex.: os importados do painel do Imobibrasil), para que
/// apareçam no mapa. Tenta o endereço completo; se não achar, só a rua na cidade; por fim, o bairro na cidade.
/// Usa a Geocoding API do Google (sem a chave, não localiza nada).
/// </summary>
public sealed class LocalizacaoLoteService(IDbContextFactory<InovabensDb> fabrica, GeocodificacaoService geo)
{
    private static readonly TimeSpan Pausa = TimeSpan.FromMilliseconds(100);

    public async Task<ResultadoLocalizacao> LocalizarPendentesAsync(Action<string>? progresso = null, CancellationToken ct = default)
    {
        if (!geo.Configurado) throw new InvalidOperationException("Falta a chave do Google Maps (Administração → Configurações → Google Maps).");
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var pendentes = await db.Imoveis.AsNoTracking()
            .Where(i => i.ExcluidoEm == null && (i.Latitude == null || i.Longitude == null))
            .OrderBy(i => i.Codigo)
            .Select(i => new { i.Id, i.Codigo, i.Logradouro, i.Numero, i.Bairro, i.Cidade, i.Uf, i.Cep })
            .ToListAsync(ct);

        int endereco = 0, rua = 0, bairro = 0;
        var falhas = new List<string>();
        foreach (var i in pendentes)
        {
            ct.ThrowIfCancellationRequested();
            Localizacao? ponto = null;
            var nivel = "";
            foreach (var (consulta, n) in Tentativas(i.Logradouro, i.Numero, i.Bairro, i.Cidade, i.Uf, i.Cep))
            {
                await Task.Delay(Pausa, ct);
                // o Google sempre devolve algo (até o centro da cidade): no endereço e na rua só vale ponto na rua
                if (await geo.LocalizarComPrecisaoAsync(consulta, ct) is { } c && (n == "bairro" || c.Precisao != PrecisaoLocal.Aproximada))
                {
                    ponto = c; nivel = n; break;
                }
            }
            if (ponto is not { } p)
            {
                falhas.Add(i.Codigo);
                progresso?.Invoke($"{i.Codigo}: não encontrado");
                continue;
            }
            await db.Imoveis.Where(x => x.Id == i.Id && x.Latitude == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Latitude, p.Latitude).SetProperty(x => x.Longitude, p.Longitude), ct);
            switch (nivel) { case "endereço": endereco++; break; case "rua": rua++; break; default: bairro++; break; }
            progresso?.Invoke($"{i.Codigo}: localizado {(nivel == "rua" ? "pela" : "pelo")} {nivel}");
        }
        return new ResultadoLocalizacao(pendentes.Count, endereco, rua, bairro, falhas);
    }

    /// <summary>
    /// Refaz a localização dos imóveis indicados (ex.: os que ficaram só na rua ou no bairro) pelo endereço completo e
    /// só troca as coordenadas quando o ponto novo é exato (no número). Os demais ficam como estão.
    /// </summary>
    public async Task<ResultadoRefino> RefinarAsync(IReadOnlyCollection<string> codigos, Action<string>? progresso = null, CancellationToken ct = default)
    {
        if (!geo.Configurado) throw new InvalidOperationException("Falta a chave do Google Maps (Administração → Configurações → Google Maps).");
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var alvos = await db.Imoveis.AsNoTracking()
            .Where(i => i.ExcluidoEm == null && codigos.Contains(i.Codigo))
            .OrderBy(i => i.Codigo)
            .Select(i => new { i.Id, i.Codigo, i.Logradouro, i.Numero, i.Bairro, i.Cidade, i.Uf, i.Cep })
            .ToListAsync(ct);

        var refinados = 0;
        var mantidos = new List<string>();
        foreach (var i in alvos)
        {
            ct.ThrowIfCancellationRequested();
            var consulta = Tentativas(i.Logradouro, i.Numero, i.Bairro, i.Cidade, i.Uf, i.Cep).FirstOrDefault(t => t.Nivel == "endereço").Consulta;
            Localizacao? ponto = null;
            if (consulta is not null)
            {
                await Task.Delay(Pausa, ct);
                ponto = await geo.LocalizarComPrecisaoAsync(consulta, ct);
            }
            if (ponto is not { Precisao: PrecisaoLocal.Exata } p)
            {
                mantidos.Add(i.Codigo);
                progresso?.Invoke($"{i.Codigo}: mantido ({(ponto is null ? "não encontrado" : $"só {ponto.Precisao.ToString().ToLowerInvariant()}")})");
                continue;
            }
            await db.Imoveis.Where(x => x.Id == i.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Latitude, p.Latitude).SetProperty(x => x.Longitude, p.Longitude), ct);
            refinados++;
            progresso?.Invoke($"{i.Codigo}: refinado no número");
        }
        mantidos.AddRange(codigos.Except(alvos.Select(a => a.Codigo), StringComparer.OrdinalIgnoreCase));
        return new ResultadoRefino(codigos.Count, refinados, mantidos);
    }

    /// <summary>Consultas da mais precisa para a mais ampla; número "0" ou "s/n" conta como sem número.</summary>
    public static IEnumerable<(string Consulta, string Nivel)> Tentativas(string? logradouro, string? numero, string? bairro, string? cidade, string? uf, string? cep)
    {
        var num = Texto.Limpo(numero) is { } n && n.Any(char.IsDigit) && n.Trim('0', ' ') != "" ? n : null;
        var vistas = new HashSet<string>();
        IEnumerable<(string?, string)> todas =
        [
            (RegrasImovel.EnderecoParaMapa(logradouro, num, bairro, cidade, uf, cep), "endereço"),
            (Texto.Limpo(logradouro) is null || Texto.Limpo(cidade) is null ? null : RegrasImovel.EnderecoParaMapa(logradouro, num, null, cidade, uf, null), "rua"),
            (Texto.Limpo(bairro) is null || Texto.Limpo(cidade) is null ? null : RegrasImovel.EnderecoParaMapa(null, null, bairro, cidade, uf, null), "bairro"),
        ];
        foreach (var (consulta, nivel) in todas)
            if (consulta is not null && vistas.Add(consulta)) yield return (consulta, nivel);
    }
}
