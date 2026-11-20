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
        var expected = File.ReadAllText(path);
        if (expected == actual) return;

        var want = expected.Split('\n');
        var got = actual.Split('\n');
        var line = Enumerable.Range(0, Math.Min(want.Length, got.Length)).FirstOrDefault(i => want[i] != got[i], Math.Min(want.Length, got.Length));
        throw new Xunit.Sdk.XunitException(
            $"{fileName} differs at line {line + 1}.{Environment.NewLine}  golden: {(line < want.Length ? want[line] : "<end>")}{Environment.NewLine}  actual: {(line < got.Length ? got[line] : "<end>")}{Environment.NewLine}"
            + "After a deliberate change, review it and regenerate with EMBER_WRITE_FIXTURES=1.");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ember.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Ember.slnx not found above the test output directory.");
    }
}
