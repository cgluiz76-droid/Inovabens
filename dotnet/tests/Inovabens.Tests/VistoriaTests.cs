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

/// <summary>Vistoria terceirizada (locação, fase 4): agenda com chave, laudo no Drive, custo, aceite, pendências e rescisão.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class VistoriaTests : IAsyncLifetime
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
        await S<PessoasService>().CriarAsync(u, new PessoaForm { NomeRazao = Unico(nome), Whatsapp = "(11) 97777-6666" });

    /// <summary>Contrato de administração ativo há 2 meses, sem intermediação (repasse cheio).</summary>
    private async Task<(Guid Contrato, Guid Imovel)> Contrato(UsuarioAtual u)
    {
        var dono = await Pessoa(u, "Ana Locadora");
        var inquilino = await Pessoa(u, "Beto Inquilino");
        var imovel = await S<ImoveisService>().CriarAsync(u, new ImovelForm
        {
            NomeDrive = Unico("Vist"), Titulo = Unico("Apto"), Tipo = "apartamento", Finalidade = FinalidadeImovel.LOCACAO, PrecoLocacao = 1800,
        });
        await S<ImoveisService>().SalvarProprietariosAsync(u, imovel, [new ProprietarioEntrada { PessoaId = dono, Percentual = 100 }]);
        var loc = S<LocacaoService>();
        var form = await loc.NovoParaImovelAsync(u, imovel);
        var hoje = DatasBR.Hoje();
        form.Inicio = new DateOnly(hoje.Year, hoje.Month, 1).AddMonths(-2);
        form.IntermediacaoValor = 0;
        form.Partes.Add(new ParteForm { PessoaId = inquilino, Papel = PapelContratoLocacao.LOCATARIO });
        var id = await loc.CriarAsync(u, form);
        await loc.AtivarAsync(u, id);
        return (id, imovel);
    }

    [Fact]
    public async Task Entrada_agenda_com_chave_laudo_na_pasta_e_custo_no_repasse()
    {
        var admin = await Admin();
        var v = S<VistoriaService>();
        var loc = S<LocacaoService>();
        var (contrato, imovel) = await Contrato(admin);
        var empresa = await Pessoa(admin, "Vistoria Rápida");

        var id = await v.CriarAsync(admin, new VistoriaForm { ContratoId = contrato, Tipo = TipoVistoria.ENTRADA, EmpresaId = empresa });
        await Assert.ThrowsAsync<RegraNegocioException>(() => v.CriarAsync(admin, new VistoriaForm { ContratoId = contrato, Tipo = TipoVistoria.ENTRADA }));
        var d = await v.ObterAsync(admin, id);
        Assert.StartsWith("VIS-", d.Codigo);
        Assert.Equal(imovel, d.Vistoria.ImovelId);
        Assert.Contains("Beto Inquilino", d.LocatarioNome);

        // agenda: compromisso + chave com a empresa + mensagens prontas
        var chave = await S<ChavesService>().CriarAsync(admin, imovel, new ChaveForm { Codigo = Unico("V")[..8].Replace(" ", ""), Descricao = "Porta" });
        var inicio = DatasBR.Agora().Date.AddDays(1).AddHours(10);
        var avisos = await v.AgendarAsync(admin, id, new AgendamentoVistoriaForm { Inicio = inicio, ChaveId = chave });
        Assert.StartsWith("https://wa.me/", avisos.WhatsappPrestador);
        Assert.StartsWith("https://wa.me/", avisos.WhatsappInquilino);
        d = await v.ObterAsync(admin, id);
        Assert.NotNull(d.Vistoria.CompromissoId);
        Assert.Equal(inicio, DatasBR.DeUtc(d.Vistoria.AgendadaPara!.Value));
        Assert.Contains("Vistoria Rápida", (await S<ChavesService>().DoImovelAsync(admin, imovel)).Chaves.Single(k => k.Id == chave).Fora?.ComQuem);

        // custo só depois de realizada; data futura não vale
        var custo = new CustoVistoriaForm { Valor = 250, Cobranca = CobrancaManutencao.REPASSE, Vencimento = DatasBR.Hoje().AddDays(5) };
        await Assert.ThrowsAsync<RegraNegocioException>(() => v.LancarCustoAsync(admin, id, custo));
        await Assert.ThrowsAsync<RegraNegocioException>(() => v.RegistrarRealizacaoAsync(admin, id, new RealizacaoVistoriaForm { Data = DatasBR.Hoje().AddDays(1) }));
        await v.RegistrarRealizacaoAsync(admin, id, new RealizacaoVistoriaForm
        {
            Data = DatasBR.Hoje(), LeituraAgua = "1234", LeituraLuz = "5678", Chaves = 3,
        });
        d = await v.ObterAsync(admin, id);
        Assert.Equal(StatusVistoria.REALIZADA, d.Vistoria.Status);
        Assert.Equal(DatasBR.Hoje().AddDays(VistoriaService.DiasContestacao), d.Vistoria.PrazoContestacao);

        // laudo na subpasta "Vistorias" da pasta do contrato, com nome padronizado
        await v.EnviarLaudoAsync(admin, id, "laudo empresa.pdf", [1, 2, 3], "application/pdf");
        d = await v.ObterAsync(admin, id);
        Assert.Equal($"{d.Codigo} - Laudo de entrada - {DatasBR.Hoje():dd-MM-yyyy}.pdf", d.Vistoria.LaudoNome);
        var drive = (ArmazenamentoMemoria)S<IArmazenamento>();
        var arq = drive.Itens[d.Vistoria.LaudoDriveId!];
        Assert.Equal(VistoriaService.SubpastaVistorias, drive.Itens[arq.Pai!].Nome);
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
            Assert.Equal(await db.ContratosLocacao.Where(c => c.Id == contrato).Select(c => c.DrivePastaId).SingleAsync(), drive.Itens[arq.Pai!].Pai);
        Assert.Equal(new byte[] { 1, 2, 3 }, (await v.LaudoAsync(admin, id)).Conteudo);

        // custo por conta do locador: conta a pagar à empresa e desconto no próximo repasse (uma vez só)
        await v.LancarCustoAsync(admin, id, custo);
        await Assert.ThrowsAsync<RegraNegocioException>(() => v.LancarCustoAsync(admin, id, custo));
        d = await v.ObterAsync(admin, id);
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
        {
            var l = await db.LancamentosEmpresa.SingleAsync(x => x.Id == d.Vistoria.LancamentoEmpresaId);
            Assert.Equal(-250, l.Valor);
            Assert.Equal(empresa, l.PessoaId);
        }
        var f = (await loc.ObterAsync(admin, contrato)).Faturas[0];
        var conta = await S<FinanceiroEmpresaService>().CriarContaAsync(admin, new ContaBancariaForm { Nome = Unico("Cora"), DataSaldoInicial = new(2020, 1, 1) });
        var pg = await loc.ReceberAsync(admin, f.Id, new RecebimentoLocacaoForm { Data = f.Vencimento, Principal = f.Total, ContaBancariaId = conta });
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
        {
            var desconto = (await db.RepassesLocacao.Where(r => r.PagamentoId == pg).ToListAsync()).Sum(r => r.DescontoManutencao);
            Assert.Equal(250, desconto);
            Assert.True(await db.ManutencaoAbatimentos.AnyAsync(a => a.VistoriaId == id && a.ChamadoId == null));
        }
        Assert.Equal(250, (await v.ObterAsync(admin, id)).AbatidoRepasse);

        await v.RegistrarAceiteAsync(admin, id, DatasBR.Hoje());
        Assert.Equal(DatasBR.Hoje(), (await v.ObterAsync(admin, id)).Vistoria.AceiteEm);
        Assert.Contains((await v.ListarAsync(admin, contrato)), x => x.Id == id && x.TemLaudo);
    }

    [Fact]
    public async Task Saida_compara_com_entrada_e_pendencias_viram_chamado_ou_debito()
    {
        var admin = await Admin();
        var v = S<VistoriaService>();
        var (contrato, _) = await Contrato(admin);

        var entrada = await v.CriarAsync(admin, new VistoriaForm { ContratoId = contrato, Tipo = TipoVistoria.ENTRADA });
        await v.RegistrarRealizacaoAsync(admin, entrada, new RealizacaoVistoriaForm { Data = DatasBR.Hoje().AddDays(-60), Chaves = 4, LeituraAgua = "100" });
        await v.AdicionarPendenciaAsync(admin, entrada, new PendenciaForm { Comodo = "Sala", Descricao = "Risco no piso", Responsavel = ResponsavelManutencao.LOCADOR });

        var saida = await v.CriarAsync(admin, new VistoriaForm { ContratoId = contrato, Tipo = TipoVistoria.SAIDA });
        await v.RegistrarRealizacaoAsync(admin, saida, new RealizacaoVistoriaForm { Data = DatasBR.Hoje(), Chaves = 3, LeituraAgua = "180" });
        var d = await v.ObterAsync(admin, saida);
        var en = Assert.Single(d.Anteriores);
        Assert.Equal(entrada, en.Id);
        Assert.Equal(4, en.Chaves);
        Assert.Single(en.Pendencias);

        // locador: vira chamado de manutenção do contrato
        var pLocador = await v.AdicionarPendenciaAsync(admin, saida, new PendenciaForm
        {
            Comodo = "Banheiro", Descricao = "Infiltração no teto", Responsavel = ResponsavelManutencao.LOCADOR,
        });
        var chamado = await v.AbrirChamadoAsync(admin, pLocador);
        var ch = await S<ManutencaoService>().ObterAsync(admin, chamado);
        Assert.Equal(contrato, ch.Chamado.ContratoId);
        Assert.Equal(ResponsavelManutencao.LOCADOR, ch.Chamado.Responsavel);
        Assert.StartsWith("Banheiro: Infiltração", ch.Chamado.Titulo);
        await Assert.ThrowsAsync<RegraNegocioException>(() => v.AbrirChamadoAsync(admin, pLocador));

        // inquilino: com valor vira débito sugerido na rescisão; sem valor não entra
        var pPintura = await v.AdicionarPendenciaAsync(admin, saida, new PendenciaForm { Comodo = "Quarto", Descricao = "Pintura manchada", Valor = 300 });
        await v.AdicionarPendenciaAsync(admin, saida, new PendenciaForm { Descricao = "Limpeza geral" });
        var pChave = await v.AdicionarPendenciaAsync(admin, saida, new PendenciaForm { Descricao = "1 chave não devolvida", Valor = 40 });
        await v.RemoverPendenciaAsync(admin, pChave);

        var deb = await v.DebitosSaidaAsync(admin, contrato);
        Assert.Equal(300, deb.Total);
        Assert.Contains("Pintura manchada", deb.Descricao);
        Assert.Equal([pPintura], deb.Pendencias);

        await v.MarcarCobradasAsync(admin, deb.Pendencias);
        Assert.Equal(0, (await v.DebitosSaidaAsync(admin, contrato)).Total);
        d = await v.ObterAsync(admin, saida);
        Assert.Equal(StatusPendenciaVistoria.COBRADA, d.Pendencias.Single(p => p.Id == pPintura).Status);
        Assert.Equal(StatusPendenciaVistoria.CHAMADO, d.Pendencias.Single(p => p.Id == pLocador).Status);
        Assert.NotNull(d.Pendencias.Single(p => p.Id == pLocador).ChamadoCodigo);

        // contestação e cancelamento
        await v.RegistrarContestacaoAsync(admin, saida, DatasBR.Hoje(), "Pintura já estava assim");
        Assert.Equal("Pintura já estava assim", (await v.ObterAsync(admin, saida)).Vistoria.Contestacao);
        await Assert.ThrowsAsync<RegraNegocioException>(() => v.CancelarAsync(admin, saida, "erro"));
        var periodica = await v.CriarAsync(admin, new VistoriaForm { ContratoId = contrato, Tipo = TipoVistoria.PERIODICA });
        await Assert.ThrowsAsync<RegraNegocioException>(() => v.CancelarAsync(admin, periodica, " "));
        await v.CancelarAsync(admin, periodica, "Inquilino viajando");
        Assert.Equal(StatusVistoria.CANCELADA, (await v.ObterAsync(admin, periodica)).Vistoria.Status);
        Assert.DoesNotContain(await v.ListarAsync(admin, soAgendadas: true), x => x.Id == periodica);
    }
}
