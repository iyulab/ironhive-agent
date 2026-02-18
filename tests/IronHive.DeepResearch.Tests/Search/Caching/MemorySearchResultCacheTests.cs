using FluentAssertions;
using IronHive.DeepResearch.Models.Search;
using IronHive.DeepResearch.Options;
using IronHive.DeepResearch.Search.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IronHive.Flux.Tests.DeepResearch.Search.Caching;

public class MemorySearchResultCacheTests : IDisposable
{
    private readonly MemorySearchResultCache _cache;
    private readonly MemoryCache _memoryCache;

    public MemorySearchResultCacheTests()
    {
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new DeepResearchOptions();
        _cache = new MemorySearchResultCache(
            _memoryCache,
            options,
            NullLogger<MemorySearchResultCache>.Instance);
    }

    public void Dispose()
    {
        _memoryCache.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GenerateKey_SameQuery_ReturnsSameKey()
    {
        // Arrange
        var query1 = new SearchQuery { Query = "test query" };
        var query2 = new SearchQuery { Query = "test query" };

        // Act
        var key1 = _cache.GenerateKey(query1);
        var key2 = _cache.GenerateKey(query2);

        // Assert
        key1.Should().Be(key2);
        key1.Should().StartWith("search:");
    }

    [Fact]
    public void GenerateKey_DifferentQueries_ReturnsDifferentKeys()
    {
        // Arrange
        var query1 = new SearchQuery { Query = "test query 1" };
        var query2 = new SearchQuery { Query = "test query 2" };

        // Act
        var key1 = _cache.GenerateKey(query1);
        var key2 = _cache.GenerateKey(query2);

        // Assert
        key1.Should().NotBe(key2);
    }

    [Fact]
    public void GenerateKey_DifferentDepth_ReturnsDifferentKeys()
    {
        // Arrange
        var query1 = new SearchQuery { Query = "test", Depth = QueryDepth.Basic };
        var query2 = new SearchQuery { Query = "test", Depth = QueryDepth.Deep };

        // Act
        var key1 = _cache.GenerateKey(query1);
        var key2 = _cache.GenerateKey(query2);

        // Assert
        key1.Should().NotBe(key2);
    }

    [Fact]
    public void GenerateKey_DomainsOrderIndependent_ReturnsSameKey()
    {
        // Arrange
        var query1 = new SearchQuery
        {
            Query = "test",
            IncludeDomains = ["a.com", "b.com", "c.com"]
        };
        var query2 = new SearchQuery
        {
            Query = "test",
            IncludeDomains = ["c.com", "a.com", "b.com"]
        };

        // Act
        var key1 = _cache.GenerateKey(query1);
        var key2 = _cache.GenerateKey(query2);

        // Assert
        key1.Should().Be(key2);
    }

    [Fact]
    public void TryGet_NotCached_ReturnsFalse()
    {
        // Arrange
        var key = "non-existent-key";

        // Act
        var result = _cache.TryGet(key, out var cached);

        // Assert
        result.Should().BeFalse();
        cached.Should().BeNull();
    }

    [Fact]
    public void Set_ThenGet_ReturnsValue()
    {
        // Arrange
        var query = new SearchQuery { Query = "test" };
        var key = _cache.GenerateKey(query);
        var searchResult = new SearchResult
        {
            Query = query,
            Provider = "test-provider",
            Sources = [],
            SearchedAt = DateTimeOffset.UtcNow
        };

        // Act
        _cache.SetEntry(key, searchResult);
        var result = _cache.TryGet(key, out var cached);

        // Assert
        result.Should().BeTrue();
        cached.Should().NotBeNull();
        cached!.Provider.Should().Be("test-provider");
    }

    [Fact]
    public void Invalidate_RemovesCachedItem()
    {
        // Arrange
        var query = new SearchQuery { Query = "test" };
        var key = _cache.GenerateKey(query);
        var searchResult = new SearchResult
        {
            Query = query,
            Provider = "test-provider",
            Sources = [],
            SearchedAt = DateTimeOffset.UtcNow
        };

        _cache.SetEntry(key, searchResult);
        _cache.TryGet(key, out _).Should().BeTrue();

        // Act
        _cache.Invalidate(key);

        // Assert
        _cache.TryGet(key, out _).Should().BeFalse();
    }

    [Fact]
    public void Set_WithCustomExpiration_Works()
    {
        // Arrange
        var query = new SearchQuery { Query = "test" };
        var key = _cache.GenerateKey(query);
        var searchResult = new SearchResult
        {
            Query = query,
            Provider = "test-provider",
            Sources = [],
            SearchedAt = DateTimeOffset.UtcNow
        };

        // Act
        _cache.SetEntry(key, searchResult, TimeSpan.FromMinutes(5));
        var result = _cache.TryGet(key, out var cached);

        // Assert
        result.Should().BeTrue();
        cached.Should().NotBeNull();
    }
}
