using IronHive.Abstractions.Messages;
using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Adapters;
using IronHive.DeepResearch.Autonomous;
using IronHive.DeepResearch.Content;
using IronHive.DeepResearch.Options;
using IronHive.DeepResearch.Orchestration;
using IronHive.DeepResearch.Orchestration.Agents;
using IronHive.DeepResearch.Search;
using IronHive.DeepResearch.Search.Caching;
using IronHive.DeepResearch.Search.Providers;
using IronHive.DeepResearch.Search.QueryExpansion;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using WebFlux.Extensions;

namespace IronHive.DeepResearch.Extensions;

/// <summary>
/// DI 확장 메서드
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// DeepResearch 서비스 등록 (ITextGenerationService가 이미 등록된 경우)
    /// </summary>
    public static IServiceCollection AddDeepResearch(
        this IServiceCollection services,
        Action<DeepResearchOptions>? configureOptions = null)
    {
        // 옵션 등록
        var options = new DeepResearchOptions();
        configureOptions?.Invoke(options);
        services.AddSingleton(options);

        // 메모리 캐시 추가
        services.AddMemoryCache();

        // === Phase 1: 검색 인프라 ===
        services.AddSingleton<ISearchResultCache, MemorySearchResultCache>();
        AddResilientHttpClient<TavilySearchProvider>(services, options);
        services.AddSingleton<ISearchProvider, TavilySearchProvider>();
        services.AddSingleton<SearchProviderFactory>();

        // === Phase 2: 콘텐츠 추출 ===
        services.AddSingleton<ContentChunker>();

        if (options.UseWebFluxPackage)
        {
            services.AddWebFlux();
            services.AddSingleton<IContentExtractor, WebFluxIntegratedContentExtractor>();
        }
        else
        {
            services.AddSingleton<ContentProcessor>();
            AddResilientHttpClient<WebFluxContentExtractor>(services, options);
            services.AddSingleton<IContentExtractor, WebFluxContentExtractor>();
        }

        // === Phase 3: 쿼리 계획 ===
        services.AddSingleton<IQueryExpander, LLMQueryExpander>();
        services.AddSingleton<QueryPlannerAgent>();

        // === Phase 4: 검색 실행 ===
        services.AddSingleton<SearchCoordinatorAgent>();

        // === Phase 5: 콘텐츠 강화 ===
        services.AddSingleton<ContentEnrichmentAgent>();

        // === Phase 6: 분석 및 충분성 평가 ===
        services.AddSingleton<AnalysisAgent>();

        // === Phase 7: 보고서 생성 ===
        services.AddSingleton<ReportGeneratorAgent>();

        // === Phase 8: 오케스트레이터 및 통합 ===
        services.AddSingleton<ResearchOrchestrator>();
        services.AddSingleton<IDeepResearcher, DeepResearcher>();

        // === Phase 9: Autonomous 오케스트레이션 (Ironbees.Autonomous 통합) ===
        services.AddSingleton<ResearchTaskExecutor>();
        services.AddSingleton<ResearchOracleVerifier>();
        services.AddSingleton<AutonomousResearchRunner>();

        return services;
    }

    /// <summary>
    /// DeepResearch 서비스 등록 (IChatClient 기반)
    /// </summary>
    public static IServiceCollection AddDeepResearch(
        this IServiceCollection services,
        IChatClient chatClient,
        Action<DeepResearchOptions>? configureOptions = null)
    {
        services.AddSingleton<ITextGenerationService>(sp =>
        {
            var callback = sp.GetService<IResearchUsageCallback>();
            return new ChatClientTextGenerationAdapter(chatClient, callback);
        });

        return services.AddDeepResearch(configureOptions);
    }

    /// <summary>
    /// DeepResearch 서비스 등록 (IMessageGenerator 기반)
    /// </summary>
    public static IServiceCollection AddDeepResearch(
        this IServiceCollection services,
        IMessageGenerator generator,
        string modelId,
        Action<DeepResearchOptions>? configureOptions = null)
    {
        services.AddSingleton<ITextGenerationService>(sp =>
        {
            var callback = sp.GetService<IResearchUsageCallback>();
            return new IronHiveTextGenerationAdapter(generator, modelId, callback);
        });

        return services.AddDeepResearch(configureOptions);
    }

    /// <summary>
    /// Tavily 검색 프로바이더만 등록 (단독 사용 시)
    /// </summary>
    public static IServiceCollection AddTavilySearchProvider(
        this IServiceCollection services,
        Action<DeepResearchOptions>? configureOptions = null)
    {
        var options = new DeepResearchOptions();
        configureOptions?.Invoke(options);
        services.AddSingleton(options);

        services.AddMemoryCache();
        services.AddSingleton<ISearchResultCache, MemorySearchResultCache>();

        AddResilientHttpClient<TavilySearchProvider>(services, options);
        services.AddSingleton<ISearchProvider, TavilySearchProvider>();

        return services;
    }

    /// <summary>
    /// 콘텐츠 추출기만 등록 (단독 사용 시)
    /// </summary>
    public static IServiceCollection AddContentExtractor(
        this IServiceCollection services,
        Action<DeepResearchOptions>? configureOptions = null)
    {
        var options = new DeepResearchOptions();
        configureOptions?.Invoke(options);
        services.AddSingleton(options);

        services.AddSingleton<ContentChunker>();

        if (options.UseWebFluxPackage)
        {
            services.AddWebFlux();
            services.AddSingleton<IContentExtractor, WebFluxIntegratedContentExtractor>();
        }
        else
        {
            services.AddSingleton<ContentProcessor>();
            AddResilientHttpClient<WebFluxContentExtractor>(services, options);
            services.AddSingleton<IContentExtractor, WebFluxContentExtractor>();
        }

        return services;
    }

    /// <summary>
    /// 쿼리 계획 에이전트만 등록 (단독 사용 시)
    /// ITextGenerationService 구현체가 별도로 등록되어 있어야 함
    /// </summary>
    public static IServiceCollection AddQueryPlanner(
        this IServiceCollection services,
        Action<DeepResearchOptions>? configureOptions = null)
    {
        var options = new DeepResearchOptions();
        configureOptions?.Invoke(options);
        services.AddSingleton(options);

        services.AddSingleton<IQueryExpander, LLMQueryExpander>();
        services.AddSingleton<QueryPlannerAgent>();

        return services;
    }

    private static void AddResilientHttpClient<TClient>(
        IServiceCollection services,
        DeepResearchOptions options)
        where TClient : class
    {
        services.AddHttpClient<TClient>(client =>
            {
                client.Timeout = options.HttpTimeout;
            })
            .AddStandardResilienceHandler(resilienceOptions =>
            {
                // 재시도 정책 설정
                resilienceOptions.Retry.MaxRetryAttempts = options.MaxRetries;
                resilienceOptions.Retry.Delay = TimeSpan.FromSeconds(1);
                resilienceOptions.Retry.UseJitter = true;
                resilienceOptions.Retry.BackoffType = DelayBackoffType.Exponential;

                // 서킷 브레이커 설정
                resilienceOptions.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                resilienceOptions.CircuitBreaker.FailureRatio = 0.5;
                resilienceOptions.CircuitBreaker.MinimumThroughput = 5;
                resilienceOptions.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);

                // 타임아웃 설정
                resilienceOptions.TotalRequestTimeout.Timeout = options.HttpTimeout * 2;
            });
    }
}
