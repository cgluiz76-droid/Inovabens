using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Inovabens.Core.Dominio;

/* ------------------------------ Pessoas ------------------------------ */

[Table("pessoas")]
public class Pessoa
{
    public Guid Id { get; set; }
    public TipoPessoa Tipo { get; set; } = TipoPessoa.PF;
    public string NomeRazao { get; set; } = "";
    public string? CpfCnpj { get; set; }
    public string? Rg { get; set; }
    public string? Email { get; set; }
    public string? Telefone { get; set; }
    public string? Whatsapp { get; set; }
    public string? Cep { get; set; }
    public string? Logradouro { get; set; }
    public string? Numero { get; set; }
    public string? Complemento { get; set; }
    public string? Bairro { get; set; }
    public string? Cidade { get; set; }
    public string? Uf { get; set; }
    public string? Observacoes { get; set; }
    public DateTime? LgpdConsentimentoEm { get; set; }
    public Guid? CriadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? ExcluidoEm { get; set; }
    public List<PessoaPapel> Papeis { get; set; } = [];

    public Pessoa Copia()
    {
        var c = (Pessoa)MemberwiseClone();
        c.Papeis = [.. Papeis.Select(p => new PessoaPapel { PessoaId = p.PessoaId, Papel = p.Papel, Especialidade = p.Especialidade })];
        return c;
    }
}

[Table("pessoa_papeis")]
public class PessoaPapel
{
    [JsonIgnore] public Guid PessoaId { get; set; }
    /// <summary>Código do perfil de pessoa (tabela perfis_pessoa).</summary>
    public string Papel { get; set; } = "";
    /// <summary>Prestadores: advogado, topógrafo, despachante...</summary>
    public string? Especialidade { get; set; }
}

/// <summary>Perfil de pessoa (cliente, proprietário, ...), mantido na Administração. O código não muda.</summary>
[Table("perfis_pessoa")]
public class PerfilPessoa
{
    public const string Cliente = "CLIENTE";
    public const string Prestador = "PRESTADOR";

    public string Codigo { get; set; } = "";
    public string Nome { get; set; } = "";
    public bool Ativo { get; set; } = true;
    public int Ordem { get; set; }
}

/* ----------------------------- Memorandos ----------------------------- */

[Table("memorandos")]
public class Memorando
{
    public Guid Id { get; set; }
    public Guid ImovelId { get; set; }
    public Guid AutorId { get; set; }
    public TipoMemorando Tipo { get; set; } = TipoMemorando.MEMORANDO;
    public string Titulo { get; set; } = "";
    public string Corpo { get; set; } = "";
    public bool Fixado { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? ExcluidoEm { get; set; }
    [JsonIgnore] public Usuario? Autor { get; set; }

    public Memorando Copia() => (Memorando)MemberwiseClone();
}

/* ------------------------------- Agenda ------------------------------- */

/// <summary>Compromisso (tarefa, reunião, visita) — sempre ligado a um imóvel. CHECK no banco: fim_em &gt;= inicio_em.</summary>
[Table("compromissos")]
public class Compromisso
{
    public Guid Id { get; set; }
    public Guid ImovelId { get; set; }
    public Guid? LeadId { get; set; }
    public TipoCompromisso Tipo { get; set; } = TipoCompromisso.TAREFA;
    public string Titulo { get; set; } = "";
    public string? Descricao { get; set; }
    public string? Local { get; set; }
    public DateTime InicioEm { get; set; }
    public DateTime FimEm { get; set; }
    public StatusCompromisso Status { get; set; } = StatusCompromisso.PENDENTE;
    public DateTime? ConcluidoEm { get; set; }
    public Guid ResponsavelId { get; set; }
    public string? GoogleEventId { get; set; }
    public string? GoogleEtag { get; set; }
    public DateTime? SincronizadoEm { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? ExcluidoEm { get; set; }
    [JsonIgnore] public Imovel? Imovel { get; set; }
    [JsonIgnore] public Usuario? Responsavel { get; set; }

    public Compromisso Copia() => (Compromisso)MemberwiseClone();
}

[Table("compromisso_participantes")]
public class CompromissoParticipante
{
    public Guid Id { get; set; }
    public Guid CompromissoId { get; set; }
    public Guid? UsuarioId { get; set; }
    public Guid? PessoaId { get; set; }
    public string? Email { get; set; }
    public string? Resposta { get; set; }
}

/// <summary>
/// Alertas por usuário. Índices únicos parciais no banco garantem um alerta ABERTO por compromisso
/// e um por (lançamento, usuário) — os jobs inserem com ON CONFLICT DO NOTHING.
/// </summary>
[Table("alertas")]
public class Alerta
{
    public Guid Id { get; set; }
    public TipoAlerta Tipo { get; set; }
    public Guid UsuarioId { get; set; }
    public Guid? CompromissoId { get; set; }
    public Guid? LancamentoId { get; set; }
    public string Mensagem { get; set; } = "";
    public DateTime GeradoEm { get; set; } = DateTime.UtcNow;
    public DateTime? LidoEm { get; set; }
    public DateTime? ResolvidoEm { get; set; }
}

/// <summary>Conta Google Calendar conectada por usuário. O refresh token fica cifrado (AES-GCM, TOKEN_ENC_KEY).</summary>
[Table("google_contas")]
public class GoogleConta
{
    public Guid Id { get; set; }
    public Guid UsuarioId { get; set; }
    public string? GoogleEmail { get; set; }
    public string RefreshTokenCifrado { get; set; } = "";
    public string CalendarId { get; set; } = "primary";
    public string? SyncToken { get; set; }
    public string? CanalWatchId { get; set; }
    public string? CanalResourceId { get; set; }
    public DateTime? CanalExpiraEm { get; set; }
    public DateTime? UltimaSyncEm { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
}

/* -------------------------------- CRM -------------------------------- */

[Table("funil_estagios")]
public class FunilEstagio
{
    public Guid Id { get; set; }
    public Funil Funil { get; set; } = Funil.VENDA;
    public string Nome { get; set; } = "";
    public int Ordem { get; set; }
    public string? Cor { get; set; } = "#64748b";
    public TipoEstagio Tipo { get; set; } = TipoEstagio.ABERTO;
    public bool Ativo { get; set; } = true;
}

[Table("leads")]
public class Lead
{
    public Guid Id { get; set; }
    public Guid PessoaId { get; set; }
    public FinalidadeLead Finalidade { get; set; } = FinalidadeLead.COMPRA;
    public string? Origem { get; set; }
    public Guid EstagioId { get; set; }
    public Guid? ResponsavelId { get; set; }
    public decimal? OrcamentoMin { get; set; }
    public decimal? OrcamentoMax { get; set; }
    public string? MotivoPerda { get; set; }
    public DateTime? ProximaAcaoEm { get; set; }
    public string? Observacoes { get; set; }
    public int Ordem { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? ExcluidoEm { get; set; }
    [JsonIgnore] public Pessoa? Pessoa { get; set; }
    [JsonIgnore] public FunilEstagio? Estagio { get; set; }
    [JsonIgnore] public Usuario? Responsavel { get; set; }

    public Lead Copia() => (Lead)MemberwiseClone();
}

[Table("lead_historico_estagio")]
public class LeadHistoricoEstagio
{
    public Guid Id { get; set; }
    public Guid LeadId { get; set; }
    public Guid? EstagioAnteriorId { get; set; }
    public Guid EstagioNovoId { get; set; }
    public Guid? UsuarioId { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}

[Table("lead_interacoes")]
public class LeadInteracao
{
    public Guid Id { get; set; }
    public Guid LeadId { get; set; }
    public Guid? UsuarioId { get; set; }
    public CanalInteracao Canal { get; set; }
    public string Resumo { get; set; } = "";
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}

[Table("lead_imoveis_interesse")]
public class LeadImovelInteresse
{
    public Guid LeadId { get; set; }
    public Guid ImovelId { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}
