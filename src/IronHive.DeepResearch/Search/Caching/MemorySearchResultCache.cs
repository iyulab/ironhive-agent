using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IronHive.DeepResearch.Models.Search;
using IronHive.DeepResearch.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace IronHive.DeepResearch.Search.Caching;

/// <summary>
/// 메모리 기반 검색 결과 캐시
/// </summary>
public partial class MemorySearchResultCache : ISearchResultCache
{
    private readonly IMemoryCache _cache;
    private readonly DeepResearchOptions _options;
    private readonly ILogger<MemorySearchResultCache> _logger;
    private readonly TimeSpan _defaultExpiration;

    public MemorySearchResultCache(
        IMemoryCache cache,
        DeepResearchOptions options,
        ILogger<MemorySearchResultCache> logger)
    {
        _cache = cache;
        _options = options;
        _logger = logger;
        _defaultExpiration = TimeSpan.FromHours(1);
    }

    public bool TryGet(string cacheKey, out SearchResult? result)
    {
        if (_cache.TryGetValue(cacheKey, out result))
        {
            LogCacheHit(_logger, cacheKey);
            return true;
        }

        LogCacheMiss(_logger, cacheKey);
        result = null;
        return false;
    }

    public void SetEntry(string cacheKey, SearchResult result, TimeSpan? expiration = null)
    {
        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiration ?? _defaultExpiration,
            SlidingExpiration = TimeSpan.FromMinutes(15),
            Size = 1 // 각 항목을 1 단위로 계산
        };

        _cache.Set(cacheKey, result, options);
        LogCachedSearchResult(_logger, cacheKey, expiration ?? _defaultExpiration);
    }

    public string GenerateKey(SearchQuery query)
    {
        // 쿼리의 핵심 속성들을 조합하여 해시 생성
        var keyData = new
        {
            query.Query,
            query.Type,
            query.Depth,
            query.MaxResults,
            IncludeDomains = query.IncludeDomains != null
                ? string.Join(",", query.IncludeDomains.OrderBy(d => d))
                : null,
            ExcludeDomains = query.ExcludeDomains != null
                ? string.Join(",", query.ExcludeDomains.OrderBy(d => d))
                : null
        };

        var json = JsonSerializer.Serialize(keyData);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return $"search:{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }

    public void Invalidate(string cacheKey)
    {
        _cache.Remove(cacheKey);
        LogCacheInvalidated(_logger, cacheKey);
    }

    public void Clear()
    {
        // IMemoryCache는 전체 클리어를 직접 지원하지 않음
        // 실제로는 MemoryCache를 새로 생성하거나 특정 패턴의 키들을 추적해야 함
        LogClearNotSupported(_logger);
    }

    #region LoggerMessage Definitions

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache hit: {CacheKey}")]
    private static partial void LogCacheHit(ILogger logger, string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache miss: {CacheKey}")]
    private static partial void LogCacheMiss(ILogger logger, string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cached search result: {CacheKey}, Expiration: {Expiration}")]
    private static partial void LogCachedSearchResult(ILogger logger, string cacheKey, TimeSpan expiration);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache invalidated: {CacheKey}")]
    private static partial void LogCacheInvalidated(ILogger logger, string cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Clear not fully supported for IMemoryCache")]
    private static partial void LogClearNotSupported(ILogger logger);

    #endregion
}
