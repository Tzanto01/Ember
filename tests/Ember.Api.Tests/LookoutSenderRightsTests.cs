using System.Security.Cryptography;
using Ember.Api.Feedback;
using Ember.Api.Lookout;
using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ember.Api.Tests;

/// <summary>The real sender and the deleted-refs verb under the role <c>ops/sql/ember_lookout.sql</c> creates, against every migration.</summary>
public sealed class LookoutSenderRightsTests(PostgresFixture postgres) : ApiTestBase(postgres), IDisposable
{
    private static readonly string RoleSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private readonly PostgresFixture _postgres = postgres;
    private readonly string _dir = Directory.CreateTempSubdirectory("ember-sender-rights-").FullName;

    private string RefsFile => Path.Combine(_dir, "refs.jsonl");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string SuperuserConnection => Host.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")!;

    private string RoleConnection
    {
        get
        {
            var passfile = Path.Combine(_dir, "pgpass");
            File.WriteAllText(passfile, $"*:*:*:ember_lookout:{RoleSecret}\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(passfile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var superuser = new NpgsqlConnectionStringBuilder(SuperuserConnection);
            return new NpgsqlConnectionStringBuilder
            { Host = superuser.Host, Port = superuser.Port, Database = superuser.Database, Username = "ember_lookout", Passfile = passfile, Pooling = false }.ConnectionString;
        }
    }

    private static string Script()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Path.Combine(dir.FullName, "ops", "sql", "ember_lookout.sql") is var script && File.Exists(script)) return script;
        throw new FileNotFoundException("ops/sql/ember_lookout.sql not found above the test binaries");
    }

    private Task RunScript(string? secret = null) => _postgres.RunScriptAsync(SuperuserConnection, Script(), "LOOKOUT_PW", secret ?? RoleSecret);

    private async Task<List<string>> Query(FormattableString sql) => await Host.Db(db => db.Database.SqlQuery<string>(sql).ToListAsync());

    private async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(SuperuserConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private EmberDbContext RoleDb() => new(new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(RoleConnection).Options);

    private ServiceProvider RoleServices()
    {
        var connection = RoleConnection;
        return new ServiceCollection().AddDbContext<EmberDbContext>(o => o.UseNpgsql(connection)).BuildServiceProvider();
    }

    private Task Report(Guid? userId, string title)
        => Host.Run<FeedbackService>(s => s.SaveFeedbackAsync(userId, "web", "1.0", "text of " + title, default, title,
            [new FeedbackImage { ContentType = "image/png", Data = SyntheticImages.Png() }]));

    private Task Upload(string key, TimeSpan age)
        => Host.Db(async db =>
        {
            db.LookoutDiagnostics.Add(new LookoutDiagnostic
            { Key = key, Ref = "EMB-" + key, Body = [1], CreatedAt = Host.Time.GetUtcNow().UtcDateTime - age });
            return await db.SaveChangesAsync();
        });

    /// <summary>
    /// Every path of the sender: a deletion, an anonymous and a linked report confirmed with their copies deleted, a report parked, an
    /// expired, a deferred and a delivered upload; then the deleted-refs verb.
    /// </summary>
    private async Task RunEveryPathAsync()
    {
        await Host.Db(db => db.FeedbackIntake.ExecuteDeleteAsync());
        await Host.Db(db => db.Feedback.ExecuteDeleteAsync());
        await Host.Db(db => db.LookoutOutbox.ExecuteDeleteAsync());
        await Host.Db(db => db.LookoutDiagnostics.ExecuteDeleteAsync());
        File.Delete(RefsFile);
        var leaving = await Host.Service(s => s.ResolveOrCreateDiscordUserAsync(ApiHost.NewDiscordId()));
        var staying = await Host.Service(s => s.ResolveOrCreateDiscordUserAsync(ApiHost.NewDiscordId()));
        await Report(leaving, "leaving");
        await Report(null, "anonymous");
        await Report(staying, "linked");
        await Report(staying, "parked");
        await Host.Run<AccountService>(s => s.DeleteUserDataAsync(leaving));
        await Upload("EXPIRED0", TimeSpan.FromDays(31));
        await Upload("DEFERRED", TimeSpan.Zero);
        await Upload("SENT0000", TimeSpan.Zero);

        await using var services = RoleServices();
        await using var rig = await SenderRig.StartAsync(Host, extraSettings: new()
        {
            [FeedbackRetention.DeleteSentCopiesSetting] = "true",
            [LookoutDeletedRefs.FileSetting] = RefsFile,
        }, scopes: services.GetRequiredService<IServiceScopeFactory>());
        rig.Fake.Script(200, 201, 201, 400, 503, 201);
        for (var i = 0; i < 6; i++) await rig.Step();
        (await rig.Step()).Should().Be(LookoutSender.PollInterval);

        rig.Fake.Requests.Select(r => r.Method + " " + r.Path.Split('/')[3]).Should()
            .Equal("DELETE users", "PUT tickets", "PUT tickets", "PUT tickets", "PUT diagnostics", "PUT diagnostics");
        (await Host.Db(db => db.Feedback.OrderBy(f => f.Title).Select(f => new { f.Title, Pictures = f.Images.Count }).ToListAsync()))
            .Select(f => (f.Title, f.Pictures)).Should().Equal(("linked", 0), ("parked", 1));
        (await Host.Db(db => db.LookoutOutbox.AsNoTracking().OrderBy(o => o.Id).ToListAsync())).Select(o => (Sent: o.SentAt != null, o.ParkedStatus))
            .Should().Equal((true, null), (false, 400));
        (await Host.Db(db => db.LookoutDiagnostics.AsNoTracking().SingleAsync())).Attempts.Should().Be(1);

        await using var db = RoleDb();
        (await LookoutDeletedRefsCli.PrintAsync(db, RefsFile, TextWriter.Null, TextWriter.Null)).Should().Be(0);
    }

    [DockerFact]
    public async Task The_sender_and_the_verb_work_under_the_role_and_it_reaches_nothing_else_also_after_a_rerun_over_other_grants()
    {
        await RunScript();
        await Execute("GRANT SELECT ON \"AspNetUsers\" TO ember_lookout; GRANT CREATE ON SCHEMA public TO ember_lookout; GRANT pg_read_all_data TO ember_lookout");
        await RunScript();

        await RunEveryPathAsync();

        await using var db = RoleDb();
        (await LookoutSenderRights.MissingAsync(db, default)).Should().BeEmpty();
        (await EverythingGranted()).Should().BeEquivalentTo(LookoutSenderRights.Required.Select(r => r.ToString()));
    }

    private Task<List<string>> EverythingGranted() => Query($"""
        SELECT p || ' ("' || a.attname || '") ON "' || k.relname || '"' AS "Value"
        FROM pg_class k JOIN pg_namespace n ON n.oid = k.relnamespace JOIN pg_attribute a ON a.attrelid = k.oid AND a.attnum > 0 AND NOT a.attisdropped
        CROSS JOIN unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'REFERENCES']) p
        WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND k.relkind IN ('r', 'p', 'v', 'm', 'f')
            AND has_column_privilege('ember_lookout', k.oid, a.attnum, p)
        UNION ALL
        SELECT p || ' ON "' || k.relname || '"' FROM pg_class k JOIN pg_namespace n ON n.oid = k.relnamespace
        CROSS JOIN unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'REFERENCES', 'DELETE', 'TRUNCATE', 'TRIGGER', 'MAINTAIN']) p
        WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND k.relkind IN ('r', 'p', 'v', 'm', 'f') AND has_table_privilege('ember_lookout', k.oid, p)
        UNION ALL
        SELECT 'sequence ' || k.relname FROM pg_class k CROSS JOIN unnest(ARRAY['USAGE', 'SELECT', 'UPDATE']) p
        WHERE k.relkind = 'S' AND has_sequence_privilege('ember_lookout', k.oid, p)
        UNION ALL
        SELECT 'create in ' || nspname FROM pg_namespace WHERE has_schema_privilege('ember_lookout', oid, 'CREATE')
        UNION ALL
        SELECT 'create in the database' WHERE has_database_privilege('ember_lookout', current_database(), 'CREATE')
        UNION ALL
        SELECT 'member of ' || roleid::regrole FROM pg_auth_members WHERE member = 'ember_lookout'::regrole
        UNION ALL
        SELECT 'role attribute' FROM pg_roles WHERE rolname = 'ember_lookout'
            AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls OR rolinherit)
        """);

    [DockerFact]
    public async Task Each_granted_right_is_needed_and_the_startup_check_names_exactly_the_missing_one()
    {
        await RunScript();
        foreach (var right in LookoutSenderRights.Required)
        {
            await Execute($"REVOKE {right} FROM ember_lookout");

            var run = RunEveryPathAsync;
            (await run.Should().ThrowAsync<Exception>($"{right} is used")).Which.GetBaseException().Message.Should().Contain("permission denied", $"{right} is used");
            await using (var db = RoleDb())
                (await LookoutSenderRights.MissingAsync(db, default)).Should().Equal(right);

            await Execute($"GRANT {right} TO ember_lookout");
        }

        await RunEveryPathAsync();
    }

    [DockerFact]
    public async Task The_sender_refuses_to_start_and_logs_the_missing_right()
    {
        await RunScript();
        await Execute("REVOKE UPDATE (\"SentAt\") ON \"LookoutOutbox\" FROM ember_lookout");
        await using var services = RoleServices();
        await using var rig = await SenderRig.StartAsync(Host, scopes: services.GetRequiredService<IServiceScopeFactory>());

        var start = () => rig.RequireRights();

        await start.Should().ThrowAsync<InvalidOperationException>();
        rig.Logs.All.Should().Contain("Critical").And.Contain("UPDATE (\"SentAt\") ON \"LookoutOutbox\"");
    }

    [DockerFact]
    public async Task The_login_the_sender_falls_back_to_passes_the_check_and_a_missing_table_fails_it()
    {
        (await Host.Db(db => LookoutSenderRights.MissingAsync(db, default))).Should().BeEmpty();

        await Execute("DROP TABLE \"LookoutDiagnostics\"");

        (await Host.Db(db => LookoutSenderRights.MissingAsync(db, default))).Should().OnlyContain(r => r.Table == "LookoutDiagnostics").And.HaveCount(9);
    }

    [DockerFact]
    public async Task The_script_refuses_a_login_secret_that_is_short_or_would_break_the_connection_string()
    {
        var tooShort = () => RunScript(RoleSecret[..12]);
        var semicolon = () => RunScript(RoleSecret + ";");

        await tooShort.Should().ThrowAsync<InvalidOperationException>();
        await semicolon.Should().ThrowAsync<InvalidOperationException>();
    }
}
