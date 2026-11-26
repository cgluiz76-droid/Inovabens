using ClosedXML.Excel;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Relatorios;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Locacao;

/// <summary>Divisão de um recebimento por natureza: o aluguel (com multa e juros) é rendimento; encargos são repasse de despesa.</summary>
public sealed record PartilhaRecebimento(decimal Aluguel, decimal Condominio, decimal Iptu, decimal Seguro, decimal Outros)
{
    public decimal Encargos => Condominio + Iptu + Seguro + Outros;
    public decimal Total => Aluguel + Encargos;

    /// <summary>Parte de um locador (fator = bruto dele / total); a sobra do arredondamento fica no aluguel.</summary>
    public PartilhaRecebimento Parte(decimal bruto)
    {
        if (Total <= 0) return new(bruto, 0, 0, 0, 0);
        var f = bruto / Total;
        decimal R(decimal v) => decimal.Round(v * f, 2, MidpointRounding.AwayFromZero);
        var (c, i, s, o) = (R(Condominio), R(Iptu), R(Seguro), R(Outros));
        return new(bruto - c - i - s - o, c, i, s, o);
    }
}

/// <summary>
/// Locação, fase 6: documentos de cobrança e quitação, informe de rendimentos do proprietário, planilha da DIMOB para o
/// escritório contábil e o relatório gerencial em Excel. Valores anuais seguem o regime de caixa (data do recebimento),
/// como pedem o carnê-leão, a declaração do IR e a DIMOB.
/// </summary>
public sealed class LocacaoRelatoriosService(IDbContextFactory<InovabensDb> fabrica, AuditService audit, LocacaoService locacao)
{
    /* ------------------------------ cálculo (sem banco) ------------------------------ */

    /// <summary>
    /// Mesmo critério do repasse: o pagamento parcial quita os itens na proporção; o conserto cobrado do inquilino
    /// (MANUTENCAO) reembolsa a imobiliária e fica fora; multa e juros acompanham o aluguel.
    /// </summary>
    public static PartilhaRecebimento Partilha(IReadOnlyList<FaturaLocacaoItem> itens, decimal principal, decimal multa, decimal juros)
    {
        var total = itens.Sum(i => i.Valor);
        var fracao = total <= 0 ? 1 : Math.Min(1, principal / total);
        decimal Soma(params TipoItemFatura[] tipos) =>
            decimal.Round(itens.Where(i => tipos.Contains(i.Tipo)).Sum(i => i.Valor) * fracao, 2, MidpointRounding.AwayFromZero);
        var cond = Soma(TipoItemFatura.CONDOMINIO);
        var iptu = Soma(TipoItemFatura.IPTU);
        var seg = Soma(TipoItemFatura.SEGURO_INCENDIO);
        var outros = Soma(TipoItemFatura.OUTRO, TipoItemFatura.VISTORIA, TipoItemFatura.CAUCAO);
        var bruto = principal + multa + juros - CalculoLocacao.Reembolso(itens, principal);
        return new(bruto - cond - iptu - seg - outros, cond, iptu, seg, outros);
    }

    /// <summary>Faixa de atraso para o relatório de inadimplência.</summary>
    public static string FaixaAtraso(int dias) => dias switch
    {
        <= 30 => "1 a 30 dias",
        <= 60 => "31 a 60 dias",
        <= 90 => "61 a 90 dias",
        _ => "mais de 90 dias",
    };

    /* ------------------------------ rendimentos por locador ------------------------------ */

    private sealed record Rendimento(Guid ContratoId, Guid PessoaId, DateOnly Data, PartilhaRecebimento Valores, decimal Taxa,
        decimal Intermediacao, decimal Manutencao, decimal Liquido);

    /// <summary>Repasses (não estornados) dos recebimentos do ano, já divididos por natureza.</summary>
    private static async Task<List<Rendimento>> Rendimentos(InovabensDb db, UsuarioAtual u, int ano, Guid? pessoaId = null)
    {
        var de = new DateOnly(ano, 1, 1);
        var ate = de.AddYears(1);
        var visiveis = db.ContratosLocacao.Where(c => db.Imoveis.VisiveisPara(u).Any(i => i.Id == c.ImovelId));
        var q = db.RepassesLocacao.AsNoTracking().Where(r => visiveis.Any(c => c.Id == r.ContratoId));
        if (pessoaId is { } pid) q = q.Where(r => r.PessoaId == pid);
        var lista = await q.Join(db.FaturaLocacaoPagamentos.Where(p => p.EstornadoEm == null && p.Data >= de && p.Data < ate),
                r => r.PagamentoId, p => p.Id, (r, p) => new { r, p })
            .ToListAsync();
        var faturaIds = lista.Select(x => x.p.FaturaId).Distinct().ToList();
        var itens = (await db.Set<FaturaLocacaoItem>().AsNoTracking().Where(i => faturaIds.Contains(i.FaturaId)).ToListAsync())
            .GroupBy(i => i.FaturaId).ToDictionary(g => g.Key, g => (IReadOnlyList<FaturaLocacaoItem>)g.ToList());
        return [.. lista.Select(x =>
        {
            var total = Partilha(itens.GetValueOrDefault(x.p.FaturaId) ?? [], x.p.Principal, x.p.Multa, x.p.Juros);
            return new Rendimento(x.r.ContratoId, x.r.PessoaId, x.p.Data, total.Parte(x.r.Bruto), x.r.TaxaAdministracao, x.r.Intermediacao,
                x.r.DescontoManutencao, x.r.Liquido);
        })];
    }

    private sealed record DadosContrato(Guid Id, string Codigo, DateOnly Inicio, Guid ImovelId, string ImovelCodigo, string Endereco, string? Cep,
        string? Cidade, string? Uf, IReadOnlyList<(string Nome, string? Doc, TipoPessoa Tipo)> Locatarios);

    private static async Task<Dictionary<Guid, DadosContrato>> Contratos(InovabensDb db, IReadOnlyCollection<Guid> ids)
    {
        var cs = await db.ContratosLocacao.AsNoTracking().Include(c => c.Imovel).Where(c => ids.Contains(c.Id)).ToListAsync();
        var locs = (await db.ContratoLocacaoPartes.AsNoTracking().Where(p => ids.Contains(p.ContratoId) && p.Papel == PapelContratoLocacao.LOCATARIO)
                .Select(p => new { p.ContratoId, p.Pessoa!.NomeRazao, p.Pessoa.CpfCnpj, p.Pessoa.Tipo }).ToListAsync())
            .GroupBy(p => p.ContratoId).ToDictionary(g => g.Key, g => g.Select(p => (p.NomeRazao, p.CpfCnpj, p.Tipo)).ToList());
        return cs.ToDictionary(c => c.Id, c => new DadosContrato(c.Id, c.Codigo, c.Inicio, c.ImovelId, c.Imovel!.Codigo,
            ManutencaoService.Endereco(c.Imovel), c.Imovel.Cep, c.Imovel.Cidade, c.Imovel.Uf,
            locs.GetValueOrDefault(c.Id) is { } l ? [.. l.Select(x => (x.NomeRazao, x.CpfCnpj, x.Tipo))] : []));
    }

    private static string Doc(string? d) => d is null ? "—" : Formatos.Documento(d);

    /* ------------------------------ informe de rendimentos ------------------------------ */

    /// <summary>Informe anual do proprietário: aluguéis recebidos (rendimento), encargos, taxa de administração (dedutível) e líquido.</summary>
    public async Task<Arquivo> InformeRendimentosPdfAsync(UsuarioAtual u, Guid pessoaId, int ano)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var pessoa = await db.Pessoas.AsNoTracking().Where(p => p.Id == pessoaId).Select(p => new { p.NomeRazao, p.CpfCnpj }).SingleOrDefaultAsync()
            ?? throw new NaoEncontradoException("Pessoa não encontrada.");
        var rend = await Rendimentos(db, u, ano, pessoaId);
        var contratos = await Contratos(db, [.. rend.Select(r => r.ContratoId).Distinct()]);
        var pdf = RelatoriosService.NovoPdf($"Informe de rendimentos de aluguéis · {ano}", pessoa.NomeRazao + (pessoa.CpfCnpj is null ? "" : $" · {Doc(pessoa.CpfCnpj)}"),
            $"Inova Bens · informe gerado em {DatasBR.Agora():dd/MM/yyyy HH:mm}", col =>
            {
                RelatoriosService.Paragrafo(col, $"Valores recebidos pela Inova Bens, como administradora, em nome do locador, de 01/01/{ano} a 31/12/{ano} "
                    + "(regime de caixa: data do pagamento do inquilino). O aluguel inclui multa e juros por atraso. Encargos (condomínio, IPTU, "
                    + "seguro) são reembolso de despesas e não compõem o rendimento. A taxa de administração é dedutível.");
                if (rend.Count == 0) { RelatoriosService.Paragrafo(col, "Nenhum recebimento no ano."); return; }

                RelatoriosService.Secao(col, "Por contrato e fonte pagadora");
                foreach (var g in rend.GroupBy(r => r.ContratoId))
                {
                    var c = contratos[g.Key];
                    RelatoriosService.Campos(col, 3, ("Contrato", $"{c.Codigo} · {c.ImovelCodigo}"), ("Imóvel", c.Endereco),
                        ("Locatário (fonte pagadora)", string.Join("; ", c.Locatarios.Select(l => l.Doc is null ? $"{l.Nome} ({l.Tipo})" : $"{l.Nome} ({Doc(l.Doc)}, {l.Tipo})"))));
                    RelatoriosService.Tabela(col, [("Aluguel recebido", 1), ("Encargos", 1), ("Taxa adm.", 1), ("Intermediação", 1), ("Manutenção", 1), ("Líquido", 1)],
                        [[Texto.Moeda(g.Sum(r => r.Valores.Aluguel)), Texto.Moeda(g.Sum(r => r.Valores.Encargos)), Texto.Moeda(g.Sum(r => r.Taxa)),
                            Texto.Moeda(g.Sum(r => r.Intermediacao)), Texto.Moeda(g.Sum(r => r.Manutencao)), Texto.Moeda(g.Sum(r => r.Liquido))]], colunasDireita: 6);
                    if (c.Locatarios.Any(l => l.Tipo == TipoPessoa.PJ) && pessoa.CpfCnpj?.Length != 14)
                        RelatoriosService.Paragrafo(col, "Locatário pessoa jurídica: o IR é retido na fonte por ele e consta do informe que ele fornece. "
                            + "Nenhuma retenção foi registrada neste sistema; confira com o contador.");
                }

                RelatoriosService.Secao(col, "Mês a mês (carnê-leão)");
                RelatoriosService.Tabela(col, [("Mês", 0.8f), ("Aluguel recebido", 1.2f), ("Taxa adm. + intermediação", 1.4f), ("Encargos", 1), ("Líquido repassado", 1.2f)],
                    [.. rend.GroupBy(r => r.Data.Month).OrderBy(g => g.Key).Select(g => new[] { $"{g.Key:00}/{ano}", Texto.Moeda(g.Sum(r => r.Valores.Aluguel)),
                        Texto.Moeda(g.Sum(r => r.Taxa + r.Intermediacao)), Texto.Moeda(g.Sum(r => r.Valores.Encargos)), Texto.Moeda(g.Sum(r => r.Liquido)) })],
                    colunasDireita: 4);
                RelatoriosService.Campos(col, 4, ("Total de aluguéis", Texto.Moeda(rend.Sum(r => r.Valores.Aluguel))),
                    ("Taxa de administração", Texto.Moeda(rend.Sum(r => r.Taxa))), ("Intermediação", Texto.Moeda(rend.Sum(r => r.Intermediacao))),
                    ("Líquido repassado", Texto.Moeda(rend.Sum(r => r.Liquido))));
                RelatoriosService.Paragrafo(col, "Informe de apoio à declaração de imposto de renda. Confira com seu contador.");
            });
        audit.Registrar(db, u.Id, "repasses_locacao", null, "EXPORTAR", depois: new { relatorio = "informe-rendimentos", pessoa = pessoaId, ano });
        await db.SaveChangesAsync();
        return new Arquivo(pdf, $"informe-rendimentos-{ano}.pdf", RelatoriosService.TipoPdf);
    }

    /// <summary>Locadores com recebimento no ano (para escolher o informe).</summary>
    public async Task<IReadOnlyList<Opcao>> LocadoresAsync(UsuarioAtual u, int ano)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var ids = (await Rendimentos(db, u, ano)).Select(r => r.PessoaId).Distinct().ToList();
        return await db.Pessoas.AsNoTracking().Where(p => ids.Contains(p.Id)).OrderBy(p => p.NomeRazao)
            .Select(p => new Opcao(p.Id, p.NomeRazao)).ToListAsync();
    }

    /* ------------------------------ DIMOB ------------------------------ */

    /// <summary>
    /// Dados da DIMOB (locações administradas, por contrato e locador): aluguel e comissão pagos mês a mês, partes e imóvel.
    /// O escritório contábil transmite pelo programa da Receita; a aba "Conferir" aponta cadastro incompleto.
    /// </summary>
    public async Task<Arquivo> DimobExcelAsync(UsuarioAtual u, int ano)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var rend = await Rendimentos(db, u, ano);
        var contratos = await Contratos(db, [.. rend.Select(r => r.ContratoId).Distinct()]);
        var pessoas = await db.Pessoas.AsNoTracking().Where(p => rend.Select(r => r.PessoaId).Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => new { p.NomeRazao, p.CpfCnpj });

        var colunas = new List<(string, double)>
        {
            ("Contrato", 11), ("Data do contrato", 12), ("CPF/CNPJ locador", 19), ("Locador", 30), ("CPF/CNPJ locatário", 19), ("Locatário", 30),
            ("Tipo do imóvel", 10), ("Endereço", 40), ("CEP", 10), ("Município", 18), ("UF", 5),
        };
        for (var m = 1; m <= 12; m++) colunas.AddRange([($"Aluguel {m:00}", 12), ($"Comissão {m:00}", 12), ($"IR retido {m:00}", 11)]);
        colunas.AddRange([("Total aluguel", 13), ("Total comissão", 13)]);

        var linhas = new List<XLCellValue[]>();
        var conferir = new List<XLCellValue[]>();
        foreach (var g in rend.GroupBy(r => (r.ContratoId, r.PessoaId)).OrderBy(g => contratos[g.Key.ContratoId].Codigo))
        {
            var c = contratos[g.Key.ContratoId];
            var loc = pessoas[g.Key.PessoaId];
            var inq = c.Locatarios.FirstOrDefault();
            var l = new List<XLCellValue>
            {
                c.Codigo, c.Inicio.ToDateTime(TimeOnly.MinValue), Doc(loc.CpfCnpj), loc.NomeRazao, Doc(inq.Doc), inq.Nome ?? "—",
                "Urbano", c.Endereco, c.Cep ?? "", c.Cidade ?? "", c.Uf ?? "",
            };
            for (var m = 1; m <= 12; m++)
            {
                var doMes = g.Where(r => r.Data.Month == m).ToList();
                l.AddRange([doMes.Sum(r => r.Valores.Aluguel), doMes.Sum(r => r.Taxa + r.Intermediacao), 0m]);
            }
            l.AddRange([g.Sum(r => r.Valores.Aluguel), g.Sum(r => r.Taxa + r.Intermediacao)]);
            linhas.Add([.. l]);

            void Falta(string o) => conferir.Add([c.Codigo, loc.NomeRazao, o]);
            if (string.IsNullOrWhiteSpace(loc.CpfCnpj)) Falta("CPF/CNPJ do locador");
            if (inq.Nome is null) Falta("Locatário");
            else if (string.IsNullOrWhiteSpace(inq.Doc)) Falta($"CPF/CNPJ do locatário ({inq.Nome})");
            if (string.IsNullOrWhiteSpace(c.Cep)) Falta("CEP do imóvel");
            if (string.IsNullOrWhiteSpace(c.Cidade) || string.IsNullOrWhiteSpace(c.Uf)) Falta("Município/UF do imóvel");
            if (c.Locatarios.Count > 1) Falta($"Contrato com {c.Locatarios.Count} locatários: a DIMOB aceita um por linha (confira com o contador)");
        }

        using var wb = new XLWorkbook();
        var ws = RelatoriosService.Aba(wb, "Locações", [.. colunas], linhas);
        ws.Column(2).Style.DateFormat.Format = "dd/MM/yyyy";
        ws.Columns(12, colunas.Count).Style.NumberFormat.Format = "#,##0.00";
        RelatoriosService.Aba(wb, "Conferir", [("Contrato", 12), ("Locador", 30), ("Falta no cadastro", 60)],
            conferir.Count > 0 ? conferir : [["—", "—", "Cadastro completo"]]);
        RelatoriosService.Aba(wb, "Leia-me", [("Observação", 120)], [
            [$"DIMOB {ano} (entrega até o último dia útil de fevereiro de {ano + 1}). Locações administradas pela Inova Bens."],
            ["Aluguel: valor pago pelo inquilino no mês (com multa e juros), na parte do locador. Encargos (condomínio, IPTU, seguro) ficam fora."],
            ["Comissão: taxa de administração + intermediação retidas pela imobiliária."],
            ["IR retido: o sistema não registra retenção; locatário PJ retém e informa — confira com o contador."],
            ["Código do município (IBGE) não é guardado: o programa da Receita completa pelo nome/CEP."],
        ]);
        audit.Registrar(db, u.Id, "repasses_locacao", null, "EXPORTAR", depois: new { relatorio = "dimob", ano });
        await db.SaveChangesAsync();
        return new Arquivo(RelatoriosService.Salvar(wb), $"dimob-{ano}.xlsx", RelatoriosService.TipoXlsx);
    }

    /* ------------------------------ documentos do contrato ------------------------------ */

    /// <summary>2ª via da fatura: itens, vencimento e, se vencida, o valor atualizado com multa e juros do contrato.</summary>
    public async Task<Arquivo> SegundaViaPdfAsync(UsuarioAtual u, Guid faturaId)
    {
        u.Exigir(P.LocacaoVer);
        Guid contratoId;
        await using (var db = await fabrica.CreateDbContextAsync())
            contratoId = await db.FaturasLocacao.Where(f => f.Id == faturaId).Select(f => (Guid?)f.ContratoId).SingleOrDefaultAsync()
                ?? throw new NaoEncontradoException("Fatura não encontrada.");
        var d = await locacao.ObterAsync(u, contratoId);
        var f = d.Faturas.Single(x => x.Id == faturaId);
        if (f.Status == StatusFaturaLocacao.CANCELADA) throw new RegraNegocioException("_", "Fatura cancelada.");
        var c = d.Contrato;
        var hoje = DatasBR.Hoje();
        var atraso = f.Atrasada(hoje) ? CalculoLocacao.Atraso(f.Saldo, f.Vencimento, hoje, c.MultaAtrasoPct, c.JurosMesPct) : null;
        var pdf = RelatoriosService.NovoPdf($"Fatura de aluguel · {f.Competencia:MM/yyyy}", $"{d.Codigo} · {d.ImovelCodigo} · 2ª via",
            $"Inova Bens · 2ª via emitida em {DatasBR.Agora():dd/MM/yyyy HH:mm}", col =>
            {
                RelatoriosService.Campos(col, 3, ("Locatário", Nomes(d, PapelContratoLocacao.LOCATARIO)), ("Imóvel", d.ImovelEndereco ?? d.ImovelCodigo),
                    ("Vencimento", Texto.Data(f.Vencimento)));
                RelatoriosService.Tabela(col, [("Descrição", 4), ("Valor", 1.4f)],
                    [.. f.Itens.Select(i => new[] { i.Descricao, Texto.Moeda(i.Valor) }), ["Total da fatura", Texto.Moeda(f.Total)]], alinharUltimaDireita: true);
                var campos = new List<(string, string?)> { ("Já pago", Texto.Moeda(f.Pago)), ("Saldo", Texto.Moeda(f.Saldo)) };
                if (atraso is { } a)
                    campos.AddRange([("Multa", Texto.Moeda(a.Multa)), ("Juros", Texto.Moeda(a.Juros)), ($"Total para pagar hoje ({Texto.Data(hoje)})", Texto.Moeda(f.Saldo + a.Total))]);
                RelatoriosService.Campos(col, 3, [.. campos]);
                if (f.Status == StatusFaturaLocacao.PAGA) RelatoriosService.Paragrafo(col, "Fatura quitada.");
                else if (atraso is not null)
                    RelatoriosService.Paragrafo(col, $"Fatura vencida: multa de {c.MultaAtrasoPct.ToString("0.##", Texto.PtBr)}% e juros de "
                        + $"{c.JurosMesPct.ToString("0.##", Texto.PtBr)}% ao mês, pro rata, calculados até hoje. Após esta data o valor muda.");
                RelatoriosService.Paragrafo(col, "Pagamento pelos dados informados pela Inova Bens. Envie o comprovante pelo WhatsApp.");
            });
        await Auditar(u, "faturas_locacao", faturaId, "segunda-via");
        return new Arquivo(pdf, $"fatura-{f.Competencia:yyyy-MM}.pdf", RelatoriosService.TipoPdf);
    }

    /// <summary>
    /// Carta de cobrança (amigável) ou notificação extrajudicial de atraso (formal, com prazo e fiadores cientificados,
    /// Lei 8.245/91, arts. 9º, III, e 62) das faturas vencidas.
    /// </summary>
    public async Task<Arquivo> CobrancaPdfAsync(UsuarioAtual u, Guid contratoId, bool notificacao, int prazoDias = 10)
    {
        u.Exigir(P.LocacaoVer);
        var d = await locacao.ObterAsync(u, contratoId);
        var c = d.Contrato;
        var hoje = DatasBR.Hoje();
        var vencidas = d.Faturas.Where(f => f.Atrasada(hoje) && f.Saldo > 0).OrderBy(f => f.Vencimento).ToList();
        if (vencidas.Count == 0) throw new RegraNegocioException("_", "Não há faturas vencidas neste contrato.");
        var linhas = vencidas.Select(f => (f, a: CalculoLocacao.Atraso(f.Saldo, f.Vencimento, hoje, c.MultaAtrasoPct, c.JurosMesPct))).ToList();
        var total = linhas.Sum(x => x.f.Saldo + x.a.Total);
        var locatarios = Nomes(d, PapelContratoLocacao.LOCATARIO);
        var fiadores = d.Partes.Where(p => p.Papel == PapelContratoLocacao.FIADOR).Select(p => p.Nome).ToList();
        var titulo = notificacao ? "Notificação extrajudicial" : "Aviso de débito";
        var pdf = RelatoriosService.NovoPdf(titulo, $"{d.Codigo} · {d.ImovelCodigo}", $"Inova Bens · emitido em {DatasBR.Agora():dd/MM/yyyy HH:mm}", col =>
        {
            RelatoriosService.Campos(col, 2, ("Destinatário(s)", locatarios), ("Imóvel", d.ImovelEndereco ?? d.ImovelCodigo));
            RelatoriosService.Paragrafo(col, notificacao
                ? $"Pela presente, a Inova Bens, administradora do contrato de locação {d.Codigo}, NOTIFICA V.Sa. de que constam em aberto os "
                  + $"aluguéis e encargos abaixo, que somam {Texto.Moeda(total)} ({Extenso.Reais(total)}) atualizados até {Texto.Data(hoje)}. "
                  + $"Fica concedido o prazo de {prazoDias} dias, a contar do recebimento, para o pagamento. Não havendo a quitação, serão tomadas as "
                  + "medidas judiciais cabíveis, inclusive ação de despejo por falta de pagamento cumulada com cobrança (Lei 8.245/91, arts. 9º, III, e 62), "
                  + "com acréscimo de custas e honorários."
                : $"Prezado(a) {locatarios}, até {Texto.Data(hoje)} não identificamos o pagamento dos valores abaixo do contrato {d.Codigo}. "
                  + "Se o pagamento já foi feito, por favor desconsidere este aviso e envie o comprovante. Caso contrário, pedimos a regularização "
                  + "o quanto antes; estamos à disposição para conversar sobre um acordo.");
            RelatoriosService.Tabela(col, [("Referência", 1), ("Vencimento", 1), ("Saldo", 1), ("Multa", 1), ("Juros", 1), ("Atualizado", 1.2f)],
                [.. linhas.Select(x => new[] { x.f.Competencia.ToString("MM/yyyy"), Texto.Data(x.f.Vencimento), Texto.Moeda(x.f.Saldo),
                    Texto.Moeda(x.a.Multa), Texto.Moeda(x.a.Juros), Texto.Moeda(x.f.Saldo + x.a.Total) }),
                    ["Total", "", Texto.Moeda(linhas.Sum(x => x.f.Saldo)), "", "", Texto.Moeda(total)]], colunasDireita: 4);
            RelatoriosService.Paragrafo(col, $"Multa de {c.MultaAtrasoPct.ToString("0.##", Texto.PtBr)}% e juros de {c.JurosMesPct.ToString("0.##", Texto.PtBr)}% "
                + "ao mês, pro rata, conforme o contrato. Valores mudam a cada dia de atraso.");
            if (notificacao && fiadores.Count > 0)
                RelatoriosService.Paragrafo(col, $"Ficam cientificados os fiadores ({string.Join(", ", fiadores)}), responsáveis solidários pelo débito.");
            RelatoriosService.Paragrafo(col, $"\n\n_____________________________________________\nInova Bens · {Texto.Data(hoje)}"
                + (notificacao ? "\n\nRecebi em ____/____/______   Assinatura: ______________________________" : ""));
        });
        await Auditar(u, "contratos_locacao", contratoId, notificacao ? "notificacao-atraso" : "carta-cobranca");
        return new Arquivo(pdf, $"{(notificacao ? "notificacao" : "cobranca")}-{d.Codigo}.pdf", RelatoriosService.TipoPdf);
    }

    /// <summary>
    /// Comprovante anual dos pagamentos ao inquilino (para o IR dele) com a declaração de quitação dos meses do ano
    /// totalmente pagos.
    /// </summary>
    public async Task<Arquivo> QuitacaoAnualPdfAsync(UsuarioAtual u, Guid contratoId, int ano)
    {
        u.Exigir(P.LocacaoVer);
        var d = await locacao.ObterAsync(u, contratoId);
        var pagos = d.Faturas.SelectMany(f => f.Pagamentos.Where(p => !p.Estornado && p.Data.Year == ano).Select(p => (f, p)))
            .OrderBy(x => x.p.Data).ToList();
        var doAno = d.Faturas.Where(f => f.Competencia.Year == ano && f.Status != StatusFaturaLocacao.CANCELADA).ToList();
        var abertas = doAno.Where(f => f.Status == StatusFaturaLocacao.ABERTA).OrderBy(f => f.Competencia).ToList();
        var partilhas = pagos.Select(x => Partilha(x.f.Itens, x.p.Principal, x.p.Multa, x.p.Juros)).ToList();
        var locadores = d.Partes.Where(p => p.Papel == PapelContratoLocacao.LOCADOR).ToList();
        var pdf = RelatoriosService.NovoPdf($"Declaração de pagamentos e quitação · {ano}", $"{d.Codigo} · {d.ImovelCodigo}",
            $"Inova Bens · emitida em {DatasBR.Agora():dd/MM/yyyy HH:mm}", col =>
            {
                RelatoriosService.Campos(col, 2, ("Locatário(s)", string.Join("; ", d.Partes.Where(p => p.Papel == PapelContratoLocacao.LOCATARIO)
                    .Select(p => $"{p.Nome} ({Doc(p.CpfCnpj)})"))), ("Imóvel", d.ImovelEndereco ?? d.ImovelCodigo));
                if (d.Contrato.Modalidade == ModalidadeLocacao.ADMINISTRACAO && locadores.Count > 0)
                    RelatoriosService.Campos(col, 1, ("Locador(es) (beneficiários do aluguel)",
                        string.Join("; ", locadores.Select(p => $"{p.Nome} ({Doc(p.CpfCnpj)}){(p.Percentual is { } pc && pc < 100 ? $" · {pc:0.##}%" : "")}"))));
                RelatoriosService.Secao(col, $"Pagamentos feitos em {ano}");
                RelatoriosService.Tabela(col, [("Recibo", 0.8f), ("Referência", 0.9f), ("Pago em", 0.9f), ("Aluguel", 1), ("Encargos", 1), ("Total", 1)],
                    [.. pagos.Select((x, i) => new[] { x.p.Recibo, x.f.Competencia.ToString("MM/yyyy"), Texto.Data(x.p.Data),
                        Texto.Moeda(partilhas[i].Aluguel), Texto.Moeda(partilhas[i].Encargos), Texto.Moeda(x.p.Total) }),
                        ["Total", "", "", Texto.Moeda(partilhas.Sum(p => p.Aluguel)), Texto.Moeda(partilhas.Sum(p => p.Encargos)), Texto.Moeda(pagos.Sum(x => x.p.Total))]],
                    "Nenhum pagamento no ano.", colunasDireita: 3);
                RelatoriosService.Paragrafo(col, "Aluguel inclui multa e juros pagos; encargos: condomínio, IPTU, seguro e outros itens da fatura.");
                RelatoriosService.Secao(col, "Declaração");
                if (doAno.Count > 0 && abertas.Count == 0)
                    RelatoriosService.Paragrafo(col, $"Declaramos, para os devidos fins, que estão quitados os aluguéis e encargos das competências de {ano} "
                        + $"({doAno.Min(f => f.Competencia):MM/yyyy} a {doAno.Max(f => f.Competencia):MM/yyyy}) do contrato {d.Codigo}.");
                else if (doAno.Count > 0)
                    RelatoriosService.Paragrafo(col, $"Declaramos quitadas apenas as competências pagas acima. Constam em aberto: "
                        + string.Join(", ", abertas.Select(f => $"{f.Competencia:MM/yyyy} ({Texto.Moeda(f.Saldo)})")) + ".");
                else RelatoriosService.Paragrafo(col, $"Não há faturas com competência em {ano} neste contrato.");
                RelatoriosService.Paragrafo(col, $"\n\n_____________________________________________\nInova Bens · {Texto.Data(DatasBR.Hoje())}");
            });
        await Auditar(u, "contratos_locacao", contratoId, "quitacao-anual");
        return new Arquivo(pdf, $"quitacao-{ano}-{d.Codigo}.pdf", RelatoriosService.TipoPdf);
    }

    private static string Nomes(ContratoDetalhe d, PapelContratoLocacao papel) =>
        string.Join(", ", d.Partes.Where(p => p.Papel == papel).Select(p => p.Nome)) is { Length: > 0 } s ? s : "—";

    private async Task Auditar(UsuarioAtual u, string tabela, Guid id, string doc)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        audit.Registrar(db, u.Id, tabela, id, "EXPORTAR", depois: new { documento = doc, formato = "pdf" });
        await db.SaveChangesAsync();
    }

    /* ------------------------------ relatório gerencial ------------------------------ */

    /// <summary>Uma planilha com as visões da carteira de locação (ano escolhido para os valores do período).</summary>
    public async Task<Arquivo> GerencialExcelAsync(UsuarioAtual u, int ano)
    {
        u.Exigir(P.LocacaoVer);
        var hoje = DatasBR.Hoje();
        var de = new DateOnly(ano, 1, 1);
        var ate = de.AddYears(1);
        var vencimentos = await locacao.VencimentosAsync(u);
        await using var db = await fabrica.CreateDbContextAsync();
        var imoveis = db.Imoveis.VisiveisPara(u);
        var visiveis = db.ContratosLocacao.Where(c => imoveis.Any(i => i.Id == c.ImovelId));
        var contratos = await visiveis.AsNoTracking().Include(c => c.Imovel).ToListAsync();
        var porId = contratos.ToDictionary(c => c.Id);
        var inquilinos = (await db.ContratoLocacaoPartes.AsNoTracking()
                .Where(p => p.Papel == PapelContratoLocacao.LOCATARIO && visiveis.Any(c => c.Id == p.ContratoId))
                .Select(p => new { p.ContratoId, p.Pessoa!.NomeRazao }).ToListAsync())
            .GroupBy(p => p.ContratoId).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(p => p.NomeRazao)));
        string Inq(Guid c) => inquilinos.GetValueOrDefault(c) ?? "—";
        static XLCellValue D(DateOnly? d) => d is { } x ? x.ToDateTime(TimeOnly.MinValue) : Blank.Value;

        using var wb = new XLWorkbook();

        // 1) inadimplência por idade da dívida
        var abertas = await db.FaturasLocacao.AsNoTracking().Include(f => f.Itens).Include(f => f.Pagamentos)
            .Where(f => f.Status == StatusFaturaLocacao.ABERTA && f.Vencimento < hoje && visiveis.Any(c => c.Id == f.ContratoId)).ToListAsync();
        var inad = abertas.Select(f =>
            {
                var c = porId[f.ContratoId];
                var saldo = Math.Max(0, f.Itens.Sum(i => i.Valor) - f.Pagamentos.Where(p => p.EstornadoEm == null).Sum(p => p.Principal));
                var dias = hoje.DayNumber - f.Vencimento.DayNumber;
                var a = CalculoLocacao.Atraso(saldo, f.Vencimento, hoje, c.MultaAtrasoPct, c.JurosMesPct);
                return (c, f, saldo, dias, atual: saldo + a.Total);
            }).Where(x => x.saldo > 0).OrderByDescending(x => x.dias).ToList();
        Formatar(RelatoriosService.Aba(wb, "Inadimplência",
            [("Contrato", 11), ("Imóvel", 10), ("Inquilino", 30), ("Referência", 11), ("Vencimento", 12), ("Dias", 7), ("Faixa", 15), ("Saldo", 13), ("Atualizado hoje", 14)],
            [.. inad.Select(x => new XLCellValue[] { x.c.Codigo, x.c.Imovel!.Codigo, Inq(x.c.Id), x.f.Competencia.ToString("MM/yyyy"), D(x.f.Vencimento),
                x.dias, FaixaAtraso(x.dias), x.saldo, x.atual })]), [5], [8, 9]);
        Formatar(RelatoriosService.Aba(wb, "Inadimplência por faixa", [("Faixa", 16), ("Faturas", 9), ("Saldo", 14), ("Atualizado hoje", 15)],
            [.. inad.GroupBy(x => FaixaAtraso(x.dias)).OrderBy(g => g.Min(x => x.dias))
                .Select(g => new XLCellValue[] { g.Key, g.Count(), g.Sum(x => x.saldo), g.Sum(x => x.atual) })]), [], [3, 4]);

        // 2) contratos a vencer, reajustes, garantias e seguros (próximos 90 dias)
        Formatar(RelatoriosService.Aba(wb, "Vencimentos", [("Data", 12), ("Evento", 16), ("Contrato", 11), ("Imóvel", 10), ("Descrição", 80)],
            [.. vencimentos.Select(v => new XLCellValue[] { D(v.Data), Texto.Rotulo(v.Tipo), v.ContratoCodigo, v.ImovelCodigo, v.Descricao })]), [1], []);

        // 3) repasses: realizados no ano e pendentes
        var reps = await db.RepassesLocacao.AsNoTracking().Where(r => visiveis.Any(c => c.Id == r.ContratoId))
            .Join(db.FaturaLocacaoPagamentos.Where(p => p.EstornadoEm == null), r => r.PagamentoId, p => p.Id, (r, p) => new { r, p.Data, p.FaturaId })
            .Where(x => x.r.PagoEm == null || (x.Data >= de && x.Data < ate))
            .Select(x => new { x.r, x.Data, Pessoa = x.r.Pessoa!.NomeRazao, Competencia = db.FaturasLocacao.Where(f => f.Id == x.FaturaId).Select(f => f.Competencia).First() })
            .ToListAsync();
        Formatar(RelatoriosService.Aba(wb, "Repasses",
            [("Contrato", 11), ("Imóvel", 10), ("Proprietário", 30), ("Referência", 11), ("Recebido em", 12), ("Bruto", 12), ("Taxa adm.", 11),
                ("Intermediação", 12), ("Manutenção", 11), ("Líquido", 12), ("Previsto", 12), ("Pago em", 12)],
            [.. reps.OrderBy(x => x.r.PagoEm is null ? 0 : 1).ThenBy(x => x.r.PrevistoPara).Select(x => new XLCellValue[]
            {
                porId[x.r.ContratoId].Codigo, porId[x.r.ContratoId].Imovel!.Codigo, x.Pessoa, x.Competencia.ToString("MM/yyyy"), D(x.Data),
                x.r.Bruto, x.r.TaxaAdministracao, x.r.Intermediacao, x.r.DescontoManutencao, x.r.Liquido, D(x.r.PrevistoPara), D(x.r.PagoEm),
            })]), [5, 11, 12], [6, 7, 8, 9, 10]);

        // 4) receita da administração (taxa e intermediação), mês a mês pelo recebimento
        var doAno = reps.Where(x => x.Data >= de && x.Data < ate).ToList();
        Formatar(RelatoriosService.Aba(wb, "Receita da administração", [("Mês", 10), ("Aluguéis recebidos", 16), ("Taxa de administração", 18), ("Intermediação", 14), ("Total", 13)],
            [.. Enumerable.Range(1, 12).Select(m => doAno.Where(x => x.Data.Month == m).ToList()).Select((l, i) => new XLCellValue[]
            {
                $"{i + 1:00}/{ano}", l.Sum(x => x.r.Bruto), l.Sum(x => x.r.TaxaAdministracao), l.Sum(x => x.r.Intermediacao),
                l.Sum(x => x.r.TaxaAdministracao + x.r.Intermediacao),
            })]), [], [2, 3, 4, 5]);

        // 5) vacância: imóveis para alugar sem contrato ativo, e tempo até alugar (contratos iniciados no ano)
        var paraAlugar = await imoveis.AsNoTracking().Where(i => i.ExcluidoEm == null
                && (i.Finalidade == FinalidadeImovel.LOCACAO || i.Finalidade == FinalidadeImovel.AMBOS)
                && i.Situacao != SituacaoImovel.VENDIDO && i.Situacao != SituacaoImovel.ARQUIVADO)
            .Select(i => new { i.Id, i.Codigo, i.Titulo, i.PrecoLocacao, i.CriadoEm }).ToListAsync();
        var ativos = contratos.Where(c => c.Status == StatusContratoLocacao.ATIVO).Select(c => c.ImovelId).ToHashSet();
        DateOnly VagoDesde(Guid imovel, DateTime criado, DateOnly? antesDe = null) =>
            contratos.Where(c => c.ImovelId == imovel && c.EncerradoEm != null && (antesDe is null || c.EncerradoEm <= antesDe))
                .Max(c => c.EncerradoEm) ?? DateOnly.FromDateTime(DatasBR.DeUtc(criado));
        var vagos = paraAlugar.Where(i => !ativos.Contains(i.Id)).Select(i => (i, desde: VagoDesde(i.Id, i.CriadoEm))).OrderBy(x => x.desde).ToList();
        Formatar(RelatoriosService.Aba(wb, "Vacância", [("Imóvel", 10), ("Título", 40), ("Aluguel pedido", 14), ("Vago desde", 12), ("Dias vago", 10)],
            [.. vagos.Select(x => new XLCellValue[] { x.i.Codigo, x.i.Titulo, x.i.PrecoLocacao is { } pl ? pl : Blank.Value, D(x.desde),
                hoje.DayNumber - x.desde.DayNumber })]), [4], [3]);
        var criados = paraAlugar.ToDictionary(i => i.Id, i => i.CriadoEm);
        var alugados = contratos.Where(c => c.Inicio >= de && c.Inicio < ate && c.Status != StatusContratoLocacao.CANCELADO && c.Status != StatusContratoLocacao.RASCUNHO)
            .Select(c =>
            {
                var desde = VagoDesde(c.ImovelId, criados.GetValueOrDefault(c.ImovelId, c.CriadoEm), c.Inicio);
                return (c, desde, dias: Math.Max(0, c.Inicio.DayNumber - desde.DayNumber));
            }).OrderBy(x => x.c.Inicio).ToList();
        var tempo = RelatoriosService.Aba(wb, "Tempo para alugar", [("Contrato", 11), ("Imóvel", 10), ("Vago desde", 12), ("Início", 12), ("Dias", 8)],
            [.. alugados.Select(x => new XLCellValue[] { x.c.Codigo, x.c.Imovel!.Codigo, D(x.desde), D(x.c.Inicio), x.dias })]);
        tempo.Columns(3, 4).Style.DateFormat.Format = "dd/MM/yyyy";
        if (alugados.Count > 0)
        {
            var r = alugados.Count + 3;
            tempo.Cell(r, 4).Value = "Média (dias)";
            tempo.Cell(r, 5).Value = Math.Round(alugados.Average(x => x.dias), 1);
            tempo.Range(r, 4, r, 5).Style.Font.SetBold();
        }

        // 6) chamados: abertos e concluídos no ano, com tempo de atendimento
        var chamados = await db.ChamadosManutencao.AsNoTracking().Include(x => x.Imovel)
            .Where(x => imoveis.Any(i => i.Id == x.ImovelId)
                && ((x.Status != StatusChamado.CONCLUIDO && x.Status != StatusChamado.RECUSADO && x.Status != StatusChamado.CANCELADO)
                    || (x.ConcluidoEm >= de && x.ConcluidoEm < ate)))
            .OrderBy(x => x.Numero).ToListAsync();
        int Dias(ChamadoManutencao x) => (x.ConcluidoEm ?? hoje).DayNumber - DateOnly.FromDateTime(DatasBR.DeUtc(x.CriadoEm)).DayNumber;
        Formatar(RelatoriosService.Aba(wb, "Chamados",
            [("Chamado", 10), ("Imóvel", 10), ("Título", 40), ("Categoria", 14), ("Por conta do", 13), ("Situação", 18), ("Aberto em", 12), ("Concluído em", 12), ("Dias", 7), ("Valor", 12)],
            [.. chamados.Select(x => new XLCellValue[] { x.Codigo, x.Imovel!.Codigo, x.Titulo, Texto.Rotulo(x.Categoria), Texto.Rotulo(x.Responsavel),
                Texto.Rotulo(x.Status), D(DateOnly.FromDateTime(DatasBR.DeUtc(x.CriadoEm))), D(x.ConcluidoEm), Dias(x), x.ValorFinal is { } v ? v : Blank.Value })]),
            [7, 8], [10]);

        // 7) custo de manutenção (consertos concluídos + vistorias) por imóvel no ano
        var consertos = chamados.Where(x => x.Status == StatusChamado.CONCLUIDO && x.ValorFinal > 0)
            .Select(x => (x.ImovelId, Codigo: x.Imovel!.Codigo, Valor: x.ValorFinal!.Value, Vistoria: false));
        var vistorias = (await db.Vistorias.AsNoTracking().Where(v => imoveis.Any(i => i.Id == v.ImovelId) && v.Valor > 0
                && v.RealizadaEm >= de && v.RealizadaEm < ate).Select(v => new { v.ImovelId, v.Imovel!.Codigo, v.Valor }).ToListAsync())
            .Select(v => (v.ImovelId, v.Codigo, Valor: v.Valor!.Value, Vistoria: true));
        var custos = consertos.Concat(vistorias).GroupBy(x => (x.ImovelId, x.Codigo))
            .ToDictionary(g => g.Key.ImovelId, g => (g.Key.Codigo, Consertos: g.Where(x => !x.Vistoria).Sum(x => x.Valor),
                Qtd: g.Count(x => !x.Vistoria), Vistorias: g.Where(x => x.Vistoria).Sum(x => x.Valor)));
        Formatar(RelatoriosService.Aba(wb, "Manutenção por imóvel", [("Imóvel", 10), ("Consertos", 10), ("Custo dos consertos", 17), ("Vistorias", 12), ("Total", 13)],
            [.. custos.Values.OrderByDescending(x => x.Consertos + x.Vistorias)
                .Select(x => new XLCellValue[] { x.Codigo, x.Qtd, x.Consertos, x.Vistorias, x.Consertos + x.Vistorias })]), [], [3, 4, 5]);

        // 8) rentabilidade (yield) dos imóveis com contrato ativo
        var precos = await imoveis.AsNoTracking().Where(i => ativos.Contains(i.Id)).Select(i => new { i.Id, i.PrecoVenda }).ToDictionaryAsync(i => i.Id, i => i.PrecoVenda);
        var rent = RelatoriosService.Aba(wb, "Rentabilidade",
            [("Contrato", 11), ("Imóvel", 10), ("Aluguel", 12), ("Aluguel anual", 14), ("Valor do imóvel", 15), ("Yield bruto", 11), ("Taxa adm. no ano", 15), ("Manutenção no ano", 16), ("Yield líquido", 12)],
            [.. contratos.Where(c => c.Status == StatusContratoLocacao.ATIVO).OrderBy(c => c.Numero).Select(c =>
            {
                var anual = c.ValorAluguel * 12;
                var taxa = anual * c.TaxaAdministracaoPct / 100;
                var manut = custos.TryGetValue(c.ImovelId, out var cu) ? cu.Consertos + cu.Vistorias : 0;
                var valor = precos.GetValueOrDefault(c.ImovelId);
                return new XLCellValue[] { c.Codigo, c.Imovel!.Codigo, c.ValorAluguel, anual, valor is { } v ? v : Blank.Value,
                    valor > 0 ? (double)(anual / valor.Value) : Blank.Value, taxa, manut, valor > 0 ? (double)((anual - taxa - manut) / valor.Value) : Blank.Value };
            })]);
        rent.Columns(3, 5).Style.NumberFormat.Format = "#,##0.00";
        rent.Columns(7, 8).Style.NumberFormat.Format = "#,##0.00";
        rent.Column(6).Style.NumberFormat.Format = "0.00%";
        rent.Column(9).Style.NumberFormat.Format = "0.00%";

        // 9) carteira por proprietário
        var locadores = await db.ContratoLocacaoPartes.AsNoTracking()
            .Where(p => p.Papel == PapelContratoLocacao.LOCADOR && visiveis.Any(c => c.Id == p.ContratoId && c.Status == StatusContratoLocacao.ATIVO))
            .Select(p => new { p.PessoaId, p.Pessoa!.NomeRazao, p.ContratoId, p.Percentual }).ToListAsync();
        Formatar(RelatoriosService.Aba(wb, "Carteira por proprietário",
            [("Proprietário", 32), ("Contratos ativos", 15), ("Aluguel mensal (parte dele)", 24), ($"Repassado em {ano}", 16), ("A repassar", 13)],
            [.. locadores.GroupBy(p => (p.PessoaId, p.NomeRazao)).OrderBy(g => g.Key.NomeRazao).Select(g => new XLCellValue[]
            {
                g.Key.NomeRazao, g.Count(), g.Sum(p => porId[p.ContratoId].ValorAluguel * (p.Percentual ?? 100) / 100),
                reps.Where(x => x.r.PessoaId == g.Key.PessoaId && x.r.PagoEm >= de && x.r.PagoEm < ate).Sum(x => x.r.Liquido),
                reps.Where(x => x.r.PessoaId == g.Key.PessoaId && x.r.PagoEm == null).Sum(x => x.r.Liquido),
            })]), [], [3, 4, 5]);

        audit.Registrar(db, u.Id, "contratos_locacao", null, "EXPORTAR", depois: new { relatorio = "gerencial-locacao", ano });
        await db.SaveChangesAsync();
        return new Arquivo(RelatoriosService.Salvar(wb), $"locacao-gerencial-{ano}.xlsx", RelatoriosService.TipoXlsx);
    }

    private static void Formatar(IXLWorksheet ws, int[] datas, int[] moedas)
    {
        foreach (var c in datas) ws.Column(c).Style.DateFormat.Format = "dd/MM/yyyy";
        foreach (var c in moedas) ws.Column(c).Style.NumberFormat.Format = "#,##0.00";
    }
}
