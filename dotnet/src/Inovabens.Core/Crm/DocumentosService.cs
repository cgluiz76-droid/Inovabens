using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Relatorios;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Crm;

public sealed record ModeloResumo(Guid Id, string Nome, TipoModeloDocumento Tipo, FinalidadeLead? Finalidade, string NomeArquivo,
    int Tamanho, bool Ativo, DateTime CriadoEm);

/// <summary>Resultado do envio: marcadores do modelo que o sistema não conhece (ficam como estão no documento).</summary>
public sealed record ModeloEnviado(Guid Id, IReadOnlyList<string> Marcadores, IReadOnlyList<string> Desconhecidos);

/// <summary>
/// Documentos da proposta: PDF padrão gerado pelo sistema e modelos Word (.docx) enviados pelo administrador, com marcadores
/// {{campo}} preenchidos com os dados da proposta, do cliente, do imóvel e dos proprietários. O modelo preenchido vira PDF quando
/// há LibreOffice no servidor (<see cref="ConversorPdf"/>); sem ele, sai o .docx preenchido.
/// </summary>
public sealed class DocumentosService(IDbContextFactory<InovabensDb> fabrica, PropostasService propostas, ConversorPdf conversor, AuditService audit)
{
    public const int TamanhoMaximo = 10 * 1024 * 1024;

    /// <summary>Marcadores disponíveis nos modelos (mostrados na tela de modelos).</summary>
    public static readonly IReadOnlyList<(string Chave, string Descricao)> Campos =
    [
        ("data.hoje", "Data de hoje por extenso (10 de novembro de 2026)"),
        ("data.hoje_curta", "Data de hoje (10/11/2026)"),
        ("cliente.nome", "Nome ou razão social do cliente (lead)"),
        ("cliente.cpf_cnpj", "CPF/CNPJ do cliente"),
        ("cliente.rg", "RG do cliente"),
        ("cliente.email", "E-mail do cliente"),
        ("cliente.telefone", "Telefone do cliente"),
        ("cliente.endereco", "Endereço do cliente"),
        ("proprietario.nome", "Proprietário principal (ou o primeiro)"),
        ("proprietario.cpf_cnpj", "CPF/CNPJ do proprietário principal"),
        ("proprietario.rg", "RG do proprietário principal"),
        ("proprietario.endereco", "Endereço do proprietário principal"),
        ("proprietarios.nomes", "Todos os proprietários, com CPF/CNPJ e %, um por linha"),
        ("imovel.codigo", "Código do imóvel"),
        ("imovel.titulo", "Título do imóvel"),
        ("imovel.tipo", "Tipo do imóvel"),
        ("imovel.endereco", "Endereço completo do imóvel"),
        ("imovel.matricula", "Matrícula"),
        ("imovel.cartorio", "Cartório"),
        ("imovel.inscricao_municipal", "Inscrição municipal"),
        ("imovel.area_total", "Área total (m²)"),
        ("imovel.area_construida", "Área construída (m²)"),
        ("imovel.valor_anunciado", "Preço anunciado no dia da proposta"),
        ("proposta.numero", "Número da proposta"),
        ("proposta.data", "Data da proposta"),
        ("proposta.validade", "Validade da proposta"),
        ("proposta.autor", "Proposta do cliente / contraproposta do proprietário"),
        ("proposta.total", "Valor total (venda) ou aluguel (locação)"),
        ("proposta.total_extenso", "Valor total por extenso"),
        ("proposta.diferenca", "Diferença para o anunciado (%)"),
        ("proposta.condicoes", "Condições"),
        ("pagamento.itens", "Formas de pagamento, uma por linha"),
        ("locacao.aluguel", "Aluguel mensal"),
        ("locacao.aluguel_extenso", "Aluguel por extenso"),
        ("locacao.garantia", "Garantia"),
        ("locacao.prazo_meses", "Prazo do contrato (meses)"),
        ("locacao.reajuste", "Índice de reajuste"),
        ("locacao.carencia_meses", "Carência (meses)"),
        ("corretor.nome", "Corretor responsável pelo lead"),
        ("corretor.email", "E-mail do corretor"),
    ];

    private static readonly string[] Meses =
        ["janeiro", "fevereiro", "março", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro"];

    public static string DataPorExtenso(DateOnly d) => $"{d.Day} de {Meses[d.Month - 1]} de {d.Year}";

    private static string Juntar(string separador, params string?[] partes) =>
        string.Join(separador, partes.Where(x => !string.IsNullOrWhiteSpace(x)));

    private static string Endereco(string? logradouro, string? numero, string? complemento, string? bairro, string? cidade, string? uf, string? cep) =>
        Juntar(", ", Juntar(", ", logradouro, numero), complemento, bairro, Juntar("/", cidade, uf), cep is null ? null : $"CEP {cep}");

    /* ============================ Modelos ============================ */

    public async Task<IReadOnlyList<ModeloResumo>> ModelosAsync(UsuarioAtual u, FinalidadeLead? finalidade = null, bool incluirInativos = false)
    {
        u.Exigir(incluirInativos ? P.AdminConfig : P.CrmVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var q = db.ModelosDocumento.AsNoTracking();
        // modelos de locação (contrato de aluguel, acordo) aparecem só na página do contrato de locação
        if (!incluirInativos) q = q.Where(m => m.Ativo && (m.Tipo == TipoModeloDocumento.PROPOSTA || m.Tipo == TipoModeloDocumento.CONTRATO));
        if (finalidade is { } f) q = q.Where(m => m.Finalidade == null || m.Finalidade == f);
        return await q.OrderBy(m => m.Tipo).ThenBy(m => m.Nome)
            .Select(m => new ModeloResumo(m.Id, m.Nome, m.Tipo, m.Finalidade, m.NomeArquivo, m.Tamanho, m.Ativo, m.CriadoEm))
            .ToListAsync();
    }

    public async Task<ModeloEnviado> EnviarModeloAsync(UsuarioAtual u, string? nome, TipoModeloDocumento tipo, FinalidadeLead? finalidade,
        string nomeArquivo, byte[] conteudo)
    {
        u.Exigir(P.AdminConfig);
        var n = Texto.Limpo(nome) ?? throw new RegraNegocioException(nameof(nome), "Informe o nome do modelo");
        if (n.Length > 120) throw new RegraNegocioException(nameof(nome), "No máximo 120 caracteres");
        if (!nomeArquivo.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            throw new RegraNegocioException("arquivo", "Envie o modelo em Word (.docx). Arquivo .doc antigo: abra no Word e salve como .docx");
        if (conteudo.Length > TamanhoMaximo) throw new RegraNegocioException("arquivo", "O modelo pode ter no máximo 10 MB");
        if (!Docx.Valido(conteudo)) throw new RegraNegocioException("arquivo", "O arquivo não é um documento Word (.docx) válido");

        var marcadores = Docx.Marcadores(conteudo);
        var conhecidos = (tipo is TipoModeloDocumento.CONTRATO_LOCACAO or TipoModeloDocumento.ACORDO_LOCACAO
            ? Locacao.LocacaoDocumentosService.Campos : Campos).Select(c => c.Chave).ToHashSet();
        await using var db = await fabrica.CreateDbContextAsync();
        var m = new ModeloDocumento
        {
            Id = Guid.NewGuid(), Nome = n, Tipo = tipo, Finalidade = finalidade, NomeArquivo = Path.GetFileName(nomeArquivo),
            Conteudo = conteudo, Tamanho = conteudo.Length, CriadoPor = u.Id,
        };
        db.ModelosDocumento.Add(m);
        audit.Registrar(db, u.Id, "modelos_documento", m.Id, "CRIAR", depois: new { m.Nome, m.Tipo, m.Finalidade, m.NomeArquivo, m.Tamanho });
        await db.SaveChangesAsync();
        return new ModeloEnviado(m.Id, marcadores, [.. marcadores.Where(x => !conhecidos.Contains(x))]);
    }

    public async Task AtivarModeloAsync(UsuarioAtual u, Guid id, bool ativo)
    {
        u.Exigir(P.AdminConfig);
        await using var db = await fabrica.CreateDbContextAsync();
        var m = await db.ModelosDocumento.SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Modelo não encontrado.");
        (m.Ativo, m.AtualizadoEm) = (ativo, DateTime.UtcNow);
        audit.Registrar(db, u.Id, "modelos_documento", id, "ATUALIZAR", depois: new { ativo });
        await db.SaveChangesAsync();
    }

    public async Task<Arquivo> BaixarModeloAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.AdminConfig);
        await using var db = await fabrica.CreateDbContextAsync();
        var m = await db.ModelosDocumento.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Modelo não encontrado.");
        return new Arquivo(m.Conteudo, m.NomeArquivo, Docx.Tipo);
    }

    /* ============================ Dados da proposta ============================ */

    private sealed record Dados(PropostaDetalhe Proposta, Pessoa Cliente, Imovel Imovel, IReadOnlyList<(Pessoa Pessoa, decimal Percentual, bool Principal)> Proprietarios,
        Usuario? Corretor);

    private async Task<Dados> CarregarAsync(UsuarioAtual u, Guid propostaId)
    {
        var p = await propostas.ObterAsync(u, propostaId); // confere a visibilidade do lead
        await using var db = await fabrica.CreateDbContextAsync();
        var lead = await db.Leads.AsNoTracking().Include(l => l.Pessoa).SingleAsync(l => l.Id == p.Dados.LeadId);
        var im = await db.Imoveis.AsNoTracking().SingleAsync(i => i.Id == p.Dados.ImovelId);
        var hoje = DatasBR.Hoje();
        var donos = await db.Set<ImovelProprietario>().AsNoTracking().Include(x => x.Pessoa)
            .Where(x => x.ImovelId == im.Id && (x.FimEm == null || x.FimEm >= hoje))
            .OrderByDescending(x => x.Principal).ThenByDescending(x => x.Percentual).ToListAsync();
        var corretorId = lead.ResponsavelId ?? p.Dados.CriadoPor;
        var corretor = corretorId is null ? null : await db.Usuarios.AsNoTracking().SingleOrDefaultAsync(x => x.Id == corretorId);
        return new Dados(p, lead.Pessoa!, im, [.. donos.Select(d => (d.Pessoa!, d.Percentual, d.Principal))], corretor);
    }

    internal static string LinhaItem(PropostaItem i) =>
        $"{PropostasService.Rotulo(i.Tipo)}: {Texto.Moeda(i.Valor)}{(Detalhes(i) is { Length: > 0 } det ? $" ({det})" : "")}";

    /// <summary>Banco, parcelas, datas e descrição de uma forma de pagamento ("" se nada).</summary>
    internal static string Detalhes(PropostaItem i)
    {
        var det = new List<string>();
        if (i.Instituicao is { } inst) det.Add(inst);
        if (i.Parcelas is { } n) det.Add(i.Tipo == TipoItemProposta.FINANCIAMENTO ? $"{n} meses" : $"{n} parcelas");
        if (i.CreditoAprovado == true) det.Add("crédito aprovado");
        if (i.DataPrevista is { } d) det.Add(i.Tipo == TipoItemProposta.PARCELAS_PROPRIETARIO ? $"1º vencimento {Texto.Data(d)}" : $"em {Texto.Data(d)}");
        if (i.Descricao is { } desc) det.Add(desc);
        return string.Join("; ", det);
    }

    private static Dictionary<string, string> Valores(Dados d)
    {
        var p = d.Proposta.Dados;
        var c = d.Cliente;
        var im = d.Imovel;
        var dono = d.Proprietarios.Count > 0 ? d.Proprietarios[0].Pessoa : null;
        var hoje = DatasBR.Hoje();
        var dif = d.Proposta.DiferencaPct is { } x ? $"{(x > 0 ? "+" : "")}{x.ToString("0.#", Texto.PtBr)}%" : "";
        var v = new Dictionary<string, string>
        {
            ["data.hoje"] = DataPorExtenso(hoje),
            ["data.hoje_curta"] = Texto.Data(hoje),
            ["cliente.nome"] = c.NomeRazao,
            ["cliente.cpf_cnpj"] = c.CpfCnpj ?? "",
            ["cliente.rg"] = c.Rg ?? "",
            ["cliente.email"] = c.Email ?? "",
            ["cliente.telefone"] = c.Telefone ?? c.Whatsapp ?? "",
            ["cliente.endereco"] = Endereco(c.Logradouro, c.Numero, c.Complemento, c.Bairro, c.Cidade, c.Uf, c.Cep),
            ["proprietario.nome"] = dono?.NomeRazao ?? "",
            ["proprietario.cpf_cnpj"] = dono?.CpfCnpj ?? "",
            ["proprietario.rg"] = dono?.Rg ?? "",
            ["proprietario.endereco"] = dono is null ? "" : Endereco(dono.Logradouro, dono.Numero, dono.Complemento, dono.Bairro, dono.Cidade, dono.Uf, dono.Cep),
            ["proprietarios.nomes"] = string.Join("\n", d.Proprietarios.Select(o =>
                Juntar(", ", o.Pessoa.NomeRazao, o.Pessoa.CpfCnpj is { } doc ? $"CPF/CNPJ {doc}" : null,
                    d.Proprietarios.Count > 1 ? $"{o.Percentual.ToString("0.##", Texto.PtBr)}%" : null))),
            ["imovel.codigo"] = im.Codigo,
            ["imovel.titulo"] = im.Titulo,
            ["imovel.tipo"] = Juntar(" / ", im.Tipo, im.Subtipo),
            ["imovel.endereco"] = Endereco(im.Logradouro, im.Numero, im.Complemento, im.Bairro, im.Cidade, im.Uf, im.Cep),
            ["imovel.matricula"] = im.Matricula ?? "",
            ["imovel.cartorio"] = im.Cartorio ?? "",
            ["imovel.inscricao_municipal"] = im.InscricaoMunicipal ?? "",
            ["imovel.area_total"] = im.AreaTotalM2?.ToString("0.##", Texto.PtBr) ?? "",
            ["imovel.area_construida"] = im.AreaConstruidaM2?.ToString("0.##", Texto.PtBr) ?? "",
            ["imovel.valor_anunciado"] = p.ValorAnunciado is { } va ? Texto.Moeda(va) : "",
            ["proposta.numero"] = p.Numero.ToString(),
            ["proposta.data"] = Texto.Data(DateOnly.FromDateTime(DatasBR.DeUtc(p.CriadoEm))),
            ["proposta.validade"] = p.ValidadeEm is { } val ? Texto.Data(val) : "",
            ["proposta.autor"] = p.Autor == AutorProposta.CLIENTE ? "Proposta do cliente" : "Contraproposta do proprietário",
            ["proposta.total"] = Texto.Moeda(p.ValorTotal),
            ["proposta.total_extenso"] = Extenso.Reais(p.ValorTotal),
            ["proposta.diferenca"] = dif,
            ["proposta.condicoes"] = p.Condicoes ?? "",
            ["pagamento.itens"] = string.Join("\n", p.Itens.OrderBy(i => i.Ordem).Select(LinhaItem)),
            ["locacao.aluguel"] = p.Aluguel is { } al ? Texto.Moeda(al) : "",
            ["locacao.aluguel_extenso"] = p.Aluguel is { } al2 ? Extenso.Reais(al2) : "",
            ["locacao.garantia"] = p.Garantia is { } g ? PropostasService.Rotulo(g) : "",
            ["locacao.prazo_meses"] = p.PrazoMeses?.ToString() ?? "",
            ["locacao.reajuste"] = p.IndiceReajuste ?? "",
            ["locacao.carencia_meses"] = p.CarenciaMeses?.ToString() ?? "",
            ["corretor.nome"] = d.Corretor?.Nome ?? "",
            ["corretor.email"] = d.Corretor?.Email ?? "",
        };
        return v;
    }

    /// <summary>Valores dos marcadores para uma proposta (usado nos testes e para conferência).</summary>
    public async Task<IReadOnlyDictionary<string, string>> ValoresAsync(UsuarioAtual u, Guid propostaId) => Valores(await CarregarAsync(u, propostaId));

    /* ============================ Geração ============================ */

    /// <summary>
    /// Documento da proposta: sem modelo, o PDF padrão; com modelo, o .docx preenchido convertido em PDF (ou o próprio .docx
    /// quando não há LibreOffice). Fica registrado na auditoria.
    /// </summary>
    public async Task<Arquivo> GerarAsync(UsuarioAtual u, Guid propostaId, Guid? modeloId = null)
    {
        u.Exigir(P.CrmVer);
        var d = await CarregarAsync(u, propostaId);
        var p = d.Proposta.Dados;
        var baseNome = $"proposta-{d.Imovel.Codigo}-n{p.Numero}";
        Arquivo arquivo;
        string? modeloNome = null;
        if (modeloId is { } mid)
        {
            await using var db = await fabrica.CreateDbContextAsync();
            var m = await db.ModelosDocumento.AsNoTracking().SingleOrDefaultAsync(x => x.Id == mid && x.Ativo)
                ?? throw new NaoEncontradoException("Modelo não encontrado.");
            if (m.Finalidade is { } f && f != p.Finalidade) throw new RegraNegocioException("modelo", "Este modelo é de outra finalidade (venda/locação)");
            modeloNome = m.Nome;
            var docx = Docx.Preencher(m.Conteudo, Valores(d));
            var nome = $"{(m.Tipo == TipoModeloDocumento.CONTRATO ? "contrato" : "proposta")}-{d.Imovel.Codigo}-n{p.Numero}";
            arquivo = await conversor.ConverterAsync(docx) is { } pdf
                ? new Arquivo(pdf, $"{nome}.pdf", RelatoriosService.TipoPdf)
                : new Arquivo(docx, $"{nome}.docx", Docx.Tipo);
        }
        else arquivo = new Arquivo(PdfPadrao(d), $"{baseNome}.pdf", RelatoriosService.TipoPdf);

        await using (var db = await fabrica.CreateDbContextAsync())
        {
            audit.Registrar(db, u.Id, "propostas", propostaId, "EXPORTAR", depois: new { modelo = modeloNome ?? "padrão", arquivo = arquivo.Nome });
            await db.SaveChangesAsync();
        }
        return arquivo;
    }

    private static byte[] PdfPadrao(Dados d)
    {
        var p = d.Proposta.Dados;
        var v = Valores(d);
        var venda = p.Finalidade == FinalidadeLead.COMPRA;
        var titulo = venda ? "Proposta de Compra" : "Proposta de Locação";
        return RelatoriosService.NovoPdf(titulo, $"Nº {p.Numero} · {d.Imovel.Codigo} · {d.Imovel.Titulo}",
            $"{titulo} nº {p.Numero} · {d.Imovel.Codigo} · gerada em {Texto.DataHora(DateTime.UtcNow)}", col =>
            {
                RelatoriosService.Secao(col, venda ? "Proponente comprador" : "Proponente locatário");
                RelatoriosService.Campos(col, 3, ("Nome", v["cliente.nome"]), ("CPF/CNPJ", v["cliente.cpf_cnpj"]), ("RG", v["cliente.rg"]),
                    ("Telefone", v["cliente.telefone"]), ("E-mail", v["cliente.email"]));
                RelatoriosService.Campos(col, 1, ("Endereço", v["cliente.endereco"]));

                RelatoriosService.Secao(col, d.Proprietarios.Count > 1 ? "Proprietários" : "Proprietário");
                if (d.Proprietarios.Count == 0) RelatoriosService.Paragrafo(col, null);
                foreach (var (dono, pct, _) in d.Proprietarios)
                    RelatoriosService.Campos(col, 3, ("Nome", dono.NomeRazao), ("CPF/CNPJ", dono.CpfCnpj),
                        ("Participação", d.Proprietarios.Count > 1 ? $"{pct.ToString("0.##", Texto.PtBr)}%" : null));

                RelatoriosService.Secao(col, "Imóvel");
                RelatoriosService.Campos(col, 3, ("Código", d.Imovel.Codigo), ("Tipo", v["imovel.tipo"]), ("Matrícula", v["imovel.matricula"]));
                RelatoriosService.Campos(col, 1, ("Endereço", v["imovel.endereco"]));

                if (venda)
                {
                    RelatoriosService.Secao(col, "Valor e forma de pagamento");
                    RelatoriosService.Tabela(col, [("Forma de pagamento", 2.2f), ("Detalhes", 3f), ("Valor", 1.4f)],
                        [.. p.Itens.OrderBy(i => i.Ordem).Select(i => new[]
                        {
                            PropostasService.Rotulo(i.Tipo),
                            Detalhes(i) is { Length: > 0 } det ? det : "—",
                            Texto.Moeda(i.Valor),
                        })], alinharUltimaDireita: true);
                    col.Item().AlignRight().Text(t =>
                    {
                        t.Span("Total: ").Bold();
                        t.Span($"{Texto.Moeda(p.ValorTotal)} ({v["proposta.total_extenso"]})").Bold();
                    });
                }
                else
                {
                    RelatoriosService.Secao(col, "Condições da locação");
                    RelatoriosService.Campos(col, 3, ("Aluguel mensal", v["locacao.aluguel"]), ("Garantia", v["locacao.garantia"]),
                        ("Prazo", p.PrazoMeses is { } pm ? $"{pm} meses" : null), ("Reajuste", v["locacao.reajuste"]),
                        ("Carência", p.CarenciaMeses is > 0 ? $"{p.CarenciaMeses} meses" : null));
                    RelatoriosService.Paragrafo(col, $"Aluguel por extenso: {v["locacao.aluguel_extenso"]}.");
                }

                RelatoriosService.Secao(col, "Condições gerais");
                RelatoriosService.Paragrafo(col, p.Condicoes);
                RelatoriosService.Campos(col, 2, ("Validade da proposta", p.ValidadeEm is { } val ? Texto.Data(val) : "A combinar"),
                    ("Corretor", v["corretor.nome"]));

                col.Item().PaddingTop(16).Text($"{(d.Imovel.Cidade is { } cid ? $"{cid}, " : "")}{v["data.hoje"]}.");
                col.Item().PaddingTop(40).Row(r =>
                {
                    void Assinatura(string nome, string papel) => r.RelativeItem().PaddingHorizontal(10).Column(c =>
                    {
                        c.Item().BorderTop(1).PaddingTop(4).AlignCenter().Text(nome).Bold();
                        c.Item().AlignCenter().Text(papel).FontSize(8);
                    });
                    Assinatura(v["cliente.nome"], venda ? "Proponente comprador" : "Proponente locatário");
                    Assinatura(v["proprietario.nome"] is { Length: > 0 } pn ? pn : "Proprietário", venda ? "Proprietário vendedor" : "Proprietário locador");
                });
            });
    }
}
