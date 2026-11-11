using Synect.Application.Access;
using Synect.Application.Accounts;

namespace Synect.Tests.Accounts;

/// <summary>Choosing the organization a question refers to, only among those available to the key (stage 6.5).</summary>
public class OrganizationResolverTests
{
    private const double MinSimilarity = 0.45;

    private static readonly OrganizationInfo Uzbek = new(Guid.NewGuid(), "ООО «Ўзбек Қурилиш Ғалла»", "301234567");
    private static readonly OrganizationInfo Hamkor = new(Guid.NewGuid(), "Ҳамкор Савдо МЧЖ", "302345678");
    private static readonly OrganizationInfo Romashka1 = new(Guid.NewGuid(), "Ромашка Север", "303456789");
    private static readonly OrganizationInfo Romashka2 = new(Guid.NewGuid(), "Ромашка Юг", "304567890");
    private static readonly OrganizationInfo Person = new(Guid.NewGuid(), "ЯТТ Алиев Шерзод", "12345678901234");
    private static readonly OrganizationInfo Gofur = new(Guid.NewGuid(), "G'afur G'ulom nashriyoti", null);

    private static readonly IReadOnlyList<OrganizationInfo> Available = [Uzbek, Hamkor, Romashka1, Romashka2, Person, Gofur];

    private static OrganizationMatch Resolve(string value) => OrganizationResolver.Resolve(value, Available, MinSimilarity);

    [Theory]
    [InlineData("301234567")]
    [InlineData(" 301234567 ")]
    public void Nine_digit_tin_selects_the_organization(string tin) =>
        Assert.Equal(Uzbek, Resolve(tin).Organization);

    [Fact]
    public void Fourteen_digit_tin_and_id_select_the_organization()
    {
        Assert.Equal(Person, Resolve("12345678901234").Organization);
        Assert.Equal(Hamkor, Resolve(Hamkor.TenantId.ToString()).Organization);
    }

    // Uzbek letters ў, қ, ғ, ҳ and the apostrophes of o' g' in Cyrillic, Latin and mixed spellings, with and without legal form.
    [Theory]
    [InlineData("Ўзбек Қурилиш Ғалла")]
    [InlineData("ООО Ўзбек Қурилиш Ғалла")]
    [InlineData("O'zbek Qurilish G'alla")]
    [InlineData("Oʻzbek Qurilish Gʻalla MChJ")]
    [InlineData("O`zbek Qurilish G`alla")]
    [InlineData("Узбек Курилиш Галла")]
    [InlineData("ozbek qurilish galla")]
    [InlineData("Ўзбек Qurilish")]
    public void Uzbek_spellings_find_the_same_organization(string name)
    {
        var match = Resolve(name);
        Assert.Equal(OrganizationMatchKind.Resolved, match.Kind);
        Assert.Equal(Uzbek, match.Organization);
    }

    [Theory]
    [InlineData("Hamkor Savdo")]
    [InlineData("Ҳамкор савдо")]
    [InlineData("Xamkor Savdo MChJ")]
    [InlineData("Хамкор Савдо")]
    public void Uzbek_h_spellings_find_hamkor(string name) => Assert.Equal(Hamkor, Resolve(name).Organization);

    [Theory]
    [InlineData("Gafur Gulom")]
    [InlineData("Ғафур Ғулом")]
    [InlineData("G‘afur G‘ulom nashriyoti")]
    public void Apostrophes_and_cyrillic_g_find_gofur(string name) => Assert.Equal(Gofur, Resolve(name).Organization);

    [Fact]
    public void Typo_is_tolerated_when_one_name_is_close()
    {
        var match = Resolve("Хамкор Сафдо");
        Assert.Equal(Hamkor, match.Organization);
    }

    [Fact]
    public void Several_similar_names_are_ambiguous_and_never_guessed()
    {
        var match = Resolve("Ромашка");
        Assert.Equal(OrganizationMatchKind.Ambiguous, match.Kind);
        Assert.Null(match.Organization);
        Assert.Equal([Romashka1, Romashka2], match.Candidates.OrderBy(c => c.Name).ToList());
    }

    [Fact]
    public void Exact_normalized_name_wins_over_partial_matches() =>
        Assert.Equal(Romashka2, Resolve("ООО «Ромашка Юг»").Organization);

    [Theory]
    [InlineData("399999999")]          // a TIN that is not in the list (another account's or nonexistent)
    [InlineData("Совсем другая фирма")]
    [InlineData("ООО")]                // only a legal form
    [InlineData("12")]
    public void Anything_else_is_not_found(string value)
    {
        var match = Resolve(value);
        Assert.Equal(OrganizationMatchKind.NotFound, match.Kind);
        Assert.Empty(match.Candidates);
    }

    [Fact]
    public void Organizations_outside_the_list_are_never_looked_at()
    {
        // The same value against a list with and against a list without the organization: without it the answer is the
        // plain "not found" a nonexistent organization gets, so nothing tells that it exists elsewhere.
        var other = new OrganizationInfo(Guid.NewGuid(), "Чужая Компания", "305678901");
        Assert.Equal(other, OrganizationResolver.Resolve("305678901", [.. Available, other], MinSimilarity).Organization);
        Assert.Equal(OrganizationMatch.NotFound.Kind, Resolve("305678901").Kind);
        Assert.Equal(OrganizationMatch.NotFound.Kind, Resolve("Чужая Компания").Kind);
        Assert.Equal(OrganizationMatch.NotFound.Kind, Resolve(other.TenantId.ToString()).Kind);
    }

    [Fact]
    public void Similarity_is_symmetric_and_bounded()
    {
        Assert.Equal(1, OrganizationResolver.Similarity("ozbek", "ozbek"));
        Assert.Equal(OrganizationResolver.Similarity("abc def", "abd"), OrganizationResolver.Similarity("abd", "abc def"));
        Assert.InRange(OrganizationResolver.Similarity("hamkor", "romashka"), 0, 0.3);
    }
}
