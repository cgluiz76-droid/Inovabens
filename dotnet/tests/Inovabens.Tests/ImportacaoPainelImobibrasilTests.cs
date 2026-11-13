using Inovabens.Core;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using S = Inovabens.Core.Imoveis.ImportacaoPainelImobibrasilService;

namespace Inovabens.Tests;

/// <summary>Regras da importação do painel do Imobibrasil que não dependem do banco.</summary>
public class ImportacaoPainelImobibrasilRegrasTests
{
    [Theory]
    [InlineData("/etc/passwd", "x", true)]
    [InlineData("ðð Sex Dating. Go >>> Yandex.com/poll/abc", null, true)]
    [InlineData("", "Olá", true)]
    [InlineData("http://spam.example", "oi", true)]
    [InlineData("Fabia", "Olá, gostaria de ter mais informações.", false)]
    public void Reconhece_lead_lixo(string nome, string? mensagem, bool lixo) => Assert.Equal(lixo, S.LeadLixo(nome, mensagem));

    [Theory]
    [InlineData("Gabriel", "Gabriel  Construtor", true)]
    [InlineData("José", "jose da silva", true)]
    [InlineData("Donizete", "Tatiane Mello", false)]
    [InlineData(null, "Tatiane", true)]
    public void Telefone_igual_so_junta_mesmo_primeiro_nome(string? a, string b, bool junta) => Assert.Equal(junta, S.MesmoPrimeiroNome(a, b));

    [Theory]
    [InlineData("Sobrado em Condomínio", "casa em condomínio")]
    [InlineData("Sobrado", "casa")]
    [InlineData("Barracão", "galpão")]
    [InlineData("Sítio", "fazenda/sítio")]
    public void Tipo_do_painel_vira_tipo_do_sistema(string painel, string sistema) =>
        Assert.Equal(sistema, ImportacaoImobibrasilService.TipoSistema(painel));
}

/// <summary>Importação do painel contra o banco real: código repetido no painel, proprietário, negócio, lead, agenda; reimportar não duplica.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class ImportacaoPainelImobibrasilIntegracaoTests
{
    [Fact]
    public async Task Importa_e_reimporta_sem_duplicar()
    {
        using var escopo = ExtratoComprovantesTests.Compartilhado.CreateScope();
        escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        var admin = (await escopo.ServiceProvider.GetRequiredService<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var fabrica = escopo.ServiceProvider.GetRequiredService<IDbContextFactory<InovabensDb>>();
        var servico = escopo.ServiceProvider.GetRequiredService<S>();

        var r = Random.Shared;
        long Id() => r.NextInt64(10_000_000_000, 90_000_000_000);
        var (idPessoa, idImA, idImB, idNeg, idNegFim, idLead, idSpam, idAg, idAgSolto) = (Id(), Id(), Id(), Id(), Id(), Id(), Id(), Id(), Id());
        var referencia = $"TE{r.Next(5000, 9999)}";
        var fone = "1799" + r.Next(1000000, 9999999);
        var foneLead = "1798" + r.Next(1000000, 9999999);

        var dados = new DadosPainelImobibrasil
        {
            Pessoas = [new ImbPessoaPainel { Id = idPessoa, Nome = "Proprietária Teste", Telefones = [fone], Papeis = ["Proprietário"], OutrasInfo = "prefere WhatsApp" }],
            Imoveis =
            [
                new ImbImovelPainel { Id = idImA, Referencia = referencia, Tipo = "Terreno", Finalidade = "Venda", Ativo = true, Preco = 250_000m,
                    Cidade = "São José do Rio Preto", Uf = "SP", Bairro = "Centro, ótimo lote", ProprietarioId = idPessoa, LocalChave = "na imobiliária",
                    Caracteristicas = ["Murado"], Descricao = "Terreno plano no centro da cidade de Rio Preto" },
                // mesmo código no painel (acontece com inativos): ganha o próximo da sequência
                new ImbImovelPainel { Id = idImB, Referencia = referencia, Tipo = "Terreno", Finalidade = "Venda", Ativo = false,
                    Cidade = "Mirassol", Uf = "SP", Exclusividade = true },
            ],
            Negocios =
            [
                new ImbNegocioPainel { Id = idNeg, Titulo = "Negócio de Proprietária", ClienteId = idPessoa, ImovelId = idImA, Etapa = "NEGOCIAÇÃO", Status = "ATIVO",
                    CriadoEm = new DateTime(2026, 4, 16, 9, 19, 0),
                    Atividades = [new ImbAtividadePainel("AE", null, null, null, null, "ATENDIMENTO", "NEGOCIAÇÃO", new DateTime(2026, 4, 17, 10, 0, 0), null, null,
                        "Negócio movido de ATENDIMENTO para NEGOCIAÇÃO\nBruno em 17/04/2026 10:00")] },
                new ImbNegocioPainel { Id = idNegFim, Titulo = "Negócio Perdido", ImovelId = idImB, Status = "PERDIDO", Motivo = "Proprietário mudou de ideia",
                    CriadoEm = new DateTime(2026, 3, 5, 14, 0, 0) },
            ],
            Leads =
            [
                new ImbLeadPainel { Id = idLead, Lista = "leads", Nome = "Interessado Novo", Telefone = foneLead, Mensagem = "Ainda está disponível?",
                    Origem = "WhatsApp Lead", ImovelId = idImA, ReferenciaImovel = referencia, RecebidoEm = new DateTime(2026, 7, 25, 10, 3, 0) },
                new ImbLeadPainel { Id = idSpam, Lista = "leads", Nome = "/etc/passwd", Mensagem = "x" },
            ],
            Agenda =
            [
                new ImbCompromissoPainel { Id = idAg, Titulo = "Visita", Categoria = "Visita", Descricao = "Visita com cliente", ImovelId = idImA,
                    Inicio = new DateTime(2023, 11, 4, 10, 0, 0), Fim = new DateTime(2023, 11, 5, 0, 0, 0), DiaTodo = true },
                new ImbCompromissoPainel { Id = idAgSolto, Titulo = "Visita", Descricao = "sem vínculo", Inicio = new DateTime(2023, 11, 1, 12, 0, 0) },
            ],
        };

        var rel1 = await servico.ImportarAsync(admin, dados, comFotos: false);
        var rel2 = await servico.ImportarAsync(admin, dados, comFotos: false);

        await using var db = await fabrica.CreateDbContextAsync();
        var a = await db.Imoveis.SingleAsync(i => i.ImobibrasilId == idImA);
        var b = await db.Imoveis.SingleAsync(i => i.ImobibrasilId == idImB);
        Assert.Equal(referencia, a.Codigo);
        Assert.Equal(SituacaoImovel.DISPONIVEL, a.Situacao);
        Assert.True(a.DivulgarSite);
        Assert.Equal("Centro", a.Bairro);
        Assert.Contains("Local da chave: na imobiliária", a.DescricaoInterna);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(a.DescricaoInterna!, @"\[Imobibrasil imóvel"));
        Assert.Contains("\"imobibrasil\"", a.Atributos);
        Assert.StartsWith("TE", b.Codigo);
        Assert.NotEqual(referencia, b.Codigo);
        Assert.Equal(SituacaoImovel.ARQUIVADO, b.Situacao);
        Assert.False(b.DivulgarSite);
        Assert.Contains(rel1.Avisos, x => x.Contains($"recebeu {b.Codigo}"));

        var pessoa = await db.Pessoas.Include(p => p.Papeis).SingleAsync(p => p.ImobibrasilId == idPessoa);
        Assert.Contains(pessoa.Papeis, x => x.Papel == "PROPRIETARIO");
        Assert.Contains("prefere WhatsApp", pessoa.Observacoes);
        Assert.True(await db.ImovelProprietarios.AnyAsync(x => x.ImovelId == a.Id && x.PessoaId == pessoa.Id));
        Assert.Single(await db.ImovelCaracteristicas.Where(x => x.ImovelId == a.Id).ToListAsync());

        var neg = await db.Leads.Include(l => l.Estagio).SingleAsync(l => l.ImobibrasilRef == "negocio:" + idNeg);
        Assert.Equal(pessoa.Id, neg.PessoaId);
        Assert.Equal(TipoEstagio.ABERTO, neg.Estagio!.Tipo);
        Assert.True(neg.Estagio.Ordem > 1);
        Assert.Single(await db.LeadInteracoes.Where(i => i.LeadId == neg.Id).ToListAsync());
        Assert.True(await db.LeadImoveisInteresse.AnyAsync(x => x.LeadId == neg.Id && x.ImovelId == a.Id));
        Assert.False(await db.Leads.AnyAsync(l => l.ImobibrasilRef == "negocio:" + idNegFim));
        Assert.Single(await db.Memorandos.Where(m => m.ImovelId == b.Id && m.Corpo.Contains("mudou de ideia")).ToListAsync());

        var lead = await db.Leads.Include(l => l.Pessoa).SingleAsync(l => l.ImobibrasilRef == "lead:" + idLead);
        Assert.Equal("Interessado Novo", lead.Pessoa!.NomeRazao);
        Assert.Single(await db.LeadInteracoes.Where(i => i.LeadId == lead.Id).ToListAsync());
        Assert.False(await db.Leads.AnyAsync(l => l.ImobibrasilRef == "lead:" + idSpam));

        var comp = await db.Compromissos.SingleAsync(x => x.ImobibrasilId == idAg);
        Assert.Equal(StatusCompromisso.CONCLUIDO, comp.Status); // antigo: não vira alerta de "não cumprido"
        Assert.Equal(a.Id, comp.ImovelId);
        Assert.False(await db.Compromissos.AnyAsync(x => x.ImobibrasilId == idAgSolto));

        Assert.Equal(1, rel2.Contagens.GetValueOrDefault("pessoas já importadas"));
        Assert.Equal(2, rel2.Contagens.GetValueOrDefault("imóveis existentes completados"));
        Assert.Equal(2, rel2.Contagens.GetValueOrDefault("negócios já importados"));
        Assert.Equal(1, rel2.Contagens.GetValueOrDefault("leads já importados"));
        Assert.Equal(1, rel2.Contagens.GetValueOrDefault("compromissos já importados"));
        Assert.DoesNotContain(rel2.Contagens.Keys, k => k.Contains("novas") || k.Contains("criados") || k.Contains("importados ("));
    }
}
