using Discord;
using Discord.Interactions;
using Ember.Bot.Discord.CustomIds;
using Ember.Bot.Discord.Messages;
using Ember.Core.Localization;
using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;
using Microsoft.Extensions.Configuration;

namespace Ember.Bot.Modules;

/// <summary>
/// Handles the select-menu interaction fired by /habit template.
/// Custom ID: habit:template:select
/// </summary>
public class TemplateSelectModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly HabitService _habits;
    private readonly ConsentService _consent;
    private readonly UserSettingsService _settings;
    private readonly IConfiguration _config;

    public TemplateSelectModule(HabitService habits, ConsentService consent, UserSettingsService settings, IConfiguration config)
    {
        _habits = habits;
        _consent = consent;
        _settings = settings;
        _config = config;
    }

    /// <summary>The template's name with its emoji (<c>☀️ Morning start</c>) in the user's language.</summary>
    internal static string Name(Texts t, HabitTemplates.Template template) => t.Get($"template.{template.Key}.name");

    /// <summary>The template's one-line description in the user's language.</summary>
    internal static string Description(Texts t, HabitTemplates.Template template) => t.Get($"template.{template.Key}.description");

    [ComponentInteraction(HabitIds.TemplateSelect)]
    public async Task HandleTemplateSelectAsync(string[] selected)
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var key = selected[0];
        var template = HabitTemplates.Find(key);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);

        if (template is null)
        {
            await RespondAsync(t.Get("template.unknown"), ephemeral: true);
            return;
        }

        // Strip leading emoji + space. Emoji are multi-char in UTF-16 so we can't
        // use char literals — find the first space and take everything after it.
        var rawName = Name(t, template);
        var spaceIdx = rawName.IndexOf(' ');
        var cleanName = spaceIdx >= 0 ? rawName[(spaceIdx + 1)..].Trim() : rawName;

        var frequency = template.Frequency == HabitTemplates.FrequencyHint.Weekly
            ? FrequencyType.Weekly
            : FrequencyType.Daily;

        var (result, added) = await _habits.AddHabitAsync(
            userId,
            cleanName,
            reminderTime: null,
            frequency,
            template.WeeklyTarget);
        if (result == HabitWriteResult.ConsentRequired)
        {
            await ConsentPrompt.RespondAsync(Context, _config, _habits, _consent);
            return;
        }
        if (result == HabitWriteResult.LimitReached)
        {
            await RespondAsync(t.Format("habit.limit", Limits.HabitsPerUser), ephemeral: true);
            return;
        }
        var habit = added!;

        var hasTimezone = await _settings.HasTimezoneSetAsync(userId);

        var freqLabel = frequency == FrequencyType.Weekly
            ? t.Format("template.per-week", template.WeeklyTarget)
            : t.Get("habit.freq.daily");
        var reminderLabel = template.SuggestedReminderTime.ToString(t.Get("time.suggested"), t.Culture);

        var embed = new EmbedBuilder()
            .WithColor(0xE8873A)
            .WithTitle(t.Format("template.started.title", rawName))
            .WithDescription(t.Format("template.started.body", habit.Name, freqLabel, Description(t, template), reminderLabel))
            .WithFooter(t.Get("template.started.footer"))
            .Build();

        ComponentBuilder components;

        if (!hasTimezone)
        {
            components = new ComponentBuilder()
                .WithButton(t.Get("common.timezone-first"), OnboardingIds.Timezone(habit.Id), ButtonStyle.Primary, new Emoji("🌍"))
                .WithButton(t.Get("common.skip-for-now"), ReminderIds.Skip(habit.Id), ButtonStyle.Secondary);
        }
        else
        {
            components = new ComponentBuilder()
                .WithButton(t.Format("template.remind-at", reminderLabel), ReminderIds.Preset(habit.Id, template.SuggestedReminderTime), ButtonStyle.Primary, new Emoji("⏰"))
                .WithButton(t.Get("template.other-time"), ReminderIds.Set(habit.Id), ButtonStyle.Secondary)
                .WithButton(t.Get("template.no-reminder"), ReminderIds.Skip(habit.Id), ButtonStyle.Secondary);
        }

        components.WithButton(t.Get("common.days-and-times"), FrequencyIds.Menu(habit.Id), ButtonStyle.Secondary, new Emoji("📅"), row: 1);
        await RespondAsync(embed: embed, components: components.Build(), ephemeral: true);
        await StreaksQuestion.AskOnceAsync(Context.Interaction, _settings, userId, t);
    }
}
