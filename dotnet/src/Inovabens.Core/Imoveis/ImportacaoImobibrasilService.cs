using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inovabens.Core.Imoveis;

/// <summary>Resultado de uma importação: um item por anúncio lido.</summary>
public sealed record ItemImportado(long ImobibrasilId, string? Referencia, string Codigo, bool Novo, int FotosNovas, string? Aviso);

/// <summary>
/// Traz os imóveis ativos do site atual (feito no Imobibrasil) para o sistema, lendo as páginas públicas.
/// Reimportar atualiza o cadastro já trazido (pelo id do anúncio) e só envia as fotos que faltam; não duplica.
/// As fotos vão para "04 - Fotos e Vídeos" já marcadas como públicas (o R2 recebe a cópia pelo PublicacaoFotosJob)
/// e não passam pelo Magnific, para não gastar créditos em lote.
/// </summary>
public sealed class ImportacaoImobibrasilService(
    IDbContextFactory<InovabensDb> fabrica, ImoveisService imoveis, GeocodificacaoService geo, ILogger<ImportacaoImobibrasilService> log)
{
    private static readonly HttpClient Http = CriarHttp();

    private static HttpClient CriarHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Inovabens-Importacao/1.0");
        return h;
    }

    /// <summary>Intervalo entre requisições ao site (não sobrecarregar o servidor do Imobibrasil).</summary>
    public TimeSpan Pausa { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Tipo do Imobibrasil → tipo cadastrado no sistema (tipos_imovel).</summary>
    public static string TipoSistema(string tipoOriginal) => tipoOriginal.Trim().ToLowerInvariant() switch
    {
        "casa" or "sobrado" => "casa",
        "casa em condomínio" => "casa em condomínio",
        "apartamento" or "cobertura" or "flat" or "kitnet" or "studio" => "apartamento",
        "terreno" or "lote" => "terreno",
        "área" => "área",
        "barracão" or "galpão" => "galpão",
        "sítio" or "fazenda" or "rancho" => "fazenda/sítio",
        "chácara" => "Chácara",
        "sala comercial" or "sala" => "sala comercial",
        "loja" or "ponto comercial" => "loja",
        "comercial" or "prédio comercial" => "comercial",
        _ => "outro",
    };

    /// <summary>Lista os anúncios publicados (venda e locação), página por página, até não aparecer anúncio novo.</summary>
    public async Task<IReadOnlyList<(long Id, string Url)>> ListarAsync(string site, CancellationToken ct = default)
    {
        site = site.TrimEnd('/');
        var vistos = new Dictionary<long, string>();
        foreach (var finalidade in new[] { "venda", "locacao" })
        {
            for (var pag = 1; pag <= 50; pag++)
            {
                var html = LeitorImobibrasil.Decodificar(await Http.GetByteArrayAsync($"{site}/imovel/{finalidade}/?pag={pag}", ct));
                var novos = LeitorImobibrasil.LinksDaLista(html).Where(l => vistos.TryAdd(l.Id, site + l.Caminho)).Count();
                if (novos == 0) break;
                await Task.Delay(Pausa, ct);
            }
        }
        return [.. vistos.Select(kv => (kv.Key, kv.Value))];
    }

    public async Task<IReadOnlyList<ItemImportado>> ImportarAsync(UsuarioAtual u, string site, bool comFotos = true,
        Action<string>? progresso = null, CancellationToken ct = default)
    {
        var lista = await ListarAsync(site, ct);
        progresso?.Invoke($"{lista.Count} anúncios encontrados em {site}.");
        var resultado = new List<ItemImportado>();
        foreach (var (id, url) in lista)
        {
            try
            {
                var html = LeitorImobibrasil.Decodificar(await Http.GetByteArrayAsync(url, ct));
                var anuncio = LeitorImobibrasil.LerFicha(id, url, html);
                var item = await ImportarAnuncioAsync(u, anuncio, comFotos, ct);
                resultado.Add(item);
                progresso?.Invoke($"{item.Codigo} ← {anuncio.Referencia} ({id}){(item.Novo ? " novo" : " atualizado")}, {item.FotosNovas} fotos novas{(item.Aviso is null ? "" : " · " + item.Aviso)}");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Falha ao importar o anúncio {Id} do Imobibrasil", id);
                resultado.Add(new ItemImportado(id, null, "—", false, 0, "falhou: " + e.Message));
                progresso?.Invoke($"anúncio {id}: falhou ({e.Message})");
            }
            await Task.Delay(Pausa, ct);
        }
        return resultado;
    }

    public async Task<ItemImportado> ImportarAnuncioAsync(UsuarioAtual u, AnuncioImobibrasil a, bool comFotos, CancellationToken ct = default)
    {
        Guid? existente;
        await using (var db = await fabrica.CreateDbContextAsync(ct))
            existente = await db.Imoveis.Where(i => i.ImobibrasilId == a.Id && i.ExcluidoEm == null).Select(i => (Guid?)i.Id).FirstOrDefaultAsync(ct);

        var form = existente is { } idAtual ? ImovelForm.De(await CarregarAsync(idAtual, ct)) : new ImovelForm();
        Preencher(form, a, novo: existente is null);
        var avisos = new List<string>();
        if (form.Latitude is null && RegrasImovel.EnderecoParaMapa(form.Logradouro, form.Numero, form.Bairro, form.Cidade, form.Uf, form.Cep) is { } end
            && await geo.LocalizarAsync(end, ct) is { } c)
            (form.Latitude, form.Longitude) = c;
        if (form.Latitude is null) avisos.Add("sem coordenadas");

        Guid id;
        if (existente is { } e) { await imoveis.AtualizarAsync(u, e, form); id = e; }
        else id = await imoveis.CriarAsync(u, form);

        await using (var db = await fabrica.CreateDbContextAsync(ct))
            await db.Imoveis.Where(i => i.Id == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.ImobibrasilId, a.Id), ct);

        await imoveis.SalvarCaracteristicasAsync(u, id, [.. a.Caracteristicas.Select(x => new CaracteristicaEntrada { Grupo = x.Grupo, Chave = Corta(x.Item, 80)! })]);

        var fotosNovas = 0;
        if (comFotos)
        {
            HashSet<string> jaTem;
            await using (var db = await fabrica.CreateDbContextAsync(ct))
                jaTem = [.. await db.ImovelMidias.Where(m => m.ImovelId == id && m.NomeOriginal != null).Select(m => m.NomeOriginal!).ToListAsync(ct)];
            foreach (var f in a.Fotos.Take(RegrasImovel.MaximoFotos))
            {
                if (jaTem.Contains(f.NomeArquivo)) continue;
                try
                {
                    using var resp = await Http.GetAsync(f.Url, ct);
                    resp.EnsureSuccessStatusCode();
                    var tipo = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                    await using var corpo = new MemoryStream(await resp.Content.ReadAsByteArrayAsync(ct));
                    await imoveis.EnviarMidiaAsync(u, id, corpo, f.NomeArquivo, tipo, SubpastasDrive.FotosVideos,
                        publica: true, legenda: Corta(f.Legenda, 200), ct: ct, melhorar: false);
                    fotosNovas++;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    log.LogWarning("Foto {Url} do anúncio {Id} não baixada: {Erro}", f.Url, a.Id, ex.Message);
                    avisos.Add($"foto {f.NomeArquivo} não baixada");
                }
            }
            if (a.Fotos.Count > RegrasImovel.MaximoFotos) avisos.Add($"{a.Fotos.Count - RegrasImovel.MaximoFotos} fotos acima do limite ficaram de fora");
        }

        string codigo;
        await using (var db = await fabrica.CreateDbContextAsync(ct))
            codigo = await db.Imoveis.Where(i => i.Id == id).Select(i => i.Codigo).SingleAsync(ct);
        if (a.OutrosCampos.Count > 0) avisos.Add("campos não reconhecidos: " + string.Join(", ", a.OutrosCampos.Keys));
        return new ItemImportado(a.Id, a.Referencia, codigo, existente is null, fotosNovas, avisos.Count == 0 ? null : string.Join("; ", avisos));
    }

    private async Task<Imovel> CarregarAsync(Guid id, CancellationToken ct)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        return await db.Imoveis.AsNoTracking().SingleAsync(i => i.Id == id, ct);
    }

    /// <summary>
    /// Copia o anúncio para o formulário. Campos só do sistema (matrícula, cartório, proprietários, observações
    /// internas já escritas, coordenadas corrigidas) ficam como estão numa reimportação.
    /// </summary>
    public static void Preencher(ImovelForm f, AnuncioImobibrasil a, bool novo)
    {
        f.Finalidade = a.Locacao ? FinalidadeImovel.LOCACAO : FinalidadeImovel.VENDA;
        if (novo) f.Situacao = SituacaoImovel.DISPONIVEL;
        f.Tipo = TipoSistema(a.TipoOriginal);
        f.Subtipo = string.Equals(f.Tipo, a.TipoOriginal, StringComparison.OrdinalIgnoreCase) ? null : Corta(a.TipoOriginal, 60);
        f.Titulo = Titulo(a);
        f.Referencia = Corta(a.Referencia, 20);
        if (novo) f.NomeDrive = NomeDrive(a);
        f.DescricaoPublica = a.Descricao;
        if (novo)
        {
            var nota = $"Importado do site Imobibrasil em {DatasBR.Hoje():dd/MM/yyyy}: {a.Url}";
            if (a.BairroOriginal is { } b && b != a.Bairro) nota += $"\nBairro no Imobibrasil: {b}";
            f.DescricaoInterna = nota;
        }
        f.Cep = a.Cep; f.Logradouro = Corta(a.Logradouro, 200); f.Numero = Corta(a.Numero, 20); f.Complemento = Corta(a.Complemento, 100);
        f.Bairro = Corta(a.Bairro, 100); f.Cidade = Corta(a.Cidade, 100); f.Uf = RegrasImovel.UfValida(a.Uf) ? a.Uf : null;
        f.Zona = Corta(a.Zona, 60); f.Regiao = Corta(a.Regiao, 100); f.PontoReferencia = Corta(a.PontoReferencia, 200);
        f.AreaTotalM2 = a.AreaTotalM2; f.AreaConstruidaM2 = a.AreaConstruidaM2; f.AreaUtilM2 = a.AreaUtilM2; f.AreaTerrenoM2 = a.AreaTerrenoM2;
        f.TerrenoFrenteM = a.TerrenoFrenteM; f.TerrenoFundoM = a.TerrenoFundoM; f.TerrenoEsquerdaM = a.TerrenoEsquerdaM; f.TerrenoDireitaM = a.TerrenoDireitaM;
        f.Pavimento = Corta(a.Pavimento, 40); f.AnoConstrucao = a.AnoConstrucao;
        f.Quartos = a.Quartos; f.Suites = a.Suites; f.Banheiros = a.Banheiros; f.Salas = a.Salas; f.Vagas = a.Vagas;
        if (a.Locacao) f.PrecoLocacao = a.Preco; else f.PrecoVenda = a.Preco;
        f.ValorCondominio = a.ValorCondominio; f.ValorIptu = a.ValorIptu;
        f.AceitaPermuta = a.AceitaPermuta; f.EmCondominio = a.EmCondominio;
        f.VideoUrl = a.VideoUrl ?? f.VideoUrl;
        f.DivulgarSite = true;
    }

    /// <summary>A chamada do anúncio vira o título; se vier toda em maiúsculas ou longa demais, monta um título pelos dados.</summary>
    public static string Titulo(AnuncioImobibrasil a)
    {
        // sem emoji/símbolos no começo ("📍Condomínio Damha III")
        var ch = a.Chamada is null ? null : new string([.. a.Chamada.SkipWhile(c => !char.IsLetterOrDigit(c))]).Trim();
        var letras = ch?.Where(char.IsLetter).ToList() ?? [];
        var maiusculas = letras.Count > 0 && letras.Count(char.IsUpper) > letras.Count * 0.6;
        if (ch is { Length: >= 25 and <= 150 } && !maiusculas) return ch;
        var onde = a.Bairro is { } b && !string.Equals(b, a.Cidade, StringComparison.OrdinalIgnoreCase) ? $"{b}, {a.Cidade}" : a.Cidade;
        return Corta($"{a.TipoOriginal} {(a.Locacao ? "para locação" : "à venda")} em {onde}", 200)!;
    }

    /// <summary>"CA0060 - Casa São Francisco": referência na frente para achar a pasta no Drive pelo código do anúncio.</summary>
    public static string NomeDrive(AnuncioImobibrasil a)
    {
        var onde = a.Bairro ?? a.Cidade ?? "";
        var nome = $"{a.Referencia ?? a.Id.ToString()} - {a.TipoOriginal} {onde}".Trim();
        nome = new string([.. nome.Select(ch => RegrasImovel.CaracteresProibidosPasta.Contains(ch) ? '-' : ch)]);
        return RegrasImovel.NomePasta(Corta(nome, 120))!;
    }

    private static string? Corta(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max].TrimEnd();
}
