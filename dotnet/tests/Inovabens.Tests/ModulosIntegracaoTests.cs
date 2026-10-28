using ClosedXML.Excel;
using Inovabens.Core;
using Inovabens.Core.Admin;
using Inovabens.Core.Agenda;
using Inovabens.Core.Comum;
using Inovabens.Core.Crm;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Financeiro;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Memorandos;
using Inovabens.Core.Painel;
using Inovabens.Core.Pessoas;
using Inovabens.Core.Relatorios;
using Inovabens.Core.Servicos;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using OtpNet;

namespace Inovabens.Tests;

/// <summary>
/// Pessoas, Memorandos, Agenda/Alertas, CRM, Painel, Administração, Conta e Relatórios contra um PostgreSQL real
/// (mesmo banco dos demais testes, ver <see cref="BancoFixture"/>). Os dados levam um sufixo único para o teste
/// poder rodar várias vezes no mesmo banco.
/// </summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class ModulosIntegracaoTests
{
    private static string Url => BancoFixture.Url;
    private const string Senha = "Trocar@123456";
    private readonly string sufixo = Guid.NewGuid().ToString("N")[..8];

    private sealed class Ambiente : IAsyncDisposable
    {
        private readonly ServiceProvider _sp;
        private readonly IServiceScope _escopo;
        public UsuarioAtual Admin = default!, Corretor = default!;

        public Ambiente()
        {
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DATABASE_URL"] = Url }).Build();
            _sp = new ServiceCollection().AddLogging().AddInovabensCore(InovabensOpcoes.De(cfg, producao: false)).BuildServiceProvider();
            _escopo = _sp.CreateScope();
            _escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        }

        public T S<T>() where T : notnull => _escopo.ServiceProvider.GetRequiredService<T>();

        public async Task<Ambiente> Entrar()
        {
            var auth = S<AuthService>();
            Admin = (await auth.LoginAsync("admin@inovabens.com.br", Senha, null)).Usuario!;
            Corretor = (await auth.LoginAsync("carla@inovabens.com.br", Senha, null)).Usuario!;
            return this;
        }

        public async ValueTask DisposeAsync()
        {
            _escopo.Dispose();
            await _sp.DisposeAsync();
        }
    }

    private static async Task<Guid> NovoImovel(Ambiente a, UsuarioAtual dono, string titulo) =>
        await a.S<ImoveisService>().CriarAsync(dono, new ImovelForm { Titulo = titulo, Tipo = "casa", CorretorResponsavelId = dono.Id });

    [Fact]
    public async Task Pessoas_cadastro_papeis_busca_e_exclusao()
    {
        await using var a = await new Ambiente().Entrar();
        var s = a.S<PessoasService>();
        var cpf = Random.Shared.NextInt64(10_000_000_000, 99_999_999_999).ToString();

        var form = new PessoaForm
        {
            NomeRazao = $"Fulano {sufixo}", CpfCnpj = $"{cpf[..3]}.{cpf[3..6]}.{cpf[6..9]}-{cpf[9..]}", Email = $"FULANO.{sufixo}@Exemplo.com",
            Uf = "sp", Papeis = [new() { Papel = PapelPessoa.CLIENTE }, new() { Papel = PapelPessoa.PRESTADOR, Especialidade = "Engenheiro" }],
        };
        var id = await s.CriarAsync(a.Corretor, form);
        var p = await s.ObterAsync(a.Admin, id);
        Assert.Equal(cpf, p.CpfCnpj); // só dígitos, como no Node
        Assert.Equal($"fulano.{sufixo}@exemplo.com", p.Email);
        Assert.Equal("SP", p.Uf);
        Assert.Equal("Engenheiro", p.Papeis.Single(x => x.Papel == PapelPessoa.PRESTADOR).Especialidade);

        // CPF duplicado, CPF inválido e UF inválida
        await Assert.ThrowsAnyAsync<Exception>(() => s.CriarAsync(a.Admin, new PessoaForm { NomeRazao = "Outro", CpfCnpj = cpf }));
        await Assert.ThrowsAsync<RegraNegocioException>(() => s.CriarAsync(a.Admin, new PessoaForm { NomeRazao = "Outro", CpfCnpj = "123" }));
        await Assert.ThrowsAsync<RegraNegocioException>(() => s.CriarAsync(a.Admin, new PessoaForm { NomeRazao = "Outro", Uf = "São Paulo" }));

        // troca de papéis (remove um, mantém outro com nova especialidade, adiciona outro) na mesma gravação
        var f2 = PessoaForm.De(p);
        f2.Papeis = [new() { Papel = PapelPessoa.PRESTADOR, Especialidade = "Despachante" }, new() { Papel = PapelPessoa.FIADOR }];
        await s.AtualizarAsync(a.Admin, id, f2);
        p = await s.ObterAsync(a.Admin, id);
        Assert.Equal([PapelPessoa.FIADOR, PapelPessoa.PRESTADOR], p.Papeis.Select(x => x.Papel).Order());
        Assert.Equal("Despachante", p.Papeis.Single(x => x.Papel == PapelPessoa.PRESTADOR).Especialidade);

        // busca por nome, documento e filtro por papel
        Assert.Contains((await s.ListarAsync(a.Admin, new FiltroPessoas($"fulano {sufixo}"))).Itens, x => x.Id == id);
        Assert.Contains((await s.ListarAsync(a.Admin, new FiltroPessoas(cpf[..6]))).Itens, x => x.Id == id);
        Assert.Contains((await s.ListarAsync(a.Admin, new FiltroPessoas(sufixo, PapelPessoa.FIADOR))).Itens, x => x.Id == id);
        Assert.DoesNotContain((await s.ListarAsync(a.Admin, new FiltroPessoas(sufixo, PapelPessoa.CLIENTE))).Itens, x => x.Id == id);

        // corretor não exclui; admin exclui (lógico) e libera o CPF
        await Assert.ThrowsAsync<AcessoNegadoException>(() => s.ExcluirAsync(a.Corretor, id));
        await s.ExcluirAsync(a.Admin, id);
        await Assert.ThrowsAsync<NaoEncontradoException>(() => s.ObterAsync(a.Admin, id));
        await s.CriarAsync(a.Admin, new PessoaForm { NomeRazao = $"Reuso CPF {sufixo}", CpfCnpj = cpf });
    }

    [Fact]
    public async Task Memorandos_fixar_ordenar_editar_e_escopo()
    {
        await using var a = await new Ambiente().Entrar();
        var s = a.S<MemorandosService>();
        var doAdmin = await NovoImovel(a, a.Admin, $"Imóvel do admin {sufixo}");
        var daCarla = await NovoImovel(a, a.Corretor, $"Imóvel da Carla {sufixo}");

        var m1 = await s.CriarAsync(a.Corretor, daCarla, new MemorandoForm { Titulo = "Primeiro", Corpo = "texto" });
        var m2 = await s.CriarAsync(a.Corretor, daCarla, new MemorandoForm { Titulo = "Ação", Tipo = TipoMemorando.ACAO_OPERACIONAL });
        await s.FixarAsync(a.Corretor, m1, true);
        var lista = await s.ListarAsync(a.Corretor, daCarla);
        Assert.Equal([m1, m2], lista.Select(x => x.Dados.Id));
        Assert.Equal("Carla Corretora", lista[0].AutorNome);
        Assert.Equal(TipoMemorando.ACAO_OPERACIONAL, lista[1].Dados.Tipo);

        await s.AtualizarAsync(a.Corretor, m2, new MemorandoForm { Titulo = "Ação revisada", Tipo = TipoMemorando.ACAO_ESTRATEGICA });
        Assert.Equal("Ação revisada", (await s.ListarAsync(a.Admin, daCarla)).Single(x => x.Dados.Id == m2).Dados.Titulo);

        // corretor não vê imóvel de outro responsável e não exclui memorando
        await Assert.ThrowsAsync<AcessoNegadoException>(() => s.ListarAsync(a.Corretor, doAdmin));
        await Assert.ThrowsAsync<AcessoNegadoException>(() => s.ExcluirAsync(a.Corretor, m2));
        await s.ExcluirAsync(a.Admin, m2);
        Assert.Single(await s.ListarAsync(a.Admin, daCarla));
    }

    [Fact]
    public async Task Agenda_alertas_reagendamento_e_painel()
    {
        await using var a = await new Ambiente().Entrar();
        var agenda = a.S<AgendaService>();
        var alertas = a.S<AlertasService>();
        var daCarla = await NovoImovel(a, a.Corretor, $"Agenda Carla {sufixo}");
        var doAdmin = await NovoImovel(a, a.Admin, $"Agenda admin {sufixo}");
        var agora = DatasBR.Agora();

        // compromisso no passado (pendente) e outro no futuro com convidados
        var vencido = await agenda.CriarAsync(a.Corretor, daCarla, new CompromissoForm
        { Titulo = $"Visita atrasada {sufixo}", Inicio = agora.AddHours(-3), Fim = agora.AddHours(-2) });
        var futuro = await agenda.CriarAsync(a.Corretor, daCarla, new CompromissoForm
        { Titulo = $"Reunião {sufixo}", Tipo = TipoCompromisso.REUNIAO, Inicio = agora.AddDays(1), Fim = agora.AddDays(1).AddHours(1), Convidados = "a@x.com; B@x.com, a@x.com" });
        Assert.Equal(["a@x.com", "b@x.com"], (await agenda.ObterAsync(a.Corretor, futuro)).Participantes.Order());
        await Assert.ThrowsAsync<RegraNegocioException>(() => agenda.CriarAsync(a.Corretor, daCarla, new CompromissoForm
        { Titulo = "Fim antes do início", Inicio = agora, Fim = agora.AddHours(-1) }));
        await Assert.ThrowsAsync<AcessoNegadoException>(() => agenda.CriarAsync(a.Corretor, doAdmin, CompromissoFormComTitulo("Sem acesso")));

        // listagem por período (horário de Brasília convertido para UTC) e escopo
        var doMes = await agenda.ListarAsync(a.Corretor, new FiltroAgenda(DatasBR.ParaUtc(agora.AddDays(-1)), DatasBR.ParaUtc(agora.AddDays(2))));
        Assert.Contains(doMes, c => c.Dados.Id == vencido);
        Assert.Contains(doMes, c => c.Dados.Id == futuro && !c.Vencido);
        Assert.True(doMes.Single(c => c.Dados.Id == vencido).Vencido);
        var compAdmin = await agenda.CriarAsync(a.Admin, doAdmin, CompromissoFormComTitulo($"Do admin {sufixo}"));
        Assert.DoesNotContain(await agenda.ListarAsync(a.Corretor, new FiltroAgenda()), c => c.Dados.Id == compAdmin);
        await Assert.ThrowsAsync<NaoEncontradoException>(() => agenda.ObterAsync(a.Corretor, compAdmin)); // não revela que existe

        // job: gera 1 alerta (idempotente) para a responsável
        Assert.True((await alertas.VerificarCompromissosAsync()).Criados >= 1);
        Assert.Equal(0, (await alertas.VerificarCompromissosAsync()).Criados); // idempotente
        var antes = await alertas.ContagemAsync(a.Corretor);
        var alerta = (await alertas.ListarAsync(a.Corretor, false)).Single(x => x.CompromissoId == vencido);
        Assert.Equal(TipoAlerta.COMPROMISSO_NAO_CUMPRIDO, alerta.Dados.Tipo);
        Assert.Equal(daCarla, alerta.ImovelId);
        Assert.Contains("Visita atrasada", alerta.Dados.Mensagem);

        await alertas.MarcarLidoAsync(a.Corretor, alerta.Dados.Id);
        await Assert.ThrowsAsync<NaoEncontradoException>(() => alertas.MarcarLidoAsync(a.Admin, alerta.Dados.Id)); // alerta de outro usuário

        // reagendar pelo alerta: volta a pendente no futuro e encerra o alerta
        await agenda.ReagendarPorAlertaAsync(a.Corretor, alerta.Dados.Id, agora.AddDays(2), agora.AddDays(2).AddHours(1));
        Assert.Equal(antes - 1, await alertas.ContagemAsync(a.Corretor));
        var reag = await agenda.ObterAsync(a.Corretor, vencido);
        Assert.False(reag.Vencido);
        Assert.Equal(StatusCompromisso.PENDENTE, reag.Dados.Status);
        Assert.Contains(await alertas.ListarAsync(a.Corretor, true), x => x.Dados.Id == alerta.Dados.Id && x.Dados.ResolvidoEm != null);

        // concluir / reabrir / excluir
        await agenda.ConcluirAsync(a.Corretor, futuro);
        var concl = await agenda.ObterAsync(a.Corretor, futuro);
        Assert.Equal(StatusCompromisso.CONCLUIDO, concl.Dados.Status);
        Assert.NotNull(concl.Dados.ConcluidoEm);
        await agenda.MudarStatusAsync(a.Corretor, futuro, StatusCompromisso.PENDENTE);
        Assert.Null((await agenda.ObterAsync(a.Corretor, futuro)).Dados.ConcluidoEm);
        var f = CompromissoForm.De(await agenda.ObterAsync(a.Corretor, futuro));
        f.Titulo = "Reunião remarcada";
        f.Convidados = "c@x.com";
        await agenda.AtualizarAsync(a.Corretor, futuro, f);
        var atualizado = await agenda.ObterAsync(a.Corretor, futuro);
        Assert.Equal("Reunião remarcada", atualizado.Dados.Titulo);
        Assert.Equal(["c@x.com"], atualizado.Participantes);
        Assert.Equal(DatasBR.ParaUtc(f.Inicio!.Value), atualizado.Dados.InicioEm, TimeSpan.FromSeconds(1));
        await agenda.ExcluirAsync(a.Corretor, futuro);
        Assert.DoesNotContain(await agenda.DoImovelAsync(a.Corretor, daCarla), c => c.Dados.Id == futuro);

        // painel respeita escopo e permissões
        var painelCarla = await a.S<PainelService>().ResumoAsync(a.Corretor);
        var painelAdmin = await a.S<PainelService>().ResumoAsync(a.Admin);
        Assert.True(painelAdmin.ImoveisPorSituacao.Sum(x => x.Total) > painelCarla.ImoveisPorSituacao.Sum(x => x.Total));
        Assert.InRange(painelCarla.ProximosCompromissos.Count, 1, 5);
        Assert.All(painelCarla.ProximosCompromissos, c => Assert.Equal(StatusCompromisso.PENDENTE, c.Dados.Status));
        Assert.Equal(painelCarla.ProximosCompromissos.Select(c => c.Dados.InicioEm).Order(), painelCarla.ProximosCompromissos.Select(c => c.Dados.InicioEm));
        Assert.DoesNotContain(painelCarla.ProximosCompromissos, c => c.Dados.Id == compAdmin);
        Assert.NotNull(painelAdmin.LancamentosNaoPagos);

        // Google não configurado no teste: status informa e conectar é recusado de forma controlada
        var google = a.S<GoogleService>();
        Assert.False((await google.StatusAsync(a.Corretor.Id)).Configurado);
        Assert.ThrowsAny<Exception>(() => google.UrlAutorizacao(a.Corretor));
        await google.WebhookAsync("canal-inexistente", "token-falso", "exists"); // ignorado sem erro
    }

    private static CompromissoForm CompromissoFormComTitulo(string titulo)
    {
        var f = CompromissoForm.Novo();
        f.Titulo = titulo;
        return f;
    }

    [Fact]
    public async Task Alerta_de_despesa_vencida_e_resolucao_ao_pagar()
    {
        await using var a = await new Ambiente().Entrar();
        var despesas = a.S<DespesasService>();
        var alertas = a.S<AlertasService>();
        var im = await NovoImovel(a, a.Admin, $"Despesa vencida {sufixo}");
        var cat = (await despesas.CategoriasAsync())[0];
        var mes = DatasBR.Hoje().AddMonths(-2);
        mes = new DateOnly(mes.Year, mes.Month, 1);
        await despesas.CriarAsync(a.Admin, im, new DespesaForm
        { CategoriaId = cat.Id, Descricao = $"Taxa {sufixo}", Recorrencia = Recorrencia.UNICA, ValorPrevisto = 90m, InicioEm = mes });
        Assert.Equal(1, await despesas.GerarCompetenciaAsync(mes, im));
        var lanc = (await despesas.HistoricoAsync(a.Admin, im)).Single();

        var r = await alertas.VerificarDespesasAsync();
        Assert.True(r.Criados >= 2); // admin e carla (ambos com financeiro:ver no banco de teste)
        var alerta = (await alertas.ListarAsync(a.Admin, false)).Single(x => x.Dados.LancamentoId == lanc.Dados.Id);
        Assert.Equal(TipoAlerta.DESPESA_NAO_PAGA, alerta.Dados.Tipo);
        Assert.Equal(im, alerta.ImovelId);
        Assert.Equal(0, (await alertas.VerificarDespesasAsync()).Criados); // idempotente

        await despesas.AtualizarStatusAsync(a.Admin, lanc.Dados.Id, new StatusLancamentoEntrada { Status = StatusLancamento.PAGO, DataPagamento = DatasBR.Hoje() });
        // marcar como pago já encerra os alertas na hora (o job é só a rede de segurança)
        Assert.DoesNotContain(await alertas.ListarAsync(a.Admin, false), x => x.Dados.LancamentoId == lanc.Dados.Id);
    }

    [Fact]
    public async Task Crm_funil_movimentacao_perda_interacoes_e_estagios()
    {
        await using var a = await new Ambiente().Entrar();
        var crm = a.S<CrmService>();
        var pipeline = await crm.PipelineAsync(a.Corretor, Funil.VENDA);
        Assert.True(pipeline.Count >= 7);
        var inicial = pipeline[0].Estagio;
        var perdido = pipeline.First(c => c.Estagio.Tipo == TipoEstagio.PERDIDO).Estagio;
        var proposta = pipeline.First(c => c.Estagio.Nome == "Proposta").Estagio;

        // novo lead com contato novo: cria a pessoa como CLIENTE, no 1º estágio, responsável = a corretora
        var id = await crm.CriarAsync(a.Corretor, new LeadForm
        { NovoNome = $"Lead {sufixo}", NovoTelefone = "13 99999-0000", OrcamentoMin = 300000, OrcamentoMax = 500000, ProximaAcaoEm = DatasBR.Agora().AddDays(1), Origem = "Site" });
        var d = await crm.ObterAsync(a.Corretor, id);
        Assert.Equal(inicial.Id, d.Estagio.Id);
        Assert.Equal("Carla Corretora", d.ResponsavelNome);
        Assert.Contains((await a.S<PessoasService>().ObterAsync(a.Admin, d.Pessoa.Id)).Papeis, p => p.Papel == PapelPessoa.CLIENTE);
        Assert.Contains((await crm.PipelineAsync(a.Corretor, Funil.VENDA))[0].Leads, l => l.Id == id);
        Assert.DoesNotContain((await crm.PipelineAsync(a.Corretor, Funil.LOCACAO)).SelectMany(c => c.Leads), l => l.Id == id);
        await Assert.ThrowsAsync<RegraNegocioException>(() => crm.CriarAsync(a.Corretor, new LeadForm { OrcamentoMin = 10, OrcamentoMax = 5, NovoNome = "X" }));
        await Assert.ThrowsAsync<RegraNegocioException>(() => crm.CriarAsync(a.Corretor, new LeadForm()));

        // lead do admin não aparece para a corretora
        var doAdmin = await crm.CriarAsync(a.Admin, new LeadForm { NovoNome = $"Lead admin {sufixo}", Finalidade = FinalidadeLead.LOCACAO });
        Assert.DoesNotContain((await crm.PipelineAsync(a.Corretor, Funil.LOCACAO)).SelectMany(c => c.Leads), l => l.Id == doAdmin);
        await Assert.ThrowsAnyAsync<Exception>(() => crm.ObterAsync(a.Corretor, doAdmin));

        // mover; perda exige motivo; histórico registra cada troca
        await crm.MoverAsync(a.Corretor, id, proposta.Id);
        await Assert.ThrowsAsync<RegraNegocioException>(() => crm.MoverAsync(a.Corretor, id, perdido.Id));
        await crm.MoverAsync(a.Corretor, id, perdido.Id, "Comprou com outra imobiliária");
        d = await crm.ObterAsync(a.Corretor, id);
        Assert.Equal(perdido.Id, d.Estagio.Id);
        Assert.Equal("Comprou com outra imobiliária", d.Dados.MotivoPerda);
        var hist = await crm.HistoricoAsync(a.Corretor, id);
        Assert.Equal(3, hist.Count);
        Assert.Contains(hist, h => h.EstagioAnterior == "Proposta" && h.EstagioNovo == perdido.Nome);

        // interações, imóveis de interesse e edição
        await crm.CriarInteracaoAsync(a.Corretor, id, new InteracaoForm { Canal = CanalInteracao.TELEFONE, Resumo = "Ligou pedindo fotos" });
        var inter = Assert.Single(await crm.InteracoesAsync(a.Corretor, id));
        Assert.Equal("Carla Corretora", inter.UsuarioNome);
        var im = await NovoImovel(a, a.Corretor, $"Interesse {sufixo}");
        await crm.DefinirInteresseAsync(a.Corretor, id, [im]);
        Assert.Equal(im, Assert.Single((await crm.ObterAsync(a.Corretor, id)).ImoveisInteresse).Id);
        // imóvel excluído some da lista de interesse
        await a.S<ImoveisService>().ExcluirAsync(a.Admin, im);
        Assert.Empty((await crm.ObterAsync(a.Corretor, id)).ImoveisInteresse);
        await crm.DefinirInteresseAsync(a.Corretor, id, []);
        Assert.Empty((await crm.ObterAsync(a.Corretor, id)).ImoveisInteresse);
        // a corretora não vincula imóvel alheio (veria código e título), mas mantém o que um gestor vinculou
        var alheio = await NovoImovel(a, a.Admin, $"Alheio {sufixo}");
        await Assert.ThrowsAsync<NaoEncontradoException>(() => crm.DefinirInteresseAsync(a.Corretor, id, [alheio]));
        Assert.Empty((await crm.ObterAsync(a.Corretor, id)).ImoveisInteresse);
        await crm.DefinirInteresseAsync(a.Admin, id, [alheio]);
        var proprio = await NovoImovel(a, a.Corretor, $"Próprio {sufixo}");
        await crm.DefinirInteresseAsync(a.Corretor, id, [alheio, proprio]);
        Assert.Equal(2, (await crm.ObterAsync(a.Corretor, id)).ImoveisInteresse.Count);
        await Assert.ThrowsAsync<NaoEncontradoException>(() => crm.DefinirInteresseAsync(a.Corretor, id, [Guid.NewGuid()]));
        await crm.AtualizarAsync(a.Corretor, id, new LeadForm { Origem = "Indicação", OrcamentoMax = 450000 });
        Assert.Equal("Indicação", (await crm.ObterAsync(a.Corretor, id)).Dados.Origem);

        // estágios (admin:config): corretora não gerencia; criar, renomear, reordenar, excluir
        var nome = $"Teste {sufixo}";
        await Assert.ThrowsAsync<AcessoNegadoException>(() => crm.CriarEstagioAsync(a.Corretor, new EstagioForm { Nome = nome }));
        await crm.CriarEstagioAsync(a.Admin, new EstagioForm { Funil = Funil.LOCACAO, Nome = nome, Ordem = 99, Cor = "#123456" });
        var novo = (await crm.EstagiosAsync(Funil.LOCACAO)).Single(e => e.Nome == nome);
        await Assert.ThrowsAsync<RegraNegocioException>(() => crm.AtualizarEstagioAsync(a.Admin, novo.Id, new EstagioForm { Funil = Funil.LOCACAO, Nome = nome, Cor = "vermelho" }));
        await crm.AtualizarEstagioAsync(a.Admin, novo.Id, new EstagioForm { Funil = Funil.LOCACAO, Nome = nome + " ok", Ordem = 99, Cor = "#654321" });
        var locacao = await crm.EstagiosAsync(Funil.LOCACAO);
        var reordem = locacao.Select(e => e.Id).Reverse().Select((eid, i) => (eid, i + 1)).ToList();
        await crm.ReordenarAsync(a.Admin, reordem);
        Assert.Equal(novo.Id, (await crm.EstagiosAsync(Funil.LOCACAO))[0].Id);
        await crm.ReordenarAsync(a.Admin, [.. locacao.Select((e, i) => (e.Id, i + 1))]);

        // estágio com lead não pode ser excluído; vazio pode
        var leadNoNovo = await crm.CriarAsync(a.Admin, new LeadForm { NovoNome = $"No estágio {sufixo}", Finalidade = FinalidadeLead.LOCACAO });
        await crm.MoverAsync(a.Admin, leadNoNovo, novo.Id);
        await Assert.ThrowsAsync<RegraNegocioException>(() => crm.ExcluirEstagioAsync(a.Admin, novo.Id));
        await crm.ExcluirAsync(a.Admin, leadNoNovo);
        await crm.ExcluirAsync(a.Admin, doAdmin);
        // tem histórico (lead excluído logicamente): exclusão física é recusada com mensagem clara
        await Assert.ThrowsAsync<RegraNegocioException>(() => crm.ExcluirEstagioAsync(a.Admin, novo.Id));
        await crm.AtualizarEstagioAsync(a.Admin, novo.Id, new EstagioForm { Funil = Funil.LOCACAO, Nome = nome + " ok", Ordem = 99, Ativo = false });

        // auditoria registra a movimentação do lead
        var aud = await a.S<AuditoriaService>().ListarAsync(a.Admin, "leads", id);
        Assert.Contains(aud.Itens, x => x.Dados.Acao == "CRIAR");
        Assert.Contains(aud.Itens, x => x.Dados.Acao != "CRIAR");
        Assert.Contains("leads", await a.S<AuditoriaService>().EntidadesAsync(a.Admin));
        await Assert.ThrowsAsync<AcessoNegadoException>(() => a.S<AuditoriaService>().ListarAsync(a.Corretor, null, null));
    }

    [Fact]
    public async Task Falhas_de_login_simultaneas_sao_todas_contadas()
    {
        var sufixo = Guid.NewGuid().ToString("N")[..8];
        await using var a = await new Ambiente().Entrar();
        var email = $"forca.bruta.{sufixo}@inovabens.com.br";
        var perfil = (await a.S<PerfisService>().ListarAsync(a.Admin)).Single(p => p.Nome == "Corretor");
        var uid = await a.S<UsuariosService>().CriarAsync(a.Admin, new UsuarioForm { Nome = $"Alvo {sufixo}", Email = email, Senha = "SenhaBoa12345", PerfilId = perfil.Id });

        var auth = a.S<AuthService>();
        var resultados = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() => auth.LoginAsync(email, $"errada-{i}", null))));

        // as recusadas por "conta bloqueada" não chegam a conferir a senha; todas as outras precisam ser contadas
        var avaliadas = resultados.Count(r => r.Mensagem == "Credenciais inválidas.");
        await using var db = await a.S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync();
        var usr = await db.Usuarios.AsNoTracking().SingleAsync(x => x.Id == uid);
        Assert.Equal(avaliadas, usr.TentativasFalhas);
        Assert.True(usr.TentativasFalhas >= 5);
        Assert.True(usr.BloqueadoAte > DateTime.UtcNow);
        Assert.Equal(SituacaoLogin.Falha, (await auth.LoginAsync(email, "SenhaBoa12345", null)).Situacao);
    }

    [Fact]
    public async Task Administracao_usuarios_perfis_senha_e_2fa()
    {
        await using var a = await new Ambiente().Entrar();
        var usuarios = a.S<UsuariosService>();
        var perfis = a.S<PerfisService>();
        var auth = a.S<AuthService>();
        var email = $"novo.{sufixo}@inovabens.com.br";

        // perfil novo com escopo próprio e 2 permissões
        await Assert.ThrowsAsync<AcessoNegadoException>(() => perfis.CriarAsync(a.Corretor, new PerfilForm { Nome = "X" }));
        await perfis.CriarAsync(a.Admin, new PerfilForm { Nome = $"Estagiário {sufixo}", EscopoProprio = true, Permissoes = [Permissoes.ImoveisVer, Permissoes.AgendaVer] });
        var perfil = (await perfis.ListarAsync(a.Admin)).Single(p => p.Nome == $"Estagiário {sufixo}");
        Assert.Equal([Permissoes.AgendaVer, Permissoes.ImoveisVer], perfil.Permissoes.Order());
        var adminPerfil = (await perfis.ListarAsync(a.Admin)).Single(p => p.Nome == "Administrador");
        await Assert.ThrowsAnyAsync<Exception>(() => perfis.AtualizarAsync(a.Admin, adminPerfil.Id, new PerfilForm { Nome = "Administrador" }));
        await Assert.ThrowsAnyAsync<Exception>(() => perfis.ExcluirAsync(a.Admin, adminPerfil.Id));

        // usuário: senha fraca recusada; criado entra com as permissões do perfil
        await Assert.ThrowsAsync<RegraNegocioException>(() => usuarios.CriarAsync(a.Admin, new UsuarioForm { Nome = "Novo", Email = email, Senha = "curta", PerfilId = perfil.Id }));
        var uid = await usuarios.CriarAsync(a.Admin, new UsuarioForm { Nome = $"Novo {sufixo}", Email = email.ToUpperInvariant(), Senha = "SenhaBoa12345", PerfilId = perfil.Id });
        await Assert.ThrowsAnyAsync<Exception>(() => usuarios.CriarAsync(a.Admin, new UsuarioForm { Nome = "Dup", Email = email, Senha = "SenhaBoa12345", PerfilId = perfil.Id }));
        var novo = (await auth.LoginAsync(email, "SenhaBoa12345", null)).Usuario!;
        Assert.True(novo.EscopoProprio);
        Assert.True(novo.Pode(Permissoes.AgendaVer));
        Assert.False(novo.Pode(Permissoes.CrmVer));
        Assert.Contains((await usuarios.ListarAsync(a.Admin, sufixo)).Itens, u => u.Id == uid && u.PerfilNome == perfil.Nome);

        // perfil em uso não pode ser excluído; alterar permissões vale na próxima revalidação da sessão
        await Assert.ThrowsAnyAsync<Exception>(() => perfis.ExcluirAsync(a.Admin, perfil.Id));
        await perfis.AtualizarAsync(a.Admin, perfil.Id, new PerfilForm { Nome = perfil.Nome, EscopoProprio = true, Permissoes = [Permissoes.CrmVer] });
        var recarregado = (await auth.RecarregarAsync(uid))!;
        Assert.True(recarregado.Pode(Permissoes.CrmVer));
        Assert.False(recarregado.Pode(Permissoes.AgendaVer));

        // admin não se desativa nem troca o próprio perfil; desativar bloqueia login e revalidação
        await Assert.ThrowsAsync<RegraNegocioException>(() => usuarios.DefinirAtivoAsync(a.Admin, a.Admin.Id, false));
        await Assert.ThrowsAsync<RegraNegocioException>(() => usuarios.AtualizarAsync(a.Admin, a.Admin.Id,
            new UsuarioForm { Nome = "Administrador", Email = "admin@inovabens.com.br", PerfilId = perfil.Id }));
        await usuarios.DefinirAtivoAsync(a.Admin, uid, false);
        Assert.Equal(SituacaoLogin.Falha, (await auth.LoginAsync(email, "SenhaBoa12345", null)).Situacao);
        Assert.Null(await auth.RecarregarAsync(uid));
        await usuarios.DefinirAtivoAsync(a.Admin, uid, true);

        // troca de senha: senha atual errada recusada; selo muda (derruba as outras sessões)
        await Assert.ThrowsAsync<RegraNegocioException>(() => auth.AlterarSenhaAsync(novo, new AlterarSenhaForm { SenhaAtual = "errada", NovaSenha = "OutraSenha123" }));
        var seloNovo = await auth.AlterarSenhaAsync(novo, new AlterarSenhaForm { SenhaAtual = "SenhaBoa12345", NovaSenha = "OutraSenha123" });
        Assert.NotEqual(novo.Selo, seloNovo);
        Assert.Equal(seloNovo, (await auth.RecarregarAsync(uid))!.Selo);
        Assert.Equal(SituacaoLogin.Falha, (await auth.LoginAsync(email, "SenhaBoa12345", null)).Situacao);
        novo = (await auth.LoginAsync(email, "OutraSenha123", null)).Usuario!;

        // 2FA: configurar, código errado recusado, login passa a pedir código, desativar exige senha + código
        var cfg = await auth.TotpIniciarAsync(novo);
        Assert.StartsWith("otpauth://totp/", cfg.OtpauthUrl);
        Assert.False(await auth.TotpAtivoAsync(uid));
        await Assert.ThrowsAsync<RegraNegocioException>(() => auth.TotpAtivarAsync(novo, "000000"));
        var totp = new Totp(Base32Encoding.ToBytes(cfg.Segredo));
        await auth.TotpAtivarAsync(novo, totp.ComputeTotp());
        Assert.True(await auth.TotpAtivoAsync(uid));
        Assert.Equal(SituacaoLogin.PedeTotp, (await auth.LoginAsync(email, "OutraSenha123", null)).Situacao);
        Assert.Equal(SituacaoLogin.Sucesso, (await auth.LoginAsync(email, "OutraSenha123", totp.ComputeTotp())).Situacao);
        await Assert.ThrowsAsync<RegraNegocioException>(() => auth.TotpDesativarAsync(novo, "errada", totp.ComputeTotp()));
        await auth.TotpDesativarAsync(novo, "OutraSenha123", totp.ComputeTotp());
        Assert.Equal(SituacaoLogin.Sucesso, (await auth.LoginAsync(email, "OutraSenha123", null)).Situacao);
        Assert.True((await usuarios.ListarAsync(a.Admin, email)).Itens.Single().UltimoLoginEm is not null);
    }

    [Fact]
    public async Task Relatorios_pdf_e_excel_com_auditoria()
    {
        await using var a = await new Ambiente().Entrar();
        var rel = a.S<RelatoriosService>();
        var im = await NovoImovel(a, a.Corretor, $"Relatório {sufixo} – Ação & Cia");
        var codigo = (await a.S<ImoveisService>().DetalheAsync(a.Admin, im)).Dados.Codigo;
        await a.S<ImoveisService>().SalvarCaracteristicasAsync(a.Admin, im, [new() { Chave = "Piscina" }]);
        var pessoa = (await a.S<OpcoesService>().PessoasAsync(a.Admin))[0];
        await a.S<ImoveisService>().SalvarProprietariosAsync(a.Admin, im, [new() { PessoaId = pessoa.Id, Percentual = 100, Principal = true }]);
        await a.S<AquisicaoService>().SalvarAsync(a.Admin, im, new AquisicaoForm { DataAquisicao = DatasBR.Hoje().AddMonths(-6), ValorPago = 250000m, Impostos = 7500m });

        var pdf = await rel.FichaImovelPdfAsync(a.Corretor, im);
        Assert.Equal($"ficha-{codigo}.pdf", pdf.Nome);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf.Conteudo, 0, 4));

        var xlsx = await rel.FichaImovelXlsxAsync(a.Corretor, im);
        using (var wb = new XLWorkbook(new MemoryStream(xlsx.Conteudo)))
        {
            Assert.Equal(["Ficha do Imóvel", "Características", "Pessoas", "Regularização"], wb.Worksheets.Select(w => w.Name));
            Assert.Equal($"FICHA DO IMÓVEL — {codigo}", wb.Worksheet(1).Cell("A1").GetString());
            Assert.Equal("Piscina", wb.Worksheet("Características").Cell(2, 1).GetString());
            Assert.Equal(pessoa.Nome, wb.Worksheet("Pessoas").Cell(2, 2).GetString());
        }

        // ficha financeira: exige financeiro:exportar (a corretora não tem)
        await Assert.ThrowsAsync<AcessoNegadoException>(() => rel.FichaFinanceiraPdfAsync(a.Corretor, im, CodigoIndice.IPCA));
        var fpdf = await rel.FichaFinanceiraPdfAsync(a.Admin, im, CodigoIndice.IGPM);
        Assert.Equal($"ficha-financeira-{codigo}.pdf", fpdf.Nome);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(fpdf.Conteudo, 0, 4));
        var fx = await rel.FichaFinanceiraXlsxAsync(a.Admin, im, CodigoIndice.IPCA);
        using (var wb = new XLWorkbook(new MemoryStream(fx.Conteudo)))
        {
            Assert.Equal(["Resumo", "Aquisição", "Despesas", "Fluxo de caixa"], wb.Worksheets.Select(w => w.Name));
            var resumo = wb.Worksheet("Resumo");
            var linha = resumo.RowsUsed().Single(r => r.Cell(1).GetString() == "Custo de aquisição");
            Assert.Equal(257500m, linha.Cell(2).GetValue<decimal>());
        }

        // corretora não exporta imóvel de outro responsável; toda exportação fica na auditoria
        var doAdmin = await NovoImovel(a, a.Admin, $"Outro {sufixo}");
        await Assert.ThrowsAsync<AcessoNegadoException>(() => rel.FichaImovelPdfAsync(a.Corretor, doAdmin));
        await using var db = await a.S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync();
        var exportacoes = await db.AuditLog.Where(x => x.EntidadeId == im && x.Acao == "EXPORTAR").Select(x => x.ValoresDepois!).ToListAsync();
        Assert.Equal(4, exportacoes.Count);
        Assert.Contains(exportacoes, x => x.Contains("ficha-financeira") && x.Contains("IGPM"));
    }

    /// <summary>Abas que só aparecem após um clique (não entram no prerender HTTP): renderizadas no servidor com dados reais.</summary>
    [Fact]
    public async Task Componentes_interativos_renderizam_com_dados()
    {
        await using var a = await new Ambiente().Entrar();
        var im = await NovoImovel(a, a.Corretor, $"Render {sufixo}");
        await a.S<MemorandosService>().CriarAsync(a.Corretor, im, new MemorandoForm { Titulo = $"Memo {sufixo}", Corpo = "linha 1", Fixado = true });
        await a.S<AgendaService>().CriarAsync(a.Corretor, im, CompromissoFormComTitulo($"Visita {sufixo}"));

        var servicos = new ServiceCollection();
        foreach (var t in new[] { typeof(MemorandosService), typeof(AgendaService), typeof(OpcoesService), typeof(UsuariosService), typeof(PerfisService),
                     typeof(DespesasService), typeof(IndicesService), typeof(CrmService), typeof(AuditoriaService) })
            servicos.AddSingleton(t, a.S<IServiceProvider>().GetRequiredService(t));
        servicos.AddSingleton(a.S<ILoggerFactory>());
        servicos.AddSingleton<IJSRuntime, SemJs>();
        await using var provedor = servicos.BuildServiceProvider();

        async Task<string> Renderizar<T>(UsuarioAtual u, Dictionary<string, object?>? p = null) where T : IComponent
        {
            await using var renderer = new HtmlRenderer(provedor, provedor.GetRequiredService<ILoggerFactory>());
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var raiz = await renderer.RenderComponentAsync<CascadingValue<UsuarioAtual>>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Value"] = u, ["IsFixed"] = true,
                    ["ChildContent"] = (RenderFragment)(b =>
                    {
                        b.OpenComponent<T>(0);
                        var i = 1;
                        foreach (var (k, v) in p ?? []) b.AddAttribute(i++, k, v);
                        b.CloseComponent();
                    }),
                }));
                await raiz.QuiescenceTask;
                return System.Net.WebUtility.HtmlDecode(raiz.ToHtmlString());
            });
        }

        var memos = await Renderizar<Web.Components.Imoveis.MemorandosAba>(a.Corretor, new() { ["ImovelId"] = im });
        Assert.Contains($"Memo {sufixo}", memos);
        Assert.Contains("📌", memos);
        Assert.DoesNotContain("msg-erro", memos);
        var agenda = await Renderizar<Web.Components.Imoveis.AgendaImovelAba>(a.Corretor, new() { ["ImovelId"] = im });
        Assert.Contains($"Visita {sufixo}", agenda);
        Assert.Contains("Novo compromisso", agenda);
        Assert.DoesNotContain("msg-erro", agenda);

        Assert.Contains("Carla Corretora", await Renderizar<Web.Components.Administracao.UsuariosAdmin>(a.Admin));
        var perfisHtml = await Renderizar<Web.Components.Administracao.PerfisAdmin>(a.Admin);
        Assert.Contains("Corretor", perfisHtml);
        var config = await Renderizar<Web.Components.Administracao.ConfiguracoesAdmin>(a.Admin);
        Assert.Contains("Novo Lead", config);
        Assert.Contains("IPTU", config);
        Assert.DoesNotContain("msg-erro", config);
        var auditoria = await Renderizar<Web.Components.Administracao.AuditoriaAdmin>(a.Admin);
        Assert.Contains("memorandos", auditoria);
        Assert.DoesNotContain("msg-erro", auditoria);
        // sem permissão o componente mostra o erro controlado, não quebra
        Assert.Contains("msg-erro", await Renderizar<Web.Components.Administracao.AuditoriaAdmin>(a.Corretor));
    }

    private sealed class SemJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new InvalidOperationException("JS indisponível no teste");
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args) => throw new InvalidOperationException("JS indisponível no teste");
    }
}
