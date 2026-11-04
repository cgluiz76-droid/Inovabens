using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using DriveArquivo = Google.Apis.Drive.v3.Data.File;

namespace Inovabens.Core.Servicos;

/// <summary>
/// Armazenamento num drive compartilhado do Google Workspace, acessado por uma conta de serviço que é
/// "Administrador de conteúdo" do drive (GOOGLE_DRIVE_ID + GOOGLE_DRIVE_CREDENCIAIS ou GOOGLE_DRIVE_CREDENCIAIS_JSON).
/// Conta de serviço não tem espaço próprio no Drive: só grava dentro de drive compartilhado.
/// Desabilitado se faltar configuração.
/// </summary>
public sealed class GoogleDriveArmazenamento : IArmazenamento, IDisposable
{
    private const string TipoPasta = "application/vnd.google-apps.folder";

    private readonly DriveService? _drive;
    private readonly string _driveId = "";
    private readonly ILogger<GoogleDriveArmazenamento> _log;

    public GoogleDriveArmazenamento(InovabensOpcoes op, ILogger<GoogleDriveArmazenamento> log)
    {
        _log = log;
        var json = op.GoogleDriveCredenciaisJson
            ?? (op.GoogleDriveCredenciais is { } caminho && File.Exists(caminho) ? File.ReadAllText(caminho) : null);
        if (op.GoogleDriveId is null || json is null)
        {
            log.LogWarning("Google Drive não configurado (GOOGLE_DRIVE_ID e GOOGLE_DRIVE_CREDENCIAIS): envio de fotos/documentos desabilitado.");
            return;
        }
        _driveId = op.GoogleDriveId;
        var credencial = CredentialFactory.FromJson<ServiceAccountCredential>(json)
            .ToGoogleCredential()
            .CreateScoped(DriveService.Scope.Drive);
        _drive = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credencial,
            ApplicationName = "Inovabens",
        });
    }

    public bool Habilitado => _drive is not null;

    private DriveService Drive => _drive ?? throw new InvalidOperationException("Armazenamento desabilitado.");

    public async Task<string?> BuscarPastaAsync(string nome, string? paiId, CancellationToken ct = default)
    {
        var req = Drive.Files.List();
        req.Q = $"name = '{Escapar(nome)}' and '{Escapar(paiId ?? _driveId)}' in parents and mimeType = '{TipoPasta}' and trashed = false";
        req.Corpora = "drive";
        req.DriveId = _driveId;
        req.IncludeItemsFromAllDrives = true;
        req.SupportsAllDrives = true;
        req.Fields = "files(id)";
        req.PageSize = 1;
        var r = await req.ExecuteAsync(ct);
        return r.Files.FirstOrDefault()?.Id;
    }

    public async Task<string> CriarPastaAsync(string nome, string? paiId, CancellationToken ct = default)
    {
        var req = Drive.Files.Create(new DriveArquivo { Name = nome, MimeType = TipoPasta, Parents = [paiId ?? _driveId] });
        req.SupportsAllDrives = true;
        req.Fields = "id";
        return (await req.ExecuteAsync(ct)).Id;
    }

    public async Task RenomearAsync(string id, string nome, CancellationToken ct = default)
    {
        var req = Drive.Files.Update(new DriveArquivo { Name = nome }, id);
        req.SupportsAllDrives = true;
        req.Fields = "id";
        await req.ExecuteAsync(ct);
    }

    public async Task<string> EnviarAsync(string pastaId, string nome, Stream conteudo, string contentType, CancellationToken ct = default)
    {
        var req = Drive.Files.Create(new DriveArquivo { Name = nome, Parents = [pastaId], MimeType = contentType }, conteudo, contentType);
        req.SupportsAllDrives = true;
        req.Fields = "id";
        var progresso = await req.UploadAsync(ct);
        if (progresso.Exception is not null) throw progresso.Exception;
        return req.ResponseBody.Id;
    }

    public async Task<string?> PastaDoArquivoAsync(string id, CancellationToken ct = default)
    {
        try
        {
            var req = Drive.Files.Get(id);
            req.SupportsAllDrives = true;
            req.Fields = "parents";
            return (await req.ExecuteAsync(ct)).Parents?.FirstOrDefault();
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ConteudoArquivo?> BaixarAsync(string? id, CancellationToken ct = default)
    {
        if (_drive is null || string.IsNullOrEmpty(id)) return null;
        try
        {
            var meta = _drive.Files.Get(id);
            meta.SupportsAllDrives = true;
            meta.Fields = "mimeType";
            var tipo = (await meta.ExecuteAsync(ct)).MimeType ?? "application/octet-stream";

            var req = _drive.Files.Get(id);
            req.SupportsAllDrives = true;
            using var ms = new MemoryStream();
            var progresso = await req.DownloadAsync(ms, ct);
            if (progresso.Exception is not null) throw progresso.Exception;
            return new ConteudoArquivo(ms.ToArray(), tipo);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning("Falha ao baixar {Arquivo} do Drive: {Erro}", id, e.Message);
            return null;
        }
    }

    public async Task RemoverAsync(string id)
    {
        if (_drive is null) return;
        try
        {
            // "Administrador de conteúdo" não exclui de vez em drive compartilhado: vai para a lixeira (30 dias)
            var req = _drive.Files.Update(new DriveArquivo { Trashed = true }, id);
            req.SupportsAllDrives = true;
            req.Fields = "id";
            await req.ExecuteAsync();
        }
        catch (Exception e)
        {
            _log.LogWarning("Falha ao mandar {Arquivo} para a lixeira do Drive: {Erro}", id, e.Message);
        }
    }

    public string? LinkPasta(string? pastaId) =>
        string.IsNullOrEmpty(pastaId) ? null : $"https://drive.google.com/drive/folders/{Uri.EscapeDataString(pastaId)}";

    /// <summary>Texto entre aspas simples na consulta do Drive: escapa \ e '.</summary>
    internal static string Escapar(string s) => s.Replace(@"\", @"\\").Replace("'", @"\'");

    public void Dispose() => _drive?.Dispose();
}
