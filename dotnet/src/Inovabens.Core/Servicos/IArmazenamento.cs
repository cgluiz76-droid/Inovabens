namespace Inovabens.Core.Servicos;

/// <summary>Conteúdo de um arquivo lido do armazenamento, com o tipo gravado no envio.</summary>
public sealed record ConteudoArquivo(byte[] Dados, string Tipo);

/// <summary>
/// Armazenamento de fotos e documentos organizado em pastas (Google Drive). Arquivos e pastas são identificados
/// pelo id que o armazenamento devolve; renomear ou mover no Drive não quebra o vínculo com o sistema.
/// </summary>
public interface IArmazenamento
{
    bool Habilitado { get; }

    /// <summary>Id da pasta com este nome dentro de <paramref name="paiId"/> (null = raiz do drive), ou null se não existir.</summary>
    Task<string?> BuscarPastaAsync(string nome, string? paiId, CancellationToken ct = default);

    Task<string> CriarPastaAsync(string nome, string? paiId, CancellationToken ct = default);

    Task RenomearAsync(string id, string nome, CancellationToken ct = default);

    /// <summary>Grava o arquivo na pasta e devolve o id dele.</summary>
    Task<string> EnviarAsync(string pastaId, string nome, Stream conteudo, string contentType, CancellationToken ct = default);

    /// <summary>Pasta onde o arquivo está, ou null se ele não existir mais.</summary>
    Task<string?> PastaDoArquivoAsync(string id, CancellationToken ct = default);

    /// <summary>Conteúdo do arquivo, ou null se o armazenamento estiver desligado ou a leitura falhar.</summary>
    Task<ConteudoArquivo?> BaixarAsync(string? id, CancellationToken ct = default);

    /// <summary>Manda para a lixeira (recuperável por 30 dias no Drive). Falhas só são registradas no log.</summary>
    Task RemoverAsync(string id);

    /// <summary>Endereço para abrir a pasta no navegador.</summary>
    string? LinkPasta(string? pastaId);
}
