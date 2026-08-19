using BookHeaven.Core.Entities;
using BookHeaven.Core.Entities.Base;


namespace BookHeaven.Core;

public partial class DatabaseContext(DbContextOptions<DatabaseContext> options) : DbContext(options)
{
    public DbSet<Book> Books { get; set; }
    public DbSet<Series> Series { get; set; }
    public DbSet<Author> Authors { get; set; }
    public DbSet<BookProgress> BooksProgress { get; set; }
    public DbSet<Profile> Profiles { get; set; }
    public DbSet<ProfileSettings> ProfilesSettings { get; set; }
    public DbSet<Font> Fonts { get; set; }
    public DbSet<Tag> Tags { get; set; }
    public DbSet<Collection> Collections { get; set; }
    public DbSet<KoreaderProgress> KoreaderProgress { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DatabaseContext).Assembly);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = new())
    {
        var now = DateTimeOffset.Now;
        
        foreach (var entry in ChangeTracker.Entries().Where(e => e.Entity is BaseEntity))
        {
            if(entry is { Entity: BaseEntity entity, State: EntityState.Added or EntityState.Modified })
            {
                entity.UpdatedAt = now;
            }
        }
        
        return await base.SaveChangesAsync(cancellationToken);
    }
}