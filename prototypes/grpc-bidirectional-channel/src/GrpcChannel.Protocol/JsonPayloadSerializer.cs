using System.Text.Json;
using System.Text.Json.Serialization;
using GrpcChannel.Protocol.Contracts;

namespace GrpcChannel.Protocol;

/// <summary>
/// JSON payload serializer using System.Text.Json.
/// Default serializer for non-protobuf types.
/// </summary>
public sealed class JsonPayloadSerializer : IPayloadSerializer
{
    /// <summary>
    /// Default instance with standard options.
    /// </summary>
    public static JsonPayloadSerializer Default { get; } = new();

    /// <summary>
    /// Instance configured for AOT compatibility with source generation.
    /// </summary>
    public static JsonPayloadSerializer Aot(JsonSerializerContext context) => new(context);

    private readonly JsonSerializerOptions _options;
    private readonly JsonSerializerContext? _context;

    /// <summary>
    /// Creates a new JSON serializer with default options.
    /// </summary>
    public JsonPayloadSerializer() : this(CreateDefaultOptions())
    {
    }

    /// <summary>
    /// Creates a new JSON serializer with custom options.
    /// </summary>
    public JsonPayloadSerializer(JsonSerializerOptions options)
    {
        _options = options;
        _context = null;
    }

    /// <summary>
    /// Creates a new JSON serializer with a source-generated context for AOT.
    /// </summary>
    public JsonPayloadSerializer(JsonSerializerContext context)
    {
        _context = context;
        _options = context.Options;
    }

    /// <inheritdoc />
    public string ContentType => "application/json";

    /// <inheritdoc />
    public byte[] Serialize<T>(T value)
    {
        if (_context is not null)
        {
            return JsonSerializer.SerializeToUtf8Bytes(value, typeof(T), _context);
        }

        return JsonSerializer.SerializeToUtf8Bytes(value, _options);
    }

    /// <inheritdoc />
    public T Deserialize<T>(byte[] data)
    {
        if (_context is not null)
        {
            return (T)JsonSerializer.Deserialize(data, typeof(T), _context)!;
        }

        return JsonSerializer.Deserialize<T>(data, _options)!;
    }

    /// <inheritdoc />
    public object Deserialize(byte[] data, Type type)
    {
        if (_context is not null)
        {
            return JsonSerializer.Deserialize(data, type, _context)!;
        }

        return JsonSerializer.Deserialize(data, type, _options)!;
    }

    private static JsonSerializerOptions CreateDefaultOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}
