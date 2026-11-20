using Ember.Core.Data;
using Ember.Core.Privacy;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Ember.Tests;

/// <summary>Holds the EF model to the data-lifecycle registry, so every new entity gets its withdrawal, import, export and merge decided.</summary>
public class DataLifecycleTests
{
    [Fact]
    public void Every_entity_type_of_the_model_is_registered_exactly_once()
    {
        using var db = new EmberDbContext(new DbContextOptionsBuilder<EmberDbContext>().UseSqlite("Data Source=:memory:").Options);
        var mapped = db.Model.GetEntityTypes().Select(t => t.ClrType).ToList();

        DataLifecycle.Entities.Select(e => e.Entity).Should().OnlyHaveUniqueItems()
            .And.BeEquivalentTo(mapped, "a new entity needs its owner, withdrawal, import, export and merge rule in DataLifecycle");
    }

    [Fact]
    public void Every_entity_that_is_never_exported_says_why()
        => DataLifecycle.Entities.Where(e => e.Export == ExportRule.Never).Should().OnlyContain(e => e.ExportNote.Length > 0);
}
