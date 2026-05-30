namespace SomaCore.IntegrationTests.Observability;

/// <summary>
/// xUnit collection definition for tests that use <see cref="TraceAssertions.Capture"/>.
///
/// <see cref="System.Diagnostics.ActivityListener"/> is process-global: when two
/// test classes that each install a listener for <c>SomaCore.Ingestion</c> run
/// in parallel, each listener captures the other's spans. The tests can't tell
/// "their" activities apart from someone else's without filtering, so we
/// serialize all trace-capturing tests instead.
///
/// Apply with <c>[Collection(nameof(TracingCollection))]</c> on any test class
/// that calls <c>TraceAssertions.Capture()</c>. Tests within the collection
/// run sequentially; tests outside it run in parallel as normal.
/// </summary>
[CollectionDefinition(nameof(TracingCollection), DisableParallelization = true)]
public sealed class TracingCollection
{
}
