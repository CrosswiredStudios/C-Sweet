using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSweet.Compute.Contracts;

public static class ComputeProtocol
{
    public static JsonSerializerOptions Json { get; } = CreateJson();
    public static byte[] DispatchPayload(string json) => Encoding.UTF8.GetBytes("CSweet.Compute.Dispatch.v1\n" + json);
    public static byte[] ResultPayload(string json) => Encoding.UTF8.GetBytes("CSweet.Compute.ProviderResult.v1\n" + json);
    public static byte[] MaintenancePayload(string json) => Encoding.UTF8.GetBytes("CSweet.Compute.ProviderMaintenance.v1\n" + json);
    public static byte[] WorkRequestPayload(string json) => Encoding.UTF8.GetBytes("CSweet.Compute.ProviderWorkRequest.v1\n" + json);
    public static string Digest(string value) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false, MaxDepth = 16,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
