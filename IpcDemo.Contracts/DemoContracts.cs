using System.Runtime.Serialization;
using Liftoff.Ipc;

namespace IpcDemo.Contracts;

public static class DemoIpc
{
    public const string PipeName = "IpcDemo.Pipe";
}

[DataContract]
public sealed record AnalyzeModelRequest(
    [property: DataMember(Order = 1)] string ModelName,
    [property: DataMember(Order = 2)] int Steps = 5,
    [property: DataMember(Order = 3)] int DelayMilliseconds = 500,
    [property: DataMember(Order = 4)] bool ShouldFail = false) : IIpcRequest<AnalyzeModelResult>;

[DataContract]
public sealed record AnalyzeModelResult(
    [property: DataMember(Order = 1)] string ModelName,
    [property: DataMember(Order = 2)] int ElementsAnalyzed,
    [property: DataMember(Order = 3)] TimeSpan Elapsed);

[DataContract]
public sealed record ModelChanged(
    [property: DataMember(Order = 1)] int Sequence,
    [property: DataMember(Order = 2)] string ElementName,
    [property: DataMember(Order = 3)] DateTimeOffset ChangedAt) : IIpcEvent;

[DataContract]
public sealed record GetThemeStateRequest : IIpcRequest<ThemeState>;

[DataContract]
public sealed record ThemeState(
    [property: DataMember(Order = 1)] bool IsDark,
    [property: DataMember(Order = 2)] DateTimeOffset ChangedAt);

[DataContract]
public sealed record ThemeChanged(
    [property: DataMember(Order = 1)] bool IsDark,
    [property: DataMember(Order = 2)] DateTimeOffset ChangedAt) : IIpcEvent;
