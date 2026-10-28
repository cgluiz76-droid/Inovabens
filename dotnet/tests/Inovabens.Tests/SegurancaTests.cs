using Inovabens.Core;
using Inovabens.Core.Agenda;
using Inovabens.Core.Comum;
using Inovabens.Core.Crm;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.Extensions.Configuration;

namespace Inovabens.Tests;

/// <summary>Assinaturas do Google, bloqueio progressivo do login e regra de perda do CRM (sem banco).</summary>
public class SegurancaTests
{
    private const string Segredo = "segredo-de-teste";
    private static readonly DateTimeOffset Agora = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void State_do_OAuth_identifica_o_usuario_dentro_da_validade()
    {
        var usuario = Guid.NewGuid();
        var state = AssinaturaGoogle.AssinarEstado(Segredo, usuario, Agora);

        Assert.Equal(usuario, AssinaturaGoogle.LerEstado(Segredo, state, Agora.AddMinutes(9)));
        Assert.Null(AssinaturaGoogle.LerEstado(Segredo, state, Agora.AddMinutes(11)));
        Assert.Null(AssinaturaGoogle.LerEstado("outro-segredo", state, Agora));
    }

    [Fact]
    public void State_adulterado_ou_malformado_e_recusado()
    {
        var state = AssinaturaGoogle.AssinarEstado(Segredo, Guid.NewGuid(), Agora);
        var p = state.Split('.');

        // trocar o usuário ou estender a validade invalida a assinatura
        Assert.Null(AssinaturaGoogle.LerEstado(Segredo, $"{Guid.NewGuid():N}.{p[1]}.{p[2]}", Agora));
        Assert.Null(AssinaturaGoogle.LerEstado(Segredo, $"{p[0]}.{long.Parse(p[1]) + 3600}.{p[2]}", Agora));
        Assert.Null(AssinaturaGoogle.LerEstado(Segredo, $"{p[0]}.{p[1]}.{p[2].ToUpperInvariant()}", Agora));
        Assert.Null(AssinaturaGoogle.LerEstado(Segredo, "", Agora));
        Assert.Null(AssinaturaGoogle.LerEstado(Segredo, "a.b.c", Agora));
        Assert.Null(AssinaturaGoogle.LerEstado(Segredo, state + ".extra", Agora));
    }

    [Fact]
    public void Token_do_canal_do_webhook()
    {
        var token = AssinaturaGoogle.TokenDoCanal(Segredo, "canal-1");
        Assert.True(AssinaturaGoogle.TokenDoCanalValido(Segredo, "canal-1", token));
        Assert.False(AssinaturaGoogle.TokenDoCanalValido(Segredo, "canal-2", token));
        Assert.False(AssinaturaGoogle.TokenDoCanalValido("outro-segredo", "canal-1", token));
        Assert.False(AssinaturaGoogle.TokenDoCanalValido(Segredo, "canal-1", null));
        Assert.False(AssinaturaGoogle.TokenDoCanalValido(Segredo, null, token));
    }

    [Fact]
    public void Hmac_no_mesmo_formato_da_versao_Node()
    {
        // crypto.createHmac('sha256', 'key').update('The quick brown fox jumps over the lazy dog').digest('hex')
        Assert.Equal("f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8",
            AssinaturaGoogle.Hmac("key", "The quick brown fox jumps over the lazy dog"));
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(4, null)]
    [InlineData(5, 5.0)]
    [InlineData(6, 10.0)]
    [InlineData(7, 20.0)]
    [InlineData(13, 1280.0)]
    [InlineData(14, 1440.0)]
    [InlineData(5000, 1440.0)]
    public void Bloqueio_progressivo_do_login(int falhas, double? minutos) =>
        Assert.Equal(minutos is { } m ? TimeSpan.FromMinutes(m) : null, AuthService.TempoDeBloqueio(falhas));

    private static InovabensOpcoes Opcoes(bool producao, string? segredo) => InovabensOpcoes.De(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "Host=x;Database=y", ["TOKEN_ENC_KEY"] = new string('a', 64), ["JWT_REFRESH_SECRET"] = segredo,
        }).Build(), producao);

    [Theory]
    [InlineData(null)]
    [InlineData("curto-demais")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // igual à TOKEN_ENC_KEY
    public void Producao_exige_segredo_de_assinatura_proprio(string? segredo)
    {
        var erro = Assert.Throws<InvalidOperationException>(() => Opcoes(producao: true, segredo));
        Assert.Contains("JWT_REFRESH_SECRET", erro.Message);
        Assert.NotNull(Opcoes(producao: false, segredo)); // em desenvolvimento continua com o padrão
    }

    [Fact]
    public void Producao_usa_o_segredo_informado()
    {
        var segredo = new string('s', 40);
        Assert.Equal(segredo, Opcoes(producao: true, segredo).SegredoHmac);
    }

    [Fact]
    public void Perda_exige_motivo_e_demais_estagios_descartam()
    {
        Assert.Equal("Comprou com outro corretor", CrmService.MotivoPerda(TipoEstagio.PERDIDO, "  Comprou com outro corretor "));
        Assert.Throws<RegraNegocioException>(() => CrmService.MotivoPerda(TipoEstagio.PERDIDO, "   "));
        Assert.Throws<RegraNegocioException>(() => CrmService.MotivoPerda(TipoEstagio.PERDIDO, new string('x', 501)));
        Assert.Null(CrmService.MotivoPerda(TipoEstagio.ABERTO, "qualquer"));
        Assert.Null(CrmService.MotivoPerda(TipoEstagio.GANHO, new string('x', 501)));
    }
}
