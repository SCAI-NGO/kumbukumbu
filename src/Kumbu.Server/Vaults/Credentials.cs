// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server.Vaults;

/// <summary>The secret a request unlocks vaults with. Today the dev token; API keys in step 7.</summary>
public interface ICredential
{
    string Secret { get; }
}

/// <summary>The bearer token of the current request (already checked by the auth middleware).</summary>
public sealed class BearerCredential(IHttpContextAccessor http) : ICredential
{
    public string Secret
    {
        get
        {
            var header = http.HttpContext?.Request.Headers.Authorization.ToString() ?? "";
            return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "";
        }
    }
}

public sealed record FixedCredential(string Secret) : ICredential;

/// <summary>Vaults can't be used at all, e.g. because the database still holds plaintext notes.</summary>
public sealed class VaultUnavailableException(string message) : Exception(message);
