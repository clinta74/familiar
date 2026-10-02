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
        var dataSource = new NpgsqlDataSourceBuilder(connectionString)
            .EnableDynamicJson()
            .Build();

        services.AddSingleton(dataSource);
        services.AddPooledDbContextFactory<FamiliarDbContext>(options => options.UseNpgsql(dataSource));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FamiliarDbContext>>().CreateDbContext());

        return services;
    }
}
