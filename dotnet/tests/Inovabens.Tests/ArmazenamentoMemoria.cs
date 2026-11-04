using System.Collections.Concurrent;
using Inovabens.Core.Servicos;

namespace Inovabens.Tests;

/// <summary>Google Drive de mentira, em memória: pastas e arquivos com id, pai, nome e lixeira.</summary>
public sealed class ArmazenamentoMemoria : IArmazenamento
{
    public sealed record Item(string Id, string Nome, string? Pai, bool Pasta, byte[] Dados, string Tipo)
    {
        public bool NaLixeira { get; set; }
    }

    public const string Raiz = "drive-raiz";
    public ConcurrentDictionary<string, Item> Itens { get; } = new();

    public bool Habilitado => true;

    private static string NovoId() => Guid.NewGuid().ToString("N");

    public IEnumerable<Item> Filhos(string? pai) => Itens.Values.Where(i => i.Pai == (pai ?? Raiz) && !i.NaLixeira);

    public Task<string?> BuscarPastaAsync(string nome, string? paiId, CancellationToken ct = default) =>
        Task.FromResult(Filhos(paiId).FirstOrDefault(i => i.Pasta && i.Nome == nome)?.Id);

    public Task<string> CriarPastaAsync(string nome, string? paiId, CancellationToken ct = default)
    {
        var id = NovoId();
        Itens[id] = new Item(id, nome, paiId ?? Raiz, Pasta: true, [], "application/vnd.google-apps.folder");
        return Task.FromResult(id);
    }

    public Task RenomearAsync(string id, string nome, CancellationToken ct = default)
    {
        Itens[id] = Itens[id] with { Nome = nome };
        return Task.CompletedTask;
    }

    public async Task<string> EnviarAsync(string pastaId, string nome, Stream conteudo, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await conteudo.CopyToAsync(ms, ct);
        var id = NovoId();
        Itens[id] = new Item(id, nome, pastaId, Pasta: false, ms.ToArray(), contentType);
        return id;
    }

    public Task<string?> PastaDoArquivoAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(Itens.TryGetValue(id, out var i) ? i.Pai : null);

    public Task<ConteudoArquivo?> BaixarAsync(string? id, CancellationToken ct = default) =>
        Task.FromResult(id is not null && Itens.TryGetValue(id, out var i) && !i.NaLixeira ? new ConteudoArquivo(i.Dados, i.Tipo) : null);

    public Task RemoverAsync(string id)
    {
        if (Itens.TryGetValue(id, out var i)) i.NaLixeira = true;
        return Task.CompletedTask;
    }

    public string? LinkPasta(string? pastaId) => pastaId is null ? null : $"https://drive.exemplo/{pastaId}";
}
