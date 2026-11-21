using Ember.Bot.Discord.Messages;
using Ember.Bot.Notifications;
using Ember.Bot.Services;
using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Ember.Bot.Jobs;

/// <summary>
/// Runs every minute and sends every reminder whose <see cref="Habit.NextReminderUtc"/> is due once to each server destination
/// <see cref="ReminderDestinations.Resolve"/> picks for the habit (Discord: a Discord login, the account answered yes, DM reminders not
/// paused and not excluded; web push: each enabled push subscription not in <see cref="Habit.ReminderExclusions"/>, when the channel is
/// registered), then advances it once to the next local
/// occurrence of any of the habit's <see cref="Habit.ReminderTimes"/>, so a habit with several times sends at each of them while
/// its day is still open. Only the Discord outcome counts towards the DM pause. The advance applies only while the reminder is still
/// the one that was due, so an edit or snooze saved while it was being sent (for example by the API) is kept.
/// Phones are never sent to from here: the native app schedules their reminders as local notifications on the device.
/// While the Discord client isn't logged in (<see cref="IDiscordDmSender.IsLoggedIn"/>), Discord is left out: a reminder it would send only
/// there stays due without counting a failure, so it goes out once the login succeeds (unless it went stale meanwhile), one with other
/// destinations goes to those, and open Discord reminder messages wait to be closed.
/// Reminders overdue by more than <see cref="ReminderPolicy.StaleAfter"/> (e.g. after downtime) are advanced
/// without sending, as are habits paused or already completed on the user's local day, weekly habits whose target is already
/// reached in the user's local Monday–Sunday week, and habits in a routine that has a
/// reminder (the routine's reminder replaces theirs); <see cref="ReminderPolicy"/> decides. A due <see cref="Routine"/> reminder is sent the same way, once, listing
/// the member habits still pending or partial on the user's local day, and advanced without sending when there are none.
/// After <see cref="MaxConsecutiveDmFailures"/> undeliverable reminders in a row the user's
/// reminders pause (still advanced, not sent) until they confirm Ember can reach them again.
/// A reminder that no channel delivered because a send failed transiently (timeout, 5xx, 429, network), or a first or routine
/// reminder whose processing threw, stays due and is retried up to <see cref="ReminderRetries.MaxAttempts"/> attempts in all, <see cref="ReminderRetries.Spacing"/> apart;
/// after that it moves to its next reminder time and, for a Discord send, counts once towards the DM pause.
/// A delivered regular (not snoozed) reminder schedules one second reminder <see cref="User.SecondReminderMinutes"/> later when that
/// is on, falls on the same local day and comes before the habit's next regular reminder; it is sent like the first only if the day
/// (and a weekly habit's week) is still open then, and never followed by another.
/// A <see cref="Core.Models.HabitKind.LetGo"/> habit's reminder is its quiet check-in (<see cref="Reminder.LetGo"/>): sent at its time
/// unless stale or paused that day (no done, weekly or low-day checks), never snoozed and never followed by a second reminder.
/// Each run then closes the open Discord reminder messages whose habits are done for their day (a routine's: every member due that
/// day, <see cref="HabitRules.IsRoutineDone"/>), or whose buttons expired
/// (<see cref="ReminderExpiry.IsExpired"/>), and for each web push reminder delivered for an item that is now done for its day sends the clear push
/// (<see cref="WebPushSender.SendClearAsync"/>) to the user's push subscriptions that are targets of that item; then forgets
/// it, as it forgets expired ones without a push.
/// </summary>
[DisallowConcurrentExecution]
public class ReminderDispatchJob : IJob
{
    public static readonly JobKey Key = new("reminder-dispatch", "reminders");

    /// <summary>Undeliverable reminders in a row after which a user's DM reminders pause.</summary>
    public const int MaxConsecutiveDmFailures = 3;

    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<IReminderChannel> _channels;
    private readonly IDiscordDmSender _discord;
    private readonly ILogger<ReminderDispatchJob> _logger;
    private readonly TimeProvider _time;
    private readonly ReminderRetries _retries;
    private readonly WebPushSender? _push;

    private const string HabitKind = "habit", RoutineKind = "routine", SecondHabitKind = "second-habit", SecondRoutineKind = "second-routine";

    private enum Delivery { Delivered, NotDelivered, Retry }

    public ReminderDispatchJob(IServiceProvider services, IEnumerable<IReminderChannel> channels, IDiscordDmSender discord,
        ILogger<ReminderDispatchJob> logger, TimeProvider time, ReminderRetries retries, WebPushSender? push = null)
    {
        _retries = retries;
        _services = services;
        _channels = channels.DistinctBy(c => c.Channel).ToList();
        _discord = discord;
        _logger = logger;
        _time = time;
        _push = push;
    }

    public Task Execute(IJobExecutionContext context) => RunAsync(context.CancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        _retries.Prune(now);
        List<(int Id, DateTime DueAt)> dueHabits, dueRoutines;
        List<int> dueSecondIds, dueSecondRoutineIds;
        using (var scope = _services.CreateScope())
        {
            var reminders = scope.ServiceProvider.GetRequiredService<ReminderService>();
            dueHabits = await reminders.GetDueReminderHabitsAsync(now, cancellationToken);
            dueRoutines = await reminders.GetDueReminderRoutinesAsync(now, cancellationToken);
            dueSecondIds = await reminders.GetDueSecondReminderHabitIdsAsync(now, cancellationToken);
            dueSecondRoutineIds = await reminders.GetDueSecondReminderRoutineIdsAsync(now, cancellationToken);
        }

        foreach (var habitId in dueSecondIds)
        {
            if (cancellationToken.IsCancellationRequested) return;
            try
            {
                await ProcessSecondAsync(habitId, now, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Second reminder for habit {HabitId} failed; dropping it.", habitId);
                await ClearSecondAsync(reminders => reminders.ClearSecondReminderAsync(habitId, cancellationToken));
            }
        }

        foreach (var routineId in dueSecondRoutineIds)
        {
            if (cancellationToken.IsCancellationRequested) return;
            try
            {
                await ProcessRoutineSecondAsync(routineId, now, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Second reminder for routine {RoutineId} failed; dropping it.", routineId);
                await ClearSecondAsync(reminders => reminders.ClearRoutineSecondReminderAsync(routineId, cancellationToken));
            }
        }

        foreach (var (habitId, dueAt) in dueHabits)
        {
            if (cancellationToken.IsCancellationRequested) return;
            if (!_retries.CanAttempt(HabitKind, habitId, dueAt, now)) continue;
            try
            {
                await ProcessAsync(habitId, now, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!_retries.Fail(HabitKind, habitId, dueAt, now))
                {
                    _logger.LogError(ex, "Reminder for habit {HabitId} failed; retrying later.", habitId);
                    continue;
                }
                _logger.LogError(ex, "Reminder for habit {HabitId} failed again; moving it to its next reminder time.", habitId);
                await DeferAsync(habitId, now, cancellationToken);
            }
        }

        foreach (var (routineId, dueAt) in dueRoutines)
        {
            if (cancellationToken.IsCancellationRequested) return;
            if (!_retries.CanAttempt(RoutineKind, routineId, dueAt, now)) continue;
            try
            {
                await ProcessRoutineAsync(routineId, now, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!_retries.Fail(RoutineKind, routineId, dueAt, now))
                {
                    _logger.LogError(ex, "Reminder for routine {RoutineId} failed; retrying later.", routineId);
                    continue;
                }
                _logger.LogError(ex, "Reminder for routine {RoutineId} failed again; moving it to its next reminder time.", routineId);
                await DeferRoutineAsync(routineId, now, cancellationToken);
            }
        }

        await CloseReminderMessagesAsync(cancellationToken);
        await ClosePushRemindersAsync(cancellationToken);
    }

    /// <summary>
    /// For each delivered web push reminder whose habit or routine is done for its day, sends the clear push to the push subscriptions
    /// that are its targets now, then forgets it; forgets expired ones without a push. A failed clear is forgotten too (sent at most once).
    /// </summary>
    private async Task ClosePushRemindersAsync(CancellationToken cancellationToken)
    {
        List<(PushReminder Reminder, string Timezone, bool HabitDone)> open;
        using (var scope = _services.CreateScope())
            open = await scope.ServiceProvider.GetRequiredService<ReminderService>().GetPushRemindersAsync(cancellationToken);

        foreach (var (reminder, timezone, habitDone) in open)
        {
            if (cancellationToken.IsCancellationRequested) return;
            var expired = ReminderExpiry.IsExpired(reminder.Date, TimeZones.LocalNow(timezone, _time.GetUtcNow()).DateTime);
            if (reminder.HabitId is not null && !habitDone && !expired) continue;
            try
            {
                using var scope = _services.CreateScope();
                var reminders = scope.ServiceProvider.GetRequiredService<ReminderService>();
                if (!expired)
                {
                    User? user = null;
                    List<string> exclusions = [];
                    var tag = "";
                    if (reminder.HabitId is { } habitId)
                    {
                        if (await reminders.GetHabitWithUserAsync(habitId, cancellationToken) is { } habit)
                            (user, exclusions, tag) = (habit.User, habit.ReminderExclusions, WebPushSender.HabitTag(habitId));
                    }
                    else if (await reminders.GetRoutineWithUserAsync(reminder.RoutineId!.Value, cancellationToken) is { } routine)
                    {
                        if (!await IsRoutineDoneAsync(scope.ServiceProvider, routine, reminder.Date)) continue;
                        (user, exclusions, tag) = (routine.User, routine.ReminderExclusions, WebPushSender.RoutineTag(routine.Id));
                    }
                    if (user is not null && _push is not null
                        && ReminderDestinations.Resolve(user, exclusions).PushSubscriptionIds is { Count: > 0 } ids)
                        await _push.SendClearAsync(user.Id, ids, tag, user.Language, cancellationToken);
                }
                await reminders.DeletePushReminderAsync(reminder.Id, CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not clear web push reminder {PushReminderId}.", reminder.Id);
            }
        }
    }

    /// <summary>
    /// Closes each open Discord reminder message whose habits are done for its day (<see cref="ReminderDm.LoggedLine"/>)
    /// or whose buttons expired (<see cref="ReminderDm.ClosedLine"/>), and forgets it, also when Discord can't edit it. Nothing while the
    /// Discord client isn't logged in.
    /// </summary>
    private async Task CloseReminderMessagesAsync(CancellationToken cancellationToken)
    {
        if (!_discord.IsLoggedIn) return;
        List<(ReminderMessage Message, string Timezone, bool HabitDone, string Language)> open;
        using (var scope = _services.CreateScope())
            open = await scope.ServiceProvider.GetRequiredService<ReminderService>().GetOpenReminderMessagesAsync(cancellationToken);

        foreach (var (message, timezone, habitDone, language) in open)
        {
            if (cancellationToken.IsCancellationRequested) return;
            var expired = ReminderExpiry.IsExpired(message.Date, TimeZones.LocalNow(timezone, _time.GetUtcNow()).DateTime);
            if (message.HabitId is not null && !habitDone && !expired) continue;
            try
            {
                using var scope = _services.CreateScope();
                var reminders = scope.ServiceProvider.GetRequiredService<ReminderService>();
                var done = message.HabitId is not null ? habitDone
                    : message.RoutineId is { } routineId && await reminders.GetRoutineWithUserAsync(routineId, cancellationToken) is { } routine
                    && await IsRoutineDoneAsync(scope.ServiceProvider, routine, message.Date);
                if (!done && !expired) continue;

                await _discord.CloseMessageAsync(message.ChannelId, message.MessageId,
                    done ? ReminderDm.LoggedLine(BotTexts.For(language)) : ReminderDm.ClosedLine(BotTexts.For(language)),
                    cancellationToken);
                await reminders.DeleteReminderMessageAsync(message.Id, CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not close reminder message {ReminderMessageId}.", message.Id);
            }
        }
    }

    /// <summary>
    /// Whether the routine (loaded with its user and recent logs) is done for its reminder on the user's local <paramref name="date"/>
    /// (<see cref="HabitRules.IsRoutineDone"/>): every member due that day is done.
    /// </summary>
    private async Task<bool> IsRoutineDoneAsync(IServiceProvider services, Routine routine, DateOnly date)
    {
        var user = routine.User;
        var lowDays = await services.GetRequiredService<HabitService>().GetLowDaysAsync(user.Id);
        return HabitRules.IsRoutineDone(routine.Habits, date, TimeZones.LocalToday(user.Timezone, _time.GetUtcNow()),
            TimeZones.Find(user.Timezone) ?? TimeZoneInfo.Utc, lowDays);
    }

    /// <summary>
    /// Sends the habit's due second reminder when <see cref="ReminderPolicy.ShouldSendSecondReminder"/> says so; then drops it either way.
    /// </summary>
    private async Task ProcessSecondAsync(int habitId, DateTime now, CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var reminders = scope.ServiceProvider.GetRequiredService<ReminderService>();
        var habits = scope.ServiceProvider.GetRequiredService<HabitService>();
        var habit = await reminders.GetHabitWithUserAsync(habitId, cancellationToken);
        if (habit?.SecondReminderUtc is not { } dueAt || dueAt > now || !_retries.CanAttempt(SecondHabitKind, habitId, dueAt, now)) return;

        var user = habit.User;
        var tz = TimeZones.Find(user.Timezone) ?? TimeZoneInfo.Utc;
        var today = TimeZones.LocalToday(user.Timezone, _time.GetUtcNow());
        var discordId = user.DiscordId();
        var (targets, pushIds) = Targets(user, habit.ReminderExclusions);
        var send = targets.Count > 0
            && ReminderPolicy.ShouldSendSecondReminder(habit, user.SecondReminderMinutes, dueAt, now, today, tz,
                await DayFactsAsync(reminders, habits, habit, today, cancellationToken));
        if (send)
        {
            if (Reachable(targets) is not { } reachable) return;
            targets = reachable;
        }

        var delivery = Delivery.NotDelivered;
        if (send)
        {
            var recent = await reminders.CountCompletedBetweenAsync(habit.Id, today.AddDays(-7), today, cancellationToken);
            delivery = await DeliverAsync(targets, new Reminder(user.Id, discordId, habit.Id, habit.Name, recent, false, today, Second: true,
                PushSubscriptionIds: pushIds, Language: user.Language),
                user, now, (SecondHabitKind, habit.Id, dueAt), cancellationToken);
        }

        if (delivery != Delivery.Retry) habit.SecondReminderUtc = null;
        await reminders.SaveChangesAsync(send ? CancellationToken.None : cancellationToken);
        if (delivery != Delivery.Retry) _retries.Forget(SecondHabitKind, habitId, dueAt);
    }

    /// <summary>
    /// Sends the routine's due second reminder, listing the members still pending or partial, while it
    /// <see cref="ReminderPolicy.IsSecondStillDue"/>; then drops it either way.
    /// </summary>
    private async Task ProcessRoutineSecondAsync(int routineId, DateTime now, CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var reminders = scope.ServiceProvider.GetRequiredService<ReminderService>();
        var habits = scope.ServiceProvider.GetRequiredService<HabitService>();
        var routine = await reminders.GetRoutineWithUserAsync(routineId, cancellationToken);
        if (routine?.SecondReminderUtc is not { } dueAt || dueAt > now || !_retries.CanAttempt(SecondRoutineKind, routineId, dueAt, now)) return;

        var user = routine.User;
        var tz = TimeZones.Find(user.Timezone) ?? TimeZoneInfo.Utc;
        var today = TimeZones.LocalToday(user.Timezone, _time.GetUtcNow());
        var discordId = user.DiscordId();
        var (targets, pushIds) = Targets(user, routine.ReminderExclusions);

        List<RoutineHabit> pending = [];
        if (targets.Count > 0 && routine.ReminderTime is not null
            && ReminderPolicy.IsSecondStillDue(user.SecondReminderMinutes, dueAt, now, today, tz))
        {
            var lowDays = await habits.GetLowDaysAsync(user.Id);
            pending = HabitRules.PendingRoutineHabits(routine.Habits, today, tz, lowDays)
                .Select(h => new RoutineHabit(h.Id, h.Name))
                .ToList();
        }

        var send = pending.Count > 0;
        if (send)
        {
            if (Reachable(targets) is not { } reachable) return;
            targets = reachable;
        }
        var delivery = send
            ? await DeliverAsync(targets, new Reminder(user.Id, discordId, routine.Id, routine.Name, 0, false, today, pending, Second: true,
                PushSubscriptionIds: pushIds, Language: user.Language),
                user, now, (SecondRoutineKind, routine.Id, dueAt), cancellationToken)
            : Delivery.NotDelivered;

        if (delivery != Delivery.Retry) routine.SecondReminderUtc = null;
        await reminders.SaveChangesAsync(send ? CancellationToken.None : cancellationToken);
        if (delivery != Delivery.Retry) _retries.Forget(SecondRoutineKind, routineId, dueAt);
    }

    /// <summary>The habit's completion, week count and low day on the user's local <paramref name="today"/>, for <see cref="ReminderPolicy"/>.</summary>
    private static async Task<ReminderDayFacts> DayFactsAsync(ReminderService reminders, HabitService habits, Habit habit, DateOnly today,
        CancellationToken cancellationToken)
        => new(await reminders.IsCompletedOnAsync(habit.Id, today, cancellationToken),
            await reminders.CountCompletedInWeekAsync(habit.Id, today, cancellationToken),
            await habits.IsLowDayStoredAsync(habit.User.Id, today));

    private async Task ClearSecondAsync(Func<ReminderService, Task> clear)
    {
        try
        {
            using var scope = _services.CreateScope();
            await clear(scope.ServiceProvider.GetRequiredService<ReminderService>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not drop a second reminder.");
        }
    }

    private async Task ProcessAsync(int habitId, DateTime now, CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var reminders = scope.ServiceProvider.GetRequiredService<ReminderService>();
        var habits = scope.ServiceProvider.GetRequiredService<HabitService>();
        var habit = await reminders.GetHabitWithUserAsync(habitId, cancellationToken);
        if (habit?.NextReminderUtc is not { } dueAt || dueAt > now) return;

        var tz = TimeZones.Find(habit.User.Timezone) ?? TimeZoneInfo.Utc;
        var today = TimeZones.LocalToday(habit.User.Timezone, _time.GetUtcNow());

        var user = habit.User;
        var discordId = user.DiscordId();
        var (targets, pushIds) = Targets(user, habit.ReminderExclusions);
        var letGo = habit.Kind == Core.Models.HabitKind.LetGo;
        var send = targets.Count > 0
            && ReminderPolicy.ShouldSendReminder(habit, dueAt, now, today, await DayFactsAsync(reminders, habits, habit, today, cancellationToken));
        if (send)
        {
            if (Reachable(targets) is not { } reachable) return;
            targets = reachable;
        }

        DateTime? secondUtc = null;
        var delivery = Delivery.NotDelivered;
        if (send && letGo)
        {
            delivery = await DeliverAsync(targets, new Reminder(user.Id, discordId, habit.Id, habit.Name, 0, false, today,
                PushSubscriptionIds: pushIds, Language: user.Language, LetGo: true), user, now, (HabitKind, habit.Id, dueAt), cancellationToken);
        }
        else if (send)
        {
            var weekAgo = today.AddDays(-7);
            var recent = await reminders.CountCompletedBetweenAsync(habit.Id, weekAgo, today, cancellationToken);
            var snoozed = TimeZones.NextOccurrenceUtc(habit.ReminderTimes, tz, dueAt.AddTicks(-1)) != dueAt;
            var reminder = new Reminder(user.Id, discordId, habit.Id, habit.Name, recent, snoozed, today, PushSubscriptionIds: pushIds,
                Language: user.Language);
            delivery = await DeliverAsync(targets, reminder, user, now, (HabitKind, habit.Id, dueAt), cancellationToken);
            if (delivery == Delivery.Delivered && !snoozed && ReminderPolicy.SecondReminderAt(user.SecondReminderMinutes, tz, today, now) is { } second
                && (TimeZones.NextOccurrenceUtc(habit.ReminderTimes, tz, now) is not { } next || next > second))
                secondUtc = second;
        }

        var saveToken = send ? CancellationToken.None : cancellationToken;
        if (delivery == Delivery.Retry)
        {
            habit.SecondReminderUtc = null;
            await reminders.SaveChangesAsync(saveToken);
            return;
        }

        if (!await reminders.SaveAndAdvanceReminderAsync(habit.Id, dueAt, TimeZones.NextOccurrenceUtc(habit.ReminderTimes, tz, now), secondUtc,
                saveToken))
            _logger.LogInformation("Reminder for habit {HabitId} changed while it was sent; keeping the change.", habitId);
        _retries.Forget(HabitKind, habitId, dueAt);
    }

    private async Task ProcessRoutineAsync(int routineId, DateTime now, CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var reminders = scope.ServiceProvider.GetRequiredService<ReminderService>();
        var habits = scope.ServiceProvider.GetRequiredService<HabitService>();
        var routine = await reminders.GetRoutineWithUserAsync(routineId, cancellationToken);
        if (routine?.NextReminderUtc is not { } dueAt || dueAt > now) return;

        var user = routine.User;
        var tz = TimeZones.Find(user.Timezone) ?? TimeZoneInfo.Utc;
        var today = TimeZones.LocalToday(user.Timezone, _time.GetUtcNow());
        var discordId = user.DiscordId();
        var (targets, pushIds) = Targets(user, routine.ReminderExclusions);

        List<RoutineHabit> pending = [];
        if (targets.Count > 0 && !ReminderPolicy.IsStale(dueAt, now))
        {
            var lowDays = await habits.GetLowDaysAsync(user.Id);
            pending = HabitRules.PendingRoutineHabits(routine.Habits, today, tz, lowDays)
                .Select(h => new RoutineHabit(h.Id, h.Name))
                .ToList();
        }

        var send = pending.Count > 0;
        if (send)
        {
            if (Reachable(targets) is not { } reachable) return;
            targets = reachable;
        }
        var delivery = send
            ? await DeliverAsync(targets, new Reminder(user.Id, discordId, routine.Id, routine.Name, 0, false, today, pending,
                PushSubscriptionIds: pushIds, Language: user.Language), user, now,
                (RoutineKind, routine.Id, dueAt), cancellationToken)
            : Delivery.NotDelivered;
        var saveToken = send ? CancellationToken.None : cancellationToken;
        if (delivery == Delivery.Retry)
        {
            routine.SecondReminderUtc = null;
            await reminders.SaveChangesAsync(saveToken);
            return;
        }

        var next = routine.ReminderTime is { } reminderTime ? TimeZones.NextOccurrenceUtc(reminderTime, tz, now) : (DateTime?)null;
        var secondUtc = delivery == Delivery.Delivered ? ReminderPolicy.SecondReminderAt(user.SecondReminderMinutes, tz, today, now) : null;
        if (!await reminders.SaveAndAdvanceRoutineReminderAsync(routine.Id, dueAt, next, secondUtc, saveToken))
            _logger.LogInformation("Reminder for routine {RoutineId} changed while it was sent; keeping the change.", routineId);
        _retries.Forget(RoutineKind, routineId, dueAt);
    }

    /// <summary>
    /// The registered channels that reach at least one of the item's server destinations (<see cref="ReminderDestinations.Resolve"/>),
    /// and the push subscriptions web push may send to.
    /// </summary>
    private (List<IReminderChannel> Channels, IReadOnlyList<int> PushSubscriptionIds) Targets(User user, IReadOnlyCollection<string> exclusions)
    {
        var targets = ReminderDestinations.Resolve(user, exclusions);
        return (_channels.Where(c => c.Channel switch
        {
            ReminderChannels.Discord => targets.Discord,
            ReminderChannels.WebPush => targets.PushSubscriptionIds.Count > 0,
            _ => false,
        }).ToList(), targets.PushSubscriptionIds);
    }

    /// <summary>
    /// The <paramref name="targets"/> a reminder can go to now: all of them while the Discord client is logged in, else those besides
    /// Discord. Null when Discord was the only one, so the caller leaves the reminder due and unchanged until the login succeeds.
    /// </summary>
    private List<IReminderChannel>? Reachable(List<IReminderChannel> targets)
    {
        if (_discord.IsLoggedIn || targets.All(c => c.Channel != ReminderChannels.Discord)) return targets;
        var others = targets.Where(c => c.Channel != ReminderChannels.Discord).ToList();
        return others.Count > 0 ? others : null;
    }

    /// <summary>
    /// Sends the reminder to each target. <see cref="Delivery.Retry"/> when no channel delivered it, a send failed transiently and
    /// <paramref name="key"/> has attempts left, so the caller keeps it due and changes nothing else. Otherwise the final Discord
    /// outcome updates the user's DM failure count and pause, once per due reminder: undeliverable, or transient failures until the
    /// last attempt when no other channel delivered it, count as one failure.
    /// </summary>
    private async Task<Delivery> DeliverAsync(List<IReminderChannel> targets, Reminder reminder, User user, DateTime now,
        (string Kind, int Id, DateTime DueAt) key, CancellationToken cancellationToken)
    {
        var delivered = false;
        var transient = false;
        DeliveryOutcome? discord = null;
        var discordTransient = false;
        foreach (var channel in targets)
        {
            var (outcome, isTransient) = await SendAsync(channel, reminder, cancellationToken);
            delivered |= outcome == DeliveryOutcome.Delivered;
            transient |= isTransient;
            if (channel.Channel != ReminderChannels.Discord) continue;
            discord = outcome;
            discordTransient = isTransient;
        }

        if (delivered) _retries.Delivered(key.Kind, key.Id, key.DueAt, now);
        else if (transient && !_retries.Fail(key.Kind, key.Id, key.DueAt, now)) return Delivery.Retry;

        if (discord == DeliveryOutcome.Delivered)
        {
            user.DmFailureCount = 0;
        }
        else if ((discord is not null || (discordTransient && !delivered)) && !reminder.Second && ++user.DmFailureCount >= MaxConsecutiveDmFailures)
        {
            user.DmRemindersPausedAt = now;
            _logger.LogWarning("Pausing DM reminders for user {UserId} after {Count} undeliverable reminders.",
                user.Id, user.DmFailureCount);
        }
        return delivered ? Delivery.Delivered : Delivery.NotDelivered;
    }

    private async Task DeferAsync(int habitId, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ReminderService>()
                .DeferReminderAsync(habitId, now, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not defer the reminder for habit {HabitId}.", habitId);
        }
    }

    private async Task DeferRoutineAsync(int routineId, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ReminderService>()
                .DeferRoutineReminderAsync(routineId, now, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not defer the reminder for routine {RoutineId}.", routineId);
        }
    }

    /// <summary>The channel's outcome, or null and whether the failure <see cref="ReminderRetries.IsTransient"/> when the send threw.</summary>
    private async Task<(DeliveryOutcome? Outcome, bool Transient)> SendAsync(IReminderChannel channel, Reminder reminder,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await channel.SendAsync(reminder, cancellationToken);
            if (outcome != DeliveryOutcome.Delivered)
                _logger.LogWarning("{Channel} reminder for habit {HabitId} to user {UserId} was not delivered: {Outcome}.",
                    channel.Channel, reminder.ItemId, reminder.UserId, outcome);
            return (outcome, false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send {Channel} reminder for habit {HabitId} to user {UserId}.",
                channel.Channel, reminder.ItemId, reminder.UserId);
            return (null, ReminderRetries.IsTransient(ex));
        }
    }
}
