using Ember.Core.Models;

namespace Ember.Core.Rules;

/// <summary>The first failing picture rule: <c>Field</c> is <c>picture</c>, <c>portionAmount</c> or <c>portionUnit</c> (the API field names).</summary>
public sealed record PictureError(string Field, string Message);

/// <summary>A habit's picture and portion as stored (<see cref="Habit.Picture"/>, <see cref="Habit.PortionAmount"/>, <see cref="Habit.PortionUnit"/>).</summary>
public readonly record struct HabitPictureState(HabitPicture? Picture, int? PortionAmount, PortionUnit? PortionUnit)
{
    /// <summary>The picture state of <paramref name="habit"/>.</summary>
    public static HabitPictureState Of(Habit habit) => new(habit.Picture, habit.PortionAmount, habit.PortionUnit);
}

/// <summary>
/// Habit picture rules (habit-visuals-v1.md): which units each picture's portion allows, their ranges, the default portions, the wire
/// names and the checks every write path runs, so no path stores an invalid combination.
/// </summary>
public static class PictureRules
{
    /// <summary>Message for a picture name that isn't one of the five.</summary>
    public const string PictureMessage = "Must be one of bottle, pills, kindling, shelf, trail.";

    /// <summary>Message for a portion unit name that isn't one of the four.</summary>
    public const string UnitMessage = "Must be one of ml, min, pages, m.";

    /// <summary>Message for a picture or portion on a let-go habit.</summary>
    public const string LetGoMessage = "Never set for let-go habits.";

    /// <summary>The wire and export name of <paramref name="picture"/>.</summary>
    public static string Name(HabitPicture picture) => picture.ToString().ToLowerInvariant();

    /// <summary>The wire and export name of <paramref name="unit"/>.</summary>
    public static string Name(PortionUnit unit) => unit switch
    {
        PortionUnit.Milliliters => "ml",
        PortionUnit.Minutes => "min",
        PortionUnit.Pages => "pages",
        PortionUnit.Meters => "m",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
    };

    /// <summary>The picture named <paramref name="name"/> (exact lowercase wire name), or null for any other text.</summary>
    public static HabitPicture? ParsePicture(string? name) =>
        Enum.GetValues<HabitPicture>().Cast<HabitPicture?>().FirstOrDefault(p => Name(p!.Value) == name);

    /// <summary>The unit named <paramref name="name"/> (exact wire name), or null for any other text.</summary>
    public static PortionUnit? ParseUnit(string? name) =>
        Enum.GetValues<PortionUnit>().Cast<PortionUnit?>().FirstOrDefault(u => Name(u!.Value) == name);

    /// <summary>The units a portion of <paramref name="picture"/> may use; empty for pills and kindling, which take no portion.</summary>
    public static IReadOnlyList<PortionUnit> AllowedUnits(HabitPicture picture) => picture switch
    {
        HabitPicture.Bottle => [PortionUnit.Milliliters],
        HabitPicture.Shelf => [PortionUnit.Minutes, PortionUnit.Pages],
        HabitPicture.Trail => [PortionUnit.Minutes, PortionUnit.Meters],
        _ => [],
    };

    /// <summary>The inclusive range of a portion amount in <paramref name="unit"/>.</summary>
    public static (int Min, int Max) Range(PortionUnit unit) => unit switch
    {
        PortionUnit.Milliliters => (50, 2000),
        PortionUnit.Minutes => (1, 240),
        PortionUnit.Pages => (1, 500),
        PortionUnit.Meters => (100, 50000),
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
    };

    /// <summary>The portion <paramref name="picture"/> gets when it is set without one; null for pills and kindling.</summary>
    public static (int Amount, PortionUnit Unit)? DefaultPortion(HabitPicture picture) => picture switch
    {
        HabitPicture.Bottle => (250, PortionUnit.Milliliters),
        HabitPicture.Shelf => (15, PortionUnit.Minutes),
        HabitPicture.Trail => (10, PortionUnit.Minutes),
        _ => null,
    };

    /// <summary>
    /// The picture state a write leads to from <paramref name="current"/> (all null for a new habit). <paramref name="clear"/> removes
    /// the picture and portion. A sent portion (either field) replaces the portion as sent. Without one, a sent picture equal to the
    /// current one keeps the current portion and a different one gets its <see cref="DefaultPortion"/>. Nothing sent keeps
    /// <paramref name="current"/>. The result still needs <see cref="Validate"/>.
    /// </summary>
    public static HabitPictureState Apply(HabitPictureState current, HabitPicture? picture, int? portionAmount, PortionUnit? portionUnit,
        bool clear = false)
    {
        if (clear) return default;
        var target = picture ?? current.Picture;
        if (portionAmount is not null || portionUnit is not null) return new(target, portionAmount, portionUnit);
        if (picture is null || picture == current.Picture) return current;
        return DefaultPortion(picture.Value) is { } portion ? new(picture, portion.Amount, portion.Unit) : new(picture, null, null);
    }

    /// <summary>
    /// The first rule <paramref name="state"/> breaks for a habit of <paramref name="kind"/>, or null when it may be stored. In order:
    /// let-go (on <c>picture</c> when one is set, else on <c>portionAmount</c>); a portion for pills or kindling; only one of amount and
    /// unit; a portion without a picture; a unit the picture doesn't allow; an amount outside the unit's <see cref="Range"/>.
    /// </summary>
    public static PictureError? Validate(HabitKind kind, HabitPictureState state)
    {
        var (picture, amount, unit) = state;
        var anyPortion = amount is not null || unit is not null;
        if (kind == HabitKind.LetGo)
        {
            if (picture is not null) return new("picture", LetGoMessage);
            return anyPortion ? new("portionAmount", LetGoMessage) : null;
        }
        if (picture is HabitPicture.Pills or HabitPicture.Kindling && anyPortion)
            return new("portionAmount", "Pills and kindling have no portion.");
        if ((amount is null) != (unit is null)) return new("portionAmount", "Send portionAmount and portionUnit together.");
        if (amount is not { } value || unit is not { } portionUnit) return null;
        if (picture is not { } p) return new("portionAmount", "Choose a picture first.");
        if (!AllowedUnits(p).Contains(portionUnit))
            return new("portionUnit", $"Must be {string.Join(" or ", AllowedUnits(p).Select(Name))} for {Name(p)}.");
        var (min, max) = Range(portionUnit);
        return value < min || value > max ? new("portionAmount", $"Must be between {min} and {max}.") : null;
    }

    /// <summary>Throws when <see cref="Validate"/> finds a broken rule.</summary>
    /// <exception cref="ArgumentException">The first broken rule, its field as the parameter name.</exception>
    public static void ThrowIfInvalid(HabitKind kind, HabitPictureState state)
    {
        if (Validate(kind, state) is { } error) throw new ArgumentException(error.Message, error.Field);
    }
}
