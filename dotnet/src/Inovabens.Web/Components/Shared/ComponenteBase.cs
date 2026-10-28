using Inovabens.Core.Comum;
using Microsoft.AspNetCore.Components;

namespace Inovabens.Web.Components.Shared;

/// <summary>
/// Base das telas: usuário logado (vindo do MainLayout) e execução de ações com tratamento de erro
/// padronizado — mensagens de regra de negócio vão para a tela; erros inesperados vão também para o log.
/// </summary>
public abstract class ComponenteBase : ComponentBase
{
    [Inject] private ILoggerFactory LogFactory { get; set; } = default!;

    [CascadingParameter] protected UsuarioAtual Usuario { get; set; } = default!;

    protected string? Erro { get; set; }
    protected string? Sucesso { get; set; }
    protected bool Ocupado { get; private set; }

    /// <summary>Executa a ação; devolve false (e preenche <see cref="Erro"/>) se falhar.</summary>
    protected async Task<bool> Executar(Func<Task> acao)
    {
        Erro = null;
        Sucesso = null;
        Ocupado = true;
        try
        {
            await acao();
            return true;
        }
        catch (Exception ex)
        {
            Erro = Erros.Mensagem(ex);
            if (Erros.Inesperado(ex)) LogFactory.CreateLogger(GetType()).LogError(ex, "Falha ao executar ação");
            return false;
        }
        finally
        {
            Ocupado = false;
        }
    }
}
