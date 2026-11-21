using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Ember.Bot.Jobs;

/// <summary>
/// Runs daily and deletes email signups that were never confirmed within
/// <see cref="AccountService.UnconfirmedAccountLifetime"/> (see <see cref="AccountService.DeleteStaleUnconfirmedAccountsAsync"/>),
/// expired sign-in sessions (see <see cref="AccountService.DeleteExpiredSessionsAsync"/>) and expired reminder action tokens
/// (see <see cref="ReminderService.DeleteExpiredReminderActionTokensAsync"/>) and unsent diagnostics uploads past
/// <see cref="LookoutDiagnostic.MaxUnsentAge"/> (see <see cref="LookoutDiagnostic.DeleteExpiredAsync"/>), whether or not the Lookout
/// sender runs, and prunes the <see cref="DeletionLedger"/>.
/// </summary>
[DisallowConcurrentExecution]
public class UnconfirmedAccountCleanupJob : IJob
{
    public static readonly JobKey Key = new("unconfirmed-account-cleanup", "accounts");

    private readonly IServiceProvider _services;
    private readonly ILogger<UnconfirmedAccountCleanupJob> _logger;

    public UnconfirmedAccountCleanupJob(IServiceProvider services, ILogger<UnconfirmedAccountCleanupJob> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = _services.CreateScope();
        var reminders = scope.ServiceProvider.GetRequiredService<ReminderService>();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
        var deleted = await accounts.DeleteStaleUnconfirmedAccountsAsync(context.CancellationToken);
        if (deleted > 0)
            _logger.LogInformation("UnconfirmedAccountCleanupJob: deleted {Count} unconfirmed accounts.", deleted);
        var sessions = await accounts.DeleteExpiredSessionsAsync(context.CancellationToken);
        if (sessions > 0)
            _logger.LogInformation("UnconfirmedAccountCleanupJob: deleted {Count} expired sessions.", sessions);
        var tokens = await reminders.DeleteExpiredReminderActionTokensAsync(context.CancellationToken);
        if (tokens > 0)
            _logger.LogInformation("UnconfirmedAccountCleanupJob: deleted {Count} expired reminder action tokens.", tokens);
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var diagnostics = await LookoutDiagnostic.DeleteExpiredAsync(
            scope.ServiceProvider.GetRequiredService<EmberDbContext>().LookoutDiagnostics, now, context.CancellationToken);
        if (diagnostics > 0)
            _logger.LogInformation("UnconfirmedAccountCleanupJob: deleted {Count} diagnostics uploads unsent for 30 days.", diagnostics);
        var pruned = scope.ServiceProvider.GetRequiredService<DeletionLedger>().Prune();
        if (pruned > 0)
            _logger.LogInformation("UnconfirmedAccountCleanupJob: pruned {Count} deletion ledger entries.", pruned);
    }
}
