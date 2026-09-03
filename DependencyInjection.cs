using BookHeaven.Core.Features.Reader.Abstractions;
using BookHeaven.Core.Features.Reader.Services;
using BookHeaven.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BookHeaven.Core;

public static class DependencyInjection
{
    // Add migration
    // dotnet ef migrations add [MigrationName] --project BookHeaven.Core --startup-project BookHeaven.Server

    /// <summary>
    /// Registers the database context and any other services needed for the domain layer.<br/>
    /// Applies any pending migrations on startup.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="folders">Action to configure the folder paths for books, covers, fonts, and database</param>
    public static IServiceCollection AddCore(this IServiceCollection services, Action<CoreOptions> folders)
    {
        var folderOptions = new CoreOptions();
        folders.Invoke(folderOptions);
        folderOptions.ValidateAndRegister();
        
        services.Configure(folders);

        services.AddDbContextFactory<DatabaseContext>(options =>
        {
            options.UseSqlite($"Data Source={Path.Combine(folderOptions.DatabasePath, "BookHeaven.db")}");
#if DEBUG
            options.EnableSensitiveDataLogging();
#endif
        });

        services.AddMediator(config =>
        {
            config.Assemblies = [typeof(DependencyInjection).Assembly];
            config.GenerateTypesAsInternal = true;
        });

        
        services.AddSingleton<GlobalEventsService>();
        services.AddScoped<IReaderCacheService, ReaderCacheService>();
        services.AddScoped<IReaderSettingsService, ReaderSettingsService>();
        services.AddScoped<IReaderService, ReaderService>();
        services.AddScoped<BookManager>();
        
        return services;
    }
    
    public static IServiceProvider ApplyDatabaseMigrations(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
        using var dbContext = dbContextFactory.CreateDbContext();

        if (dbContext.Database.GetPendingMigrations().Any())
        {
            dbContext.Database.Migrate();
        }

        return services;
    }
}

public class CoreOptions
{
    public string BooksPath { get; set; } = null!;
    public string CoversPath { get; set; } = null!;
    public string FontsPath { get; set; } = null!;
    public string DatabasePath { get; set; } = null!;
    public string CachePath { get; set; } = null!;
    
    internal void ValidateAndRegister()
    {
        if (string.IsNullOrEmpty(BooksPath)) throw new ArgumentException("BooksPath must be provided");
        if (string.IsNullOrEmpty(CoversPath)) throw new ArgumentException("CoversPath must be provided");
        if (string.IsNullOrEmpty(FontsPath)) throw new ArgumentException("FontsPath must be provided");
        if (string.IsNullOrEmpty(DatabasePath)) throw new ArgumentException("DatabasePath must be provided");
        if (string.IsNullOrEmpty(CachePath)) throw new ArgumentException("CachePath must be provided");
        
        CoreGlobals.BooksPath = BooksPath;
        CoreGlobals.CoversPath = CoversPath;
        CoreGlobals.FontsPath = FontsPath;
        CoreGlobals.DatabasePath = DatabasePath;
        CoreGlobals.CachePath = CachePath;
        
        Directory.CreateDirectory(BooksPath);
        Directory.CreateDirectory(CoversPath);
        Directory.CreateDirectory(FontsPath);
        Directory.CreateDirectory(DatabasePath);
        Directory.CreateDirectory(CachePath);
    }
}