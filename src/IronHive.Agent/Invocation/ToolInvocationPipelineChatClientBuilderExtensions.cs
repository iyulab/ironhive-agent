using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IronHive.Agent.Invocation;

/// <summary>Installs a <see cref="ToolInvocationPipeline"/> on a chat client pipeline.</summary>
public static class ToolInvocationPipelineChatClientBuilderExtensions
{
    /// <summary>
    /// Adds function invocation (a <see cref="FunctionInvokingChatClient"/>, in place of <c>UseFunctionInvocation()</c>)
    /// that runs every tool call through the container's <see cref="ToolInvocationPipeline"/> — registered by
    /// <c>AddIronHiveAgent</c> and assembled from the middleware registered in the container. Build the client with the
    /// container: <c>.Build(serviceProvider)</c>.
    /// </summary>
    /// <param name="builder">The chat client builder.</param>
    /// <param name="configure">
    /// Configures the function-invoking client (iteration and error limits, detailed errors). The
    /// <see cref="FunctionInvokingChatClient.FunctionInvoker"/> belongs to the pipeline — setting it here throws; add an
    /// <see cref="IToolInvocationMiddleware"/> instead. <see cref="FunctionInvokingChatClient.IncludeDetailedErrors"/>
    /// is already <see langword="true"/> (a failing tool's message reaches the model); set it to
    /// <see langword="false"/> here to send only a generic failure.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// When the client is built: the services it is built with hold no <see cref="ToolInvocationPipeline"/>.
    /// </exception>
    public static ChatClientBuilder UseToolInvocationPipeline(
        this ChatClientBuilder builder,
        Action<FunctionInvokingChatClient>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Use((inner, services) =>
        {
            var pipeline = services.GetService<ToolInvocationPipeline>()
                ?? throw new InvalidOperationException(
                    "UseToolInvocationPipeline() found no ToolInvocationPipeline in the services the chat client was built with. " +
                    "Register the agent services (AddIronHiveAgent) and build the client with them (Build(serviceProvider)), " +
                    "or pass a pipeline: UseToolInvocationPipeline(pipeline).");
            return Create(inner, pipeline, services, configure);
        });
    }

    /// <summary>
    /// Adds function invocation (a <see cref="FunctionInvokingChatClient"/>, in place of <c>UseFunctionInvocation()</c>)
    /// that runs every tool call through <paramref name="pipeline"/>.
    /// </summary>
    /// <param name="builder">The chat client builder.</param>
    /// <param name="pipeline">The pipeline; <see cref="ToolInvocationPipeline.CreateDefault"/> for the default loop guards only.</param>
    /// <param name="configure">
    /// Configures the function-invoking client. The <see cref="FunctionInvokingChatClient.FunctionInvoker"/> belongs to
    /// the pipeline — setting it here throws. <see cref="FunctionInvokingChatClient.IncludeDetailedErrors"/> is already
    /// <see langword="true"/>; set it to <see langword="false"/> here to send only a generic failure.
    /// </param>
    public static ChatClientBuilder UseToolInvocationPipeline(
        this ChatClientBuilder builder,
        ToolInvocationPipeline pipeline,
        Action<FunctionInvokingChatClient>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(pipeline);

        return builder.Use((inner, services) => Create(inner, pipeline, services, configure));
    }

    private static PipelineFunctionInvokingChatClient Create(
        IChatClient inner,
        ToolInvocationPipeline pipeline,
        IServiceProvider services,
        Action<FunctionInvokingChatClient>? configure)
    {
        var client = new PipelineFunctionInvokingChatClient(inner, pipeline, services.GetService<ILoggerFactory>(), services)
        {
            // The library's tools report failure by throwing; without the message the model reads only "Function failed"
            // and cannot correct the call (a wrong path, a non-unique edit). Set before configure, so a host can turn it off.
            IncludeDetailedErrors = true,
        };
        configure?.Invoke(client);
        if (client.FunctionInvoker is not null)
        {
            throw new InvalidOperationException(
                "UseToolInvocationPipeline configures FunctionInvoker itself; add an IToolInvocationMiddleware to the pipeline instead of setting it.");
        }

        client.FunctionInvoker = pipeline.Invoker;
        return client;
    }

    /// <summary>
    /// A function-invoking client that answers <c>GetService&lt;ToolInvocationPipeline&gt;()</c>, so a loop given the
    /// client finds the pipeline for the results a host supplies.
    /// </summary>
    private sealed class PipelineFunctionInvokingChatClient(
        IChatClient inner,
        ToolInvocationPipeline pipeline,
        ILoggerFactory? loggerFactory,
        IServiceProvider services)
        : FunctionInvokingChatClient(inner, loggerFactory, services)
    {
        public override object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType == typeof(ToolInvocationPipeline)
                ? pipeline
                : base.GetService(serviceType, serviceKey);
        }
    }
}
