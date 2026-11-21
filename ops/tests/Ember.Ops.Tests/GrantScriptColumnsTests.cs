using System.Text.RegularExpressions;
using FluentAssertions;

namespace Ember.Ops.Tests;

/// <summary>
/// <c>ember_dashboard.sql</c> skips a grant or revoke whose table or column is missing, so after a rename or drop the dashboard would
/// silently lose access, or keep a health column the script meant to revoke. Against every Ember migration, each table and column the
/// script names must exist.
/// </summary>
[Collection(EmptyCollection.Name)]
public sealed partial class GrantScriptColumnsTests(EmptyDatabaseFixture fixture)
{
    [Fact]
    public async Task Every_table_and_column_the_grant_script_names_exists_after_all_migrations()
    {
        var script = await File.ReadAllTextAsync(Path.Combine(DatabaseFixture.OpsRoot(), "sql", "ember_dashboard.sql"));
        var named = NamedColumns(script);
        var tables = NamedTables(script);
        named.Should().Contain(("Feedback", "Status")).And.Contain(("Habits", "Kind")).And.Contain(("AuthSessions", "AppVersion"));
        tables.Should().Contain("LookoutOutbox").And.Contain("HabitLogs");

        var existing = (await fixture.ScalarAsync<string>("emberdb", """
            SELECT string_agg(table_name || '.' || column_name, ',') FROM information_schema.columns WHERE table_schema = 'public'
            """)).Split(',').ToHashSet();

        named.Where(c => !existing.Contains($"{c.Table}.{c.Column}")).Should().BeEmpty();
        tables.Where(t => !existing.Any(c => c.StartsWith(t + ".", StringComparison.Ordinal))).Should().BeEmpty();
    }

    private static HashSet<(string Table, string Column)> NamedColumns(string script)
    {
        var columns = new HashSet<(string, string)>();
        foreach (Match grant in GrantOrRevokeColumns().Matches(script))
            foreach (Match column in QuotedName().Matches(grant.Groups["columns"].Value))
                columns.Add((grant.Groups["table"].Value, column.Groups[1].Value));
        foreach (Match check in ColumnCheck().Matches(script))
            columns.Add((check.Groups["table"].Value, check.Groups["column"].Value));
        foreach (Match pair in ValuesPair().Matches(script))
            columns.Add((pair.Groups["table"].Value, pair.Groups["column"].Value));
        return columns;
    }

    private static HashSet<string> NamedTables(string script)
    {
        var tables = new HashSet<string>();
        foreach (Match array in TableArray().Matches(script))
            foreach (Match table in QuotedLiteral().Matches(array.Groups[1].Value))
                tables.Add(table.Groups[1].Value);
        foreach (Match regclass in RegClass().Matches(script))
            tables.Add(regclass.Groups[1].Value);
        return tables;
    }

    [GeneratedRegex("""(?:GRANT|REVOKE ALL)\s+(?:\w+\s+)?\((?<columns>"[^)]*)\)\s+ON\s+"(?<table>\w+)"\s""")]
    private static partial Regex GrantOrRevokeColumns();

    [GeneratedRegex("""table_name = '(?<table>\w+)' AND column_name = '(?<column>\w+)'""")]
    private static partial Regex ColumnCheck();

    [GeneratedRegex("""\('(?<table>\w+)', '(?<column>\w+)'\)""")]
    private static partial Regex ValuesPair();

    [GeneratedRegex("""ARRAY\[([^\]]*)\]""")]
    private static partial Regex TableArray();

    [GeneratedRegex("""to_regclass\('public\."(\w+)"'\)""")]
    private static partial Regex RegClass();

    [GeneratedRegex("\"(\\w+)\"")]
    private static partial Regex QuotedName();

    [GeneratedRegex("""'(\w+)'""")]
    private static partial Regex QuotedLiteral();
}
