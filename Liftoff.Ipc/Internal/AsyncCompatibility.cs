namespace Liftoff.Ipc.Internal;

internal static class AsyncCompatibility
{
    public static Task CancelAsync(CancellationTokenSource source)
    {
#if NETFRAMEWORK
        source.Cancel();
        return Task.CompletedTask;
#else
        return source.CancelAsync();
#endif
    }

    public static async Task WaitWithCancellationAsync(
        Task task,
        CancellationToken cancellationToken)
    {
#if NETFRAMEWORK
        if (!cancellationToken.CanBeCanceled)
        {
            await task;
            return;
        }

        var cancellation = Task.Delay(Timeout.Infinite, cancellationToken);
        if (await Task.WhenAny(task, cancellation) != task)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        await task;
#else
        await task.WaitAsync(cancellationToken);
#endif
    }

    public static async Task<T> WaitWithCancellationAsync<T>(
        Task<T> task,
        CancellationToken cancellationToken)
    {
#if NETFRAMEWORK
        if (!cancellationToken.CanBeCanceled)
        {
            return await task;
        }

        var cancellation = Task.Delay(Timeout.Infinite, cancellationToken);
        if (await Task.WhenAny(task, cancellation) != task)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return await task;
#else
        return await task.WaitAsync(cancellationToken);
#endif
    }

    public static async Task<T> WaitWithTimeoutAsync<T>(
        Task<T> task,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
#if NETFRAMEWORK
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        if (await Task.WhenAny(task, timeoutTask) != task)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"The operation did not complete within {timeout}.");
        }

        return await task;
#else
        return await task.WaitAsync(timeout, cancellationToken);
#endif
    }
}
