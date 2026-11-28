using System.Text.Json;
using System.Text.Json.Nodes;
using Ember.Core.Models;
using Ember.Core.Rules;
using FluentAssertions;

namespace Ember.Tests;

/// <summary>
/// Checks Core's picture rules against docs/contracts/fixtures/pictures-v1.json, the cases the no-account client also has to
/// match. The fixture is the truth: it is never rewritten from Core.
/// </summary>
public class PictureFixtureTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record PortionOut(int? PortionAmount, string? PortionUnit);

    private sealed record CaseInput(string Name, string Kind, string? Picture, int? PortionAmount, string? PortionUnit, bool Valid,
        string? Field, string? Message);

    private static JsonNode Fixture()
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "docs", "contracts", "fixtures", "pictures-v1.json")))!;
        root["version"]!.GetValue<int>().Should().Be(1);
        return root;
    }

    [Fact]
    public void Core_validates_the_stored_combinations_as_the_fixture_says()
    {
        var cases = Fixture()["cases"]!.AsArray();
        cases.Should().NotBeEmpty();

        var failures = new List<string>();
        foreach (var node in cases)
        {
            var c = node.Deserialize<CaseInput>(Json)!;
            var state = new HabitPictureState(Parse(c.Picture, PictureRules.ParsePicture), c.PortionAmount, Parse(c.PortionUnit, PictureRules.ParseUnit));
            var kind = c.Kind == "letGo" ? HabitKind.LetGo : HabitKind.Build;
            var expected = c.Valid ? null : new PictureError(c.Field!, c.Message!);
            var actual = PictureRules.Validate(kind, state);
            if (actual != expected) failures.Add($"case \"{c.Name}\": expected {expected?.ToString() ?? "valid"} but Core gave {actual?.ToString() ?? "valid"}");
        }

        failures.Should().BeEmpty("pictures-v1.json is the truth for the picture rules");
    }

    [Fact]
    public void A_picture_set_without_a_portion_gets_the_fixture_default()
    {
        var defaults = Fixture()["defaults"]!.AsObject();
        defaults.Select(d => d.Key).Should().BeEquivalentTo(Enum.GetValues<HabitPicture>().Select(PictureRules.Name));

        foreach (var (name, node) in defaults)
        {
            var state = PictureRules.Apply(default, PictureRules.ParsePicture(name), null, null);
            new PortionOut(state.PortionAmount, state.PortionUnit is { } unit ? PictureRules.Name(unit) : null)
                .Should().Be(node.Deserialize<PortionOut>(Json), name);
            PictureRules.Validate(HabitKind.Build, state).Should().BeNull(name);
        }
    }

    private static T? Parse<T>(string? name, Func<string?, T?> parse) where T : struct
        => name is null ? null : parse(name) ?? throw new InvalidOperationException($"Unknown name {name} in the fixture.");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ember.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Ember.slnx not found above the test output directory.");
    }
}
