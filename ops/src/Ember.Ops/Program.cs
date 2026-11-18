using Ember.Ops;

if (args is ["--healthcheck"])
{
    var port = (Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080").Split([',', ';'])[0];
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        return (await http.GetAsync($"http://127.0.0.1:{port}/healthz")).IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception)
    {
        return 1;
    }
}

var app = OpsApp.Build(OpsApp.CreateBuilder(args));
await OpsApp.InitializeAsync(app);
await app.RunAsync();
return 0;

/// <summary>Entry point, public for <c>WebApplicationFactory</c>.</summary>
public partial class Program;
