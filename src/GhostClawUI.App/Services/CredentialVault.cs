using GhostClawUI.Shared;
using Windows.Security.Credentials;

namespace GhostClawUI.App.Services;

internal sealed class CredentialVault
{
    private readonly PasswordVault _vault = new();

    public void SaveProviderKey(string providerId, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        DeleteProviderKey(providerId);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _vault.Add(new PasswordCredential(Resource(providerId), providerId, apiKey));
        }
    }

    public string? ReadProviderKey(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        try
        {
            var credential = _vault.Retrieve(Resource(providerId), providerId);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch
        {
            return null;
        }
    }

    public void DeleteProviderKey(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        try
        {
            var credential = _vault.Retrieve(Resource(providerId), providerId);
            _vault.Remove(credential);
        }
        catch
        {
            // Missing credentials are fine.
        }
    }

    private static string Resource(string providerId) => $"{GhostClawConstants.CredentialResourcePrefix}.{providerId}";
}



