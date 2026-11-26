using Microsoft.AspNetCore.Components;

namespace Inovabens.Web.Components.Site;

/// <summary>
/// Endereço com impressão digital (ex.: site-previa.abc123.css, cache de 1 ano) para os arquivos estáticos do site público.
/// As páginas do site são minimal API (RazorComponentResult), onde o <c>@Assets</c> do Blazor não resolve e devolve o nome
/// sem impressão digital — servido com "no-cache", o navegador revalidava o CSS a cada visita. Aqui a coleção é lida
/// das rotas do Blazor (MapRazorComponents depois de MapStaticAssets), uma vez só.
/// </summary>
public sealed class AtivosSite(EndpointDataSource rotas)
{
    private ResourceAssetCollection? colecao;

    public string this[string caminho]
    {
        get
        {
            colecao ??= rotas.Endpoints.Select(e => e.Metadata.GetMetadata<ResourceAssetCollection>()).FirstOrDefault(c => c is not null)
                ?? ResourceAssetCollection.Empty;
            return colecao[caminho];
        }
    }
}
