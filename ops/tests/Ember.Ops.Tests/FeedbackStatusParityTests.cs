using Ember.Core.Data;
using Ember.Core.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Ember.Ops.Tests;

public sealed class FeedbackStatusParityTests
{
    [Fact]
    public void The_board_columns_are_exactly_the_statuses_Ember_stores()
    {
        using var db = new EmberDbContext(new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql("Host=unused").Options);
        var converter = db.Model.FindEntityType(typeof(Feedback))!.FindProperty(nameof(Feedback.Status))!.GetValueConverter()!;

        var stored = Enum.GetValues<FeedbackStatus>().Select(s => (string)converter.ConvertToProvider(s)!);

        FeedbackStore.Statuses.Should().BeEquivalentTo(stored);
    }
}
