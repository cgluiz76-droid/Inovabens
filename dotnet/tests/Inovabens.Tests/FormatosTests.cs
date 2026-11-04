using Inovabens.Core.Comum;
using Inovabens.Core.Dominio;
using Inovabens.Core.Pessoas;

namespace Inovabens.Tests;

public class FormatosTests
{
    [Theory]
    [InlineData("fulano@exemplo.com", true)]
    [InlineData("fulano.de_tal+imoveis@sub.exemplo.com.br", true)]
    [InlineData("fulano@exemplo", false)]
    [InlineData("fulano@-exemplo.com", false)]
    [InlineData("fulano exemplo@x.com", false)]
    [InlineData("@exemplo.com", false)]
    [InlineData("fulano@exemplo.c", false)]
    public void Email_pela_regex(string email, bool valido) => Assert.Equal(valido, Formatos.Email().IsMatch(email));

    [Fact]
    public void Documento_mascara_cpf_e_cnpj_alfanumerico()
    {
        Assert.Equal("12345678901", Formatos.DocumentoSemMascara(" 123.456.789-01 "));
        Assert.Equal("123.456.789-01", Formatos.Documento("12345678901"));
        Assert.Equal("12.ABC.345/01DE-35", Formatos.Documento(Formatos.DocumentoSemMascara("12.abc.345/01de-35")));
        Assert.True(Formatos.CnpjValido("12ABC34501DE35"));
        Assert.False(Formatos.CnpjValido("12ABC34501DEXY")); // verificadores são numéricos
        Assert.False(Formatos.CpfValido("1234567890A"));
        Assert.Equal("—", Formatos.Documento(null));
    }

    [Theory]
    [InlineData("11987654321", "(11) 98765-4321")]
    [InlineData("(11) 3456-7890", "(11) 3456-7890")]
    [InlineData("+55 21 99876-5432", "(21) 99876-5432")]
    public void Telefone_com_ddd(string entrada, string esperado)
    {
        Assert.True(Formatos.TelefoneValido(Formatos.TelefoneDigitos(entrada)));
        Assert.Equal(esperado, Formatos.TelefoneFormatado(entrada));
    }

    [Theory]
    [InlineData("987654321")]    // sem DDD
    [InlineData("01987654321")]  // DDD com zero
    [InlineData("1187654321")]   // fixo não começa com 8
    [InlineData("11887654321")]  // celular começa com 9
    public void Telefone_invalido(string entrada) => Assert.False(Formatos.TelefoneValido(Formatos.TelefoneDigitos(entrada)));

    [Fact]
    public void Uf_pelo_enum()
    {
        Assert.Equal(Enum.GetValues<Uf>().Length, Formatos.Ufs.Count);
        Assert.Equal(("SP", "São Paulo"), Formatos.Ufs.Single(x => x.Sigla == "SP"));
        Assert.True(Formatos.UfValida(" rj "));
        Assert.False(Formatos.UfValida("XX"));
        Assert.False(Formatos.UfValida("5"));
    }

    [Fact]
    public void Formulario_de_pessoa_valida_por_tipo_e_normaliza()
    {
        var cnpjEmPf = Assert.Throws<RegraNegocioException>(() => Validacao.Validar(new PessoaForm { NomeRazao = "Fulano", CpfCnpj = "12.345.678/0001-90" }));
        Assert.True(cnpjEmPf.Erros.ContainsKey(nameof(PessoaForm.CpfCnpj)));
        var erros = Assert.Throws<RegraNegocioException>(() => Validacao.Validar(new PessoaForm
        {
            NomeRazao = "Fulano", Email = "fulano@exemplo", Telefone = "3456-7890", Whatsapp = "11 8765", Uf = "São Paulo",
        })).Erros;
        Assert.True(erros.ContainsKey(nameof(PessoaForm.Email)));
        Assert.True(erros.ContainsKey(nameof(PessoaForm.Telefone)));
        Assert.True(erros.ContainsKey(nameof(PessoaForm.Whatsapp)));
        Assert.True(erros.ContainsKey(nameof(PessoaForm.Uf)));

        var form = new PessoaForm { Tipo = TipoPessoa.PJ, NomeRazao = "Empresa", CpfCnpj = "12.abc.345/01de-35", Telefone = "1134567890" };
        Validacao.Validar(form);
        var p = new Pessoa();
        form.AplicarEm(p);
        Assert.Equal("12ABC34501DE35", p.CpfCnpj);
        Assert.Equal("(11) 3456-7890", p.Telefone);
        Assert.Equal("12.ABC.345/01DE-35", PessoaForm.De(p).CpfCnpj);
    }
}
