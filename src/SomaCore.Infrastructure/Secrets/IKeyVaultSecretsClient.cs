namespace SomaCore.Infrastructure.Secrets;

public interface IKeyVaultSecretsClient
{
    /// <summary>Set or rotate a secret. Creates a new version if the name already exists.</summary>
    Task SetSecretAsync(string name, string value, CancellationToken cancellationToken);

    /// <summary>Read the latest version of a secret. Returns null if the secret does not exist.</summary>
    Task<string?> TryGetSecretAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Best-effort delete of a secret (soft-delete under KV semantics; the
    /// secret is recoverable for the vault's soft-delete retention window).
    /// Returns true if the secret was deleted or did not exist, false on a
    /// non-404 error. Callers should log + continue regardless — disconnect
    /// is the user's intent and the secret has no value without a connection.
    /// </summary>
    Task<bool> TryDeleteSecretAsync(string name, CancellationToken cancellationToken);
}
