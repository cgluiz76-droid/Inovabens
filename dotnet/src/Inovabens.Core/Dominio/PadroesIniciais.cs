using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Dominio;

/// <summary>Perfis, categorias e estágios criados no banco novo (mesmos valores de packages/shared).</summary>
public static class PadroesIniciais
{
    public sealed record PerfilPadrao(string Nome, string Descricao, bool EscopoProprio, IReadOnlyList<string> Permissoes);

    public sealed record EstagioPadrao(string Nome, TipoEstagio Tipo, string Cor);

    public sealed record SugestaoPadrao(TipoCompromisso Tipo, string Titulo, int Dias);

    /// <summary>Próximo passo sugerido nas etapas padrão (os mesmos da migração 0026).</summary>
    public static SugestaoPadrao? SugestaoDe(Funil f, string etapa) => (f, etapa) switch
    {
        (Funil.VENDA or Funil.LOCACAO, "Novo Lead") => new(TipoCompromisso.LIGACAO, "Primeiro contato com o cliente", 0),
        (Funil.VENDA or Funil.LOCACAO, "Contato Realizado") => new(TipoCompromisso.VISITA, "Agendar visita ao imóvel", 2),
        (Funil.VENDA or Funil.LOCACAO, "Visita Agendada") => new(TipoCompromisso.VISITA, "Visita ao imóvel", 2),
        (Funil.VENDA or Funil.LOCACAO, "Proposta") => new(TipoCompromisso.LIGACAO, "Retorno sobre a proposta", 2),
        (Funil.VENDA or Funil.LOCACAO, "Negociação") => new(TipoCompromisso.LIGACAO, "Acompanhar a negociação", 2),
        (Funil.VENDA, "Documentação/Fechamento") => new(TipoCompromisso.TAREFA, "Conferir a documentação", 3),
        (Funil.LOCACAO, "Documentação/Fechamento") => new(TipoCompromisso.TAREFA, "Conferir a documentação e a garantia", 3),
        (Funil.VENDA, "Concluído") => new(TipoCompromisso.TAREFA, "Pós-venda: contato com o cliente", 7),
        (Funil.LOCACAO, "Concluído") => new(TipoCompromisso.TAREFA, "Pós-locação: contato com o inquilino", 7),
        (Funil.CAPTACAO, "Contato") => new(TipoCompromisso.LIGACAO, "Conversar com o proprietário", 0),
        (Funil.CAPTACAO, "Avaliação") => new(TipoCompromisso.VISITA, "Avaliar o imóvel", 2),
        (Funil.CAPTACAO, "Documentos") => new(TipoCompromisso.TAREFA, "Receber os documentos do imóvel", 3),
        (Funil.CAPTACAO, "Captado") => new(TipoCompromisso.TAREFA, "Cadastrar o imóvel e as fotos", 1),
        _ => null,
    };

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
            // o corretor leva e devolve chaves dos seus imóveis; cadastrar/excluir chave fica com o escritório
            P.ChavesVer, P.ChavesMovimentar,
        ]),
        new("Financeiro", "Gerencia os lançamentos financeiros (receitas e despesas) dos imóveis", false,
        [
            P.PessoasVer, P.ImoveisVer, P.ImoveisExportar,
            P.FinanceiroVer, P.FinanceiroEditar, P.FinanceiroExportar,
            P.FinanceiroEmpresaVer, P.FinanceiroEmpresaEditar, P.FinanceiroEmpresaExportar,
            P.MemorandosVer, P.MemorandosCriar, P.MemorandosEditar,
            P.AgendaVer, P.AgendaCriar, P.AgendaEditar,
            P.CrmVer,
        ]),
    ];

    /// <summary>Contas orçamentárias iniciais (as mesmas da migração 0030).</summary>
    public static readonly IReadOnlyList<(string Nome, TipoConta Tipo)> ContasOrcamentarias =
    [
        ("Reformas", TipoConta.DESPESA), ("Manutenção", TipoConta.DESPESA), ("IPTU", TipoConta.DESPESA), ("Água", TipoConta.DESPESA),
        ("Luz", TipoConta.DESPESA), ("Condomínio", TipoConta.DESPESA), ("Taxas cartorárias", TipoConta.DESPESA),
        ("Honorários profissionais", TipoConta.DESPESA), ("Seguro", TipoConta.DESPESA), ("Outras", TipoConta.DESPESA),
        ("Reembolso", TipoConta.RECEITA), ("Taxa de administração de aluguel", TipoConta.RECEITA), ("Aluguel recebido", TipoConta.RECEITA),
        ("Outras receitas", TipoConta.RECEITA),
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
