using System.Security.Cryptography;
using System.Text;
using Inovabens.Core.Admin;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Locacao;

/// <summary>
/// Locação, fase 7: boleto com Pix no banco (Cora) para a fatura do inquilino e baixa automática quando o banco avisa
/// o pagamento (webhook) ou na conferência de hora em hora. A baixa usa o mesmo recebimento da tela (recibo, financeiro
/// e repasses), registrado em nome do administrador principal.
/// </summary>
public sealed class CobrancaBancariaService(
    IDbContextFactory<InovabensDb> fabrica, AuditService audit, InovabensOpcoes op, ICobrancaBancaria banco,
    LocacaoService locacao, ILogger<CobrancaBancariaService> log)
{
    /// <summary>Uma baixa por vez: o webhook e a conferência podem chegar juntos para o mesmo boleto.</summary>
    private static readonly SemaphoreSlim TravaBaixa = new(1, 1);

    public bool Habilitado => banco.Habilitado;

    /// <summary>Boletos das faturas do contrato (o mais novo primeiro).</summary>
    public async Task<IReadOnlyList<FaturaBoleto>> BoletosAsync(UsuarioAtual u, Guid contratoId)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        return await db.FaturaBoletos.AsNoTracking()
            .Where(b => db.FaturasLocacao.Any(f => f.Id == b.FaturaId && f.ContratoId == contratoId
                && db.Imoveis.VisiveisPara(u).Any(i => i.Id == f.Contrato!.ImovelId)))
            .OrderByDescending(b => b.CriadoEm).ToListAsync();
    }

    /// <summary>
    /// Emite o boleto com Pix do saldo da fatura. Vencida: vence hoje, já com multa e juros até hoje (o banco só soma
    /// juros dali em diante). Reemitir cancela o boleto anterior no banco, para não haver dois pagáveis.
    /// </summary>
    public async Task<FaturaBoleto> EmitirAsync(UsuarioAtual u, Guid faturaId, CancellationToken ct = default)
    {
        u.Exigir(P.LocacaoReceber);
        if (!banco.Habilitado) throw new RegraNegocioException("_", "Configure o Banco Cora em Administração → Integrações para emitir boletos.");
        await using var db = await fabrica.CreateDbContextAsync();
        var f = await db.FaturasLocacao.AsNoTracking().Include(x => x.Itens).Include(x => x.Pagamentos).Include(x => x.Contrato)
            .Where(x => x.Id == faturaId && db.Imoveis.VisiveisPara(u).Any(i => i.Id == x.Contrato!.ImovelId)).SingleOrDefaultAsync(ct)
            ?? throw new NaoEncontradoException("Fatura não encontrada.");
        var c = f.Contrato!;
        if (f.Status != StatusFaturaLocacao.ABERTA) throw new RegraNegocioException("_", "Esta fatura não está em aberto.");
        var saldo = f.Itens.Sum(i => i.Valor) - f.Pagamentos.Where(p => p.EstornadoEm == null).Sum(p => p.Principal);
        if (saldo <= 0) throw new RegraNegocioException("_", "A fatura não tem saldo a cobrar.");
        var pagador = await db.ContratoLocacaoPartes.AsNoTracking()
            .Where(p => p.ContratoId == c.Id && p.Papel == PapelContratoLocacao.LOCATARIO)
            .Select(p => new { p.Pessoa!.NomeRazao, p.Pessoa.CpfCnpj, p.Pessoa.Email }).FirstOrDefaultAsync(ct)
            ?? throw new RegraNegocioException("_", "O contrato não tem locatário.");
        var doc = Texto.Digitos(pagador.CpfCnpj);
        if (doc.Length is not (11 or 14)) throw new RegraNegocioException("_", "Cadastre o CPF ou CNPJ do locatário para emitir o boleto.");
        var codImovel = await db.Imoveis.AsNoTracking().Where(i => i.Id == c.ImovelId).Select(i => i.Codigo).SingleAsync(ct);

        var hoje = DatasBR.Hoje();
        var vencida = f.Vencimento < hoje;
        var valor = saldo + (vencida ? CalculoLocacao.Atraso(saldo, f.Vencimento, hoje, c.MultaAtrasoPct, c.JurosMesPct).Total : 0);
        if (valor < 5) throw new RegraNegocioException("_", "O banco só emite boleto a partir de R$ 5,00.");

        // o anterior sai do banco antes de emitir o novo
        var anteriores = await db.FaturaBoletos.Where(b => b.FaturaId == f.Id && b.CanceladoEm == null && b.Status != "PAID" && b.Status != "CANCELLED").ToListAsync(ct);
        foreach (var a in anteriores) await CancelarNoBanco(db, a, u.Id, ct);

        var pedido = new BoletoPedido($"{c.Codigo}-{f.Competencia:yyyyMM}", pagador.NomeRazao, doc, Texto.Limpo(pagador.Email),
            $"{(f.Rescisao ? "Acerto de rescisão" : "Aluguel")} {f.Competencia:MM/yyyy} - imóvel {codImovel}", decimal.Round(valor, 2),
            vencida ? hoje : f.Vencimento, vencida ? 0 : c.MultaAtrasoPct, c.JurosMesPct, Guid.NewGuid());
        BoletoBanco r;
        try { r = await banco.EmitirAsync(pedido, ct); }
        catch (Exception e) when (e is CoraRecusouException or HttpRequestException or TaskCanceledException)
        {
            throw new RegraNegocioException("_", "O banco não emitiu o boleto: " + e.Message);
        }
        var b = new FaturaBoleto
        {
            FaturaId = f.Id, ExternoId = r.Id, Idempotencia = pedido.Idempotencia, Status = r.Status, Valor = pedido.Valor,
            Vencimento = pedido.Vencimento, LinhaDigitavel = r.LinhaDigitavel, CodigoBarras = r.CodigoBarras, PixCopiaCola = r.PixCopiaCola,
            UrlPdf = r.UrlPdf, CriadoPor = u.Id,
        };
        db.FaturaBoletos.Add(b);
        audit.Registrar(db, u.Id, "fatura_boletos", b.Id, "EMITIR", depois: new { b.FaturaId, b.ExternoId, b.Valor, b.Vencimento });
        await db.SaveChangesAsync(ct);
        return b;
    }

    public async Task CancelarAsync(UsuarioAtual u, Guid boletoId, CancellationToken ct = default)
    {
        u.Exigir(P.LocacaoReceber);
        await using var db = await fabrica.CreateDbContextAsync();
        var b = await db.FaturaBoletos.Where(x => x.Id == boletoId && db.FaturasLocacao.Any(f => f.Id == x.FaturaId
                && db.Imoveis.VisiveisPara(u).Any(i => i.Id == f.Contrato!.ImovelId))).SingleOrDefaultAsync(ct)
            ?? throw new NaoEncontradoException("Boleto não encontrado.");
        if (!b.Ativo) throw new RegraNegocioException("_", "Este boleto já foi pago ou cancelado.");
        await CancelarNoBanco(db, b, u.Id, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task CancelarNoBanco(InovabensDb db, FaturaBoleto b, Guid? usuario, CancellationToken ct)
    {
        try { await banco.CancelarAsync(b.ExternoId, ct); }
        catch (Exception e) when (e is CoraRecusouException or HttpRequestException or TaskCanceledException)
        {
            throw new RegraNegocioException("_", $"O banco não cancelou o boleto {b.ExternoId}: {e.Message}");
        }
        b.Status = "CANCELLED";
        b.CanceladoEm = b.AtualizadoEm = DateTime.UtcNow;
        audit.Registrar(db, usuario, "fatura_boletos", b.Id, "CANCELAR", depois: new { b.ExternoId });
    }

    /// <summary>Endereço que o banco chama ao mudar um boleto (com o token secreto no caminho).</summary>
    public string? UrlWebhook() => op.Integracoes.CoraWebhookToken is { } t ? $"{op.WebUrl.TrimEnd('/')}/api/v1/webhooks/cora/{t}" : null;

    public async Task<string> RegistrarWebhookAsync(UsuarioAtual u, CancellationToken ct = default)
    {
        u.Exigir(P.AdminConfig);
        if (!banco.Habilitado) throw new RegraNegocioException("_", "Configure o Banco Cora antes de registrar o webhook.");
        var url = UrlWebhook() ?? throw new RegraNegocioException("_", "Informe o token do webhook (qualquer texto longo e secreto).");
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new RegraNegocioException("_", "O webhook precisa de endereço https (o do sistema publicado).");
        try { return await banco.RegistrarWebhookAsync(url, ct); }
        catch (Exception e) when (e is CoraRecusouException or HttpRequestException or TaskCanceledException)
        {
            throw new RegraNegocioException("_", "Falha ao registrar: " + e.Message);
        }
    }

    /// <summary>
    /// Aviso do banco. O aviso não é assinado: vale só o token secreto do caminho e, depois, a consulta do boleto no
    /// próprio banco. Devolve false quando o token não confere (a rota responde 404).
    /// </summary>
    public async Task<bool> ProcessarWebhookAsync(string token, string? recursoId, CancellationToken ct = default)
    {
        if (!banco.Habilitado || op.Integracoes.CoraWebhookToken is not { } esperado) return false;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token ?? ""), Encoding.UTF8.GetBytes(esperado))) return false;
        if (string.IsNullOrWhiteSpace(recursoId) || recursoId.Length > 100) return true;
        try { await AtualizarAsync(recursoId, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // a conferência de hora em hora tenta de novo; o banco não precisa reenviar
            log.LogWarning(e, "Webhook do Cora: falha ao atualizar o boleto {Id}", recursoId);
        }
        return true;
    }

    /// <summary>Conferência periódica: boletos em aberto, pagos sem baixa e os de faturas já quitadas ou canceladas.</summary>
    public async Task<int> SincronizarAsync(CancellationToken ct = default)
    {
        if (!banco.Habilitado) return 0;
        await using var db = await fabrica.CreateDbContextAsync();
        var pendentes = await db.FaturaBoletos.AsNoTracking()
            .Where(b => b.CanceladoEm == null && ((b.Status != "PAID" && b.Status != "CANCELLED") || (b.Status == "PAID" && b.PagamentoId == null && b.Erro == null)))
            .Select(b => new { b.Id, b.ExternoId, Fatura = db.FaturasLocacao.Where(f => f.Id == b.FaturaId).Select(f => f.Status).Single() })
            .ToListAsync(ct);
        var n = 0;
        foreach (var p in pendentes)
        {
            try
            {
                var mudou = await AtualizarAsync(p.ExternoId, ct);
                // quitada à mão (ou cancelada) e boleto ainda aberto no banco: cancela para o inquilino não pagar duas vezes
                if (p.Fatura != StatusFaturaLocacao.ABERTA)
                {
                    var b = await db.FaturaBoletos.SingleAsync(x => x.Id == p.Id, ct);
                    if (b.Ativo)
                    {
                        await CancelarNoBanco(db, b, null, ct);
                        await db.SaveChangesAsync(ct);
                        mudou = true;
                    }
                }
                if (mudou) n++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Conferência do Cora: boleto {Id}", p.ExternoId);
            }
        }
        return n;
    }

    /// <summary>
    /// Consulta o boleto no banco e grava a situação. Pago: lança o recebimento na fatura (principal até o saldo; o que
    /// passar vira multa e depois juros). Devolve true quando algo mudou.
    /// </summary>
    public async Task<bool> AtualizarAsync(string externoId, CancellationToken ct = default)
    {
        await TravaBaixa.WaitAsync(ct);
        try
        {
            await using var db = await fabrica.CreateDbContextAsync();
            var b = await db.FaturaBoletos.SingleOrDefaultAsync(x => x.ExternoId == externoId, ct);
            if (b is null || b.PagamentoId is not null) return false;
            var r = await banco.ConsultarAsync(externoId, ct);
            if (r is null) return false;
            var mudou = b.Status != r.Status;
            b.Status = r.Status;
            b.AtualizadoEm = DateTime.UtcNow;
            if (r.Status == "CANCELLED") b.CanceladoEm ??= DateTime.UtcNow;
            if (r.Status == "PAID")
            {
                await Baixar(db, b, r, ct);
                mudou = true;
            }
            await db.SaveChangesAsync(ct);
            return mudou;
        }
        finally { TravaBaixa.Release(); }
    }

    private async Task Baixar(InovabensDb db, FaturaBoleto b, BoletoBanco r, CancellationToken ct)
    {
        var hoje = DatasBR.Hoje();
        var pago = r.TotalPago > 0 ? r.TotalPago : r.Valor;
        var data = r.PagoEm is { } d && d <= hoje ? d : hoje;
        b.PagoEm = data;
        b.ValorPago = pago;
        b.FormaPagamento = r.Forma == "PIX" ? "PIX" : "BOLETO";
        var f = await db.FaturasLocacao.AsNoTracking().Include(x => x.Itens).Include(x => x.Pagamentos).Include(x => x.Contrato)
            .SingleAsync(x => x.Id == b.FaturaId, ct);
        if (f.Status != StatusFaturaLocacao.ABERTA)
        {
            b.Erro = "Pago no banco, mas a fatura já estava quitada ou cancelada: confira (possível pagamento em duplicidade).";
            return;
        }
        var c = f.Contrato!;
        var saldo = f.Itens.Sum(i => i.Valor) - f.Pagamentos.Where(p => p.EstornadoEm == null).Sum(p => p.Principal);
        var principal = Math.Min(saldo, pago);
        var resto = pago - principal;
        var multa = Math.Min(resto, CalculoLocacao.Atraso(saldo, f.Vencimento, data, c.MultaAtrasoPct, c.JurosMesPct).Multa);
        var juros = resto - multa;

        var nomeConta = op.Integracoes.CoraConta;
        var conta = await db.ContasBancarias.AsNoTracking()
            .Where(x => x.Ativa && x.Tipo != TipoContaBancaria.CARTAO_CREDITO && x.Nome.ToLower() == nomeConta.ToLower())
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        if (conta is null && c.Modalidade == ModalidadeLocacao.ADMINISTRACAO)
        {
            b.Erro = $"Pago no banco, mas a conta \"{nomeConta}\" não existe no financeiro: cadastre-a (ou ajuste em Integrações) e dê baixa manual.";
            return;
        }
        var robo = await Robo(db, ct);
        if (robo is null)
        {
            b.Erro = "Pago no banco, mas não há administrador ativo para registrar a baixa: dê baixa manual.";
            return;
        }
        if (principal <= 0)
        {
            b.Erro = "Pago no banco, mas a fatura não tinha saldo: confira (possível pagamento em duplicidade).";
            return;
        }
        try
        {
            b.PagamentoId = await locacao.ReceberAsync(robo, f.Id, new RecebimentoLocacaoForm
            {
                Data = data, Principal = principal, Multa = multa, Juros = juros, ContaBancariaId = conta,
                Forma = b.FormaPagamento == "PIX" ? "Pix (Cora)" : "Boleto (Cora)",
                Observacao = $"Baixa automática do boleto {b.ExternoId}.",
            });
            b.Erro = null;
            audit.Registrar(db, robo.Id, "fatura_boletos", b.Id, "BAIXA_AUTOMATICA", depois: new { b.ExternoId, pago, principal, multa, juros, data });
        }
        catch (RegraNegocioException e)
        {
            b.Erro = "Pago no banco, mas a baixa automática falhou: " + e.Message;
        }
    }

    /// <summary>"Usuário" das rotinas automáticas: o administrador principal, com todas as permissões.</summary>
    internal async Task<UsuarioAtual?> Robo(InovabensDb db, CancellationToken ct = default) => await RoboAsync(db, op, ct);

    internal static async Task<UsuarioAtual?> RoboAsync(InovabensDb db, InovabensOpcoes op, CancellationToken ct = default)
    {
        var adm = await ConfiguracoesService.AdministradorPrincipalAsync(db, op.SeedAdminEmail);
        return adm is null ? null : new UsuarioAtual(adm.Id, adm.Nome, adm.Email, adm.PerfilId, PerfisService.PerfilAdmin, false,
            P.Todas.ToHashSet(), "");
    }
}
