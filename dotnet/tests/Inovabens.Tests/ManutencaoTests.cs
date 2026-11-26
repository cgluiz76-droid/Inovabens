using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Financeiro;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Locacao;
using Inovabens.Core.Pessoas;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Rateio do conserto nos repasses e reembolso do item de manutenção da fatura (sem banco).</summary>
public class CalculoManutencaoTests
{
    [Fact]
    public void Abate_proporcional_ao_liquido_e_limitado_ao_disponivel()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        IReadOnlyList<RepasseCalculado> reps = [new(a, 60, 1200, 120, 0), new(b, 40, 800, 80, 0)]; // líquidos 1080 e 720
        var r = CalculoLocacao.AbaterManutencao(reps, 300);
        Assert.Equal(180, r[0].DescontoManutencao);
        Assert.Equal(120, r[1].DescontoManutencao);
        Assert.Equal(1500, r.Sum(x => x.Liquido));

        var tudo = CalculoLocacao.AbaterManutencao(reps, 5000); // mais que o disponível: zera o líquido
        Assert.Equal(1800, tudo.Sum(x => x.DescontoManutencao));
        Assert.All(tudo, x => Assert.Equal(0, x.Liquido));
        Assert.Same(reps, CalculoLocacao.AbaterManutencao(reps, 0));
    }

    [Fact]
    public void Reembolso_proporcional_ao_pagamento()
    {
        List<FaturaLocacaoItem> itens =
        [
            new() { Tipo = TipoItemFatura.ALUGUEL, Valor = 1000 },
            new() { Tipo = TipoItemFatura.MANUTENCAO, Valor = 200 },
        ];
        Assert.Equal(200, CalculoLocacao.Reembolso(itens, 1200));
        Assert.Equal(100, CalculoLocacao.Reembolso(itens, 600));
        Assert.Equal(0, CalculoLocacao.Reembolso([itens[0]], 1000));
        Assert.Equal(ResponsavelManutencao.LOCADOR, ManutencaoService.ResponsavelSugerido(CategoriaManutencao.TELHADO));
        Assert.Equal(CobrancaManutencao.FATURA, ManutencaoService.CobrancaSugerida(ResponsavelManutencao.LOCATARIO, ModalidadeLocacao.ADMINISTRACAO));
        Assert.Equal(CobrancaManutencao.REPASSE, ManutencaoService.CobrancaSugerida(ResponsavelManutencao.LOCADOR, ModalidadeLocacao.ADMINISTRACAO));
    }
}

/// <summary>Manutenção (locação, fase 5): chamado, orçamento, aprovação pelo link, agenda com chave, conclusão e custo.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class ManutencaoTests : IAsyncLifetime
{
    private IServiceScope _escopo = default!;
    private T S<T>() where T : notnull => _escopo.ServiceProvider.GetRequiredService<T>();

    public Task InitializeAsync()
    {
        _escopo = ExtratoComprovantesTests.Compartilhado.CreateScope();
        _escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _escopo.Dispose();
        return Task.CompletedTask;
    }

    private async Task<UsuarioAtual> Admin() => (await S<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
    private static string Unico(string nome) => $"{nome} {Guid.NewGuid().ToString("N")[..6]}";

    private async Task<Guid> Pessoa(UsuarioAtual u, string nome) =>
        await S<PessoasService>().CriarAsync(u, new PessoaForm { NomeRazao = Unico(nome), Whatsapp = "(11) 98888-7777" });

    /// <summary>Contrato de administração ativo (começou há 2 meses), sem intermediação para o repasse sair cheio.</summary>
    private async Task<(Guid Contrato, Guid Imovel, Guid Dono)> Contrato(UsuarioAtual u, decimal limite = 0)
    {
        var dono = await Pessoa(u, "Lia Locadora");
        var inquilino = await Pessoa(u, "Rui Inquilino");
        var imovel = await S<ImoveisService>().CriarAsync(u, new ImovelForm
        {
            NomeDrive = Unico("Manut"), Titulo = Unico("Casa"), Tipo = "casa", Finalidade = FinalidadeImovel.LOCACAO, PrecoLocacao = 2000,
        });
        await S<ImoveisService>().SalvarProprietariosAsync(u, imovel, [new ProprietarioEntrada { PessoaId = dono, Percentual = 100 }]);
        var loc = S<LocacaoService>();
        var form = await loc.NovoParaImovelAsync(u, imovel);
        var hoje = DatasBR.Hoje();
        form.Inicio = new DateOnly(hoje.Year, hoje.Month, 1).AddMonths(-2);
        form.IntermediacaoValor = 0;
        form.LimiteManutencao = limite;
        form.Partes.Add(new ParteForm { PessoaId = inquilino, Papel = PapelContratoLocacao.LOCATARIO });
        var id = await loc.CriarAsync(u, form);
        await loc.AtivarAsync(u, id);
        return (id, imovel, dono);
    }

    private async Task<Guid> Conta(UsuarioAtual u) =>
        await S<FinanceiroEmpresaService>().CriarContaAsync(u, new ContaBancariaForm { Nome = Unico("Cora"), DataSaldoInicial = new(2020, 1, 1) });

    [Fact]
    public async Task Aprovacao_pelo_link_agenda_com_chave_e_desconto_no_repasse()
    {
        var admin = await Admin();
        var m = S<ManutencaoService>();
        var loc = S<LocacaoService>();
        var (contrato, imovel, _) = await Contrato(admin, limite: 300);
        var prestador = await Pessoa(admin, "Zé Encanador");

        var id = await m.CriarAsync(admin, new ChamadoForm
        {
            ImovelId = imovel, Titulo = "Vazamento no banheiro", Categoria = CategoriaManutencao.HIDRAULICA, Urgencia = UrgenciaManutencao.ALTA,
            Responsavel = ManutencaoService.ResponsavelSugerido(CategoriaManutencao.HIDRAULICA),
        });
        var d = await m.ObterAsync(admin, id);
        Assert.Equal(contrato, d.Chamado.ContratoId); // pega o contrato ativo do imóvel
        Assert.Equal(ResponsavelManutencao.LOCADOR, d.Chamado.Responsavel);
        Assert.StartsWith("MAN-", d.Codigo);

        var barato = await m.AdicionarOrcamentoAsync(admin, id, new OrcamentoForm { PrestadorId = prestador, Valor = 450, PrazoDias = 2 });
        await m.AdicionarOrcamentoAsync(admin, id, new OrcamentoForm { PrestadorId = prestador, Valor = 700 });
        Assert.Equal(StatusChamado.ORCAMENTO, (await m.ObterAsync(admin, id)).Chamado.Status);

        // acima do limite pré-aprovado (300): pede ao proprietário pelo link
        // proprietário escolhe entre 3 cotações: com 2 ainda não dá
        await Assert.ThrowsAsync<RegraNegocioException>(() => m.PedirAprovacaoAsync(admin, id, barato));
        await Assert.ThrowsAsync<RegraNegocioException>(() => m.PedirAprovacaoAsync(admin, id, barato, "   "));
        var terceira = await m.AdicionarOrcamentoAsync(admin, id, new OrcamentoForm { PrestadorId = prestador, Valor = 900 });

        // cotações anexadas vão para a subpasta "Cotações" da pasta do chamado
        await m.AnexarCotacaoAsync(admin, barato, "orcamento ze.pdf", [7, 8, 9], "application/pdf");
        var drive = (ArmazenamentoMemoria)S<IArmazenamento>();
        var det = await m.ObterAsync(admin, id);
        var cot = det.Orcamentos.Single(o => o.Id == barato).Cotacao;
        Assert.NotNull(cot);
        Assert.StartsWith("Cotação 1 - Zé Encanador", cot);
        Assert.EndsWith(".pdf", cot);
        var arq = drive.Itens.Values.Single(i => i.Nome == cot);
        Assert.Equal(ManutencaoService.SubpastaCotacoes, drive.Itens[arq.Pai!].Nome);
        Assert.Equal(det.Chamado.DrivePastaId, drive.Itens[arq.Pai!].Pai);
        Assert.Null(det.Orcamentos.Single(o => o.Id == terceira).Cotacao);

        var pedido = await m.PedirAprovacaoAsync(admin, id, barato);
        Assert.False(pedido.AprovadoNaHora);
        Assert.Contains("/aprovacao/", pedido.Link);
        Assert.StartsWith("https://wa.me/5511988887777?text=", pedido.LinkWhatsapp);
        var token = pedido.Link!.Split('/')[^1];

        var publica = await m.AprovacaoAsync(token);
        Assert.NotNull(publica);
        Assert.Equal(3, publica!.Orcamentos.Count);
        Assert.True(publica.Orcamentos.Single(o => o.Id == barato).TemCotacao);
        Assert.Equal(new byte[] { 7, 8, 9 }, (await m.CotacaoPublicaAsync(pedido.Link!.Split('/')[^1], barato))!.Dados);
        Assert.Null(await m.CotacaoPublicaAsync(pedido.Link!.Split('/')[^1], terceira)); // sem arquivo
        Assert.Null(await m.CotacaoPublicaAsync("outro-token", barato));
        Assert.Equal(barato, publica.Sugerido);
        Assert.Null(await m.AprovacaoAsync("token-que-nao-existe"));

        Assert.True(await m.ResponderAprovacaoAsync(token, aprovar: true, barato, "Pode fazer", "Lia"));
        await Assert.ThrowsAsync<RegraNegocioException>(() => m.ResponderAprovacaoAsync(token, false, null, null, null));
        d = await m.ObterAsync(admin, id);
        Assert.Equal(StatusChamado.APROVADO, d.Chamado.Status);
        Assert.Equal(450, d.Chamado.Valor);
        Assert.Contains("Lia", d.Chamado.AprovadoPor);
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
            Assert.True(await db.Alertas.AnyAsync(a => a.ChamadoManutencaoId == id && a.UsuarioId == admin.Id && a.Tipo == TipoAlerta.MANUTENCAO));

        // agenda: compromisso + chave entregue ao prestador + mensagens prontas
        var chave = await S<ChavesService>().CriarAsync(admin, imovel, new ChaveForm { Codigo = Unico("M")[..8].Replace(" ", ""), Descricao = "Porta" });
        var inicio = DatasBR.Agora().Date.AddDays(1).AddHours(9);
        var avisos = await m.AgendarAsync(admin, id, new AgendamentoForm { Inicio = inicio, ChaveId = chave });
        Assert.StartsWith("https://wa.me/", avisos.WhatsappPrestador);
        Assert.StartsWith("https://wa.me/", avisos.WhatsappInquilino);
        d = await m.ObterAsync(admin, id);
        Assert.Equal(StatusChamado.AGENDADO, d.Chamado.Status);
        Assert.NotNull(d.Chamado.CompromissoId);
        var chaves = await S<ChavesService>().DoImovelAsync(admin, imovel);
        Assert.Contains("Zé Encanador", chaves.Chaves.Single(k => k.Id == chave).Fora?.ComQuem);

        // conclusão por conta do locador: conta a pagar ao prestador e desconto no próximo repasse
        await m.ConcluirAsync(admin, id, new ConclusaoForm
        {
            Data = DatasBR.Hoje(), ValorFinal = 450, Cobranca = CobrancaManutencao.REPASSE, VencimentoPrestador = DatasBR.Hoje().AddDays(5), Avaliacao = 5,
        });
        d = await m.ObterAsync(admin, id);
        Assert.Equal(StatusChamado.CONCLUIDO, d.Chamado.Status);
        Assert.Equal(0, d.AbatidoRepasse);
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
        {
            var l = await db.LancamentosEmpresa.Include(x => x.Categoria).SingleAsync(x => x.Id == d.Chamado.LancamentoEmpresaId);
            Assert.Equal(-450, l.Valor);
            Assert.Equal(ManutencaoService.CategoriaPagoPeloProprietario, l.Categoria!.Nome);
            Assert.Equal(prestador, l.PessoaId);
        }

        var f = (await loc.ObterAsync(admin, contrato)).Faturas[0];
        var pg = await loc.ReceberAsync(admin, f.Id, new RecebimentoLocacaoForm { Data = f.Vencimento, Principal = f.Total, ContaBancariaId = await Conta(admin) });
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
        {
            var reps = await db.RepassesLocacao.Where(r => r.PagamentoId == pg).ToListAsync();
            var desconto = reps.Sum(r => r.DescontoManutencao);
            Assert.Equal(Math.Min(450, reps.Sum(r => r.Liquido + r.DescontoManutencao)), desconto);
            Assert.True(desconto > 0);
            Assert.Equal(desconto, (await m.ObterAsync(admin, id)).AbatidoRepasse);
        }

        // estorno do recebimento: o desconto volta a ficar pendente
        await loc.EstornarAsync(admin, pg);
        Assert.Equal(0, (await m.ObterAsync(admin, id)).AbatidoRepasse);
    }

    [Fact]
    public async Task Menos_de_3_cotacoes_vai_ao_proprietario_com_justificativa()
    {
        var admin = await Admin();
        var m = S<ManutencaoService>();
        var (_, imovel, _) = await Contrato(admin);
        var prestador = await Pessoa(admin, "Ivo Telhadista");
        var id = await m.CriarAsync(admin, new ChamadoForm { ImovelId = imovel, Titulo = "Goteira no quarto", Categoria = CategoriaManutencao.TELHADO });
        var orc = await m.AdicionarOrcamentoAsync(admin, id, new OrcamentoForm { PrestadorId = prestador, Valor = 1200 });
        var pedido = await m.PedirAprovacaoAsync(admin, id, orc, "Urgência: chuva forte, único telhadista disponível");
        Assert.False(pedido.AprovadoNaHora);
        var d = await m.ObterAsync(admin, id);
        Assert.Equal(StatusChamado.AGUARDANDO_APROVACAO, d.Chamado.Status);
        Assert.Contains(d.Eventos, e => e.Descricao.Contains("Justificativa: Urgência"));
        var publica = await m.AprovacaoAsync(pedido.Link!.Split('/')[^1]);
        Assert.StartsWith("Urgência", publica!.JustificativaCotacoes);
        Assert.True(await m.ResponderAprovacaoAsync(pedido.Link.Split('/')[^1], true, orc, null, "Lia"));
        Assert.Equal(StatusChamado.APROVADO, (await m.ObterAsync(admin, id)).Chamado.Status);
    }

    [Fact]
    public async Task Conserto_do_inquilino_vai_na_fatura_e_volta_como_reembolso()
    {
        var admin = await Admin();
        var m = S<ManutencaoService>();
        var loc = S<LocacaoService>();
        var (contrato, imovel, _) = await Contrato(admin);
        var prestador = await Pessoa(admin, "Eli Eletricista");
        var id = await m.CriarAsync(admin, new ChamadoForm
        {
            ImovelId = imovel, Titulo = "Trocar tomada queimada", Categoria = CategoriaManutencao.ELETRICA, Responsavel = ResponsavelManutencao.LOCATARIO,
        });
        var orc = await m.AdicionarOrcamentoAsync(admin, id, new OrcamentoForm { PrestadorId = prestador, Valor = 150 });
        var pedido = await m.PedirAprovacaoAsync(admin, id, orc);
        Assert.True(pedido.AprovadoNaHora); // custo do inquilino: não depende do proprietário

        // a fatura do mês seguinte ainda em aberto recebe o item
        var faturas = (await loc.ObterAsync(admin, contrato)).Faturas;
        var alvo = faturas.Where(x => x.Status == StatusFaturaLocacao.ABERTA && x.Vencimento >= DatasBR.Hoje()).OrderBy(x => x.Vencimento).First();
        await m.ConcluirAsync(admin, id, new ConclusaoForm
        {
            Data = DatasBR.Hoje(), ValorFinal = 150, Cobranca = CobrancaManutencao.FATURA, VencimentoPrestador = DatasBR.Hoje(),
        });
        var f = (await loc.ObterAsync(admin, contrato)).Faturas.Single(x => x.Id == alvo.Id);
        Assert.Contains(f.Itens, i => i.Tipo == TipoItemFatura.MANUTENCAO && i.Valor == 150);
        Assert.Equal(alvo.Total + 150, f.Total);

        var pg = await loc.ReceberAsync(admin, f.Id, new RecebimentoLocacaoForm { Data = DatasBR.Hoje(), Principal = f.Total, ContaBancariaId = await Conta(admin) });
        await using var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync();
        var p = await db.FaturaLocacaoPagamentos.SingleAsync(x => x.Id == pg);
        var lancs = await db.LancamentosEmpresa.Include(l => l.Categoria).Where(l => p.LancamentosEmpresaIds.Contains(l.Id)).ToListAsync();
        Assert.Contains(lancs, l => l.Categoria!.Nome == ManutencaoService.CategoriaReembolsoInquilino && l.Valor == 150);
        Assert.Equal(p.Total, lancs.Where(l => l.Valor > 0).Sum(l => l.Valor));
        var reps = await db.RepassesLocacao.Where(r => r.PagamentoId == pg).ToListAsync();
        Assert.Equal(p.Total - 150, reps.Sum(r => r.Bruto)); // o conserto não vai para o proprietário
    }

    [Fact]
    public async Task Inquilino_abre_chamado_pelo_link_e_preventiva_gera_chamado()
    {
        var admin = await Admin();
        var m = S<ManutencaoService>();
        var (contrato, imovel, _) = await Contrato(admin);

        var link = await m.LinkInquilinoAsync(admin, contrato);
        Assert.Contains("/chamado/", link.Link);
        Assert.Equal(link.Link, (await m.LinkInquilinoAsync(admin, contrato)).Link); // link fixo do contrato
        var token = link.Link.Split('/')[^1];
        Assert.NotNull(await m.ContextoInquilinoAsync(token));
        Assert.Null(await m.ContextoInquilinoAsync("nao-existe"));

        var form = new AberturaInquilinoForm
        {
            Nome = "Rui", Telefone = "11977776666", Titulo = "Portão não fecha", Categoria = CategoriaManutencao.ESQUADRIAS,
            Urgencia = UrgenciaManutencao.EMERGENCIA,
        };
        var codigo = await m.AbrirPeloInquilinoAsync(token, form, [("foto.jpg", new byte[] { 1, 2, 3 }, "image/jpeg")]);
        Assert.StartsWith("MAN-", codigo);
        await Assert.ThrowsAsync<RegraNegocioException>(() =>
            m.AbrirPeloInquilinoAsync(token, form, [("x.exe", new byte[] { 1 }, "application/octet-stream")]));
        Assert.Equal("", await m.AbrirPeloInquilinoAsync(token, new AberturaInquilinoForm { Site = "spam" }, [])); // robô
        Assert.Null(await m.AbrirPeloInquilinoAsync("nao-existe", form, []));

        var ch = (await m.ListarAsync(admin, new FiltroChamados(ContratoId: contrato))).Single();
        Assert.Equal(OrigemChamado.INQUILINO, ch.Origem);
        var d = await m.ObterAsync(admin, ch.Id);
        Assert.Single(d.Arquivos, a => a.PeloInquilino && a.Tipo == TipoArquivoChamado.FOTO);
        Assert.Contains("Portão", (await m.ContextoInquilinoAsync(token))!.Abertos.Single().Titulo);
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
            Assert.True(await db.Alertas.AnyAsync(a => a.ChamadoManutencaoId == ch.Id && a.Mensagem.Contains("EMERGÊNCIA")));

        // preventiva: abre o chamado 30 dias antes e avança a próxima data; não duplica enquanto o chamado está aberto
        var hoje = DatasBR.Hoje();
        var prev = await m.SalvarPreventivaAsync(admin, null, new PreventivaForm
        {
            ImovelId = imovel, Titulo = "Limpeza da caixa d'água", PeriodicidadeMeses = 6, ProximaEm = hoje.AddDays(10),
        });
        Assert.True(await m.GerarPreventivasAsync(hoje) >= 1);
        await m.GerarPreventivasAsync(hoje);
        var preventiva = (await m.PreventivasAsync(admin, imovel)).Single(p => p.Id == prev);
        Assert.Equal(hoje.AddDays(10).AddMonths(6), preventiva.ProximaEm);
        var gerados = (await m.ListarAsync(admin, new FiltroChamados(ImovelId: imovel))).Where(c => c.Origem == OrigemChamado.PREVENTIVA).ToList();
        Assert.Single(gerados);
        Assert.Equal(contrato, gerados[0].ContratoId);
    }
}
