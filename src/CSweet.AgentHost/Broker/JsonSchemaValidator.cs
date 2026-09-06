using System.Text.Json;
using CSweet.Infrastructure.Setup;

namespace CSweet.AgentHost.Broker;

internal static class JsonSchemaValidator
{
    public static void Validate(JsonElement value, JsonElement schema) => RequestSchemaValidator.Validate(value, schema);
    public static void ValidateSchema(JsonElement schema) => RequestSchemaValidator.ValidateSchema(schema);
}
