using Inovabens.Core.Imoveis;

namespace Inovabens.Tests;

/// <summary>Localização em lote: do endereço completo para a rua e o bairro; número "0" vale como sem número.</summary>
public class LocalizacaoLoteTests
{
    [Fact]
    public void Tenta_endereco_depois_rua_depois_bairro()
    {
        var t = LocalizacaoLoteService.Tentativas("Ararigboia", "0", "Vila Anchieta", "São José do Rio Preto", "SP", "15050-350").ToList();
        Assert.Equal(
        [
            ("Ararigboia, Vila Anchieta, São José do Rio Preto - SP, 15050-350, Brasil", "endereço"),
            ("Ararigboia, São José do Rio Preto - SP, Brasil", "rua"),
            ("Vila Anchieta, São José do Rio Preto - SP, Brasil", "bairro"),
        ], t);
    }

    [Fact]
    public void Mantem_o_numero_e_pula_o_que_falta()
    {
        var t = LocalizacaoLoteService.Tentativas("Nove de Julho", "1504", null, "Mirassol", "SP", null).ToList();
        Assert.Equal(["Nove de Julho, 1504, Mirassol - SP, Brasil"], t.Select(x => x.Consulta));
        Assert.Empty(LocalizacaoLoteService.Tentativas(null, null, null, null, null, null));
    }
}
