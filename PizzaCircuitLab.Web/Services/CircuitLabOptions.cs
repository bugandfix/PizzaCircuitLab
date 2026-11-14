namespace PizzaCircuitLab.Web.Services;

/// <summary>Behaviour of the lab itself. Bound from the <c>CircuitLab</c> configuration section.</summary>
public sealed class CircuitLabOptions
{
    public const string SectionName = "CircuitLab";

    /// <summary>How long a tab must stay hidden before the browser pauses its circuit.</summary>
    public TimeSpan AutoPauseHiddenDelay { get; set; } = TimeSpan.FromSeconds(12);

    /// <summary>How long an administrative pause waits for the browser to finish pausing before reporting "accepted, not yet confirmed".</summary>
    public TimeSpan PauseConfirmationTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a paused session stays resumable. Also applied to
    /// <c>CircuitOptions.PersistedCircuitInMemoryRetentionPeriod</c> so the registry and the framework agree.
    /// </summary>
    public TimeSpan PausedRetention { get; set; } = TimeSpan.FromHours(2);

    /// <summary>How long closed (non-resumable) sessions remain listed for diagnostics.</summary>
    public TimeSpan ClosedRetention { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Where the Aspire AppHost serves this app. A copy started without a control key (not by the
    /// AppHost) forwards browsers here instead of serving a second, unmanaged lab.
    /// </summary>
    public string ManagedUrl { get; set; } = "https://localhost:9875";

    /// <summary>Let a copy started outside Aspire serve the app itself (its circuits are invisible to the dashboard).</summary>
    public bool AllowStandalone { get; set; }

    /// <summary>Upper bound on retained records; the oldest non-live records are dropped first.</summary>
    public int MaxRecords { get; set; } = 2000;
}

/// <summary>Protection of the administrative API. Bound from the <c>CircuitControl</c> configuration section.</summary>
public sealed class CircuitControlOptions
{
    public const string SectionName = "CircuitControl";
    public const string HeaderName = "X-Circuit-Control-Key";
    public const string CommandIdHeaderName = "X-Circuit-Command-Id";

    /// <summary>Shared secret injected by the Aspire AppHost. When empty the administrative API is disabled.</summary>
    public string? Key { get; set; }

    /// <summary>Administrative requests are accepted from loopback only unless this is enabled.</summary>
    public bool AllowRemote { get; set; }
}
