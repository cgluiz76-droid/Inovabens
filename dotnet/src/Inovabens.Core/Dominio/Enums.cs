namespace Inovabens.Core.Dominio;

// Os nomes dos membros são IGUAIS aos rótulos dos tipos enum do PostgreSQL (ver Dados/Migracoes/0000_init.sql).
// Não renomeie: o mapeamento é feito sem tradução de nomes (InovabensDb.ConfigurarNpgsql).

public enum FinalidadeImovel { VENDA, LOCACAO, AMBOS }

public enum SituacaoImovel { CAPTACAO, EM_AQUISICAO, EM_REGULARIZACAO, EM_RETROFIT, DISPONIVEL, VENDIDO, LOCADO, ARQUIVADO }

public enum PapelPessoa { CLIENTE, PROPRIETARIO, FIADOR, PARCEIRO, PRESTADOR, TERCEIRO }

public enum TipoMidia { FOTO, VIDEO, PLANTA, DOCUMENTO }

public enum TipoRegularizacao { DOCUMENTACAO, TRIBUTOS_ATRASO, DESMEMBRAMENTO, FRACIONAMENTO, RETIFICACAO_AREA, OUTRO }

public enum StatusRegularizacao { NAO_INICIADA, EM_ANDAMENTO, CONCLUIDA, CANCELADA }

public enum TipoItemAquisicao { IMPOSTO, TAXA, DESPESA }

public enum Recorrencia { UNICA, MENSAL, ANUAL }

public enum StatusLancamento { PAGO, NAO_PAGO, NAO_SE_APLICA }

public enum TipoReceita { VENDA, LOCACAO, OUTRA }

public enum CodigoIndice { IPCA, INCC, IGPM, SELIC }

public enum TipoPessoa { PF, PJ }

public enum TipoMemorando { MEMORANDO, ACAO_ESTRATEGICA, ACAO_OPERACIONAL }

public enum TipoCompromisso { TAREFA, REUNIAO, VISITA }

public enum StatusCompromisso { PENDENTE, CONCLUIDO, CANCELADO }

public enum TipoAlerta { COMPROMISSO_NAO_CUMPRIDO, DESPESA_NAO_PAGA }

public enum FinalidadeLead { COMPRA, LOCACAO }

public enum Funil { VENDA, LOCACAO }

public enum TipoEstagio { ABERTO, GANHO, PERDIDO }

public enum CanalInteracao { TELEFONE, WHATSAPP, EMAIL, VISITA }
