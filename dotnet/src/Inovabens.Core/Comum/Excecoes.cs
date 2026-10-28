using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Inovabens.Core.Comum;

public class NaoEncontradoException(string mensagem) : Exception(mensagem);

public class AcessoNegadoException(string mensagem = "Permissão insuficiente") : Exception(mensagem);

public class ServicoIndisponivelException(string mensagem) : Exception(mensagem);

/// <summary>Violação de regra de negócio / validação (equivale ao 422 da API Node), com erros por campo.</summary>
public class RegraNegocioException : Exception
{
    public IReadOnlyDictionary<string, string[]> Erros { get; }

    public RegraNegocioException(string campo, string mensagem) : this(new Dictionary<string, string[]> { [campo] = [mensagem] }) { }

    public RegraNegocioException(IReadOnlyDictionary<string, string[]> erros)
        : base(string.Join(" ", erros.Values.SelectMany(v => v))) => Erros = erros;
}

public static class Erros
{
    /// <summary>Mensagem amigável para exibir na tela.</summary>
    public static string Mensagem(Exception e) => e switch
    {
        RegraNegocioException or NaoEncontradoException or AcessoNegadoException or ServicoIndisponivelException => e.Message,
        DbUpdateException { InnerException: PostgresException pg } => pg.SqlState switch
        {
            // Violações de constraint do PostgreSQL viram mensagens legíveis (como o ProblemFilter do Node)
            PostgresErrorCodes.UniqueViolation => "Registro duplicado.",
            PostgresErrorCodes.CheckViolation => $"Regra de negócio violada ({pg.ConstraintName}).",
            PostgresErrorCodes.ForeignKeyViolation => "Registro vinculado a outros dados.",
            _ => "Erro ao gravar no banco de dados.",
        },
        _ => "Erro inesperado. Tente novamente; se persistir, avise o administrador.",
    };

    /// <summary>true quando o erro não é uma situação prevista (deve ser registrado em log).</summary>
    public static bool Inesperado(Exception e) =>
        e is not (RegraNegocioException or NaoEncontradoException or AcessoNegadoException or ServicoIndisponivelException)
        && e is not DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation } };
}
