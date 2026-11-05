using Inovabens.Core.Admin;
using Inovabens.Core.Agenda;
using Inovabens.Core.Comum;
using Inovabens.Core.Crm;
using Inovabens.Core.Dados;
using Inovabens.Core.Financeiro;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Memorandos;
using Inovabens.Core.Painel;
using Inovabens.Core.Pessoas;
using Inovabens.Core.Relatorios;
using Inovabens.Core.Servicos;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Core;

public static class Registro
{
    /// <summary>Registra banco, serviços de infraestrutura e módulos de negócio.</summary>
    public static IServiceCollection AddInovabensCore(this IServiceCollection services, InovabensOpcoes op)
    {
        var conexao = InovabensDb.NormalizarConexao(op.DatabaseUrl);
        // Blazor Server: um DbContext por operação (o circuito vive muito; um contexto único acumularia estado).
        services.AddDbContextFactory<InovabensDb>(o => InovabensDb.ConfigurarNpgsql(o, conexao));
        services.AddSingleton(op);
        services.AddSingleton<Migrador>();
        services.AddSingleton<DadosIniciais>();
        services.AddSingleton<IArmazenamento, GoogleDriveArmazenamento>();
        services.AddScoped<PastasDriveService>();
        services.AddSingleton<IArmazenamentoPublico, R2ArmazenamentoPublico>();
        services.AddScoped<PublicacaoFotosService>();
        services.AddScoped<ContextoConexao>();
        services.AddScoped<AuditService>();
        services.AddScoped<AuthService>();
        services.AddScoped<OpcoesService>();
        services.AddScoped<ImoveisService>();
        services.AddScoped<ImportacaoImobibrasilService>();
        services.AddSingleton<GeocodificacaoService>();
        services.AddSingleton<CepService>();
        services.AddSingleton(sp => new MagnificCliente(new HttpClient { Timeout = TimeSpan.FromMinutes(2) }, op));
        services.AddScoped<MelhoriaFotosService>();
        services.AddScoped<AquisicaoService>();
        services.AddScoped<DespesasService>();
        services.AddScoped<ReceitasService>();
        services.AddScoped<IndicesService>();
        services.AddScoped<RetornoService>();
        services.AddScoped<PessoasService>();
        services.AddScoped<MemorandosService>();
        services.AddScoped<AlertasService>();
        services.AddScoped<AgendaService>();
        services.AddSingleton<GoogleService>(); // guarda tokens de acesso em cache e faz chamadas em segundo plano
        services.AddScoped<CrmService>();
        services.AddScoped<PainelService>();
        services.AddScoped<UsuariosService>();
        services.AddScoped<PerfisService>();
        services.AddScoped<CadastrosService>();
        services.AddScoped<AuditoriaService>();
        services.AddScoped<RelatoriosService>();
        return services;
    }
}
