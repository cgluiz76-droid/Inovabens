using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inovabens.Core.Crm;

/// <summary>Uma mensagem tirada do aviso (webhook) da Cloud API: do cliente (ENTRADA) ou do celular da empresa (SAIDA).</summary>
public sealed record MensagemWhatsapp(string Id, string Telefone, string? Nome, bool Entrada, string Tipo, string? Texto, DateTime EnviadaEm);

public sealed record ResultadoWhatsapp(int Mensagens, int LeadsCriados, int Repetidas);

/// <summary>
/// WhatsApp Business no CRM. O webhook da Cloud API (direto da Meta ou repassado por um parceiro) traz as mensagens;
/// cada mensagem de um cliente sem lead aberto cria contato + lead na primeira coluna do funil, com origem "WhatsApp",
/// e avisa os gestores no sino. Com lead aberto, a mensagem só entra no histórico. O usuário vincula o imóvel no CRM.
/// </summary>
public sealed partial class WhatsappService(
    IDbContextFactory<InovabensDb> fabrica, InovabensOpcoes op, AuditService audit, ILogger<WhatsappService> log)
{
    public const string Origem = "WhatsApp";
    private const int TamanhoResumo = 2000;

    public bool Configurado => op.Integracoes.WhatsappVerifyToken is not null || op.Integracoes.WhatsappAppSecret is not null;

    /* --------------------------- Autenticação --------------------------- */

    /// <summary>Verificação do webhook pela Meta (GET com hub.mode=subscribe): devolve o desafio se o token confere.</summary>
    public string? Desafio(string? modo, string? token, string? desafio) =>
        modo == "subscribe" && Iguais(token, op.Integracoes.WhatsappVerifyToken) ? desafio : null;

    /// <summary>
    /// O aviso é legítimo se vier assinado com o App Secret (cabeçalho X-Hub-Signature-256, envio direto da Meta) ou,
    /// para parceiros que repassam as mensagens, com o token de verificação na URL.
    /// </summary>
    public bool Autentico(byte[] corpo, string? assinatura, string? tokenUrl)
    {
        var i = op.Integracoes;
        if (i.WhatsappAppSecret is { } segredo && assinatura is { } a && a.StartsWith("sha256=", StringComparison.Ordinal))
        {
            var esperado = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(segredo), corpo));
            if (Iguais(a["sha256=".Length..].ToLowerInvariant(), esperado)) return true;
        }
        return tokenUrl is not null && Iguais(tokenUrl, i.WhatsappVerifyToken);
    }

    private static bool Iguais(string? a, string? b) =>
        a is not null && b is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /* ----------------------------- Leitura ----------------------------- */

    /// <summary>Mensagens do aviso: campo "messages" (do cliente) e "smb_message_echoes" (enviadas pelo app do celular).</summary>
    public static IReadOnlyList<MensagemWhatsapp> Ler(JsonElement raiz, string? numeroPermitido = null)
    {
        var lista = new List<MensagemWhatsapp>();
        if (!raiz.TryGetProperty("entry", out var entradas) || entradas.ValueKind != JsonValueKind.Array) return lista;
        foreach (var entrada in entradas.EnumerateArray())
        {
            if (!entrada.TryGetProperty("changes", out var mudancas) || mudancas.ValueKind != JsonValueKind.Array) continue;
            foreach (var m in mudancas.EnumerateArray())
            {
                var campo = Str(m, "field");
                if (!m.TryGetProperty("value", out var v)) continue;
                if (numeroPermitido is not null && v.TryGetProperty("metadata", out var meta) && Str(meta, "phone_number_id") is { } numero
                    && numero != numeroPermitido) continue;

                var nomes = new Dictionary<string, string>();
                if (v.TryGetProperty("contacts", out var contatos) && contatos.ValueKind == JsonValueKind.Array)
                    foreach (var c in contatos.EnumerateArray())
                        if (Str(c, "wa_id") is { } wa && c.TryGetProperty("profile", out var perfil) && Str(perfil, "name") is { } nome)
                            nomes[wa] = nome;

                if (campo == "messages" && v.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                    foreach (var x in msgs.EnumerateArray())
                        if (Str(x, "id") is { } id && Str(x, "from") is { } de)
                            lista.Add(new MensagemWhatsapp(id, de, nomes.GetValueOrDefault(de), true, Str(x, "type") ?? "text", Conteudo(x), Quando(x)));

                if (campo == "smb_message_echoes" && v.TryGetProperty("message_echoes", out var ecos) && ecos.ValueKind == JsonValueKind.Array)
                    foreach (var x in ecos.EnumerateArray())
                        if (Str(x, "id") is { } id && Str(x, "to") is { } para)
                            lista.Add(new MensagemWhatsapp(id, para, nomes.GetValueOrDefault(para), false, Str(x, "type") ?? "text", Conteudo(x), Quando(x)));
            }
        }
        return lista;
    }

    private static string? Str(JsonElement e, string nome) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static DateTime Quando(JsonElement x) =>
        long.TryParse(Str(x, "timestamp"), out var s) ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : DateTime.UtcNow;

    /// <summary>Texto legível de qualquer tipo de mensagem (anexos viram uma descrição; o arquivo fica no WhatsApp).</summary>
    private static string? Conteudo(JsonElement x)
    {
        var tipo = Str(x, "type");
        string? Sub(string obj, string campo) => x.TryGetProperty(obj, out var o) ? Str(o, campo) : null;
        return tipo switch
        {
            "text" => Sub("text", "body"),
            "button" => Sub("button", "text"),
            "interactive" => x.TryGetProperty("interactive", out var i)
                ? (i.TryGetProperty("button_reply", out var b) ? Str(b, "title") : i.TryGetProperty("list_reply", out var l) ? Str(l, "title") : null)
                : null,
            "image" => Anexo("📷 Foto", Sub("image", "caption")),
            "video" => Anexo("🎬 Vídeo", Sub("video", "caption")),
            "document" => Anexo("📄 Documento " + Sub("document", "filename"), Sub("document", "caption")),
            "audio" => "🎤 Áudio",
            "sticker" => "Figurinha",
            "location" => x.TryGetProperty("location", out var loc) ? $"📍 Localização {Str(loc, "name") ?? Str(loc, "address") ?? ""}".Trim() : "📍 Localização",
            "contacts" => "👤 Contato compartilhado",
            "reaction" => x.TryGetProperty("reaction", out var r) ? $"Reação {Str(r, "emoji")}" : "Reação",
            _ => $"Mensagem ({tipo})",
        };

        static string Anexo(string rotulo, string? legenda) => legenda is null ? rotulo.Trim() : $"{rotulo.Trim()}: {legenda}";
    }

    /* ---------------------------- Telefones ---------------------------- */

    /// <summary>
    /// Chave de comparação de telefone: no Brasil, DDD + 8 últimos dígitos (o WhatsApp às vezes manda o celular sem o 9,
    /// e o cadastro pode ter +55, parênteses ou traço). Fora do Brasil, todos os dígitos.
    /// </summary>
    public static string? ChaveTelefone(string? telefone)
    {
        var d = new string((telefone ?? "").Where(char.IsAsciiDigit).ToArray());
        if (d.Length is 12 or 13 && d.StartsWith("55")) d = d[2..];
        if (d.Length == 11 && d[2] == '9') return d[..2] + d[3..];
        if (d.Length == 10) return d;
        return d.Length >= 8 ? d : null;
    }

    /// <summary>Telefone para o cadastro: "(17) 98185-0171" no Brasil (põe o 9 do celular se veio sem), senão "+&lt;dígitos&gt;".</summary>
    public static string FormatarTelefone(string waId)
    {
        var d = new string(waId.Where(char.IsAsciiDigit).ToArray());
        if (d.StartsWith("55") && d.Length is 12 or 13)
        {
            var n = d[2..];
            if (n.Length == 10 && n[2] is >= '6' and <= '9') n = n[..2] + "9" + n[2..];
            return n.Length == 11 ? $"({n[..2]}) {n[2..7]}-{n[7..]}" : $"({n[..2]}) {n[2..6]}-{n[6..]}";
        }
        return "+" + d;
    }

    /* ---------------------------- Gravação ---------------------------- */

    /// <summary>Grava as mensagens do aviso (as já recebidas são ignoradas: a Meta reenvia avisos).</summary>
    public async Task<ResultadoWhatsapp> ProcessarAsync(JsonElement aviso, CancellationToken ct = default)
    {
        int novas = 0, leads = 0, repetidas = 0;
        foreach (var m in Ler(aviso, op.Integracoes.WhatsappPhoneNumberId).OrderBy(m => m.EnviadaEm))
        {
            var (gravada, criouLead) = await GravarAsync(m, ct);
            if (!gravada) { repetidas++; continue; }
            novas++;
            if (criouLead) leads++;
        }
        if (novas > 0) log.LogInformation("WhatsApp: {Novas} mensagem(ns), {Leads} lead(s) novo(s)", novas, leads);
        return new ResultadoWhatsapp(novas, leads, repetidas);
    }

    private async Task<(bool Gravada, bool CriouLead)> GravarAsync(MensagemWhatsapp m, CancellationToken ct)
    {
        var chave = ChaveTelefone(m.Telefone);
        if (chave is null) return (false, false);
        await using var db = await fabrica.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // um contato por vez: duas mensagens seguidas de um número novo não criam dois leads
        await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtext({"whatsapp:" + chave}))", ct);
        if (await db.WhatsappMensagens.AnyAsync(x => x.Id == m.Id, ct)) return (false, false);

        var pessoa = await PessoaDoTelefone(db, chave, ct);
        var lead = pessoa is null ? null : await db.Leads
            .Where(l => l.PessoaId == pessoa.Id && l.ExcluidoEm == null && l.Estagio!.Tipo == TipoEstagio.ABERTO)
            .OrderByDescending(l => l.AtualizadoEm).FirstOrDefaultAsync(ct);

        var criouLead = false;
        if (lead is null && m.Entrada)
        {
            if (pessoa is null)
            {
                pessoa = new Pessoa
                {
                    Id = Guid.NewGuid(), NomeRazao = Nome(m), Whatsapp = FormatarTelefone(m.Telefone),
                    Observacoes = "Contato criado por mensagem no WhatsApp.",
                };
                pessoa.Papeis.Add(new PessoaPapel { PessoaId = pessoa.Id, Papel = PerfilPessoa.Cliente });
                db.Pessoas.Add(pessoa);
                audit.Registrar(db, null, "pessoas", pessoa.Id, "CRIAR", depois: new { pessoa.NomeRazao, pessoa.Whatsapp, origem = Origem });
            }
            else
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"insert into pessoa_papeis (pessoa_id, papel) values ({pessoa.Id}, {PerfilPessoa.Cliente}) on conflict do nothing", ct);

            var imovel = await ImovelCitado(db, m.Texto, ct);
            var finalidade = imovel?.Finalidade == FinalidadeImovel.LOCACAO ? FinalidadeLead.LOCACAO : FinalidadeLead.COMPRA;
            var estagio = await db.FunilEstagios
                .Where(e => e.Funil == CrmService.FunilDa(finalidade) && e.Ativo && e.Tipo == TipoEstagio.ABERTO)
                .OrderBy(e => e.Ordem).FirstOrDefaultAsync(ct);
            if (estagio is null)
            {
                log.LogWarning("WhatsApp: funil {Funil} sem estágio ativo; mensagem {Id} guardada sem lead", finalidade, m.Id);
            }
            else
            {
                var ordem = (await db.Leads.Where(x => x.EstagioId == estagio.Id).MinAsync(x => (int?)x.Ordem, ct) ?? 1) - 1;
                var daVez = op.Integracoes.CrmRodizio ? await CorretorDaVez(db, ct) : null;
                lead = new Lead
                {
                    Id = Guid.NewGuid(), PessoaId = pessoa.Id, Finalidade = finalidade, EstagioId = estagio.Id, Origem = Origem,
                    ImovelSugeridoId = imovel?.Id, Ordem = ordem, ResponsavelId = daVez,
                };
                db.Leads.Add(lead);
                db.LeadHistoricoEstagios.Add(new LeadHistoricoEstagio { LeadId = lead.Id, EstagioNovoId = estagio.Id });
                audit.Registrar(db, null, "leads", lead.Id, "CRIAR",
                    depois: new { origem = Origem, telefone = pessoa.Whatsapp, imovel_sugerido = imovel?.Id, responsavel_rodizio = daVez });
                await db.SaveChangesAsync(ct); // o alerta referencia o lead, que precisa existir antes
                var aviso = $"Novo lead pelo WhatsApp: {pessoa.NomeRazao} ({pessoa.Whatsapp ?? pessoa.Telefone})" +
                    (imovel is null ? ". Vincule o imóvel de interesse no CRM." : $", interessado no imóvel {imovel.Codigo}. Confirme o vínculo no CRM.");
                await AvisarGestores(db, lead.Id, aviso, ct);
                if (daVez is { } c)
                    db.Alertas.Add(new Alerta { Id = Guid.NewGuid(), Tipo = TipoAlerta.NOVO_LEAD, UsuarioId = c, LeadId = lead.Id, Mensagem = aviso + " (rodízio: o lead é seu)" });
                else
                    await AvisarCorretores(db, lead.Id, aviso + " Lead na fila aberta: pegue o atendimento no CRM.", ct);
                criouLead = true;
            }
        }

        if (lead is not null)
        {
            var texto = m.Texto ?? $"Mensagem ({m.Tipo})";
            var resumo = (m.Entrada ? "Mensagem recebida: " : "Enviada pelo celular: ") + texto;
            db.LeadInteracoes.Add(new LeadInteracao
            {
                LeadId = lead.Id, Canal = CanalInteracao.WHATSAPP, CriadoEm = m.EnviadaEm,
                Resumo = resumo.Length > TamanhoResumo ? resumo[..TamanhoResumo] + "…" : resumo,
            });
            lead.AtualizadoEm = DateTime.UtcNow;
        }

        db.WhatsappMensagens.Add(new WhatsappMensagem
        {
            Id = m.Id, Telefone = new string(m.Telefone.Where(char.IsAsciiDigit).Take(20).ToArray()),
            NomeContato = m.Nome is { Length: > 200 } n ? n[..200] : m.Nome, Direcao = m.Entrada ? "ENTRADA" : "SAIDA",
            Tipo = m.Tipo.Length > 30 ? m.Tipo[..30] : m.Tipo, Texto = m.Texto, PessoaId = pessoa?.Id, LeadId = lead?.Id, EnviadaEm = m.EnviadaEm,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (true, criouLead);
    }

    private static string Nome(MensagemWhatsapp m)
    {
        var nome = m.Nome?.Trim();
        if (string.IsNullOrEmpty(nome)) return "WhatsApp " + FormatarTelefone(m.Telefone);
        return nome.Length > 200 ? nome[..200] : nome;
    }

    /// <summary>Pessoa ativa cujo WhatsApp ou telefone é o mesmo número (ignorando formato, +55 e o 9 do celular).</summary>
    internal static async Task<Pessoa?> PessoaDoTelefone(InovabensDb db, string chave, CancellationToken ct)
    {
        var final = "%" + chave[^8..];
        var candidatas = await db.Pessoas.FromSqlInterpolated($"""
            select * from pessoas
             where excluido_em is null
               and (regexp_replace(coalesce(whatsapp, ''), '\D', '', 'g') like {final}
                 or regexp_replace(coalesce(telefone, ''), '\D', '', 'g') like {final})
            """).ToListAsync(ct);
        return candidatas.Where(p => ChaveTelefone(p.Whatsapp) == chave || ChaveTelefone(p.Telefone) == chave)
            .OrderBy(p => p.CriadoEm).FirstOrDefault();
    }

    /// <summary>
    /// Imóvel citado na mensagem: a do botão do site diz "imóvel &lt;código&gt; (título)"; também vale o link da ficha
    /// (/site/imovel/&lt;id&gt;) ou um código/referência solto no texto. Vendido ou arquivado não é sugerido.
    /// </summary>
    internal static async Task<Imovel?> ImovelCitado(InovabensDb db, string? texto, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        var ativos = db.Imoveis.AsNoTracking().Where(i => i.ExcluidoEm == null && !CrmService.SituacoesSemInteresse.Contains(i.Situacao));
        if (LinkFicha().Match(texto) is { Success: true } link && Guid.TryParse(link.Groups[1].Value, out var id))
            return await ativos.FirstOrDefaultAsync(i => i.Id == id, ct);

        var termos = new List<string>();
        if (CitacaoSite().Match(texto) is { Success: true } c) termos.Add(c.Groups[1].Value);
        termos.AddRange(Palavras().Matches(texto).Select(x => x.Value).Where(p => p.Any(char.IsDigit)).Take(20));
        foreach (var t in termos.Select(t => t.Trim('.', ',', ';', ':', '!', '?', '(', ')').ToUpperInvariant()).Where(t => t.Length >= 2).Distinct())
        {
            var im = await ativos.Where(i => i.Codigo.ToUpper() == t || (i.Referencia != null && i.Referencia.ToUpper() == t)
                || (i.CodigoAntigo != null && i.CodigoAntigo.ToUpper() == t))
                .FirstOrDefaultAsync(ct);
            if (im is not null) return im;
        }
        return null;
    }

    [GeneratedRegex(@"/site/imovel/([0-9a-fA-F-]{36})")]
    private static partial Regex LinkFicha();

    [GeneratedRegex(@"im[óo]vel\s+(\S+)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex CitacaoSite();

    [GeneratedRegex(@"[\p{L}\d][\p{L}\d\-/\.]*")]
    private static partial Regex Palavras();

    /// <summary>Alerta "novo lead" para quem vê todos os leads e pode editar o CRM (o Corretor só vê os seus).</summary>
    internal static async Task AvisarGestores(InovabensDb db, Guid leadId, string mensagem, CancellationToken ct)
    {
        var destinatarios = await db.Usuarios
            .Where(u => u.Ativo && u.ExcluidoEm == null && !u.Perfil!.EscopoProprio)
            .Where(u => db.PerfilPermissoes.Any(pp => pp.PerfilId == u.PerfilId && pp.Permissao!.Codigo == Permissoes.CrmEditar))
            .Select(u => u.Id).ToListAsync(ct);
        foreach (var id in destinatarios)
            db.Alertas.Add(new Alerta { Id = Guid.NewGuid(), Tipo = TipoAlerta.NOVO_LEAD, UsuarioId = id, LeadId = leadId, Mensagem = mensagem });
    }

    /// <summary>Corretores que atendem leads: perfil com escopo próprio que pode editar o CRM.</summary>
    private static IQueryable<Usuario> Corretores(InovabensDb db) =>
        db.Usuarios.Where(u => u.Ativo && u.ExcluidoEm == null && u.Perfil!.EscopoProprio)
            .Where(u => db.PerfilPermissoes.Any(pp => pp.PerfilId == u.PerfilId && pp.Permissao!.Codigo == Permissoes.CrmEditar));

    /// <summary>
    /// Rodízio: o corretor que recebeu lead do WhatsApp há mais tempo (quem nunca recebeu vem primeiro). Sem corretor
    /// ativo, o lead fica na fila aberta.
    /// </summary>
    internal static async Task<Guid?> CorretorDaVez(InovabensDb db, CancellationToken ct = default)
    {
        var candidatos = await Corretores(db)
            .Select(u => new
            {
                u.Id, u.Nome,
                Ultimo = db.Leads.Where(l => l.ResponsavelId == u.Id && l.Origem == Origem).Max(l => (DateTime?)l.CriadoEm),
            })
            .ToListAsync(ct);
        return candidatos.OrderBy(c => c.Ultimo ?? DateTime.MinValue).ThenBy(c => c.Nome).Select(c => (Guid?)c.Id).FirstOrDefault();
    }

    /// <summary>Fila aberta: todos os corretores são avisados; o primeiro que pegar fica com o lead.</summary>
    internal static async Task AvisarCorretores(InovabensDb db, Guid leadId, string mensagem, CancellationToken ct)
    {
        foreach (var id in await Corretores(db).Select(u => u.Id).ToListAsync(ct))
            db.Alertas.Add(new Alerta { Id = Guid.NewGuid(), Tipo = TipoAlerta.NOVO_LEAD, UsuarioId = id, LeadId = leadId, Mensagem = mensagem });
    }

    /// <summary>Lead ganhou imóvel de interesse: o alerta de "novo lead" se resolve.</summary>
    internal static Task ResolverAlertasDoLead(InovabensDb db, Guid leadId) =>
        db.Alertas.Where(a => a.LeadId == leadId && a.Tipo == TipoAlerta.NOVO_LEAD && a.ResolvidoEm == null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ResolvidoEm, DateTime.UtcNow));
}
