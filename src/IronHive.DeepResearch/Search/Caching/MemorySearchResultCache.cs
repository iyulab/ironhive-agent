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
public class MemorySearchResultCache : ISearchResultCache
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
            _logger.LogDebug("Cache hit: {CacheKey}", cacheKey);
            return true;
        }

        _logger.LogDebug("Cache miss: {CacheKey}", cacheKey);
        result = null;
        return false;
    }

    public void Set(string cacheKey, SearchResult result, TimeSpan? expiration = null)
    {
        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiration ?? _defaultExpiration,
            SlidingExpiration = TimeSpan.FromMinutes(15),
            Size = 1 // 각 항목을 1 단위로 계산
        };

        _cache.Set(cacheKey, result, options);
        _logger.LogDebug("Cached search result: {CacheKey}, Expiration: {Expiration}",
            cacheKey, expiration ?? _defaultExpiration);
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
        _logger.LogDebug("Cache invalidated: {CacheKey}", cacheKey);
    }

    public void Clear()
    {
        // IMemoryCache는 전체 클리어를 직접 지원하지 않음
        // 실제로는 MemoryCache를 새로 생성하거나 특정 패턴의 키들을 추적해야 함
        _logger.LogWarning("Clear not fully supported for IMemoryCache");
    }
}
