using Npgsql;

namespace Ember.Ops;

/// <summary>The two databases: Ember's (read-only, as <c>ember_dashboard</c>) and the dashboard's own <c>emberops</c>.</summary>
public sealed class OpsDatabases(NpgsqlDataSource ember, NpgsqlDataSource ops) : IAsyncDisposable
{
    public NpgsqlDataSource Ember { get; } = ember;

    public NpgsqlDataSource Ops { get; } = ops;

    /// <summary>Builds both data sources from <see cref="OpsOptions"/>, reading each password from its file.</summary>
    public static OpsDatabases Create(OpsOptions options) =>
        new(Open(options.EmberConnection, options.EmberPasswordFile), Open(options.OpsConnection, options.OpsPasswordFile));

    private static NpgsqlDataSource Open(string connectionString, string? passwordFile)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 4 };
        if (!string.IsNullOrWhiteSpace(passwordFile))
            builder.Password = File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await Ember.DisposeAsync();
        await Ops.DisposeAsync();
    }
}
