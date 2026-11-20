using FluentAssertions;

namespace Ember.Tests;

/// <summary>
/// Compares text with a reviewed file under tests/Ember.Tests/Golden. With <c>EMBER_WRITE_FIXTURES=1</c> it rewrites the file
/// instead of asserting, like <see cref="RulesFixtureTests"/>.
/// </summary>
internal static class GoldenFile
{
    public static void AssertMatches(string fileName, string actual)
    {
        var path = Path.Combine(RepoRoot(), "tests", "Ember.Tests", "Golden", fileName);
        if (Environment.GetEnvironmentVariable("EMBER_WRITE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        File.Exists(path).Should().BeTrue($"{fileName} is a reviewed golden; create it with EMBER_WRITE_FIXTURES=1");
        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        actual = actual.Replace("\r\n", "\n");
        if (expected == actual) return;

        var want = expected.Split('\n');
        var got = actual.Split('\n');
        var line = Enumerable.Range(0, Math.Min(want.Length, got.Length)).FirstOrDefault(i => want[i] != got[i], Math.Min(want.Length, got.Length));
        throw new Xunit.Sdk.XunitException(
            $"{fileName} differs at line {line + 1}.{Environment.NewLine}  golden: {(line < want.Length ? want[line] : "<end>")}{Environment.NewLine}  actual: {(line < got.Length ? got[line] : "<end>")}{Environment.NewLine}"
            + "After a deliberate change, review it and regenerate with EMBER_WRITE_FIXTURES=1.");
    }

    /// <summary>
    /// For a growing CSV: every golden column must still exist in the same relative order and every golden row must still appear, in order, with
    /// the same values in the golden's columns. Extra columns, extra rows and extra record types in the actual output are fine.
    /// </summary>
    public static void AssertCsvKeeps(string fileName, string actual)
    {
        var path = Path.Combine(RepoRoot(), "tests", "Ember.Tests", "Golden", fileName);
        if (Environment.GetEnvironmentVariable("EMBER_WRITE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        var golden = ParseCsv(File.ReadAllText(path));
        var rows = ParseCsv(actual);
        var columns = rows[0];
        var positions = new List<int>();
        var from = 0;
        foreach (var column in golden[0])
        {
            var at = Array.IndexOf(columns, column, from);
            at.Should().BeGreaterThanOrEqualTo(0, $"column {column} must stay, after the golden columns before it");
            positions.Add(at);
            from = at + 1;
        }

        var next = 1;
        for (var i = 1; i < golden.Count; i++)
        {
            var want = golden[i];
            var found = false;
            for (; next < rows.Count && !found; next++)
                found = positions.Select((p, c) => rows[next].ElementAtOrDefault(p) == want[c]).All(equal => equal);
            found.Should().BeTrue($"golden row {i} must still be exported with the same values, in order: {string.Join(",", want)}");
        }
    }

    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n') { cells.Add(cell.ToString()); cell.Clear(); rows.Add([.. cells]); cells.Clear(); }
            else cell.Append(c);
        }
        if (cell.Length > 0 || cells.Count > 0) { cells.Add(cell.ToString()); rows.Add([.. cells]); }
        return rows;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ember.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Ember.slnx not found above the test output directory.");
    }
}
