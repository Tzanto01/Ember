using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Impl.Matchers;

namespace Ember.Tests;

/// <summary>What the host schedules: a job that loses its trigger or changes its cron or time zone silently stops or shifts a user-facing DM.</summary>
[Collection("Integration")]
public class JobWiringGoldenTests
{
    [Fact]
    public async Task RegisteredQuartzJobs_KeepTheirTriggerKeysCronsTimeZonesAndConcurrencyRule()
    {
        await using var host = await IntegrationTestHost.CreateAsync();
        var scheduler = await host.WithScopeAsync(sp => sp.GetRequiredService<ISchedulerFactory>().GetScheduler());

        var lines = new List<string>();
        foreach (var key in (await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup())).OrderBy(k => k.ToString(), StringComparer.Ordinal))
        {
            var detail = (await scheduler.GetJobDetail(key))!;
            lines.Add($"job {key} concurrent={(detail.ConcurrentExecutionDisallowed ? "disallowed" : "allowed")}");
            foreach (var trigger in (await scheduler.GetTriggersOfJob(key)).OrderBy(t => t.Key.ToString(), StringComparer.Ordinal))
                lines.Add(trigger is ICronTrigger cron
                    ? $"  trigger {trigger.Key} cron \"{cron.CronExpressionString}\" tz={cron.TimeZone.Id}"
                    : $"  trigger {trigger.Key} {(trigger is ISimpleTrigger simple ? $"every {simple.RepeatInterval}" : trigger.GetType().Name)}");
        }

        GoldenFile.AssertMatches("quartz-jobs.txt", string.Join('\n', lines) + "\n");
    }
}
