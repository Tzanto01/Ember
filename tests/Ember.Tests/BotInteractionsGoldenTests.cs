using System.Reflection;
using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Ember.Bot;
using Microsoft.Extensions.DependencyInjection;

namespace Ember.Tests;

/// <summary>
/// Messages already sent carry component and modal custom IDs and users have slash commands memorized, so renaming any of them
/// breaks old buttons and habits. The golden lists the registered surface without wording and without the handler classes.
/// </summary>
[Collection("Integration")]
public class BotInteractionsGoldenTests
{
    [Fact]
    public async Task RegisteredSlashCommands_ComponentIds_AndModalIds_MatchTheReviewedGolden()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false);
        using var scope = host.Host.Services.CreateScope();
        using var interactions = new InteractionService(new DiscordSocketClient(), AppHost.InteractionConfig());
        await interactions.AddModulesAsync(typeof(AppHost).Assembly, scope.ServiceProvider);

        var toProps = typeof(InteractionService).Assembly.GetType("Discord.Interactions.ApplicationCommandRestUtil")!
            .GetMethod("ToApplicationCommandProps", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, [typeof(ModuleInfo), typeof(bool)])!;
        var commands = interactions.Modules.Where(m => !m.IsSubModule)
            .SelectMany(m => (IReadOnlyCollection<ApplicationCommandProperties>)toProps.Invoke(null, [m, false])!)
            .ToList();

        var lines = new List<string>();
        foreach (var command in commands.OfType<SlashCommandProperties>().OrderBy(c => c.Name.Value, StringComparer.Ordinal))
        {
            lines.Add($"slash /{command.Name.Value}");
            AppendOptions(lines, command.Options.IsSpecified ? command.Options.Value : null, "  ");
        }
        foreach (var command in commands.Where(c => c is not SlashCommandProperties).OrderBy(c => c.Name.Value, StringComparer.Ordinal))
            lines.Add($"{(command is UserCommandProperties ? "user-command" : "message-command")} {command.Name.Value}");
        lines.AddRange(interactions.ComponentCommands.Select(c => $"component {c.Name}").Order(StringComparer.Ordinal));
        lines.AddRange(interactions.ModalCommands.Select(c => $"modal {c.Name}").Order(StringComparer.Ordinal));

        GoldenFile.AssertMatches("bot-interactions.txt", string.Join('\n', lines) + "\n");
    }

    private static void AppendOptions(List<string> lines, IEnumerable<ApplicationCommandOptionProperties>? options, string indent)
    {
        var list = options?.ToList() ?? [];
        if (list.All(o => o.Type is ApplicationCommandOptionType.SubCommand or ApplicationCommandOptionType.SubCommandGroup))
            list = [.. list.OrderBy(o => o.Name, StringComparer.Ordinal)];
        foreach (var option in list)
        {
            var details = new StringBuilder($"{indent}{option.Name}: {option.Type}");
            if (option.IsRequired == true) details.Append(" required");
            if (option.IsAutocomplete == true) details.Append(" autocomplete");
            if (option.Type is ApplicationCommandOptionType.Integer or ApplicationCommandOptionType.Number && option.MinValue is { } min) details.Append($" min={min}");
            if (option.Type is ApplicationCommandOptionType.Integer or ApplicationCommandOptionType.Number && option.MaxValue is { } max) details.Append($" max={max}");
            if (option.MinLength is { } minLength) details.Append($" minLength={minLength}");
            if (option.MaxLength is { } maxLength) details.Append($" maxLength={maxLength}");
            if (option.Choices is { Count: > 0 } choices) details.Append($" choices=[{string.Join(", ", choices.Select(c => c.Value.ToString()))}]");
            lines.Add(details.ToString());
            AppendOptions(lines, option.Options, indent + "  ");
        }
    }
}
