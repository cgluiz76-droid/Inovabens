using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Relatorios;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Locacao;

/// <summary>
/// Locação, fase 3 (docs/plano-locacao.md): reajuste anual pelo índice, renovação/aditivo, avisos de vencimento (sino e
/// agenda), rescisão com acerto e termo de entrega das chaves, e notificação do direito de preferência (art. 27).
/// Toda mudança de valor ou prazo fica no histórico do contrato.
/// </summary>
public sealed partial class LocacaoService
{
    /* ------------------------------ reajuste ------------------------------ */

    /// <summary>Prévia do próximo reajuste com os índices já lançados (BCB ou manual).</summary>
    public async Task<ReajustePrevia> ReajustePreviaAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await Fabrica.CreateDbContextAsync();
        var c = await Contrato(db, u, id, rastrear: false);
        return await Previa(db, c);
    }

    /// <summary>
    /// Aplica o reajuste do aniversário: novo aluguel a partir da competência do aniversário (faturas em aberto sem
    /// recebimento são refeitas). <paramref name="valorManual"/> substitui o calculado (acordo com o locatário).
    /// </summary>
    public async Task AplicarReajusteAsync(UsuarioAtual u, Guid id, decimal? valorManual = null)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await Fabrica.CreateDbContextAsync();
        var c = await Contrato(db, u, id);
        if (c.Status != StatusContratoLocacao.ATIVO) throw new RegraNegocioException("_", "Só o contrato ativo é reajustado.");
        var p = await Previa(db, c);
        if (DatasBR.Hoje() < p.Aniversario.AddDays(-60))
            throw new RegraNegocioException("_", $"O reajuste vale a partir de {Texto.Data(p.Aniversario)}; aplique nos 60 dias antes dessa data.");
        if (valorManual is <= 0) throw new RegraNegocioException("valor", "Valor precisa ser maior que zero.");
        if (p.Percentual is null && valorManual is null)
            throw new RegraNegocioException("_", $"Faltam índices de {string.Join(", ", p.Faltando.Select(m => m.ToString("MM/yyyy")))}: "
                + "sincronize os índices ou lance o mês à mão em Financeiro, ou informe o novo valor.");
        var antes = c.Copia();
        var novo = valorManual ?? p.ValorNovo;
        c.ValorAluguel = novo;
        c.UltimoReajuste = p.Aniversario;
        c.AtualizadoPor = u.Id;
        c.AtualizadoEm = DateTime.UtcNow;
        var desc = p.Percentual is { } pct
            ? $"{Texto.Rotulo(p.Indice)} acumulado de {p.De:MM/yyyy} a {p.Ate:MM/yyyy}: {pct.ToString("0.####", Texto.PtBr)}%"
              + (p.Negativo ? " (negativo: valor mantido)" : "")
            : "Sem índice completo";
        if (valorManual is not null) desc += $"; valor combinado {Texto.Moeda(novo)} (calculado {Texto.Moeda(p.ValorNovo)})";
        db.ContratoLocacaoHistorico.Add(new ContratoLocacaoHistorico
        {
            ContratoId = c.Id, Tipo = TipoHistoricoLocacao.REAJUSTE, Data = p.Aniversario, ValorAnterior = p.ValorAtual, ValorNovo = novo,
            Indice = p.Indice, Percentual = p.Percentual, Descricao = desc, CriadoPor = u.Id,
        });
        await RecalcularAbertas(db, c, p.Aniversario);
        Audit.Registrar(db, u.Id, "contratos_locacao", c.Id, "REAJUSTAR", antes, c);
        await db.SaveChangesAsync();
    }

    private static async Task<ReajustePrevia> Previa(InovabensDb db, ContratoLocacao c)
    {
        var aniversario = c.ProximoReajuste;
        var (de, ate) = CalculoLocacao.PeriodoReajuste(aniversario);
        var serie = await db.IndicesSerie.AsNoTracking()
            .Where(s => s.Codigo == c.IndiceReajuste && s.Competencia >= de && s.Competencia <= ate)
            .ToDictionaryAsync(s => s.Competencia, s => s.VariacaoMensalPct);
        var faltando = Enumerable.Range(0, 12).Select(i => de.AddMonths(i)).Where(m => !serie.ContainsKey(m)).ToList();
        if (faltando.Count > 0) return new(aniversario, c.IndiceReajuste, de, ate, null, c.ValorAluguel, c.ValorAluguel, faltando);
        var (pct, novo) = CalculoLocacao.Reajuste(c.ValorAluguel, serie.OrderBy(x => x.Key).Select(x => x.Value));
        return new(aniversario, c.IndiceReajuste, de, ate, pct, c.ValorAluguel, novo, []);
    }

    /// <summary>
    /// Carta de reajuste ao locatário: de um reajuste já aplicado (<paramref name="historicoId"/>) ou a prévia do próximo,
    /// para enviar antes do aniversário.
    /// </summary>
    public async Task<Arquivo> CartaReajustePdfAsync(UsuarioAtual u, Guid id, Guid? historicoId = null)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await Fabrica.CreateDbContextAsync();
        var d = await ObterAsync(u, id);
        var c = d.Contrato;
        DateOnly aniversario; CodigoIndice indice; decimal? pct; decimal anterior, novo; string periodo;
        if (historicoId is { } hid)
        {
            var h = d.Historico.FirstOrDefault(x => x.Id == hid && x.Tipo == TipoHistoricoLocacao.REAJUSTE)
                ?? throw new NaoEncontradoException("Reajuste não encontrado.");
            var (de, ate) = CalculoLocacao.PeriodoReajuste(h.Data);
            (aniversario, indice, pct, anterior, novo, periodo) = (h.Data, h.Indice ?? c.IndiceReajuste, h.Percentual, h.ValorAnterior ?? 0, h.ValorNovo ?? 0, $"{de:MM/yyyy} a {ate:MM/yyyy}");
        }
        else
        {
            var p = await Previa(db, c);
            if (p.Percentual is null) throw new RegraNegocioException("_", "Faltam índices do período: a carta sai quando o índice estiver completo.");
            (aniversario, indice, pct, anterior, novo, periodo) = (p.Aniversario, p.Indice, p.Percentual, p.ValorAtual, p.ValorNovo, $"{p.De:MM/yyyy} a {p.Ate:MM/yyyy}");
        }
        var locatarios = string.Join(" e ", d.Partes.Where(p => p.Papel == PapelContratoLocacao.LOCATARIO).Select(p => p.Nome));
        var primeiroVenc = CalculoLocacao.Vencimento(CalculoLocacao.MesDe(aniversario), c.DiaVencimento, c.PagamentoAntecipado, c.Inicio);
        var pdf = RelatoriosService.NovoPdf("Comunicado de reajuste do aluguel", $"{d.Codigo} · {d.ImovelCodigo} · {d.ImovelTitulo}",
            $"Inova Bens · emitido em {DatasBR.Agora():dd/MM/yyyy HH:mm}", col =>
            {
                RelatoriosService.Paragrafo(col, $"Prezado(a) {locatarios},");
                RelatoriosService.Paragrafo(col, $"Conforme o contrato de locação {d.Codigo} do imóvel {d.ImovelEndereco ?? d.ImovelCodigo}, "
                    + $"o aluguel é reajustado a cada 12 meses pelo {Texto.Rotulo(indice)}. No aniversário de {Texto.Data(aniversario)}, "
                    + $"o índice acumulado de {periodo} foi de {pct?.ToString("0.##", Texto.PtBr)}%.");
                RelatoriosService.Campos(col, 3, ("Aluguel atual", Texto.Moeda(anterior)), ("Novo aluguel", Texto.Moeda(novo)),
                    ("Vale a partir de", $"{aniversario:MM/yyyy} (vencimento {Texto.Data(primeiroVenc)})"));
                if (pct < 0)
                    RelatoriosService.Paragrafo(col, "Como o índice acumulado ficou negativo, o aluguel permanece com o mesmo valor.");
                RelatoriosService.Paragrafo(col, "Condomínio, IPTU e seguro seguem os valores cobrados pelos respectivos órgãos e administradoras. "
                    + "Ficamos à disposição para qualquer esclarecimento.");
                RelatoriosService.Paragrafo(col, $"\n\nAtenciosamente,\n\n_____________________________________________\nInova Bens · {Texto.Data(DatasBR.Hoje())}");
            });
        Audit.Registrar(db, u.Id, "contratos_locacao", id, "EXPORTAR", depois: new { documento = "carta-reajuste", aniversario });
        await db.SaveChangesAsync();
        return new Arquivo(pdf, $"reajuste-{d.Codigo}-{aniversario:yyyy-MM}.pdf", RelatoriosService.TipoPdf);
    }

    /* ------------------------------ renovação / aditivo ------------------------------ */

    /// <summary>Renovação (mais meses de prazo) ou aditivo (novo valor ou outra cláusula), sempre com registro no histórico.</summary>
    public async Task AditivoAsync(UsuarioAtual u, Guid id, AditivoForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        if (form.Tipo is not (TipoHistoricoLocacao.ADITIVO or TipoHistoricoLocacao.RENOVACAO))
            throw new RegraNegocioException(nameof(form.Tipo), "Escolha renovação ou aditivo.");
        if (form.Tipo == TipoHistoricoLocacao.RENOVACAO && form.MesesAdicionais == 0)
            throw new RegraNegocioException(nameof(form.MesesAdicionais), "Renovação precisa de meses a mais no prazo.");
        await using var db = await Fabrica.CreateDbContextAsync();
        var c = await Contrato(db, u, id);
        if (c.Status != StatusContratoLocacao.ATIVO) throw new RegraNegocioException("_", "Só o contrato ativo recebe aditivo.");
        if (form.Data < c.Inicio) throw new RegraNegocioException(nameof(form.Data), "Data anterior ao início do contrato.");
        if (c.PrazoMeses + form.MesesAdicionais > 600) throw new RegraNegocioException(nameof(form.MesesAdicionais), "Prazo total acima de 600 meses.");
        var antes = c.Copia();
        var valorNovo = form.NovoValor ?? c.ValorAluguel;
        db.ContratoLocacaoHistorico.Add(new ContratoLocacaoHistorico
        {
            ContratoId = c.Id, Tipo = form.Tipo, Data = form.Data!.Value,
            ValorAnterior = c.ValorAluguel, ValorNovo = valorNovo, PrazoAnterior = c.PrazoMeses, PrazoNovo = c.PrazoMeses + form.MesesAdicionais,
            Descricao = form.Descricao.Trim() + (form.MesesAdicionais > 0 ? $" · novo fim {Texto.Data(c.Inicio.AddMonths(c.PrazoMeses + form.MesesAdicionais).AddDays(-1))}" : ""),
            CriadoPor = u.Id,
        });
        c.PrazoMeses += form.MesesAdicionais;
        c.ValorAluguel = valorNovo;
        c.AtualizadoPor = u.Id;
        c.AtualizadoEm = DateTime.UtcNow;
        if (valorNovo != antes.ValorAluguel) await RecalcularAbertas(db, c, form.Data);
        Audit.Registrar(db, u.Id, "contratos_locacao", c.Id, form.Tipo.ToString(), antes, c);
        await db.SaveChangesAsync();
    }

    /* ------------------------------ rescisão ------------------------------ */

    /// <summary>Prévia do acerto da rescisão, sem gravar.</summary>
    public async Task<RescisaoCalculo> SimularRescisaoAsync(UsuarioAtual u, Guid id, RescisaoForm form)
    {
        u.Exigir(P.LocacaoVer);
        Validacao.Validar(form);
        await using var db = await Fabrica.CreateDbContextAsync();
        var c = await Contrato(db, u, id, rastrear: false);
        return CalculoLocacao.Rescisao(c, form);
    }

    /// <summary>
    /// Encerra o contrato na entrega das chaves: o aluguel do último mês vira pró-rata, faturas posteriores sem
    /// recebimento são canceladas, o acerto (multa, aviso, vistoria, caução) vira uma fatura própria, as chaves que estão
    /// fora voltam e o imóvel deixa de estar locado.
    /// </summary>
    public async Task RescindirAsync(UsuarioAtual u, Guid id, RescisaoForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        await using var db = await Fabrica.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var c = await Contrato(db, u, id);
        if (c.Status != StatusContratoLocacao.ATIVO) throw new RegraNegocioException("_", "Só o contrato ativo pode ser encerrado.");
        var entrega = form.EntregaChaves!.Value;
        if (entrega < c.Inicio) throw new RegraNegocioException(nameof(form.EntregaChaves), "A entrega das chaves é anterior ao início.");
        if (await db.FaturasLocacao.AnyAsync(f => f.ContratoId == id && f.Rescisao && f.Status != StatusFaturaLocacao.CANCELADA))
            throw new RegraNegocioException("_", "Este contrato já tem acerto de rescisão.");
        var calc = CalculoLocacao.Rescisao(c, form);
        var antes = c.Copia();
        c.Status = StatusContratoLocacao.ENCERRADO;
        c.EncerradoEm = entrega;
        c.IniciativaRescisao = form.Iniciativa;
        c.AvisoDesocupacaoEm = form.AvisoEm;
        c.MotivoEncerramento = Texto.Limpo(form.Motivo);
        c.AtualizadoPor = u.Id;
        c.AtualizadoEm = DateTime.UtcNow;

        // faturas: depois do mês da entrega, canceladas; o mês da entrega, pró-rata; meses ainda não faturados, gerados
        var mesFim = CalculoLocacao.MesDe(entrega);
        var futuras = await db.FaturasLocacao.Where(f => f.ContratoId == id && f.Status == StatusFaturaLocacao.ABERTA && f.Competencia > mesFim
            && !f.Rescisao && !f.Pagamentos.Any(p => p.EstornadoEm == null)).ToListAsync();
        foreach (var f in futuras)
        {
            f.Status = StatusFaturaLocacao.CANCELADA;
            f.CanceladaEm = DateTime.UtcNow;
            f.MotivoCancelamento = $"Contrato encerrado em {Texto.Data(entrega)}";
        }
        await RecalcularAbertas(db, c, mesFim);
        await GerarFaturas(db, c, DateOnly.MaxValue);

        if (calc.Itens.Count > 0)
        {
            var acerto = new FaturaLocacao
            {
                ContratoId = c.Id, Competencia = mesFim, Vencimento = form.VencimentoAcerto ?? entrega.AddDays(5), Rescisao = true,
                // caução cobriu tudo: nada a receber
                Status = calc.TotalAcerto <= 0 ? StatusFaturaLocacao.PAGA : StatusFaturaLocacao.ABERTA,
            };
            acerto.Itens.AddRange(calc.Itens.Select((i, k) => new FaturaLocacaoItem { FaturaId = acerto.Id, Ordem = k + 1, Tipo = i.Tipo, Descricao = i.Descricao, Valor = i.Valor }));
            db.FaturasLocacao.Add(acerto);
        }

        // chaves que estão fora (com o locatário ou outra pessoa) voltam para a imobiliária
        var devolvidas = 0;
        if (form.DevolverChaves)
        {
            var abertos = await db.ChaveMovimentos.Where(m => m.DevolvidaEm == null
                && db.Chaves.Any(ch => ch.Id == m.ChaveId && ch.ImovelId == c.ImovelId && ch.ExcluidoEm == null)).ToListAsync();
            foreach (var m in abertos)
            {
                m.DevolvidaEm = DateTime.UtcNow;
                m.DevolucaoRegistradaPor = u.Id;
                m.ObservacaoDevolucao = $"Entrega das chaves · rescisão {c.Codigo}";
                Audit.Registrar(db, u.Id, "chave_movimentos", m.Id, "RECEBER", depois: new { m.DevolvidaEm, contrato = c.Codigo });
            }
            devolvidas = abertos.Count;
        }

        // imóvel volta a ficar vago; anunciar de novo para alugar, se pedido
        var im = await db.Imoveis.SingleAsync(i => i.Id == c.ImovelId);
        var imAntes = new { im.Situacao, im.Finalidade, im.PrecoLocacao };
        if (im.Situacao == SituacaoImovel.LOCADO) im.Situacao = SituacaoImovel.DISPONIVEL;
        if (form.AnunciarLocacao)
        {
            if (im.Finalidade == FinalidadeImovel.VENDA) im.Finalidade = FinalidadeImovel.AMBOS;
            im.PrecoLocacao ??= c.ValorAluguel;
            if (im.Situacao is not (SituacaoImovel.DISPONIVEL)) im.Situacao = SituacaoImovel.DISPONIVEL;
        }
        if (imAntes.Situacao != im.Situacao || imAntes.Finalidade != im.Finalidade || imAntes.PrecoLocacao != im.PrecoLocacao)
            Audit.Registrar(db, u.Id, "imoveis", im.Id, "EDITAR", imAntes, new { im.Situacao, im.Finalidade, im.PrecoLocacao, contrato = c.Codigo });

        var linhas = new List<string> { $"Entrega das chaves em {Texto.Data(entrega)} · iniciativa: {Texto.Rotulo(form.Iniciativa)}" };
        if (form.AvisoEm is { } av) linhas.Add($"Aviso de desocupação em {Texto.Data(av)}");
        linhas.AddRange(calc.Itens.Select(i => $"{i.Descricao}: {Texto.Moeda(i.Valor)}"));
        if (calc.Caucao > 0) linhas.Add($"Caução corrigida {Texto.Moeda(calc.Caucao)}; a devolver ao locatário {Texto.Moeda(calc.ADevolver)}");
        if (devolvidas > 0) linhas.Add($"{devolvidas} chave(s) devolvida(s) no controle de chaves");
        if (c.MotivoEncerramento is { } mot) linhas.Add($"Motivo: {mot}");
        db.ContratoLocacaoHistorico.Add(new ContratoLocacaoHistorico
        {
            ContratoId = c.Id, Tipo = TipoHistoricoLocacao.RESCISAO, Data = entrega, ValorAnterior = calc.Debitos, ValorNovo = calc.ADevolver,
            Descricao = string.Join("\n", linhas), CriadoPor = u.Id,
        });
        Audit.Registrar(db, u.Id, "contratos_locacao", c.Id, "ENCERRAR", antes, c);
        await db.SaveChangesAsync();
        await ResolverAvisos(db, c.Id, null);
        await tx.CommitAsync();
    }

    /// <summary>Termo de rescisão e entrega das chaves (com o acerto e a devolução da caução).</summary>
    public async Task<Arquivo> TermoRescisaoPdfAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.LocacaoVer);
        var d = await ObterAsync(u, id);
        var c = d.Contrato;
        var h = d.Historico.FirstOrDefault(x => x.Tipo == TipoHistoricoLocacao.RESCISAO)
            ?? throw new RegraNegocioException("_", "O contrato ainda não foi encerrado.");
        var acerto = d.Faturas.FirstOrDefault(f => f.Rescisao && f.Status != StatusFaturaLocacao.CANCELADA);
        var abertas = d.Faturas.Where(f => !f.Rescisao && f.Saldo > 0).ToList();
        string Nomes(PapelContratoLocacao p) => string.Join(", ", d.Partes.Where(x => x.Papel == p)
            .Select(x => x.CpfCnpj is { } doc ? $"{x.Nome} ({Formatos.Documento(doc)})" : x.Nome)) is { Length: > 0 } s ? s : "—";
        var pdf = RelatoriosService.NovoPdf("Termo de rescisão e entrega das chaves", $"{d.Codigo} · {d.ImovelCodigo} · {d.ImovelTitulo}",
            $"Inova Bens · emitido em {DatasBR.Agora():dd/MM/yyyy HH:mm}", col =>
            {
                RelatoriosService.Campos(col, 2, ("Locador(es)", Nomes(PapelContratoLocacao.LOCADOR)), ("Locatário(s)", Nomes(PapelContratoLocacao.LOCATARIO)),
                    ("Imóvel", d.ImovelEndereco ?? d.ImovelCodigo), ("Contrato", $"{d.Codigo} · início {Texto.Data(c.Inicio)} · prazo {c.PrazoMeses} meses"),
                    ("Entrega das chaves", Texto.Data(h.Data)), ("Iniciativa", c.IniciativaRescisao is { } ini ? Texto.Rotulo(ini) : "—"));
                RelatoriosService.Paragrafo(col, $"As partes declaram encerrada a locação do imóvel acima em {Texto.Data(h.Data)}, data em que "
                    + "o locatário devolveu as chaves, no estado descrito na vistoria de saída.");
                RelatoriosService.Secao(col, "Acerto da rescisão");
                if (acerto is null) RelatoriosService.Paragrafo(col, "Sem multa, aviso prévio, débitos de vistoria ou outros encargos.");
                else RelatoriosService.Tabela(col, [("Descrição", 4), ("Valor", 1.4f)],
                    [.. acerto.Itens.Select(i => new[] { i.Descricao, Texto.Moeda(i.Valor) }), ["Total do acerto", Texto.Moeda(acerto.Total)]], alinharUltimaDireita: true);
                if (h.ValorNovo is > 0)
                    RelatoriosService.Paragrafo(col, $"Caução a devolver ao locatário: {Texto.Moeda(h.ValorNovo.Value)} ({Extenso.Reais(h.ValorNovo.Value)}).");
                if (abertas.Count > 0)
                {
                    RelatoriosService.Secao(col, "Aluguéis e encargos em aberto");
                    RelatoriosService.Tabela(col, [("Referência", 2), ("Vencimento", 1.4f), ("Saldo", 1.4f)],
                        [.. abertas.Select(f => new[] { f.Competencia.ToString("MM/yyyy"), Texto.Data(f.Vencimento), Texto.Moeda(f.Saldo) })], alinharUltimaDireita: true);
                }
                RelatoriosService.Paragrafo(col, "Quitados os valores acima, as partes dão plena quitação das obrigações do contrato, "
                    + "ressalvados vícios ocultos não apontados na vistoria de saída.");
                RelatoriosService.Paragrafo(col, "\n\n_____________________________________________\nLocador / Inova Bens (administradora)"
                    + "\n\n\n_____________________________________________\nLocatário");
            });
        await using var db = await Fabrica.CreateDbContextAsync();
        Audit.Registrar(db, u.Id, "contratos_locacao", id, "EXPORTAR", depois: new { documento = "termo-rescisao" });
        await db.SaveChangesAsync();
        return new Arquivo(pdf, $"termo-rescisao-{d.Codigo}.pdf", RelatoriosService.TipoPdf);
    }

    /* ------------------------------ direito de preferência (art. 27) ------------------------------ */

    /// <summary>Registra a notificação ao locatário (preço e condições da venda); ele tem 30 dias para aceitar.</summary>
    public async Task NotificarPreferenciaAsync(UsuarioAtual u, Guid id, PreferenciaForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        await using var db = await Fabrica.CreateDbContextAsync();
        var c = await Contrato(db, u, id);
        if (c.Status != StatusContratoLocacao.ATIVO) throw new RegraNegocioException("_", "Só o contrato ativo tem direito de preferência.");
        var antes = c.Copia();
        var hoje = DatasBR.Hoje();
        c.PreferenciaNotificadaEm = hoje;
        c.PreferenciaValor = form.Valor;
        c.PreferenciaCondicoes = form.Condicoes.Trim();
        c.PreferenciaResposta = null;
        c.AtualizadoPor = u.Id;
        c.AtualizadoEm = DateTime.UtcNow;
        db.ContratoLocacaoHistorico.Add(new ContratoLocacaoHistorico
        {
            ContratoId = c.Id, Tipo = TipoHistoricoLocacao.PREFERENCIA, Data = hoje, ValorNovo = form.Valor,
            Descricao = $"Notificação de preferência: {Texto.Moeda(form.Valor!.Value)} · {c.PreferenciaCondicoes} · prazo até {Texto.Data(c.PreferenciaPrazo!.Value)}",
            CriadoPor = u.Id,
        });
        Audit.Registrar(db, u.Id, "contratos_locacao", c.Id, "PREFERENCIA", antes, c);
        await db.SaveChangesAsync();
    }

    public async Task ResponderPreferenciaAsync(UsuarioAtual u, Guid id, RespostaPreferencia resposta)
    {
        u.Exigir(P.LocacaoEditar);
        await using var db = await Fabrica.CreateDbContextAsync();
        var c = await Contrato(db, u, id);
        if (c.PreferenciaNotificadaEm is null) throw new RegraNegocioException("_", "O locatário ainda não foi notificado.");
        var antes = c.Copia();
        c.PreferenciaResposta = resposta;
        c.AtualizadoPor = u.Id;
        c.AtualizadoEm = DateTime.UtcNow;
        var texto = resposta switch
        {
            RespostaPreferencia.ACEITA => "Locatário aceitou comprar nas condições notificadas",
            RespostaPreferencia.RECUSADA => "Locatário recusou a compra: imóvel livre para vender a terceiro nas mesmas condições",
            _ => "Prazo de 30 dias terminou sem resposta (caducou o direito de preferência)",
        };
        db.ContratoLocacaoHistorico.Add(new ContratoLocacaoHistorico
        {
            ContratoId = c.Id, Tipo = TipoHistoricoLocacao.PREFERENCIA, Data = DatasBR.Hoje(), ValorNovo = c.PreferenciaValor, Descricao = texto, CriadoPor = u.Id,
        });
        Audit.Registrar(db, u.Id, "contratos_locacao", c.Id, "PREFERENCIA", antes, c);
        await db.SaveChangesAsync();
        await ResolverAvisos(db, c.Id, TipoAvisoLocacao.PREFERENCIA);
    }

    /// <summary>Notificação do direito de preferência (art. 27): preço, condições e prazo de 30 dias.</summary>
    public async Task<Arquivo> NotificacaoPreferenciaPdfAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.LocacaoVer);
        var d = await ObterAsync(u, id);
        var c = d.Contrato;
        if (c.PreferenciaNotificadaEm is not { } em) throw new RegraNegocioException("_", "Registre a notificação antes de gerar o documento.");
        var locatarios = string.Join(" e ", d.Partes.Where(p => p.Papel == PapelContratoLocacao.LOCATARIO).Select(p => p.Nome));
        var locadores = string.Join(", ", d.Partes.Where(p => p.Papel == PapelContratoLocacao.LOCADOR).Select(p => p.Nome));
        var pdf = RelatoriosService.NovoPdf("Notificação de direito de preferência", $"{d.Codigo} · {d.ImovelCodigo} · {d.ImovelTitulo}",
            $"Inova Bens · emitido em {DatasBR.Agora():dd/MM/yyyy HH:mm}", col =>
            {
                RelatoriosService.Paragrafo(col, $"Prezado(a) {locatarios},");
                RelatoriosService.Paragrafo(col, $"Nos termos do art. 27 da Lei 8.245/91, comunicamos que o(s) proprietário(s) {(locadores.Length > 0 ? locadores : "")} "
                    + $"pretende(m) vender o imóvel que V. Sa. ocupa ({d.ImovelEndereco ?? d.ImovelCodigo}), objeto do contrato {d.Codigo}, "
                    + "e oferecemos a V. Sa. a preferência na compra, em igualdade de condições com terceiros.");
                RelatoriosService.Campos(col, 2, ("Preço", Texto.Moeda(c.PreferenciaValor ?? 0)), ("Prazo para resposta", $"até {Texto.Data(c.PreferenciaPrazo!.Value)} (30 dias)"));
                RelatoriosService.Secao(col, "Condições do negócio");
                RelatoriosService.Paragrafo(col, c.PreferenciaCondicoes);
                RelatoriosService.Paragrafo(col, "A documentação do imóvel está à disposição para consulta na Inova Bens. A falta de manifestação "
                    + "inequívoca e integral dentro do prazo faz caducar o direito de preferência (art. 28).");
                RelatoriosService.Paragrafo(col, $"\n\n_____________________________________________\nInova Bens · {Texto.Data(em)}"
                    + "\n\n\nCiente em ____/____/________\n_____________________________________________\nLocatário");
            });
        await using var db = await Fabrica.CreateDbContextAsync();
        Audit.Registrar(db, u.Id, "contratos_locacao", id, "EXPORTAR", depois: new { documento = "notificacao-preferencia" });
        await db.SaveChangesAsync();
        return new Arquivo(pdf, $"preferencia-{d.Codigo}.pdf", RelatoriosService.TipoPdf);
    }

    /* ------------------------------ vencimentos: sino e agenda ------------------------------ */

    /// <summary>Próximos vencimentos (90 dias) e os já vencidos que pedem providência, dos contratos ativos visíveis.</summary>
    public async Task<IReadOnlyList<VencimentoLocacao>> VencimentosAsync(UsuarioAtual u)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await Fabrica.CreateDbContextAsync();
        var hoje = DatasBR.Hoje();
        var contratos = await db.ContratosLocacao.AsNoTracking().Include(c => c.Imovel)
            .Where(c => c.Status == StatusContratoLocacao.ATIVO && db.Imoveis.VisiveisPara(u).Any(i => i.Id == c.ImovelId)).ToListAsync();
        return [.. contratos.SelectMany(c => CalculoLocacao.Eventos(c)
                .Where(e => e.Data <= hoje.AddDays(90) && (e.Tipo == TipoAvisoLocacao.REAJUSTE || e.Data >= hoje.AddDays(-30)))
                .Select(e => new VencimentoLocacao(c.Id, c.Codigo, c.Imovel!.Codigo, e.Tipo, e.Data, MensagemAviso(c, c.Imovel.Codigo, e.Tipo, e.Data))))
            .OrderBy(v => v.Data)];
    }

    /// <summary>
    /// Rotina diária: cria os avisos devidos (sino de quem tem locacao:ver e, no primeiro aviso do evento, uma tarefa na
    /// agenda do responsável pelo contrato) e resolve os avisos de eventos que já foram tratados.
    /// </summary>
    public async Task<int> VerificarVencimentosAsync(DateOnly? hoje = null, CancellationToken ct = default)
    {
        await using var db = await Fabrica.CreateDbContextAsync(ct);
        var dia = hoje ?? DatasBR.Hoje();
        var contratos = await db.ContratosLocacao.AsNoTracking().Include(c => c.Imovel)
            .Where(c => c.Status == StatusContratoLocacao.ATIVO).ToListAsync(ct);
        var usuarios = await db.Usuarios.Where(x => x.Ativo && x.ExcluidoEm == null
                && db.PerfilPermissoes.Any(pp => pp.PerfilId == x.PerfilId && pp.Permissao!.Codigo == P.LocacaoVer))
            .Select(x => x.Id).ToListAsync(ct);
        var criados = 0;
        foreach (var c in contratos)
        {
            foreach (var (tipo, data, ant) in CalculoLocacao.AvisosDevidos(c, dia))
            {
                var doEvento = await db.LocacaoAvisos.Where(a => a.ContratoId == c.Id && a.Tipo == tipo && a.DataEvento == data).ToListAsync(ct);
                if (doEvento.Any(a => a.Antecedencia == ant)) continue;
                var aviso = new LocacaoAviso { ContratoId = c.Id, Tipo = tipo, DataEvento = data, Antecedencia = (short)ant };
                aviso.CompromissoId = doEvento.Select(a => a.CompromissoId).FirstOrDefault(x => x != null)
                    ?? TarefaNaAgenda(db, c, tipo, data, usuarios);
                db.LocacaoAvisos.Add(aviso);
                // um alerta aberto por evento: o aviso mais próximo substitui o anterior
                var anteriores = doEvento.Select(a => (Guid?)a.Id).ToList();
                await db.Alertas.Where(a => anteriores.Contains(a.LocacaoAvisoId) && a.ResolvidoEm == null)
                    .ExecuteUpdateAsync(x => x.SetProperty(a => a.ResolvidoEm, DateTime.UtcNow), ct);
                var msg = MensagemAviso(c, c.Imovel!.Codigo, tipo, data);
                foreach (var uid in usuarios)
                    db.Alertas.Add(new Alerta { Id = Guid.NewGuid(), Tipo = TipoAlerta.LOCACAO_VENCIMENTO, UsuarioId = uid, LocacaoAvisoId = aviso.Id, Mensagem = msg });
                criados++;
            }
        }
        await db.SaveChangesAsync(ct);

        // eventos tratados (reajuste aplicado, renovado, encerrado, garantia renovada) ou fora da janela: alerta resolvido
        var abertos = await db.LocacaoAvisos.AsNoTracking()
            .Where(a => db.Alertas.Any(al => al.LocacaoAvisoId == a.Id && al.ResolvidoEm == null)).ToListAsync(ct);
        foreach (var g in abertos.GroupBy(a => a.ContratoId))
        {
            var c = contratos.FirstOrDefault(x => x.Id == g.Key);
            var validos = c is null ? [] : CalculoLocacao.AvisosDevidos(c, dia).Select(e => (e.Tipo, e.Data)).ToHashSet();
            var resolver = g.Where(a => !validos.Contains((a.Tipo, a.DataEvento))).Select(a => (Guid?)a.Id).ToList();
            if (resolver.Count > 0)
                await db.Alertas.Where(a => resolver.Contains(a.LocacaoAvisoId) && a.ResolvidoEm == null)
                    .ExecuteUpdateAsync(x => x.SetProperty(a => a.ResolvidoEm, DateTime.UtcNow), ct);
        }
        return criados;
    }

    private static string MensagemAviso(ContratoLocacao c, string imovel, TipoAvisoLocacao tipo, DateOnly data) => tipo switch
    {
        TipoAvisoLocacao.REAJUSTE => $"Reajuste anual do contrato {c.Codigo} ({imovel}) em {Texto.Data(data)} pelo {Texto.Rotulo(c.IndiceReajuste)}: confira a prévia, envie a carta e aplique.",
        TipoAvisoLocacao.FIM => $"Prazo do contrato {c.Codigo} ({imovel}) termina em {Texto.Data(data)}: renove, faça aditivo ou prepare a rescisão.",
        TipoAvisoLocacao.GARANTIA => $"Garantia do contrato {c.Codigo} ({imovel}, {Texto.Rotulo(c.Garantia)}) vence em {Texto.Data(data)}: peça a renovação.",
        TipoAvisoLocacao.SEGURO_INCENDIO => $"Seguro incêndio do contrato {c.Codigo} ({imovel}) vence em {Texto.Data(data)}: peça a renovação.",
        _ => $"Prazo do direito de preferência do contrato {c.Codigo} ({imovel}) termina em {Texto.Data(data)}: registre a resposta do locatário.",
    };

    /// <summary>Tarefa no dia do evento para quem criou o contrato (ou o primeiro usuário com acesso à locação).</summary>
    private static Guid? TarefaNaAgenda(InovabensDb db, ContratoLocacao c, TipoAvisoLocacao tipo, DateOnly data, IReadOnlyList<Guid> usuarios)
    {
        Guid? resp = c.CriadoPor is { } cp && usuarios.Contains(cp) ? cp : usuarios.FirstOrDefault();
        if (resp is null || resp == Guid.Empty) return null;
        var inicio = DatasBR.ParaUtc(data.ToDateTime(new TimeOnly(9, 0)));
        var titulo = tipo switch
        {
            TipoAvisoLocacao.REAJUSTE => $"Reajuste do aluguel · {c.Codigo}",
            TipoAvisoLocacao.FIM => $"Fim do prazo da locação · {c.Codigo}",
            TipoAvisoLocacao.GARANTIA => $"Renovar garantia da locação · {c.Codigo}",
            TipoAvisoLocacao.SEGURO_INCENDIO => $"Renovar seguro incêndio · {c.Codigo}",
            _ => $"Prazo da preferência na venda · {c.Codigo}",
        };
        var comp = new Compromisso
        {
            Id = Guid.NewGuid(), ImovelId = c.ImovelId, Tipo = TipoCompromisso.TAREFA, Titulo = titulo,
            Descricao = MensagemAviso(c, c.Imovel?.Codigo ?? "", tipo, data), InicioEm = inicio, FimEm = inicio.AddMinutes(30), ResponsavelId = resp.Value,
        };
        db.Compromissos.Add(comp);
        return comp.Id;
    }

    /// <summary>Resolve os alertas abertos do contrato (todos, ou só de um tipo de evento).</summary>
    private static Task ResolverAvisos(InovabensDb db, Guid contratoId, TipoAvisoLocacao? tipo)
    {
        var avisos = db.LocacaoAvisos.Where(a => a.ContratoId == contratoId && (tipo == null || a.Tipo == tipo)).Select(a => (Guid?)a.Id);
        return db.Alertas.Where(a => avisos.Contains(a.LocacaoAvisoId) && a.ResolvidoEm == null)
            .ExecuteUpdateAsync(x => x.SetProperty(a => a.ResolvidoEm, DateTime.UtcNow));
    }
}
