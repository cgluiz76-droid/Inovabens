using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;

namespace Inovabens.Tests;

/// <summary>Leitura das fichas do site Imobibrasil (páginas reais salvas em Dados/).</summary>
public class ImportacaoImobibrasilTests
{
    private static string Pagina(string nome) =>
        LeitorImobibrasil.Decodificar(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Dados", nome)));

    [Fact]
    public void Le_a_ficha_completa_em_iso_8859_1()
    {
        var a = LeitorImobibrasil.LerFicha(4145138, "u", Pagina("imobibrasil-4145138.html"));

        Assert.Equal("CA0060", a.Referencia);
        Assert.Equal("Casa", a.TipoOriginal);
        Assert.False(a.Locacao);
        Assert.Equal(500_000m, a.Preco);
        Assert.Equal(("Rua Arthur Truzzi", "580"), (a.Logradouro, a.Numero));
        Assert.Equal("casa", a.Complemento);
        Assert.Equal("São Francisco", a.Bairro);
        Assert.Equal(("São José do Rio Preto", "SP", "15086-180"), (a.Cidade, a.Uf, a.Cep));
        Assert.Equal(("Centro", "próximo UBS São Francisco"), (a.Zona, a.PontoReferencia));
        Assert.Equal((156.52m, 156.52m, 360m), (a.AreaTotalM2, a.AreaConstruidaM2, a.AreaTerrenoM2));
        Assert.Equal((12m, 12m, 30m, 30m), (a.TerrenoFrenteM, a.TerrenoFundoM, a.TerrenoEsquerdaM, a.TerrenoDireitaM));
        Assert.Equal("térreo", a.Pavimento);
        Assert.True(a.AceitaPermuta);
        Assert.Equal((3, 2, 2, 1), (a.Quartos, a.Banheiros, a.Vagas, a.Salas));
        Assert.Empty(a.OutrosCampos);

        Assert.Equal(30, a.Fotos.Count);
        Assert.Equal("FACHADA", a.Fotos[0].Legenda);
        Assert.Equal("202603261753154136.jpeg", a.Fotos[0].NomeArquivo);
        Assert.Contains(("Cômodos", "Despensa"), a.Caracteristicas);
        Assert.Contains(("Proximidades", "Escola"), a.Caracteristicas);

        Assert.StartsWith("Casa com 3 Quartos e Quintal Amplo no São Francisco", a.Descricao);
        Assert.Contains("- Dormitórios: 3 quartos amplos e bem ventilados.", a.Descricao);
        // frase partida por <strong> volta a ser uma linha só
        Assert.Contains("analisamos permutas e aceitamos veículos como 100% do pagamento.", a.Descricao);
        Assert.DoesNotContain("www.inovabens.com.br", a.Descricao);
    }

    [Fact]
    public void Le_video_medidas_e_caracteristicas_de_area_rural()
    {
        var a = LeitorImobibrasil.LerFicha(2751546, "u", Pagina("imobibrasil-2751546.html"));

        Assert.Equal("AR0004", a.Referencia);
        Assert.Equal("Área", a.TipoOriginal);
        Assert.Equal(7_700_000m, a.Preco);
        Assert.Equal("Fazenda Monte Alto (Zona Rural)", a.Bairro);
        Assert.Equal(("Rodovia SP-425", "km 178,8"), (a.Logradouro, a.Complemento));
        Assert.Equal((1349.20m, 22230.93m), (a.AreaConstruidaM2, a.AreaTerrenoM2));
        Assert.Equal((4, 4, 6), (a.Quartos, a.Suites, a.Banheiros));
        Assert.Equal("https://www.youtube.com/watch?v=GtvcXI15NRY", a.VideoUrl);
        Assert.Contains(("Características", "SPA / Hidromassagem"), a.Caracteristicas);
    }

    [Fact]
    public void Lista_traz_os_links_das_fichas()
    {
        var links = LeitorImobibrasil.LinksDaLista(Pagina("imobibrasil-lista2.html"));
        Assert.NotEmpty(links);
        Assert.All(links, l => Assert.StartsWith($"/imovel/{l.Id}/", l.Caminho));
        Assert.Equal(links.Count, links.Select(l => l.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("156,52 m²", 156.52)]
    [InlineData("30.416,98 m²", 30416.98)]
    [InlineData("3,76 Hectares", 37600)]
    [InlineData("5 Alqueires paulistas", 121000)]
    [InlineData("12 Metros", 12)]
    public void Converte_areas_para_m2(string texto, double esperado) =>
        Assert.Equal((decimal)esperado, LeitorImobibrasil.Area(texto));

    [Theory]
    [InlineData("Estrada de Cedral, sn", "Estrada de Cedral", "s/n", null)]
    [InlineData("Euclides da Cunha, 0", "Euclides da Cunha", "s/n", null)]
    [InlineData("Travessa Maria Cristina de Almeida, 5079, Lote 01", "Travessa Maria Cristina de Almeida", "5079", "Lote 01")]
    [InlineData("Rodovia SP-425", "Rodovia SP-425", null, null)]
    public void Separa_logradouro_e_numero(string texto, string rua, string? numero, string? extra) =>
        Assert.Equal((rua, numero, extra), LeitorImobibrasil.Endereco(texto));

    [Theory]
    [InlineData("Centro, diversas opções de lotes", "Centro")]
    [InlineData("Mais Parque - O MELHOR EM FRENTE AO RECINTO DE EXPOSIÇÕES!!!", "Mais Parque")]
    [InlineData("Fazenda Monte Alto (Zona Rural)", "Fazenda Monte Alto (Zona Rural)")]
    public void Bairro_perde_o_texto_de_anuncio(string bairro, string esperado) =>
        Assert.Equal(esperado, LeitorImobibrasil.BairroCurto(bairro));

    [Fact]
    public void Formulario_preenchido_passa_na_validacao_do_cadastro()
    {
        var a = LeitorImobibrasil.LerFicha(4145138, "https://www.inovabens.com.br/imovel/4145138/x", Pagina("imobibrasil-4145138.html"));
        var f = new ImovelForm();
        ImportacaoImobibrasilService.Preencher(f, a, novo: true);

        Inovabens.Core.Comum.Validacao.Validar(f);
        Assert.Equal("casa", f.Tipo);
        Assert.Equal(SituacaoImovel.DISPONIVEL, f.Situacao);
        Assert.Equal("CA0060 - Casa São Francisco", f.NomeDrive);
        Assert.Equal("Casa com 3 Quartos e Quintal Amplo no São Francisco – Aceita Troca e Financiamento", f.Titulo);
        Assert.True(f.DivulgarSite);
        Assert.False(f.EnderecoPublico);
        Assert.Contains("4145138", f.DescricaoInterna);
    }

    [Theory]
    [InlineData("Barracão", "galpão")]
    [InlineData("Sítio", "fazenda/sítio")]
    [InlineData("Casa em Condomínio", "casa em condomínio")]
    [InlineData("Sala Comercial", "sala comercial")]
    [InlineData("Qualquer", "outro")]
    public void Tipo_do_imobibrasil_vira_tipo_do_sistema(string original, string esperado) =>
        Assert.Equal(esperado, ImportacaoImobibrasilService.TipoSistema(original));

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=GtvcXI15NRY", "GtvcXI15NRY")]
    [InlineData("https://youtu.be/GtvcXI15NRY?t=3", "GtvcXI15NRY")]
    [InlineData("https://www.youtube.com/shorts/GtvcXI15NRY", "GtvcXI15NRY")]
    [InlineData("https://vimeo.com/123", null)]
    public void Reconhece_links_do_youtube(string link, string? id) => Assert.Equal(id, RegrasImovel.IdYoutube(link));
}
