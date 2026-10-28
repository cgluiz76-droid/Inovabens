using Inovabens.Core.Comum;

namespace Inovabens.Core.Imoveis;

/// <summary>Regras de negócio puras do módulo (sem banco), fáceis de testar.</summary>
public static class RegrasImovel
{
    /// <summary>Próximo código sequencial no formato IMV-000123.</summary>
    public static string FormatarCodigo(int numero) => $"IMV-{numero:D6}";

    /// <summary>
    /// Regra: a soma dos percentuais VIGENTES (na data de hoje) dos proprietários deve ser 100%.
    /// Lista vazia é permitida (imóvel ainda sem proprietários).
    /// </summary>
    public static void ValidarProprietarios(IReadOnlyCollection<ProprietarioEntrada> itens, DateOnly hoje)
    {
        var erros = new List<string>();
        foreach (var (it, i) in itens.Select((x, i) => (x, i + 1)))
        {
            if (it.PessoaId is null || it.PessoaId == Guid.Empty) erros.Add($"Linha {i}: selecione a pessoa.");
            if (it.Percentual is not { } p || p <= 0 || p > 100) erros.Add($"Linha {i}: o percentual deve ser maior que 0 e no máximo 100.");
            else if (decimal.Round(p, 2) != p) erros.Add($"Linha {i}: use no máximo 2 casas decimais no percentual.");
            if (it.InicioEm is { } ini && it.FimEm is { } fim && fim < ini) erros.Add($"Linha {i}: o fim não pode ser antes do início.");
        }
        if (erros.Count > 0) throw new RegraNegocioException(new Dictionary<string, string[]> { ["proprietarios"] = [.. erros] });

        if (itens.Count == 0) return;
        var soma = itens
            .Where(i => (i.InicioEm is null || i.InicioEm <= hoje) && (i.FimEm is null || i.FimEm >= hoje))
            .Sum(i => i.Percentual!.Value);
        if (soma != 100m)
            throw new RegraNegocioException("proprietarios",
                $"A soma dos percentuais vigentes deve ser 100% (atual: {soma.ToString("N2", Texto.PtBr)}%).");
    }
}
