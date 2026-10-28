using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Memorandos;

public sealed record MemorandoDetalhe(Memorando Dados, string AutorNome);

public sealed class MemorandoForm
{
    public TipoMemorando Tipo { get; set; } = TipoMemorando.MEMORANDO;

    [Required(ErrorMessage = "Informe o título.")]
    [StringLength(200, MinimumLength = 2, ErrorMessage = "O título deve ter de 2 a 200 caracteres.")]
    public string Titulo { get; set; } = "";

    public string? Corpo { get; set; }
    public bool Fixado { get; set; }
}

/// <summary>Memorandos e ações do imóvel (port de memorandos.module.ts). Toda edição fica na auditoria.</summary>
public sealed class MemorandosService(IDbContextFactory<InovabensDb> fabrica, AuditService audit)
{
    public async Task<IReadOnlyList<MemorandoDetalhe>> ListarAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.MemorandosVer);
        await using var db = await fabrica.CreateDbContextAsync();
        await ImoveisService.ImovelComAcesso(db, u, imovelId, rastrear: false);
        return await db.Memorandos.AsNoTracking()
            .Where(m => m.ImovelId == imovelId && m.ExcluidoEm == null)
            .OrderByDescending(m => m.Fixado).ThenByDescending(m => m.CriadoEm)
            .Select(m => new MemorandoDetalhe(m, m.Autor!.Nome))
            .ToListAsync();
    }

    public async Task<Guid> CriarAsync(UsuarioAtual u, Guid imovelId, MemorandoForm form)
    {
        u.Exigir(P.MemorandosCriar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await ImoveisService.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var m = new Memorando
        {
            Id = Guid.NewGuid(), ImovelId = imovelId, AutorId = u.Id, Tipo = form.Tipo,
            Titulo = form.Titulo.Trim(), Corpo = form.Corpo ?? "", Fixado = form.Fixado,
        };
        db.Memorandos.Add(m);
        audit.Registrar(db, u.Id, "memorandos", m.Id, "CRIAR", depois: m);
        await db.SaveChangesAsync();
        return m.Id;
    }

    public async Task AtualizarAsync(UsuarioAtual u, Guid id, MemorandoForm form)
    {
        u.Exigir(P.MemorandosEditar);
        Validacao.Validar(form);
        await Alterar(u, id, "ATUALIZAR", m =>
        {
            m.Tipo = form.Tipo;
            m.Titulo = form.Titulo.Trim();
            m.Corpo = form.Corpo ?? "";
            m.Fixado = form.Fixado;
        });
    }

    public Task FixarAsync(UsuarioAtual u, Guid id, bool fixado)
    {
        u.Exigir(P.MemorandosEditar);
        return Alterar(u, id, "ATUALIZAR", m => m.Fixado = fixado);
    }

    public Task ExcluirAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.MemorandosExcluir);
        return Alterar(u, id, "EXCLUIR", m => m.ExcluidoEm = DateTime.UtcNow);
    }

    private async Task Alterar(UsuarioAtual u, Guid id, string acao, Action<Memorando> mudar)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var m = await db.Memorandos.SingleOrDefaultAsync(x => x.Id == id && x.ExcluidoEm == null)
            ?? throw new NaoEncontradoException("Memorando não encontrado.");
        await ImoveisService.ImovelComAcesso(db, u, m.ImovelId, rastrear: false);
        var antes = m.Copia();
        mudar(m);
        audit.Registrar(db, u.Id, "memorandos", id, acao, antes, acao == "EXCLUIR" ? null : m);
        await db.SaveChangesAsync();
    }
}
