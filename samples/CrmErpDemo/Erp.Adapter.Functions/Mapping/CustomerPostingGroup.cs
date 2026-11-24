namespace Erp.Adapter.Functions.Mapping;

/// <summary>
/// Maps a customer's country to the ERP posting group that decides how its invoices are taxed.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class holds the demo's planted defect, on purpose.</b> The table below covers the
/// countries the regional hubs serve except Finland, and <see cref="For"/> assumes the country is
/// always in the table. An account in Finland, or in any country outside the table, throws a
/// <see cref="KeyNotFoundException"/> that quotes the country.
/// </para>
/// <para>
/// It runs only while erp-web's error mode has the "Missing posting group (application defect)"
/// reason selected (see <c>ErrorModeGuard</c>); no handler calls it otherwise. It exists so the
/// failure NimBus records is a true one: the error names the value the message carried, and the
/// fix is a code change in this file. Do not fix it as a drive-by.
/// </para>
/// </remarks>
public static class CustomerPostingGroup
{
    private static readonly Dictionary<string, string> ByCountry = new(StringComparer.Ordinal)
    {
        ["DK"] = "DOMESTIC",
        ["SE"] = "EU",
        ["NO"] = "EEA",
        ["DE"] = "EU",
        ["AT"] = "EU",
        ["CH"] = "EXPORT",
    };

    /// <summary>The posting group for a customer in <paramref name="countryCode"/>.</summary>
    /// <param name="countryCode">The customer's ISO 3166-1 alpha-2 country code.</param>
    public static string For(string countryCode) => ByCountry[countryCode];
}
