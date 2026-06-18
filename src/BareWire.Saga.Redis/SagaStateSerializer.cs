using System.Text.Json;
using BareWire.Abstractions.Saga;
using StackExchange.Redis;

namespace BareWire.Saga.Redis;

/// <summary>
/// Serializes and deserializes SAGA state objects using System.Text.Json with UTF-8 encoding,
/// eliminating intermediate string allocations and unnecessary transcoding.
/// </summary>
/// <typeparam name="TSaga">The SAGA state type to serialize.</typeparam>
internal sealed class SagaStateSerializer<TSaga>
    where TSaga : class, ISagaState
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Serializes a SAGA state instance to a UTF-8 JSON byte array suitable for storage as a Redis value.
    /// </summary>
    /// <param name="saga">The SAGA state to serialize. Must not be <see langword="null"/>.</param>
    /// <returns>A <see cref="RedisValue"/> backed by the UTF-8 JSON byte representation of <paramref name="saga"/>.</returns>
    internal static RedisValue Serialize(TSaga saga)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(saga, SerializerOptions);
        return bytes;
    }

    /// <summary>
    /// Deserializes a SAGA state instance from a Redis value containing UTF-8 JSON bytes.
    /// </summary>
    /// <param name="value">The <see cref="RedisValue"/> containing the UTF-8 JSON payload.</param>
    /// <returns>The deserialized SAGA state instance.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when deserialization produces a <see langword="null"/> result, which indicates
    /// a corrupted or unexpected payload in Redis.
    /// </exception>
    internal static TSaga Deserialize(RedisValue value)
    {
        byte[] bytes = (byte[])value!;
        TSaga? result = JsonSerializer.Deserialize<TSaga>(bytes.AsSpan(), SerializerOptions);
        return result ?? throw new InvalidOperationException(
            $"Failed to deserialize SAGA state of type '{typeof(TSaga).Name}' from Redis: deserialization returned null.");
    }
}
