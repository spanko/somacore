namespace SomaCore.Infrastructure.Secrets;

public interface IKeyVaultSecretsClient
{
    /// <summary>Set or rotate a secret. Creates a new version if the name already exists.</summary>
    Task SetSecretAsync(string name, string value, CancellationToken cancellationToken);

    /// <summary>Read the latest version of a secret. Returns null if the secret does not exist.</summary>
    Task<string?> TryGetSecretAsync(string name, CancellationToken cancellationToken);
}
