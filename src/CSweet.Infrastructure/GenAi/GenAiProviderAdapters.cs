using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Application.GenAi;
using CSweet.Contracts.GenAi;
using CSweet.Domain.Setup;

namespace CSweet.Infrastructure.GenAi;

internal static class GenAiAdapterHelpers
{
    public static string RenderTemplate(string template, GenAiMediaRequest request)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{{prompt}}"] = request.Prompt,
            ["{{negative_prompt}}"] = request.NegativePrompt ?? string.Empty,
            ["{{seed}}"] = (request.Seed ?? Random.Shared.Next()).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["{{width}}"] = (request.Width ?? 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["{{height}}"] = (request.Height ?? 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["{{aspect_ratio}}"] = request.AspectRatio ?? "1:1",
            ["{{duration}}"] = (request.DurationSeconds ?? 5).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["{{edit_strength}}"] = (request.EditStrength ?? 0.75).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var node = JsonNode.Parse(template) ?? throw new InvalidOperationException("Template JSON is empty.");
        Replace(node, replacements);
        return node.ToJsonString();
    }

    public static async Task<MemoryStream> DownloadAsync(HttpClient client, string url, Func<Uri, bool> allow, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !allow(uri))
            throw new InvalidOperationException("Provider returned an untrusted output URL.");
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        if (length > 1_073_741_824) throw new InvalidOperationException("Provider output exceeds the 1 GB limit.");
        var output = new MemoryStream();
        await response.Content.CopyToAsync(output, token);
        if (output.Length > 1_073_741_824) throw new InvalidOperationException("Provider output exceeds the 1 GB limit.");
        output.Position = 0;
        return output;
    }

    public static string Extension(string contentType) => contentType.Split(';')[0].ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg", "image/webp" => ".webp", "video/mp4" => ".mp4", "video/webm" => ".webm", _ => ".png"
    };

    private static void Replace(JsonNode node, IReadOnlyDictionary<string, string> values)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(x => x.Key).ToList())
            {
                var child = obj[key];
                if (child is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    foreach (var pair in values) text = text.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
                    obj[key] = text;
                }
                else if (child is not null) Replace(child, values);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                var child = array[index];
                if (child is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    foreach (var pair in values) text = text.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
                    array[index] = text;
                }
                else if (child is not null) Replace(child, values);
            }
        }
    }
}

public abstract class ComfyUiGenAiProviderAdapter(IHttpClientFactory clients) : IGenAiProviderAdapter
{
    protected abstract bool Cloud { get; }
    public abstract GenAiProviderType ProviderType { get; }

    public async Task<GenAiConnectionTestResponse> TestAsync(GenAiProviderProfile profile, string? apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var request = Request(profile, HttpMethod.Get, Cloud ? "/api/object_info" : "/object_info", apiKey);
            using var response = await clients.CreateClient("GenAi").SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(false, "provider_http_error", $"ComfyUI returned HTTP {(int)response.StatusCode}.", DateTimeOffset.UtcNow);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var isObjectInfo = json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.EnumerateObject().Any(node =>
                    node.Value.ValueKind == JsonValueKind.Object &&
                    node.Value.TryGetProperty("input", out var input) &&
                    input.ValueKind == JsonValueKind.Object);
            return isObjectInfo
                ? new(true, null, "Connected to ComfyUI.", DateTimeOffset.UtcNow)
                : new(false, "provider_invalid_response", "The endpoint did not return ComfyUI object information.", DateTimeOffset.UtcNow);
        }
        catch (JsonException)
        {
            return new(false, "provider_invalid_response", "The endpoint did not return valid ComfyUI object information.", DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(false, "provider_unreachable", "Could not connect to ComfyUI.", DateTimeOffset.UtcNow);
        }
    }

    public Task ValidateOperationAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operation.TemplateJson)) throw new InvalidOperationException("ComfyUI operations require API-format workflow JSON.");
        if (string.IsNullOrWhiteSpace(operation.OutputSelector)) throw new InvalidOperationException("ComfyUI operations require an output node selector.");
        using var document = JsonDocument.Parse(operation.TemplateJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("ComfyUI workflow must be a JSON object.");
        if (!operation.TemplateJson.Contains("{{prompt}}", StringComparison.Ordinal))
            throw new InvalidOperationException("ComfyUI workflow must contain a {{prompt}} placeholder.");
        return Task.CompletedTask;
    }

    public async Task<GenAiAdapterSubmission> SubmitAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation, GenAiMediaRequest request,
        IReadOnlyDictionary<Guid, GenAiAdapterInput> inputs, string? apiKey, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient("GenAi");
        foreach (var input in inputs)
        {
            using var upload = Request(profile, HttpMethod.Post, Cloud ? "/api/upload/image" : "/upload/image", apiKey);
            using var form = new MultipartFormDataContent();
            form.Add(new StreamContent(input.Value.Content), "image", input.Value.FileName);
            upload.Content = form;
            using var uploaded = await client.SendAsync(upload, cancellationToken);
            uploaded.EnsureSuccessStatusCode();
        }
        var workflow = GenAiAdapterHelpers.RenderTemplate(operation.TemplateJson!, request);
        foreach (var input in inputs.Values.Select((value, index) => (value, index)))
            workflow = workflow.Replace($"{{{{source_asset_{input.index}}}}}", input.value.FileName, StringComparison.Ordinal);
        using var submit = Request(profile, HttpMethod.Post, Cloud ? "/api/prompt" : "/prompt", apiKey);
        submit.Content = new StringContent($"{{\"prompt\":{workflow}}}", Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(submit, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var id = json.RootElement.GetProperty("prompt_id").GetString() ?? throw new InvalidOperationException("ComfyUI did not return a prompt ID.");
        return new(id, false, []);
    }

    public async Task<GenAiAdapterPollResult> PollAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation, string providerJobId, string? apiKey, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient("GenAi");
        using var request = Request(profile, HttpMethod.Get, $"{(Cloud ? "/api/history/" : "/history/")}{Uri.EscapeDataString(providerJobId)}", apiKey);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(providerJobId, out var byId)) root = byId;
        if (!root.TryGetProperty("outputs", out var outputs)) return new(false, false, null, null, []);
        if (!outputs.TryGetProperty(operation.OutputSelector!, out var selected))
            return new(true, true, "output_missing", "Configured ComfyUI output node did not produce media.", []);
        var items = selected.TryGetProperty("images", out var images) ? images :
            selected.TryGetProperty("gifs", out var gifs) ? gifs : default;
        if (items.ValueKind != JsonValueKind.Array) return new(true, true, "output_missing", "ComfyUI output did not contain media.", []);
        var results = new List<GenAiAdapterOutput>();
        foreach (var item in items.EnumerateArray())
        {
            var fileName = item.GetProperty("filename").GetString()!;
            var subfolder = item.TryGetProperty("subfolder", out var sf) ? sf.GetString() : "";
            var type = item.TryGetProperty("type", out var ty) ? ty.GetString() : "output";
            var path = $"{(Cloud ? "/api/view" : "/view")}?filename={Uri.EscapeDataString(fileName)}&subfolder={Uri.EscapeDataString(subfolder ?? "")}&type={Uri.EscapeDataString(type ?? "output")}";
            using var download = Request(profile, HttpMethod.Get, path, apiKey);
            using var media = await client.SendAsync(download, cancellationToken);
            media.EnsureSuccessStatusCode();
            var stream = new MemoryStream();
            await media.Content.CopyToAsync(stream, cancellationToken);
            stream.Position = 0;
            results.Add(new(fileName, media.Content.Headers.ContentType?.MediaType ?? GuessType(fileName), stream));
        }
        return new(true, false, null, null, results);
    }

    public async Task CancelAsync(GenAiProviderProfile profile, string providerJobId, string? apiKey, CancellationToken cancellationToken)
    {
        using var request = Request(profile, HttpMethod.Post, Cloud ? "/api/interrupt" : "/interrupt", apiKey);
        using var response = await clients.CreateClient("GenAi").SendAsync(request, cancellationToken);
    }

    private static HttpRequestMessage Request(GenAiProviderProfile profile, HttpMethod method, string path, string? apiKey)
    {
        var request = new HttpRequestMessage(method, profile.BaseUrl.TrimEnd('/') + path);
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
        return request;
    }

    private static string GuessType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".mp4" => "video/mp4", ".webm" => "video/webm", _ => "image/png"
    };
}

public sealed class ComfyUiLocalGenAiProviderAdapter(IHttpClientFactory clients) : ComfyUiGenAiProviderAdapter(clients)
{
    protected override bool Cloud => false;
    public override GenAiProviderType ProviderType => GenAiProviderType.ComfyUiLocal;
}

public sealed class ComfyUiCloudGenAiProviderAdapter(IHttpClientFactory clients) : ComfyUiGenAiProviderAdapter(clients)
{
    protected override bool Cloud => true;
    public override GenAiProviderType ProviderType => GenAiProviderType.ComfyUiCloud;
}

public sealed class OpenAiGenAiProviderAdapter(IHttpClientFactory clients) : IGenAiProviderAdapter
{
    public GenAiProviderType ProviderType => GenAiProviderType.OpenAi;

    public async Task<GenAiConnectionTestResponse> TestAsync(GenAiProviderProfile profile, string? apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return new(false, "api_key_required", "OpenAI requires an API key.", DateTimeOffset.UtcNow);
        using var request = new HttpRequestMessage(HttpMethod.Get, profile.BaseUrl.TrimEnd('/') + "/v1/models");
        request.Headers.Authorization = new("Bearer", apiKey);
        try
        {
            using var response = await clients.CreateClient("GenAi").SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode ? new(true, null, "Connected to OpenAI.", DateTimeOffset.UtcNow)
                : new(false, "provider_http_error", $"OpenAI returned HTTP {(int)response.StatusCode}.", DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { return new(false, "provider_unreachable", "Could not connect to OpenAI.", DateTimeOffset.UtcNow); }
    }

    public Task ValidateOperationAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation, CancellationToken cancellationToken)
    {
        if (operation.OperationType is GenAiOperationType.VideoGeneration or GenAiOperationType.VideoEditing)
            throw new InvalidOperationException("OpenAI video operations are not enabled because the current Videos API is deprecated.");
        if (string.IsNullOrWhiteSpace(operation.ModelId)) throw new InvalidOperationException("OpenAI image operations require a model ID.");
        return Task.CompletedTask;
    }

    public async Task<GenAiAdapterSubmission> SubmitAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation, GenAiMediaRequest request,
        IReadOnlyDictionary<Guid, GenAiAdapterInput> inputs, string? apiKey, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient("GenAi");
        using var message = new HttpRequestMessage(HttpMethod.Post, profile.BaseUrl.TrimEnd('/') +
            (operation.OperationType == GenAiOperationType.ImageEditing ? "/v1/images/edits" : "/v1/images/generations"));
        message.Headers.Authorization = new("Bearer", apiKey);
        if (operation.OperationType == GenAiOperationType.ImageEditing)
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(operation.ModelId!), "model");
            form.Add(new StringContent(request.Prompt), "prompt");
            foreach (var input in inputs.Values) form.Add(new StreamContent(input.Content), "image[]", input.FileName);
            message.Content = form;
        }
        else
        {
            message.Content = JsonContent.Create(new { model = operation.ModelId, prompt = request.Prompt, size = Size(request) });
        }
        using var response = await client.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var results = new List<GenAiAdapterOutput>();
        foreach (var item in json.RootElement.GetProperty("data").EnumerateArray())
        {
            MemoryStream stream;
            if (item.TryGetProperty("b64_json", out var b64)) stream = new(Convert.FromBase64String(b64.GetString()!));
            else stream = await GenAiAdapterHelpers.DownloadAsync(client, item.GetProperty("url").GetString()!,
                uri => uri.Host.EndsWith("openai.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("oaistatic.com", StringComparison.OrdinalIgnoreCase), cancellationToken);
            results.Add(new($"image-{results.Count + 1}.png", "image/png", stream));
        }
        return new(Guid.NewGuid().ToString("N"), true, results);
    }

    public Task<GenAiAdapterPollResult> PollAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation, string providerJobId, string? apiKey, CancellationToken cancellationToken) =>
        Task.FromResult(new GenAiAdapterPollResult(true, true, "invalid_state", "OpenAI image jobs complete during submission.", []));
    public Task CancelAsync(GenAiProviderProfile profile, string providerJobId, string? apiKey, CancellationToken cancellationToken) => Task.CompletedTask;
    private static string Size(GenAiMediaRequest request) => request.Width.HasValue && request.Height.HasValue ? $"{request.Width}x{request.Height}" : "1024x1024";
}

public sealed class GoogleGeminiGenAiProviderAdapter(IHttpClientFactory clients) : IGenAiProviderAdapter
{
    public GenAiProviderType ProviderType => GenAiProviderType.GoogleGemini;
    public async Task<GenAiConnectionTestResponse> TestAsync(GenAiProviderProfile profile, string? apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return new(false, "api_key_required", "Google Gemini requires an API key.", DateTimeOffset.UtcNow);
        try
        {
            using var response = await clients.CreateClient("GenAi").GetAsync($"{profile.BaseUrl.TrimEnd('/')}/v1beta/models?key={Uri.EscapeDataString(apiKey)}", cancellationToken);
            return response.IsSuccessStatusCode ? new(true, null, "Connected to Google Gemini.", DateTimeOffset.UtcNow)
                : new(false, "provider_http_error", $"Google Gemini returned HTTP {(int)response.StatusCode}.", DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { return new(false, "provider_unreachable", "Could not connect to Google Gemini.", DateTimeOffset.UtcNow); }
    }

    public Task ValidateOperationAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operation.ModelId)) throw new InvalidOperationException("Google Gemini operations require a model ID.");
        return Task.CompletedTask;
    }

    public async Task<GenAiAdapterSubmission> SubmitAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation, GenAiMediaRequest request,
        IReadOnlyDictionary<Guid, GenAiAdapterInput> inputs, string? apiKey, CancellationToken cancellationToken)
    {
        if (operation.OperationType is GenAiOperationType.VideoGeneration or GenAiOperationType.VideoEditing)
        {
            var url = $"{profile.BaseUrl.TrimEnd('/')}/v1beta/models/{Uri.EscapeDataString(operation.ModelId!)}:predictLongRunning?key={Uri.EscapeDataString(apiKey!)}";
            var instance = new JsonObject { ["prompt"] = request.Prompt };
            if (inputs.Values.FirstOrDefault() is { } source)
            {
                using var memory = new MemoryStream();
                await source.Content.CopyToAsync(memory, cancellationToken);
                var media = new JsonObject
                {
                    ["bytesBase64Encoded"] = Convert.ToBase64String(memory.ToArray()),
                    ["mimeType"] = source.ContentType
                };
                instance[operation.OperationType == GenAiOperationType.VideoEditing ? "video" : "image"] = media;
            }
            using var response = await clients.CreateClient("GenAi").PostAsJsonAsync(url,
                new JsonObject { ["instances"] = new JsonArray(instance) }, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return new(json.RootElement.GetProperty("name").GetString()!, false, []);
        }
        var parts = new List<object> { new { text = request.Prompt } };
        foreach (var input in inputs.Values)
        {
            using var memory = new MemoryStream();
            await input.Content.CopyToAsync(memory, cancellationToken);
            parts.Add(new { inline_data = new { mime_type = input.ContentType, data = Convert.ToBase64String(memory.ToArray()) } });
        }
        var imageUrl = $"{profile.BaseUrl.TrimEnd('/')}/v1beta/models/{Uri.EscapeDataString(operation.ModelId!)}:generateContent?key={Uri.EscapeDataString(apiKey!)}";
        using var imageResponse = await clients.CreateClient("GenAi").PostAsJsonAsync(imageUrl, new
        {
            contents = new[] { new { parts } },
            generationConfig = new { responseModalities = new[] { "TEXT", "IMAGE" } }
        }, cancellationToken);
        imageResponse.EnsureSuccessStatusCode();
        using var imageJson = JsonDocument.Parse(await imageResponse.Content.ReadAsStringAsync(cancellationToken));
        var outputs = new List<GenAiAdapterOutput>();
        foreach (var part in imageJson.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts").EnumerateArray())
        {
            if (!part.TryGetProperty("inlineData", out var data) && !part.TryGetProperty("inline_data", out data)) continue;
            var mime = data.TryGetProperty("mimeType", out var mt) ? mt.GetString()! : data.GetProperty("mime_type").GetString()!;
            outputs.Add(new($"image-{outputs.Count + 1}{GenAiAdapterHelpers.Extension(mime)}", mime,
                new MemoryStream(Convert.FromBase64String(data.GetProperty("data").GetString()!))));
        }
        return new(Guid.NewGuid().ToString("N"), true, outputs);
    }

    public async Task<GenAiAdapterPollResult> PollAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation, string providerJobId, string? apiKey, CancellationToken cancellationToken)
    {
        var url = $"{profile.BaseUrl.TrimEnd('/')}/v1beta/{providerJobId.TrimStart('/')}?key={Uri.EscapeDataString(apiKey!)}";
        using var response = await clients.CreateClient("GenAi").GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!json.RootElement.TryGetProperty("done", out var done) || !done.GetBoolean()) return new(false, false, null, null, []);
        if (json.RootElement.TryGetProperty("error", out var error)) return new(true, true, "provider_error", error.ToString(), []);
        var urls = new List<string>();
        FindUris(json.RootElement, urls);
        var outputs = new List<GenAiAdapterOutput>();
        foreach (var outputUrl in urls)
        {
            var stream = await GenAiAdapterHelpers.DownloadAsync(clients.CreateClient("GenAi"), outputUrl,
                uri => uri.Host.EndsWith("googleapis.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("googleusercontent.com", StringComparison.OrdinalIgnoreCase), cancellationToken);
            outputs.Add(new($"video-{outputs.Count + 1}.mp4", "video/mp4", stream));
        }
        return outputs.Count == 0 ? new(true, true, "output_missing", "Google did not return a video output.", []) : new(true, false, null, null, outputs);
    }

    public Task CancelAsync(GenAiProviderProfile profile, string providerJobId, string? apiKey, CancellationToken cancellationToken) => Task.CompletedTask;

    private static void FindUris(JsonElement element, ICollection<string> results)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("uri") && property.Value.ValueKind == JsonValueKind.String) results.Add(property.Value.GetString()!);
                else FindUris(property.Value, results);
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) FindUris(item, results);
    }
}

public sealed class ReplicateGenAiProviderAdapter(IHttpClientFactory clients) : IGenAiProviderAdapter
{
    public GenAiProviderType ProviderType => GenAiProviderType.Replicate;
    public async Task<GenAiConnectionTestResponse> TestAsync(GenAiProviderProfile profile, string? apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return new(false, "api_key_required", "Replicate requires an API token.", DateTimeOffset.UtcNow);
        using var request = Request(profile, HttpMethod.Get, "/v1/account", apiKey);
        try
        {
            using var response = await clients.CreateClient("GenAi").SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode ? new(true, null, "Connected to Replicate.", DateTimeOffset.UtcNow)
                : new(false, "provider_http_error", $"Replicate returned HTTP {(int)response.StatusCode}.", DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { return new(false, "provider_unreachable", "Could not connect to Replicate.", DateTimeOffset.UtcNow); }
    }

    public Task ValidateOperationAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operation.ModelId) || !operation.ModelId.Contains('/'))
            throw new InvalidOperationException("Replicate operations require an owner/model identifier.");
        if (string.IsNullOrWhiteSpace(operation.TemplateJson))
            throw new InvalidOperationException("Replicate operations require prediction input template JSON.");
        if (string.IsNullOrWhiteSpace(operation.OutputSelector))
            throw new InvalidOperationException("Replicate operations require an output selector such as 'output'.");
        using var document = JsonDocument.Parse(operation.TemplateJson);
        if (!operation.TemplateJson.Contains("{{prompt}}", StringComparison.Ordinal))
            throw new InvalidOperationException("Replicate input template must contain a {{prompt}} placeholder.");
        return Task.CompletedTask;
    }

    public async Task<GenAiAdapterSubmission> SubmitAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation, GenAiMediaRequest request,
        IReadOnlyDictionary<Guid, GenAiAdapterInput> inputs, string? apiKey, CancellationToken cancellationToken)
    {
        var rendered = GenAiAdapterHelpers.RenderTemplate(operation.TemplateJson!, request);
        foreach (var input in inputs.Values.Select((value, index) => (value, index)))
        {
            using var memory = new MemoryStream();
            await input.value.Content.CopyToAsync(memory, cancellationToken);
            var dataUri = $"data:{input.value.ContentType};base64,{Convert.ToBase64String(memory.ToArray())}";
            rendered = rendered.Replace($"{{{{source_asset_{input.index}}}}}", dataUri, StringComparison.Ordinal);
        }
        using var message = Request(profile, HttpMethod.Post, $"/v1/models/{operation.ModelId}/predictions", apiKey);
        message.Content = new StringContent($"{{\"input\":{rendered}}}", Encoding.UTF8, "application/json");
        using var response = await clients.CreateClient("GenAi").SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return new(json.RootElement.GetProperty("id").GetString()!, false, []);
    }

    public async Task<GenAiAdapterPollResult> PollAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation, string providerJobId, string? apiKey, CancellationToken cancellationToken)
    {
        using var request = Request(profile, HttpMethod.Get, $"/v1/predictions/{Uri.EscapeDataString(providerJobId)}", apiKey);
        using var response = await clients.CreateClient("GenAi").SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var status = json.RootElement.GetProperty("status").GetString();
        if (status is "starting" or "processing") return new(false, false, null, null, []);
        if (status != "succeeded") return new(true, true, "provider_error",
            json.RootElement.TryGetProperty("error", out var error) ? error.ToString() : "Replicate prediction failed.", []);
        var urls = new List<string>();
        if (TrySelect(json.RootElement, operation.OutputSelector!, out var output))
        {
            if (output.ValueKind == JsonValueKind.String) urls.Add(output.GetString()!);
            else if (output.ValueKind == JsonValueKind.Array) urls.AddRange(output.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
        }
        var results = new List<GenAiAdapterOutput>();
        foreach (var url in urls)
        {
            var stream = await GenAiAdapterHelpers.DownloadAsync(clients.CreateClient("GenAi"), url,
                uri => uri.Host.EndsWith("replicate.delivery", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("replicate.com", StringComparison.OrdinalIgnoreCase), cancellationToken);
            var isVideo = operation.OperationType is GenAiOperationType.VideoGeneration or GenAiOperationType.VideoEditing;
            results.Add(new($"{(isVideo ? "video" : "image")}-{results.Count + 1}{(isVideo ? ".mp4" : ".png")}",
                isVideo ? "video/mp4" : "image/png", stream));
        }
        return results.Count == 0 ? new(true, true, "output_missing", "Replicate returned no media output.", []) : new(true, false, null, null, results);
    }

    public async Task CancelAsync(GenAiProviderProfile profile, string providerJobId, string? apiKey, CancellationToken cancellationToken)
    {
        using var request = Request(profile, HttpMethod.Post, $"/v1/predictions/{Uri.EscapeDataString(providerJobId)}/cancel", apiKey);
        using var response = await clients.CreateClient("GenAi").SendAsync(request, cancellationToken);
    }

    private static HttpRequestMessage Request(GenAiProviderProfile profile, HttpMethod method, string path, string? key)
    {
        var request = new HttpRequestMessage(method, profile.BaseUrl.TrimEnd('/') + path);
        request.Headers.Authorization = new("Bearer", key);
        return request;
    }

    private static bool TrySelect(JsonElement root, string selector, out JsonElement selected)
    {
        selected = root;
        foreach (var segment in selector.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (selected.ValueKind != JsonValueKind.Object || !selected.TryGetProperty(segment, out selected))
                return false;
        }
        return true;
    }
}
