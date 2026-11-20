using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LocalMessaging.Contracts;

public sealed class ApiException(int status, string code, string detail, IDictionary<string,string[]>? errors = null) : Exception(detail)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public IDictionary<string,string[]>? Errors { get; } = errors;
}

public sealed record ValidatedImage(byte[] Bytes, string ContentType, int Width, int Height, string Sha256);
public sealed record PageQuery(int Limit = 50, string? PageToken = null, string? AsOf = null,
    string? View = null, string? HandlePrefix = null, Guid? OwnerUserId = null);

public interface IImageValidator
{
    Task<ValidatedImage> ValidateAsync(byte[] bytes, CancellationToken cancellationToken = default);
}

public static class Wire
{
    public const string ClientHeader = "X-Local-Messaging-Client";
    public const string ClientHeaderValue = "1";
    public const int ContractVersion = 1;
    public static JsonSerializerOptions ResponseOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 32,
        WriteIndented = false
    };
    public static JsonSerializerOptions RequestOptions { get; } = new(ResponseOptions)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowOutOfOrderMetadataProperties = true
    };
    public static T ReadRequest<T>(JsonElement json) where T : class
    {
        try { return json.Deserialize<T>(RequestOptions) ?? throw new JsonException("A JSON object is required."); }
        catch (JsonException ex)
        {
            throw new ApiException(400, "invalid_contract", "The request does not match the version 1 contract.",
                new Dictionary<string,string[]> { [ex.Path ?? "$"] = ["Check required fields, types, discriminators, and unknown properties."] });
        }
    }
}

public sealed record ProductLimits
{
    public int JsonBytes { get; init; } = 64 * 1024;
    public int ImageBytes { get; init; } = 8 * 1024 * 1024;
    public int MultipartBytes { get; init; } = 9 * 1024 * 1024;
    public int ImageSide { get; init; } = 8192;
    public long ImagePixels { get; init; } = 20_000_000;
    public int TextLength { get; init; } = 16_384;
    public int CaptionLength { get; init; } = 4096;
    public int TargetUsers { get; init; } = 20;
    public int FilterIds { get; init; } = 100;
    public int PageSize { get; init; } = 200;
    public int EventHeaders { get; init; } = 1000;
    public int EventPayloadBytes { get; init; } = 1024 * 1024;
    public int EventFrameBytes { get; init; } = 256 * 1024;
    public int ActiveStreams { get; init; } = 64;
    public int ImageValidations { get; init; } = 2;
    public int ImageResponses { get; init; } = 8;
    public int WriteWaitSeconds { get; init; } = 5;
    public int DatabaseTimeoutSeconds { get; init; } = 5;
    public int StreamWriteSeconds { get; init; } = 30;
    public int HeartbeatSeconds { get; init; } = 15;
    public int ClientIdleSeconds { get; init; } = 45;
    public int DrainSeconds { get; init; } = 10;
}
