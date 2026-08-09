using System.Diagnostics;
using System.Security.Cryptography;

namespace Liftoff.Ipc;

public sealed class IpcSession
{
    public const string PipeNameEnvironmentVariable = "LIFTOFF_IPC_PIPE_NAME";
    public const string AuthenticationKeyEnvironmentVariable = "LIFTOFF_IPC_AUTHENTICATION_KEY";

    private const int AuthenticationKeyBytes = 32;
    private readonly byte[] _authenticationKey;

    private IpcSession(string pipeName, byte[] authenticationKey)
    {
        PipeName = pipeName;
        _authenticationKey = authenticationKey;
    }

    public string PipeName { get; }

    public static IpcSession Create(string? pipeName = null)
    {
        pipeName ??= $"Liftoff.Ipc.{Guid.NewGuid():N}";
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("The pipe name cannot be empty.", nameof(pipeName));
        }

        var authenticationKey = new byte[AuthenticationKeyBytes];
        using (var random = RandomNumberGenerator.Create())
        {
            random.GetBytes(authenticationKey);
        }

        return new IpcSession(pipeName, authenticationKey);
    }

    public static IpcSession FromEnvironment()
    {
        var pipeName = Environment.GetEnvironmentVariable(PipeNameEnvironmentVariable);
        var encodedKey = Environment.GetEnvironmentVariable(AuthenticationKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(encodedKey))
        {
            throw new IpcConfigurationException(
                $"Environment variables '{PipeNameEnvironmentVariable}' and " +
                $"'{AuthenticationKeyEnvironmentVariable}' must contain an IPC session.");
        }

        byte[] authenticationKey;
        try
        {
            authenticationKey = Convert.FromBase64String(encodedKey);
        }
        catch (FormatException exception)
        {
            throw new IpcConfigurationException(
                $"Environment variable '{AuthenticationKeyEnvironmentVariable}' is invalid.",
                exception);
        }

        if (authenticationKey.Length != AuthenticationKeyBytes)
        {
            throw new IpcConfigurationException(
                $"Environment variable '{AuthenticationKeyEnvironmentVariable}' is invalid.");
        }

        return new IpcSession(pipeName, authenticationKey);
    }

    public static bool TryFromEnvironment(out IpcSession? session)
    {
        var pipeName = Environment.GetEnvironmentVariable(PipeNameEnvironmentVariable);
        var encodedKey = Environment.GetEnvironmentVariable(AuthenticationKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(pipeName) && string.IsNullOrWhiteSpace(encodedKey))
        {
            session = null;
            return false;
        }

        session = FromEnvironment();
        return true;
    }

    public void ConfigureChildProcess(ProcessStartInfo startInfo)
    {
        if (startInfo is null)
        {
            throw new ArgumentNullException(nameof(startInfo));
        }

        startInfo.UseShellExecute = false;
        startInfo.EnvironmentVariables[PipeNameEnvironmentVariable] = PipeName;
        startInfo.EnvironmentVariables[AuthenticationKeyEnvironmentVariable] =
            Convert.ToBase64String(_authenticationKey);
    }

    public override string ToString() => $"IpcSession {{ PipeName = {PipeName} }}";

    internal byte[] CopyAuthenticationKey() => (byte[])_authenticationKey.Clone();
}
