using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Alerts;
using NimBus.MessageStore.Abstractions;

namespace NimBus.WebApp.Services.AlertRules;

/// <summary>Registers alert rules (Spec 053) in the WebApp.</summary>
public static partial class AlertRuleServiceCollectionExtensions
{
    /// <summary>
    /// Registers the notice channels, read from <c>NimBus:Alerts:Mail</c> and
    /// <c>NimBus:Alerts:Webhook</c> as the Resolver reads them, the evaluator, and its scheduler. The
    /// scheduler runs only when the storage provider offers <see cref="IAlertRuleStore"/>. Invalid
    /// alert settings never stop the WebApp: that channel stays off and the reason is logged.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddNimBusAlertRules(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => CreateDelivery(sp));
        services.TryAddScoped(sp => new AlertRuleEvaluator(
            sp.GetRequiredService<IAlertRuleStore>(),
            sp.GetRequiredService<NimBus.MessageStore.Abstractions.IMessageTrackingStore>(),
            sp.GetRequiredService<AlertRuleDelivery>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<AlertRuleEvaluator>>() ?? NullLogger<AlertRuleEvaluator>.Instance,
            $"{Environment.MachineName}/{Environment.ProcessId}"));

        // AddSingleton<IHostedService>(factory), not AddHostedService: the latter drops a second
        // registration of the same type. Without a rule store there is nothing to evaluate.
        services.AddSingleton<IHostedService>(sp => sp.GetService<IAlertRuleStore>() is null
            ? new NoAlertRuleStore(sp.GetService<ILogger<AlertRuleEvaluatorService>>() ?? NullLogger<AlertRuleEvaluatorService>.Instance)
            : new AlertRuleEvaluatorService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<ILogger<AlertRuleEvaluatorService>>() ?? NullLogger<AlertRuleEvaluatorService>.Instance));
        return services;
    }

    private static AlertRuleDelivery CreateDelivery(IServiceProvider sp)
    {
        var configuration = sp.GetService<IConfiguration>();
        var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("NimBus.WebApp.AlertRules") ?? NullLogger.Instance;

        var mailOptions = configuration?.GetSection(AlertMailOptions.SectionName).Get<AlertMailOptions>() ?? new AlertMailOptions();
        IAlertMailSender? mailSender = null;
        AlertMailComposer? mailComposer = null;
        if (!mailOptions.IsConfigured)
        {
            LogMailOff(logger, AlertMailOptions.SectionName);
        }
        else if (mailOptions.Validate() is { Count: > 0 } mailErrors)
        {
            LogMailInvalid(logger, string.Join(" ", mailErrors));
        }
        else
        {
            mailSender = new SmtpAlertMailSender(mailOptions);
            mailComposer = new AlertMailComposer(mailOptions);
        }

        var webhookOptions = configuration?.GetSection(AlertWebhookOptions.SectionName).Get<AlertWebhookOptions>() ?? new AlertWebhookOptions();
        IAlertRuleWebhookSender? webhookSender = null;
        if (!webhookOptions.IsConfigured)
        {
            LogWebhooksOff(logger, AlertWebhookOptions.SectionName);
        }
        else if (webhookOptions.Validate() is { Count: > 0 } webhookErrors)
        {
            LogWebhooksInvalid(logger, string.Join(" ", webhookErrors));
        }
        else
        {
            // A token is only requested for a listed prefix, so the credential is only created
            // when there is one. A host can register its own TokenCredential.
            var credential = webhookOptions.BearerTokens.Count == 0
                ? null
                : sp.GetService<TokenCredential>() ?? new DefaultAzureCredential();
            webhookSender = new AlertWebhookSender(
                webhookOptions,
                AlertWebhookSender.CreateHttpClient(webhookOptions),
                credential,
                sp.GetRequiredService<TimeProvider>());
        }

        return new AlertRuleDelivery(mailSender, mailComposer, webhookSender, mailOptions.WebAppUrl);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Alert rule mail is off: set {Section}:Smtp:Host and {Section}:From on the WebApp to mail rule targets.")]
    private static partial void LogMailOff(ILogger logger, string section);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alert rule mail is off because its settings are invalid: {Errors}")]
    private static partial void LogMailInvalid(ILogger logger, string errors);

    [LoggerMessage(Level = LogLevel.Information, Message = "Alert rule webhooks are off: set {Section}:SigningKey on the WebApp to call rule webhook targets.")]
    private static partial void LogWebhooksOff(ILogger logger, string section);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alert rule webhooks are off because their settings are invalid: {Errors}")]
    private static partial void LogWebhooksInvalid(ILogger logger, string errors);

    /// <summary>Stands in for the scheduler when the storage provider has no rule store.</summary>
    private sealed partial class NoAlertRuleStore(ILogger logger) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            LogNoStore(logger);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        [LoggerMessage(Level = LogLevel.Information, Message = "Alert rules are off: the storage provider has no alert rule store.")]
        private static partial void LogNoStore(ILogger logger);
    }
}
