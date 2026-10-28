using System.Text;
using Inovabens.Core.Dominio;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Npgsql.NameTranslation;

namespace Inovabens.Core.Dados;

/// <summary>
/// Contexto do EF Core sobre o banco EXISTENTE. Não usamos migrations do EF: o esquema
/// (tabelas, CHECKs, triggers, índices) vem dos scripts SQL de Dados/Migracoes, aplicados pelo <see cref="Migrador"/>.
/// </summary>
public class InovabensDb(DbContextOptions<InovabensDb> options) : DbContext(options), IDataProtectionKeyContext
{
    /// <summary>Chaves que assinam o cookie de sessão e os tokens antifalsificação, compartilhadas entre as instâncias.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<Imovel> Imoveis => Set<Imovel>();
    public DbSet<ImovelCaracteristica> ImovelCaracteristicas => Set<ImovelCaracteristica>();
    public DbSet<ImovelMidia> ImovelMidias => Set<ImovelMidia>();
    public DbSet<ImovelProprietario> ImovelProprietarios => Set<ImovelProprietario>();
    public DbSet<ImovelEnvolvido> ImovelEnvolvidos => Set<ImovelEnvolvido>();
    public DbSet<Regularizacao> Regularizacoes => Set<Regularizacao>();
    public DbSet<Pessoa> Pessoas => Set<Pessoa>();
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Perfil> Perfis => Set<Perfil>();
    public DbSet<Permissao> Permissoes => Set<Permissao>();
    public DbSet<PerfilPermissao> PerfilPermissoes => Set<PerfilPermissao>();
    public DbSet<AuditLog> AuditLog => Set<AuditLog>();

    public DbSet<Aquisicao> Aquisicoes => Set<Aquisicao>();
    public DbSet<AquisicaoItem> AquisicaoItens => Set<AquisicaoItem>();
    public DbSet<DespesaCategoria> DespesaCategorias => Set<DespesaCategoria>();
    public DbSet<Despesa> Despesas => Set<Despesa>();
    public DbSet<DespesaLancamento> DespesaLancamentos => Set<DespesaLancamento>();
    public DbSet<Receita> Receitas => Set<Receita>();
    public DbSet<IndiceSerie> IndicesSerie => Set<IndiceSerie>();
    public DbSet<Alerta> Alertas => Set<Alerta>();

    public DbSet<PessoaPapel> PessoaPapeis => Set<PessoaPapel>();
    public DbSet<Memorando> Memorandos => Set<Memorando>();
    public DbSet<Compromisso> Compromissos => Set<Compromisso>();
    public DbSet<CompromissoParticipante> CompromissoParticipantes => Set<CompromissoParticipante>();
    public DbSet<GoogleConta> GoogleContas => Set<GoogleConta>();
    public DbSet<FunilEstagio> FunilEstagios => Set<FunilEstagio>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<LeadHistoricoEstagio> LeadHistoricoEstagios => Set<LeadHistoricoEstagio>();
    public DbSet<LeadInteracao> LeadInteracoes => Set<LeadInteracao>();
    public DbSet<LeadImovelInteresse> LeadImoveisInteresse => Set<LeadImovelInteresse>();

    /// <summary>Configura o Npgsql: conexão e tipos enum do PostgreSQL (rótulos iguais aos nomes C#).</summary>
    public static void ConfigurarNpgsql(DbContextOptionsBuilder o, string conexao) =>
        o.UseNpgsql(conexao, n =>
        {
            var semTraducao = new NpgsqlNullNameTranslator();
            n.MapEnum<FinalidadeImovel>("finalidade_imovel", nameTranslator: semTraducao);
            n.MapEnum<SituacaoImovel>("situacao_imovel", nameTranslator: semTraducao);
            n.MapEnum<PapelPessoa>("papel_pessoa", nameTranslator: semTraducao);
            n.MapEnum<TipoMidia>("tipo_midia", nameTranslator: semTraducao);
            n.MapEnum<TipoRegularizacao>("tipo_regularizacao", nameTranslator: semTraducao);
            n.MapEnum<StatusRegularizacao>("status_regularizacao", nameTranslator: semTraducao);
            n.MapEnum<TipoItemAquisicao>("tipo_item_aquisicao", nameTranslator: semTraducao);
            n.MapEnum<Recorrencia>("recorrencia", nameTranslator: semTraducao);
            n.MapEnum<StatusLancamento>("status_lancamento", nameTranslator: semTraducao);
            n.MapEnum<TipoReceita>("tipo_receita", nameTranslator: semTraducao);
            n.MapEnum<CodigoIndice>("codigo_indice", nameTranslator: semTraducao);
            n.MapEnum<TipoPessoa>("tipo_pessoa", nameTranslator: semTraducao);
            n.MapEnum<TipoMemorando>("tipo_memorando", nameTranslator: semTraducao);
            n.MapEnum<TipoCompromisso>("tipo_compromisso", nameTranslator: semTraducao);
            n.MapEnum<StatusCompromisso>("status_compromisso", nameTranslator: semTraducao);
            n.MapEnum<TipoAlerta>("tipo_alerta", nameTranslator: semTraducao);
            n.MapEnum<FinalidadeLead>("finalidade_lead", nameTranslator: semTraducao);
            n.MapEnum<Funil>("funil", nameTranslator: semTraducao);
            n.MapEnum<TipoEstagio>("tipo_estagio", nameTranslator: semTraducao);
            n.MapEnum<CanalInteracao>("canal_interacao", nameTranslator: semTraducao);
        });

    /// <summary>Aceita tanto "Host=...;Database=..." quanto a URL do Node (postgres://user:senha@host:5432/banco).</summary>
    public static string NormalizarConexao(string valor)
    {
        if (!valor.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !valor.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return valor;
        var uri = new Uri(valor);
        var credenciais = uri.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(credenciais[0]),
            Password = credenciais.Length > 1 ? Uri.UnescapeDataString(credenciais[1]) : null,
        };
        foreach (var par in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = par.Split('=', 2);
            if (kv[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase) && kv.Length == 2 &&
                Enum.TryParse<SslMode>(kv[1], ignoreCase: true, out var ssl))
                b.SslMode = ssl;
        }
        return b.ConnectionString;
    }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Imovel>(e =>
        {
            e.Property(x => x.Atributos).HasColumnType("jsonb");
            e.HasOne(x => x.Corretor).WithMany().HasForeignKey(x => x.CorretorResponsavelId);
            e.HasMany(x => x.Caracteristicas).WithOne().HasForeignKey(x => x.ImovelId);
            e.HasMany(x => x.Midias).WithOne().HasForeignKey(x => x.ImovelId);
            e.HasMany(x => x.Proprietarios).WithOne().HasForeignKey(x => x.ImovelId);
            e.HasMany(x => x.Envolvidos).WithOne().HasForeignKey(x => x.ImovelId);
        });
        mb.Entity<ImovelProprietario>().HasOne(x => x.Pessoa).WithMany().HasForeignKey(x => x.PessoaId);
        mb.Entity<ImovelEnvolvido>().HasOne(x => x.Pessoa).WithMany().HasForeignKey(x => x.PessoaId);
        mb.Entity<Regularizacao>().HasOne(x => x.Profissional).WithMany().HasForeignKey(x => x.ProfissionalId);
        mb.Entity<Usuario>().HasOne(x => x.Perfil).WithMany().HasForeignKey(x => x.PerfilId);
        mb.Entity<PerfilPermissao>(e =>
        {
            e.HasKey(x => new { x.PerfilId, x.PermissaoId });
            e.HasOne(x => x.Permissao).WithMany().HasForeignKey(x => x.PermissaoId);
        });
        mb.Entity<AuditLog>(e =>
        {
            e.Property(x => x.ValoresAntes).HasColumnType("jsonb");
            e.Property(x => x.ValoresDepois).HasColumnType("jsonb");
        });

        // Colunas GERADAS pelo banco: o EF nunca as envia, só lê de volta após gravar.
        mb.Entity<Aquisicao>().Property(x => x.TotalAquisicao)
            .HasComputedColumnSql("valor_pago + impostos + taxas + despesas_diversas", stored: true);
        mb.Entity<Receita>().Property(x => x.ValorLiquido)
            .HasComputedColumnSql("valor_bruto - custos_venda - tributos", stored: true);
        mb.Entity<AquisicaoItem>().HasOne(x => x.Aquisicao).WithMany().HasForeignKey(x => x.AquisicaoId);
        mb.Entity<Despesa>().HasOne(x => x.Categoria).WithMany().HasForeignKey(x => x.CategoriaId);
        mb.Entity<DespesaLancamento>().HasOne(x => x.Despesa).WithMany().HasForeignKey(x => x.DespesaId);

        mb.Entity<Pessoa>().HasMany(x => x.Papeis).WithOne().HasForeignKey(x => x.PessoaId);
        mb.Entity<PessoaPapel>().HasKey(x => new { x.PessoaId, x.Papel });
        mb.Entity<Memorando>().HasOne(x => x.Autor).WithMany().HasForeignKey(x => x.AutorId);
        mb.Entity<Compromisso>(e =>
        {
            e.HasOne(x => x.Imovel).WithMany().HasForeignKey(x => x.ImovelId);
            e.HasOne(x => x.Responsavel).WithMany().HasForeignKey(x => x.ResponsavelId);
        });
        mb.Entity<Lead>(e =>
        {
            e.HasOne(x => x.Pessoa).WithMany().HasForeignKey(x => x.PessoaId);
            e.HasOne(x => x.Estagio).WithMany().HasForeignKey(x => x.EstagioId);
            e.HasOne(x => x.Responsavel).WithMany().HasForeignKey(x => x.ResponsavelId);
        });
        mb.Entity<LeadImovelInteresse>(e =>
        {
            e.HasKey(x => new { x.LeadId, x.ImovelId });
            e.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId);
        });
        // FKs das tabelas filhas: o EF ordena os INSERTs (pai antes do filho) no mesmo SaveChanges
        mb.Entity<CompromissoParticipante>().HasOne<Compromisso>().WithMany().HasForeignKey(x => x.CompromissoId);
        mb.Entity<LeadHistoricoEstagio>().HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId);
        mb.Entity<LeadInteracao>().HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId);

        mb.Entity<DataProtectionKey>().ToTable("data_protection_keys");

        // Convenção: coluna = snake_case da propriedade (AreaTotalM2 -> area_total_m2).
        foreach (var entidade in mb.Model.GetEntityTypes())
            foreach (var prop in entidade.GetProperties())
                prop.SetColumnName(SnakeCase(prop.Name));
    }

    /// <summary>Mantém atualizado_em em dia em toda alteração.</summary>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        var agora = DateTime.UtcNow;
        foreach (var e in ChangeTracker.Entries().Where(e => e.State == EntityState.Modified))
        {
            var p = e.Metadata.FindProperty("AtualizadoEm");
            if (p is not null) e.Property(p.Name).CurrentValue = agora;
        }
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    internal static string SnakeCase(string nome)
    {
        var sb = new StringBuilder(nome.Length + 8);
        for (var i = 0; i < nome.Length; i++)
        {
            var c = nome[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
