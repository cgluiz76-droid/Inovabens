using System.Net;
using System.Text;
using System.Text.Json;
using Inovabens.Core.Dominio;
using Inovabens.Core.Notion;

namespace Inovabens.Tests;

/// <summary>Notion falso: responde por caminho e guarda os pedidos (para provar que só há leitura).</summary>
internal sealed class NotionFalso(Dictionary<string, string> respostas) : HttpMessageHandler
{
    public List<(HttpMethod Metodo, string Caminho)> Pedidos { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var caminho = req.RequestUri!.AbsolutePath.Replace("/v1/", "");
        Pedidos.Add((req.Method, caminho));
        var corpo = respostas.GetValueOrDefault(caminho) ?? """{"results":[],"has_more":false}""";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") });
    }
}

public class NotionLeitorTests
{
    private static string Rico(string texto) => $$"""[{"type":"text","plain_text":"{{texto}}","href":null}]""";

    private static string Bloco(string id, string tipo, string dados, bool filhos = false) =>
        $$"""{"id":"{{id}}","type":"{{tipo}}","has_children":{{(filhos ? "true" : "false")}},"{{tipo}}":{{dados}}}""";

    private static string Lista(params string[] itens) => $$"""{"results":[{{string.Join(",", itens)}}],"has_more":false}""";

    private static string Linha(string id, params string[] celulas) =>
        Bloco(id, "table_row", $$"""{"cells":[{{string.Join(",", celulas.Select(Rico))}}]}""");

    [Fact]
    public async Task Converte_blocos_em_texto_e_separa_historico_e_arquivos()
    {
        var falso = new NotionFalso(new()
        {
            ["blocks/pag/children"] = Lista(
                Bloco("b1", "callout", $$$"""{"rich_text":{{{Rico("Objetivo final: aprovar o loteamento")}}},"icon":{"type":"emoji","emoji":"🎯"}}"""),
                Bloco("b2", "bulleted_list_item", $$"""{"rich_text":{{Rico("Topografia")}}}"""),
                Bloco("b3", "to_do", $$"""{"rich_text":{{Rico("Protocolar")}},"checked":true}"""),
                Bloco("tab", "table", """{"table_width":2,"has_column_header":false}""", filhos: true),
                Bloco("img", "image", """{"type":"file","file":{"url":"https://s3.example/a/foto%201.jpg?x=1"},"caption":[]}"""),
                Bloco("db", "child_database", """{"title":"Agenda mensal"}""")),
            ["blocks/tab/children"] = Lista(Linha("h", "Data", "Atualização"), Linha("r1", "05/03/2026", "Reunião na prefeitura"), Linha("r2", "", ""),
                Linha("r3", "25.02.2026", "Cálculos"), Linha("r4", "sem data", "Anotação solta")),
            ["databases/db/query"] = Lista(),
        });
        var notion = new NotionCliente("t", new HttpClient(falso)) { Pausa = TimeSpan.Zero };

        var c = await NotionLeitor.ConteudoAsync(notion, "pag");

        Assert.Contains("🎯 Objetivo final: aprovar o loteamento", c.TextoFinal);
        Assert.Contains("• Topografia", c.TextoFinal);
        Assert.Contains("[x] Protocolar", c.TextoFinal);
        Assert.Contains("[Base \"Agenda mensal\": 0 itens]", c.TextoFinal);
        Assert.Equal(2, c.Historico.Count);
        var h = c.Historico[0];
        Assert.Equal((new DateOnly(2026, 3, 5), "Reunião na prefeitura", "r1"), (h.Data, h.Texto, h.Id));
        Assert.Equal(new DateOnly(2026, 2, 25), c.Historico[1].Data);
        Assert.Contains("sem data | Anotação solta", c.TextoFinal);   // nada se perde
        Assert.DoesNotContain("Data | Atualização", c.TextoFinal);
        var a = Assert.Single(c.Arquivos);
        Assert.Equal(("foto 1.jpg", true), (a.Nome, a.HospedadoNoNotion));
        Assert.All(falso.Pedidos, p => Assert.True(p.Metodo == HttpMethod.Get || p.Caminho.EndsWith("/query")));
    }

    [Fact]
    public void Le_propriedades_pelo_nome_sem_os_espacos_do_notion()
    {
        var pagina = JsonDocument.Parse("""
        {"id":"2810be81-eecc-80cb-98f5-e74b4d9adb1f","url":"u","created_time":"2026-01-01T00:00:00.000Z","last_edited_time":"2026-01-02T00:00:00.000Z",
         "properties":{
           "Tarefa":{"id":"title","type":"title","title":[{"type":"text","plain_text":"Levantamento","href":null}]},
           "Ação ":{"id":"a","type":"rich_text","rich_text":[{"type":"text","plain_text":"Medir o lote","href":null}]},
           "Status":{"id":"s","type":"status","status":{"name":"Em andamento"}},
           "Concluir até":{"id":"d","type":"date","date":{"start":"2026-03-10","end":null}},
           "Projeto ":{"id":"p%3D","type":"relation","relation":[{"id":"2830be81-eecc-8001-8fcb-f95e6920c920"}],"has_more":false}
         }}
        """).RootElement;

        Assert.Equal("2810be81eecc80cb98f5e74b4d9adb1f", NotionLeitor.Id(pagina));
        Assert.Equal("Levantamento", NotionLeitor.Titulo(pagina));
        Assert.Equal("Medir o lote", NotionLeitor.Texto(pagina, "Ação"));
        Assert.Equal("Em andamento", NotionLeitor.Texto(pagina, "Status"));
        Assert.Equal((new DateTime(2026, 3, 10), (DateTime?)null, false), NotionLeitor.Data(pagina, "Concluir até"));
        var (ids, incompleta, propId) = NotionLeitor.Relacao(pagina, "Projeto");
        Assert.Equal(["2830be81eecc80018fcbf95e6920c920"], ids);
        Assert.False(incompleta);
        Assert.Equal("p%3D", propId);
    }
}

public class ImportacaoNotionRegrasTests
{
    [Theory]
    [InlineData("ENGº BALDUÍNO", "ENG BALDUINO")]
    [InlineData("PRÉDIO DA BENÊ", "PREDIO DA BENE")]
    [InlineData("SP002 - Prédio da Beneficência Portuguesa", "SP002 PREDIO DA BENEFICENCIA PORTUGUESA")]
    public void Normaliza_titulos(string titulo, string esperado) => Assert.Equal(esperado, ImportacaoNotionService.Normalizar(titulo));

    [Theory]
    [InlineData("Vizinhos", "Terceiros", null, "TERCEIRO")]
    [InlineData("Cartório - instituição", null, null, "TERCEIRO")]
    [InlineData("Engenheiro", "Engenharia", "Terceirizado", "EQUIPE")]
    [InlineData("Advogado", "Terceiros", "Parceria", "PARCEIRO")]
    [InlineData("Topógrafo", "Terceiros", "Terceirizado", "PRESTADOR")]
    [InlineData(null, null, null, "PRESTADOR")]
    public void Perfil_do_envolvido(string? atributo, string? divisao, string? vinculo, string esperado) =>
        Assert.Equal(esperado, ImportacaoNotionService.PerfilEnvolvido(atributo, divisao, vinculo));

    [Fact]
    public void Acha_cpf_valido_no_texto_de_documentos()
    {
        Assert.Equal("52998224725", ImportacaoNotionService.CpfDoTexto("RG 12.345.678-9 · CPF 529.982.247-25 · CNH 123"));
        Assert.Null(ImportacaoNotionService.CpfDoTexto("CPF 111.111.111-11"));
        Assert.Null(ImportacaoNotionService.CpfDoTexto(null));
    }

    [Theory]
    [InlineData("Euro Mundi Locações e Montagens Industriais LTDA", TipoPessoa.PJ)]
    [InlineData("Katia Manchini da Silva", TipoPessoa.PF)]
    public void Tipo_de_pessoa_pelo_nome(string nome, TipoPessoa esperado) => Assert.Equal(esperado, ImportacaoNotionService.TipoPorNome(nome));

    [Theory]
    [InlineData("Concluído", StatusCompromisso.CONCLUIDO)]
    [InlineData("Cancelada", StatusCompromisso.CANCELADO)]
    [InlineData("Em andamento", StatusCompromisso.PENDENTE)]
    [InlineData(null, StatusCompromisso.PENDENTE)]
    public void Status_da_tarefa(string? status, StatusCompromisso esperado) => Assert.Equal(esperado, ImportacaoNotionService.StatusTarefa(status));

    [Fact]
    public void Horario_de_datas_sem_hora_e_fim_antes_do_inicio()
    {
        var criada = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        // 09:00–18:00 de Brasília = 12:00–21:00 UTC
        var (i, f, aj) = ImportacaoNotionService.Horario(new DateTime(2026, 3, 2), null, false, new DateTime(2026, 3, 5), null, false, criada);
        Assert.Equal((new DateTime(2026, 3, 2, 12, 0, 0), new DateTime(2026, 3, 5, 21, 0, 0), false), (i, f, aj));

        (i, f, aj) = ImportacaoNotionService.Horario(new DateTime(2026, 3, 5), null, false, new DateTime(2026, 3, 2), null, false, criada);
        Assert.Equal((new DateTime(2026, 3, 5, 12, 0, 0), new DateTime(2026, 3, 5, 21, 0, 0), true), (i, f, aj));

        (i, f, aj) = ImportacaoNotionService.Horario(null, null, false, null, null, false, criada);
        Assert.True(aj);
        Assert.True(f >= i);
    }
}
