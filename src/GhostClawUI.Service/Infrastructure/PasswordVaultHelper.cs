using GhostClawUI.Shared;

namespace GhostClawUI.Service.Infrastructure;

internal static class PasswordVaultHelper
{
    public static string? ReadProviderKey(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        try
        {
            var vault = new Windows.Security.Credentials.PasswordVault();
            var resource = $"{GhostClawConstants.CredentialResourcePrefix}.{providerId}";
            var credential = vault.Retrieve(resource, providerId);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to retrieve password from vault: {ex}");
            return null;
        }
    }
}
