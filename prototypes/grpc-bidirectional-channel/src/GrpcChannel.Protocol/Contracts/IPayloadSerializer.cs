namespace GrpcChannel.Protocol.Contracts;

/// <summary>
/// Contract for payload serialization. Implementations should be AOT-compatible.
/// </summary>
public interface IPayloadSerializer
{
    /// <summary>
    /// Serializes an object to bytes.
    /// </summary>
    byte[] Serialize<T>(T value) where T : class;

    /// <summary>
    /// Deserializes bytes to an object.
    /// </summary>
    T Deserialize<T>(byte[] data) where T : class;

    /// <summary>
    /// Tries to deserialize bytes to an object.
    /// </summary>
    bool TryDeserialize<T>(byte[] data, out T? value) where T : class;
}
