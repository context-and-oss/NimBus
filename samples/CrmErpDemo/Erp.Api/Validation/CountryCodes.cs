using System.Collections.Frozen;

namespace Erp.Api.Validation;

/// <summary>
/// The rule ERP applies to a customer that arrives from CRM: its country is an ISO 3166-1
/// alpha-2 code. CRM does not check this, so a mistyped country (the classic one is "UK" for the
/// United Kingdom, whose code is "GB") reaches ERP and is rejected here.
/// </summary>
/// <remarks>
/// This is the demo's data-quality failure, and it is a real one: the rejection quotes the value
/// the message carried, so the failure NimBus records agrees with the message's payload.
/// </remarks>
public static class CountryCodes
{
    /// <summary>Every officially assigned ISO 3166-1 alpha-2 code.</summary>
    public static readonly FrozenSet<string> Iso3166Alpha2 = FrozenSet.ToFrozenSet(
        ("AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ " +
         "BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BV BW BY BZ " +
         "CA CC CD CF CG CH CI CK CL CM CN CO CR CU CV CW CX CY CZ " +
         "DE DJ DK DM DO DZ " +
         "EC EE EG EH ER ES ET " +
         "FI FJ FK FM FO FR " +
         "GA GB GD GE GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY " +
         "HK HM HN HR HT HU " +
         "ID IE IL IM IN IO IQ IR IS IT " +
         "JE JM JO JP " +
         "KE KG KH KI KM KN KP KR KW KY KZ " +
         "LA LB LC LI LK LR LS LT LU LV LY " +
         "MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT MU MV MW MX MY MZ " +
         "NA NC NE NF NG NI NL NO NP NR NU NZ " +
         "OM " +
         "PA PE PF PG PH PK PL PM PN PR PS PT PW PY " +
         "QA " +
         "RE RO RS RU RW " +
         "SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS ST SV SX SY SZ " +
         "TC TD TF TG TH TJ TK TL TM TN TO TR TT TV TW TZ " +
         "UA UG UM US UY UZ " +
         "VA VC VE VG VI VN VU " +
         "WF WS " +
         "YE YT " +
         "ZA ZM ZW").Split(' '),
        StringComparer.Ordinal);

    /// <summary>
    /// Null when <paramref name="countryCode"/> is an ISO 3166-1 alpha-2 code; otherwise why ERP
    /// rejects it, quoting the value. Codes are upper case, as the standard writes them.
    /// </summary>
    /// <param name="countryCode">The country the customer carries.</param>
    public static string? Problem(string? countryCode)
    {
        if (string.IsNullOrEmpty(countryCode))
            return "CountryCode is missing. ERP needs an ISO 3166-1 alpha-2 country code for every customer.";

        if (Iso3166Alpha2.Contains(countryCode))
            return null;

        var hint = string.Equals(countryCode, "UK", StringComparison.Ordinal)
            ? " The code for the United Kingdom is 'GB'."
            : string.Empty;
        return $"CountryCode '{countryCode}' is not an ISO 3166-1 alpha-2 country code, so ERP did not save the customer.{hint} " +
               "Correct the country on the account in the source system and send the account again.";
    }
}
