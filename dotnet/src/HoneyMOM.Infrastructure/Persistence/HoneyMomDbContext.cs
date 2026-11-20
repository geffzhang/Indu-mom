using HoneyMOM.Infrastructure.Persistence.Entities;
using HoneyMOM.Application.Metadata;
using HoneyMOM.Infrastructure.Persistence.Metadata;
using Microsoft.EntityFrameworkCore;

namespace HoneyMOM.Infrastructure.Persistence;

public sealed class HoneyMomDbContext : DbContext
{
    private readonly IEntitySchemaCatalog _schemaCatalog;

    public HoneyMomDbContext(
        DbContextOptions<HoneyMomDbContext> options,
        IEntitySchemaCatalog? schemaCatalog = null)
        : base(options)
    {
        _schemaCatalog = schemaCatalog ?? SchemaSnapshotCatalog.Default;
    }

    public DbSet<SystemUserRow> SystemUsers => Set<SystemUserRow>();
    public DbSet<SystemRoleRow> SystemRoles => Set<SystemRoleRow>();
    public DbSet<SettingRow> Settings => Set<SettingRow>();
    public DbSet<AuditLogRow> AuditLogs => Set<AuditLogRow>();

    public override int SaveChanges()
    {
        EnsureAuditAppendOnly();
        return base.SaveChanges();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureAuditAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuditAppendOnly();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnsureAuditAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        DynamicEntityModelBuilder.Apply(modelBuilder, _schemaCatalog);

        modelBuilder.Entity<SystemUserRow>(entity =>
        {
            entity.ToTable("sys_user");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(row => row.Username).HasColumnName("username");
            entity.Property(row => row.Name).HasColumnName("name");
            entity.Property(row => row.Roles).HasColumnName("roles");
            entity.Property(row => row.Employee).HasColumnName("employee");
            entity.Property(row => row.Active).HasColumnName("active");
            entity.Property(row => row.PasswordHash).HasColumnName("password_hash");
            entity.Property(row => row.Scope).HasColumnName("scope").HasColumnType("json");
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(row => row.Username).IsUnique();
        });

        modelBuilder.Entity<SystemRoleRow>(entity =>
        {
            entity.ToTable("sys_role");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(row => row.Code).HasColumnName("code");
            entity.Property(row => row.Name).HasColumnName("name");
            entity.Property(row => row.Perms).HasColumnName("perms").HasColumnType("json");
            entity.Property(row => row.Scope).HasColumnName("scope").HasColumnType("json");
            entity.Property(row => row.Builtin).HasColumnName("builtin");
            entity.Property(row => row.Remark).HasColumnName("remark");
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(row => row.Code).IsUnique();
        });

        modelBuilder.Entity<SettingRow>(entity =>
        {
            entity.ToTable("setting");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(row => row.Key).HasColumnName("key");
            entity.Property(row => row.Value).HasColumnName("value");
            entity.Property(row => row.Remark).HasColumnName("remark");
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(row => row.Key).IsUnique();
        });

        modelBuilder.Entity<AuditLogRow>(entity =>
        {
            entity.ToTable("audit_log");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(row => row.Time).HasColumnName("time");
            entity.Property(row => row.User).HasColumnName("user");
            entity.Property(row => row.Kind).HasColumnName("kind");
            entity.Property(row => row.Target).HasColumnName("target");
            entity.Property(row => row.Detail).HasColumnName("detail").HasColumnType("json");
            entity.Property(row => row.Entity).HasColumnName("entity");
            entity.Property(row => row.EntityId).HasColumnName("entity_id");
            entity.Property(row => row.Action).HasColumnName("action");
            entity.Property(row => row.Diff).HasColumnName("diff").HasColumnType("json");
            entity.Property(row => row.Source).HasColumnName("source");
            entity.Property(row => row.Ip).HasColumnName("ip");
            entity.Property(row => row.Seq).HasColumnName("seq");
            entity.Property(row => row.PrevHash).HasColumnName("prev_hash");
            entity.Property(row => row.Hash).HasColumnName("hash");
            entity.HasIndex(row => row.Seq).IsUnique();
        });
    }

    private void EnsureAuditAppendOnly()
    {
        foreach (var entry in ChangeTracker.Entries<AuditLogRow>())
        {
            if (entry.State == EntityState.Deleted ||
                entry.State == EntityState.Modified && entry.OriginalValues.GetValue<int?>(nameof(AuditLogRow.Seq)) is not null)
            {
                throw new InvalidOperationException("Audit log rows are append-only after sealing and cannot be deleted.");
            }
        }
    }
}
