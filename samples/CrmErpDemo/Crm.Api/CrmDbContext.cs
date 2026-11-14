using Crm.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Newtonsoft.Json;

namespace Crm.Api;

public class CrmDbContext(DbContextOptions<CrmDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Audit> Audits => Set<Audit>();

    /// <summary>
    /// Creates the list indexes on a database that already exists. The model below declares
    /// the same indexes, but it only reaches a database that is created from it, and start-up
    /// creates the tables once and upgrades them with SQL after that. Safe to run every start.
    /// </summary>
    public const string ListIndexUpgradeSql = @"
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Accounts_CreatedAt_Id' AND object_id = OBJECT_ID('dbo.Accounts'))
            CREATE INDEX IX_Accounts_CreatedAt_Id ON dbo.Accounts (CreatedAt DESC, Id);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Contacts_CreatedAt_Id' AND object_id = OBJECT_ID('dbo.Contacts'))
            CREATE INDEX IX_Contacts_CreatedAt_Id ON dbo.Contacts (CreatedAt DESC, Id);";

    // Properties that are bookkeeping rather than user-visible state — never
    // emit a field-diff audit row for these. IsDeleted flips become a single
    // Deleted summary row instead (see ProjectAuditEntries).
    private static readonly HashSet<string> IgnoredProperties =
        new(StringComparer.Ordinal) { "Id", "CreatedAt", "UpdatedAt", "IsDeleted" };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.LegalName).IsRequired().HasMaxLength(200);
            e.Property(x => x.CountryCode).IsRequired().HasMaxLength(2);
            e.Property(x => x.TaxId).HasMaxLength(40);
            e.Property(x => x.ErpCustomerNumber).HasMaxLength(40);
            // The list is paged newest first, then by Id. Without this index every page sorts
            // the whole table, which took over a second for 21,000 contacts.
            e.HasIndex(x => new { x.CreatedAt, x.Id }).IsDescending(true, false);
        });

        modelBuilder.Entity<Contact>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FirstName).IsRequired().HasMaxLength(100);
            e.Property(x => x.LastName).IsRequired().HasMaxLength(100);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.HasIndex(x => x.AccountId);
            e.HasIndex(x => new { x.CreatedAt, x.Id }).IsDescending(true, false);
        });

        modelBuilder.Entity<Audit>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.EntityType).IsRequired().HasMaxLength(50);
            e.Property(x => x.Action).IsRequired().HasMaxLength(20);
            e.Property(x => x.FieldName).HasMaxLength(100);
            e.Property(x => x.Origin).HasMaxLength(10);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
        });
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ProjectAuditEntries();
        return await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ProjectAuditEntries()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = new List<Audit>();

        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            // Only audit the entity types the demo cares about; never audit
            // Audit rows themselves (would loop and double-write on every save).
            var (entityType, entityId, origin) = ResolveEntity(entry.Entity);
            if (entityType is null) continue;

            switch (entry.State)
            {
                case EntityState.Added:
                    rows.Add(new Audit
                    {
                        Id = Guid.NewGuid(),
                        EntityType = entityType,
                        EntityId = entityId,
                        Action = "Created",
                        NewValue = SnapshotJson(entry),
                        Timestamp = now,
                        Origin = origin,
                    });
                    break;

                case EntityState.Modified:
                    var deletedFlagChanged = false;
                    foreach (var prop in entry.Properties)
                    {
                        var name = prop.Metadata.Name;
                        if (name == "IsDeleted" && prop.IsModified
                            && Equals(prop.OriginalValue, false) && Equals(prop.CurrentValue, true))
                        {
                            deletedFlagChanged = true;
                            continue;
                        }
                        if (IgnoredProperties.Contains(name)) continue;
                        if (!prop.IsModified) continue;
                        if (Equals(prop.OriginalValue, prop.CurrentValue)) continue;

                        rows.Add(new Audit
                        {
                            Id = Guid.NewGuid(),
                            EntityType = entityType,
                            EntityId = entityId,
                            Action = "Updated",
                            FieldName = name,
                            OldValue = Format(prop.OriginalValue),
                            NewValue = Format(prop.CurrentValue),
                            Timestamp = now,
                            Origin = origin,
                        });
                    }
                    if (deletedFlagChanged)
                    {
                        rows.Add(new Audit
                        {
                            Id = Guid.NewGuid(),
                            EntityType = entityType,
                            EntityId = entityId,
                            Action = "Deleted",
                            Timestamp = now,
                            Origin = origin,
                        });
                    }
                    break;

                case EntityState.Deleted:
                    rows.Add(new Audit
                    {
                        Id = Guid.NewGuid(),
                        EntityType = entityType,
                        EntityId = entityId,
                        Action = "Deleted",
                        Timestamp = now,
                        Origin = origin,
                    });
                    break;
            }
        }

        if (rows.Count > 0) Audits.AddRange(rows);
    }

    private static (string? type, Guid id, string? origin) ResolveEntity(object entity) => entity switch
    {
        Account a => ("Account", a.Id, NullIfEmpty(a.Origin)),
        Contact c => ("Contact", c.Id, NullIfEmpty(c.Origin)),
        _ => (null, Guid.Empty, null),
    };

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string? Format(object? value) => value switch
    {
        null => null,
        DateTimeOffset dto => dto.ToString("O"),
        DateTime dt => dt.ToString("O"),
        _ => value.ToString(),
    };

    private static string SnapshotJson(EntityEntry entry)
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in entry.Properties)
        {
            var name = prop.Metadata.Name;
            if (IgnoredProperties.Contains(name)) continue;
            bag[name] = prop.CurrentValue;
        }
        return JsonConvert.SerializeObject(bag);
    }
}
