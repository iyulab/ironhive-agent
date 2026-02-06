using MemoryIndexer.Interfaces;

namespace IronHive.Agent.Memory;

/// <summary>
/// Default implementation of ISessionMemoryService using MemoryIndexer.
/// </summary>
public class SessionMemoryService : ISessionMemoryService
{
    private readonly IMemoryService _memoryService;
    private string _sessionId;
    private readonly string _userId;

    public SessionMemoryService(IMemoryService memoryService, string? userId = null)
    {
        _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
        _userId = userId ?? Environment.UserName;
        _sessionId = GenerateSessionId();
    }

    /// <inheritdoc />
    public string SessionId => _sessionId;

    /// <inheritdoc />
    public string UserId => _userId;

    /// <inheritdoc />
    public async Task RememberUserMessageAsync(string content, CancellationToken cancellationToken = default)
    {
        await _memoryService.RememberAsync(_userId, _sessionId, content, "user", cancellationToken);
    }

    /// <inheritdoc />
    public async Task RememberAssistantMessageAsync(string content, CancellationToken cancellationToken = default)
    {
        await _memoryService.RememberAsync(_userId, _sessionId, content, "assistant", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MemoryRecallResult> RecallAsync(string query, int limit = 10, CancellationToken cancellationToken = default)
    {
        var context = await _memoryService.RecallAsync(_userId, _sessionId, query, limit, cancellationToken);

        return new MemoryRecallResult
        {
            UserMemories = context.UserMemories
                .Select(m => new MemoryItem
                {
                    Content = m.Content,
                    Role = m.Role,
                    Score = m.ImportanceScore,
                    CreatedAt = new DateTimeOffset(m.CreatedAt, TimeSpan.Zero)
                })
                .ToList(),
            SessionMemories = context.SessionMemories
                .Select(m => new MemoryItem
                {
                    Content = m.Content,
                    Role = m.Role,
                    Score = m.ImportanceScore,
                    CreatedAt = new DateTimeOffset(m.CreatedAt, TimeSpan.Zero)
                })
                .ToList()
        };
    }

    /// <inheritdoc />
    public void StartSession(string? sessionId = null)
    {
        _sessionId = sessionId ?? GenerateSessionId();
    }

    /// <inheritdoc />
    public async Task EndSessionAsync(CancellationToken cancellationToken = default)
    {
        await _memoryService.EndSessionAsync(_userId, _sessionId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearSessionAsync(CancellationToken cancellationToken = default)
    {
        await _memoryService.ForgetSessionAsync(_userId, _sessionId, cancellationToken);
    }

    private static string GenerateSessionId()
    {
        return $"session-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
    }
}
