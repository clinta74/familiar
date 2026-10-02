using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Familiar.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers a pooled <see cref="IDbContextFactory{TContext}"/> (for singletons and auth
    /// handlers) and a scoped <see cref="FamiliarDbContext"/> (for endpoints) over one data source.
    /// </summary>
    public static IServiceCollection AddFamiliarPersistence(this IServiceCollection services, string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString).EnableDynamicJson();
        builder.UseVector();
        var dataSource = builder.Build();

        services.AddSingleton(dataSource);
        services.AddPooledDbContextFactory<FamiliarDbContext>(options => options.UseNpgsql(dataSource, npgsql => npgsql.UseVector()));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FamiliarDbContext>>().CreateDbContext());

        return services;
    }
}
