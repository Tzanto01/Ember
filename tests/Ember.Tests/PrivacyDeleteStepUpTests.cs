using Ember.Bot;
using Ember.Bot.Modules;
using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ember.Tests;

[Collection("Integration")]
public class PrivacyDeleteStepUpTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ConfirmByOwner_OfAnAccountWithAPasswordOrTwoStepSignIn_DeletesNothing(bool password, bool twoFactor)
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false);
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var user = new User
            {
                Id = TestUsers.Id(1),
                Logins = TestUsers.Discord(1),
                Email = "someone@example.test",
                PasswordHash = password ? "synthetic-hash" : null,
                TwoFactorEnabled = twoFactor,
            };
            user.Habits.Add(new Habit { Name = "Walk" });
            db.Users.Add(user);
            await db.SaveChangesAsync();
        });

        var outcome = await host.WithScopeAsync(sp =>
            PrivacyModule.TryDeleteAsync(sp.GetRequiredService<HabitService>(), sp.GetRequiredService<AccountService>(), "1", clickerId: 1));

        outcome.Should().Be(PrivacyModule.DeleteOutcome.NeedsWebApp);

        var left = await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            return (Users: await db.Users.CountAsync(), Habits: await db.Habits.CountAsync());
        });
        left.Should().Be((1, 1));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void ProtectedAccountReply_LinksToDeletionInTheWebAppSettings(string language)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ember:WebOrigin"] = "https://ember.example.test/" })
            .Build();

        PrivacyModule.WebAppDeleteText(BotTexts.For(language), config).Should().Contain("https://ember.example.test/settings#delete");
    }

    [Fact]
    public void ProtectedAccountReply_WithoutAWebOrigin_PointsToTheWebAppSettings()
    {
        var reply = PrivacyModule.WebAppDeleteText(BotTexts.For("en"), new ConfigurationBuilder().Build());

        reply.Should().Contain("Settings").And.NotContain("{0}");
    }
}
