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
    public static IServiceCollection AddDomain(this IServiceCollection services, Action<DomainOptions> folders)
    {
        var folderOptions = new DomainOptions();
        folders.Invoke(folderOptions);
        
        folderOptions.ValidateAndRegister();
        
        services.AddDbContextFactory<DatabaseContext>(options =>
        {
            options.UseSqlite($"Data Source={Path.Combine(folderOptions.DatabasePath, "BookHeaven.db")}");
#if DEBUG
            options.EnableSensitiveDataLogging();
#endif
        });
        
        using (var scope = services.BuildServiceProvider().CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

            if (context.Database.GetPendingMigrations().Any())
            {
                context.Database.Migrate();
            }
        }

        services.AddMediator(config =>
        {
            config.Assemblies = [typeof(DependencyInjection).Assembly];
            config.GenerateTypesAsInternal = true;
        });

        services.AddSingleton<GlobalEventsService>();
        services.AddScoped<BookManager>();
        
        return services;
    } 
}

public class DomainOptions
{
    public string BooksPath { get; set; } = null!;
    public string CoversPath { get; set; } = null!;
    public string FontsPath { get; set; } = null!;
    public string DatabasePath { get; set; } = null!;
    
    internal void ValidateAndRegister()
    {
        if (string.IsNullOrEmpty(BooksPath)) throw new ArgumentException("BooksPath must be provided");
        if (string.IsNullOrEmpty(CoversPath)) throw new ArgumentException("CoversPath must be provided");
        if (string.IsNullOrEmpty(FontsPath)) throw new ArgumentException("FontsPath must be provided");
        if (string.IsNullOrEmpty(DatabasePath)) throw new ArgumentException("DatabasePath must be provided");
        
        CoreGlobals.BooksPath = BooksPath;
        CoreGlobals.CoversPath = CoversPath;
        CoreGlobals.FontsPath = FontsPath;
        CoreGlobals.DatabasePath = DatabasePath;
        
        Directory.CreateDirectory(BooksPath);
        Directory.CreateDirectory(CoversPath);
        Directory.CreateDirectory(FontsPath);
        Directory.CreateDirectory(DatabasePath);
    }
}