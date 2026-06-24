using System.ComponentModel.DataAnnotations;

namespace SomaCore.Infrastructure.Agent;

/// <summary>
/// Configuration for the Anthropic API connection used by
/// <see cref="LiveDailyAgentService"/>. Per ADR 0012 / the no-model-names
/// directive: the specific model id is a runtime config value, not a
/// committed-to-in-docs choice. Pick the right model at deploy time and
/// revisit per Anthropic's lineup.
///
/// All values are bound from configuration under the "Anthropic" section.
/// In production, <see cref="ApiKey"/> resolves from Key Vault via a
/// secret reference (e.g. <c>@Microsoft.KeyVault(SecretUri=...)</c>); in
/// local dev it can be set via user-secrets.
/// </summary>
public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    /// <summary>
    /// Master switch. When <c>false</c> (the default), the API client is
    /// not registered and no live calls happen even for opted-in users.
    /// The router falls back to the stub for everyone. Flip this only
    /// after <see cref="ApiKey"/> is populated in the live environment.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Anthropic API key (sk-ant-...). Populated from Key Vault in
    /// production; never logged. The OptionsValidator throws at startup
    /// if Enabled is true but ApiKey is empty.
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// The Anthropic model id to call. Runtime choice — not a documented
    /// architectural commitment. Default left blank so a misconfigured
    /// environment fails loudly instead of quietly hitting the wrong model.
    /// </summary>
    public string ModelId { get; init; } = string.Empty;

    /// <summary>
    /// Maximum output tokens per invocation. The daily card is short by
    /// design — a paragraph + three actions — so we cap generously but
    /// not extravagantly to keep cost predictable.
    /// </summary>
    [Range(256, 4096)]
    public int MaxOutputTokens { get; init; } = 1024;

    /// <summary>
    /// Sampling temperature. The coach voice asks for consistency rather
    /// than creativity; we want low temperature.
    /// </summary>
    [Range(0.0, 2.0)]
    public double Temperature { get; init; } = 0.4;

    /// <summary>
    /// Per-invocation request timeout. Card generation should be sub-15s
    /// in steady state; longer than that and we want to surface the
    /// failure rather than hold the /me request open.
    /// </summary>
    [Range(5, 120)]
    public int RequestTimeoutSeconds { get; init; } = 30;
}
