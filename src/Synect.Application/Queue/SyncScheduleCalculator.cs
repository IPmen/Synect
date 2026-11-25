using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Synect.Domain.Sync;

namespace Synect.Application.Queue;

/// <summary>A schedule slot: the job should start at <see cref="Start"/> and may still be started until <see cref="ValidUntil"/>.</summary>
public sealed record ScheduleSlot(DateTimeOffset Start, DateTimeOffset ValidUntil);

/// <summary>
/// Computes when each connection is due. Start times are spread over the window by a deterministic
/// jitter derived from a hash of the connection id: the same connection always gets the same time,
/// different connections are distributed over the window.
/// </summary>
public sealed class SyncScheduleCalculator(IOptions<ScheduleOptions> options, IOptions<OData.ODataOptions>? odataOptions = null)
{
    private ScheduleOptions O => options.Value;

    /// <summary>Stable value in [0, 1) derived from SHA-256 of the connection id.</summary>
    public static double JitterFraction(Guid connectionId)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(connectionId.ToByteArray(), hash);
        var value = BinaryPrimitives.ReadUInt64BigEndian(hash) >> 11; // 53 bits fit a double exactly
        return value / (double)(1UL << 53);
    }

    public ScheduleSlot NightlySlot(Guid connectionId, DateOnly localDate)
    {
        var window = O.NightlyWindowEnd - O.NightlyWindowStart;
        if (window <= TimeSpan.Zero)
            throw new InvalidOperationException("Sync:Schedule nightly window must start before it ends on the same day.");
        var offset = TimeSpan.FromSeconds(Math.Floor(window.TotalSeconds * JitterFraction(connectionId)));
        return new ScheduleSlot(Local(localDate, O.NightlyWindowStart + offset), Local(localDate, O.NightlyWindowEnd));
    }

    /// <summary>Daytime refresh slots of an EDO connection: every N hours from DaytimeStart, shifted by the jitter.</summary>
    public IReadOnlyList<ScheduleSlot> DaytimeSlots(Guid connectionId, DateOnly localDate) => DaytimeSlots(connectionId, localDate, O.EdoDaytimeRefreshHours);

    /// <summary>Daytime refresh slots every <paramref name="hours"/> hours from DaytimeStart, shifted by the jitter.</summary>
    public IReadOnlyList<ScheduleSlot> DaytimeSlots(Guid connectionId, DateOnly localDate, int hours)
    {
        if (hours <= 0) return [];
        var period = TimeSpan.FromHours(hours);
        var offset = TimeSpan.FromSeconds(Math.Floor(period.TotalSeconds * JitterFraction(connectionId)));
        var slots = new List<ScheduleSlot>();
        for (var start = O.DaytimeStart + offset; start < O.DaytimeEnd; start += period)
        {
            var end = start + period < O.DaytimeEnd ? start + period : O.DaytimeEnd;
            slots.Add(new ScheduleSlot(Local(localDate, start), Local(localDate, end)));
        }
        return slots;
    }

    /// <summary>The slot that is due now (start passed, still valid), or null.</summary>
    public ScheduleSlot? CurrentSlot(Guid connectionId, AdapterKind kind, DateTimeOffset now, bool oneCOData = false)
    {
        var localDate = DateOnly.FromDateTime(now.ToOffset(O.UtcOffset).DateTime);
        var slots = new List<ScheduleSlot> { NightlySlot(connectionId, localDate) };
        if (kind == AdapterKind.Edo) slots.AddRange(DaytimeSlots(connectionId, localDate));
        // 1C:Fresh over OData: daytime deltas every OData:DaytimeRefreshHours (the nightly slot usually becomes the full load).
        if (oneCOData) slots.AddRange(DaytimeSlots(connectionId, localDate, odataOptions?.Value.DaytimeRefreshHours ?? 2));
        return slots.Where(s => s.Start <= now && now < s.ValidUntil).MaxBy(s => s.Start);
    }

    private DateTimeOffset Local(DateOnly date, TimeSpan timeOfDay) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), O.UtcOffset).Add(timeOfDay).ToUniversalTime();
}
