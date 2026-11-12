using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Stores the dashboard tokens that give a wall display read-only access to the WebApp's Monitor.
/// Separate from <see cref="IEndpointMetadataStore"/> so existing implementations keep compiling;
/// without a registered implementation the WebApp cannot issue tokens.
/// </summary>
public interface IDashboardTokenStore
{
    /// <summary>
    /// The issued tokens, or an empty list with a null <see cref="DashboardTokenSettings.Revision"/>
    /// when none was saved.
    /// </summary>
    Task<DashboardTokenSettings> GetDashboardTokenSettings();

    /// <summary>
    /// Saves the tokens when the stored revision equals <paramref name="expectedRevision"/>;
    /// null means only when nothing is stored yet. <paramref name="settings"/> must already carry
    /// the new revision.
    /// </summary>
    /// <param name="settings">The tokens to store, with their new revision.</param>
    /// <param name="expectedRevision">The revision the caller read, or null to create.</param>
    /// <returns>False on a revision conflict; the stored record is unchanged.</returns>
    Task<bool> TrySetDashboardTokenSettings(DashboardTokenSettings settings, string? expectedRevision);

    /// <summary>The last recorded use of every dashboard token that has been used.</summary>
    Task<List<DashboardTokenUsage>> GetDashboardTokenUsage();

    /// <summary>Records that the token authenticated a request; the last write wins.</summary>
    /// <param name="tokenId">The token's <see cref="DashboardToken.TokenId"/>.</param>
    /// <param name="usedAtUtc">When the token was used.</param>
    Task RecordDashboardTokenUse(string tokenId, DateTime usedAtUtc);
}
