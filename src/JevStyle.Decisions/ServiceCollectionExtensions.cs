using JevStyle.Decisions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the Jev-style decision client with dependency injection.</summary>
public static class JevStyleServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IDecisionClient"/> backed by <see cref="OpenAiChatLogprobSource"/> as a typed
    /// <see cref="HttpClient"/>. Returns the <see cref="IHttpClientBuilder"/> so you can add resilience,
    /// handlers or a timeout.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the server address, model and key.</param>
    /// <example>
    /// <code>
    /// services.AddJevStyleDecisions(o =>
    /// {
    ///     o.BaseAddress = new Uri("http://localhost:1234");
    ///     o.Model = "jev-style-qwen3.5-2b-decision";
    /// });
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddJevStyleDecisions(this IServiceCollection services, Action<JevStyleClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        OptionsBuilder<JevStyleClientOptions> options = services.AddOptions<JevStyleClientOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.Validate(static o => o.TryValidate(out _), "JevStyleClientOptions are invalid: check BaseAddress, Model and TopLogprobs.");
        services.TryAddTransient<IDecisionClient, DecisionClient>();
        return services.AddHttpClient<ILogprobSource, OpenAiChatLogprobSource>();
    }

    /// <summary>
    /// Registers <see cref="IDecisionsApi"/> (OpenRouter's decisions API by default) as a typed
    /// <see cref="HttpClient"/>, plus <see cref="HostedDecisionClient"/> for code that uses <see cref="IDecisionClient"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the model, key and address.</param>
    public static IHttpClientBuilder AddDecisionsApi(this IServiceCollection services, Action<DecisionsApiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        OptionsBuilder<DecisionsApiOptions> options = services.AddOptions<DecisionsApiOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.Validate(static o => o.TryValidate(out _), "DecisionsApiOptions are invalid: check BaseAddress and Model.");
        services.TryAddTransient<HostedDecisionClient>();
        return services.AddHttpClient<IDecisionsApi, DecisionsApiClient>();
    }
}
