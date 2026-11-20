using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ember.Tests;

[Collection("Integration")]
public class DiscordUserTests
{
    [Fact]
    public async Task ResolveOrCreate_MakesABotOnlyIdentityUser_WithItsDiscordLogin()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false);

        var userId = await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().ResolveOrCreateDiscordUserAsync(123456789012345678));

        var user = await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Users
            .Include(u => u.Logins).AsNoTracking().SingleAsync(u => u.Id == userId));
        user.DiscordId().Should().Be(123456789012345678UL);
        user.UserName.Should().Be("discord_123456789012345678");
        user.NormalizedUserName.Should().Be("DISCORD_123456789012345678");
        user.SecurityStamp.Should().NotBeNullOrEmpty();
        user.ConcurrencyStamp.Should().NotBeNullOrEmpty();
        user.Email.Should().BeNull();
        user.PasswordHash.Should().BeNull();

        var again = await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().ResolveOrCreateDiscordUserAsync(123456789012345678));
        again.Should().Be(userId);
    }

    [Fact]
    public async Task ReadsAndDelete_ForAnUnknownDiscordUser_NeverCreateAUser()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false);

        await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            (await service.FindDiscordUserAsync(55)).Should().Be(Guid.Empty);
            (await service.GetHabitsAsync(await TestUsers.UserIdAsync(sp, 55))).Should().BeEmpty();
            (await sp.GetRequiredService<ConsentService>().HasHealthDataConsentAsync(await TestUsers.UserIdAsync(sp, 55))).Should().BeFalse();
            (await sp.GetRequiredService<UserSettingsService>().GetGraceDaysAsync(await TestUsers.UserIdAsync(sp, 55))).Should().Be(1);
            var accounts = sp.GetRequiredService<AccountService>();
            (await accounts.GetUserForExportAsync(await service.FindDiscordUserAsync(55))).Should().BeNull();
            await accounts.DeleteUserDataAsync(await service.FindDiscordUserAsync(55));
        });

        (await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Users.CountAsync())).Should().Be(0);
    }
}
