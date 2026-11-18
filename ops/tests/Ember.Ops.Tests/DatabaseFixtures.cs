using Ember.Ops.TestHost;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Ember.Ops.Tests;

/// <summary>A throwaway Postgres set up like production, built once and shared by the tests that use it.</summary>
public abstract class DatabaseFixture(bool productionSchema) : IAsyncLifetime
{
    /// <summary>The day every aggregate expectation is written against (Wednesday).</summary>
    public static readonly DateOnly Today = new(2026, 11, 18);

    public OpsTestDatabase Database { get; private set; } = null!;

    public Dataset Data { get; private set; } = null!;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        Database = await OpsTestDatabase.StartAsync(productionSchema, OpsRoot());
        Data = await Dataset.InsertAsync(Database.SuperuserConnection("emberdb"), Today, includeV2: !productionSchema);
    }

    public async Task DisposeAsync() => await Database.DisposeAsync();

    public async Task<NpgsqlConnection> OpenAsync(string database)
    {
        var connection = new NpgsqlConnection(Database.SuperuserConnection(database));
        await connection.OpenAsync();
        return connection;
    }

    public async Task<T> ScalarAsync<T>(string database, string sql, params object[] parameters)
    {
        await using var connection = await OpenAsync(database);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var p in parameters) command.Parameters.Add(new NpgsqlParameter { Value = p });
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default! : (T)result;
    }

    public NpgsqlConnection DashboardConnection()
    {
        var builder = new NpgsqlConnectionStringBuilder(Database.EmberConnection) { Password = File.ReadAllText(Database.EmberPasswordFile).Trim() };
        return new NpgsqlConnection(builder.ConnectionString);
    }

    public OpsDatabases OpenOpsDatabases() => OpsDatabases.Create(new OpsOptions
    {
        EmberConnection = Database.EmberConnection,
        EmberPasswordFile = Database.EmberPasswordFile,
        OpsConnection = Database.OpsConnection,
        OpsPasswordFile = Database.OpsPasswordFile,
    });

    public static string OpsRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "sql", "ember_dashboard.sql"))) return dir.FullName;
        throw new DirectoryNotFoundException("ops root with sql/ember_dashboard.sql not found above the test binaries");
    }
}

/// <summary>Ember migrated with every migration (v2 schema).</summary>
public sealed class V2DatabaseFixture() : DatabaseFixture(productionSchema: false);

/// <summary>Ember migrated only as far as production (<c>main</c>) is.</summary>
public sealed class ProductionDatabaseFixture() : DatabaseFixture(productionSchema: true);

[CollectionDefinition(Name)]
public sealed class V2Collection : ICollectionFixture<V2DatabaseFixture>
{
    public const string Name = "v2 database";
}

[CollectionDefinition(Name)]
public sealed class ProductionCollection : ICollectionFixture<ProductionDatabaseFixture>
{
    public const string Name = "production database";
}
