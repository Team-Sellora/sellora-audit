using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sellora.AuditService.Infrastructure.Exports;

namespace Sellora.AuditService.Tests.Support;

/// <summary>Runs the real export worker and cleanup over a host's own services.</summary>
public static class ExportRunner
{
    public static ExportWorkerService Worker(IServiceProvider services) => new(
        services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new ExportOptions()),
        TimeProvider.System,
        NullLogger<ExportWorkerService>.Instance);

    public static ExportCleanupService Cleanup(IServiceProvider services) => new(
        services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new ExportOptions()),
        TimeProvider.System,
        NullLogger<ExportCleanupService>.Instance);

    /// <summary>Claims jobs until the queue is empty; returns how many this worker ran.</summary>
    public static async Task<int> DrainAsync(ExportWorkerService worker)
    {
        var ran = 0;

        while (await worker.RunNextAsync(CancellationToken.None))
        {
            ran++;
        }

        return ran;
    }

    /// <summary>The CSV lines after the byte order mark.</summary>
    public static string[] CsvLines(byte[] file)
    {
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, file[..3]);
        return Encoding.UTF8.GetString(file, 3, file.Length - 3).Split("\r\n");
    }
}
