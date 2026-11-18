namespace Inovabens.Core.Dominio;

// Os nomes dos membros são IGUAIS aos rótulos dos tipos enum do PostgreSQL (ver Dados/Migracoes/0000_init.sql).
// Não renomeie: o mapeamento é feito sem tradução de nomes (InovabensDb.ConfigurarNpgsql).

public enum FinalidadeImovel { VENDA, LOCACAO, AMBOS }

public enum SituacaoImovel { CAPTACAO, EM_AQUISICAO, EM_REGULARIZACAO, EM_RETROFIT, DISPONIVEL, VENDIDO, LOCADO, ARQUIVADO }

public enum TipoMidia { FOTO, VIDEO, PLANTA, DOCUMENTO }

/// <summary>Melhoria da foto pelo Magnific. Gravado como texto (varchar + CHECK), não é enum do PostgreSQL.</summary>
public enum StatusMelhoria { PENDENTE, PROCESSANDO, CONCLUIDA, FALHA }

public enum StatusRegularizacao { NAO_INICIADA, EM_ANDAMENTO, CONCLUIDA, CANCELADA }

public enum TipoItemAquisicao { IMPOSTO, TAXA, DESPESA }

public enum TipoConta { RECEITA, DESPESA }

/// <summary>
/// PREVISTO: gerado pela recorrência, aguardando confirmação. ABERTO: título a pagar/receber (vencimento em <c>data</c>).
/// LIQUIDADO: pago/recebido, entra no saldo. IGNORADO: previsto descartado.
/// </summary>
public enum SituacaoLancamento { PREVISTO, ABERTO, LIQUIDADO, IGNORADO }

public enum CodigoIndice { IPCA, INCC, IGPM, SELIC }

public enum TipoPessoa { PF, PJ }

/// <summary>Unidades da federação (ordem alfabética do nome). Gravado como texto (a sigla), não é enum do PostgreSQL.</summary>
public enum Uf { AC, AL, AP, AM, BA, CE, DF, ES, GO, MA, MT, MS, MG, PA, PB, PR, PE, PI, RJ, RN, RS, RO, RR, SC, SP, SE, TO }

public enum TipoMemorando { MEMORANDO, ACAO_ESTRATEGICA, ACAO_OPERACIONAL }

public enum TipoCompromisso { TAREFA, REUNIAO, VISITA, LIGACAO, MENSAGEM }

public enum StatusCompromisso { PENDENTE, CONCLUIDO, CANCELADO }

public enum TipoAlerta { COMPROMISSO_NAO_CUMPRIDO, TITULO_EM_ATRASO, LIMITE_GRATUITO, NOVO_LEAD, PROPOSTA_EXPIRADA, ETAPA_PARADA }

/// <summary>CAPTACAO: proprietário oferecendo o imóvel (funil de captação).</summary>
public enum FinalidadeLead { COMPRA, LOCACAO, CAPTACAO }

/// <summary>
/// VENDA, LOCACAO e CAPTACAO são funis de leads (cartão = lead); ADMINISTRATIVO e PROJETOS são funis de processos do
/// imóvel (cartão = imóvel). Ver <see cref="Funis"/>.
/// </summary>
public enum Funil { VENDA, LOCACAO, CAPTACAO, ADMINISTRATIVO, PROJETOS }

/// <summary>Nomes e regras dos funis.</summary>
public static class Funis
{
    public static readonly IReadOnlyList<Funil> DeLeads = [Funil.VENDA, Funil.LOCACAO, Funil.CAPTACAO];
    public static readonly IReadOnlyList<Funil> DeProcessos = [Funil.ADMINISTRATIVO, Funil.PROJETOS];

    public static bool EhProcesso(Funil f) => f is Funil.ADMINISTRATIVO or Funil.PROJETOS;

    public static string Nome(Funil f) => f switch
    {
        Funil.VENDA => "Venda",
        Funil.LOCACAO => "Locação",
        Funil.CAPTACAO => "Captação",
        Funil.ADMINISTRATIVO => "Administrativo",
        Funil.PROJETOS => "Projetos",
        _ => f.ToString(),
    };

    public static string Nome(FinalidadeLead f) => Nome(Do(f));

    public static Funil Do(FinalidadeLead f) => f switch
    {
        FinalidadeLead.LOCACAO => Funil.LOCACAO,
        FinalidadeLead.CAPTACAO => Funil.CAPTACAO,
        _ => Funil.VENDA,
    };

    /// <summary>Finalidade dos leads de um funil de leads (funil de processo não tem leads).</summary>
    public static FinalidadeLead FinalidadeDo(Funil f) => f switch
    {
        Funil.VENDA => FinalidadeLead.COMPRA,
        Funil.LOCACAO => FinalidadeLead.LOCACAO,
        Funil.CAPTACAO => FinalidadeLead.CAPTACAO,
        _ => throw new ArgumentException($"O funil {Nome(f)} é de processos do imóvel, não de leads.", nameof(f)),
    };
}

public enum TipoEstagio { ABERTO, GANHO, PERDIDO }

public enum CanalInteracao { TELEFONE, WHATSAPP, EMAIL, VISITA, OUTRO }

public enum StatusProposta { EM_ANALISE, ACEITA, RECUSADA, SUBSTITUIDA, CANCELADA, EXPIRADA }

/// <summary>Quem fez a oferta: o cliente (proposta) ou o proprietário (contraproposta).</summary>
public enum AutorProposta { CLIENTE, PROPRIETARIO }

public enum TipoItemProposta { ENTRADA, PARCELAS_PROPRIETARIO, FINANCIAMENTO, FGTS, CONSORCIO, PERMUTA_IMOVEL, PERMUTA_VEICULO, OUTRO }

public enum TipoModeloDocumento { PROPOSTA, CONTRATO }

public enum GarantiaLocacao { FIADOR, CAUCAO, SEGURO_FIANCA, TITULO_CAPITALIZACAO, SEM_GARANTIA }
