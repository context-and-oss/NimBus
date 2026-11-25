#pragma warning disable CA1707, CA2007
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NimBus.Adapter.Mcp.Tests.Infrastructure;

/// <summary>One captured log entry with its structured state.</summary>
internal sealed record CapturedLog(string Category, LogLevel Level, EventId EventId, string Message, IReadOnlyDictionary<string, object?> State);

/// <summary>Captures every structured log entry, for asserting on the read log.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();

    public IReadOnlyList<CapturedLog> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
                : [];
            entries.Enqueue(new CapturedLog(category, logLevel, eventId, formatter(state, exception), values));
        }
    }
}
