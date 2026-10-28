using System.Text.Json;
using CSweet.ConnectionService.Protocol;

namespace CSweet.Infrastructure.Setup;

/// <summary>One bounded schema implementation for local and managed connector boundaries.</summary>
public static class RequestSchemaValidator
{
    public static void Validate(JsonElement value, JsonElement schema) => ManagedRequestSchemaValidator.Validate(value, schema);
    public static void ValidateSchema(JsonElement schema) => ManagedRequestSchemaValidator.ValidateSchema(schema);
}
