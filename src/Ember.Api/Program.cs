using Ember.Api;
using Ember.Api.Feedback;
using Ember.Api.Hosting;
using Ember.Api.Hosting.Metrics;
using Ember.Api.Lookout;
using Ember.Api.Operations;

if (args is ["--healthcheck"])
{
    var port = (Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080").Split([',', ';'])[0];
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        return (await http.GetAsync($"http://127.0.0.1:{port}/health")).IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception)
    {
        return 1;
    }
}

if (FeedbackCli.Handles(args)) return await FeedbackCli.RunAsync(args);
if (DeletionReplayCli.Handles(args)) return await DeletionReplayCli.RunAsync(args);

if (args is ["--lookout-sender"])
{
    await LookoutSender.BuildHost().RunAsync();
    return 0;
}

var app = ApiApp.Build(ApiMetrics.Add(ApiApp.CreateBuilder(args)));
app.Run();
return 0;

public partial class Program { }
