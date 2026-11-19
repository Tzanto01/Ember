using Ember.Ops.TestHost;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Ember.Ops.Tests;

/// <summary>A throwaway Postgres set up like production, built once and shared by the tests that use it.</summary>
public abstract class DatabaseFixture(bool productionSchema, bool seed = true) : IAsyncLifetime
{
    /// <summary>The day every aggregate expectation is written against (Wednesday).</summary>
    public static readonly DateOnly Today = new(2026, 11, 18);

    public OpsTestDatabase Database { get; private set; } = null!;

    public Dataset Data { get; private set; } = null!;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        Database = await OpsTestDatabase.StartAsync(productionSchema, OpsRoot());
        if (seed) Data = await Dataset.InsertAsync(Database.SuperuserConnection("emberdb"), Today, includeV2: !productionSchema);
        await PrepareAsync();
    }

    /// <summary>Runs after the dataset is in.</summary>
    protected virtual Task PrepareAsync() => Task.CompletedTask;

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

/// <summary>
/// Ember with every migration plus the columns Ember is adding (<c>AspNetUsers."CreatedUtc"</c>, <c>AuthSessions."Platform"</c> and
/// <c>"AppVersion"</c>), the grant script re-run, and the dataset dated and tagged: u1 to u3 created 2026-11-10, u4 2026-11-03, u5 unknown,
/// u6 2026-11-17; u1's sessions web without a version, u2's last seen one Android 1.4.0 and its never-seen one unrecorded; and sessions
/// seen today for u3 (Android 1.4.0), u4 (Android 1.3.2), u5 (an unknown platform, 1.3.2) and u6 (Discord 1.4.0).
/// </summary>
public sealed class ComingColumnsDatabaseFixture() : DatabaseFixture(productionSchema: false)
{
    protected override async Task PrepareAsync()
    {
        await using (var connection = await OpenAsync("emberdb"))
        await using (var alter = new NpgsqlCommand("""
            ALTER TABLE "AspNetUsers" ADD COLUMN "CreatedUtc" timestamp with time zone;
            ALTER TABLE "AuthSessions" ADD COLUMN "Platform" text, ADD COLUMN "AppVersion" text;
            """, connection))
            await alter.ExecuteNonQueryAsync();
        await Database.RunScriptAsync(Path.Combine(OpsRoot(), "sql", "ember_dashboard.sql"), "emberdb", "DASH_PW", File.ReadAllText(Database.EmberPasswordFile).Trim());

        var u = Data.UserIds;
        var seen = new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);
        await using var data = await OpenAsync("emberdb");
        List<NpgsqlBatchCommand> commands =
        [
            Command("""UPDATE "AspNetUsers" SET "CreatedUtc" = '2026-11-10T12:00:00Z' WHERE "Id" = ANY($1)""", new[] { u[0], u[1], u[2] }),
            Command("""UPDATE "AspNetUsers" SET "CreatedUtc" = '2026-11-03T12:00:00Z' WHERE "Id" = $1""", u[3]),
            Command("""UPDATE "AspNetUsers" SET "CreatedUtc" = '2026-11-17T12:00:00Z' WHERE "Id" = $1""", u[5]),
            Command("""UPDATE "AuthSessions" SET "Platform" = 'web' WHERE "UserId" = $1""", u[0]),
            Command("""UPDATE "AuthSessions" SET "Platform" = 'Android', "AppVersion" = '1.4.0' WHERE "UserId" = $1 AND "LastSeenUtc" IS NOT NULL""", u[1]),
            .. new (Guid User, string Platform, string Version)[] { (u[2], "android", "1.4.0"), (u[3], "android", "1.3.2"), (u[5], "discord", "1.4.0"), (u[4], "smartwatch", "1.3.2") }.Select(x => Command("""
                INSERT INTO "AuthSessions" ("Id", "UserId", "CreatedUtc", "ExpiresUtc", "LastSeenUtc", "Platform", "AppVersion") VALUES (gen_random_uuid(), $1, $2, $3, $2, $4, $5)
                """, x.User, seen, seen.AddDays(60), x.Platform, x.Version)),
        ];
        await using var batch = new NpgsqlBatch(data);
        commands.ForEach(batch.BatchCommands.Add);
        await batch.ExecuteNonQueryAsync();
    }

    private static NpgsqlBatchCommand Command(string sql, params object[] parameters)
    {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var p in parameters) command.Parameters.Add(new NpgsqlParameter { Value = p });
        return command;
    }
}

/// <summary>Ember with every migration and no rows at all.</summary>
public sealed class EmptyDatabaseFixture() : DatabaseFixture(productionSchema: false, seed: false);

[CollectionDefinition(Name)]
public sealed class ComingColumnsCollection : ICollectionFixture<ComingColumnsDatabaseFixture>
{
    public const string Name = "coming columns database";
}

[CollectionDefinition(Name)]
public sealed class EmptyCollection : ICollectionFixture<EmptyDatabaseFixture>
{
    public const string Name = "empty database";
}

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
