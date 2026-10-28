using System.Text.Json;
using System.Text.Json.Serialization;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;

namespace Inovabens.Core.Servicos;

/// <summary>Dados da conexão do usuário (por circuito do Blazor). O IP é capturado na requisição inicial.</summary>
public sealed class ContextoConexao
{
    public string? Ip { get; set; }
}

/// <summary>
/// Trilha de auditoria (tabela somente-inserção). O registro entra no MESMO SaveChanges da
/// operação auditada, portanto é gravado na mesma transação: ou grava tudo, ou nada.
/// </summary>
public sealed class AuditService(ContextoConexao conexao)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serializa na hora da chamada (o "antes" fica congelado mesmo que a entidade mude depois).</summary>
    public void Registrar(InovabensDb db, Guid? usuarioId, string entidade, Guid? entidadeId, string acao,
        object? antes = null, object? depois = null)
    {
        db.AuditLog.Add(new AuditLog
        {
            UsuarioId = usuarioId,
            Entidade = entidade,
            EntidadeId = entidadeId,
            Acao = acao,
            ValoresAntes = antes is null ? null : JsonSerializer.Serialize(antes, Json),
            ValoresDepois = depois is null ? null : JsonSerializer.Serialize(depois, Json),
            Ip = conexao.Ip is { Length: > 64 } ip ? ip[..64] : conexao.Ip,
        });
    }
}
