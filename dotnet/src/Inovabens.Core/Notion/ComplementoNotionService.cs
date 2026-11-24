using System.Text.Json;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Notion;

/// <summary>Memorando preparado fora do sistema a partir de uma página do Notion.</summary>
public sealed record ComplementoMemorando(string NotionId, string Imovel, string Titulo, string Corpo, DateTime CriadoEm);

/// <summary>Arquivo baixado do Notion (caminho relativo à pasta do pacote) a guardar em "05 - Documentos" do imóvel.</summary>
public sealed record ComplementoArquivo(string NotionId, string Imovel, string Arquivo, string Nome, string ContentType, string Origem);

public sealed record ComplementoNotion(List<ComplementoMemorando> Memorandos, List<ComplementoArquivo> Arquivos);

/// <summary>
/// Complemento da importação do Notion, feito antes de encerrar a assinatura: análises comerciais, ativos e status
/// comercial (fora das bases que o importador lê) viram memorandos, e os arquivos vão para o Drive do imóvel.
/// O pacote (manifesto.json + arquivos) é montado fora do sistema, com o Notion em só leitura. Cada item fica em
/// notion_importacoes: rodar de novo não duplica.
/// </summary>
public sealed class ComplementoNotionService(IDbContextFactory<InovabensDb> fabrica, ImoveisService imoveis, AuditService audit)
{
    private const string RegMemorando = "MEMORANDO", RegArquivo = "ARQUIVO";

    public static ComplementoNotion Ler(string pasta) =>
        JsonSerializer.Deserialize<ComplementoNotion>(File.ReadAllText(Path.Combine(pasta, "manifesto.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidOperationException("Manifesto vazio.");

    public async Task<List<string>> ImportarAsync(UsuarioAtual u, string pasta, bool simular, Action<string>? progresso = null,
        CancellationToken ct = default)
    {
        u.Exigir(P.ImoveisEditar);
        var pacote = Ler(pasta);
        var log = new List<string>();
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var mapa = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in await db.Imoveis.Where(i => i.ExcluidoEm == null).Select(i => new { i.Id, i.Codigo, i.Referencia, i.CodigoAntigo }).ToListAsync(ct))
        {
            mapa[i.Codigo] = i.Id;
            if (i.Referencia is { Length: > 0 } re) mapa.TryAdd(re, i.Id);
            if (i.CodigoAntigo is { Length: > 0 } ant) mapa.TryAdd(ant, i.Id);
        }
        var feitos = (await db.NotionImportacoes.Select(r => new { r.NotionId, r.Tipo }).ToListAsync(ct))
            .Select(r => (r.NotionId, r.Tipo)).ToHashSet();

        // confere tudo antes de gravar: imóvel inexistente ou arquivo faltando param o processo sem gravar nada
        var faltam = pacote.Memorandos.Select(m => m.Imovel).Concat(pacote.Arquivos.Select(a => a.Imovel)).Distinct()
            .Where(r => !mapa.ContainsKey(r)).ToList();
        if (faltam.Count > 0) throw new InvalidOperationException("Imóveis não encontrados: " + string.Join(", ", faltam));
        var semArquivo = pacote.Arquivos.Where(a => !File.Exists(Path.Combine(pasta, a.Arquivo))).Select(a => a.Arquivo).ToList();
        if (semArquivo.Count > 0) throw new InvalidOperationException("Arquivos ausentes no pacote: " + string.Join(", ", semArquivo));

        var agora = DateTime.UtcNow;
        foreach (var m in pacote.Memorandos)
        {
            if (feitos.Contains((m.NotionId, RegMemorando))) { log.Add($"já importado: {m.Imovel} · {m.Titulo}"); continue; }
            log.Add($"memorando: {m.Imovel} · {m.Titulo} ({m.Corpo.Length} caracteres)");
            if (simular) continue;
            var memo = new Memorando
            {
                Id = Guid.NewGuid(), ImovelId = mapa[m.Imovel], AutorId = u.Id, Tipo = Dominio.TipoMemorando.MEMORANDO,
                Titulo = m.Titulo.Length <= 200 ? m.Titulo : m.Titulo[..199] + "…", Corpo = m.Corpo, Fixado = false,
                CriadoEm = m.CriadoEm.ToUniversalTime(), AtualizadoEm = agora,
            };
            db.Memorandos.Add(memo);
            audit.Registrar(db, u.Id, "memorandos", memo.Id, "CRIAR", depois: memo);
            db.NotionImportacoes.Add(new NotionImportacao { NotionId = m.NotionId, Tipo = RegMemorando, EntidadeId = memo.Id, ImportadoEm = agora });
        }
        if (!simular) await db.SaveChangesAsync(ct);

        foreach (var a in pacote.Arquivos)
        {
            if (feitos.Contains((a.NotionId, RegArquivo))) { log.Add($"já copiado: {a.Imovel} · {a.Nome}"); continue; }
            log.Add($"arquivo: {a.Imovel} · {a.Nome}");
            if (simular) continue;
            progresso?.Invoke($"Copiando {a.Nome}...");
            await using (var arq = File.OpenRead(Path.Combine(pasta, a.Arquivo)))
                await imoveis.EnviarMidiaAsync(u, mapa[a.Imovel], arq, a.Nome, a.ContentType, SubpastasDrive.Documentos,
                    legenda: $"Do Notion: {a.Origem}", ct: ct, melhorar: false);
            db.NotionImportacoes.Add(new NotionImportacao { NotionId = a.NotionId, Tipo = RegArquivo, EntidadeId = mapa[a.Imovel], ImportadoEm = DateTime.UtcNow });
            await db.SaveChangesAsync(ct); // um por um: se cair no meio, rodar de novo continua de onde parou
        }
        return log;
    }
}
