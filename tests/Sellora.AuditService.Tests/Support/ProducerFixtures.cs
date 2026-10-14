using System.Text;
using System.Text.Json;
using Sellora.AuditService.Application.Ingestion;

namespace Sellora.AuditService.Tests.Support;

/// <summary>What one fixture says the normaliser must produce.</summary>
public sealed record ExpectedEnvelope(
    Guid EventId,
    string EventType,
    string AggregateType,
    Guid? AggregateId,
    string? OrderReference,
    Guid? ProvinceId,
    string ActorUserId,
    string? ActorRole,
    string? CorrelationId,
    string OccurredAt);

public sealed record ProducerFixture(string Name, string Producer, IncomingMessage Message, ExpectedEnvelope Expected)
{
    public string Payload => Encoding.UTF8.GetString(Message.Value!);

    public override string ToString() => Name;
}

/// <summary>
/// Loads Fixtures/&lt;producer&gt;/*.json. Each file is one Kafka record as the
/// producer's own code emits it (see Fixtures/README.md). Every file in the
/// folder is loaded, so a real capture can be dropped in without code changes.
/// </summary>
public static class ProducerFixtures
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<ProducerFixture> All { get; } = Load();

    public static ProducerFixture Get(string name) => All.Single(fixture => fixture.Name == name);

    public static IEnumerable<object[]> Names() => All.Select(fixture => new object[] { fixture.Name });

    private static IReadOnlyList<ProducerFixture> Load()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures");

        return Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                var file = JsonSerializer.Deserialize<FixtureFile>(File.ReadAllText(path), Json)!;
                var name = $"{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileNameWithoutExtension(path)}";

                var headers = file.Headers
                    .Select(header => new KeyValuePair<string, byte[]?>(
                        header.Key,
                        header.Base64 is not null ? Convert.FromBase64String(header.Base64) : Encoding.UTF8.GetBytes(header.Text ?? string.Empty)))
                    .ToList();

                var message = new IncomingMessage(
                    file.Topic, file.Partition, file.Offset, file.Timestamp, file.Key, headers, Encoding.UTF8.GetBytes(file.Value));

                return new ProducerFixture(name, file.Producer, message, file.Expected);
            })
            .ToList();
    }

    private sealed record FixtureHeader(string Key, string? Text, string? Base64);

    private sealed record FixtureFile(
        string Producer,
        string Source,
        string Topic,
        int Partition,
        long Offset,
        DateTimeOffset Timestamp,
        string? Key,
        List<FixtureHeader> Headers,
        string Value,
        ExpectedEnvelope Expected);
}
