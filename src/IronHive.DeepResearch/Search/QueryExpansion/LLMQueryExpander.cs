using System.Text.Json;
using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Models.Planning;
using Microsoft.Extensions.Logging;

namespace IronHive.DeepResearch.Search.QueryExpansion;

/// <summary>
/// LLM 기반 쿼리 확장기
/// </summary>
public class LLMQueryExpander : IQueryExpander
{
    private readonly ITextGenerationService _textService;
    private readonly ILogger<LLMQueryExpander> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public LLMQueryExpander(
        ITextGenerationService textService,
        ILogger<LLMQueryExpander> logger)
    {
        _textService = textService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubQuestion>> DecomposeQueryAsync(
        string query,
        QueryExpansionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new QueryExpansionOptions();

        var prompt = BuildDecompositionPrompt(query, options);
        var genOptions = new TextGenerationOptions
        {
            Temperature = 0.3,
            MaxTokens = 1500,
            SystemPrompt = GetDecompositionSystemPrompt(options.Language)
        };

        try
        {
            var response = await _textService.GenerateStructuredAsync<DecompositionResponse>(
                prompt, genOptions, cancellationToken);

            if (response?.Questions is null || response.Questions.Count == 0)
            {
                _logger.LogWarning("쿼리 분해 실패: 빈 응답");
                return CreateFallbackSubQuestions(query);
            }

            return response.Questions
                .Take(options.MaxSubQuestions)
                .Select((q, i) => new SubQuestion
                {
                    Id = $"sq_{i + 1}",
                    Question = q.Question,
                    Purpose = q.Intent ?? "정보 수집",
                    Priority = q.Priority,
                    DependsOn = q.DependsOn ?? []
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "쿼리 분해 중 오류 발생");
            return CreateFallbackSubQuestions(query);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ResearchPerspective>> DiscoverPerspectivesAsync(
        string query,
        QueryExpansionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new QueryExpansionOptions();

        var prompt = BuildPerspectivePrompt(query, options);
        var genOptions = new TextGenerationOptions
        {
            Temperature = 0.5,
            MaxTokens = 1500,
            SystemPrompt = GetPerspectiveSystemPrompt(options.Language)
        };

        try
        {
            var response = await _textService.GenerateStructuredAsync<PerspectiveResponse>(
                prompt, genOptions, cancellationToken);

            if (response?.Perspectives is null || response.Perspectives.Count == 0)
            {
                _logger.LogWarning("관점 발견 실패: 빈 응답");
                return CreateFallbackPerspectives();
            }

            return response.Perspectives
                .Take(options.MaxPerspectives)
                .Select((p, i) => new ResearchPerspective
                {
                    Id = $"persp_{i + 1}",
                    Name = p.Name,
                    Description = p.Description,
                    KeyTopics = p.KeyTopics ?? []
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "관점 발견 중 오류 발생");
            return CreateFallbackPerspectives();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExpandedQuery>> ExpandQueriesAsync(
        string originalQuery,
        IReadOnlyList<SubQuestion> subQuestions,
        IReadOnlyList<ResearchPerspective> perspectives,
        QueryExpansionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new QueryExpansionOptions();

        var prompt = BuildExpansionPrompt(originalQuery, subQuestions, perspectives, options);
        var genOptions = new TextGenerationOptions
        {
            Temperature = 0.4,
            MaxTokens = 2000,
            SystemPrompt = GetExpansionSystemPrompt(options.Language)
        };

        try
        {
            var response = await _textService.GenerateStructuredAsync<ExpansionResponse>(
                prompt, genOptions, cancellationToken);

            if (response?.Queries is null || response.Queries.Count == 0)
            {
                _logger.LogWarning("쿼리 확장 실패: 빈 응답");
                return CreateFallbackExpandedQueries(originalQuery, subQuestions);
            }

            return response.Queries
                .Take(options.MaxExpandedQueries)
                .Select(q => new ExpandedQuery
                {
                    Query = q.Query,
                    Intent = q.Intent ?? "정보 탐색",
                    Priority = q.Priority,
                    SearchType = ParseSearchType(q.SearchType),
                    PerspectiveId = q.PerspectiveId,
                    SubQuestionId = q.SubQuestionId
                })
                .OrderBy(q => q.Priority)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "쿼리 확장 중 오류 발생");
            return CreateFallbackExpandedQueries(originalQuery, subQuestions);
        }
    }

    #region Prompt Building

    private static string BuildDecompositionPrompt(string query, QueryExpansionOptions options)
    {
        var langNote = options.Language == "ko"
            ? "모든 출력은 한국어로 작성하세요."
            : $"Write all output in {options.Language}.";

        return $$"""
            사용자 질문: {{query}}

            위 질문을 검색 가능한 하위 질문으로 분해하세요.

            규칙:
            1. 각 하위 질문은 단일 검색으로 답변 가능해야 합니다
            2. 시간적 맥락이 필요하면 명시하세요 (예: "2026년", "최근")
            3. 모호한 용어는 구체화하세요
            4. 5-{{options.MaxSubQuestions}}개 질문을 생성하세요
            5. {{langNote}}

            JSON 형식으로 응답:
            {
              "questions": [
                {
                  "question": "하위 질문",
                  "intent": "이 질문의 의도",
                  "priority": 1-3 (1=최우선),
                  "dependsOn": ["의존하는 다른 질문 인덱스"] // 선택사항
                }
              ]
            }
            """;
    }

    private static string BuildPerspectivePrompt(string query, QueryExpansionOptions options)
    {
        return $$"""
            연구 주제: {{query}}

            이 주제를 다양한 관점에서 탐구하기 위한 리서치 관점을 발견하세요.

            규칙:
            1. 최소 3개, 최대 {{options.MaxPerspectives}}개 관점 제시
            2. 각 관점은 서로 다른 시각을 제공해야 합니다
            3. 기술적, 사회적, 경제적, 윤리적 등 다양한 측면 고려
            4. 각 관점에서 탐색할 핵심 주제를 제시하세요

            JSON 형식으로 응답:
            {
              "perspectives": [
                {
                  "name": "관점 이름",
                  "description": "이 관점에서 무엇을 탐구하는지",
                  "keyTopics": ["탐색할 핵심 주제들"]
                }
              ]
            }
            """;
    }

    private static string BuildExpansionPrompt(
        string originalQuery,
        IReadOnlyList<SubQuestion> subQuestions,
        IReadOnlyList<ResearchPerspective> perspectives,
        QueryExpansionOptions options)
    {
        var subQuestionsJson = JsonSerializer.Serialize(
            subQuestions.Select(q => new { q.Id, q.Question }),
            JsonOptions);

        var perspectivesJson = JsonSerializer.Serialize(
            perspectives.Select(p => new { p.Id, p.Name, p.KeyTopics }),
            JsonOptions);

        var searchTypes = new List<string> { "web" };
        if (options.IncludeNews) searchTypes.Add("news");
        if (options.IncludeAcademic) searchTypes.Add("academic");

        var searchTypesStr = string.Join(", ", searchTypes);

        return $$"""
            원본 질문: {{originalQuery}}

            하위 질문:
            {{subQuestionsJson}}

            리서치 관점:
            {{perspectivesJson}}

            위 정보를 바탕으로 실제 검색 엔진에서 사용할 검색 쿼리를 생성하세요.

            규칙:
            1. 검색 엔진에 최적화된 쿼리 (키워드 중심, 자연어 질문이 아님)
            2. 최대 {{options.MaxExpandedQueries}}개 쿼리 생성
            3. 중복 쿼리 방지
            4. 사용 가능한 검색 유형: {{searchTypesStr}}
            5. 우선순위 1-3 (1=최우선) 할당

            JSON 형식으로 응답:
            {
              "queries": [
                {
                  "query": "검색 쿼리",
                  "intent": "이 쿼리의 의도",
                  "priority": 1-3,
                  "searchType": "web|news|academic",
                  "perspectiveId": "관점 ID (선택)",
                  "subQuestionId": "하위질문 ID (선택)"
                }
              ]
            }
            """;
    }

    private static string GetDecompositionSystemPrompt(string language)
    {
        return language == "ko"
            ? "당신은 복잡한 질문을 검색 가능한 하위 질문으로 분해하는 전문가입니다. 항상 JSON 형식으로 응답하세요."
            : "You are an expert at decomposing complex questions into searchable sub-questions. Always respond in JSON format.";
    }

    private static string GetPerspectiveSystemPrompt(string language)
    {
        return language == "ko"
            ? "당신은 STORM 리서치 방법론의 전문가로, 다양한 관점에서 주제를 탐구합니다. 항상 JSON 형식으로 응답하세요."
            : "You are an expert in STORM research methodology, exploring topics from diverse perspectives. Always respond in JSON format.";
    }

    private static string GetExpansionSystemPrompt(string language)
    {
        return language == "ko"
            ? "당신은 검색 쿼리 최적화 전문가입니다. 검색 엔진에 최적화된 쿼리를 생성합니다. 항상 JSON 형식으로 응답하세요."
            : "You are a search query optimization expert. Generate queries optimized for search engines. Always respond in JSON format.";
    }

    #endregion

    #region Response DTOs

    private record DecompositionResponse
    {
        public List<DecomposedQuestion>? Questions { get; init; }
    }

    private record DecomposedQuestion
    {
        public string Question { get; init; } = "";
        public string? Intent { get; init; }
        public int Priority { get; init; } = 2;
        public List<string>? DependsOn { get; init; }
    }

    private record PerspectiveResponse
    {
        public List<DiscoveredPerspective>? Perspectives { get; init; }
    }

    private record DiscoveredPerspective
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public List<string>? KeyTopics { get; init; }
    }

    private record ExpansionResponse
    {
        public List<GeneratedQuery>? Queries { get; init; }
    }

    private record GeneratedQuery
    {
        public string Query { get; init; } = "";
        public string? Intent { get; init; }
        public int Priority { get; init; } = 2;
        public string? SearchType { get; init; }
        public string? PerspectiveId { get; init; }
        public string? SubQuestionId { get; init; }
    }

    #endregion

    #region Fallback Methods

    private static IReadOnlyList<SubQuestion> CreateFallbackSubQuestions(string query)
    {
        return
        [
            new SubQuestion
            {
                Id = "sq_1",
                Question = query,
                Purpose = "원본 질문 탐색",
                Priority = 1
            }
        ];
    }

    private static IReadOnlyList<ResearchPerspective> CreateFallbackPerspectives()
    {
        return
        [
            new ResearchPerspective
            {
                Id = "persp_1",
                Name = "일반적 관점",
                Description = "주제에 대한 전반적인 이해"
            }
        ];
    }

    private static IReadOnlyList<ExpandedQuery> CreateFallbackExpandedQueries(
        string originalQuery,
        IReadOnlyList<SubQuestion> subQuestions)
    {
        var queries = new List<ExpandedQuery>
        {
            new()
            {
                Query = originalQuery,
                Intent = "원본 쿼리 검색",
                Priority = 1
            }
        };

        // 하위 질문을 검색 쿼리로 변환
        queries.AddRange(subQuestions.Take(5).Select((sq, i) => new ExpandedQuery
        {
            Query = sq.Question,
            Intent = sq.Purpose,
            Priority = sq.Priority,
            SubQuestionId = sq.Id
        }));

        return queries;
    }

    private static QuerySearchType ParseSearchType(string? type)
    {
        return type?.ToLowerInvariant() switch
        {
            "news" => QuerySearchType.News,
            "academic" => QuerySearchType.Academic,
            _ => QuerySearchType.Web
        };
    }

    #endregion
}
