using System.Diagnostics.CodeAnalysis;
using NimBus.Core.Messages;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Extensions;

/// <summary>
/// Defines a message pipeline behavior (middleware) that wraps message handling.
/// Behaviors execute in registration order, each calling the next delegate to continue the pipeline.
/// </summary>
/// <example>
/// <code>
/// public class LoggingBehavior : IMessagePipelineBehavior
/// {
///     public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken ct)
///     {
///         Console.WriteLine($"Before: {context.EventId}");
///         await next(context, ct);
///         Console.WriteLine($"After: {context.EventId}");
///     }
/// }
/// </code>
/// </example>
public interface IMessagePipelineBehavior
{
    /// <summary>
    /// Handles the message, optionally delegating to the next behavior in the pipeline.
    /// </summary>
    /// <param name="context">The message context.</param>
    /// <param name="next">Delegate to invoke the next behavior or the terminal handler.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default);
}

/// <summary>
/// Delegate representing the next step in the message pipeline.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Public API; renaming would break every pipeline behavior.")]
public delegate Task MessagePipelineDelegate(IMessageContext context, CancellationToken cancellationToken = default);
