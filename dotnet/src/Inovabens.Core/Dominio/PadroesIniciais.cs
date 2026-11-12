using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Dominio;

/// <summary>Perfis, categorias e estágios criados no banco novo (mesmos valores de packages/shared).</summary>
public static class PadroesIniciais
{
    public sealed record PerfilPadrao(string Nome, string Descricao, bool EscopoProprio, IReadOnlyList<string> Permissoes);

    public sealed record EstagioPadrao(string Nome, TipoEstagio Tipo, string Cor);

    public static readonly IReadOnlyList<PerfilPadrao> Perfis =
    [
        new("Administrador", "Acesso total ao sistema", false, P.Todas),
        new("Corretor", "Capta e atende; vê apenas os seus imóveis e leads", true,
        [
            P.PessoasVer, P.PessoasCriar, P.PessoasEditar,
            P.ImoveisVer, P.ImoveisCriar, P.ImoveisEditar, P.ImoveisExportar,
            P.MemorandosVer, P.MemorandosCriar, P.MemorandosEditar,
            P.AgendaVer, P.AgendaCriar, P.AgendaEditar, P.AgendaExcluir,
            P.CrmVer, P.CrmCriar, P.CrmEditar,
        ]),
        new("Financeiro", "Gerencia aquisição, despesas e retorno dos imóveis", false,
        [
            P.PessoasVer, P.ImoveisVer, P.ImoveisExportar,
            P.FinanceiroVer, P.FinanceiroEditar, P.FinanceiroExportar,
            P.MemorandosVer, P.MemorandosCriar, P.MemorandosEditar,
            P.AgendaVer, P.AgendaCriar, P.AgendaEditar,
            P.CrmVer,
        ]),
    ];

    public static readonly IReadOnlyList<string> CategoriasDespesa =
    [
        "Reformas", "Manutenção", "IPTU", "Água", "Luz", "Condomínio", "Taxas cartorárias", "Honorários profissionais", "Seguro", "Outras",
    ];

    public static readonly IReadOnlyList<EstagioPadrao> Estagios =
    [
        new("Novo Lead", TipoEstagio.ABERTO, "#64748b"),
        new("Contato Realizado", TipoEstagio.ABERTO, "#0ea5e9"),
        new("Visita Agendada", TipoEstagio.ABERTO, "#8b5cf6"),
        new("Proposta", TipoEstagio.ABERTO, "#f59e0b"),
        new("Negociação", TipoEstagio.ABERTO, "#f97316"),
        new("Documentação/Fechamento", TipoEstagio.ABERTO, "#0d9488"),
        new("Concluído", TipoEstagio.GANHO, "#16a34a"),
        new("Perdido", TipoEstagio.PERDIDO, "#dc2626"),
    ];

    /// <summary>Etapas padrão de cada funil (os de leads de compra e locação usam <see cref="Estagios"/>).</summary>
    public static IReadOnlyList<EstagioPadrao> EstagiosDo(Funil f) => f switch
    {
        Funil.CAPTACAO =>
        [
            new("Contato", TipoEstagio.ABERTO, "#64748b"),
            new("Avaliação", TipoEstagio.ABERTO, "#0ea5e9"),
            new("Documentos", TipoEstagio.ABERTO, "#f59e0b"),
            new("Captado", TipoEstagio.GANHO, "#16a34a"),
            new("Descartado", TipoEstagio.PERDIDO, "#dc2626"),
        ],
        Funil.ADMINISTRATIVO =>
        [
            new("Matrícula e certidões", TipoEstagio.ABERTO, "#64748b"),
            new("Regularização", TipoEstagio.ABERTO, "#0ea5e9"),
            new("Contrato", TipoEstagio.ABERTO, "#8b5cf6"),
            new("Cartório/Registro", TipoEstagio.ABERTO, "#f59e0b"),
            new("Concluído", TipoEstagio.GANHO, "#16a34a"),
            new("Cancelado", TipoEstagio.PERDIDO, "#dc2626"),
        ],
        Funil.PROJETOS =>
        [
            new("Aguardando início", TipoEstagio.ABERTO, "#64748b"),
            new("Definir estratégia", TipoEstagio.ABERTO, "#0ea5e9"),
            new("Desenvolvimento técnico", TipoEstagio.ABERTO, "#8b5cf6"),
            new("Análise externa", TipoEstagio.ABERTO, "#f59e0b"),
            new("Aprovação/registro", TipoEstagio.ABERTO, "#f97316"),
            new("Pendente cliente", TipoEstagio.ABERTO, "#a16207"),
            new("Vitrine", TipoEstagio.ABERTO, "#0d9488"),
            new("Concluído", TipoEstagio.GANHO, "#16a34a"),
            new("Cancelado", TipoEstagio.PERDIDO, "#dc2626"),
        ],
        _ => Estagios,
    };
}
