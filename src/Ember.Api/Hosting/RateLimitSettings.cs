namespace Ember.Api.Hosting;

/// <summary>Limits from <c>Ember:RateLimits</c>.</summary>
internal sealed class RateLimitSettings
{
    public int AuthPerIpPerMinute { get; set; } = 30;
    public int AuthOtherPerIpPerMinute { get; set; } = 300;
    public int WidgetPerIpPerMinute { get; set; } = 60;
    public int MailPerEmailPerHour { get; set; } = 5;
    public int MailPerEmailAnyClientPerHour { get; set; } = 20;
    public int MailPerIpPerHour { get; set; } = 10;
    public int MailPerDay { get; set; } = 200;
    public int FeedbackPerHour { get; set; } = 5;
    public int FeedbackPerDay { get; set; } = 200;
    public int ExportPerHour { get; set; } = 10;
}
