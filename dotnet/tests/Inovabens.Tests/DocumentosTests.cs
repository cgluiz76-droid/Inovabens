using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Inovabens.Core.Comum;
using Inovabens.Core.Crm;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

public class ExtensoTests
{
    [Theory]
    [InlineData(1, "um real")]
    [InlineData(2.5, "dois reais e cinquenta centavos")]
    [InlineData(0.01, "um centavo")]
    [InlineData(100, "cem reais")]
    [InlineData(101, "cento e um reais")]
    [InlineData(1500, "mil e quinhentos reais")]
    [InlineData(2005, "dois mil e cinco reais")]
    [InlineData(350000, "trezentos e cinquenta mil reais")]
    [InlineData(1000000, "um milhão de reais")]
    [InlineData(2000000, "dois milhões de reais")]
    [InlineData(1200000, "um milhão e duzentos mil reais")]
    [InlineData(1250300, "um milhão, duzentos e cinquenta mil e trezentos reais")]
    [InlineData(17, "dezessete reais")]
    public void Reais_por_extenso(decimal valor, string esperado) => Assert.Equal(esperado, Extenso.Reais(valor));

    [Fact]
    public void Data_por_extenso() => Assert.Equal("10 de novembro de 2026", DocumentosService.DataPorExtenso(new DateOnly(2026, 11, 10)));
}

public class DocxTests
{
    private const string Ns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>.docx mínimo; cada parágrafo é uma lista de trechos (runs), para simular o Word partindo o marcador.</summary>
    internal static byte[] Modelo(string[][] paragrafos, string? rodape = null)
    {
        static string Corpo(string[][] ps) => string.Concat(ps.Select(p =>
            "<w:p>" + string.Concat(p.Select(t => $"<w:r><w:rPr><w:b/></w:rPr><w:t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(t)}</w:t></w:r>")) + "</w:p>"));
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string nome, string xml)
            {
                using var s = new StreamWriter(zip.CreateEntry(nome).Open(), new UTF8Encoding(false));
                s.Write(xml);
            }
            Add("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Add("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{Ns}\"><w:body>{Corpo(paragrafos)}</w:body></w:document>");
            if (rodape is not null) Add("word/footer1.xml", $"<?xml version=\"1.0\"?><w:ftr xmlns:w=\"{Ns}\">{Corpo([[rodape]])}</w:ftr>");
        }
        return ms.ToArray();
    }

    internal static XDocument Parte(byte[] docx, string nome)
    {
        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        using var s = zip.GetEntry(nome)!.Open();
        return XDocument.Load(s);
    }

    internal static string Texto(XDocument d) =>
        string.Join("|", d.Descendants(XName.Get("p", Ns)).Select(p => string.Concat(p.Descendants().Select(e =>
            e.Name.LocalName == "t" ? e.Value : e.Name.LocalName == "br" ? "\n" : ""))));

    [Fact]
    public void Preenche_marcador_partido_em_varios_trechos_e_mantem_o_resto()
    {
        var m = Modelo([["Comprador: {{clie", "nte.", "nome}}, CPF {{ cliente.cpf_cnpj }}."], ["Sem marcador"], ["{{desconhecido}} fica"]]);
        Assert.Equal(["cliente.nome", "cliente.cpf_cnpj", "desconhecido"], Docx.Marcadores(m));
        var r = Docx.Preencher(m, new Dictionary<string, string> { ["cliente.nome"] = "Ana & Filhos <Ltda>", ["cliente.cpf_cnpj"] = "123" });
        Assert.Equal("Comprador: Ana & Filhos <Ltda>, CPF 123.|Sem marcador|{{desconhecido}} fica", Texto(Parte(r, "word/document.xml")));
        // a formatação do trecho (negrito) continua
        Assert.All(Parte(r, "word/document.xml").Descendants(XName.Get("r", Ns)), run => Assert.NotNull(run.Element(XName.Get("rPr", Ns))));
    }

    [Fact]
    public void Quebra_de_linha_no_valor_vira_quebra_no_word_e_rodape_tambem_e_preenchido()
    {
        var m = Modelo([["Pagamento:", " {{pagamento.itens}}"]], rodape: "Proposta nº {{proposta.numero}}");
        var r = Docx.Preencher(m, new Dictionary<string, string> { ["pagamento.itens"] = "Entrada\nFinanciamento", ["proposta.numero"] = "3" });
        Assert.Equal("Pagamento: Entrada\nFinanciamento", Texto(Parte(r, "word/document.xml")));
        Assert.Single(Parte(r, "word/document.xml").Descendants(XName.Get("br", Ns)));
        Assert.Equal("Proposta nº 3", Texto(Parte(r, "word/footer1.xml")));
        Assert.True(Docx.Valido(r));
    }

    [Fact]
    public void Arquivo_que_nao_e_docx_e_invalido()
    {
        Assert.False(Docx.Valido(Encoding.UTF8.GetBytes("não sou um zip")));
        Assert.True(Docx.Valido(Modelo([["oi"]])));
    }
}

[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class DocumentosTests : IAsyncLifetime
{
    private IServiceScope _escopo = default!;
    private T S<T>() where T : notnull => _escopo.ServiceProvider.GetRequiredService<T>();
    private readonly string sufixo = Guid.NewGuid().ToString("N")[..6];
    private UsuarioAtual admin = default!, corretor = default!;

    public async Task InitializeAsync()
    {
        _escopo = ExtratoComprovantesTests.Compartilhado.CreateScope(); // provider compartilhado (limite de 20 do EF)
        _escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        var auth = S<AuthService>();
        admin = (await auth.LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        corretor = (await auth.LoginAsync("carla@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
    }

    public Task DisposeAsync()
    {
        _escopo.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> PropostaVenda()
    {
        var casa = await S<ImoveisService>().CriarAsync(corretor, new ImovelForm
        {
            NomeDrive = Guid.NewGuid().ToString(), Titulo = $"Casa Documento {sufixo}", Tipo = "casa", PrecoVenda = 400_000,
            CorretorResponsavelId = corretor.Id, Cidade = "São José do Rio Preto", Uf = "SP", Matricula = "12.345",
        });
        var lead = await S<CrmService>().CriarAsync(corretor, new LeadForm { NovoNome = $"Cliente Documento {sufixo}", ResponsavelId = corretor.Id });
        var f = new PropostaForm
        {
            ImovelId = casa, Titulo = "Levar ao proprietário", Inicio = DatasBR.Agora().Date.AddDays(1).AddHours(10), Condicoes = "Chaves na escritura",
            Itens = [new() { Tipo = TipoItemProposta.ENTRADA, Valor = 100_000 }, new() { Tipo = TipoItemProposta.FINANCIAMENTO, Valor = 250_000, Instituicao = "Caixa", Parcelas = 360 }],
        };
        return await S<PropostasService>().CriarAsync(corretor, lead, f);
    }

    [Fact]
    public async Task Valores_cobrem_todos_os_marcadores_e_pdf_padrao_e_gerado()
    {
        var docs = S<DocumentosService>();
        var id = await PropostaVenda();
        var v = await docs.ValoresAsync(corretor, id);
        Assert.All(DocumentosService.Campos, c => Assert.True(v.ContainsKey(c.Chave), c.Chave));
        Assert.Equal($"Cliente Documento {sufixo}", v["cliente.nome"]);
        Assert.Equal("trezentos e cinquenta mil reais", v["proposta.total_extenso"]);
        Assert.Equal("-12,5%", v["proposta.diferenca"]);
        Assert.Contains("Financiamento bancário: R$ 250.000,00 (Caixa; 360 meses)", v["pagamento.itens"]);
        Assert.Equal("12.345", v["imovel.matricula"]);

        var pdf = await docs.GerarAsync(corretor, id);
        Assert.Equal("application/pdf", pdf.Tipo);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf.Conteudo, 0, 4));
        Assert.EndsWith("-n1.pdf", pdf.Nome);
    }

    [Fact]
    public async Task Modelo_enviado_pelo_admin_e_preenchido_com_os_dados_da_proposta()
    {
        var docs = S<DocumentosService>();
        var modelo = DocxTests.Modelo([["Eu, {{cliente.nome}}, ofereço {{proposta.total}} ({{proposta.total_extenso}}) pelo {{imovel.codigo}}. {{campo.inventado}}"]]);
        // só administrador envia; só .docx
        await Assert.ThrowsAsync<AcessoNegadoException>(() => docs.EnviarModeloAsync(corretor, "X", TipoModeloDocumento.PROPOSTA, null, "a.docx", modelo));
        await Assert.ThrowsAsync<RegraNegocioException>(() => docs.EnviarModeloAsync(admin, "X", TipoModeloDocumento.PROPOSTA, null, "a.doc", modelo));
        await Assert.ThrowsAsync<RegraNegocioException>(() => docs.EnviarModeloAsync(admin, "X", TipoModeloDocumento.PROPOSTA, null, "a.docx", [1, 2, 3]));

        var r = await docs.EnviarModeloAsync(admin, $"Proposta {sufixo}", TipoModeloDocumento.PROPOSTA, FinalidadeLead.COMPRA, "proposta.docx", modelo);
        Assert.Equal(["campo.inventado"], r.Desconhecidos);
        Assert.Contains(await docs.ModelosAsync(corretor, FinalidadeLead.COMPRA), m => m.Id == r.Id);
        Assert.DoesNotContain(await docs.ModelosAsync(corretor, FinalidadeLead.LOCACAO), m => m.Id == r.Id);

        var id = await PropostaVenda();
        var arq = await docs.GerarAsync(corretor, id, r.Id);
        // sem LibreOffice no ambiente de teste sai o .docx preenchido
        if (arq.Tipo == Docx.Tipo)
        {
            var texto = DocxTests.Texto(DocxTests.Parte(arq.Conteudo, "word/document.xml"));
            Assert.Contains($"Eu, Cliente Documento {sufixo}, ofereço R$ 350.000,00 (trezentos e cinquenta mil reais) pelo IMV-", texto);
            Assert.Contains("{{campo.inventado}}", texto);
        }
        else Assert.Equal("application/pdf", arq.Tipo);

        // desativado some da lista e não gera mais
        await docs.AtivarModeloAsync(admin, r.Id, false);
        Assert.DoesNotContain(await docs.ModelosAsync(corretor, FinalidadeLead.COMPRA), m => m.Id == r.Id);
        await Assert.ThrowsAsync<NaoEncontradoException>(() => docs.GerarAsync(corretor, id, r.Id));
    }
}
