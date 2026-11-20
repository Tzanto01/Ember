using Ember.Core.Models;
using Ember.Core.Rules;
using Microsoft.EntityFrameworkCore;

namespace Ember.Core.Services;

/// <summary>Outcome of creating or editing a routine.</summary>
public enum RoutineWriteResult
{
    Saved,

    /// <summary>The routine, or one of the listed habits, isn't the user's. Nothing was written.</summary>
    NotFound,

    /// <summary>The user already has <see cref="Limits.RoutinesPerUser"/> routines. Nothing was written.</summary>
    LimitReached,
}

/// <summary>Outcome of reordering a user's routines.</summary>
public enum RoutineOrderResult
{
    Saved,

    /// <summary>The ids aren't exactly the user's routines, each once. Nothing was written.</summary>
    Mismatch,
}

/// <summary>Routines: named, ordered groups of habits with an optional shared reminder.</summary>
public partial class HabitService
{
    /// <summary>The user's routines in their chosen order (<see cref="Routine.Position"/>) with their member habits ordered by <see cref="Habit.RoutinePosition"/>.</summary>
    public Task<List<Routine>> GetRoutinesAsync(Guid userId)
        => _db.Routines
            .AsNoTracking()
            .Where(r => r.UserId == userId)
            .Include(r => r.Habits.OrderBy(h => h.RoutinePosition))
            .OrderBy(r => r.Position).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .ToListAsync();

    /// <summary>
    /// Creates a routine at the end of the user's list. <paramref name="habitIds"/> (ordered) become its members,
    /// moving them out of any other routine.
    /// </summary>
    /// <exception cref="ArgumentException">One of <paramref name="habitIds"/> is a let-go habit; nothing is written.</exception>
    public async Task<(RoutineWriteResult Result, Routine? Routine)> AddRoutineAsync(Guid userId, string name, TimeOnly? reminderTime,
        IReadOnlyCollection<string>? reminderExclusions, IReadOnlyList<int>? habitIds)
    {
        if (await _db.Routines.CountAsync(r => r.UserId == userId) >= Limits.RoutinesPerUser)
            return (RoutineWriteResult.LimitReached, null);

        var routine = new Routine
        {
            UserId = userId,
            Name = name,
            ReminderTime = reminderTime,
            ReminderExclusions = reminderExclusions?.Distinct().ToList() ?? [],
            Position = (await _db.Routines.Where(r => r.UserId == userId).MaxAsync(r => (int?)r.Position) ?? -1) + 1,
            CreatedAt = _time.GetUtcNow().UtcDateTime,
        };
        if (habitIds is not null && !await SetRoutineHabitsAsync(userId, routine, habitIds))
            return (RoutineWriteResult.NotFound, null);

        routine.NextReminderUtc = HabitRules.NextReminderUtc(routine, await _settings.GetUserTzAsync(userId), _time.GetUtcNow().UtcDateTime);
        _db.Routines.Add(routine);
        await _db.SaveChangesAsync();
        return (RoutineWriteResult.Saved, routine);
    }

    /// <summary>
    /// Edits the user's routine; null arguments keep the current value. <paramref name="reminderExclusions"/> replaces
    /// <see cref="Routine.ReminderExclusions"/>. <paramref name="habitIds"/> (ordered) replaces the members:
    /// listed habits move into this routine, members not listed leave it.
    /// </summary>
    /// <exception cref="ArgumentException">One of <paramref name="habitIds"/> is a let-go habit; nothing is written.</exception>
    public async Task<RoutineWriteResult> EditRoutineAsync(Guid userId, int routineId, string? name, TimeOnly? reminderTime,
        bool clearReminder, IReadOnlyCollection<string>? reminderExclusions, IReadOnlyList<int>? habitIds)
    {
        var routine = await _db.Routines
            .Include(r => r.Habits)
            .FirstOrDefaultAsync(r => r.Id == routineId && r.UserId == userId);
        if (routine is null) return RoutineWriteResult.NotFound;
        if (habitIds is not null && !await SetRoutineHabitsAsync(userId, routine, habitIds))
            return RoutineWriteResult.NotFound;

        if (name is not null) routine.Name = name;
        if (reminderExclusions is not null) routine.ReminderExclusions = reminderExclusions.Distinct().ToList();
        if (clearReminder)
        {
            routine.ReminderTime = null;
            routine.NextReminderUtc = null;
        }
        else if (reminderTime.HasValue)
        {
            routine.ReminderTime = reminderTime;
            routine.NextReminderUtc = HabitRules.NextReminderUtc(routine, await _settings.GetUserTzAsync(userId), _time.GetUtcNow().UtcDateTime);
        }

        await _db.SaveChangesAsync();
        return RoutineWriteResult.Saved;
    }

    /// <summary>Puts the user's routines in the order of <paramref name="routineIds"/>, which must list each of them once.</summary>
    public async Task<RoutineOrderResult> SetRoutineOrderAsync(Guid userId, IReadOnlyList<int> routineIds)
    {
        var routines = await _db.Routines.Where(r => r.UserId == userId).ToDictionaryAsync(r => r.Id);
        if (routineIds.Count != routines.Count || routineIds.Distinct().Count() != routines.Count
            || routineIds.Any(id => !routines.ContainsKey(id)))
            return RoutineOrderResult.Mismatch;

        for (var i = 0; i < routineIds.Count; i++)
            routines[routineIds[i]].Position = i;
        await _db.SaveChangesAsync();
        return RoutineOrderResult.Saved;
    }

    /// <summary>Deletes the user's routine; its habits stay, ungrouped. False when the routine isn't the user's.</summary>
    public async Task<bool> DeleteRoutineAsync(Guid userId, int routineId)
        => await _db.Routines
            .Where(r => r.Id == routineId && r.UserId == userId)
            .ExecuteDeleteAsync() == 1;

    /// <summary>
    /// Makes <paramref name="habitIds"/> the routine's members in that order. Members of a tracked routine must be loaded.
    /// False, changing nothing, when any id isn't one of the user's habits.
    /// </summary>
    /// <exception cref="ArgumentException">One of the habits is a let-go habit, which can't be in a routine; nothing is changed.</exception>
    private async Task<bool> SetRoutineHabitsAsync(Guid userId, Routine routine, IReadOnlyList<int> habitIds)
    {
        var listed = await _db.Habits
            .Where(h => h.UserId == userId && habitIds.Contains(h.Id))
            .ToDictionaryAsync(h => h.Id);
        if (habitIds.Any(id => !listed.ContainsKey(id))) return false;
        if (listed.Values.Any(h => h.Kind == HabitKind.LetGo))
            throw new ArgumentException("Let-go habits can't be in a routine.", nameof(habitIds));

        foreach (var member in routine.Habits.Where(h => !listed.ContainsKey(h.Id)).ToList())
            routine.Habits.Remove(member);
        for (var i = 0; i < habitIds.Count; i++)
        {
            var habit = listed[habitIds[i]];
            habit.RoutinePosition = i;
            if (!routine.Habits.Contains(habit)) routine.Habits.Add(habit);
        }
        return true;
    }
}
