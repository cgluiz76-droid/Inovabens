using System.Globalization;
using System.Text;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Busca;

public enum TipoResultadoBusca { Imovel, Pessoa, Lead }

/// <summary>Um resultado da busca global: o que mostrar na lista e para onde ir ao escolher.</summary>
public sealed record ItemBusca(TipoResultadoBusca Tipo, Guid Id, string Titulo, string? Detalhe, string? Etiqueta, string Url);

public sealed record ResultadoBusca(IReadOnlyList<ItemBusca> Imoveis, IReadOnlyList<ItemBusca> Pessoas, IReadOnlyList<ItemBusca> Leads)
{
    public static readonly ResultadoBusca Vazio = new([], [], []);
    public IEnumerable<ItemBusca> Todos => Imoveis.Concat(Pessoas).Concat(Leads);
}

/// <summary>
/// Busca global (Ctrl+K): imóveis, pessoas e leads por código, nome, endereço, e-mail ou telefone, sem diferenciar
/// acentos e maiúsculas. Cada tipo só aparece para quem pode vê-lo, com o mesmo escopo das telas.
/// </summary>
public sealed class BuscaGlobalService(IDbContextFactory<InovabensDb> fabrica)
{
    public const int MinimoCaracteres = 2;
    private const int PorTipo = 5;

    public async Task<ResultadoBusca> BuscarAsync(UsuarioAtual u, string? termo, CancellationToken ct = default)
    {
        var limpo = Normalizar(termo);
        if (limpo.Length < MinimoCaracteres) return ResultadoBusca.Vazio;
        var like = $"%{Texto.LikeLiteral(limpo)}%";
        var digitos = new string(limpo.Where(char.IsAsciiDigit).ToArray());
        // telefone/CPF só pelos dígitos quando o termo tem dígitos suficientes para não casar com tudo
        var likeDigitos = digitos.Length >= 4 ? $"%{digitos}%" : null;

        await using var db = await fabrica.CreateDbContextAsync(ct);
        IReadOnlyList<ItemBusca> imoveis = [], pessoas = [], leads = [];

        if (u.Pode(P.ImoveisVer))
        {
            var prefixo = $"{Texto.LikeLiteral(limpo)}%";
            var linhas = await db.Imoveis.AsNoTracking().Where(i => i.ExcluidoEm == null).VisiveisPara(u)
                .Where(i => EF.Functions.Like(FuncoesBusca.Normalizar(i.Codigo), like, @"\")
                    || EF.Functions.Like(FuncoesBusca.Normalizar(i.CodigoAntigo), like, @"\")
                    || EF.Functions.Like(FuncoesBusca.Normalizar(i.Titulo), like, @"\")
                    || EF.Functions.Like(FuncoesBusca.Normalizar(i.Logradouro), like, @"\")
                    || EF.Functions.Like(FuncoesBusca.Normalizar(i.Bairro), like, @"\")
                    || EF.Functions.Like(FuncoesBusca.Normalizar(i.Cidade), like, @"\"))
                // código que começa com o termo vem primeiro ("ca00" → CA0001, CA0002…); arquivados por último
                .OrderBy(i => EF.Functions.Like(FuncoesBusca.Normalizar(i.Codigo), prefixo, @"\") ? 0 : 1)
                .ThenBy(i => i.Situacao == SituacaoImovel.ARQUIVADO ? 1 : 0).ThenBy(i => i.Codigo)
                .Take(PorTipo)
                .Select(i => new { i.Id, i.Codigo, i.Titulo, i.Bairro, i.Cidade, i.Situacao })
                .ToListAsync(ct);
            imoveis = [.. linhas.Select(i => new ItemBusca(TipoResultadoBusca.Imovel, i.Id, $"{i.Codigo} · {i.Titulo}",
                Juntar(i.Bairro, i.Cidade), Texto.Rotulo(i.Situacao), $"imoveis/{i.Id}"))];
        }

        if (u.Pode(P.PessoasVer))
        {
            var linhas = await db.Pessoas.AsNoTracking().Where(p => p.ExcluidoEm == null)
                .Where(p => EF.Functions.Like(FuncoesBusca.Normalizar(p.NomeRazao), like, @"\")
                    || EF.Functions.Like(FuncoesBusca.Normalizar(p.Email), like, @"\")
                    || (likeDigitos != null && (EF.Functions.Like(FuncoesBusca.Digitos(p.Telefone), likeDigitos)
                        || EF.Functions.Like(FuncoesBusca.Digitos(p.Whatsapp), likeDigitos)
                        || EF.Functions.Like(FuncoesBusca.Digitos(p.CpfCnpj), likeDigitos))))
                .OrderBy(p => p.NomeRazao).Take(PorTipo)
                .Select(p => new { p.Id, p.NomeRazao, p.Telefone, p.Whatsapp, p.Email, p.Cidade })
                .ToListAsync(ct);
            pessoas = [.. linhas.Select(p => new ItemBusca(TipoResultadoBusca.Pessoa, p.Id, p.NomeRazao,
                Juntar(p.Whatsapp ?? p.Telefone, p.Email, p.Cidade), null, $"pessoas?id={p.Id}"))];
        }

        if (u.Pode(P.CrmVer))
        {
            var linhas = await db.Leads.AsNoTracking().Where(l => l.ExcluidoEm == null).VisiveisPara(u)
                .Where(l => EF.Functions.Like(FuncoesBusca.Normalizar(l.Pessoa!.NomeRazao), like, @"\")
                    || EF.Functions.Like(FuncoesBusca.Normalizar(l.Pessoa.Email), like, @"\")
                    || (likeDigitos != null && (EF.Functions.Like(FuncoesBusca.Digitos(l.Pessoa.Telefone), likeDigitos)
                        || EF.Functions.Like(FuncoesBusca.Digitos(l.Pessoa.Whatsapp), likeDigitos))))
                .OrderByDescending(l => l.AtualizadoEm).Take(PorTipo)
                .Select(l => new { l.Id, Nome = l.Pessoa!.NomeRazao, Estagio = l.Estagio!.Nome, Responsavel = l.Responsavel != null ? l.Responsavel.Nome : null })
                .ToListAsync(ct);
            leads = [.. linhas.Select(l => new ItemBusca(TipoResultadoBusca.Lead, l.Id, l.Nome,
                Juntar(l.Responsavel is null ? "Fila aberta" : l.Responsavel), l.Estagio, $"crm?lead={l.Id}"))];
        }

        return new ResultadoBusca(imoveis, pessoas, leads);
    }

    /// <summary>Minúsculas e sem acento, igual à função busca_normalizar do banco.</summary>
    public static string Normalizar(string? texto)
    {
        if (Texto.Limpo(texto) is not { } t) return "";
        var sb = new StringBuilder(t.Length);
        foreach (var c in t.ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string? Juntar(params string?[] partes) =>
        string.Join(" · ", partes.Where(p => !string.IsNullOrWhiteSpace(p))) is { Length: > 0 } s ? s : null;
}
