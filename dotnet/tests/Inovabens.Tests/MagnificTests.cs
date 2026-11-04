using System.Net;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Inovabens.Tests;

/// <summary>API do Magnific simulada: responde com o que o teste programar e guarda o que recebeu.</summary>
internal sealed class MagnificFalso : HttpMessageHandler
{
    public readonly List<(HttpRequestMessage Req, string Corpo)> Recebidas = [];
    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Recebidas.Add((req, req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct)));
        return Responder(req);
    }

    public static HttpResponseMessage Tarefa(string id, string status, params string[] gerados) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { data = new { task_id = id, status, generated = gerados } }), Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Arquivo(byte[] dados) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(dados) };
}

internal static class Imagens
{
    public static byte[] Gerar(int w, int h, SKEncodedImageFormat formato = SKEncodedImageFormat.Jpeg)
    {
        using var b = new SKBitmap(w, h);
        using (var c = new SKCanvas(b)) c.Clear(SKColors.SteelBlue);
        using var img = SKImage.FromBitmap(b);
        return img.Encode(formato, 90).ToArray();
    }

    public static (int W, int H) Tamanho(byte[] dados)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(dados));
        return (codec.Info.Width, codec.Info.Height);
    }
}

public class MagnificClienteTests
{
    private static (MagnificCliente, MagnificFalso) Cliente()
    {
        var falso = new MagnificFalso();
        var op = InovabensOpcoes.De(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "Host=x;Database=y", ["MAGNIFIC_API_KEY"] = "chave-teste",
        }).Build(), producao: false);
        return (new MagnificCliente(new HttpClient(falso), op), falso);
    }

    [Fact]
    public async Task Cria_tarefa_com_chave_e_parametros_de_foto()
    {
        var (cliente, falso) = Cliente();
        falso.Responder = _ => MagnificFalso.Tarefa("t-1", "CREATED");

        var t = await cliente.CriarAsync([1, 2, 3]);

        Assert.Equal(new TarefaMagnific("t-1", SituacaoTarefaMagnific.EmAndamento, null), t);
        var (req, corpo) = falso.Recebidas.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal(MagnificCliente.Endpoint, req.RequestUri!.ToString());
        Assert.Equal("chave-teste", req.Headers.GetValues("x-magnific-api-key").Single());
        using var json = JsonDocument.Parse(corpo);
        Assert.Equal("AQID", json.RootElement.GetProperty("image").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("scale_factor").GetInt32());
        Assert.Equal("photo", json.RootElement.GetProperty("flavor").GetString());
    }

    [Fact]
    public async Task Consulta_le_resultado_e_distingue_erro_definitivo_de_temporario()
    {
        var (cliente, falso) = Cliente();
        falso.Responder = _ => MagnificFalso.Tarefa("t-1", "COMPLETED", "https://cdn.exemplo.com/r.png");
        Assert.Equal(new TarefaMagnific("t-1", SituacaoTarefaMagnific.Concluida, "https://cdn.exemplo.com/r.png"), await cliente.ConsultarAsync("t-1"));
        Assert.EndsWith("/image-upscaler-precision-v2/t-1", falso.Recebidas.Last().Req.RequestUri!.ToString());

        falso.Responder = _ => MagnificFalso.Tarefa("t-1", "FAILED");
        Assert.Equal(SituacaoTarefaMagnific.Falhou, (await cliente.ConsultarAsync("t-1")).Situacao);

        falso.Responder = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("imagem inválida") };
        var e = await Assert.ThrowsAsync<MagnificRecusouException>(() => cliente.CriarAsync([1]));
        Assert.Contains("imagem inválida", e.Message);

        falso.Responder = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        await Assert.ThrowsAsync<HttpRequestException>(() => cliente.CriarAsync([1]));
        falso.Responder = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        await Assert.ThrowsAsync<HttpRequestException>(() => cliente.CriarAsync([1]));
    }

    [Fact]
    public async Task Resultado_so_e_baixado_por_https() =>
        await Assert.ThrowsAsync<MagnificRecusouException>(() => Cliente().Item1.BaixarResultadoAsync("http://cdn.exemplo.com/r.png"));
}

public class ImagemFotoTests
{
    [Fact]
    public void Foto_grande_e_reduzida_para_o_Magnific()
    {
        var jpeg = ImagemFoto.PrepararEntrada(Imagens.Gerar(4000, 3000))!;
        Assert.Equal((2048, 1536), Imagens.Tamanho(jpeg));
    }

    [Fact]
    public void Foto_pequena_nao_e_ampliada_e_png_vira_jpeg()
    {
        var jpeg = ImagemFoto.PrepararEntrada(Imagens.Gerar(800, 600, SKEncodedImageFormat.Png))!;
        Assert.Equal((800, 600), Imagens.Tamanho(jpeg));
        Assert.Equal([0xFF, 0xD8], jpeg[..2]); // assinatura JPEG
    }

    [Fact]
    public void Arquivo_que_nao_e_imagem_e_recusado()
    {
        Assert.Null(ImagemFoto.PrepararEntrada("não é imagem"u8.ToArray()));
        Assert.True(ImagemFoto.Suportado("image/jpeg"));
        Assert.False(ImagemFoto.Suportado("image/heic"));
    }
}

/// <summary>
/// Fluxo completo com banco real, Google Drive em memória (<see cref="ArmazenamentoMemoria"/>) e o Magnific simulado:
/// pastas do imóvel, envio por subpasta, troca da foto, restauração e falha.
/// </summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class MelhoriaFotosIntegracaoTests : IAsyncLifetime
{
    private ServiceProvider _sp = default!;
    private IServiceScope _escopo = default!;
    private readonly MagnificFalso _magnific = new();
    private readonly ArmazenamentoMemoria _drive = new();

    public Task InitializeAsync()
    {
        var op = InovabensOpcoes.De(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = BancoFixture.Url, ["MAGNIFIC_API_KEY"] = "chave-teste",
        }).Build(), producao: false);
        var servicos = new ServiceCollection().AddLogging().AddInovabensCore(op);
        servicos.AddSingleton(new MagnificCliente(new HttpClient(_magnific), op)); // substitui o cliente real
        servicos.AddSingleton<IArmazenamento>(_drive); // substitui o Google Drive
        _sp = servicos.BuildServiceProvider();
        _escopo = _sp.CreateScope();
        _escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _escopo.Dispose();
        await _sp.DisposeAsync();
    }

    [Fact]
    public async Task Pastas_do_imovel_no_drive_criadas_renomeadas_e_arquivos_por_subpasta()
    {
        var admin = (await S<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var imoveis = S<ImoveisService>();
        var nome = $"Residencial Teste {Guid.NewGuid():N}";

        // cadastrar cria a pasta principal com as seis subpastas
        var id = await imoveis.CriarAsync(admin, new ImovelForm { NomeDrive = $"  {nome}  ", Titulo = "Casa com pastas", Tipo = "casa" });
        var detalhe = await imoveis.DetalheAsync(admin, id);
        var raiz = detalhe.Dados.DrivePastaId!;
        Assert.Equal(nome, detalhe.Dados.NomeDrive);
        Assert.Equal(nome, _drive.Itens[raiz].Nome);
        Assert.Equal(ArmazenamentoMemoria.Raiz, _drive.Itens[raiz].Pai);
        Assert.Equal(SubpastasDrive.Todas.Select(s => s.Nome), _drive.Filhos(raiz).Select(i => i.Nome).Order());
        Assert.NotNull(detalhe.LinkDrive);

        // mesmo nome em outro imóvel: recusado (sem diferenciar maiúsculas)
        var e = await Assert.ThrowsAsync<RegraNegocioException>(() =>
            imoveis.CriarAsync(admin, new ImovelForm { NomeDrive = nome.ToUpperInvariant(), Titulo = "Outra casa", Tipo = "casa" }));
        Assert.Contains(nameof(ImovelForm.NomeDrive), e.Erros.Keys);

        // imagem na pasta de matrícula é documento; na 04 é foto da galeria
        using (var scan = new MemoryStream(Imagens.Gerar(100, 100)))
            await imoveis.EnviarMidiaAsync(admin, id, scan, "matricula.jpg", "image/jpeg", SubpastasDrive.Matricula);
        using (var foto = new MemoryStream(Imagens.Gerar(100, 100)))
            await imoveis.EnviarMidiaAsync(admin, id, foto, "sala.jpg", "image/jpeg", SubpastasDrive.FotosVideos);
        var midias = await imoveis.MidiasAsync(admin, id);
        var doc = midias.Single(m => m.Midia.NomeOriginal == "matricula.jpg");
        var galeria = midias.Single(m => m.Midia.NomeOriginal == "sala.jpg");
        Assert.Equal((TipoMidia.DOCUMENTO, false, "01 - Matrícula"), (doc.Midia.Tipo, doc.Midia.Publica, doc.NomeSubpasta));
        // sem marcar "pública", a foto fica só no Drive
        Assert.Equal((TipoMidia.FOTO, false, true), (galeria.Midia.Tipo, galeria.Midia.Publica, galeria.Midia.Capa));
        Assert.Equal("01 - Matrícula", _drive.Itens[_drive.Itens[doc.Midia.ArquivoChave].Pai!].Nome);
        Assert.Equal("04 - Fotos e Vídeos", _drive.Itens[_drive.Itens[galeria.Midia.ArquivoChave].Pai!].Nome);

        // o arquivo é servido pelo sistema; HTML vira download
        var servido = await imoveis.ArquivoMidiaAsync(admin, galeria.Midia.Id, galeria.Midia.ArquivoChave);
        Assert.Equal(("image/jpeg", false), (servido.ContentType, servido.Download));
        await Assert.ThrowsAsync<NaoEncontradoException>(() => imoveis.ArquivoMidiaAsync(admin, galeria.Midia.Id, doc.Midia.ArquivoChave));
        using (var html = new MemoryStream("<script>alert(1)</script>"u8.ToArray()))
            await imoveis.EnviarMidiaAsync(admin, id, html, "pagina.html", "text/html", SubpastasDrive.Documentos);
        var pagina = (await imoveis.MidiasAsync(admin, id)).Single(m => m.Midia.NomeOriginal == "pagina.html");
        var baixado = await imoveis.ArquivoMidiaAsync(admin, pagina.Midia.Id, pagina.Midia.ArquivoChave);
        Assert.Equal(("application/octet-stream", true), (baixado.ContentType, baixado.Download));

        // renomear no cadastro renomeia a pasta no Drive
        var form = ImovelForm.De(detalhe.Dados);
        form.NomeDrive = nome + " (renomeado)";
        await imoveis.AtualizarAsync(admin, id, form);
        Assert.Equal(nome + " (renomeado)", _drive.Itens[raiz].Nome);

        // subpasta apagada no Drive é recriada no próximo envio
        var protocolos = _drive.Filhos(raiz).Single(i => i.Nome == "06 - Protocolos");
        await _drive.RemoverAsync(protocolos.Id);
        using (var pdf = new MemoryStream("%PDF-1.4"u8.ToArray()))
            await imoveis.EnviarMidiaAsync(admin, id, pdf, "protocolo.pdf", "application/pdf", SubpastasDrive.Protocolos);
        Assert.Single(_drive.Filhos(raiz), i => i.Nome == "06 - Protocolos");

        // excluir a mídia manda o arquivo para a lixeira do Drive
        await imoveis.RemoverMidiaAsync(admin, doc.Midia.Id);
        Assert.True(_drive.Itens[doc.Midia.ArquivoChave].NaLixeira);
    }

    private T S<T>() where T : notnull => _escopo.ServiceProvider.GetRequiredService<T>();

    [Fact]
    public async Task Foto_enviada_e_melhorada_restaurada_e_falha_registrada()
    {
        var admin = (await S<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var imoveis = S<ImoveisService>();
        var melhoria = S<MelhoriaFotosService>();
        var storage = S<IArmazenamento>();

        var id = await imoveis.CriarAsync(admin, new ImovelForm { NomeDrive = Guid.NewGuid().ToString(), Titulo = "Casa com fotos", Tipo = "casa" });
        using (var foto = new MemoryStream(Imagens.Gerar(3000, 2000)))
            await imoveis.EnviarMidiaAsync(admin, id, foto, "fachada.jpg", "image/jpeg", SubpastasDrive.FotosVideos);
        using (var doc = new MemoryStream("%PDF-1.4"u8.ToArray()))
            await imoveis.EnviarMidiaAsync(admin, id, doc, "escritura.pdf", "application/pdf", SubpastasDrive.Documentos);
        async Task<ImovelMidia> Foto() => (await imoveis.MidiasAsync(admin, id)).Single(m => m.Midia.Tipo == TipoMidia.FOTO).Midia;

        var m = await Foto();
        Assert.Equal(StatusMelhoria.PENDENTE, m.MelhoriaStatus);
        Assert.Null((await imoveis.MidiasAsync(admin, id)).Single(x => x.Midia.Tipo == TipoMidia.DOCUMENTO).Midia.MelhoriaStatus);
        var original = m.ArquivoChave;

        // 1ª rodada: a foto vai reduzida para o Magnific
        var tarefa = $"t-{Guid.NewGuid():N}";
        _magnific.Responder = _ => MagnificFalso.Tarefa(tarefa, "CREATED");
        Assert.Equal(0, await melhoria.ProcessarAsync());
        var envio = _magnific.Recebidas.Single(r => r.Req.Method == HttpMethod.Post);
        var enviada = Convert.FromBase64String(JsonDocument.Parse(envio.Corpo).RootElement.GetProperty("image").GetString()!);
        Assert.Equal((2048, 1365), Imagens.Tamanho(enviada));
        m = await Foto();
        Assert.Equal((StatusMelhoria.PROCESSANDO, tarefa), (m.MelhoriaStatus, m.MelhoriaTarefa));

        // ainda processando
        _magnific.Responder = _ => MagnificFalso.Tarefa(tarefa, "IN_PROGRESS");
        Assert.Equal(0, await melhoria.ProcessarAsync());
        Assert.Equal(StatusMelhoria.PROCESSANDO, (await Foto()).MelhoriaStatus);

        // pronta: a foto exibida passa a ser a melhorada (JPEG), e a original fica guardada
        var gerada = Imagens.Gerar(4096, 2730, SKEncodedImageFormat.Png);
        _magnific.Responder = r => r.RequestUri!.Host == "cdn.exemplo.com"
            ? MagnificFalso.Arquivo(gerada)
            : MagnificFalso.Tarefa(tarefa, "COMPLETED", "https://cdn.exemplo.com/resultado.png");
        Assert.Equal(1, await melhoria.ProcessarAsync());
        m = await Foto();
        Assert.Equal(StatusMelhoria.CONCLUIDA, m.MelhoriaStatus);
        Assert.Equal(original, m.ArquivoOriginalChave);
        // no Drive: mesma pasta da original, com " (melhorada)" no nome
        Assert.Equal("fachada (melhorada).jpg", _drive.Itens[m.ArquivoChave].Nome);
        Assert.Equal(_drive.Itens[original].Pai, _drive.Itens[m.ArquivoChave].Pai);
        var melhorada = await storage.BaixarAsync(m.ArquivoChave);
        Assert.Equal((4096, 2730), Imagens.Tamanho(melhorada!.Dados));
        await Assert.ThrowsAsync<RegraNegocioException>(() => melhoria.SolicitarAsync(admin, m.Id));

        // restaurar: volta a original e apaga a melhorada
        await melhoria.RestaurarOriginalAsync(admin, m.Id);
        var restaurada = await Foto();
        Assert.Equal((original, (string?)null, (StatusMelhoria?)null), (restaurada.ArquivoChave, restaurada.ArquivoOriginalChave, restaurada.MelhoriaStatus));
        Assert.Null(await storage.BaixarAsync(m.ArquivoChave));

        // pedir de novo e o Magnific recusar: fica como falha, com o motivo
        await melhoria.SolicitarAsync(admin, m.Id);
        _magnific.Responder = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("créditos insuficientes") };
        await melhoria.ProcessarAsync();
        var falha = await Foto();
        Assert.Equal(StatusMelhoria.FALHA, falha.MelhoriaStatus);
        Assert.Contains("créditos insuficientes", falha.MelhoriaErro);
        Assert.Equal(original, falha.ArquivoChave);

        // excluir a foto apaga o arquivo do armazenamento
        await imoveis.RemoverMidiaAsync(admin, m.Id);
        Assert.Null(await storage.BaixarAsync(original));
    }
}
