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

    private readonly InovabensOpcoes _op;
    private readonly ILogger<GoogleDriveArmazenamento> _log;
    private readonly Lock _trava = new();
    // cliente montado com as integrações em vigor; refeito quando a configuração é salva na tela
    private (Integracoes? Fonte, DriveService? Drive, string DriveId) _atual;

    public GoogleDriveArmazenamento(InovabensOpcoes op, ILogger<GoogleDriveArmazenamento> log)
    {
        _op = op;
        _log = log;
        if (Atual().Drive is null)
            log.LogWarning("Google Drive não configurado (GOOGLE_DRIVE_ID e GOOGLE_DRIVE_CREDENCIAIS): envio de fotos/documentos desabilitado.");
    }

    private (Integracoes? Fonte, DriveService? Drive, string DriveId) Atual()
    {
        var fonte = _op.Integracoes;
        var atual = _atual;
        if (ReferenceEquals(atual.Fonte, fonte)) return atual;
        lock (_trava)
        {
            if (ReferenceEquals(_atual.Fonte, fonte)) return _atual;
            DriveService? drive = null;
            if (fonte.GoogleDriveId is not null && fonte.GoogleDriveCredenciaisJson is { } json)
            {
                try
                {
                    var credencial = CredentialFactory.FromJson<ServiceAccountCredential>(json)
                        .ToGoogleCredential()
                        .CreateScoped(DriveService.Scope.Drive);
                    drive = new DriveService(new BaseClientService.Initializer
                    {
                        HttpClientInitializer = credencial,
                        ApplicationName = "Inovabens",
                    });
                }
                catch (Exception e)
                {
                    _log.LogWarning("Chave da conta de serviço do Google Drive inválida: {Erro}", e.Message);
                }
            }
            // o cliente anterior não é descartado: pode haver envio em andamento com ele
            _atual = (fonte, drive, fonte.GoogleDriveId ?? "");
            return _atual;
        }
    }

    public bool Habilitado => Atual().Drive is not null;

    private DriveService Drive => Atual().Drive ?? throw new InvalidOperationException("Armazenamento desabilitado.");

    private string DriveId => Atual().DriveId;

    public async Task<string?> BuscarPastaAsync(string nome, string? paiId, CancellationToken ct = default)
    {
        var req = Drive.Files.List();
        req.Q = $"name = '{Escapar(nome)}' and '{Escapar(paiId ?? DriveId)}' in parents and mimeType = '{TipoPasta}' and trashed = false";
        req.Corpora = "drive";
        req.DriveId = DriveId;
        req.IncludeItemsFromAllDrives = true;
        req.SupportsAllDrives = true;
        req.Fields = "files(id)";
        req.PageSize = 1;
        var r = await req.ExecuteAsync(ct);
        return r.Files.FirstOrDefault()?.Id;
    }

    public async Task<string> CriarPastaAsync(string nome, string? paiId, CancellationToken ct = default)
    {
        var req = Drive.Files.Create(new DriveArquivo { Name = nome, MimeType = TipoPasta, Parents = [paiId ?? DriveId] });
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
        var drive = Atual().Drive;
        if (drive is null || string.IsNullOrEmpty(id)) return null;
        try
        {
            var meta = drive.Files.Get(id);
            meta.SupportsAllDrives = true;
            meta.Fields = "mimeType";
            var tipo = (await meta.ExecuteAsync(ct)).MimeType ?? "application/octet-stream";

            var req = drive.Files.Get(id);
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
        var drive = Atual().Drive;
        if (drive is null) return;
        try
        {
            // "Administrador de conteúdo" não exclui de vez em drive compartilhado: vai para a lixeira (30 dias)
            var req = drive.Files.Update(new DriveArquivo { Trashed = true }, id);
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

    public void Dispose() => _atual.Drive?.Dispose();
}
