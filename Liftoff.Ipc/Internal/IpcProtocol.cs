using System.Runtime.Serialization;
using System.Text;
using System.Xml;

namespace Liftoff.Ipc.Internal;

internal static class IpcProtocol
{
    public const int MaxMessageBytes = 1024 * 1024;
    public const byte Version = 1;
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

internal static class IpcSerializer
{
    public static byte[] Serialize<T>(T value) => Serialize(value, typeof(T));

    public static byte[] Serialize(object? value, Type type)
    {
        using var output = new MemoryStream();
        using (var writer = XmlDictionaryWriter.CreateBinaryWriter(output, null, null, false))
        {
            new DataContractSerializer(type).WriteObject(writer, value);
        }

        return output.ToArray();
    }

    public static T Deserialize<T>(byte[] payload)
    {
        using var input = new MemoryStream(payload, writable: false);
        using var reader = XmlDictionaryReader.CreateBinaryReader(
            input,
            XmlDictionaryReaderQuotas.Max);
        var value = new DataContractSerializer(typeof(T)).ReadObject(reader);
        return value is T typed
            ? typed
            : throw new InvalidDataException($"The payload contained no {typeof(T).Name} value.");
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

internal sealed record IpcEnvelope(string Type, Guid? RequestId, byte[] Payload)
{
    public static IpcEnvelope Create<T>(string type, Guid? requestId, T payload) =>
        new(type, requestId, IpcSerializer.Serialize(payload));

    public static IpcEnvelope CreateEmpty(string type, Guid? requestId) =>
        new(type, requestId, Array.Empty<byte>());

    public T ReadPayload<T>() => IpcSerializer.Deserialize<T>(Payload);
}

[DataContract]
internal sealed record ExecuteRequest(
    [property: DataMember(Order = 1)] string Contract,
    [property: DataMember(Order = 2)] byte[] Arguments);
[DataContract]
internal sealed record RequestAccepted(
    [property: DataMember(Order = 1)] DateTimeOffset AcceptedAt);
[DataContract]
internal sealed record OperationProgress(
    [property: DataMember(Order = 1)] double Percent,
    [property: DataMember(Order = 2)] string Message);
[DataContract]
internal sealed record OperationCompleted(
    [property: DataMember(Order = 1)] byte[] Result);
[DataContract]
internal sealed record OperationFailed(
    [property: DataMember(Order = 1)] string Error);
[DataContract]
internal sealed record OperationCancelled(
    [property: DataMember(Order = 1)] string Reason);
[DataContract]
internal sealed record Heartbeat(
    [property: DataMember(Order = 1)] DateTimeOffset SentAt);
[DataContract]
internal sealed record SubscribeRequest(
    [property: DataMember(Order = 1)] string EventContract);
[DataContract]
internal sealed record SubscriptionAccepted(
    [property: DataMember(Order = 1)] string EventContract,
    [property: DataMember(Order = 2)] DateTimeOffset AcceptedAt);
[DataContract]
internal sealed record SubscriptionRejected(
    [property: DataMember(Order = 1)] string Error);
[DataContract]
internal sealed record UnsubscriptionAccepted(
    [property: DataMember(Order = 1)] DateTimeOffset AcceptedAt);
[DataContract]
internal sealed record EventPublished(
    [property: DataMember(Order = 1)] byte[] Data);
[DataContract]
internal sealed record AuthenticationHello(
    [property: DataMember(Order = 1)] byte[] ClientNonce);
[DataContract]
internal sealed record AuthenticationChallenge(
    [property: DataMember(Order = 1)] byte[] ServerNonce,
    [property: DataMember(Order = 2)] byte[] ServerProof);
[DataContract]
internal sealed record AuthenticationProof(
    [property: DataMember(Order = 1)] byte[] ClientProof);
[DataContract]
internal sealed record AuthenticationAccepted(
    [property: DataMember(Order = 1)] DateTimeOffset AcceptedAt);

internal static class ProtocolFraming
{
    public static async Task WriteAsync(
        Stream stream,
        IpcEnvelope message,
        CancellationToken cancellationToken = default)
    {
        var type = Encoding.UTF8.GetBytes(message.Type);
        if (type.Length > ushort.MaxValue)
        {
            throw new InvalidDataException("The message type name is too long.");
        }

        var length = 1 + 2 + type.Length + 1 + (message.RequestId.HasValue ? 16 : 0)
            + 4 + message.Payload.Length;
        if (length > IpcProtocol.MaxMessageBytes)
        {
            throw new InvalidDataException(
                $"Message is {length} bytes; maximum is {IpcProtocol.MaxMessageBytes}.");
        }

        var frame = new byte[4 + length];
        var offset = 0;
        WriteInt32(frame, ref offset, length);
        frame[offset++] = IpcProtocol.Version;
        WriteUInt16(frame, ref offset, (ushort)type.Length);
        Buffer.BlockCopy(type, 0, frame, offset, type.Length);
        offset += type.Length;
        frame[offset++] = message.RequestId.HasValue ? (byte)1 : (byte)0;
        if (message.RequestId is Guid requestId)
        {
            var requestIdBytes = requestId.ToByteArray();
            Buffer.BlockCopy(requestIdBytes, 0, frame, offset, requestIdBytes.Length);
            offset += requestIdBytes.Length;
        }

        WriteInt32(frame, ref offset, message.Payload.Length);
        Buffer.BlockCopy(message.Payload, 0, frame, offset, message.Payload.Length);
        await stream.WriteAsync(frame, 0, frame.Length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IpcEnvelope?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        var headerBytes = await ReadExactlyOrEofAsync(stream, header, cancellationToken)
            .ConfigureAwait(false);
        if (headerBytes == 0)
        {
            return null;
        }

        if (headerBytes != header.Length)
        {
            throw new EndOfStreamException("The pipe closed in the middle of a frame header.");
        }

        var offset = 0;
        var length = ReadInt32(header, ref offset);
        if (length <= 0 || length > IpcProtocol.MaxMessageBytes)
        {
            throw new InvalidDataException($"Invalid frame length: {length}.");
        }

        var body = new byte[length];
        if (await ReadExactlyOrEofAsync(stream, body, cancellationToken).ConfigureAwait(false) != length)
        {
            throw new EndOfStreamException("The pipe closed in the middle of a frame payload.");
        }

        offset = 0;
        if (body[offset++] != IpcProtocol.Version)
        {
            throw new InvalidDataException("Unsupported IPC protocol version.");
        }

        var typeLength = ReadUInt16(body, ref offset);
        EnsureRemaining(body, offset, typeLength + 1);
        var type = Encoding.UTF8.GetString(body, offset, typeLength);
        offset += typeLength;
        var hasRequestId = body[offset++];
        Guid? requestId = null;
        if (hasRequestId == 1)
        {
            EnsureRemaining(body, offset, 16);
            var requestIdBytes = new byte[16];
            Buffer.BlockCopy(body, offset, requestIdBytes, 0, 16);
            requestId = new Guid(requestIdBytes);
            offset += 16;
        }
        else if (hasRequestId != 0)
        {
            throw new InvalidDataException("Invalid request ID marker.");
        }

        EnsureRemaining(body, offset, 4);
        var payloadLength = ReadInt32(body, ref offset);
        if (payloadLength < 0 || payloadLength != body.Length - offset)
        {
            throw new InvalidDataException("Invalid message payload length.");
        }

        var payload = new byte[payloadLength];
        Buffer.BlockCopy(body, offset, payload, 0, payloadLength);
        return new IpcEnvelope(type, requestId, payload);
    }

    private static async Task<int> ReadExactlyOrEofAsync(
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
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static void EnsureRemaining(byte[] buffer, int offset, int required)
    {
        if (required < 0 || offset < 0 || required > buffer.Length - offset)
        {
            throw new InvalidDataException("The IPC frame is malformed.");
        }
    }

    private static void WriteInt32(byte[] buffer, ref int offset, int value)
    {
        buffer[offset++] = (byte)(value >> 24);
        buffer[offset++] = (byte)(value >> 16);
        buffer[offset++] = (byte)(value >> 8);
        buffer[offset++] = (byte)value;
    }

    private static int ReadInt32(byte[] buffer, ref int offset)
    {
        EnsureRemaining(buffer, offset, 4);
        return (buffer[offset++] << 24)
            | (buffer[offset++] << 16)
            | (buffer[offset++] << 8)
            | buffer[offset++];
    }

    private static void WriteUInt16(byte[] buffer, ref int offset, ushort value)
    {
        buffer[offset++] = (byte)(value >> 8);
        buffer[offset++] = (byte)value;
    }

    private static ushort ReadUInt16(byte[] buffer, ref int offset)
    {
        EnsureRemaining(buffer, offset, 2);
        return (ushort)((buffer[offset++] << 8) | buffer[offset++]);
    }
}
