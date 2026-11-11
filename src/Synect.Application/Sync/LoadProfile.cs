using System.Diagnostics;

namespace Synect.Application.Sync;

/// <summary>
/// Time spent per phase of one load or import (scoped: one per DI scope). Cheap enough to stay on; read by benchmarks
/// and returned with statement import results. Never contains data, only phase names and durations.
/// </summary>
public sealed class LoadProfile
{
    private readonly Dictionary<string, (TimeSpan Elapsed, int Calls)> _phases = new(StringComparer.Ordinal);

    public IDisposable Measure(string phase) => new Scope(this, phase);

    public IReadOnlyDictionary<string, TimeSpan> Snapshot()
    {
        lock (_phases) return _phases.ToDictionary(p => p.Key, p => p.Value.Elapsed);
    }

    public void Reset()
    {
        lock (_phases) _phases.Clear();
    }

    private void Add(string phase, TimeSpan elapsed)
    {
        lock (_phases)
        {
            var current = _phases.GetValueOrDefault(phase);
            _phases[phase] = (current.Elapsed + elapsed, current.Calls + 1);
        }
    }

    private sealed class Scope(LoadProfile owner, string phase) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();

        public void Dispose() => owner.Add(phase, Stopwatch.GetElapsedTime(_start));
    }
}
