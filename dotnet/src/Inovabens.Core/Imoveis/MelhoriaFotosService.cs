using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Imoveis;

/// <summary>
/// Melhoria automática das fotos pelo Magnific. O envio da foto não espera: ela entra como PENDENTE e a rotina
/// <c>MelhoriaFotosJob</c> (a cada minuto) cria a tarefa no Magnific, acompanha e, quando pronta, troca o arquivo
/// exibido pela versão melhorada, guardando o original para poder restaurá-lo.
/// </summary>
public sealed class MelhoriaFotosService(
    IDbContextFactory<InovabensDb> fabrica, IArmazenamento storage, MagnificCliente magnific, AuditService audit,
    ILogger<MelhoriaFotosService> log)
{
    /// <summary>Tarefas paradas há mais tempo que isso são dadas como falha.</summary>
    public static readonly TimeSpan TempoMaximo = TimeSpan.FromMinutes(30);
    private const int PorRodada = 5;

    public bool Habilitada => magnific.Configurado && storage.Habilitado;

    /* ------------------------------ Usuário ------------------------------ */

    /// <summary>Pede (de novo) a melhoria de uma foto: fotos antigas ou que falharam.</summary>
    public async Task SolicitarAsync(UsuarioAtual u, Guid midiaId)
    {
        u.Exigir(P.ImoveisEditar);
        if (!Habilitada) throw new ServicoIndisponivelException("Melhoria de fotos desligada (MAGNIFIC_API_KEY não configurada).");
        await using var db = await fabrica.CreateDbContextAsync();
        var m = await MidiaComAcesso(db, u, midiaId);
        if (m.Tipo != TipoMidia.FOTO) throw new RegraNegocioException("midia", "Só fotos podem ser melhoradas.");
        if (m.MelhoriaStatus is StatusMelhoria.PENDENTE or StatusMelhoria.PROCESSANDO) return;
        if (m.MelhoriaStatus == StatusMelhoria.CONCLUIDA) throw new RegraNegocioException("midia", "Esta foto já foi melhorada.");
        Marcar(m, StatusMelhoria.PENDENTE);
        audit.Registrar(db, u.Id, "imovel_midias", m.Id, "MELHORIA_SOLICITADA");
        await db.SaveChangesAsync();
    }

    /// <summary>Volta a exibir a foto como foi enviada e apaga a versão melhorada.</summary>
    public async Task RestaurarOriginalAsync(UsuarioAtual u, Guid midiaId)
    {
        u.Exigir(P.ImoveisEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var m = await MidiaComAcesso(db, u, midiaId);
        if (m.ArquivoOriginalChave is not { } original) throw new RegraNegocioException("midia", "Esta foto não tem versão melhorada.");
        var melhorada = m.ArquivoChave;
        m.ArquivoChave = original;
        m.ArquivoOriginalChave = null;
        m.MelhoriaStatus = null;
        m.MelhoriaTarefa = m.MelhoriaErro = null;
        m.MelhoriaAtualizadaEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "imovel_midias", m.Id, "MELHORIA_DESFEITA");
        await db.SaveChangesAsync();
        await storage.RemoverAsync(melhorada);
    }

    /* ------------------------------- Rotina ------------------------------- */

    /// <summary>Uma rodada: envia as pendentes e acompanha as que estão no Magnific. Devolve quantas concluíram.</summary>
    public async Task<int> ProcessarAsync(CancellationToken ct = default)
    {
        if (!Habilitada) return 0;
        await using var db = await fabrica.CreateDbContextAsync();
        var abertas = await db.ImovelMidias
            .Where(m => m.MelhoriaStatus == StatusMelhoria.PENDENTE || m.MelhoriaStatus == StatusMelhoria.PROCESSANDO)
            .OrderBy(m => m.MelhoriaAtualizadaEm)
            .Take(PorRodada)
            .ToListAsync(ct);

        var concluidas = 0;
        foreach (var m in abertas)
        {
            string? chaveNova = null;
            try
            {
                if (DateTime.UtcNow - m.MelhoriaAtualizadaEm > TempoMaximo)
                    Falhar(m, "Tempo esgotado no Magnific.");
                else if (m.MelhoriaStatus == StatusMelhoria.PENDENTE)
                    await Enviar(m, ct);
                else if ((chaveNova = await Acompanhar(m, ct)) is not null)
                    concluidas++;
            }
            catch (MagnificRecusouException e)
            {
                Falhar(m, e.Message);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or Google.GoogleApiException &&!ct.IsCancellationRequested)
            {
                // falha temporária (rede, 429, 5xx): fica como está e tenta de novo na próxima rodada
                log.LogWarning("Magnific indisponível para a foto {Midia}: {Erro}", m.Id, e.Message);
            }
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // a foto foi excluída enquanto era processada: descarta o que foi gerado
                db.Entry(m).State = EntityState.Detached;
                if (chaveNova is not null) { await storage.RemoverAsync(chaveNova); concluidas--; }
            }
        }
        return concluidas;
    }

    private async Task Enviar(ImovelMidia m, CancellationToken ct)
    {
        var dados = (await storage.BaixarAsync(m.ArquivoChave, ct))?.Dados ?? throw new HttpRequestException("Foto não encontrada no Google Drive.");
        var entrada = ImagemFoto.PrepararEntrada(dados) ?? throw new MagnificRecusouException("Formato de imagem não suportado.");
        var tarefa = await magnific.CriarAsync(entrada, ct);
        m.MelhoriaTarefa = tarefa.Id;
        Marcar(m, StatusMelhoria.PROCESSANDO);
    }

    /// <summary>Consulta a tarefa; se pronta, grava a foto melhorada e devolve a chave dela no armazenamento.</summary>
    private async Task<string?> Acompanhar(ImovelMidia m, CancellationToken ct)
    {
        if (m.MelhoriaTarefa is not { } id) { Marcar(m, StatusMelhoria.PENDENTE); return null; }
        var tarefa = await magnific.ConsultarAsync(id, ct);
        if (tarefa.Situacao == SituacaoTarefaMagnific.EmAndamento) return null;
        if (tarefa.Situacao == SituacaoTarefaMagnific.Falhou || tarefa.UrlResultado is null)
        {
            Falhar(m, "O Magnific não conseguiu processar a foto.");
            return null;
        }

        var resultado = ImagemFoto.PrepararResultado(await magnific.BaixarResultadoAsync(tarefa.UrlResultado, ct))
            ?? throw new MagnificRecusouException("Imagem gerada inválida.");
        // a versão melhorada fica na mesma pasta do Drive que a original, com " (melhorada)" no nome
        var pasta = await storage.PastaDoArquivoAsync(m.ArquivoChave, ct)
            ?? throw new MagnificRecusouException("A foto original não está mais no Google Drive.");
        var nome = $"{Path.GetFileNameWithoutExtension(m.NomeOriginal ?? "foto")} (melhorada).jpg";
        string chave;
        using (var conteudo = new MemoryStream(resultado))
            chave = await storage.EnviarAsync(pasta, nome, conteudo, "image/jpeg", ct);

        m.ArquivoOriginalChave ??= m.ArquivoChave;
        m.ArquivoChave = chave;
        m.MelhoriaTarefa = null;
        Marcar(m, StatusMelhoria.CONCLUIDA);
        log.LogInformation("Foto {Midia} melhorada pelo Magnific", m.Id);
        return chave;
    }

    private static void Marcar(ImovelMidia m, StatusMelhoria status)
    {
        m.MelhoriaStatus = status;
        m.MelhoriaErro = null;
        m.MelhoriaAtualizadaEm = DateTime.UtcNow;
    }

    private void Falhar(ImovelMidia m, string motivo)
    {
        log.LogWarning("Melhoria da foto {Midia} falhou: {Motivo}", m.Id, motivo);
        m.MelhoriaStatus = StatusMelhoria.FALHA;
        m.MelhoriaErro = motivo.Length > 500 ? motivo[..500] : motivo;
        m.MelhoriaTarefa = null;
        m.MelhoriaAtualizadaEm = DateTime.UtcNow;
    }

    private static async Task<ImovelMidia> MidiaComAcesso(InovabensDb db, UsuarioAtual u, Guid midiaId)
    {
        var m = await db.ImovelMidias.SingleOrDefaultAsync(x => x.Id == midiaId) ?? throw new NaoEncontradoException("Mídia não encontrada.");
        await Acesso.ImovelComAcesso(db, u, m.ImovelId, rastrear: false);
        return m;
    }
}
