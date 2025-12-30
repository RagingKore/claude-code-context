namespace GrpcChannel.Protocol.Contracts;

/// <summary>
/// Serializer for arbitrary payload types (non-protobuf).
/// Used when sending C# records, POCOs, or other serializable types.
/// </summary>
public interface IPayloadSerializer
{
    /// <summary>
    /// Content type / media type for the serialized data.
    /// Examples: "application/json", "application/x-msgpack"
    /// </summary>
    string ContentType { get; }

    /// <summary>
    /// Serializes the value to bytes.
    /// </summary>
    byte[] Serialize<T>(T value);

    /// <summary>
    /// Deserializes bytes to the specified type.
    /// </summary>
    T Deserialize<T>(byte[] data);

    /// <summary>
    /// Deserializes bytes to the specified type.
    /// </summary>
    object Deserialize(byte[] data, Type type);
}
