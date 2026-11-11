using Inovabens.Core;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Notion;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using N = Inovabens.Core.Notion.ImportacaoNotionService;

namespace Inovabens.Tests;

/// <summary>Importação do Notion contra o banco real, com um Notion falso: simulação não grava; reimportar não duplica nem apaga.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class ImportacaoNotionIntegracaoTests
{
    private static string NovoId() => Guid.NewGuid().ToString("N");

    private static string Rico(string t) => $$"""[{"type":"text","plain_text":"{{t}}","href":null}]""";

    private static string Pagina(string id, string titulo, string props = "") =>
        "{\"id\":\"" + id + "\",\"url\":\"https://www.notion.so/" + id + "\",\"archived\":false,\"created_time\":\"2026-01-10T12:00:00.000Z\"," +
        "\"last_edited_time\":\"2026-02-01T12:00:00.000Z\",\"properties\":{\"Nome\":{\"id\":\"title\",\"type\":\"title\",\"title\":" + Rico(titulo) + "}" + props + "}}";

    private static string Prop(string nome, string tipo, string valor) => ",\"" + nome + "\":{\"id\":\"x\",\"type\":\"" + tipo + "\",\"" + tipo + "\":" + valor + "}";
    private static string Texto(string nome, string valor) => Prop(nome, "rich_text", Rico(valor));
    private static string Sel(string nome, string valor) => Prop(nome, "select", "{\"name\":\"" + valor + "\"}");
    private static string Status(string nome, string valor) => Prop(nome, "status", "{\"name\":\"" + valor + "\"}");
    private static string Fone(string nome, string valor) => Prop(nome, "phone_number", "\"" + valor + "\"");
    private static string Data(string nome, string ini) => Prop(nome, "date", "{\"start\":\"" + ini + "\",\"end\":null}");
    private static string Rel(string nome, params string[] ids) =>
        Prop(nome, "relation", "[" + string.Join(",", ids.Select(i => "{\"id\":\"" + i + "\"}")) + "]");
    private static string Lista(params string[] itens) => "{\"results\":[" + string.Join(",", itens) + "],\"has_more\":false}";
    private static string Bloco(string id, string tipo, string dados, bool filhos = false) =>
        "{\"id\":\"" + id + "\",\"type\":\"" + tipo + "\",\"has_children\":" + (filhos ? "true" : "false") + ",\"" + tipo + "\":" + dados + "}";
    private static string LinhaTabela(string id, string a, string b) => Bloco(id, "table_row", "{\"cells\":[" + Rico(a) + "," + Rico(b) + "]}");

    [Fact]
    public async Task Simula_importa_e_reimporta_sem_perder_nada()
    {
        // provider compartilhado (cada provider novo conta no limite de 20 do EF); o Drive é o em memória
        using var escopo = ExtratoComprovantesTests.Compartilhado.CreateScope();
        escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        var admin = (await escopo.ServiceProvider.GetRequiredService<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var imoveis = escopo.ServiceProvider.GetRequiredService<ImoveisService>();
        var fabrica = escopo.ServiceProvider.GetRequiredService<IDbContextFactory<InovabensDb>>();
        var servico = escopo.ServiceProvider.GetRequiredService<N>();

        // sistema antes: um imóvel e uma pessoa com observação própria
        var referencia = "NT" + Random.Shared.Next(100000, 999999);
        var imovelId = await imoveis.CriarAsync(admin, new ImovelForm { Titulo = "Área do teste", Tipo = "área", NomeDrive = NovoId(), Referencia = referencia });
        var celular = "1799" + Random.Shared.Next(1000000, 9999999);
        var pessoaExistente = Guid.NewGuid();
        await using (var db = await fabrica.CreateDbContextAsync())
        {
            db.Pessoas.Add(new Pessoa { Id = pessoaExistente, NomeRazao = "Fulano de Tal " + celular, Telefone = celular, Observacoes = "nota antiga",
                Papeis = [new PessoaPapel { PessoaId = pessoaExistente, Papel = "CLIENTE" }] });
            await db.SaveChangesAsync();
        }

        string fulano = NovoId(), beltrano = NovoId(), cliente = NovoId(), projeto = NovoId(), tarefa = NovoId(), vazia = NovoId(), tabela = NovoId();
        var nomeProjeto = "PROJETO " + referencia;
        var falso = new NotionFalso(new()
        {
            [$"databases/{N.BaseEnvolvidos}/query"] = Lista(
                Pagina(fulano, "Fulano", Sel("Atributos profissionais", "Engenheiro") + Sel("Divisão", "Engenharia") + Fone("Chame no Whatsapp", "+55 " + celular) + Rel("PROJETOS", projeto)),
                Pagina(beltrano, "Beltrano " + referencia, Sel("Atributos profissionais", "Topógrafo") + Sel("Divisão", "Terceiros") + Sel("Vínculo", "Terceirizado")
                    + Texto("Documentos", "CPF 529.982.247-25") + Rel("PROJETOS", projeto))),
            [$"databases/{N.BaseClientes}/query"] = Lista(Pagina(cliente, "ACME " + referencia + " LTDA", Sel("Origem", "Indicação"))),
            [$"databases/{N.BaseProjetos}/query"] = Lista(Pagina(projeto, nomeProjeto, Status("Status", "Em andamento") + Sel("Etapa da Estratégia", "Análise externa")
                + Data("Previsão de Entrega", "2026-12-20") + Rel("Cliente ", cliente) + Rel("Equipe", fulano))),
            [$"databases/{N.BaseTarefas}/query"] = Lista(
                Pagina(tarefa, "Visita técnica", Status("Status", "Concluído") + Texto("Ação ", "Medir o terreno") + Data("Data de Início", "2026-03-05")
                    + Data("Concluir até", "2026-03-02") + Rel("Projeto ", projeto) + Rel("Responsável", fulano)),
                Pagina(vazia, "")),
            [$"blocks/{projeto}/children"] = Lista(
                Bloco("p1", "paragraph", "{\"rich_text\":" + Rico("Objetivo: aprovar") + "}"),
                Bloco(tabela, "table", "{\"table_width\":2,\"has_column_header\":true}", filhos: true)),
            [$"blocks/{tabela}/children"] = Lista(LinhaTabela("h", "Data", "Atualização"), LinhaTabela(NovoId(), "05/03/2026", "Protocolo na prefeitura")),
        });
        var notion = new NotionCliente("t", new HttpClient(falso)) { Pausa = TimeSpan.Zero };
        var mapa = new Dictionary<string, string> { [nomeProjeto] = referencia };

        // 1) simulação: relatório completo, nada gravado
        var sim = await servico.ImportarAsync(admin, notion, simular: true, mapa);
        Assert.Equal(2, sim.Contagem["pessoas novas"]);
        Assert.Equal(1, sim.Contagem["pessoas completadas"]);
        Assert.Equal(1, sim.Contagem["memorandos do histórico"]);
        await using (var db = await fabrica.CreateDbContextAsync())
        {
            Assert.Equal("nota antiga", (await db.Pessoas.SingleAsync(p => p.Id == pessoaExistente)).Observacoes);
            Assert.False(await db.Memorandos.AnyAsync(m => m.ImovelId == imovelId));
            Assert.False(await db.Pessoas.AnyAsync(p => p.NomeRazao.StartsWith("ACME " + referencia)));
        }

        // 2) importação
        var r = await servico.ImportarAsync(admin, notion, simular: false, mapa);
        Assert.Equal(1, r.Contagem["tarefas vazias (ignoradas)"]);
        await using (var db = await fabrica.CreateDbContextAsync())
        {
            var existente = await db.Pessoas.Include(p => p.Papeis).SingleAsync(p => p.Id == pessoaExistente);
            Assert.StartsWith("nota antiga", existente.Observacoes);                 // não apaga o que havia
            Assert.Contains($"[Notion {fulano}]", existente.Observacoes);
            Assert.Contains("Atributo profissional: Engenheiro", existente.Observacoes);
            Assert.Equal(["CLIENTE", "EQUIPE"], existente.Papeis.Select(p => p.Papel).Order());
            Assert.Equal("Engenheiro", existente.Papeis.Single(p => p.Papel == "EQUIPE").Especialidade);
            Assert.Equal(celular, existente.Telefone);                                // não troca o que havia

            var bel = await db.Pessoas.Include(p => p.Papeis).SingleAsync(p => p.NomeRazao == "Beltrano " + referencia);
            Assert.Equal("PRESTADOR", Assert.Single(bel.Papeis).Papel);
            var acme = await db.Pessoas.Include(p => p.Papeis).SingleAsync(p => p.NomeRazao == "ACME " + referencia + " LTDA");
            Assert.Equal((TipoPessoa.PJ, "PROPRIETARIO"), (acme.Tipo, Assert.Single(acme.Papeis).Papel));

            var env = await db.ImovelEnvolvidos.Where(e => e.ImovelId == imovelId).ToListAsync();
            Assert.Equal(3, env.Count);
            Assert.Contains(env, e => e.PessoaId == pessoaExistente && e.Papel == "EQUIPE");

            var im = await db.Imoveis.SingleAsync(i => i.Id == imovelId);
            Assert.Equal(("Em andamento", "Análise externa", new DateOnly(2026, 12, 20)), (im.ProjetoStatus, im.ProjetoEtapa, im.ProjetoPrevisaoEntrega));

            var memos = await db.Memorandos.Where(m => m.ImovelId == imovelId).OrderBy(m => m.CriadoEm).ToListAsync();
            Assert.Equal(2, memos.Count);
            Assert.True(memos[0].Fixado);
            Assert.Contains("Objetivo: aprovar", memos[0].Corpo);
            Assert.Equal("Protocolo na prefeitura", memos[1].Corpo);
            Assert.Equal(new DateTime(2026, 3, 5, 15, 0, 0), memos[1].CriadoEm);       // 12:00 de Brasília

            var comp = await db.Compromissos.SingleAsync(c => c.ImovelId == imovelId);
            Assert.Equal((StatusCompromisso.CONCLUIDO, "Visita técnica"), (comp.Status, comp.Titulo));
            Assert.True(comp.FimEm >= comp.InicioEm);
            Assert.Contains("Medir o terreno", comp.Descricao);
            Assert.Contains("Datas no Notion", comp.Descricao);
            Assert.Equal(pessoaExistente, (await db.CompromissoParticipantes.SingleAsync(p => p.CompromissoId == comp.Id)).PessoaId);
        }

        // 3) reimportar: nada duplica
        var r2 = await servico.ImportarAsync(admin, notion, simular: false, mapa);
        Assert.False(r2.Contagem.ContainsKey("pessoas novas"));
        Assert.False(r2.Contagem.ContainsKey("memorandos de projeto"));
        await using (var db = await fabrica.CreateDbContextAsync())
        {
            Assert.Equal(2, await db.Memorandos.CountAsync(m => m.ImovelId == imovelId));
            Assert.Equal(1, await db.Compromissos.CountAsync(c => c.ImovelId == imovelId));
            Assert.Equal(3, await db.ImovelEnvolvidos.CountAsync(e => e.ImovelId == imovelId));
            Assert.Equal(1, await db.Pessoas.CountAsync(p => p.NomeRazao == "Beltrano " + referencia));
            var obs = (await db.Pessoas.SingleAsync(p => p.Id == pessoaExistente)).Observacoes!;
            Assert.Equal(obs.IndexOf($"[Notion {fulano}]", StringComparison.Ordinal), obs.LastIndexOf($"[Notion {fulano}]", StringComparison.Ordinal));
        }
        Assert.All(falso.Pedidos, p => Assert.True(p.Metodo == HttpMethod.Get || p.Caminho.EndsWith("/query")));
    }
}
