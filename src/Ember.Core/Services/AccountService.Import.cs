using Ember.Core.Models;
using Ember.Core.Rules;
using Microsoft.EntityFrameworkCore;

namespace Ember.Core.Services;

/// <summary>
/// The validated, not yet saved content of an export file for <see cref="AccountService.ImportAsync"/>. Habits reference their
/// routine through <see cref="Habit.Routine"/> and carry their <see cref="Habit.Logs"/>, <see cref="Habit.Slips"/>, <see cref="Habit.MadeItDays"/> and <see cref="Habit.Notes"/>; a day note whose mood is one of the
/// imported own moods has a null <see cref="DayNote.Mood"/> and that mood as <c>CustomMood</c>. Null settings stay unchanged.
/// <c>TodayOrder</c> is the file's Today order as entries of these habits and routines (null keeps the account's order, empty
/// stores an empty one).
/// </summary>
public sealed record ImportData(
    IReadOnlyList<Routine> Routines, IReadOnlyList<Habit> Habits, IReadOnlyList<DayMark> DayMarks, IReadOnlyList<Mood> Moods,
    IReadOnlyList<(DayNote Note, Mood? CustomMood)> DayNotes, JournalKey? JournalKey, int? GraceDaysPerWeek, string? Theme,
    int SecondReminderMinutes, bool? ShowStreaks = null, string? Language = null, bool? CalmMode = null,
    IReadOnlyList<string>? TipsSeen = null, bool? OfferHabitNote = null, IReadOnlyList<ImportTodayOrderEntry>? TodayOrder = null,
    bool? SuggestPictures = null, UnitSystem? Units = null);

/// <summary>One entry of <see cref="ImportData.TodayOrder"/>: exactly one of a habit or a routine of the same <see cref="ImportData"/>.</summary>
public sealed class ImportTodayOrderEntry
{
    private readonly Habit? _habit;
    private readonly Routine? _routine;

    private ImportTodayOrderEntry(Habit? habit, Routine? routine) => (_habit, _routine) = (habit, routine);

    /// <summary>The entry of <paramref name="habit"/>.</summary>
    public static ImportTodayOrderEntry Of(Habit habit) => new(habit, null);

    /// <summary>The entry of <paramref name="routine"/>.</summary>
    public static ImportTodayOrderEntry Of(Routine routine) => new(null, routine);

    /// <summary>The <see cref="User.TodayOrder"/> key once the entity is saved: <c>h:&lt;id&gt;</c> or <c>r:&lt;id&gt;</c>.</summary>
    public string Key => _habit is not null ? $"h:{_habit.Id}" : $"r:{_routine!.Id}";
}

/// <summary>Outcome of <see cref="AccountService.ImportAsync"/>.</summary>
public enum ImportResult
{
    Imported,

    /// <summary>The account already has habits, routines, own moods, day notes or low days. Nothing was written.</summary>
    NotEmpty,

    /// <summary>The file has a journal key and the account already has one. Nothing was written.</summary>
    JournalKeyExists,

    /// <summary>The user hasn't agreed to the current health-data consent. Nothing was written.</summary>
    ConsentRequired,
}

/// <summary>Import of an export file into an empty account.</summary>
public partial class AccountService
{
    /// <summary>Whether the user has no habits, routines, own moods, day notes or low days, so an import may write into the account.</summary>
    public async Task<bool> IsAccountEmptyAsync(Guid userId)
        => !await _db.Habits.AnyAsync(h => h.UserId == userId) && !await _db.Routines.AnyAsync(r => r.UserId == userId)
           && !await _db.Moods.AnyAsync(m => m.UserId == userId) && !await _db.DayNotes.AnyAsync(n => n.UserId == userId)
           && !await _db.DayMarks.AnyAsync(d => d.UserId == userId);

    /// <summary>
    /// Writes <paramref name="data"/> into the user's account in one transaction, all or nothing, with new ids. Reminders are
    /// scheduled in the user's timezone; an imported journal key starts at version 1 and not turning off. A Today order is stored
    /// with the new ids under the rules of <see cref="UserSettingsService.SetTodayOrderAsync"/>.
    /// </summary>
    public async Task<ImportResult> ImportAsync(Guid userId, ImportData data)
    {
        if (!await _consent.HasHealthDataConsentAsync(userId))
            return ImportResult.ConsentRequired;

        await using var transaction = await _db.Database.BeginTransactionAsync();
        await _db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {userId} FOR UPDATE""");
        if (!await IsAccountEmptyAsync(userId))
            return ImportResult.NotEmpty;
        if (data.JournalKey is not null && await _db.JournalKeys.AnyAsync(k => k.UserId == userId))
            return ImportResult.JournalKeyExists;

        var tz = await _settings.GetTimeZoneAsync(userId);
        foreach (var routine in data.Routines)
        {
            routine.UserId = userId;
            routine.NextReminderUtc = HabitRules.NextReminderUtc(routine, tz, _time.GetUtcNow().UtcDateTime);
        }
        _db.Routines.AddRange(data.Routines);
        await _db.SaveChangesAsync();

        foreach (var habit in data.Habits)
        {
            habit.UserId = userId;
            habit.NextReminderUtc = HabitRules.NextReminderUtc(habit, tz, _time.GetUtcNow().UtcDateTime);
        }
        foreach (var mark in data.DayMarks) mark.UserId = userId;
        foreach (var mood in data.Moods) mood.UserId = userId;
        _db.Habits.AddRange(data.Habits);
        _db.DayMarks.AddRange(data.DayMarks);
        _db.Moods.AddRange(data.Moods);
        if (data.JournalKey is { } key)
        {
            key.UserId = userId;
            key.Version = 1;
            key.TurningOff = false;
            _db.JournalKeys.Add(key);
        }
        await _db.SaveChangesAsync();

        foreach (var (note, customMood) in data.DayNotes)
        {
            note.UserId = userId;
            if (customMood is not null) note.Mood = $"custom:{customMood.Id}";
            _db.DayNotes.Add(note);
        }
        await _db.SaveChangesAsync();

        var grace = data.GraceDaysPerWeek;
        var theme = data.Theme;
        var secondReminderMinutes = data.SecondReminderMinutes;
        var showStreaks = data.ShowStreaks;
        var language = data.Language;
        var calmMode = data.CalmMode;
        var offerHabitNote = data.OfferHabitNote;
        var suggestPictures = data.SuggestPictures;
        var units = data.Units;
        await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.GraceDaysPerWeek, u => grace ?? u.GraceDaysPerWeek)
                .SetProperty(u => u.Theme, u => theme ?? u.Theme)
                .SetProperty(u => u.SecondReminderMinutes, secondReminderMinutes)
                .SetProperty(u => u.ShowStreaks, u => showStreaks ?? u.ShowStreaks)
                .SetProperty(u => u.Language, u => language ?? u.Language)
                .SetProperty(u => u.CalmMode, u => calmMode ?? u.CalmMode)
                .SetProperty(u => u.OfferHabitNote, u => offerHabitNote ?? u.OfferHabitNote)
                .SetProperty(u => u.SuggestPictures, u => suggestPictures ?? u.SuggestPictures)
                .SetProperty(u => u.Units, u => units ?? u.Units));
        foreach (var tip in data.TipsSeen ?? []) await _settings.MarkTipSeenAsync(userId, tip);
        if (data.TodayOrder is { } order) await _settings.SetTodayOrderAsync(userId, order.Select(e => e.Key).ToList());
        await transaction.CommitAsync();
        return ImportResult.Imported;
    }
}
