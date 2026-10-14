using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Sellora.AuditService.Tests.Support;

/// <summary>Writes a component's log to the test output, so a failing integration test shows what the consumer did.</summary>
public sealed class TestOutputLogger<T>(ITestOutputHelper output) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            output.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {logLevel}: {formatter(state, exception)}{(exception is null ? "" : $" [{exception.GetType().Name}: {exception.Message}]")}");
        }
        catch (InvalidOperationException)
        {
            // The test has already finished; a late log line from the background consumer is dropped.
        }
    }
}
