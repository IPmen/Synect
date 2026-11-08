using Microsoft.Extensions.Options;
using Synect.Application.Queue;
using Synect.Domain.Sync;

namespace Synect.Tests.Queue;

public class ScheduleTests
{
    private static readonly TimeSpan Tashkent = TimeSpan.FromHours(5);
    private static readonly DateOnly Date = new(2026, 11, 8);
    private static readonly SyncScheduleCalculator Calculator = new(Options.Create(new ScheduleOptions()));

    private static DateTimeOffset Local(int hour, int minute = 0) =>
        new DateTimeOffset(Date.ToDateTime(new TimeOnly(hour, minute)), Tashkent).ToUniversalTime();

    [Fact]
    public void Same_connection_always_gets_the_same_nightly_time()
    {
        var id = Guid.NewGuid();
        var first = Calculator.NightlySlot(id, Date);
        var again = new SyncScheduleCalculator(Options.Create(new ScheduleOptions())).NightlySlot(id, Date);
        Assert.Equal(first, again);
        Assert.Equal(SyncScheduleCalculator.JitterFraction(id), SyncScheduleCalculator.JitterFraction(id));
        // Same time of day on another date.
        Assert.Equal(first.Start.TimeOfDay, Calculator.NightlySlot(id, Date.AddDays(1)).Start.TimeOfDay);
    }

    [Fact]
    public void Different_connections_are_spread_over_the_whole_window()
    {
        var starts = Enumerable.Range(0, 400).Select(_ => Calculator.NightlySlot(Guid.NewGuid(), Date).Start).ToList();

        Assert.All(starts, s => Assert.InRange(s, Local(1), Local(5).AddTicks(-1)));
        // Every half-hour bucket of the 4-hour window gets connections (400 ids, 8 buckets, ~50 each).
        var buckets = starts.GroupBy(s => (int)((s - Local(1)).TotalMinutes / 30)).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(8, buckets.Count);
        Assert.All(buckets.Values, count => Assert.InRange(count, 20, 90));
        Assert.True(starts.Distinct().Count() > 390);
    }

    [Fact]
    public void Nightly_slot_is_due_only_inside_the_window_after_its_start()
    {
        var id = Guid.NewGuid();
        var slot = Calculator.NightlySlot(id, Date);

        Assert.Null(Calculator.CurrentSlot(id, AdapterKind.Bank, slot.Start.AddSeconds(-1)));
        Assert.Equal(slot.Start, Calculator.CurrentSlot(id, AdapterKind.Bank, slot.Start)!.Start);
        Assert.Equal(slot.Start, Calculator.CurrentSlot(id, AdapterKind.Bank, Local(4, 59))!.Start);
        Assert.Null(Calculator.CurrentSlot(id, AdapterKind.Bank, Local(5)));
        Assert.Null(Calculator.CurrentSlot(id, AdapterKind.Bank, Local(12)));
    }

    [Fact]
    public void Edo_connections_get_daytime_refreshes_every_n_hours()
    {
        var id = Guid.NewGuid();
        var slots = Calculator.DaytimeSlots(id, Date);

        // 08:00-20:00 with a 4-hour period: three slots, 4 hours apart, shifted by the same jitter.
        Assert.Equal(3, slots.Count);
        Assert.All(slots.Zip(slots.Skip(1)), p => Assert.Equal(TimeSpan.FromHours(4), p.Second.Start - p.First.Start));
        Assert.InRange(slots[0].Start, Local(8), Local(12).AddTicks(-1));

        Assert.Equal(slots[1].Start, Calculator.CurrentSlot(id, AdapterKind.Edo, slots[1].Start.AddMinutes(1))!.Start);
        // Banks have no daytime refresh.
        Assert.Null(Calculator.CurrentSlot(id, AdapterKind.Bank, slots[1].Start.AddMinutes(1)));
    }

    [Fact]
    public void Daytime_refresh_period_is_configurable()
    {
        var every2h = new SyncScheduleCalculator(Options.Create(new ScheduleOptions { EdoDaytimeRefreshHours = 2 }));
        Assert.Equal(6, every2h.DaytimeSlots(Guid.NewGuid(), Date).Count);
        var disabled = new SyncScheduleCalculator(Options.Create(new ScheduleOptions { EdoDaytimeRefreshHours = 0 }));
        Assert.Empty(disabled.DaytimeSlots(Guid.NewGuid(), Date));
    }
}
