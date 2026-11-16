using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NimBus.WebApp.Services.Operations;

/// <summary>Serializes the <c>Data</c> of operator audit rows.</summary>
internal static class OperatorAuditData
{
    /// <summary>
    /// <paramref name="data"/> as JSON, with <c>agentKeyId</c> appended when an MCP agent key issued
    /// the command (Spec 052 §6.9). Rows written for every other caller keep their exact shape.
    /// </summary>
    public static string Serialize(object data, string? agentKeyId)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (string.IsNullOrEmpty(agentKeyId))
            return JsonConvert.SerializeObject(data);

        var json = JObject.FromObject(data);
        json["agentKeyId"] = agentKeyId;
        return json.ToString(Formatting.None);
    }
}
