using Ember.Core.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Ember.TestKit;

/// <summary>
/// Hands out a new database per call on one Postgres container shared by the whole test process, so test classes that each
/// hold a fixture can run in parallel without a container each. The container goes away with the process (Testcontainers' reaper).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string TemplateName = "ember_template";

    private static readonly Lazy<Task<PostgreSqlContainer>> Server = new(StartServerAsync);
    private static readonly Lazy<Task> Template = new(CreateTemplateAsync);
    private static readonly SemaphoreSlim CreateGate = new(1, 1);

    private PostgreSqlContainer? _container;

    public async Task InitializeAsync()
    {
        if (DockerFactAttribute.ShouldRun)
            _container = await Server.Value;
    }

    /// <summary>A new empty database.</summary>
    public Task<string> CreateDatabaseAsync() => CreateAsync(template: null);

    /// <summary>A new database with every migration applied, copied from a template that is migrated once per test run.</summary>
    public async Task<string> CreateMigratedDatabaseAsync()
    {
        await Template.Value;
        return await CreateAsync(TemplateName);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<string> CreateAsync(string? template)
    {
        var container = _container ?? throw new InvalidOperationException("Postgres container is not running.");
        var name = $"ember_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(container, name, template);
        return ConnectionString(container, name);
    }

    private static async Task<PostgreSqlContainer> StartServerAsync()
    {
        var container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithCommand("-c", "fsync=off", "-c", "synchronous_commit=off", "-c", "full_page_writes=off", "-c", "max_connections=300")
            .Build();
        await container.StartAsync();
        return container;
    }

    private static async Task CreateTemplateAsync()
    {
        var container = await Server.Value;
        await CreateDatabaseAsync(container, TemplateName, template: null);
        var connectionString = ConnectionString(container, TemplateName);
        await using (var db = new EmberDbContext(new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(connectionString).Options))
            await db.Database.MigrateAsync();
        await using (var connection = new NpgsqlConnection(connectionString))
            NpgsqlConnection.ClearPool(connection);
    }

    /// <summary>One database is created at a time: Postgres refuses to copy a template that another session is using.</summary>
    private static async Task CreateDatabaseAsync(PostgreSqlContainer container, string name, string? template)
    {
        await CreateGate.WaitAsync();
        try
        {
            await using var connection = new NpgsqlConnection(container.GetConnectionString());
            await connection.OpenAsync();
            var sql = template is null ? $"CREATE DATABASE \"{name}\"" : $"CREATE DATABASE \"{name}\" TEMPLATE \"{template}\"";
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            CreateGate.Release();
        }
    }

    private static string ConnectionString(PostgreSqlContainer container, string database)
        => new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = database }.ConnectionString;
}
