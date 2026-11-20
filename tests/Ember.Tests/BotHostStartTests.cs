using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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

            await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().DeleteUserDataAsync(userId));

            DeletionLedger.Read(path).Should().ContainSingle(e => e.Kind == DeletionLedger.Account)
                .Which.UserId.Should().Be(userId);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
