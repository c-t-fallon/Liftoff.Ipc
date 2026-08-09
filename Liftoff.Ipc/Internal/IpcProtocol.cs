using System.Buffers.Binary;
using System.Text.Json;

namespace Liftoff.Ipc.Internal;

internal static class IpcProtocol
{
    public const int MaxMessageBytes = 1024 * 1024;

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}

internal static class ContractName
{
    public static string For<T>() => For(typeof(T));

    public static string For(Type type)
    {
        var assemblyName = type.Assembly.GetName().Name
            ?? throw new IpcConfigurationException($"Type '{type}' has no assembly name.");
        var typeName = type.FullName
            ?? throw new IpcConfigurationException($"Type '{type}' has no full name.");
        return $"{assemblyName}:{typeName}";
    }
}

internal static class MessageTypes
{
    public const string AuthenticationHello = "authentication-hello";
    public const string AuthenticationChallenge = "authentication-challenge";
    public const string AuthenticationProof = "authentication-proof";
    public const string AuthenticationAccepted = "authentication-accepted";
    public const string ExecuteRequest = "execute-request";
    public const string RequestAccepted = "request-accepted";
    public const string OperationProgress = "operation-progress";
    public const string OperationCompleted = "operation-completed";
    public const string OperationFailed = "operation-failed";
    public const string CancelRequest = "cancel-request";
    public const string OperationCancelled = "operation-cancelled";
    public const string Heartbeat = "heartbeat";
    public const string SubscribeRequest = "subscribe-request";
    public const string SubscriptionAccepted = "subscription-accepted";
    public const string SubscriptionRejected = "subscription-rejected";
    public const string UnsubscribeRequest = "unsubscribe-request";
    public const string UnsubscriptionAccepted = "unsubscription-accepted";
    public const string EventPublished = "event-published";
}

internal sealed record IpcEnvelope(string Type, Guid? RequestId, JsonElement Payload)
{
    public static IpcEnvelope Create<T>(string type, Guid? requestId, T payload) =>
        new(type, requestId, JsonSerializer.SerializeToElement(payload, IpcProtocol.JsonOptions));

    public T ReadPayload<T>() =>
        Payload.Deserialize<T>(IpcProtocol.JsonOptions)
        ?? throw new InvalidDataException($"Message '{Type}' has no {typeof(T).Name} payload.");
}

internal sealed record ExecuteRequest(string Contract, JsonElement Arguments);
internal sealed record RequestAccepted(DateTimeOffset AcceptedAt);
internal sealed record OperationProgress(double Percent, string Message);
internal sealed record OperationCompleted(JsonElement Result);
internal sealed record OperationFailed(string Error);
internal sealed record OperationCancelled(string Reason);
internal sealed record Heartbeat(DateTimeOffset SentAt);
internal sealed record SubscribeRequest(string EventContract);
internal sealed record SubscriptionAccepted(string EventContract, DateTimeOffset AcceptedAt);
internal sealed record SubscriptionRejected(string Error);
internal sealed record UnsubscriptionAccepted(DateTimeOffset AcceptedAt);
internal sealed record EventPublished(JsonElement Data);
internal sealed record AuthenticationHello(byte[] ClientNonce);
internal sealed record AuthenticationChallenge(byte[] ServerNonce, byte[] ServerProof);
internal sealed record AuthenticationProof(byte[] ClientProof);
internal sealed record AuthenticationAccepted(DateTimeOffset AcceptedAt);

internal static class LengthPrefixedJson
{
    public static async ValueTask WriteAsync(
        Stream stream,
        IpcEnvelope message,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, IpcProtocol.JsonOptions);
        if (payload.Length > IpcProtocol.MaxMessageBytes)
        {
            throw new InvalidDataException(
                $"Message is {payload.Length} bytes; maximum is {IpcProtocol.MaxMessageBytes}.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, 0, header.Length, cancellationToken);
        await stream.WriteAsync(payload, 0, payload.Length, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async ValueTask<IpcEnvelope?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var header = new byte[sizeof(int)];
        var headerBytes = await ReadExactlyOrEofAsync(stream, header, cancellationToken);
        if (headerBytes == 0)
        {
            return null;
        }

        if (headerBytes != header.Length)
        {
            throw new EndOfStreamException("The pipe closed in the middle of a frame header.");
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > IpcProtocol.MaxMessageBytes)
        {
            throw new InvalidDataException($"Invalid frame length: {length}.");
        }

        var payload = new byte[length];
        var payloadBytes = await ReadExactlyOrEofAsync(stream, payload, cancellationToken);
        if (payloadBytes != payload.Length)
        {
            throw new EndOfStreamException("The pipe closed in the middle of a frame payload.");
        }

        return JsonSerializer.Deserialize<IpcEnvelope>(payload, IpcProtocol.JsonOptions)
            ?? throw new InvalidDataException("The frame did not contain an IPC envelope.");
    }

    private static async ValueTask<int> ReadExactlyOrEofAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer,
                total,
                buffer.Length - total,
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
