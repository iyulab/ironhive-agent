using System.Threading.Channels;

namespace IronHive.DeepResearch.Orchestration.Agents;

/// <summary>
/// <see cref="IProgress{T}"/>는 생성 시점에 캡처한 SynchronizationContext를 통해서만 전달 순서를
/// 보장한다. 캡처된 컨텍스트가 없으면(콘솔/에이전트 호스트에서는 흔한 경우) 기본 SynchronizationContext가
/// ThreadPool.QueueUserWorkItem으로 폴백하는데, 이 경로는 동시에 이루어진 Report() 호출 간 전달
/// 순서를 보장하지 않는다 — 병렬로 완료되는 작업들의 진행률(카운트 등 단조 증가 값)을 보고할 때
/// 소비자가 값이 감소하는 것처럼 보이는 순서로 콜백을 받을 수 있다.
///
/// 이 타입은 <see cref="Report"/> 호출을 단일 리더 채널을 통해 릴레이해, 호출이 들어온 순서
/// 그대로 <see cref="IProgress{T}"/> 콜백에 전달되도록 보장한다. 여러 스레드에서 동시에
/// <see cref="Report"/>를 호출하는 경우, 그 값을 계산하고 채널에 적재하는 구간은 호출자가
/// 자신의 공유 상태를 보호하는 락으로 직렬화해야 한다 — 이 타입은 "적재된 순서대로 전달"만
/// 보장하며, 값 계산 자체의 원자성은 책임지지 않는다.
/// </summary>
internal sealed class OrderedProgressReporter<T>
{
    private readonly Channel<T>? _channel;
    private readonly Task _pump;

    public OrderedProgressReporter(IProgress<T>? progress)
    {
        if (progress is null)
        {
            _channel = null;
            _pump = Task.CompletedTask;
            return;
        }

        _channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true });
        _pump = PumpAsync(_channel, progress);
    }

    private static async Task PumpAsync(Channel<T> channel, IProgress<T> progress)
    {
        await foreach (var update in channel.Reader.ReadAllAsync(CancellationToken.None))
        {
            progress.Report(update);
        }
    }

    /// <summary>
    /// 진행률 값을 큐에 적재한다. 동시 호출 간 상대적 순서를 의미 있게 만들려면 호출자가 값을
    /// 계산하는 지점을 자신의 락으로 직렬화해야 한다.
    /// </summary>
    public void Report(T value) => _channel?.Writer.TryWrite(value);

    /// <summary>
    /// 큐에 남은 항목을 전부 콜백에 전달할 때까지 대기한 뒤 완료한다. 이 작업이 끝나기 전에는
    /// 이 인스턴스로 보고한 모든 값이 콜백에 도달했다고 보장할 수 없다.
    /// </summary>
    public async Task CompleteAsync()
    {
        _channel?.Writer.TryComplete();
        await _pump;
    }
}
