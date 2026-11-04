using Inovabens.Core.Comum;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;

namespace Inovabens.Tests;

public class RegrasImovelTests
{
    private static readonly DateOnly Hoje = new(2026, 10, 28);
    private static ProprietarioEntrada P(decimal pct, DateOnly? ini = null, DateOnly? fim = null) =>
        new() { PessoaId = Guid.NewGuid(), Percentual = pct, InicioEm = ini, FimEm = fim };

    [Fact]
    public void Soma_100_e_aceita() => RegrasImovel.ValidarProprietarios([P(60), P(40)], Hoje);

    [Fact]
    public void Lista_vazia_e_aceita() => RegrasImovel.ValidarProprietarios([], Hoje);

    [Fact]
    public void Soma_diferente_de_100_e_recusada()
    {
        var e = Assert.Throws<RegraNegocioException>(() => RegrasImovel.ValidarProprietarios([P(60), P(30)], Hoje));
        Assert.Contains("atual: 90,00%", e.Message);
    }

    [Fact]
    public void So_proprietarios_vigentes_entram_na_soma()
    {
        // antigo dono (já saiu) + atual 100% = válido
        RegrasImovel.ValidarProprietarios([P(100, fim: Hoje.AddDays(-1)), P(100, ini: Hoje)], Hoje);
        // futuro dono ainda não conta: só 50% vigente
        Assert.Throws<RegraNegocioException>(() => RegrasImovel.ValidarProprietarios([P(50), P(50, ini: Hoje.AddDays(1))], Hoje));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100.01)]
    [InlineData(33.333)]
    public void Percentual_invalido_e_recusado(decimal pct) =>
        Assert.Throws<RegraNegocioException>(() => RegrasImovel.ValidarProprietarios([P(pct)], Hoje));

    [Fact]
    public void Pessoa_obrigatoria() =>
        Assert.Throws<RegraNegocioException>(() => RegrasImovel.ValidarProprietarios([new ProprietarioEntrada { Percentual = 100 }], Hoje));

    [Fact]
    public void Codigo_sequencial_tem_6_digitos() => Assert.Equal("IMV-000123", RegrasImovel.FormatarCodigo(123));
}

public class ArquivoSeguroTests
{
    [Theory]
    [InlineData("fachada.JPG", "image/jpeg", ".jpg", "image/jpeg", false, TipoMidia.FOTO)]
    [InlineData("tour.mp4", "video/mp4", ".mp4", "video/mp4", false, TipoMidia.VIDEO)]
    [InlineData("matricula.pdf", "application/pdf", ".pdf", "application/pdf", false, TipoMidia.DOCUMENTO)]
    [InlineData("planta.dwg", "application/acad", ".dwg", "application/octet-stream", true, TipoMidia.DOCUMENTO)]
    // podem conter script: viram download, nunca página
    [InlineData("logo.svg", "image/svg+xml", ".svg", "application/octet-stream", true, TipoMidia.DOCUMENTO)]
    [InlineData("pagina.html", "text/html; charset=utf-8", ".html", "application/octet-stream", true, TipoMidia.DOCUMENTO)]
    [InlineData("foto.jpg", "text/html", ".jpg", "application/octet-stream", true, TipoMidia.DOCUMENTO)]
    [InlineData("sem-extensao", "", "", "application/octet-stream", true, TipoMidia.DOCUMENTO)]
    [InlineData("../../a.p<n>g\"/", "IMAGE/PNG", "", "image/png", false, TipoMidia.FOTO)]
    [InlineData("a.p<n>g\"", "IMAGE/PNG", ".png", "image/png", false, TipoMidia.FOTO)]
    public void Tipo_e_extensao_vem_de_lista_permitida(string nome, string tipo, string ext, string guardado, bool download, TipoMidia midia)
    {
        var a = RegrasImovel.ArquivoSeguro(nome, tipo);
        Assert.Equal((ext, guardado, download, midia), (a.Extensao, a.ContentType, a.Download, a.Tipo));
    }
}

public class ImovelFormTests
{
    [Fact]
    public void Formulario_valido_passa()
    {
        Validacao.Validar(new ImovelForm { NomeDrive = Guid.NewGuid().ToString(), Titulo = "Apartamento centro", Tipo = "apartamento", PrecoVenda = 450_000.50m });
    }

    [Fact]
    public void Titulo_curto_e_preco_negativo_sao_recusados()
    {
        var e = Assert.Throws<RegraNegocioException>(() => Validacao.Validar(new ImovelForm { NomeDrive = Guid.NewGuid().ToString(), Titulo = "Ap", Tipo = "casa" }));
        Assert.True(e.Erros.ContainsKey(nameof(ImovelForm.Titulo)));

        e = Assert.Throws<RegraNegocioException>(() => Validacao.Validar(new ImovelForm { NomeDrive = Guid.NewGuid().ToString(), Titulo = "Casa", PrecoVenda = -1 }));
        Assert.True(e.Erros.ContainsKey(nameof(ImovelForm.PrecoVenda)));
    }

    [Fact]
    public void Mais_de_duas_casas_decimais_e_recusado() =>
        Assert.Throws<RegraNegocioException>(() => Validacao.Validar(new ImovelForm { NomeDrive = Guid.NewGuid().ToString(), Titulo = "Casa", AreaTotalM2 = 10.555m }));

    [Fact]
    public void Aplicar_normaliza_vazios_e_uf()
    {
        var im = new Imovel();
        new ImovelForm { NomeDrive = Guid.NewGuid().ToString(), Titulo = "  Casa  ", Tipo = "casa", Uf = "sp", Bairro = "   ", Cidade = " Santos " }.AplicarEm(im);
        Assert.Equal("Casa", im.Titulo);
        Assert.Equal("SP", im.Uf);
        Assert.Null(im.Bairro);
        Assert.Equal("Santos", im.Cidade);
    }

    [Fact]
    public void UF_com_tamanho_errado_e_recusada() =>
        Assert.Throws<RegraNegocioException>(() => Validacao.Validar(new ImovelForm { NomeDrive = Guid.NewGuid().ToString(), Titulo = "Casa", Uf = "SPX" }));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bloco A/Apto 12")]
    [InlineData("Casa: fundos")]
    public void Nome_da_pasta_no_drive_vazio_ou_com_caractere_proibido_e_recusado(string nome)
    {
        var e = Assert.Throws<RegraNegocioException>(() => Validacao.Validar(new ImovelForm { NomeDrive = nome, Titulo = "Casa", Tipo = "casa" }));
        Assert.True(e.Erros.ContainsKey(nameof(ImovelForm.NomeDrive)));
    }

    [Fact]
    public void Nome_da_pasta_tem_espacos_normalizados()
    {
        var im = new Imovel();
        new ImovelForm { NomeDrive = "  Residencial   Gonzaga - Apto 52 ", Titulo = "Casa", Tipo = "casa" }.AplicarEm(im);
        Assert.Equal("Residencial Gonzaga - Apto 52", im.NomeDrive);
    }

    [Theory]
    [InlineData(SubpastasDrive.FotosVideos, TipoMidia.FOTO, TipoMidia.FOTO)]
    [InlineData(SubpastasDrive.FotosVideos, TipoMidia.VIDEO, TipoMidia.VIDEO)]
    [InlineData(SubpastasDrive.FotosVideos, TipoMidia.DOCUMENTO, TipoMidia.DOCUMENTO)]
    [InlineData(SubpastasDrive.Matricula, TipoMidia.FOTO, TipoMidia.DOCUMENTO)]
    [InlineData(SubpastasDrive.Protocolos, TipoMidia.VIDEO, TipoMidia.DOCUMENTO)]
    public void So_a_pasta_de_fotos_e_videos_entra_na_galeria(int subpasta, TipoMidia arquivo, TipoMidia esperado) =>
        Assert.Equal(esperado, SubpastasDrive.TipoNaPasta(subpasta, arquivo));

    [Fact]
    public void Subpastas_sao_as_seis_definidas()
    {
        Assert.Equal(
            ["01 - Matrícula", "02 - Projetos", "03 - Comercial", "04 - Fotos e Vídeos", "05 - Documentos em Geral", "06 - Protocolos"],
            SubpastasDrive.Todas.Select(s => s.Nome));
        Assert.Throws<RegraNegocioException>(() => SubpastasDrive.Nome(7));
    }
}

public class TextoTests
{
    [Theory]
    [InlineData("EM_AQUISICAO", "Em aquisição")]
    [InlineData("CAPTACAO", "Captação")]
    [InlineData("RETIFICACAO_AREA", "Retificação área")]
    [InlineData("NAO_INICIADA", "Não iniciada")]
    [InlineData(null, "—")]
    public void Rotulo_igual_ao_da_interface_web(string? entrada, string esperado) => Assert.Equal(esperado, Texto.Rotulo(entrada));

    [Fact]
    public void LikeLiteral_escapa_curingas() => Assert.Equal(@"50\% de \_x\\", Texto.LikeLiteral(@"50% de _x\"));
}

public class AcessoTests
{
    private static UsuarioAtual Usuario(bool escopoProprio) =>
        new(Guid.NewGuid(), "Teste", "t@x.com", Guid.NewGuid(), "Perfil", escopoProprio, new HashSet<string>());

    [Fact]
    public void Corretor_so_ve_os_proprios_imoveis()
    {
        var corretor = Usuario(escopoProprio: true);
        var imoveis = new[]
        {
            new Imovel { Codigo = "IMV-1", CorretorResponsavelId = corretor.Id },
            new Imovel { Codigo = "IMV-2", CorretorResponsavelId = Guid.NewGuid() },
            new Imovel { Codigo = "IMV-3" },
        }.AsQueryable();

        Assert.Equal(["IMV-1"], imoveis.VisiveisPara(corretor).Select(i => i.Codigo));
        Assert.Equal(3, imoveis.VisiveisPara(Usuario(escopoProprio: false)).Count());
    }

    [Fact]
    public void Corretor_ve_leads_proprios_e_compromissos_seus_ou_dos_seus_imoveis()
    {
        var corretor = Usuario(escopoProprio: true);
        var outro = Guid.NewGuid();
        var leads = new[] { new Lead { ResponsavelId = corretor.Id }, new Lead { ResponsavelId = outro } }.AsQueryable();
        Assert.Single(leads.VisiveisPara(corretor));

        var meuImovel = new Imovel { CorretorResponsavelId = corretor.Id };
        var outroImovel = new Imovel { CorretorResponsavelId = outro };
        var compromissos = new[]
        {
            new Compromisso { Titulo = "meu, em imóvel alheio", ResponsavelId = corretor.Id, Imovel = outroImovel },
            new Compromisso { Titulo = "alheio, no meu imóvel", ResponsavelId = outro, Imovel = meuImovel },
            new Compromisso { Titulo = "alheio", ResponsavelId = outro, Imovel = outroImovel },
        }.AsQueryable();
        Assert.Equal(["meu, em imóvel alheio", "alheio, no meu imóvel"], compromissos.VisiveisPara(corretor).Select(c => c.Titulo));
        Assert.Equal(3, compromissos.VisiveisPara(Usuario(escopoProprio: false)).Count());
    }

    [Fact]
    public void Corretor_e_sempre_o_responsavel_e_nao_repassa()
    {
        var corretor = Usuario(escopoProprio: true);
        var gestor = Usuario(escopoProprio: false);
        var outro = Guid.NewGuid();

        Assert.Equal(corretor.Id, Acesso.ResponsavelAoCriar(corretor, outro));
        Assert.Equal(outro, Acesso.ResponsavelAoCriar(gestor, outro));
        Assert.Equal(gestor.Id, Acesso.ResponsavelAoCriar(gestor, null));

        Assert.Null(Acesso.NovoResponsavel(corretor, outro));
        Assert.Equal(outro, Acesso.NovoResponsavel(gestor, outro));
        Assert.Null(Acesso.NovoResponsavel(gestor, null));
    }
}

public class LocalizacaoImovelTests
{
    [Theory]
    [InlineData("-23.5505, -46.6333", -23.5505, -46.6333)]          // copiado do Google Maps
    [InlineData("(-23.5505,-46.6333)", -23.5505, -46.6333)]
    [InlineData("-23,5505; -46,6333", -23.5505, -46.6333)]          // vírgula decimal, separado por ;
    [InlineData("-23.123456789, -46.1", -23.1234568, -46.1)]        // 7 casas (~1 cm)
    public void Coordenadas_sao_lidas(string texto, double lat, double lng) =>
        Assert.Equal((lat, lng), RegrasImovel.LerCoordenadas(texto));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-23.5")]
    [InlineData("-23,5505, -46,6333")] // ambíguo: vírgula decimal sem ;
    [InlineData("95, 10")]             // latitude fora da faixa
    public void Coordenadas_invalidas_sao_recusadas(string texto) => Assert.Null(RegrasImovel.LerCoordenadas(texto));

    [Fact]
    public void Link_do_mapa_usa_coordenadas_ou_endereco()
    {
        Assert.Equal("https://www.google.com/maps/search/?api=1&query=-23.5505%2C-46.6333",
            RegrasImovel.LinkGoogleMaps(-23.5505, -46.6333, "qualquer"));
        var endereco = RegrasImovel.EnderecoParaMapa("Av. Ana Costa", "100", "Gonzaga", "Santos", "SP", null);
        Assert.Equal("Av. Ana Costa, 100, Gonzaga, Santos - SP, Brasil", endereco);
        Assert.StartsWith("https://www.google.com/maps/search/?api=1&query=Av.%20Ana%20Costa", RegrasImovel.LinkGoogleMaps(null, null, endereco));
        Assert.Null(RegrasImovel.LinkGoogleMaps(null, null, RegrasImovel.EnderecoParaMapa(null, null, null, null, null, null)));
    }

    [Fact]
    public void Uf_precisa_ser_do_Brasil()
    {
        Assert.Equal(27, RegrasImovel.Ufs.Count);
        Validacao.Validar(new ImovelForm { NomeDrive = Guid.NewGuid().ToString(), Titulo = "Casa", Uf = "rj" });
        var e = Assert.Throws<RegraNegocioException>(() => Validacao.Validar(new ImovelForm { NomeDrive = Guid.NewGuid().ToString(), Titulo = "Casa", Uf = "XX" }));
        Assert.True(e.Erros.ContainsKey(nameof(ImovelForm.Uf)));
    }

    [Fact]
    public void Latitude_sem_longitude_e_recusada() =>
        Assert.Throws<RegraNegocioException>(() => Validacao.Validar(new ImovelForm { NomeDrive = Guid.NewGuid().ToString(), Titulo = "Casa", Latitude = -23.5 }));
}

public class CodigoPerfilPessoaTests
{
    [Theory]
    [InlineData("Corretor parceiro", "CORRETOR_PARCEIRO")]
    [InlineData("  Síndico / Zelador ", "SINDICO_ZELADOR")]
    [InlineData("Cliente", "CLIENTE_2")]
    [InlineData("!!!", "PERFIL")]
    public void Codigo_sem_acento_e_unico(string nome, string esperado) =>
        Assert.Equal(esperado, Inovabens.Core.Admin.CadastrosService.CodigoLivre(nome, ["CLIENTE", "PROPRIETARIO"]));
}
