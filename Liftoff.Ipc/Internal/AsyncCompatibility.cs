namespace Liftoff.Ipc.Internal;

internal static class AsyncCompatibility
{
    public static Task CancelAsync(CancellationTokenSource source)
    {
#if NETFRAMEWORK
        try
        {
            source.Cancel();
        }
        catch (AggregateException exception) when (
            exception.Flatten().InnerExceptions.All(IsClosedHandleIoCancellation))
        {
            // .NET Framework named-pipe cancellation can race with pipe disposal.
            // CancelIoEx then reports a closed SafeHandle even though cancellation
            // was successfully requested. Preserve every other callback failure.
        }

        return Task.CompletedTask;
#else
        return source.CancelAsync();
#endif
    }

#if NETFRAMEWORK
    private static bool IsClosedHandleIoCancellation(Exception error) =>
        error is ObjectDisposedException
        && error.StackTrace?.IndexOf("CancelIoEx", StringComparison.Ordinal) >= 0;
#endif

    public static async Task WaitWithCancellationAsync(
        Task task,
        CancellationToken cancellationToken)
    {
#if NETFRAMEWORK
        if (!cancellationToken.CanBeCanceled)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var cancellation = Task.Delay(Timeout.Infinite, cancellationToken);
        if (await Task.WhenAny(task, cancellation).ConfigureAwait(false) != task)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        await task.ConfigureAwait(false);
#else
        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
#endif
    }

    public static async Task<T> WaitWithCancellationAsync<T>(
        Task<T> task,
        CancellationToken cancellationToken)
    {
#if NETFRAMEWORK
        if (!cancellationToken.CanBeCanceled)
        {
            return await task.ConfigureAwait(false);
        }

        var cancellation = Task.Delay(Timeout.Infinite, cancellationToken);
        if (await Task.WhenAny(task, cancellation).ConfigureAwait(false) != task)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return await task.ConfigureAwait(false);
#else
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
#endif
    }

    public static async Task<T> WaitWithTimeoutAsync<T>(
        Task<T> task,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
#if NETFRAMEWORK
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        if (await Task.WhenAny(task, timeoutTask).ConfigureAwait(false) != task)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"The operation did not complete within {timeout}.");
        }

        return await task.ConfigureAwait(false);
#else
        return await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
#endif
    }
}
