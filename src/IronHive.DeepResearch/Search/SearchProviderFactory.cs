using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Options;
using Microsoft.Extensions.Logging;

namespace IronHive.DeepResearch.Search;

/// <summary>
/// 검색 프로바이더 팩토리
/// </summary>
public partial class SearchProviderFactory
{
    private readonly Dictionary<string, ISearchProvider> _providers;
    private readonly DeepResearchOptions _options;
    private readonly ILogger<SearchProviderFactory> _logger;

    public SearchProviderFactory(
        IEnumerable<ISearchProvider> providers,
        DeepResearchOptions options,
        ILogger<SearchProviderFactory> logger)
    {
        _providers = providers.ToDictionary(p => p.ProviderId, StringComparer.OrdinalIgnoreCase);
        _options = options;
        _logger = logger;

        if (_logger.IsEnabled(LogLevel.Information))
        {
            var providerNames = string.Join(", ", _providers.Keys);
            LogFactoryInitialized(_logger, _providers.Count, providerNames);
        }
    }

    /// <summary>
    /// 등록된 모든 프로바이더 ID 조회
    /// </summary>
    public IReadOnlyCollection<string> AvailableProviders => _providers.Keys.ToList().AsReadOnly();

    /// <summary>
    /// 기본 프로바이더 조회
    /// </summary>
    public ISearchProvider GetDefaultProvider()
    {
        return GetProvider(_options.DefaultSearchProvider);
    }

    /// <summary>
    /// 특정 프로바이더 조회
    /// </summary>
    public ISearchProvider GetProvider(string providerId)
    {
        if (_providers.TryGetValue(providerId, out var provider))
        {
            return provider;
        }

        LogProviderNotFound(_logger, providerId, string.Join(", ", _providers.Keys));
        throw new InvalidOperationException(
            $"Search provider '{providerId}' not found. Available providers: {string.Join(", ", _providers.Keys)}");
    }

    /// <summary>
    /// 프로바이더 존재 여부 확인
    /// </summary>
    public bool HasProvider(string providerId)
    {
        return _providers.ContainsKey(providerId);
    }

    /// <summary>
    /// 특정 기능을 지원하는 프로바이더 조회
    /// </summary>
    public IReadOnlyList<ISearchProvider> GetProvidersWithCapability(SearchCapabilities capability)
    {
        return _providers.Values
            .Where(p => p.Capabilities.HasFlag(capability))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// 검색 유형에 적합한 프로바이더 선택
    /// </summary>
    public ISearchProvider SelectProviderForSearchType(Models.Search.SearchType searchType)
    {
        var capability = searchType switch
        {
            Models.Search.SearchType.Web => SearchCapabilities.WebSearch,
            Models.Search.SearchType.News => SearchCapabilities.NewsSearch,
            Models.Search.SearchType.Academic => SearchCapabilities.AcademicSearch,
            Models.Search.SearchType.Image => SearchCapabilities.ImageSearch,
            _ => SearchCapabilities.WebSearch
        };

        var providers = GetProvidersWithCapability(capability);

        if (providers.Count == 0)
        {
            LogNoProviderForCapability(_logger, capability);
            return GetDefaultProvider();
        }

        // 기본 프로바이더가 해당 기능을 지원하면 우선 사용
        if (_providers.TryGetValue(_options.DefaultSearchProvider, out var defaultProvider) &&
            defaultProvider.Capabilities.HasFlag(capability))
        {
            return defaultProvider;
        }

        return providers[0];
    }

    #region LoggerMessage Definitions

    [LoggerMessage(Level = LogLevel.Information, Message = "SearchProviderFactory initialized with {Count} providers: {Providers}")]
    private static partial void LogFactoryInitialized(ILogger logger, int count, string providers);

    [LoggerMessage(Level = LogLevel.Error, Message = "Search provider not found: {ProviderId}. Available: {Available}")]
    private static partial void LogProviderNotFound(ILogger logger, string providerId, string available);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No provider found for capability {Capability}, using default")]
    private static partial void LogNoProviderForCapability(ILogger logger, SearchCapabilities capability);

    #endregion
}
