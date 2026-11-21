using Discord.Interactions;
using Discord.WebSocket;
using Ember.Bot;
using Ember.Bot.Jobs;
using Ember.Bot.Services;
using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Impl.Matchers;

namespace Ember.Tests;

[Collection("Integration")]
public class BotHostStartTests
{
    [Fact]
    public async Task Every_scheduled_job_can_be_constructed_from_the_started_host()
    {
        await using var host = await IntegrationTestHost.CreateAsync();

        var scheduler = await host.WithScopeAsync(sp => sp.GetRequiredService<ISchedulerFactory>().GetScheduler());
        var keys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
        var jobTypes = new List<Type>();
        foreach (var key in keys)
            jobTypes.Add((await scheduler.GetJobDetail(key))!.JobType);

        jobTypes.Should().HaveCountGreaterThan(0);
        await host.WithScopeAsync(sp =>
        {
            foreach (var type in jobTypes)
                ActivatorUtilities.CreateInstance(sp, type).Should().BeAssignableTo<IJob>(type.Name);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task With_a_failing_Discord_login_the_host_still_starts_and_the_reminder_dispatch_still_runs()
    {
        var loginFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await IntegrationTestHost.CreateAsync(args: ["--BotToken=test-token"], includeDiscordGateway: true,
            configureServices: services =>
            {
                services.Remove(services.Single(d => d.ImplementationType == typeof(BotService)));
                services.AddHostedService(sp => new BotService(
                    sp.GetRequiredService<DiscordSocketClient>(), sp.GetRequiredService<InteractionService>(), sp,
                    sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ILogger<BotService>>(),
                    sp.GetRequiredService<NoticePoller>(),
                    _ => { loginFailed.TrySetResult(); throw new HttpRequestException("Discord is unreachable"); }));
            });
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var user = new User { Id = TestUsers.Id(920), Timezone = "UTC", Logins = TestUsers.Discord(920), DiscordReminders = true };
            db.Users.Add(user);
            db.Habits.Add(new Habit
            {
                UserId = user.Id, Name = "Stretch", ReminderTime = new TimeOnly(9, 0),
                NextReminderUtc = DateTime.UtcNow.AddMinutes(-1), CreatedAt = DateTime.UtcNow.AddDays(-1),
            });
            await db.SaveChangesAsync();
        });

        await loginFailed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await host.RunJobAsync(ReminderDispatchJob.Key);

        host.Discord.Messages.Should().ContainSingle().Which.UserId.Should().Be(920UL);
    }

    [Fact]
    public async Task Deleting_an_account_through_the_hosts_habit_service_is_recorded_in_the_deletion_ledger()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ember-ledger-{Guid.NewGuid():N}.jsonl");
        var userId = TestUsers.Id(910);
        try
        {
            await using var host = await IntegrationTestHost.CreateAsync(args: [$"--{DeletionLedger.FileSetting}={path}"]);
            await host.WithScopeAsync(async sp =>
            {
                var db = sp.GetRequiredService<EmberDbContext>();
                db.Users.Add(new User { Id = userId, Timezone = "UTC", Logins = TestUsers.Discord(910) });
                await db.SaveChangesAsync();
            });

            await host.WithScopeAsync(sp => sp.GetRequiredService<AccountService>().DeleteUserDataAsync(userId));

            DeletionLedger.Read(path).Should().ContainSingle(e => e.Kind == DeletionLedger.Account)
                .Which.UserId.Should().Be(userId);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
