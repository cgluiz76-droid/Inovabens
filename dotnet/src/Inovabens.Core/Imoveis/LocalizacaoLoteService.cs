using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Microsoft.EntityFrameworkCore;

namespace Inovabens.Core.Imoveis;

/// <summary>Resultado de uma rodada: quantos foram localizados e em que nível (endereço, rua ou bairro).</summary>
public sealed record ResultadoLocalizacao(int Pendentes, int PeloEndereco, int PelaRua, int PeloBairro, IReadOnlyList<string> NaoEncontrados);

/// <summary>
/// Preenche as coordenadas dos imóveis que ainda não têm (ex.: os importados do painel do Imobibrasil), para que
/// apareçam no mapa. Tenta o endereço completo; se não achar, só a rua na cidade; por fim, o bairro na cidade.
/// No OpenStreetMap respeita o limite de uma consulta por segundo.
/// </summary>
public sealed class LocalizacaoLoteService(IDbContextFactory<InovabensDb> fabrica, GeocodificacaoService geo)
{
    public async Task<ResultadoLocalizacao> LocalizarPendentesAsync(Action<string>? progresso = null, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var pendentes = await db.Imoveis.AsNoTracking()
            .Where(i => i.ExcluidoEm == null && (i.Latitude == null || i.Longitude == null))
            .OrderBy(i => i.Codigo)
            .Select(i => new { i.Id, i.Codigo, i.Logradouro, i.Numero, i.Bairro, i.Cidade, i.Uf, i.Cep })
            .ToListAsync(ct);

        var pausa = geo.Provedor == "OpenStreetMap" ? TimeSpan.FromSeconds(1.1) : TimeSpan.Zero;
        int endereco = 0, rua = 0, bairro = 0;
        var falhas = new List<string>();
        foreach (var i in pendentes)
        {
            ct.ThrowIfCancellationRequested();
            (double, double)? ponto = null;
            var nivel = "";
            foreach (var (consulta, n) in Tentativas(i.Logradouro, i.Numero, i.Bairro, i.Cidade, i.Uf, i.Cep))
            {
                if (pausa > TimeSpan.Zero) await Task.Delay(pausa, ct);
                if (await geo.LocalizarAsync(consulta, ct) is { } c) { ponto = c; nivel = n; break; }
            }
            if (ponto is not { } p)
            {
                falhas.Add(i.Codigo);
                progresso?.Invoke($"{i.Codigo}: não encontrado");
                continue;
            }
            await db.Imoveis.Where(x => x.Id == i.Id && x.Latitude == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Latitude, p.Item1).SetProperty(x => x.Longitude, p.Item2), ct);
            switch (nivel) { case "endereço": endereco++; break; case "rua": rua++; break; default: bairro++; break; }
            progresso?.Invoke($"{i.Codigo}: localizado {(nivel == "rua" ? "pela" : "pelo")} {nivel}");
        }
        return new ResultadoLocalizacao(pendentes.Count, endereco, rua, bairro, falhas);
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
