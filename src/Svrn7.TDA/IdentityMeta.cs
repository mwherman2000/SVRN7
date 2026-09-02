using System.Text.Json;
using System.Text.Json.Serialization;

namespace Svrn7.TDA;

/// <summary>
/// <c>identity.meta.json</c> — the cleartext, non-secret record of what an instance
/// directory holds (docs/AGENTWALLET.md §6). It is the only file the startup
/// locate step reads, and it mirrors the DID Document's service endpoint so
/// "what port / DID is this dir?" needs neither the wallet password nor an open
/// database. The DID Document remains authoritative for the endpoint.
/// </summary>
public sealed class IdentityMeta
{
    // Load-bearing: the startup locate step matches on did / name, and reads the
    // port out of serviceEndpointUrl without unlocking the wallet or opening the
    // (encrypted) DID database. role / createdUtc are not read by code — kept
    // only so `cat identity.meta.json` tells a human what the instance is.
    [JsonPropertyName("did")]                    public string Did { get; set; } = "";
    [JsonPropertyName("name")]                   public string Name { get; set; } = "";
    [JsonPropertyName("role")]                   public string Role { get; set; } = "";
    [JsonPropertyName("serviceEndpointUrl")]     public string ServiceEndpointUrl { get; set; } = "";
    [JsonPropertyName("createdUtc")]             public string CreatedUtc { get; set; } = "";

    /// <summary>Parent-tier DID (Society for a Citizen, Federation for a Society) — written by
    /// <see cref="Svrn7RunspaceContext.SetParentTda"/> after a successful registration. Non-secret.</summary>
    [JsonPropertyName("parentTdaDid")]           public string? ParentTdaDid { get; set; }
    [JsonPropertyName("parentTdaEndpointUrl")]   public string? ParentTdaEndpointUrl { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static IdentityMeta? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<IdentityMeta>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Writes atomically (.tmp → replace), creating the parent directory.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var json = JsonSerializer.Serialize(this, JsonOpts);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    /// <summary>The port from <see cref="ServiceEndpointUrl"/> (e.g. <c>http://localhost:8442/didcomm</c> → 8442), or null.</summary>
    public int? EndpointPort()
    {
        if (string.IsNullOrWhiteSpace(ServiceEndpointUrl)) return null;
        return Uri.TryCreate(ServiceEndpointUrl, UriKind.Absolute, out var uri) && uri.Port > 0 ? uri.Port : null;
    }
}
