using DotNet.Testcontainers.Configurations;
using Xunit;

namespace Ember.TestKit;

/// <summary>
/// Fact that needs Docker: skipped locally when Docker is unreachable, but never skipped when <c>CI=true</c>,
/// so a CI runner without Docker fails instead of silently passing.
/// </summary>
public sealed class DockerFactAttribute : FactAttribute
{
    /// <summary>Whether Docker-backed tests run in this process.</summary>
    public static readonly bool ShouldRun =
        TestcontainersSettings.OS.DockerEndpointAuthConfig is not null
        || string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    public DockerFactAttribute()
    {
        if (!ShouldRun)
            Skip = "Docker is not available (set CI=true to force these tests to run).";
    }
}
