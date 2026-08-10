using System.Security.Cryptography;
using System.Text;

namespace Liftoff.Ipc.Internal;

internal static class IpcAuthenticator
{
    private const int NonceBytes = 32;
    private static readonly byte[] ProtocolLabel = Encoding.UTF8.GetBytes("Liftoff.Ipc/auth/v1");

    public static Task AuthenticateClientAsync(
        Stream stream,
        byte[] authenticationKey,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        AuthenticateWithTimeoutAsync(
            token => AuthenticateClientCoreAsync(stream, authenticationKey, token),
            timeout,
            cancellationToken);

    public static Task AuthenticateServerAsync(
        Stream stream,
        byte[] authenticationKey,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        AuthenticateWithTimeoutAsync(
            token => AuthenticateServerCoreAsync(stream, authenticationKey, token),
            timeout,
            cancellationToken);

    private static async Task AuthenticateClientCoreAsync(
        Stream stream,
        byte[] authenticationKey,
        CancellationToken cancellationToken)
    {
        var clientNonce = CreateRandomBytes(NonceBytes);
        await ProtocolFraming.WriteAsync(
            stream,
            IpcEnvelope.Create(
                MessageTypes.AuthenticationHello,
                null,
                new AuthenticationHello(clientNonce)),
            cancellationToken);

        var challengeEnvelope = await ProtocolFraming.ReadAsync(stream, cancellationToken);
        if (challengeEnvelope?.Type != MessageTypes.AuthenticationChallenge)
        {
            throw new IpcAuthenticationException("The IPC server did not provide an authentication challenge.");
        }

        var challenge = challengeEnvelope.ReadPayload<AuthenticationChallenge>();
        ValidateLength(challenge.ServerNonce, NonceBytes, "server nonce");
        ValidateLength(challenge.ServerProof, 32, "server proof");
        var expectedServerProof = CreateProof(
            authenticationKey,
            "server",
            clientNonce,
            challenge.ServerNonce);
        if (!FixedTimeEquals(expectedServerProof, challenge.ServerProof))
        {
            throw new IpcAuthenticationException("The IPC server could not authenticate the session.");
        }

        var clientProof = CreateProof(
            authenticationKey,
            "client",
            clientNonce,
            challenge.ServerNonce);
        await ProtocolFraming.WriteAsync(
            stream,
            IpcEnvelope.Create(
                MessageTypes.AuthenticationProof,
                null,
                new AuthenticationProof(clientProof)),
            cancellationToken);

        var accepted = await ProtocolFraming.ReadAsync(stream, cancellationToken);
        if (accepted?.Type != MessageTypes.AuthenticationAccepted)
        {
            throw new IpcAuthenticationException("The IPC server rejected the session.");
        }
    }

    private static async Task AuthenticateServerCoreAsync(
        Stream stream,
        byte[] authenticationKey,
        CancellationToken cancellationToken)
    {
        var helloEnvelope = await ProtocolFraming.ReadAsync(stream, cancellationToken);
        if (helloEnvelope?.Type != MessageTypes.AuthenticationHello)
        {
            throw new IpcAuthenticationException("The IPC client did not begin authentication.");
        }

        var hello = helloEnvelope.ReadPayload<AuthenticationHello>();
        ValidateLength(hello.ClientNonce, NonceBytes, "client nonce");
        var serverNonce = CreateRandomBytes(NonceBytes);
        var serverProof = CreateProof(
            authenticationKey,
            "server",
            hello.ClientNonce,
            serverNonce);
        await ProtocolFraming.WriteAsync(
            stream,
            IpcEnvelope.Create(
                MessageTypes.AuthenticationChallenge,
                null,
                new AuthenticationChallenge(serverNonce, serverProof)),
            cancellationToken);

        var proofEnvelope = await ProtocolFraming.ReadAsync(stream, cancellationToken);
        if (proofEnvelope?.Type != MessageTypes.AuthenticationProof)
        {
            throw new IpcAuthenticationException("The IPC client did not complete authentication.");
        }

        var proof = proofEnvelope.ReadPayload<AuthenticationProof>();
        ValidateLength(proof.ClientProof, 32, "client proof");
        var expectedClientProof = CreateProof(
            authenticationKey,
            "client",
            hello.ClientNonce,
            serverNonce);
        if (!FixedTimeEquals(expectedClientProof, proof.ClientProof))
        {
            throw new IpcAuthenticationException("The IPC client could not authenticate the session.");
        }

        await ProtocolFraming.WriteAsync(
            stream,
            IpcEnvelope.Create(
                MessageTypes.AuthenticationAccepted,
                null,
                new AuthenticationAccepted(DateTimeOffset.UtcNow)),
            cancellationToken);
    }

    private static async Task AuthenticateWithTimeoutAsync(
        Func<CancellationToken, Task> authenticate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new IpcConfigurationException("The authentication timeout must be positive.");
        }

        using var authenticationLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        authenticationLifetime.CancelAfter(timeout);
        try
        {
            await authenticate(authenticationLifetime.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IpcAuthenticationException("IPC authentication timed out.", exception);
        }
        catch (IpcAuthenticationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            || exception is InvalidDataException
            || exception is System.Runtime.Serialization.SerializationException
            || exception is System.Xml.XmlException)
        {
            throw new IpcAuthenticationException("IPC authentication failed.", exception);
        }
    }

    private static byte[] CreateRandomBytes(int count)
    {
        var bytes = new byte[count];
        using (var random = RandomNumberGenerator.Create())
        {
            random.GetBytes(bytes);
        }

        return bytes;
    }

    private static byte[] CreateProof(
        byte[] authenticationKey,
        string role,
        byte[] clientNonce,
        byte[] serverNonce)
    {
        var roleBytes = Encoding.UTF8.GetBytes(role);
        var input = new byte[
            ProtocolLabel.Length + roleBytes.Length + clientNonce.Length + serverNonce.Length];
        var offset = 0;
        Buffer.BlockCopy(ProtocolLabel, 0, input, offset, ProtocolLabel.Length);
        offset += ProtocolLabel.Length;
        Buffer.BlockCopy(roleBytes, 0, input, offset, roleBytes.Length);
        offset += roleBytes.Length;
        Buffer.BlockCopy(clientNonce, 0, input, offset, clientNonce.Length);
        offset += clientNonce.Length;
        Buffer.BlockCopy(serverNonce, 0, input, offset, serverNonce.Length);

        using var hmac = new HMACSHA256(authenticationKey);
        return hmac.ComputeHash(input);
    }

    private static bool FixedTimeEquals(byte[] expected, byte[] actual)
    {
        if (expected.Length != actual.Length)
        {
            return false;
        }

        var difference = 0;
        for (var index = 0; index < expected.Length; index++)
        {
            difference |= expected[index] ^ actual[index];
        }

        return difference == 0;
    }

    private static void ValidateLength(byte[]? value, int expectedLength, string field)
    {
        if (value is null || value.Length != expectedLength)
        {
            throw new IpcAuthenticationException($"The authentication {field} is invalid.");
        }
    }
}
