using System.Globalization;
using Erp.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Newtonsoft.Json;

namespace Erp.Api;

public class ErpDbContext(DbContextOptions<ErpDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<ErpContact> Contacts => Set<ErpContact>();
    public DbSet<Audit> Audits => Set<Audit>();

    /// <summary>
    /// Creates the list indexes on a database that already exists. The model below declares
    /// the same indexes, but it only reaches a database that is created from it, and start-up
    /// creates the tables once and upgrades them with SQL after that. Safe to run every start.
    /// Keep each INCLUDE list equal to the entity's other columns: ListSortIndexTests checks it.
    /// </summary>
    public const string ListIndexUpgradeSql = @"
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Customers_CreatedAt_Id' AND object_id = OBJECT_ID('dbo.Customers'))
            CREATE INDEX IX_Customers_CreatedAt_Id ON dbo.Customers (CreatedAt DESC, Id) INCLUDE (CustomerNumber, LegalName, TaxId, CountryCode, CrmAccountId, UpdatedAt, Origin, IsDeleted, CreditHold);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Contacts_CreatedAt_Id' AND object_id = OBJECT_ID('dbo.Contacts'))
            CREATE INDEX IX_Contacts_CreatedAt_Id ON dbo.Contacts (CreatedAt DESC, Id) INCLUDE (CustomerId, FirstName, LastName, Email, Phone, UpdatedAt, Origin, IsDeleted);";

    /// <summary>
    /// The SQL Server sequence customer numbers are drawn from. A random suffix let two
    /// customers created on the same day share a number and fail the unique index.
    /// </summary>
    public const string CustomerNumberSequence = "CustomerNumbers";

    /// <summary>
    /// The first sequence value. Numbers issued before the sequence end in five random digits,
    /// so starting at six digits keeps every new number clear of the ones already stored.
    /// </summary>
    public const long FirstCustomerNumber = 100000;

    /// <summary>
    /// Creates the customer-number sequence on a database that already exists; the model
    /// declares it for a new one. Safe to run every start.
    /// </summary>
    public const string CustomerNumberSequenceUpgradeSql = @"
        IF OBJECT_ID(N'dbo.CustomerNumbers', N'SO') IS NULL
            CREATE SEQUENCE dbo.CustomerNumbers AS bigint START WITH 100000 INCREMENT BY 1;";

    /// <summary>Formats a customer number: <c>C-</c>, the UTC date as <c>yyMMdd</c>, and the sequence value.</summary>
    public static string FormatCustomerNumber(DateTime utcNow, long sequenceValue) =>
        string.Create(CultureInfo.InvariantCulture, $"C-{utcNow:yyMMdd}-{sequenceValue}");

    /// <summary>
    /// Issues the next customer number. Never repeats, also across concurrent requests and
    /// replicas, because SQL Server hands out each sequence value once.
    /// </summary>
    public async Task<string> NextCustomerNumberAsync(CancellationToken cancellationToken = default)
    {
        // ToListAsync, not FirstAsync: composing wraps the query in a derived table, where
        // SQL Server does not allow NEXT VALUE FOR.
        var values = await Database
            .SqlQueryRaw<long>($"SELECT NEXT VALUE FOR dbo.{CustomerNumberSequence} AS [Value]")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return FormatCustomerNumber(DateTime.UtcNow, values.Single());
    }

    // Properties that are bookkeeping rather than user-visible state — never
    // emit a field-diff audit row for these. IsDeleted flips become a single
    // Deleted summary row instead (see ProjectAuditEntries).
    private static readonly HashSet<string> IgnoredProperties =
        new(StringComparer.Ordinal) { "Id", "CreatedAt", "UpdatedAt", "IsDeleted" };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<long>(CustomerNumberSequence, "dbo").StartsAt(FirstCustomerNumber).IncrementsBy(1);

        modelBuilder.Entity<Customer>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CustomerNumber).IsRequired().HasMaxLength(40);
            e.HasIndex(x => x.CustomerNumber).IsUnique();
            e.Property(x => x.LegalName).IsRequired().HasMaxLength(200);
            e.Property(x => x.CountryCode).IsRequired().HasMaxLength(2);
            e.Property(x => x.TaxId).HasMaxLength(40);
            e.HasIndex(x => x.CrmAccountId);
            // The list is paged newest first, then by Id. Without an index on that order every
            // page sorts the whole table, which took over a second for 21,000 contacts. The
            // index carries the other columns too: with the key alone, SQL Server looks up
            // every row a deep page skips, which costs more than the sort it replaces.
            e.HasIndex(x => new { x.CreatedAt, x.Id }).IsDescending(true, false)
                .IncludeProperties(x => new { x.CustomerNumber, x.LegalName, x.TaxId, x.CountryCode, x.CrmAccountId, x.UpdatedAt, x.Origin, x.IsDeleted, x.CreditHold });
        });

        modelBuilder.Entity<ErpContact>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FirstName).IsRequired().HasMaxLength(100);
            e.Property(x => x.LastName).IsRequired().HasMaxLength(100);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.HasIndex(x => x.CustomerId);
            e.HasIndex(x => new { x.CreatedAt, x.Id }).IsDescending(true, false)
                .IncludeProperties(x => new { x.CustomerId, x.FirstName, x.LastName, x.Email, x.Phone, x.UpdatedAt, x.Origin, x.IsDeleted });
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
        Customer c => ("Customer", c.Id, NullIfEmpty(c.Origin)),
        ErpContact c => ("ErpContact", c.Id, NullIfEmpty(c.Origin)),
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
