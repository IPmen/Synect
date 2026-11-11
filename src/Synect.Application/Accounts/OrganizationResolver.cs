using Synect.Application.Access;
using Synect.Domain.Accounts;
using Synect.Domain.Counterparties;

namespace Synect.Application.Accounts;

public enum OrganizationMatchKind
{
    Resolved,

    /// <summary>No organization available to the key matches. Never says whether such an organization exists elsewhere.</summary>
    NotFound,

    /// <summary>Several available organizations match; the caller must ask which one is meant.</summary>
    Ambiguous,
}

public sealed record OrganizationMatch(OrganizationMatchKind Kind, OrganizationInfo? Organization, IReadOnlyList<OrganizationInfo> Candidates)
{
    public static OrganizationMatch NotFound { get; } = new(OrganizationMatchKind.NotFound, null, []);
}

/// <summary>
/// Finds the organization a question refers to, ONLY among the organizations available to the key: by id, by TIN
/// (9 or 14 digits) or by name. Names are compared after <see cref="CounterpartyNameNormalizer"/> (legal forms and quotes
/// removed, Russian and Uzbek Cyrillic and Latin spellings brought to one form): one exact normalized match wins;
/// otherwise every name that contains the given one, is contained in it, or is similar enough (trigrams) is a candidate,
/// and more than one candidate is ambiguous - the resolver never guesses. Organizations outside the key's list are never
/// looked at, so "not found" is the same answer for an organization that does not exist and one of another account.
/// </summary>
public static class OrganizationResolver
{
    /// <summary>Shortest normalized fragment that may match by containment (shorter ones would match almost anything).</summary>
    public const int MinContainedLength = 3;

    public static OrganizationMatch Resolve(string value, IReadOnlyList<OrganizationInfo> available, double minSimilarity)
    {
        var text = value.Trim();
        if (text.Length == 0 || available.Count == 0) return OrganizationMatch.NotFound;

        if (Guid.TryParse(text, out var id))
            return Single(available.Where(o => o.TenantId == id).ToList());
        if (Tin.IsValid(text))
            return Single(available.Where(o => o.Tin == text).ToList());

        var wanted = CounterpartyNameNormalizer.Normalize(text);
        if (wanted.Length == 0) return OrganizationMatch.NotFound;
        var named = available.Select(o => (Org: o, Name: CounterpartyNameNormalizer.Normalize(o.Name))).ToList();

        var exact = named.Where(n => n.Name == wanted).Select(n => n.Org).ToList();
        if (exact.Count > 0) return Single(exact);

        var candidates = named
            .Select(n => (n.Org, Score: Score(wanted, n.Name)))
            .Where(c => c.Score >= minSimilarity)
            .OrderByDescending(c => c.Score).ThenBy(c => c.Org.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => c.Org)
            .ToList();
        return Single(candidates);
    }

    private static OrganizationMatch Single(IReadOnlyList<OrganizationInfo> matches) => matches.Count switch
    {
        0 => OrganizationMatch.NotFound,
        1 => new OrganizationMatch(OrganizationMatchKind.Resolved, matches[0], matches),
        _ => new OrganizationMatch(OrganizationMatchKind.Ambiguous, null, matches),
    };

    /// <summary>1 for containment of a long enough fragment, else the trigram similarity of the normalized names.</summary>
    public static double Score(string wanted, string name)
    {
        if (name.Length == 0) return 0;
        if ((wanted.Length >= MinContainedLength && name.Contains(wanted, StringComparison.Ordinal))
            || (name.Length >= MinContainedLength && wanted.Contains(name, StringComparison.Ordinal)))
            return 1;
        return Similarity(wanted, name);
    }

    /// <summary>Jaccard similarity of word trigrams, like pg_trgm similarity (words padded with two spaces before, one after).</summary>
    public static double Similarity(string a, string b)
    {
        var x = Trigrams(a);
        var y = Trigrams(b);
        if (x.Count == 0 || y.Count == 0) return 0;
        var common = x.Count(y.Contains);
        return (double)common / (x.Count + y.Count - common);
    }

    private static HashSet<string> Trigrams(string text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var padded = "  " + word + " ";
            for (var i = 0; i + 3 <= padded.Length; i++) result.Add(padded.Substring(i, 3));
        }
        return result;
    }
}
