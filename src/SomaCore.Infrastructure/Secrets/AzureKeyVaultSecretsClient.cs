using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

using Microsoft.Extensions.Options;

namespace SomaCore.Infrastructure.Secrets;

public sealed class AzureKeyVaultSecretsClient : IKeyVaultSecretsClient
{
    private readonly SecretClient _secretClient;

    public AzureKeyVaultSecretsClient(IOptions<KeyVaultOptions> options)
    {
        // DefaultAzureCredential auto-resolves: AZURE_CLIENT_ID env var + the
        // Container App's user-assigned identity in production; az CLI / VS
        // sign-in for local dev.
        _secretClient = new SecretClient(
            vaultUri: new Uri(options.Value.VaultUri),
            credential: new DefaultAzureCredential());
    }

    public async Task SetSecretAsync(string name, string value, CancellationToken cancellationToken)
    {
        await _secretClient.SetSecretAsync(new KeyVaultSecret(name, value), cancellationToken);
    }

    public async Task<string?> TryGetSecretAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _secretClient.GetSecretAsync(name, cancellationToken: cancellationToken);
            return response.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }
}
