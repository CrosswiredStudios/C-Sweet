using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

internal static class CalendarToolSchemas
{
    public static JsonElement Input(string capability)
    {
        var schema = JsonNode.Parse("""
        {"type":"object","properties":{},"additionalProperties":false}
        """)!.AsObject();
        var properties = schema["properties"]!.AsObject();
        if (capability == CalendarCapabilities.Read)
        {
            properties["from"] = JsonNode.Parse("""{"type":"string","format":"date-time"}""");
            properties["to"] = JsonNode.Parse("""{"type":"string","format":"date-time"}""");
            schema["required"] = new JsonArray("from", "to");
        }
        else
        {
            var create = capability is CalendarCapabilities.Create or CalendarCapabilities.Schedule;
            if (create)
            {
                properties["idempotencyKey"] = JsonNode.Parse("""{"type":"string","minLength":1,"maxLength":160}""");
                schema["required"] = new JsonArray("event", "idempotencyKey");
            }
            else
            {
                properties["eventId"] = JsonNode.Parse("""{"type":"string","format":"uuid"}""");
                properties["expectedRevision"] = JsonNode.Parse("""{"type":"integer","minimum":1}""");
                properties["occurrenceLocal"] = JsonNode.Parse("""{"type":["string","null"],"description":"Original occurrence wall-clock date/time without offset. Omit to change the entire series."}""");
                schema["required"] = capability == CalendarCapabilities.Cancel ? new JsonArray("eventId", "expectedRevision") : new JsonArray("eventId", "expectedRevision", "event");
            }
            if (capability != CalendarCapabilities.Cancel)
                properties["event"] = JsonNode.Parse("""
                {"type":"object","required":["title","startLocal","endLocal","timeZoneId"],"additionalProperties":false,"properties":{
                  "title":{"type":"string","minLength":1,"maxLength":300},
                  "startLocal":{"type":"string","description":"Local date/time WITHOUT offset, for example 2026-09-08T09:00:00."},
                  "endLocal":{"type":"string","description":"Exclusive local end WITHOUT offset."},
                  "timeZoneId":{"type":"string","description":"Time zone identifier, for example America/Los_Angeles or UTC."},
                  "allDay":{"type":"boolean"},"description":{"type":["string","null"]},"location":{"type":["string","null"]},
                  "ownerOrganizationUserId":{"type":["string","null"],"format":"uuid"},
                  "attendeeIds":{"type":"array","items":{"type":"string","format":"uuid"},"maxItems":200},
                  "reminderMinutes":{"type":"array","items":{"type":"integer","minimum":0,"maximum":43200},"maxItems":5},
                  "recurrence":{"type":["object","null"],"required":["frequency"],"properties":{"frequency":{"type":"string","enum":["Daily","Weekly","Monthly","Yearly"]},"interval":{"type":"integer","minimum":1,"maximum":1000},"count":{"type":["integer","null"],"minimum":1},"until":{"type":["string","null"],"description":"Inclusive local end date YYYY-MM-DD"}}},
                  "work":{"type":["object","null"],"required":["kind","targetOrganizationUserId"],"properties":{"kind":{"type":"string","enum":["Instructions","ExistingItem"]},"targetOrganizationUserId":{"type":"string","format":"uuid"},"instructions":{"type":["string","null"]},"itemId":{"type":["string","null"],"format":"uuid"}}}
                }}
                """);
        }
        return JsonSerializer.SerializeToElement(schema);
    }
}
