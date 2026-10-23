using System.Globalization;
using Sellora.AuditService.Domain.Audit;

namespace Sellora.AuditService.Tests.Unit;

/// <summary>US-E7-1-BE5 and BE8: the hash is deterministic, and verification finds the first broken link.</summary>
public sealed class AuditChainTests
{
    private static readonly Guid Company = Guid.Parse("30000000-0000-0000-0000-000000000001");

    // Known-answer vectors computed outside .NET with Python's hashlib over the
    // pipe-joined canonical string (see docs/US-E7-1.md, "Hash specification").
    private const string FirstVectorHash = "d84233181689dd11ec15e8b293a783a6df08cebe39c876b15d616acbb3baefba";
    private const string SecondVectorHash = "fd006727d1de117ca37762ee36146a7941abea1a74235122e71389af13c536c8";

    [Fact]
    public void The_hash_matches_an_independently_computed_vector()
    {
        var hash = AuditHasher.Compute(
            1,
            Company,
            Guid.Parse("61000000-0000-0000-0000-000000000003"),
            "PaymentRecorded",
            "Order",
            Guid.Parse("8c1e3d52-6a1f-4b7e-9d2c-0f4e5a6b7c8d"),
            new DateTimeOffset(2026, 9, 24, 5, 12, 41, 300, TimeSpan.Zero),
            "4f1c2b9e-7d3a-4e8f-b6c5-a1d2e3f4a5b6",
            "{\"amount\":1255.00,\"name\":\"ලේක්\"}",
            AuditHasher.GenesisHash);

        Assert.Equal(FirstVectorHash, hash);
    }

    [Fact]
    public void Missing_aggregate_and_correlation_ids_hash_as_empty_strings()
    {
        var hash = AuditHasher.Compute(
            2,
            Company,
            Guid.Parse("61000000-0000-0000-0000-000000000004"),
            "OrderCancelled",
            "Order",
            null,
            new DateTimeOffset(2026, 9, 25, 6, 0, 0, TimeSpan.Zero),
            null,
            "{}",
            FirstVectorHash);

        Assert.Equal(SecondVectorHash, hash);
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("si-LK")]
    [InlineData("ar-SA")]
    public void The_hash_does_not_depend_on_the_machine_culture(string culture)
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal(FirstVectorHash, Entry(1, AuditHasher.GenesisHash, "{\"amount\":1255.00,\"name\":\"ලේක්\"}").Hash);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void An_offset_timestamp_hashes_the_same_as_its_utc_instant()
    {
        var utc = new DateTimeOffset(2026, 9, 24, 5, 12, 41, 300, TimeSpan.Zero);
        var colombo = utc.ToOffset(new TimeSpan(5, 30, 0));

        Assert.Equal(AuditHasher.FormatTimestamp(utc), AuditHasher.FormatTimestamp(colombo));
        Assert.Equal("2026-09-24T05:12:41.300000Z", AuditHasher.FormatTimestamp(colombo));
    }

    [Fact]
    public void Sub_microsecond_ticks_are_dropped_before_hashing_and_storing()
    {
        var withTicks = new DateTimeOffset(2026, 9, 24, 5, 12, 41, TimeSpan.Zero).AddTicks(1_234_567);

        var stored = AuditTime.Normalise(withTicks);

        Assert.Equal(1_234_560, stored.Ticks % TimeSpan.TicksPerSecond);
        Assert.Equal(AuditHasher.FormatTimestamp(withTicks), AuditHasher.FormatTimestamp(stored));
    }

    [Fact]
    public void An_appended_entry_links_to_the_previous_hash()
    {
        var first = Entry(1, AuditHasher.GenesisHash, "{}");
        var second = Entry(2, first.Hash, "{\"n\":2}");

        Assert.Equal(AuditHasher.GenesisHash, first.PreviousHash);
        Assert.Equal(first.Hash, second.PreviousHash);
        Assert.Equal(64, second.Hash.Length);
        Assert.Equal(AuditHasher.Compute(second.ToChainRecord()), second.Hash);
    }

    [Fact]
    public void Append_refuses_a_position_below_one_or_a_malformed_previous_hash()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Entry(0, AuditHasher.GenesisHash, "{}"));
        Assert.Throws<ArgumentException>(() => Entry(1, "abc", "{}"));
    }

    [Fact]
    public void A_healthy_chain_verifies()
    {
        var verifier = new ChainVerifier();

        foreach (var record in Chain(5))
        {
            Assert.True(verifier.Check(record));
        }

        Assert.False(verifier.IsBroken);
        Assert.Equal(5, verifier.EntriesChecked);
    }

    [Fact]
    public void An_empty_chain_verifies_with_nothing_checked()
    {
        var verifier = new ChainVerifier();

        Assert.False(verifier.IsBroken);
        Assert.Equal(0, verifier.EntriesChecked);
    }

    [Fact]
    public void A_changed_payload_is_a_hash_mismatch_at_that_entry_and_earlier_entries_still_verify()
    {
        var chain = Chain(5).ToList();
        chain[2] = chain[2] with { Payload = "{\"tampered\":true}" };

        var result = Verify(chain);

        Assert.Equal(new ChainBreak(chain[2].AuditEntryId, 3, chain[2].EventType, ChainBreakReason.HashMismatch), result.FirstBreak);
        Assert.Equal(3, result.EntriesChecked);
    }

    [Fact]
    public void A_rehashed_tampered_entry_breaks_the_next_link()
    {
        var chain = Chain(4).ToList();
        var tampered = chain[1] with { Payload = "{\"tampered\":true}" };
        chain[1] = tampered with { Hash = AuditHasher.Compute(tampered) };

        var result = Verify(chain);

        Assert.Equal(ChainBreakReason.PreviousHashMismatch, result.FirstBreak!.Reason);
        Assert.Equal(3, result.FirstBreak.ChainPosition);
    }

    [Fact]
    public void A_deleted_row_is_a_missing_position()
    {
        var chain = Chain(5).ToList();
        chain.RemoveAt(2);

        var result = Verify(chain);

        Assert.Equal(new ChainBreak(null, 3, null, ChainBreakReason.MissingPosition), result.FirstBreak);
        Assert.Equal(2, result.EntriesChecked);
    }

    [Fact]
    public void A_deleted_first_row_is_reported_at_position_one()
    {
        var result = Verify(Chain(3).Skip(1));

        Assert.Equal(new ChainBreak(null, 1, null, ChainBreakReason.MissingPosition), result.FirstBreak);
    }

    [Fact]
    public void Records_after_the_first_break_are_ignored()
    {
        var chain = Chain(4).ToList();
        chain[1] = chain[1] with { PreviousHash = new string('f', 64) };
        var verifier = new ChainVerifier();

        foreach (var record in chain)
        {
            verifier.Check(record);
        }

        Assert.Equal(2, verifier.FirstBreak!.ChainPosition);
        Assert.False(verifier.Check(chain[3]));
    }

    private static ChainVerifier Verify(IEnumerable<ChainRecord> records)
    {
        var verifier = new ChainVerifier();

        foreach (var record in records)
        {
            if (!verifier.Check(record))
            {
                break;
            }
        }

        return verifier;
    }

    private static IEnumerable<ChainRecord> Chain(int length)
    {
        var previous = AuditHasher.GenesisHash;

        for (var position = 1; position <= length; position++)
        {
            var entry = Entry(position, previous, $"{{\"n\":{position}}}");
            previous = entry.Hash;
            yield return entry.ToChainRecord();
        }
    }

    private static AuditEntry Entry(long position, string previousHash, string payload) => AuditEntry.Append(
        new AuditEventContent(
            Company,
            position == 1 ? Guid.Parse("61000000-0000-0000-0000-000000000003") : Guid.NewGuid(),
            "PaymentRecorded",
            "1.0",
            "sellora.order.v1",
            0,
            position,
            "Order",
            Guid.Parse("8c1e3d52-6a1f-4b7e-9d2c-0f4e5a6b7c8d"),
            "ORD-260924-K7P2QM",
            null,
            "rep-1",
            "SalesRep",
            new DateTimeOffset(2026, 9, 24, 5, 12, 41, 300, TimeSpan.Zero),
            "4f1c2b9e-7d3a-4e8f-b6c5-a1d2e3f4a5b6",
            payload),
        position,
        previousHash,
        DateTimeOffset.UtcNow);
}
