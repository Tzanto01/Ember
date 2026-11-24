using System.Security.Cryptography;
using Ember.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ember.Ops.TestHost;

/// <summary>
/// A throwaway Postgres set up the way production is: emberdb migrated with Ember's migrations (all, or only up to
/// <see cref="ProductionMigration"/>), then <c>ops/sql/ember_dashboard.sql</c> and <c>ops/sql/emberops.sql</c> run through psql
/// inside the container with the passwords in the environment. The passwords also sit in files, as in production.
/// </summary>
public sealed class OpsTestDatabase : IAsyncDisposable
{
    /// <summary>The newest migration production (<c>main</c>) has.</summary>
    public const string ProductionMigration = "20261118130000_AddRoutinePosition";

    private readonly PostgreSqlContainer _container;
    private readonly string _secrets;

    private OpsTestDatabase(PostgreSqlContainer container, string secrets)
    {
        _container = container;
        _secrets = secrets;
    }

    public string EmberPasswordFile => Path.Combine(_secrets, "ember_dashboard");

    public string OpsPasswordFile => Path.Combine(_secrets, "emberops");

    /// <summary>Superuser connection to <paramref name="database"/>, for seeding and assertions.</summary>
    public string SuperuserConnection(string database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database }.ConnectionString;

    /// <summary>emberdb as <c>ember_dashboard</c>, without the password (that comes from <see cref="EmberPasswordFile"/>).</summary>
    public string EmberConnection => RoleConnection("emberdb", "ember_dashboard");

    /// <summary>emberops as <c>emberops</c>, without the password (that comes from <see cref="OpsPasswordFile"/>).</summary>
    public string OpsConnection => RoleConnection("emberops", "emberops");

    /// <summary>The <c>Ops</c> settings pointing the app at this database.</summary>
    public Dictionary<string, string?> AppSettings() => new()
    {
        ["Ops:EmberConnection"] = EmberConnection,
        ["Ops:EmberPasswordFile"] = EmberPasswordFile,
        ["Ops:OpsConnection"] = OpsConnection,
        ["Ops:OpsPasswordFile"] = OpsPasswordFile,
    };

    public static async Task<OpsTestDatabase> StartAsync(bool productionSchema, string opsRoot, CancellationToken ct = default)
    {
        var container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("emberdb").Build();
        await container.StartAsync(ct);
        var secrets = Directory.CreateTempSubdirectory("ember-ops-testhost-").FullName;
        var database = new OpsTestDatabase(container, secrets);
        try
        {
            var options = new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(database.SuperuserConnection("emberdb")).Options;
            await using (var db = new EmberDbContext(options))
                await db.GetService<IMigrator>().MigrateAsync(productionSchema ? ProductionMigration : null, ct);

            var dashPassword = Password();
            var opsPassword = Password();
            await File.WriteAllTextAsync(database.EmberPasswordFile, dashPassword + "\n", ct);
            await File.WriteAllTextAsync(database.OpsPasswordFile, opsPassword + "\n", ct);

            await database.RunScriptAsync(Path.Combine(opsRoot, "sql", "ember_dashboard.sql"), "emberdb", "DASH_PW", dashPassword, ct);
            await database.RunScriptAsync(Path.Combine(opsRoot, "sql", "emberops.sql"), "postgres", "OPS_PW", opsPassword, ct);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs one of the <c>ops/sql</c> scripts with psql inside the container, as the deploy does, with <paramref name="variable"/> in its environment when given.</summary>
    public async Task RunScriptAsync(string script, string database, string? variable = null, string? password = null, CancellationToken ct = default)
    {
        var target = $"/tmp/{Path.GetFileName(script)}";
        await _container.CopyAsync(await File.ReadAllBytesAsync(script, ct), target, ct: ct);
        var result = await _container.ExecAsync(
            ["sh", "-c", $"{(variable is null ? "" : $"{variable}='{password}' ")}psql -U postgres -d {database} -f - < {target}"], ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(script)} failed ({result.ExitCode}): {result.Stderr}");
    }

    private string RoleConnection(string database, string role) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = _container.Hostname,
            Port = _container.GetMappedPublicPort(5432),
            Database = database,
            Username = role,
        }.ConnectionString;

    private static string Password() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        Directory.Delete(_secrets, recursive: true);
    }
}
