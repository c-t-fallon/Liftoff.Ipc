using System.Diagnostics;
using IpcDemo.Contracts;
using Liftoff.Ipc;

namespace IpcDemo.Server;

public sealed class AnalyzeModelHandler
    : IIpcRequestHandler<AnalyzeModelRequest, AnalyzeModelResult>
{
    public async Task<AnalyzeModelResult> HandleAsync(
        AnalyzeModelRequest request,
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        if (request.Steps is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.Steps),
                "Steps must be between 1 and 100.");
        }

        var stopwatch = Stopwatch.StartNew();
        for (var step = 1; step <= request.Steps; step++)
        {
            await Task.Delay(request.DelayMilliseconds, cancellationToken);
            await context.ReportProgressAsync(
                step * 100d / request.Steps,
                $"Analyzed batch {step} of {request.Steps}.",
                cancellationToken);
        }

        if (request.ShouldFail)
        {
            throw new InvalidOperationException("The demo operation was asked to fail.");
        }

        return new AnalyzeModelResult(
            request.ModelName,
            ElementsAnalyzed: request.Steps * 125,
            stopwatch.Elapsed);
    }
}
