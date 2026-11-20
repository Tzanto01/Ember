using Ember.Bot;
using Ember.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args is ["--migrate-only"])
{
    using var migrator = AppHost.CreateHostBuilder([]).Build();
    using var scope = migrator.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<EmberDbContext>().Database.MigrateAsync();
    return;
}

var host = AppHost.CreateHostBuilder(args).Build();
await AppHost.InitializeAsync(host);

await host.RunAsync();
