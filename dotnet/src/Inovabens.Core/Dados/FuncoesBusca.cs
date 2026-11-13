namespace Inovabens.Core.Dados;

/// <summary>
/// Funções do Postgres usadas nas consultas de busca (migração 0029). Só valem dentro de consultas do EF:
/// chamá-las em memória lança exceção.
/// </summary>
public static class FuncoesBusca
{
    /// <summary><c>busca_normalizar(t)</c>: minúsculas e sem acento.</summary>
    public static string Normalizar(string? texto) => throw new InvalidOperationException("Só em consultas do EF.");

    /// <summary><c>busca_digitos(t)</c>: só os dígitos (telefone, CPF/CNPJ).</summary>
    public static string Digitos(string? texto) => throw new InvalidOperationException("Só em consultas do EF.");
}
