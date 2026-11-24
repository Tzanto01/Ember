using System.Threading.Channels;
using Ember.Api.Feedback;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ember.Api.Tests;

public sealed class FeedbackRetentionServiceTests(PostgresFixture postgres) : FeedbackRetentionTestBase(postgres)
{
    private readonly CapturingLogger<FeedbackRetentionService> _logs = new();
    private readonly ManualTime _time = new();

    private FeedbackRetentionService Service(IServiceScopeFactory scopes, string setting, string value)
        => new(scopes, new ConfigurationBuilder().AddInMemoryCollection([new(setting, value)]).Build(), _time, _logs);

    [DockerFact]
    public async Task It_runs_daily_at_8_in_Berlin_and_a_failed_run_does_not_end_it()
    {
        await Report("old", age: TimeSpan.FromDays(400));
        var scopes = new FailingOnce(Host.Services.GetRequiredService<IServiceScopeFactory>());
        using var service = Service(scopes, FeedbackRetentionService.EnabledSetting, "true");
        _time.Now = new DateTimeOffset(2026, 10, 24, 5, 0, 0, TimeSpan.Zero);
        await service.StartAsync(default);

        (await _time.NextTimer()).Should().Be(TimeSpan.FromHours(1));
        _time.Fire();
        (await _time.NextTimer()).Should().Be(TimeSpan.FromHours(25), "Berlin leaves summer time that night");
        (await Titles()).Should().Equal("old");
        _logs.All.Should().Contain("Error: The feedback retention failed");
        _time.Fire();
        (await _time.NextTimer()).Should().Be(TimeSpan.FromHours(24));

        (await Titles()).Should().BeEmpty();
        await service.StopAsync(default);
    }

    [DockerFact]
    public async Task Held_back_it_runs_nothing_and_says_so()
    {
        using var service = Service(Host.Services.GetRequiredService<IServiceScopeFactory>(), FeedbackRetentionService.EnabledSetting, "false");
        await service.StartAsync(default);

        await service.ExecuteTask!;

        _logs.All.Should().StartWith("Warning: Feedback retention is off");
        _time.Timers.Should().Be(0);
    }

    [DockerFact]
    public void A_setting_that_is_no_boolean_stops_it_before_anything_runs()
    {
        var scopes = Host.Services.GetRequiredService<IServiceScopeFactory>();

        FluentActions.Invoking(() => Service(scopes, FeedbackRetentionService.EnabledSetting, "0")).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => Service(scopes, FeedbackRetention.DeleteSentCopiesSetting, "yes")).Should().Throw<InvalidOperationException>();
    }

    private sealed class FailingOnce(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        private int _calls;

        public IServiceScope CreateScope() => _calls++ == 0 ? throw new InvalidOperationException("synthetic") : inner.CreateScope();
    }

    private sealed class ManualTime : TimeProvider
    {
        private readonly Channel<TimeSpan> _due = Channel.CreateUnbounded<TimeSpan>();
        private Action _fire = () => { };
        private TimeSpan _last;

        public DateTimeOffset Now { get; set; }

        public int Timers { get; private set; }

        public override DateTimeOffset GetUtcNow() => Now;

        public async Task<TimeSpan> NextTimer() => _last = await _due.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        public void Fire()
        {
            Now += _last;
            _fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timers++;
            _fire = () => callback(state);
            _due.Writer.TryWrite(dueTime);
            return System.CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }
}
