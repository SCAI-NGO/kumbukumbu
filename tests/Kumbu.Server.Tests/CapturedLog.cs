// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Kumbu.Server.Tests;

/// <summary>Every log line of a host, formatted, for tests that check what reaches the log.</summary>
internal sealed class CapturedLog : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}
