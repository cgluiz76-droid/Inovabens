using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Inovabens.Core.Comum;
using Inovabens.Core.Crm;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Relatorios;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Locacao;

/// <summary>Acordo de débitos em atraso: faturas incluídas, desconto, entrada e parcelas.</summary>
public sealed class AcordoForm : IValidatableObject
{
    public List<Guid> FaturaIds { get; set; } = [];

    [Range(typeof(decimal), "0", "99999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")]
    public decimal Desconto { get; set; }

    [Range(typeof(decimal), "0", "99999999", ParseLimitsInInvariantCulture = true, ErrorMessage = "Valor inválido.")]
    public decimal Entrada { get; set; }

    [Range(1, 36, ErrorMessage = "Entre 1 e 36 parcelas.")]
    public int Parcelas { get; set; } = 1;

    [Required(ErrorMessage = "Informe o vencimento da 1ª parcela.")]
    public DateOnly? PrimeiroVencimento { get; set; }

    [StringLength(1000, ErrorMessage = "Até 1000 caracteres.")]
    public string? Observacao { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (FaturaIds.Count == 0) yield return new("Escolha ao menos uma fatura.", [nameof(FaturaIds)]);
    }
}

/// <summary>Uma parcela do acordo.</summary>
public sealed record ParcelaAcordo(int Numero, DateOnly Vencimento, decimal Valor, bool Entrada);

/// <summary>Resultado do acordo: débito corrigido até hoje, desconto e parcelas.</summary>
public sealed record AcordoCalculo(IReadOnlyList<(string Referencia, decimal Saldo, decimal Acrescimos)> Faturas, decimal Debito, decimal Desconto,
    decimal Total, IReadOnlyList<ParcelaAcordo> Parcelas);

/// <summary>Documento gerado e onde ficou guardado no Drive (null se o Drive não está configurado).</summary>
public sealed record DocumentoSalvo(string Nome, string? LinkPasta);

/// <summary>
/// Documentos da locação: modelos Word de contrato e de acordo (o sistema cria um "padrão" de cada; o administrador envia
/// outros em Administração → Modelos), marcadores {{campo}} com os dados do contrato e pasta do contrato no Drive:
/// "&lt;imóvel&gt;/08 - Locação/LOC-0001 - Locatário - desde 11-2026". Renovação e aditivo ficam na mesma pasta (é o mesmo
/// contrato); um contrato novo com outro locatário ganha a sua.
/// </summary>
public sealed class LocacaoDocumentosService(IDbContextFactory<InovabensDb> fabrica, LocacaoService locacao, PastasDriveService pastas,
    IArmazenamento armazenamento, ConversorPdf conversor, AuditService audit)
{
    public const string NomeModeloContrato = "Contrato de locação (padrão)";
    public const string NomeModeloAcordo = "Acordo de débitos de locação (padrão)";

    /// <summary>Marcadores dos modelos de locação (mostrados na tela de modelos).</summary>
    public static readonly IReadOnlyList<(string Chave, string Descricao)> Campos =
    [
        ("data.hoje", "Data de hoje por extenso (10 de novembro de 2026)"),
        ("data.hoje_curta", "Data de hoje (10/11/2026)"),
        ("contrato.codigo", "Código do contrato (LOC-0001)"),
        ("contrato.tipo", "Residencial ou comercial"),
        ("contrato.inicio", "Início do contrato"),
        ("contrato.fim", "Fim do prazo atual"),
        ("contrato.prazo_meses", "Prazo em meses"),
        ("contrato.dia_vencimento", "Dia do vencimento do aluguel"),
        ("contrato.forma_pagamento", "Regra do vencimento (mês seguinte ao vencido ou adiantado)"),
        ("contrato.observacoes", "Observações do contrato"),
        ("locador.nome", "Locador principal (ou o primeiro)"),
        ("locador.qualificacao", "Locador principal: nome, CPF/CNPJ, RG e endereço"),
        ("locadores.nomes", "Todos os locadores, separados por vírgula"),
        ("locadores.qualificacao", "Todos os locadores qualificados, um por linha (com % quando há mais de um)"),
        ("locatario.nome", "Locatário principal (ou o primeiro)"),
        ("locatario.cpf_cnpj", "CPF/CNPJ do locatário"),
        ("locatario.rg", "RG do locatário"),
        ("locatario.endereco", "Endereço do locatário no cadastro"),
        ("locatario.telefone", "Telefone do locatário"),
        ("locatario.email", "E-mail do locatário"),
        ("locatario.qualificacao", "Locatário principal: nome, CPF/CNPJ, RG e endereço"),
        ("locatarios.qualificacao", "Todos os locatários qualificados, um por linha"),
        ("fiador.nome", "Fiador (o primeiro)"),
        ("fiadores.qualificacao", "Fiadores qualificados, um por linha (\"Não há.\" sem fiador)"),
        ("imovel.codigo", "Código do imóvel"),
        ("imovel.tipo", "Tipo do imóvel"),
        ("imovel.endereco", "Endereço completo do imóvel"),
        ("imovel.cidade", "Cidade do imóvel (foro e local da assinatura)"),
        ("imovel.registro", "Matrícula, cartório e inscrição municipal, só o que estiver preenchido (\"matrícula não informada\" sem nenhum)"),
        ("imovel.matricula", "Matrícula"),
        ("imovel.cartorio", "Cartório"),
        ("imovel.inscricao_municipal", "Inscrição municipal"),
        ("imovel.area_total", "Área total (m²)"),
        ("imovel.area_construida", "Área construída (m²)"),
        ("locacao.aluguel", "Aluguel mensal atual"),
        ("locacao.aluguel_extenso", "Aluguel por extenso"),
        ("locacao.condominio", "Condomínio mensal"),
        ("locacao.iptu", "IPTU mensal"),
        ("locacao.seguro_incendio", "Seguro incêndio mensal"),
        ("locacao.total_mensal", "Aluguel + encargos do mês"),
        ("locacao.reajuste", "Índice de reajuste (IGP-M, IPCA…)"),
        ("locacao.garantia", "Garantia (fiador, caução, seguro-fiança…)"),
        ("locacao.garantia_valor", "Valor da garantia"),
        ("locacao.garantia_detalhe", "Detalhe da garantia (apólice, banco…)"),
        ("locacao.garantia_completa", "Garantia com valor e detalhe (ex.: Caução · R$ 3.000,00 · conta poupança)"),
        ("locacao.multa_atraso", "Multa por atraso (%)"),
        ("locacao.juros_mes", "Juros de mora ao mês (%)"),
        ("locacao.multa_rescisoria", "Multa rescisória (nº de aluguéis)"),
        ("locacao.taxa_administracao", "Taxa de administração (%)"),
        ("administradora.nome", "Nome da administradora (Inova Bens)"),
        ("acordo.faturas", "Acordo: faturas incluídas, uma por linha"),
        ("acordo.debito", "Acordo: débito corrigido (com multa e juros até hoje)"),
        ("acordo.desconto", "Acordo: desconto concedido"),
        ("acordo.total", "Acordo: total a pagar"),
        ("acordo.total_extenso", "Acordo: total por extenso"),
        ("acordo.qtd_parcelas", "Acordo: número de parcelas (sem a entrada)"),
        ("acordo.parcelas", "Acordo: entrada e parcelas com vencimento, uma por linha"),
        ("acordo.observacao", "Acordo: observações"),
    ];

    /* ------------------------------ modelos ------------------------------ */

    /// <summary>
    /// Cria os modelos padrão de contrato e de acordo quando não existem (subida do sistema). O padrão não é editável na
    /// tela (o usuário envia o seu), então o texto acompanha o sistema: muda aqui, atualiza lá; ativo/desativado é mantido.
    /// </summary>
    public async Task GarantirModelosPadraoAsync(CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        foreach (var (tipo, nome, gerar) in new (TipoModeloDocumento, string, Func<byte[]>)[]
                 { (TipoModeloDocumento.CONTRATO_LOCACAO, NomeModeloContrato, ModeloContratoPadrao), (TipoModeloDocumento.ACORDO_LOCACAO, NomeModeloAcordo, ModeloAcordoPadrao) })
        {
            var docx = gerar();
            if (await db.ModelosDocumento.SingleOrDefaultAsync(m => m.Tipo == tipo && m.Padrao, ct) is { } existente)
            {
                if (!MesmoTexto(existente.Conteudo, docx))
                {
                    existente.Conteudo = docx;
                    existente.Tamanho = docx.Length;
                    existente.AtualizadoEm = DateTime.UtcNow;
                }
                continue;
            }
            db.ModelosDocumento.Add(new ModeloDocumento
            {
                Id = Guid.NewGuid(), Nome = nome, Tipo = tipo, Finalidade = FinalidadeLead.LOCACAO, Padrao = true,
                NomeArquivo = tipo == TipoModeloDocumento.CONTRATO_LOCACAO ? "contrato-locacao-padrao.docx" : "acordo-locacao-padrao.docx",
                Conteudo = docx, Tamanho = docx.Length,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Compara só o document.xml (o zip muda a cada geração por causa das datas internas).</summary>
    private static bool MesmoTexto(byte[] a, byte[] b)
    {
        static string Xml(byte[] docx)
        {
            using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(docx));
            using var r = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            return r.ReadToEnd();
        }
        try { return Xml(a) == Xml(b); }
        catch (Exception) { return false; }
    }

    /// <summary>Modelos ativos de um tipo (contrato ou acordo), o padrão primeiro.</summary>
    public async Task<IReadOnlyList<ModeloResumo>> ModelosAsync(UsuarioAtual u, TipoModeloDocumento tipo)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        return await db.ModelosDocumento.AsNoTracking().Where(m => m.Ativo && m.Tipo == tipo)
            .OrderBy(m => m.Padrao).ThenBy(m => m.Nome)
            .Select(m => new ModeloResumo(m.Id, m.Nome, m.Tipo, m.Finalidade, m.NomeArquivo, m.Tamanho, m.Ativo, m.CriadoEm))
            .ToListAsync();
    }

    /* ------------------------------ geração ------------------------------ */

    /// <summary>Documento do contrato (modelo de contrato) ou de um acordo registrado (<paramref name="acordoId"/>).</summary>
    public async Task<Arquivo> GerarAsync(UsuarioAtual u, Guid contratoId, Guid modeloId, Guid? acordoId = null)
    {
        u.Exigir(P.LocacaoVer);
        var d = await locacao.ObterAsync(u, contratoId);
        await using var db = await fabrica.CreateDbContextAsync();
        var m = await db.ModelosDocumento.AsNoTracking().SingleOrDefaultAsync(x => x.Id == modeloId && x.Ativo)
            ?? throw new NaoEncontradoException("Modelo não encontrado.");
        if (m.Tipo is not (TipoModeloDocumento.CONTRATO_LOCACAO or TipoModeloDocumento.ACORDO_LOCACAO))
            throw new RegraNegocioException("modelo", "Escolha um modelo de locação (contrato ou acordo).");
        AcordoCalculo? acordo = null;
        AcordoForm? acordoForm = null;
        if (m.Tipo == TipoModeloDocumento.ACORDO_LOCACAO)
        {
            var h = d.Historico.FirstOrDefault(x => x.Id == acordoId && x.Tipo == TipoHistoricoLocacao.ACORDO)
                ?? throw new RegraNegocioException("acordo", "Registre o acordo antes de gerar o documento.");
            var salvo = JsonSerializer.Deserialize<AcordoSalvo>(h.Dados ?? "{}")!;
            acordoForm = salvo.Form;
            acordo = salvo.Calculo();
        }
        var valores = await ValoresAsync(db, d, acordo, acordoForm);
        var docx = Docx.Preencher(m.Conteudo, valores);
        var nome = m.Tipo == TipoModeloDocumento.CONTRATO_LOCACAO ? $"contrato-{d.Codigo}" : $"acordo-{d.Codigo}-{DatasBR.Hoje():yyyy-MM-dd}";
        var arquivo = await conversor.ConverterAsync(docx) is { } pdf
            ? new Arquivo(pdf, $"{nome}.pdf", RelatoriosService.TipoPdf)
            : new Arquivo(docx, $"{nome}.docx", Docx.Tipo);
        audit.Registrar(db, u.Id, "contratos_locacao", contratoId, "EXPORTAR", depois: new { modelo = m.Nome, arquivo = arquivo.Nome });
        await db.SaveChangesAsync();
        return arquivo;
    }

    /// <summary>Gera o documento e guarda na pasta do contrato no Drive (fica registrado no histórico).</summary>
    public async Task<DocumentoSalvo> SalvarNoDriveAsync(UsuarioAtual u, Guid contratoId, Guid modeloId, Guid? acordoId = null)
    {
        u.Exigir(P.LocacaoEditar);
        var arquivo = await GerarAsync(u, contratoId, modeloId, acordoId);
        return await GuardarAsync(u, contratoId, arquivo.Nome, arquivo.Conteudo, arquivo.Tipo, "Documento gerado e guardado no Drive");
    }

    /// <summary>Envia um arquivo (contrato assinado, laudo de vistoria, comprovante…) para a pasta do contrato.</summary>
    public async Task<DocumentoSalvo> EnviarArquivoAsync(UsuarioAtual u, Guid contratoId, string nome, byte[] conteudo, string contentType)
    {
        u.Exigir(P.LocacaoEditar);
        if (conteudo.Length == 0) throw new RegraNegocioException("arquivo", "Arquivo vazio.");
        if (conteudo.Length > ImoveisService.TamanhoMaximoArquivo) throw new RegraNegocioException("arquivo", "Arquivo acima de 25 MB.");
        return await GuardarAsync(u, contratoId, Path.GetFileName(nome), conteudo, contentType, "Arquivo enviado para o Drive");
    }

    private async Task<DocumentoSalvo> GuardarAsync(UsuarioAtual u, Guid contratoId, string nome, byte[] conteudo, string tipo, string acao)
    {
        if (!armazenamento.Habilitado) throw new ServicoIndisponivelException("Google Drive não configurado: baixe o arquivo e guarde manualmente.");
        var pasta = await GarantirPastaAsync(u, contratoId);
        await PastasDriveService.Chamar(async () =>
        {
            using var s = new MemoryStream(conteudo);
            return await armazenamento.EnviarAsync(pasta, nome, s, tipo);
        });
        await using var db = await fabrica.CreateDbContextAsync();
        db.ContratoLocacaoHistorico.Add(new ContratoLocacaoHistorico
        {
            ContratoId = contratoId, Tipo = TipoHistoricoLocacao.DOCUMENTO, Data = DatasBR.Hoje(), Descricao = $"{acao}: {nome}", CriadoPor = u.Id,
        });
        audit.Registrar(db, u.Id, "contratos_locacao", contratoId, "ENVIAR_ARQUIVO", depois: new { arquivo = nome });
        await db.SaveChangesAsync();
        return new DocumentoSalvo(nome, pastas.Link(pasta));
    }

    /* ------------------------------ pasta do contrato ------------------------------ */

    /// <summary>Subpasta do contrato em "08 - Locação" (criada se falta; o id fica no contrato).</summary>
    public async Task<string> GarantirPastaAsync(UsuarioAtual u, Guid contratoId)
    {
        u.Exigir(P.LocacaoVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await db.ContratosLocacao.SingleOrDefaultAsync(x => x.Id == contratoId && db.Imoveis.VisiveisPara(u).Any(i => i.Id == x.ImovelId))
            ?? throw new NaoEncontradoException("Contrato não encontrado.");
        return await Pasta(db, c);
    }

    /// <summary>Link da pasta do contrato (null se ainda não existe ou sem Drive).</summary>
    public string? LinkPasta(ContratoLocacao c) => armazenamento.Habilitado ? pastas.Link(c.DrivePastaId) : null;

    public bool DriveHabilitado => armazenamento.Habilitado;

    /// <summary>Rotina diária: pasta "08 - Locação" dos imóveis para alugar e subpasta dos contratos ativos que ainda não têm.</summary>
    public async Task<int> CriarPastasPendentesAsync(CancellationToken ct = default)
    {
        if (!armazenamento.Habilitado) return 0;
        var n = await pastas.CriarPastasLocacaoPendentesAsync(ct);
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var contratos = await db.ContratosLocacao.Where(c => c.Status == StatusContratoLocacao.ATIVO && c.DrivePastaId == null).ToListAsync(ct);
        foreach (var c in contratos)
        {
            try { await Pasta(db, c, ct); n++; }
            catch (Exception e) when (e is not OperationCanceledException) { /* tenta de novo na próxima rodada */ }
        }
        return n;
    }

    private async Task<string> Pasta(InovabensDb db, ContratoLocacao c, CancellationToken ct = default)
    {
        if (c.DrivePastaId is { } existente) return existente;
        if (!armazenamento.Habilitado) throw new ServicoIndisponivelException("Google Drive não configurado.");
        var locatario = await db.ContratoLocacaoPartes.AsNoTracking().Where(p => p.ContratoId == c.Id && p.Papel == PapelContratoLocacao.LOCATARIO)
            .OrderBy(p => p.Pessoa!.NomeRazao).Select(p => p.Pessoa!.NomeRazao).FirstOrDefaultAsync(ct);
        var id = await pastas.PastaContratoLocacaoAsync(c.ImovelId, NomePasta(c, locatario), ct);
        c.DrivePastaId = id;
        await db.SaveChangesAsync(ct);
        return id;
    }

    /// <summary>"LOC-0001 - Fulano de Tal - desde 11-2026" (sem barras, que o Drive aceita mas confundem caminhos).</summary>
    public static string NomePasta(ContratoLocacao c, string? locatario)
    {
        var nome = $"{c.Codigo} - {(Texto.Limpo(locatario) ?? "sem locatário")} - desde {c.Inicio:MM-yyyy}";
        return nome.Replace('/', '-').Replace('\\', '-');
    }

    /* ------------------------------ acordo ------------------------------ */

    /// <summary>Débito corrigido (saldo + multa e juros até hoje), desconto, entrada e parcelas iguais (sobra na última).</summary>
    public async Task<AcordoCalculo> SimularAcordoAsync(UsuarioAtual u, Guid contratoId, AcordoForm form)
    {
        u.Exigir(P.LocacaoVer);
        Validacao.Validar(form);
        var d = await locacao.ObterAsync(u, contratoId);
        return Calcular(d, form, DatasBR.Hoje());
    }

    /// <summary>Registra o acordo no histórico do contrato (os dados ficam guardados para gerar o documento).</summary>
    public async Task<Guid> RegistrarAcordoAsync(UsuarioAtual u, Guid contratoId, AcordoForm form)
    {
        u.Exigir(P.LocacaoEditar);
        Validacao.Validar(form);
        var d = await locacao.ObterAsync(u, contratoId);
        var calc = Calcular(d, form, DatasBR.Hoje());
        await using var db = await fabrica.CreateDbContextAsync();
        var h = new ContratoLocacaoHistorico
        {
            ContratoId = contratoId, Tipo = TipoHistoricoLocacao.ACORDO, Data = DatasBR.Hoje(), ValorAnterior = calc.Debito, ValorNovo = calc.Total,
            Descricao = $"Acordo de {calc.Faturas.Count} fatura(s): débito {Texto.Moeda(calc.Debito)}"
                + (calc.Desconto > 0 ? $", desconto {Texto.Moeda(calc.Desconto)}" : "") + $", total {Texto.Moeda(calc.Total)}\n"
                + string.Join("\n", calc.Parcelas.Select(LinhaParcela))
                + (Texto.Limpo(form.Observacao) is { } o ? $"\n{o}" : ""),
            Dados = JsonSerializer.Serialize(AcordoSalvo.De(form, calc)),
            CriadoPor = u.Id,
        };
        db.ContratoLocacaoHistorico.Add(h);
        audit.Registrar(db, u.Id, "contratos_locacao", contratoId, "ACORDO", depois: new { calc.Debito, calc.Desconto, calc.Total, parcelas = calc.Parcelas.Count });
        await db.SaveChangesAsync();
        return h.Id;
    }

    internal static AcordoCalculo Calcular(ContratoDetalhe d, AcordoForm f, DateOnly hoje)
    {
        var c = d.Contrato;
        var faturas = d.Faturas.Where(x => f.FaturaIds.Contains(x.Id) && x.Saldo > 0).OrderBy(x => x.Competencia).ToList();
        if (faturas.Count == 0) throw new RegraNegocioException(nameof(f.FaturaIds), "As faturas escolhidas não têm saldo em aberto.");
        var linhas = faturas.Select(x => (Ref: $"{x.Competencia:MM/yyyy}{(x.Rescisao ? " (acerto da rescisão)" : "")} · venc. {Texto.Data(x.Vencimento)}",
            x.Saldo, Acr: CalculoLocacao.Atraso(x.Saldo, x.Vencimento, hoje, c.MultaAtrasoPct, c.JurosMesPct).Total)).ToList();
        var debito = linhas.Sum(x => x.Saldo + x.Acr);
        if (f.Desconto > debito) throw new RegraNegocioException(nameof(f.Desconto), "Desconto maior que o débito.");
        var total = debito - f.Desconto;
        if (f.Entrada > total) throw new RegraNegocioException(nameof(f.Entrada), "Entrada maior que o total do acordo.");
        var parcelas = new List<ParcelaAcordo>();
        var inicio = f.PrimeiroVencimento!.Value;
        if (f.Entrada > 0) parcelas.Add(new(0, inicio, f.Entrada, true));
        var resto = total - f.Entrada;
        if (resto > 0)
        {
            var valor = decimal.Round(resto / f.Parcelas, 2, MidpointRounding.AwayFromZero);
            var primeira = f.Entrada > 0 ? inicio.AddMonths(1) : inicio;
            for (var i = 1; i <= f.Parcelas; i++)
                parcelas.Add(new(i, primeira.AddMonths(i - 1), i == f.Parcelas ? resto - valor * (f.Parcelas - 1) : valor, false));
        }
        return new([.. linhas.Select(x => (x.Ref, x.Saldo, x.Acr))], debito, f.Desconto, total, parcelas);
    }

    private static string LinhaParcela(ParcelaAcordo p) =>
        $"{(p.Entrada ? "Entrada" : $"Parcela {p.Numero}")}: {Texto.Moeda(p.Valor)} com vencimento em {Texto.Data(p.Vencimento)}";

    /// <summary>O que vai para o JSON do histórico (form + cálculo do dia do acordo, que não muda depois).</summary>
    private sealed record AcordoSalvo(AcordoForm Form, AcordoCalculoJson Calc)
    {
        public static AcordoSalvo De(AcordoForm form, AcordoCalculo c) => new(form, new AcordoCalculoJson(
            [.. c.Faturas.Select(f => new FaturaJson(f.Referencia, f.Saldo, f.Acrescimos))], c.Debito, c.Desconto, c.Total, [.. c.Parcelas]));

        public AcordoCalculo Calculo() => new([.. Calc.Faturas.Select(f => (f.Referencia, f.Saldo, f.Acrescimos))], Calc.Debito, Calc.Desconto, Calc.Total, Calc.Parcelas);
    }

    private sealed record FaturaJson(string Referencia, decimal Saldo, decimal Acrescimos);

    private sealed record AcordoCalculoJson(List<FaturaJson> Faturas, decimal Debito, decimal Desconto, decimal Total, List<ParcelaAcordo> Parcelas);

    /* ------------------------------ marcadores ------------------------------ */

    /// <summary>Valores dos marcadores de um contrato (testes e conferência).</summary>
    public async Task<IReadOnlyDictionary<string, string>> ValoresAsync(UsuarioAtual u, Guid contratoId)
    {
        var d = await locacao.ObterAsync(u, contratoId);
        await using var db = await fabrica.CreateDbContextAsync();
        return await ValoresAsync(db, d, null, null);
    }

    private static string Juntar(string sep, params string?[] partes) => string.Join(sep, partes.Where(x => !string.IsNullOrWhiteSpace(x)));

    private static string Endereco(string? logradouro, string? numero, string? complemento, string? bairro, string? cidade, string? uf, string? cep) =>
        Juntar(", ", Juntar(", ", logradouro, numero), complemento, bairro, Juntar("/", cidade, uf), cep is null ? null : $"CEP {cep}");

    private static string Qualificacao(Pessoa p) => Juntar(", ", p.NomeRazao,
        p.CpfCnpj is { } doc ? $"inscrito(a) no {(p.Tipo == TipoPessoa.PJ ? "CNPJ" : "CPF")} sob o nº {Formatos.Documento(doc)}" : null,
        p.Rg is { } rg ? $"RG {rg}" : null,
        Endereco(p.Logradouro, p.Numero, p.Complemento, p.Bairro, p.Cidade, p.Uf, p.Cep) is { Length: > 0 } e ? $"residente e domiciliado(a) em {e}" : null);

    private static async Task<Dictionary<string, string>> ValoresAsync(InovabensDb db, ContratoDetalhe d, AcordoCalculo? acordo, AcordoForm? acordoForm)
    {
        var c = d.Contrato;
        var im = await db.Imoveis.AsNoTracking().SingleAsync(i => i.Id == c.ImovelId);
        var ids = d.Partes.Select(p => p.PessoaId).Distinct().ToList();
        var pessoas = await db.Pessoas.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        List<(Pessoa P, decimal? Pct)> Do(PapelContratoLocacao papel) =>
            [.. d.Partes.Where(p => p.Papel == papel && pessoas.ContainsKey(p.PessoaId)).Select(p => (pessoas[p.PessoaId], p.Percentual))];
        var locadores = Do(PapelContratoLocacao.LOCADOR);
        var locatarios = Do(PapelContratoLocacao.LOCATARIO);
        var fiadores = Do(PapelContratoLocacao.FIADOR);
        var locador = locadores.FirstOrDefault().P;
        var locatario = locatarios.FirstOrDefault().P;
        var hoje = DatasBR.Hoje();
        string Pct(decimal v) => v.ToString("0.##", Texto.PtBr) + "%";
        var mensal = c.ValorAluguel + c.ValorCondominio + c.ValorIptu + c.ValorSeguroIncendio;
        var v = new Dictionary<string, string>
        {
            ["data.hoje"] = DocumentosService.DataPorExtenso(hoje),
            ["data.hoje_curta"] = Texto.Data(hoje),
            ["contrato.codigo"] = c.Codigo,
            ["contrato.tipo"] = c.Tipo == TipoLocacao.COMERCIAL ? "comercial" : "residencial",
            ["contrato.inicio"] = Texto.Data(c.Inicio),
            ["contrato.fim"] = Texto.Data(c.Fim),
            ["contrato.prazo_meses"] = c.PrazoMeses.ToString(),
            ["contrato.dia_vencimento"] = c.DiaVencimento.ToString(),
            ["contrato.forma_pagamento"] = c.PagamentoAntecipado
                ? $"todo dia {c.DiaVencimento} do próprio mês de uso (pagamento antecipado, permitido por não haver garantia)"
                : $"todo dia {c.DiaVencimento} do mês seguinte ao vencido",
            ["contrato.observacoes"] = c.Observacoes ?? "",
            ["locador.nome"] = locador?.NomeRazao ?? "",
            ["locador.qualificacao"] = locador is null ? "" : Qualificacao(locador),
            ["locadores.nomes"] = string.Join(", ", locadores.Select(l => l.P.NomeRazao)),
            ["locadores.qualificacao"] = string.Join("\n", locadores.Select(l => Qualificacao(l.P) + (locadores.Count > 1 && l.Pct is { } p ? $" ({Pct(p)})" : ""))),
            ["locatario.nome"] = locatario?.NomeRazao ?? "",
            ["locatario.cpf_cnpj"] = locatario?.CpfCnpj is { } dl ? Formatos.Documento(dl) : "",
            ["locatario.rg"] = locatario?.Rg ?? "",
            ["locatario.endereco"] = locatario is null ? "" : Endereco(locatario.Logradouro, locatario.Numero, locatario.Complemento, locatario.Bairro, locatario.Cidade, locatario.Uf, locatario.Cep),
            ["locatario.telefone"] = locatario?.Whatsapp ?? locatario?.Telefone ?? "",
            ["locatario.email"] = locatario?.Email ?? "",
            ["locatario.qualificacao"] = locatario is null ? "" : Qualificacao(locatario),
            ["locatarios.qualificacao"] = string.Join("\n", locatarios.Select(l => Qualificacao(l.P))),
            ["fiador.nome"] = fiadores.FirstOrDefault().P?.NomeRazao ?? "",
            ["fiadores.qualificacao"] = fiadores.Count == 0 ? "Não há." : string.Join("\n", fiadores.Select(l => Qualificacao(l.P))),
            ["imovel.codigo"] = im.Codigo,
            ["imovel.tipo"] = Juntar(" / ", im.Tipo, im.Subtipo),
            ["imovel.endereco"] = Endereco(im.Logradouro, im.Numero, im.Complemento, im.Bairro, im.Cidade, im.Uf, im.Cep),
            ["imovel.cidade"] = Juntar("/", im.Cidade, im.Uf),
            ["imovel.registro"] = Juntar(", ", im.Matricula is { } mt ? $"matrícula nº {mt}" + (im.Cartorio is { } ca ? $" do {ca}" : "") : null,
                im.InscricaoMunicipal is { } ins ? $"inscrição municipal {ins}" : null) is { Length: > 0 } reg ? reg : "matrícula não informada",
            ["imovel.matricula"] = im.Matricula ?? "",
            ["imovel.cartorio"] = im.Cartorio ?? "",
            ["imovel.inscricao_municipal"] = im.InscricaoMunicipal ?? "",
            ["imovel.area_total"] = im.AreaTotalM2?.ToString("0.##", Texto.PtBr) ?? "",
            ["imovel.area_construida"] = im.AreaConstruidaM2?.ToString("0.##", Texto.PtBr) ?? "",
            ["locacao.aluguel"] = Texto.Moeda(c.ValorAluguel),
            ["locacao.aluguel_extenso"] = Extenso.Reais(c.ValorAluguel),
            ["locacao.condominio"] = Texto.Moeda(c.ValorCondominio),
            ["locacao.iptu"] = Texto.Moeda(c.ValorIptu),
            ["locacao.seguro_incendio"] = Texto.Moeda(c.ValorSeguroIncendio),
            ["locacao.total_mensal"] = Texto.Moeda(mensal),
            ["locacao.reajuste"] = c.IndiceReajuste == CodigoIndice.IGPM ? "IGP-M/FGV" : c.IndiceReajuste == CodigoIndice.IPCA ? "IPCA/IBGE" : c.IndiceReajuste.ToString(),
            ["locacao.garantia"] = PropostasService.Rotulo(c.Garantia),
            ["locacao.garantia_valor"] = c.GarantiaValor is { } gv ? Texto.Moeda(gv) : "",
            ["locacao.garantia_detalhe"] = c.GarantiaDetalhe ?? "",
            ["locacao.garantia_completa"] = Juntar(" · ", PropostasService.Rotulo(c.Garantia), c.GarantiaValor is { } gv2 ? Texto.Moeda(gv2) : null, c.GarantiaDetalhe),
            ["locacao.multa_atraso"] = Pct(c.MultaAtrasoPct),
            ["locacao.juros_mes"] = Pct(c.JurosMesPct),
            ["locacao.multa_rescisoria"] = c.MultaRescisoriaAlugueis.ToString("0.#", Texto.PtBr),
            ["locacao.taxa_administracao"] = Pct(c.TaxaAdministracaoPct),
            ["administradora.nome"] = "Inova Bens",
        };
        if (acordo is not null)
        {
            v["acordo.faturas"] = string.Join("\n", acordo.Faturas.Select(f =>
                $"{f.Referencia}: {Texto.Moeda(f.Saldo)}" + (f.Acrescimos > 0 ? $" + multa e juros {Texto.Moeda(f.Acrescimos)}" : "")));
            v["acordo.debito"] = Texto.Moeda(acordo.Debito);
            v["acordo.desconto"] = Texto.Moeda(acordo.Desconto);
            v["acordo.total"] = Texto.Moeda(acordo.Total);
            v["acordo.total_extenso"] = Extenso.Reais(acordo.Total);
            v["acordo.qtd_parcelas"] = acordo.Parcelas.Count(p => !p.Entrada).ToString();
            v["acordo.parcelas"] = string.Join("\n", acordo.Parcelas.Select(LinhaParcela));
            v["acordo.observacao"] = Texto.Limpo(acordoForm?.Observacao) ?? "";
        }
        return v;
    }

    /* ------------------------------ modelos padrão ------------------------------ */

    private static Docx.Paragrafo T(string texto) => new(texto, Negrito: true, Centralizado: true, TamanhoPt: 13, EspacoDepoisPt: 12);
    private static Docx.Paragrafo S(string texto) => new(texto, Negrito: true, EspacoDepoisPt: 4);
    private static Docx.Paragrafo N(string texto) => new(texto);

    /// <summary>Contrato de locação padrão (Lei 8.245/91), com os marcadores {{campo}}.</summary>
    public static byte[] ModeloContratoPadrao() => Docx.Criar(
    [
        T("CONTRATO DE LOCAÇÃO DE IMÓVEL"),
        N("Locação {{contrato.tipo}} · contrato nº {{contrato.codigo}}"),
        S("LOCADOR(ES)"),
        N("{{locadores.qualificacao}}"),
        S("LOCATÁRIO(S)"),
        N("{{locatarios.qualificacao}}"),
        S("FIADOR(ES)"),
        N("{{fiadores.qualificacao}}"),
        S("ADMINISTRADORA"),
        N("{{administradora.nome}}, que administra a locação em nome do(s) LOCADOR(ES)."),
        N("As partes acima qualificadas celebram o presente contrato de locação, que se regerá pela Lei nº 8.245/91 e pelas cláusulas seguintes."),
        S("CLÁUSULA 1ª – DO IMÓVEL"),
        N("O objeto deste contrato é o imóvel situado em {{imovel.endereco}}, código {{imovel.codigo}}, {{imovel.registro}}, destinado exclusivamente a uso {{contrato.tipo}}."),
        S("CLÁUSULA 2ª – DO PRAZO"),
        N("A locação tem prazo de {{contrato.prazo_meses}} meses, com início em {{contrato.inicio}} e término em {{contrato.fim}}. Findo o prazo e permanecendo o LOCATÁRIO no imóvel sem oposição do LOCADOR, a locação prorroga-se por prazo indeterminado, nas mesmas condições."),
        S("CLÁUSULA 3ª – DO ALUGUEL E DOS ENCARGOS"),
        N("O aluguel mensal é de {{locacao.aluguel}} ({{locacao.aluguel_extenso}}), a ser pago {{contrato.forma_pagamento}}, por meio indicado pela ADMINISTRADORA. Além do aluguel, o LOCATÁRIO pagará os encargos do imóvel: condomínio ({{locacao.condominio}}), IPTU ({{locacao.iptu}}) e seguro contra incêndio ({{locacao.seguro_incendio}}), totalizando {{locacao.total_mensal}} por mês nos valores atuais, além das contas de consumo (água, luz, gás, telefone e internet)."),
        N("Parágrafo único. O primeiro e o último mês serão cobrados proporcionalmente aos dias de uso."),
        S("CLÁUSULA 4ª – DO REAJUSTE"),
        N("O aluguel será reajustado a cada 12 (doze) meses, contados do início da locação, pela variação acumulada do {{locacao.reajuste}}. Se a variação acumulada for negativa, o aluguel permanecerá inalterado."),
        S("CLÁUSULA 5ª – DO ATRASO"),
        N("O pagamento após o vencimento sujeitará o LOCATÁRIO à multa de {{locacao.multa_atraso}} sobre o valor devido e a juros de mora de {{locacao.juros_mes}} ao mês, calculados dia a dia, sem prejuízo da correção monetária e das demais medidas legais."),
        S("CLÁUSULA 6ª – DA GARANTIA"),
        N("Em garantia das obrigações deste contrato, fica estabelecida a modalidade: {{locacao.garantia_completa}}. É vedada mais de uma modalidade de garantia (art. 37 da Lei 8.245/91)."),
        S("CLÁUSULA 7ª – DA CONSERVAÇÃO E DAS BENFEITORIAS"),
        N("O LOCATÁRIO recebe o imóvel no estado descrito no laudo de vistoria de entrada, que integra este contrato, e se obriga a restituí-lo no mesmo estado, ressalvado o desgaste natural do uso. Benfeitorias e modificações dependem de autorização prévia e por escrito do LOCADOR e não geram direito a indenização ou retenção, salvo as necessárias."),
        S("CLÁUSULA 8ª – DA RESCISÃO ANTECIPADA"),
        N("Se o LOCATÁRIO devolver o imóvel antes do término do prazo, pagará multa equivalente a {{locacao.multa_rescisoria}} aluguéis vigentes, proporcional ao período restante do contrato (art. 4º da Lei 8.245/91), dispensada nos casos previstos no parágrafo único do mesmo artigo. Na prorrogação por prazo indeterminado, o LOCATÁRIO deverá avisar com 30 (trinta) dias de antecedência, sob pena de pagar o valor correspondente aos dias de aviso faltantes."),
        S("CLÁUSULA 9ª – DA VENDA DO IMÓVEL"),
        N("No caso de venda, o LOCATÁRIO terá direito de preferência na aquisição, em igualdade de condições com terceiros, devendo manifestar-se em até 30 (trinta) dias da notificação (art. 27 da Lei 8.245/91)."),
        S("CLÁUSULA 10ª – DA DEVOLUÇÃO DAS CHAVES"),
        N("A devolução do imóvel se dará mediante vistoria de saída e entrega das chaves à ADMINISTRADORA, com a quitação dos aluguéis, encargos e contas de consumo até essa data."),
        S("CLÁUSULA 11ª – DISPOSIÇÕES GERAIS"),
        N("{{contrato.observacoes}}"),
        N("Fica eleito o foro da comarca de {{imovel.cidade}} para dirimir as questões deste contrato."),
        N("E, por estarem assim justos e contratados, assinam o presente em vias de igual teor, com duas testemunhas."),
        N("{{imovel.cidade}}, {{data.hoje}}."),
        N("\n\n_______________________________________\nLOCADOR(ES): {{locadores.nomes}}"),
        N("\n\n_______________________________________\nLOCATÁRIO: {{locatario.nome}}"),
        N("\n\n_______________________________________\nFIADOR: {{fiador.nome}}"),
        N("\n\n_______________________________________\nADMINISTRADORA: {{administradora.nome}}"),
        N("\nTestemunhas:\n\n1. _______________________________ CPF: ______________\n\n2. _______________________________ CPF: ______________"),
    ]);

    /// <summary>Acordo de débitos de locação padrão (confissão de dívida e parcelamento).</summary>
    public static byte[] ModeloAcordoPadrao() => Docx.Criar(
    [
        T("TERMO DE ACORDO E CONFISSÃO DE DÍVIDA DE LOCAÇÃO"),
        N("Contrato de locação nº {{contrato.codigo}} · imóvel {{imovel.codigo}} · {{imovel.endereco}}"),
        S("CREDOR(ES) / LOCADOR(ES)"),
        N("{{locadores.qualificacao}}, representado(s) pela administradora {{administradora.nome}}."),
        S("DEVEDOR(ES) / LOCATÁRIO(S)"),
        N("{{locatarios.qualificacao}}"),
        S("CLÁUSULA 1ª – DO DÉBITO"),
        N("O LOCATÁRIO reconhece e confessa dever os valores abaixo, referentes a aluguéis e encargos em aberto, já acrescidos de multa e juros até esta data:"),
        N("{{acordo.faturas}}"),
        N("Débito total: {{acordo.debito}}. Desconto concedido neste acordo: {{acordo.desconto}}. Valor do acordo: {{acordo.total}} ({{acordo.total_extenso}})."),
        S("CLÁUSULA 2ª – DO PAGAMENTO"),
        N("O valor do acordo será pago da seguinte forma:"),
        N("{{acordo.parcelas}}"),
        S("CLÁUSULA 3ª – DO DESCUMPRIMENTO"),
        N("O atraso de qualquer parcela por mais de 5 (cinco) dias tornará vencido o saldo restante, que voltará a ser cobrado sem o desconto concedido, com multa de {{locacao.multa_atraso}} e juros de {{locacao.juros_mes}} ao mês, podendo o LOCADOR adotar as medidas judiciais cabíveis, inclusive a ação de despejo por falta de pagamento."),
        S("CLÁUSULA 4ª – DOS ALUGUÉIS SEGUINTES"),
        N("Este acordo não altera o contrato de locação: os aluguéis e encargos que vencerem a partir desta data continuam devidos nos vencimentos normais, independentemente das parcelas acima."),
        S("OBSERVAÇÕES"),
        N("{{acordo.observacao}}"),
        N("{{imovel.cidade}}, {{data.hoje}}."),
        N("\n\n_______________________________________\nLOCATÁRIO: {{locatario.nome}}"),
        N("\n\n_______________________________________\nFIADOR: {{fiador.nome}}"),
        N("\n\n_______________________________________\n{{administradora.nome}}, pelo(s) LOCADOR(ES)"),
    ]);
}
