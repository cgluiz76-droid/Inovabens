namespace Inovabens.Core.Dominio;

/// <summary>Catálogo de permissões (mesmos códigos da tabela <c>permissoes</c> e de packages/shared).</summary>
public static class Permissoes
{
    public const string UsuariosGerir = "usuarios:gerir";
    public const string RbacGerir = "rbac:gerir";
    public const string AdminConfig = "admin:config";
    public const string AuditoriaVer = "auditoria:ver";

    public const string PessoasVer = "pessoas:ver";
    public const string PessoasCriar = "pessoas:criar";
    public const string PessoasEditar = "pessoas:editar";
    public const string PessoasExcluir = "pessoas:excluir";

    public const string ImoveisVer = "imoveis:ver";
    public const string ImoveisCriar = "imoveis:criar";
    public const string ImoveisEditar = "imoveis:editar";
    public const string ImoveisExcluir = "imoveis:excluir";
    public const string ImoveisExportar = "imoveis:exportar";

    public const string FinanceiroVer = "financeiro:ver";
    public const string FinanceiroEditar = "financeiro:editar";
    public const string FinanceiroExportar = "financeiro:exportar";

    public const string MemorandosVer = "memorandos:ver";
    public const string MemorandosCriar = "memorandos:criar";
    public const string MemorandosEditar = "memorandos:editar";
    public const string MemorandosExcluir = "memorandos:excluir";

    public const string AgendaVer = "agenda:ver";
    public const string AgendaCriar = "agenda:criar";
    public const string AgendaEditar = "agenda:editar";
    public const string AgendaExcluir = "agenda:excluir";

    public const string CrmVer = "crm:ver";
    public const string CrmCriar = "crm:criar";
    public const string CrmEditar = "crm:editar";
    public const string CrmExcluir = "crm:excluir";

    public static readonly IReadOnlyList<string> Todas =
    [
        UsuariosGerir, RbacGerir, AdminConfig, AuditoriaVer,
        PessoasVer, PessoasCriar, PessoasEditar, PessoasExcluir,
        ImoveisVer, ImoveisCriar, ImoveisEditar, ImoveisExcluir, ImoveisExportar,
        FinanceiroVer, FinanceiroEditar, FinanceiroExportar,
        MemorandosVer, MemorandosCriar, MemorandosEditar, MemorandosExcluir,
        AgendaVer, AgendaCriar, AgendaEditar, AgendaExcluir,
        CrmVer, CrmCriar, CrmEditar, CrmExcluir,
    ];
}
