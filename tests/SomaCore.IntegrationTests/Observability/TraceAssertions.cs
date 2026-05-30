using System.Diagnostics;

using FluentAssertions;

using SomaCore.Infrastructure.Observability;

namespace SomaCore.IntegrationTests.Observability;

/// <summary>
/// Test-side support for the ADR 0011 ingestion trace contract. Provides:
///   - <see cref="Capture"/> — install an <see cref="ActivityListener"/> for
///     the <see cref="IngestionTracing.SourceName"/> source; collect every
///     activity that completes while the listener is alive. Dispose to detach.
///   - <see cref="AssertIngestionSpanShape"/> — assert the captured activities
///     contain exactly one root with the expected name + required tags, and
///     the expected set of child spans (by name) all parented under that root.
/// </summary>
public static class TraceAssertions
{
    /// <summary>
    /// Install a listener that records every Activity emitted from
    /// <see cref="IngestionTracing.SourceName"/>. Each test wraps its
    /// system-under-test invocation in <c>using var capture = TraceAssertions.Capture()</c>
    /// and then reads <see cref="ActivityCapture.Activities"/>.
    /// </summary>
    public static ActivityCapture Capture() => new ActivityCapture();

    /// <summary>
    /// Asserts the ADR 0011 root-span shape on a captured activity set.
    ///   - Exactly one activity with name <paramref name="expectedRootName"/> exists.
    ///   - It carries the required root tags (<c>ingestion.source</c>,
    ///     <c>ingestion.trigger</c>, <c>ingestion.event_type</c>,
    ///     <c>external_connection_id</c>).
    ///   - Each name in <paramref name="expectedChildren"/> appears at least once
    ///     among activities whose parent is the root.
    /// </summary>
    public static Activity AssertIngestionSpanShape(
        IReadOnlyList<Activity> activities,
        string expectedRootName,
        IReadOnlyCollection<string> expectedChildren)
    {
        var roots = activities.Where(a => a.OperationName == expectedRootName).ToList();
        roots.Should().HaveCount(1,
            $"exactly one '{expectedRootName}' root span should be present, found {roots.Count}");

        var root = roots[0];

        root.GetTagItem(IngestionTracing.Tags.IngestionSource)
            .Should().NotBeNull($"root span must carry '{IngestionTracing.Tags.IngestionSource}' tag");
        root.GetTagItem(IngestionTracing.Tags.IngestionTrigger)
            .Should().NotBeNull($"root span must carry '{IngestionTracing.Tags.IngestionTrigger}' tag");
        root.GetTagItem(IngestionTracing.Tags.IngestionEventType)
            .Should().NotBeNull($"root span must carry '{IngestionTracing.Tags.IngestionEventType}' tag");
        root.GetTagItem(IngestionTracing.Tags.ExternalConnectionId)
            .Should().NotBeNull($"root span must carry '{IngestionTracing.Tags.ExternalConnectionId}' tag");

        foreach (var childName in expectedChildren)
        {
            activities
                .Where(a => a.OperationName == childName && IsDescendantOf(a, root))
                .Should()
                .NotBeEmpty($"expected at least one '{childName}' child under '{expectedRootName}'");
        }

        return root;
    }

    private static bool IsDescendantOf(Activity candidate, Activity ancestor)
    {
        for (var a = candidate.Parent; a is not null; a = a.Parent)
        {
            if (ReferenceEquals(a, ancestor)) return true;
        }
        return false;
    }
}

/// <summary>
/// Disposable handle returned by <see cref="TraceAssertions.Capture"/>. While
/// alive, every Activity completed under the SomaCore.Ingestion source is
/// appended to <see cref="Activities"/>.
/// </summary>
public sealed class ActivityCapture : IDisposable
{
    private readonly List<Activity> _activities = new();
    private readonly ActivityListener _listener;
    private readonly object _gate = new();

    public ActivityCapture()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == IngestionTracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                lock (_gate) _activities.Add(activity);
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<Activity> Activities
    {
        get
        {
            lock (_gate) return _activities.ToArray();
        }
    }

    public void Dispose() => _listener.Dispose();
}
