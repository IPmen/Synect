namespace Synect.Application.Adapters;

public interface IAdapterRegistry
{
    IBankAdapter GetBank(string sourceCode);
    IEdoAdapter GetEdo(string sourceCode);
    IReadOnlyCollection<string> SourceCodes { get; }
}

/// <summary>Resolves adapters by source code (case-insensitive). Duplicate codes are a configuration error.</summary>
public sealed class AdapterRegistry : IAdapterRegistry
{
    private readonly Dictionary<string, IBankAdapter> _banks;
    private readonly Dictionary<string, IEdoAdapter> _edo;

    public AdapterRegistry(IEnumerable<IBankAdapter> banks, IEnumerable<IEdoAdapter> edo)
    {
        _banks = ToDictionary(banks);
        _edo = ToDictionary(edo);
        var overlap = _banks.Keys.Intersect(_edo.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        if (overlap.Count > 0)
            throw new InvalidOperationException($"Source codes registered for several adapter kinds: {string.Join(", ", overlap)}");
    }

    public IReadOnlyCollection<string> SourceCodes => _banks.Keys.Concat(_edo.Keys).ToList();

    public IBankAdapter GetBank(string sourceCode) =>
        _banks.TryGetValue(sourceCode, out var adapter)
            ? adapter
            : throw new KeyNotFoundException($"No bank adapter registered for source '{sourceCode}'.");

    public IEdoAdapter GetEdo(string sourceCode) =>
        _edo.TryGetValue(sourceCode, out var adapter)
            ? adapter
            : throw new KeyNotFoundException($"No EDO adapter registered for source '{sourceCode}'.");

    private static Dictionary<string, T> ToDictionary<T>(IEnumerable<T> adapters) where T : ISourceAdapter
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapters)
        {
            if (!result.TryAdd(adapter.SourceCode, adapter))
                throw new InvalidOperationException($"Adapter source code '{adapter.SourceCode}' is registered twice.");
        }
        return result;
    }
}
