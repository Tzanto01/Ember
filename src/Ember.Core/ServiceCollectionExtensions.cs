using Ember.Core.Data;
using Ember.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ember.Core;

/// <summary>Registers Ember's Core for a host: the API, the bot, the Lookout sender and the command-line tools.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>The connection string every host reads for the PostgreSQL database.</summary>
    public const string ConnectionStringName = "DefaultConnection";

    /// <summary>
    /// Adds <see cref="EmberDbContext"/> (PostgreSQL from <see cref="ConnectionStringName"/>, unless <paramref name="configureDb"/>
    /// configures it instead), <see cref="TimeProvider.System"/>, the <see cref="DeletionLedger"/> (from
    /// <see cref="DeletionLedger.FileSetting"/>), <see cref="HabitService"/>, <see cref="JournalService"/> and
    /// <see cref="FiresideService"/>.
    /// </summary>
    public static IServiceCollection AddEmberCore(this IServiceCollection services, IConfiguration configuration,
        Action<DbContextOptionsBuilder>? configureDb = null)
    {
        services.AddDbContext<EmberDbContext>(options =>
        {
            if (configureDb is not null) configureDb(options);
            else UsePostgres(options, configuration);
        });
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new DeletionLedger(configuration[DeletionLedger.FileSetting], sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<DeletionLedger>>()));
        services.AddScoped<HabitService>();
        services.AddScoped<JournalService>();
        services.AddScoped<FiresideService>();
        return services;
    }

    /// <summary>Options for an <see cref="EmberDbContext"/> made without dependency injection, as the command-line tools do.</summary>
    public static DbContextOptions<EmberDbContext> EmberDbOptions(IConfiguration configuration)
        => UsePostgres(new DbContextOptionsBuilder<EmberDbContext>(), configuration).Options;

    private static T UsePostgres<T>(T options, IConfiguration configuration) where T : DbContextOptionsBuilder
    {
        options.UseNpgsql(configuration.GetConnectionString(ConnectionStringName));
        return options;
    }
}
