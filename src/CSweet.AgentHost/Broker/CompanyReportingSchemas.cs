using System.Text.Json;
using CSweet.Contracts.Core;
namespace CSweet.AgentHost.Broker;

internal static class CompanyReportingSchemas
{
    public static JsonElement Input(string capability)
    {
        using var document = JsonDocument.Parse(capability switch
    {
        CompanyReportingCapabilities.Finance => """
        {"type":"object","required":["asOf","currency"],"properties":{"asOf":{"type":"string","pattern":"^[0-9]{4}-[0-9]{2}-[0-9]{2}$"},"currency":{"type":"string","pattern":"^[A-Z]{3}$"},"revenue":{"type":["number","null"]},"expenses":{"type":["number","null"]},"cashBalance":{"type":["number","null"]},"monthlyBudget":{"type":["number","null"],"minimum":0}},"additionalProperties":false}
        """,
        CompanyReportingCapabilities.Legal => """
        {"type":"object","required":["entityName","entityType","status","verifiedOn","obligations"],"properties":{"entityName":{"type":"string","minLength":1,"maxLength":300},"entityType":{"type":"string","minLength":1,"maxLength":100},"status":{"type":"string","minLength":1,"maxLength":100},"verifiedOn":{"type":"string","pattern":"^[0-9]{4}-[0-9]{2}-[0-9]{2}$"},"obligations":{"type":"array","maxItems":100,"items":{"type":"object","required":["title","dueDate"],"properties":{"title":{"type":"string","minLength":1,"maxLength":300},"dueDate":{"type":"string","pattern":"^[0-9]{4}-[0-9]{2}-[0-9]{2}$"}},"additionalProperties":false}}},"additionalProperties":false}
        """,
        CompanyReportingCapabilities.Project => """
        {"type":"object","required":["workstreamId","summary"],"properties":{"workstreamId":{"type":"string","format":"uuid"},"summary":{"type":"string","minLength":1,"maxLength":2000}},"additionalProperties":false}
        """,
        _ => throw new ArgumentException("Unknown company reporting capability.")
        });
        return document.RootElement.Clone();
    }
}
