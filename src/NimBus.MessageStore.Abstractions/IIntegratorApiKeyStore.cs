using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Stores the integrator API keys that let scripts and partner systems call the WebApp's REST API
/// with a role and an endpoint scope (Spec 045). Separate from <see cref="IEndpointMetadataStore"/>
/// so existing implementations keep compiling; without a registered implementation the WebApp
/// cannot issue keys.
/// </summary>
public interface IIntegratorApiKeyStore
{
    /// <summary>
    /// The issued keys, or an empty list with a null <see cref="IntegratorApiKeySettings.Revision"/>
    /// when none was saved.
    /// </summary>
    Task<IntegratorApiKeySettings> GetIntegratorApiKeySettings();

    /// <summary>
    /// Saves the keys when the stored revision equals <paramref name="expectedRevision"/>;
    /// null means only when nothing is stored yet. <paramref name="settings"/> must already carry
    /// the new revision.
    /// </summary>
    /// <param name="settings">The keys to store, with their new revision.</param>
    /// <param name="expectedRevision">The revision the caller read, or null to create.</param>
    /// <returns>False on a revision conflict; the stored record is unchanged.</returns>
    Task<bool> TrySetIntegratorApiKeySettings(IntegratorApiKeySettings settings, string? expectedRevision);

    /// <summary>The last recorded use of every integrator API key that has been used.</summary>
    Task<List<IntegratorApiKeyUsage>> GetIntegratorApiKeyUsage();

    /// <summary>Records that the key authenticated a request; the last write wins.</summary>
    /// <param name="keyId">The key's <see cref="IntegratorApiKey.KeyId"/>.</param>
    /// <param name="usedAtUtc">When the key was used.</param>
    Task RecordIntegratorApiKeyUse(string keyId, DateTime usedAtUtc);
}
