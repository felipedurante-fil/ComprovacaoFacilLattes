using System.Text.Json;
using ComprovacaoFacilLattes.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ComprovacaoFacilLattes.Core.Persistence;

public class AppDbContext : DbContext
{
    private readonly string? _dbPath;

    public DbSet<LattesProfile> Profiles => Set<LattesProfile>();
    public DbSet<LattesSection> Sections => Set<LattesSection>();
    public DbSet<LattesEntry> Entries => Set<LattesEntry>();
    public DbSet<Certificate> Certificates => Set<Certificate>();

    /// <summary>Usa o banco padrão do app (<see cref="AppPaths.DatabasePath"/>).</summary>
    public AppDbContext() : this(AppPaths.DatabasePath) { }

    public AppDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    /// <summary>Para testes: permite injetar opções (ex.: um provider in-memory ou sqlite `:memory:`).</summary>
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured && _dbPath is not null)
        {
            optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var rejectedLinksComparer = new ValueComparer<List<string>>(
            (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
            v => v.Aggregate(0, (hash, s) => HashCode.Combine(hash, s.GetHashCode())),
            v => v.ToList());

        modelBuilder.Entity<LattesProfile>(b =>
        {
            b.HasKey(p => p.Id);

            b.Property(p => p.RejectedLinks)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
                .Metadata.SetValueComparer(rejectedLinksComparer);

            b.HasMany(p => p.Sections)
                .WithOne(s => s.Profile)
                .HasForeignKey(s => s.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            // Todos os certificados do perfil (vinculados + em limbo) são apagados junto com o perfil.
            b.HasMany(p => p.Certificates)
                .WithOne(c => c.Profile)
                .HasForeignKey(c => c.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LattesSection>(b =>
        {
            b.HasKey(s => s.Id);

            b.HasMany(s => s.Entries)
                .WithOne(e => e.Section)
                .HasForeignKey(e => e.SectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LattesEntry>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.CertificateStatus).HasConversion<string>();

            // Nullify (não cascade): ao deletar a entry, os certificados voltam ao limbo.
            b.HasMany(e => e.Certificates)
                .WithOne(c => c.Entry)
                .HasForeignKey(c => c.EntryId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Certificate>(b =>
        {
            b.HasKey(c => c.Id);
        });
    }
}
