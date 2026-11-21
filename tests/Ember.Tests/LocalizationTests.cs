using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ember.Bot;
using Ember.Bot.Modules;
using Ember.Core.Localization;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;

namespace Ember.Tests;

public class LocalizationTests
{
    private static readonly Regex Placeholder = new(@"\{\d+[^}]*\}");

    private static readonly Regex GermanNeverWords = new(
        @"\b(behandel\w*|behandl\w*|heilen|heilt|geheilt|heilung\w*|therapie\w*|therapeut\w*|klinisch\w*|diagnos\w*|vorbeug\w*|verhinder\w*|linder\w*|reduzier\w*|bewiesen\w*|nachgewiesen\w*)\b",
        RegexOptions.IgnoreCase);

    public static TheoryData<string> ResourceFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in BaseFiles()) data.Add(file);
        return data;
    }

    private static IEnumerable<string> BaseFiles()
        => Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.resx", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".de.resx", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Order()
            .Select(f => Path.GetRelativePath(RepoRoot(), f));

    [Fact]
    public void EveryResourceFileIsFound() => BaseFiles().Should().HaveCountGreaterThanOrEqualTo(4);

    [Theory]
    [MemberData(nameof(ResourceFiles))]
    public void GermanHasEveryKeyWithTheSamePlaceholders(string file)
    {
        var english = Entries(Path.Combine(RepoRoot(), file));
        var german = Entries(Path.Combine(RepoRoot(), file[..^".resx".Length] + ".de.resx"));

        german.Keys.Should().BeEquivalentTo(english.Keys);
        foreach (var (key, text) in german)
        {
            text.Should().NotBeNullOrWhiteSpace(key);
            Placeholder.Matches(text).Select(m => m.Value).Order()
                .Should().Equal(Placeholder.Matches(english[key]).Select(m => m.Value).Order(), key);
        }
    }

    [Fact]
    public void NoKeyIsInTwoResourceFiles()
    {
        var owners = BaseFiles().Where(file => Path.GetFileName(file) != "Commands.resx")
            .SelectMany(file => Entries(Path.Combine(RepoRoot(), file)).Keys.Select(key => (key, file)));
        owners.GroupBy(o => o.key).Where(g => g.Count() > 1).Select(g => g.Key).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(ResourceFiles))]
    public void GermanKeepsTheTone(string file)
    {
        foreach (var (key, text) in Entries(Path.Combine(RepoRoot(), file[..^".resx".Length] + ".de.resx")))
        {
            text.Should().NotContainAny(["—", "–", " - "], key);
            GermanNeverWords.IsMatch(text).Should().BeFalse($"{key} is wellness wording: \"{text}\"");
        }
    }

    [Theory]
    [InlineData("de", "de")]
    [InlineData("de-AT", "de")]
    [InlineData("DE-ch", "de")]
    [InlineData("en-US", "en")]
    [InlineData("fr", "en")]
    [InlineData("dev", "en")]
    [InlineData(null, "en")]
    public void LocaleMapsToALanguage(string? locale, string language) => Languages.FromLocale(locale).Should().Be(language);

    [Theory]
    [InlineData("de-DE,de;q=0.9,en;q=0.8", "de")]
    [InlineData("en-US,en;q=0.9,de;q=0.8", "en")]
    [InlineData("fr-FR,fr;q=0.9,de;q=0.8", "de")]
    [InlineData("en;q=0.5,de-AT;q=0.9", "de")]
    [InlineData("de;q=0.5,en", "en")]
    [InlineData("*,de;q=0.8", "de")]
    [InlineData("de;q=0,en;q=0.1", "en")]
    [InlineData("fr", "en")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    [InlineData(";;;", "en")]
    public void AcceptLanguageMapsToALanguage(string? header, string language)
        => Languages.FromAcceptLanguage(header).Should().Be(language);

    [Fact]
    public void TextsSpeakTheUsersLanguage()
    {
        BotTexts.For("de").Get("reminder.skip").Should().Be("Heute auslassen");
        BotTexts.For("en").Get("reminder.skip").Should().Be("Skip for today");
        BotTexts.For("fr").Language.Should().Be("en");
        BotTexts.For("de").Plural("habit.streak", 1).Should().Be("Streak: **1** Tag");
        BotTexts.For("de").Plural("habit.streak", 3).Should().Be("Streak: **3** Tage");
    }

    [Fact]
    public void AnUnknownKeyFails()
        => FluentActions.Invoking(() => BotTexts.English.Get("no.such.key")).Should().Throw<KeyNotFoundException>();

    [Fact]
    public void EveryLiteralTextKeyInTheCodeExistsInEnglishAndGerman()
    {
        var lookups = LiteralLookups().ToList();
        lookups.Should().HaveCountGreaterThan(300, "the scan must find the code's text lookups");

        var missing = lookups.SelectMany(l => MissingIn(l.Project, l.Keys).Select(k => $"{l.Location}: {k}")).ToList();
        string.Join(Environment.NewLine, missing).Should().BeEmpty();
    }

    [Fact]
    public void EveryKeyTheCodeBuildsFromABoundedSetExists()
    {
        string[] helpSections = ["start", "daily", "manage", "share", "web", "fireside", "privacy", "feedback"];
        IEnumerable<string> keys =
        [
            .. Enumerable.Range(0, 5).Select(n => $"missedday.closing.{n}"),
            .. Enumerable.Range(0, 4).Select(n => $"habit.grace.desc.{n}"),
            .. helpSections.SelectMany(s => new[] { $"help.{s}.title", $"help.{s}.body" }),
            .. OnboardingModule.Regions.Select(r => $"onboard.region.{r.Id}"),
            .. OnboardingModule.Timezones.Values.SelectMany(ids => ids).Select(id => $"onboard.tz.{id}"),
            .. HabitTemplates.All.SelectMany(tpl => new[] { $"template.{tpl.Key}.name", $"template.{tpl.Key}.description" }),
            .. FiresideService.Messages.Select(m => $"fireside.message.{m}"),
            .. ScheduleDayNames.All.Select(d => $"weekday.{d}"),
            "fireside.notify.warmth", "fireside.notify.warmth.unnamed", "fireside.notify.joined", "fireside.notify.joined.unnamed",
            "push.reminder.habit.first", "push.reminder.habit.second", "push.reminder.routine.first", "push.reminder.routine.second",
            "common.logged-today", "checkin.logged-day", "common.skipped-today", "checkin.skipped-day",
        ];

        string.Join(Environment.NewLine, MissingIn("Ember.Bot", keys)).Should().BeEmpty();
    }

    /// <summary>The resource files each project's <see cref="Texts"/> read, English first; the German files sit beside them.</summary>
    private static readonly Dictionary<string, string[]> TextSources = new()
    {
        ["Ember.Core"] = ["src/Ember.Core/Resources/Strings.resx"],
        ["Ember.Api"] = ["src/Ember.Api/Resources/Email.resx", "src/Ember.Core/Resources/Strings.resx"],
        ["Ember.Bot"] = ["src/Ember.Bot/Resources/Bot.resx", "src/Ember.Bot/Resources/Notifications.resx", "src/Ember.Core/Resources/Strings.resx"],
    };

    private static readonly Regex TextLookup = new(@"(?<!\bstring)(?<!\bString)\.(?<method>Get|Format|Plural)\(");

    private static IEnumerable<string> MissingIn(string project, IEnumerable<string> keys)
    {
        TextSources.Should().ContainKey(project, "every project that looks up texts needs its resource files listed here");
        var files = TextSources[project].Select(f => Path.Combine(RepoRoot(), f)).ToList();
        var english = files.SelectMany(f => Entries(f).Keys).ToHashSet(StringComparer.Ordinal);
        var german = files.SelectMany(f => Entries(f[..^".resx".Length] + ".de.resx").Keys).ToHashSet(StringComparer.Ordinal);
        return keys.Where(k => !english.Contains(k) || !german.Contains(k));
    }

    /// <summary>
    /// Every <c>.Get(…)</c>, <c>.Format(…)</c> and <c>.Plural(…)</c> under <c>src/</c> whose key argument is made of string literals only
    /// (one, or the branches of a conditional or switch). Keys built at runtime or passed in a variable are skipped.
    /// </summary>
    private static IEnumerable<(string Project, string Location, string[] Keys)> LiteralLookups()
    {
        var src = Path.Combine(RepoRoot(), "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Order())
        {
            var relative = Path.GetRelativePath(src, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Contains("obj") || parts.Contains("bin") || parts.Contains("Migrations")) continue;
            var code = File.ReadAllText(file);
            foreach (Match match in TextLookup.Matches(code))
            {
                var argument = FirstArgument(code, match.Index + match.Length);
                var keys = LiteralKeys(argument);
                if (keys.Count == 0) continue;
                if (match.Groups["method"].Value == "Plural") keys = keys.SelectMany(k => new[] { k + ".one", k + ".other" }).ToList();
                var line = code.AsSpan(0, match.Index).Count('\n') + 1;
                yield return (parts[0], $"src/{relative.Replace(Path.DirectorySeparatorChar, '/')}:{line}", [.. keys]);
            }
        }
    }

    /// <summary>The first argument of the call whose argument list starts at <paramref name="start"/>.</summary>
    private static string FirstArgument(string code, int start)
    {
        var depth = 0;
        for (var i = start; i < code.Length; i++)
        {
            switch (code[i])
            {
                case '"': i = SkipString(code, i); break;
                case '\'': i = code.IndexOf('\'', i + (code[i + 1] == '\\' ? 3 : 2)); break;
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}' when depth == 0: return code[start..i];
                case ')' or ']' or '}': depth--; break;
                case ',' when depth == 0: return code[start..i];
            }
        }
        return code[start..];
    }

    /// <summary>The index of the closing quote of the string literal opening at <paramref name="quote"/>, for plain, verbatim and interpolated strings.</summary>
    private static int SkipString(string code, int quote)
    {
        var verbatim = code[quote - 1] == '@' || (code[quote - 1] == '$' && code[quote - 2] == '@');
        var interpolated = code[quote - 1] == '$' || (code[quote - 1] == '@' && code[quote - 2] == '$');
        for (var i = quote + 1; i < code.Length; i++)
        {
            if (code[i] == '\\' && !verbatim) i++;
            else if (code[i] == '"' && verbatim && i + 1 < code.Length && code[i + 1] == '"') i++;
            else if (code[i] == '"') return i;
            else if (interpolated && code[i] == '{' && code[i + 1] == '{') i++;
            else if (interpolated && code[i] == '{') i = SkipHole(i);
        }
        return code.Length;

        int SkipHole(int open)
        {
            var depth = 0;
            for (var j = open; j < code.Length; j++)
            {
                if (code[j] == '"') j = SkipString(code, j);
                else if (code[j] == '{') depth++;
                else if (code[j] == '}' && --depth == 0) return j;
            }
            return code.Length;
        }
    }

    private static readonly Regex StringLiteral = new("\"(?<text>[^\"\\\\]*)\"");

    /// <summary>
    /// The keys in a key argument made only of plain string literals: the literal itself, or the results of a conditional or switch.
    /// Literals compared against (<c>x == "on" ? … : …</c>) are conditions, not keys. Empty when the key is built or held in a variable.
    /// </summary>
    private static List<string> LiteralKeys(string argument)
    {
        if (argument.Contains("$\"", StringComparison.Ordinal) || argument.Contains('@')) return [];
        var literals = StringLiteral.Matches(argument).ToList();
        if (literals.Count == 0 || StringLiteral.Replace(argument, "").Contains('+')) return [];
        return literals
            .Where(m => !argument[..m.Index].TrimEnd().EndsWith('=') || argument[..m.Index].TrimEnd().EndsWith("=>", StringComparison.Ordinal))
            .Where(m => !argument[(m.Index + m.Length)..].TrimStart().StartsWith('?'))
            .Select(m => m.Groups["text"].Value)
            .ToList();
    }

    private static Dictionary<string, string> Entries(string path)
        => XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ember.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Ember.slnx not found above the test output directory.");
    }
}
