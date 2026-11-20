using FluentAssertions;

namespace Ember.Api.Tests.Contract;

/// <summary>
/// Golden files next to the contract tests. With <c>EMBER_WRITE_FIXTURES=1</c> they are rewritten from the running app (LF line
/// endings); otherwise a difference fails the test.
/// </summary>
internal static class Golden
{
    public static bool Writing => Environment.GetEnvironmentVariable("EMBER_WRITE_FIXTURES") == "1";

    public static string Path(string file) => System.IO.Path.Combine(RepoRoot(), "tests", "Ember.Api.Tests", "Contract", file);

    public static string? Read(string file) => File.Exists(Path(file)) ? File.ReadAllText(Path(file)).Replace("\r\n", "\n") : null;

    public static void Write(string file, string content)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path(file))!);
        File.WriteAllText(Path(file), content.Replace("\r\n", "\n"));
    }

    /// <summary>Compares a line-per-entry text golden, reporting the lines only in the golden and only in the app.</summary>
    public static void Check(string file, string actual)
    {
        if (Writing)
        {
            Write(file, actual);
            return;
        }

        var expected = Read(file) ?? throw new InvalidOperationException($"{file} is missing; run with EMBER_WRITE_FIXTURES=1.");
        var want = expected.Split('\n').ToHashSet();
        var got = actual.Split('\n').ToHashSet();
        var removed = want.Except(got).ToList();
        var added = got.Except(want).ToList();
        (removed.Count + added.Count).Should().Be(0,
            "{0} differs from the app. In the golden only:\n{1}\nIn the app only:\n{2}\nRun with EMBER_WRITE_FIXTURES=1 to accept.",
            file, string.Join("\n", removed), string.Join("\n", added));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Ember.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Ember.slnx not found above the test output directory.");
    }
}
