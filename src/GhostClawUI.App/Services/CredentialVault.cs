using GhostClawUI.Shared;


namespace GhostClawUI.App.Services;

internal sealed class CredentialVault
{
    private readonly PipeClient _pipe;

    public CredentialVault(PipeClient pipe)
    {
        _pipe = pipe;
    }

    public void SaveProviderKey(string providerId, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        _pipe.RequestAsync<CommandResult>("provider.key.save", new ProviderKeySaveRequest(providerId, apiKey)).GetAwaiter().GetResult();
    }

    public string? ReadProviderKey(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        try
        {
            var result = _pipe.RequestAsync<SimpleTextRequest>("provider.key.get", new ProviderKeyRequest(providerId)).GetAwaiter().GetResult();
            var key = result?.Text;
            return string.IsNullOrWhiteSpace(key) ? null : key;
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
            _pipe.RequestAsync<CommandResult>("provider.key.delete", new ProviderKeyRequest(providerId)).GetAwaiter().GetResult();
        }
        catch
        {
            // Missing credentials are fine.
        }
    }
}


