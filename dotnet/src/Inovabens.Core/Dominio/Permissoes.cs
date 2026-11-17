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
    /// <summary>Botão "Exportar Excel" da lista de Pessoas (só o Administrador recebe por padrão).</summary>
    public const string PessoasExportarLista = "pessoas:exportar_lista";

    public const string ImoveisVer = "imoveis:ver";
    public const string ImoveisCriar = "imoveis:criar";
    public const string ImoveisEditar = "imoveis:editar";
    public const string ImoveisExcluir = "imoveis:excluir";
    public const string ImoveisExportar = "imoveis:exportar";
    /// <summary>Botão "Exportar Excel" da lista de Imóveis (só o Administrador recebe por padrão).</summary>
    public const string ImoveisExportarLista = "imoveis:exportar_lista";

    public const string FinanceiroVer = "financeiro:ver";
    public const string FinanceiroEditar = "financeiro:editar";
    public const string FinanceiroExportar = "financeiro:exportar";
    /// <summary>Aba Investimento do imóvel (aquisição, fechamento e lucratividade): só o Administrador recebe por padrão.</summary>
    public const string FinanceiroInvestimento = "financeiro:investimento";

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
    /// <summary>Aceitar ou recusar propostas (só o perfil Administrador recebe por padrão).</summary>
    public const string CrmPropostasDecidir = "crm:propostas:decidir";

    /// <summary>Funis de processos do imóvel (Administrativo e Projetos).</summary>
    public const string ProcessosVer = "processos:ver";
    public const string ProcessosEditar = "processos:editar";

    public static readonly IReadOnlyList<string> Todas =
    [
        UsuariosGerir, RbacGerir, AdminConfig, AuditoriaVer,
        PessoasVer, PessoasCriar, PessoasEditar, PessoasExcluir, PessoasExportarLista,
        ImoveisVer, ImoveisCriar, ImoveisEditar, ImoveisExcluir, ImoveisExportar, ImoveisExportarLista,
        FinanceiroVer, FinanceiroEditar, FinanceiroExportar, FinanceiroInvestimento,
        MemorandosVer, MemorandosCriar, MemorandosEditar, MemorandosExcluir,
        AgendaVer, AgendaCriar, AgendaEditar, AgendaExcluir,
        CrmVer, CrmCriar, CrmEditar, CrmExcluir, CrmPropostasDecidir,
        ProcessosVer, ProcessosEditar,
    ];
}
