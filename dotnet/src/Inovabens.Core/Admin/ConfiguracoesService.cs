using System.Text.Json;
using System.Text.RegularExpressions;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Admin;

public enum OrigemConfig { NaoDefinido, Padrao, Ambiente, Tela }

/// <summary>Como um campo aparece na tela. Segredos nunca voltam para a tela: só se estão definidos.</summary>
public sealed record ConfiguracaoItem(ItemConfiguracao Item, OrigemConfig Origem, string? Valor, DateTime? AtualizadoEm)
{
    public bool Definido => Origem != OrigemConfig.NaoDefinido;
}

public sealed record AdministradorPrincipal(Guid Id, string Nome, string Email);

/// <summary>
/// Integrações configuradas em Administração → Configurações. O valor salvo pela tela vale por cima da variável de
/// ambiente e passa a valer na hora (Drive, R2, Magnific, Google e rotinas leem <see cref="InovabensOpcoes.Integracoes"/>).
/// Com mais de uma instância, as outras só leem a mudança ao reiniciar.
/// </summary>
public sealed partial class ConfiguracoesService(
    IDbContextFactory<InovabensDb> fabrica, InovabensOpcoes op, AuditService audit,
    IArmazenamento armazenamento, IArmazenamentoPublico publico, MagnificCliente magnific)
{
    /// <summary>Chave interna (fora do catálogo): usuário que é o administrador principal.</summary>
    internal const string ChaveAdministrador = "sistema.admin_usuario_id";

    private const int TamanhoMaximo = 20_000;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Lê o que foi salvo pela tela e coloca em vigor (na subida da aplicação e depois de cada alteração).</summary>
    public async Task CarregarAsync(CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        op.Integracoes = op.IntegracoesAmbiente.Com(await SalvosAsync(db, ct));
    }

    public async Task<IReadOnlyList<ConfiguracaoItem>> ListarAsync(UsuarioAtual u)
    {
        u.Exigir(P.AdminConfig);
        await using var db = await fabrica.CreateDbContextAsync();
        var linhas = await db.Configuracoes.AsNoTracking().ToDictionaryAsync(c => c.Chave);
        var ambiente = op.IntegracoesAmbiente.Valores;
        return CatalogoConfiguracoes.Itens.Select(i =>
        {
            if (linhas.TryGetValue(i.Chave, out var l))
                return new ConfiguracaoItem(i, OrigemConfig.Tela, i.Secreto ? null : l.Valor, l.AtualizadoEm);
            if (ambiente.TryGetValue(i.Chave, out var v))
                return new ConfiguracaoItem(i, OrigemConfig.Ambiente, i.Secreto ? null : v, null);
            return new ConfiguracaoItem(i, i.Padrao is null ? OrigemConfig.NaoDefinido : OrigemConfig.Padrao, i.Padrao, null);
        }).ToList();
    }

    /// <summary>
    /// Salva os campos informados de um grupo: texto grava o valor (segredo, cifrado); vazio apaga o valor da tela,
    /// e volta a valer a variável de ambiente, se houver.
    /// </summary>
    public async Task SalvarAsync(UsuarioAtual u, string grupo, IReadOnlyDictionary<string, string> valores)
    {
        u.Exigir(P.AdminConfig);
        var itens = new List<(ItemConfiguracao Item, string Valor)>();
        foreach (var (chave, valor) in valores)
        {
            var item = CatalogoConfiguracoes.Item(chave);
            if (item is null || item.Grupo != grupo) throw new RegraNegocioException(chave, "Configuração desconhecida.");
            itens.Add((item, Normalizar(item, valor)));
        }
        if (itens.Count == 0) return;

        await using var db = await fabrica.CreateDbContextAsync();
        var existentes = await db.Configuracoes.Where(c => valores.Keys.Contains(c.Chave)).ToDictionaryAsync(c => c.Chave);
        var alteradas = new List<object>();
        foreach (var (item, valor) in itens)
        {
            existentes.TryGetValue(item.Chave, out var linha);
            if (valor.Length == 0)
            {
                if (linha is null) continue;
                db.Configuracoes.Remove(linha);
                alteradas.Add(new { chave = item.Chave, acao = "removida" });
                continue;
            }
            if (linha is null)
            {
                linha = new Configuracao { Chave = item.Chave };
                db.Configuracoes.Add(linha);
            }
            linha.Valor = item.Secreto ? Cripto.Cifrar(valor, op.TokenEncKey) : valor;
            linha.Cifrado = item.Secreto;
            linha.AtualizadoEm = DateTime.UtcNow;
            linha.AtualizadoPor = u.Id;
            // segredos não vão para a auditoria, só o fato de terem sido trocados
            alteradas.Add(item.Secreto ? new { chave = item.Chave, acao = "segredo alterado" } : new { chave = item.Chave, valor });
        }
        if (alteradas.Count == 0) return;
        audit.Registrar(db, u.Id, "configuracoes", null, "ATUALIZAR", depois: new { grupo, alteradas });
        await db.SaveChangesAsync();
        await CarregarAsync();
    }

    /// <summary>Confere a conexão com o serviço usando a configuração em vigor; devolve a mensagem de sucesso.</summary>
    public async Task<string> TestarAsync(UsuarioAtual u, string grupo, CancellationToken ct = default)
    {
        u.Exigir(P.AdminConfig);
        var i = op.Integracoes;
        try
        {
            switch (grupo)
            {
                case CatalogoConfiguracoes.GoogleDrive:
                    if (!armazenamento.Habilitado) throw Falta("Informe o ID do drive e a chave da conta de serviço.");
                    await armazenamento.BuscarPastaAsync("__inovabens_teste_conexao__", null, ct);
                    return "Conexão com o drive compartilhado funcionando.";
                case CatalogoConfiguracoes.R2:
                    if (publico is not R2ArmazenamentoPublico r2 || !r2.Habilitado) throw Falta("Informe endpoint, Access Key ID e Secret Access Key.");
                    var (bytes, arquivos) = await r2.UsoAsync(ct);
                    var uso = $" Uso: {arquivos} arquivo(s), {R2ArmazenamentoPublico.DescreverUso(bytes)}.";
                    return i.R2UrlPublica is null
                        ? "Acesso ao bucket funcionando." + uso + " Falta o endereço público para as fotos aparecerem no site."
                        : "Acesso ao bucket funcionando." + uso;
                case CatalogoConfiguracoes.Magnific:
                    if (!magnific.Configurado) throw Falta("Informe a chave da API.");
                    await magnific.TestarAsync(ct);
                    return "Chave do Magnific aceita.";
                case CatalogoConfiguracoes.GoogleMaps:
                    if (i.GoogleMapsApiKey is not { } chave) return "Sem chave: a localização usa o OpenStreetMap.";
                    using (var doc = JsonDocument.Parse(await Http.GetStringAsync(
                        "https://maps.googleapis.com/maps/api/geocode/json?address=" +
                        Uri.EscapeDataString("São José do Rio Preto, SP") + "&key=" + Uri.EscapeDataString(chave), ct)))
                    {
                        var status = doc.RootElement.GetProperty("status").GetString();
                        if (status == "OK") return "Chave do Google Maps aceita.";
                        var msg = doc.RootElement.TryGetProperty("error_message", out var e) ? e.GetString() : null;
                        throw new RegraNegocioException(grupo, $"O Google recusou a chave ({status}). {msg}".Trim());
                    }
                case CatalogoConfiguracoes.Imobibrasil:
                    var html = LeitorImobibrasil.Decodificar(await Http.GetByteArrayAsync($"{i.ImobibrasilSite}/imovel/venda/?pag=1", ct));
                    var anuncios = LeitorImobibrasil.LinksDaLista(html).Count;
                    return anuncios > 0
                        ? $"Site encontrado: {anuncios} anúncio(s) na primeira página."
                        : "O site respondeu, mas nenhum anúncio foi encontrado na primeira página. Confira o endereço.";
                default:
                    throw new RegraNegocioException(grupo, "Esta integração não tem teste de conexão.");
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or Google.GoogleApiException
                                      or Amazon.S3.AmazonS3Exception or Amazon.Runtime.AmazonServiceException
                                      or MagnificRecusouException or JsonException)
        {
            throw new RegraNegocioException(grupo, "Falha na conexão: " + (e.Message.Length > 300 ? e.Message[..300] : e.Message));
        }

        static RegraNegocioException Falta(string msg) => new("_", "Configuração incompleta: " + msg);
    }

    /* ------------------------- administrador principal ------------------------- */

    /// <summary>
    /// O administrador principal: o escolhido na tela; senão o criado na instalação (SEED_ADMIN_EMAIL); senão o
    /// administrador ativo mais antigo.
    /// </summary>
    public async Task<AdministradorPrincipal?> AdministradorAsync(UsuarioAtual u)
    {
        u.Exigir(P.AdminConfig);
        await using var db = await fabrica.CreateDbContextAsync();
        var usuario = await AdministradorDe(db);
        return usuario is null ? null : new AdministradorPrincipal(usuario.Id, usuario.Nome, usuario.Email);
    }

    /// <summary>Troca o e-mail (login) do administrador principal.</summary>
    public async Task DefinirEmailAdministradorAsync(UsuarioAtual u, string email)
    {
        u.Exigir(P.AdminConfig);
        u.Exigir(P.UsuariosGerir);
        email = email.Trim().ToLowerInvariant();
        if (!EmailValido(email)) throw new RegraNegocioException("email", "E-mail inválido.");
        await using var db = await fabrica.CreateDbContextAsync();
        var usuario = await AdministradorDe(db) ?? throw new NaoEncontradoException("Nenhum administrador ativo encontrado.");
        if (usuario.Email == email) return;
        if (await db.Usuarios.AnyAsync(x => x.Id != usuario.Id && x.Email.ToLower() == email))
            throw new RegraNegocioException("email", "Já existe um usuário com esse e-mail.");
        var antes = usuario.Email;
        usuario.Email = email;
        var linha = await db.Configuracoes.FindAsync(ChaveAdministrador);
        if (linha is null) db.Configuracoes.Add(linha = new Configuracao { Chave = ChaveAdministrador });
        linha.Valor = usuario.Id.ToString();
        linha.AtualizadoEm = DateTime.UtcNow;
        linha.AtualizadoPor = u.Id;
        audit.Registrar(db, u.Id, "usuarios", usuario.Id, "ATUALIZAR", new { email = antes }, new { email, administrador_principal = true });
        await db.SaveChangesAsync();
    }

    private async Task<Usuario?> AdministradorDe(InovabensDb db)
    {
        var ativos = db.Usuarios.Where(x => x.Ativo && x.ExcluidoEm == null);
        var salvo = await db.Configuracoes.AsNoTracking().Where(c => c.Chave == ChaveAdministrador).Select(c => c.Valor).FirstOrDefaultAsync();
        if (Guid.TryParse(salvo, out var id) && await ativos.FirstOrDefaultAsync(x => x.Id == id) is { } escolhido) return escolhido;
        var seed = op.SeedAdminEmail.ToLowerInvariant();
        return await ativos.FirstOrDefaultAsync(x => x.Email == seed)
            ?? await ativos.Where(x => x.Perfil!.Nome == PerfisService.PerfilAdmin).OrderBy(x => x.CriadoEm).FirstOrDefaultAsync();
    }

    /* --------------------------------- apoio --------------------------------- */

    private async Task<Dictionary<string, string>> SalvosAsync(InovabensDb db, CancellationToken ct)
    {
        var r = new Dictionary<string, string>();
        foreach (var c in await db.Configuracoes.AsNoTracking().ToListAsync(ct))
        {
            if (CatalogoConfiguracoes.Item(c.Chave) is null) continue;
            r[c.Chave] = c.Cifrado ? Cripto.Decifrar(c.Valor, op.TokenEncKey) : c.Valor;
        }
        return r;
    }

    /// <summary>Valida e limpa o valor digitado conforme o tipo do campo ("" = apagar).</summary>
    internal static string Normalizar(ItemConfiguracao item, string? valor)
    {
        var v = (valor ?? "").Trim();
        if (v.Length == 0) return "";
        if (v.Length > TamanhoMaximo) throw new RegraNegocioException(item.Chave, $"{item.Rotulo}: texto longo demais.");
        switch (item.Tipo)
        {
            case TipoCampoConfig.Url:
                if (!Uri.TryCreate(v, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    throw new RegraNegocioException(item.Chave, $"{item.Rotulo}: informe um endereço completo, começando com https://");
                return v.TrimEnd('/');
            case TipoCampoConfig.Email:
                v = v.ToLowerInvariant();
                if (!EmailValido(v)) throw new RegraNegocioException(item.Chave, $"{item.Rotulo}: e-mail inválido.");
                return v;
            case TipoCampoConfig.Telefone:
                var digitos = new string(v.Where(char.IsDigit).ToArray());
                if (digitos.Length is < 10 or > 13) throw new RegraNegocioException(item.Chave, $"{item.Rotulo}: informe DDD e número (10 a 13 dígitos).");
                return digitos;
            case TipoCampoConfig.Numero:
                if (!decimal.TryParse(v.Replace(',', '.'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n) || n <= 0 || n > 100_000)
                    throw new RegraNegocioException(item.Chave, $"{item.Rotulo}: informe um número maior que zero.");
                return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case TipoCampoConfig.Booleano:
                return v.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
        }
        if (item.Chave == "google_drive.credenciais_json") ValidarChaveContaServico(v);
        return v;
    }

    private static void ValidarChaveContaServico(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("type", out var t) && t.GetString() == "service_account"
                && r.TryGetProperty("client_email", out _) && r.TryGetProperty("private_key", out _)) return;
        }
        catch (JsonException) { }
        throw new RegraNegocioException("google_drive.credenciais_json",
            "Chave inválida: cole o conteúdo completo do arquivo .json da conta de serviço (\"type\": \"service_account\").");
    }

    private static bool EmailValido(string email) => EmailRegex().IsMatch(email) && email.Length <= 200;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();
}
