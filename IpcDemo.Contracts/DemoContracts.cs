using Liftoff.Ipc;

namespace IpcDemo.Contracts;

public static class DemoIpc
{
    public const string PipeName = "IpcDemo.Pipe";
}

public sealed record AnalyzeModelRequest(
    string ModelName,
    int Steps = 5,
    int DelayMilliseconds = 500,
    bool ShouldFail = false) : IIpcRequest<AnalyzeModelResult>;

public sealed record AnalyzeModelResult(
    string ModelName,
    int ElementsAnalyzed,
    TimeSpan Elapsed);

public sealed record ModelChanged(
    int Sequence,
    string ElementName,
    DateTimeOffset ChangedAt) : IIpcEvent;
