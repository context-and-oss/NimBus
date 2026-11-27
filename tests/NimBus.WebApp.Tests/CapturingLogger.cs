#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace NimBus.WebApp.Tests;

/// <summary>Records every log entry so a test can assert what was logged server-side.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception), exception));
}
