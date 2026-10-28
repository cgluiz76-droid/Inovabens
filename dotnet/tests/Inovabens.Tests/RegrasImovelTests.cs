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

public class ImovelFormTests
{
    [Fact]
    public void Formulario_valido_passa()
    {
        Validacao.Validar(new ImovelForm { Titulo = "Apartamento centro", Tipo = "apartamento", PrecoVenda = 450_000.50m });
    }

    [Fact]
    public void Titulo_curto_e_preco_negativo_sao_recusados()
    {
        var e = Assert.Throws<RegraNegocioException>(() => Validacao.Validar(new ImovelForm { Titulo = "Ap", Tipo = "casa" }));
        Assert.True(e.Erros.ContainsKey(nameof(ImovelForm.Titulo)));

        e = Assert.Throws<RegraNegocioException>(() => Validacao.Validar(new ImovelForm { Titulo = "Casa", PrecoVenda = -1 }));
        Assert.True(e.Erros.ContainsKey(nameof(ImovelForm.PrecoVenda)));
    }

    [Fact]
    public void Mais_de_duas_casas_decimais_e_recusado() =>
        Assert.Throws<RegraNegocioException>(() => Validacao.Validar(new ImovelForm { Titulo = "Casa", AreaTotalM2 = 10.555m }));

    [Fact]
    public void Aplicar_normaliza_vazios_e_uf()
    {
        var im = new Imovel();
        new ImovelForm { Titulo = "  Casa  ", Tipo = "casa", Uf = "sp", Bairro = "   ", Cidade = " Santos " }.AplicarEm(im);
        Assert.Equal("Casa", im.Titulo);
        Assert.Equal("SP", im.Uf);
        Assert.Null(im.Bairro);
        Assert.Equal("Santos", im.Cidade);
    }

    [Fact]
    public void UF_com_tamanho_errado_e_recusada() =>
        Assert.Throws<RegraNegocioException>(() => Validacao.Validar(new ImovelForm { Titulo = "Casa", Uf = "SPX" }));
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
}
