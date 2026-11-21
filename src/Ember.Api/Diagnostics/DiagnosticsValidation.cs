using System.Globalization;
using System.Text.RegularExpressions;
using Ember.Api.Feedback;

namespace Ember.Api.Diagnostics;

/// <summary>The body of <c>POST /api/diagnostics</c>: what the Android app collected, plus the platform.</summary>
public sealed record DiagnosticsRequest(string? Installation, string? Platform, string? Version, string? Os, string? Device,
    DiagnosticsLaunch?[]? Launches, DiagnosticsExit?[]? Exits);

/// <summary>One launch event: the minute it happened and one event of the fixed vocabulary.</summary>
public sealed record DiagnosticsLaunch(string? At, string? Event);

/// <summary>One recent process exit: the minute, the exit reason, the signal of a native crash, exception type names and stack frames.</summary>
public sealed record DiagnosticsExit(string? At, string? Reason, string? Signal, string?[]? Exception, string?[]? Frames);

/// <summary>
/// The grammar of a diagnostics upload. Every pattern is ASCII-only and matches the whole value; anything outside the vocabulary,
/// the exit reasons and signals or the frame grammars is refused, never cleaned up. Error messages name the rule, never the value.
/// </summary>
internal static partial class DiagnosticsValidation
{
    public const int MaxLaunches = 50;
    public const int MaxExits = 5;
    public const int MaxExceptions = 10;
    public const int MaxFrames = 40;

    /// <summary>The oldest <c>at</c> accepted: no older than the server's now minus this, plus one minute.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>The newest <c>at</c> accepted: at most this after the server's now (device clock skew).</summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromHours(1);

    private const string AtFormat = "yyyy-MM-dd'T'HH:mm':00Z'";

    private static readonly HashSet<string> Reasons =
    [
        "UNKNOWN", "EXIT_SELF", "SIGNALED", "LOW_MEMORY", "CRASH", "CRASH_NATIVE", "ANR", "INITIALIZATION_FAILURE", "PERMISSION_CHANGE",
        "EXCESSIVE_RESOURCE_USAGE", "USER_REQUESTED", "USER_STOPPED", "DEPENDENCY_DIED", "OTHER", "FREEZER", "PACKAGE_STATE_CHANGE",
        "PACKAGE_UPDATED",
    ];

    private static readonly HashSet<string> Signals =
    [
        "SIGHUP", "SIGINT", "SIGQUIT", "SIGILL", "SIGTRAP", "SIGABRT", "SIGBUS", "SIGFPE", "SIGKILL", "SIGUSR1", "SIGSEGV", "SIGUSR2",
        "SIGPIPE", "SIGALRM", "SIGTERM", "SIGSTKFLT", "SIGCHLD", "SIGCONT", "SIGSTOP", "SIGTSTP", "SIGTTIN", "SIGTTOU", "SIGURG", "SIGXCPU",
        "SIGXFSZ", "SIGVTALRM", "SIGPROF", "SIGWINCH", "SIGIO", "SIGPWR", "SIGSYS",
    ];

    private static readonly Func<string, bool> Bool = v => v is "true" or "false";
    private static readonly Func<string, bool> Int = v => v.Length is >= 1 and <= 9 && v.All(char.IsAsciiDigit);
    private static readonly Func<string, bool> WebViewVersion
        = v => v.Length is >= 1 and <= 40 && v.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    private static readonly Func<string, bool> Source = OneOf("user", "notification", "system");

    /// <summary>The launch event vocabulary: each name with its keys in the order they must appear; any key may be left out.</summary>
    private static readonly Dictionary<string, (string Key, Func<string, bool> IsValue)[]> Events = new(StringComparer.Ordinal)
    {
        ["launch"] = [("cold", Bool), ("saved", Bool), ("source", Source), ("history", Bool), ("webview", WebViewVersion)],
        ["new_intent"] = [("source", Source)],
        ["page_loaded"] = [("ms", Int)],
        ["renderer_gone"] = [("crashed", Bool), ("priority", Int), ("then", OneOf("recreate", "finish", "none"))],
        ["route_ignored"] = [("reason", OneOf("restoring", "no_bridge", "from_history", "rejected"))],
        ["destroy"] = [("finishing", Bool), ("config_change", Bool)],
    };

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{16,64}\z")]
    private static partial Regex InstallationPattern();

    [GeneratedRegex(@"\AAndroid [A-Za-z0-9._]{1,20} \(API [0-9]{1,3}\)\z")]
    private static partial Regex OsPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9 ._+()-]{1,64}\z")]
    private static partial Regex DevicePattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_$.]{1,200}\z")]
    private static partial Regex ExceptionPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_$.<>/-]{1,300}\([A-Za-z0-9_$.: -]{0,200}\)\z")]
    private static partial Regex JavaFramePattern();

    [GeneratedRegex(@"\A#[0-9]+ pc [0-9a-f]+ [A-Za-z0-9_./-]+( \([A-Za-z0-9_:~<>,. -]+\))?\z")]
    private static partial Regex NativeFramePattern();

    /// <summary>
    /// The field-named errors of <paramref name="request"/> received at <paramref name="now"/>, empty when it is valid. A launch or
    /// exit whose <c>at</c> is before <c>now - 7 days + 1 minute</c> or after <c>now + 1 hour</c> is a <c>clock</c> error: the
    /// device clock is off, and dropping the entry would store something other than the preview the user saw.
    /// </summary>
    public static Dictionary<string, string[]> Errors(DiagnosticsRequest request, DateTime now)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Installation is not { } installation || !InstallationPattern().IsMatch(installation))
            errors["installation"] = ["16 to 64 letters, digits, _ or -."];
        if (request.Platform is not "android") errors["platform"] = ["Must be android."];
        if (request.Version is { } version && !FeedbackEndpoints.IsVersion(version))
            errors["version"] = [$"1 to {FeedbackEndpoints.MaxVersionLength} letters, digits or . + _ -, or null."];
        if (request.Os is not { } os || !OsPattern().IsMatch(os)) errors["os"] = ["Android {release} (API {level})."];
        if (request.Device is { } device && !DevicePattern().IsMatch(device))
            errors["device"] = ["1 to 64 letters, digits, spaces or . _ + ( ) -, or null."];

        if (request.Launches is not { } launches) errors["launches"] = ["Required."];
        else if (launches.Length > MaxLaunches) errors["launches"] = [$"At most {MaxLaunches}."];
        else
        {
            for (var i = 0; i < launches.Length; i++)
            {
                if (launches[i] is not { } launch) errors[$"launches[{i}]"] = ["Required."];
                else
                {
                    if (!TryParseAt(launch.At, out _)) errors[$"launches[{i}].at"] = ["A UTC minute, yyyy-MM-ddTHH:mm:00Z."];
                    if (launch.Event is not { } name || !IsEvent(name)) errors[$"launches[{i}].event"] = ["Not a known launch event."];
                }
            }
            if (!NewestFirst(launches.Select(l => l?.At))) errors["launches"] = ["Newest first."];
        }

        if (request.Exits is not { } exits) errors["exits"] = ["Required."];
        else if (exits.Length > MaxExits) errors["exits"] = [$"At most {MaxExits}."];
        else
        {
            for (var i = 0; i < exits.Length; i++)
            {
                if (exits[i] is not { } exit)
                {
                    errors[$"exits[{i}]"] = ["Required."];
                    continue;
                }
                if (!TryParseAt(exit.At, out _)) errors[$"exits[{i}].at"] = ["A UTC minute, yyyy-MM-ddTHH:mm:00Z."];
                if (exit.Reason is not { } reason || !Reasons.Contains(reason)) errors[$"exits[{i}].reason"] = ["Not a known exit reason."];
                if (exit.Signal is { } signal && !Signals.Contains(signal))
                    errors[$"exits[{i}].signal"] = ["Not a known signal, or null."];
                if (exit.Exception is not { } exception || exception.Length > MaxExceptions
                    || !exception.All(e => e is not null && ExceptionPattern().IsMatch(e)))
                    errors[$"exits[{i}].exception"] = [$"At most {MaxExceptions} exception type names."];
                if (exit.Frames is not { } frames || frames.Length > MaxFrames
                    || !frames.All(f => f is not null && (JavaFramePattern().IsMatch(f) || NativeFramePattern().IsMatch(f))))
                    errors[$"exits[{i}].frames"] = [$"At most {MaxFrames} Java or native stack frames."];
            }
            if (!NewestFirst(exits.Select(e => e?.At))) errors["exits"] = ["Newest first."];
        }

        var times = (request.Launches ?? []).Select(l => l?.At).Concat((request.Exits ?? []).Select(e => e?.At));
        if (!WithinWindow(times, now)) errors["clock"] = ["Every at within 7 days before and 1 hour after the server's time."];
        return errors;
    }

    /// <summary>Parses an <c>at</c> time: UTC, cut to the minute, exactly <c>yyyy-MM-ddTHH:mm:00Z</c>.</summary>
    public static bool TryParseAt(string? at, out DateTime time)
    {
        time = default;
        return at is { Length: 20 }
            && DateTime.TryParseExact(at, AtFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time);
    }

    private static bool WithinWindow(IEnumerable<string?> times, DateTime now)
    {
        var oldest = now - MaxAge + TimeSpan.FromMinutes(1);
        var newest = now + MaxAhead;
        return times.All(at => !TryParseAt(at, out var time) || (time >= oldest && time <= newest));
    }

    private static bool NewestFirst(IEnumerable<string?> times)
    {
        DateTime? previous = null;
        foreach (var at in times)
        {
            if (!TryParseAt(at, out var time)) continue;
            if (time > previous) return false;
            previous = time;
        }
        return true;
    }

    private static bool IsEvent(string text)
    {
        var parts = text.Split(' ');
        if (!Events.TryGetValue(parts[0], out var keys)) return false;
        var next = 0;
        foreach (var part in parts.Skip(1))
        {
            var equals = part.IndexOf('=');
            if (equals < 0) return false;
            var key = part[..equals];
            var index = Array.FindIndex(keys, next, k => k.Key == key);
            if (index < 0 || !keys[index].IsValue(part[(equals + 1)..])) return false;
            next = index + 1;
        }
        return true;
    }

    private static Func<string, bool> OneOf(params string[] values) => values.Contains;
}
