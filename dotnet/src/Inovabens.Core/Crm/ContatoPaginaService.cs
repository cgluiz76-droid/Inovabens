using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inovabens.Core.Crm;

/// <summary>Formulário "Quero mais informações" da página do imóvel. Todos os campos são obrigatórios.</summary>
public sealed class ContatoPaginaForm : IValidatableObject
{
    [Required(ErrorMessage = "Informe seu nome.")]
    [MaxLength(120, ErrorMessage = "Nome com no máximo 120 caracteres.")]
    public string Nome { get; set; } = "";

    [Required(ErrorMessage = "Informe seu telefone com DDD.")]
    [MaxLength(30, ErrorMessage = "Telefone inválido.")]
    public string Telefone { get; set; } = "";

    [Required(ErrorMessage = "Escreva sua mensagem.")]
    [MaxLength(1000, ErrorMessage = "Mensagem com no máximo 1.000 caracteres.")]
    public string Mensagem { get; set; } = "";

    /// <summary>Concordância com o uso dos dados para responder ao contato (LGPD, Lei 13.709/2018).</summary>
    public bool Consentimento { get; set; }

    /// <summary>Campo escondido na página: pessoas não veem nem preenchem; robôs de spam costumam preencher.</summary>
    public string? Site { get; set; }

    /// <summary>Telefone brasileiro com DDD (10 ou 11 dígitos, com ou sem +55), no formato "55..." do WhatsApp. Null = inválido.</summary>
    public static string? Digitos(string? telefone)
    {
        var d = new string((telefone ?? "").Where(char.IsAsciiDigit).ToArray());
        if (d.Length is 12 or 13 && d.StartsWith("55")) d = d[2..];
        return d.Length is 10 or 11 && d[0] != '0' ? "55" + d : null;
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(Telefone) && Digitos(Telefone) is null)
            yield return new("Telefone inválido. Use DDD + número (ex.: 17 99999-9999).", [nameof(Telefone)]);
        if (!Consentimento)
            yield return new("Para enviarmos a resposta, marque a concordância com o uso dos seus dados.", [nameof(Consentimento)]);
    }
}

/// <summary>
/// Contato pelo formulário da página exclusiva do imóvel: vira lead no CRM ligado ao imóvel (ou entra no lead aberto da
/// mesma pessoa), com o mesmo rodízio e os mesmos alertas dos leads do WhatsApp. O consentimento (LGPD) fica registrado
/// na interação do lead e na auditoria.
/// </summary>
public sealed class ContatoPaginaService(IDbContextFactory<InovabensDb> fabrica, AuditService audit, InovabensOpcoes op,
    ILogger<ContatoPaginaService> log)
{
    public const string Origem = "Página do imóvel";
    public const string TextoConsentimento =
        "Concordo que a Inova Bens use meu nome, telefone e mensagem para responder a este contato, conforme a Lei 13.709/2018 (LGPD).";

    /// <summary>Grava o contato. Falso = página não publicada (ou removida). Dados inválidos: <see cref="ValidacaoException"/>.</summary>
    /// <param name="origem">Origem da visita (utm_source normalizado), para o painel de desempenho da página.</param>
    public async Task<bool> EnviarAsync(string slug, ContatoPaginaForm f, string origem = "direto", CancellationToken ct = default)
    {
        f.Nome = f.Nome?.Trim() ?? "";
        f.Telefone = f.Telefone?.Trim() ?? "";
        f.Mensagem = f.Mensagem?.Trim() ?? "";
        Validacao.Validar(f);
        if (!string.IsNullOrEmpty(f.Site)) return true; // robô: finge que deu certo e não grava nada
        var waId = ContatoPaginaForm.Digitos(f.Telefone)!;
        var chave = WhatsappService.ChaveTelefone(waId)!;

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var pagina = await db.PaginasImovel.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == slug && p.Publicada, ct);
        var imovel = pagina is null ? null
            : await db.Imoveis.AsNoTracking().FirstOrDefaultAsync(i => i.Id == pagina.ImovelId && i.ExcluidoEm == null, ct);
        if (pagina is null || imovel is null) return false;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // mesmo telefone ao mesmo tempo (WhatsApp ou outro envio do formulário) não cria duas pessoas nem dois leads
        await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtext({"whatsapp:" + chave}))", ct);

        var telefone = WhatsappService.FormatarTelefone(waId);
        var pessoa = await WhatsappService.PessoaDoTelefone(db, chave, ct);
        if (pessoa is null)
        {
            pessoa = new Pessoa
            {
                Id = Guid.NewGuid(), NomeRazao = f.Nome, Whatsapp = telefone,
                Observacoes = $"Contato criado pelo formulário da página do imóvel {imovel.Codigo}.",
            };
            pessoa.Papeis.Add(new PessoaPapel { PessoaId = pessoa.Id, Papel = PerfilPessoa.Cliente });
            db.Pessoas.Add(pessoa);
            audit.Registrar(db, null, "pessoas", pessoa.Id, "CRIAR", depois: new { pessoa.NomeRazao, pessoa.Whatsapp, origem = Origem });
        }
        else
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"insert into pessoa_papeis (pessoa_id, papel) values ({pessoa.Id}, {PerfilPessoa.Cliente}) on conflict do nothing", ct);

        var lead = await db.Leads
            .Where(l => l.PessoaId == pessoa.Id && l.ExcluidoEm == null && l.Estagio!.Tipo == TipoEstagio.ABERTO)
            .OrderByDescending(l => l.AtualizadoEm).FirstOrDefaultAsync(ct);
        string? aviso = null;
        Guid? daVez = null;
        if (lead is null)
        {
            var finalidade = imovel.Finalidade == FinalidadeImovel.LOCACAO ? FinalidadeLead.LOCACAO : FinalidadeLead.COMPRA;
            var estagio = await db.FunilEstagios
                .Where(e => e.Funil == CrmService.FunilDa(finalidade) && e.Ativo && e.Tipo == TipoEstagio.ABERTO)
                .OrderBy(e => e.Ordem).FirstOrDefaultAsync(ct);
            if (estagio is null)
            {
                log.LogError("Formulário da página {Slug}: funil {Funil} sem estágio ativo; contato não gravado", slug, finalidade);
                throw new ServicoIndisponivelException("Não foi possível registrar o contato agora. Fale conosco pelo WhatsApp.");
            }
            var ordem = (await db.Leads.Where(x => x.EstagioId == estagio.Id).MinAsync(x => (int?)x.Ordem, ct) ?? 1) - 1;
            daVez = op.Integracoes.CrmRodizio ? await WhatsappService.CorretorDaVez(db, ct) : null;
            lead = new Lead
            {
                Id = Guid.NewGuid(), PessoaId = pessoa.Id, Finalidade = finalidade, EstagioId = estagio.Id, Origem = Origem,
                ImovelSugeridoId = imovel.Id, Ordem = ordem, ResponsavelId = daVez,
            };
            db.Leads.Add(lead);
            db.LeadHistoricoEstagios.Add(new LeadHistoricoEstagio { LeadId = lead.Id, EstagioNovoId = estagio.Id });
            audit.Registrar(db, null, "leads", lead.Id, "CRIAR",
                depois: new { origem = Origem, telefone, imovel_sugerido = imovel.Id, pagina = slug, responsavel_rodizio = daVez });
            aviso = $"Novo lead pela página do imóvel {imovel.Codigo}: {pessoa.NomeRazao} ({telefone}). Confirme o imóvel de interesse no CRM.";
        }

        var agora = DateTime.UtcNow;
        var resumo = $"Formulário da página do imóvel {imovel.Codigo} (/{Imoveis.PaginasImovelService.Caminho(slug)}).\n" +
            $"Nome: {f.Nome}\nTelefone: {f.Telefone}\nMensagem: {f.Mensagem}\n" +
            $"Consentimento LGPD aceito em {DatasBR.DeUtc(agora):dd/MM/yyyy HH:mm}: \"{TextoConsentimento}\"";
        db.LeadInteracoes.Add(new LeadInteracao { LeadId = lead.Id, Canal = CanalInteracao.OUTRO, CriadoEm = agora, Resumo = resumo });
        lead.AtualizadoEm = agora;
        db.PaginasImovelEventos.Add(new PaginaImovelEvento { PaginaId = pagina.Id, Tipo = PaginaImovelEvento.Formulario, Origem = origem });
        audit.Registrar(db, null, "leads", lead.Id, "CONTATO_FORMULARIO", depois: new { pagina = slug, consentimento = TextoConsentimento });
        await db.SaveChangesAsync(ct); // o alerta referencia o lead, que precisa existir antes

        if (aviso is null && lead.ResponsavelId is { } resp)
        {
            // lead já em atendimento: o responsável fica sabendo do novo contato
            db.Alertas.Add(new Alerta
            {
                Id = Guid.NewGuid(), Tipo = TipoAlerta.NOVO_LEAD, UsuarioId = resp, LeadId = lead.Id,
                Mensagem = $"{pessoa.NomeRazao} mandou mensagem pela página do imóvel {imovel.Codigo}. Veja no CRM.",
            });
            await db.SaveChangesAsync(ct);
        }
        if (aviso is not null)
        {
            await WhatsappService.AvisarGestores(db, lead.Id, aviso, ct);
            if (daVez is { } c)
                db.Alertas.Add(new Alerta { Id = Guid.NewGuid(), Tipo = TipoAlerta.NOVO_LEAD, UsuarioId = c, LeadId = lead.Id, Mensagem = aviso + " (rodízio: o lead é seu)" });
            else
                await WhatsappService.AvisarCorretores(db, lead.Id, aviso + " Lead na fila aberta: pegue o atendimento no CRM.", ct);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return true;
    }
}
