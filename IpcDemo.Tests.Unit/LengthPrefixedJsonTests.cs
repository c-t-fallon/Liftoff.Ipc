using System.Buffers.Binary;
using Liftoff.Ipc.Internal;

namespace IpcDemo.Tests.Unit;

public sealed class LengthPrefixedJsonTests
{
    [Fact]
    public async Task Written_message_can_be_read_back()
    {
        var requestId = Guid.NewGuid();
        var expected = IpcEnvelope.Create(
            MessageTypes.OperationProgress,
            requestId,
            new OperationProgress(50, "Halfway there."));
        await using var stream = new MemoryStream();

        await LengthPrefixedJson.WriteAsync(stream, expected);
        stream.Position = 0;
        var actual = await LengthPrefixedJson.ReadAsync(stream);

        Assert.NotNull(actual);
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.RequestId, actual.RequestId);
        Assert.Equal(expected.ReadPayload<OperationProgress>(), actual.ReadPayload<OperationProgress>());
    }

    [Fact]
    public async Task Empty_stream_has_no_message()
    {
        await using var stream = new MemoryStream();

        var message = await LengthPrefixedJson.ReadAsync(stream);

        Assert.Null(message);
    }

    [Fact]
    public async Task Frame_larger_than_protocol_limit_is_rejected()
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, IpcProtocol.MaxMessageBytes + 1);
        await using var stream = new MemoryStream(header);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await LengthPrefixedJson.ReadAsync(stream));

        Assert.Contains("Invalid frame length", exception.Message);
    }
}
