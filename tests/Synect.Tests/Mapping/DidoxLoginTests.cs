using Synect.Application.Adapters;
using Synect.Infrastructure.Adapters.Didox;

namespace Synect.Tests.Mapping;

public class DidoxLoginTests
{
    [Fact]
    public void Company_login_returns_the_token()
    {
        var result = DidoxTokenCache.ParseLoginResponse("""{ "token": "t-1", "related_companies": null, "related_branches": null }""");
        Assert.Equal("t-1", result.Token);
        Assert.Null(result.Permissions);
        Assert.DoesNotContain("t-1", result.ToString());
    }

    [Fact]
    public void Person_login_into_a_company_carries_permissions()
    {
        var result = DidoxTokenCache.ParseLoginResponse("""
            { "token": "t-2", "related_companies": null, "related_branches": null,
              "permissions": { "tin": "301234567", "roles": [ "director", "accountant" ] } }
            """);
        Assert.Equal("t-2", result.Token);
        Assert.Equal("301234567", result.Permissions!.Tin);
        Assert.Equal(["director", "accountant"], result.Permissions.Roles);
    }

    [Theory]
    [InlineData("""{ "error": "invalid credentials" }""")]
    [InlineData("""{ "token": "" }""")]
    [InlineData("not json")]
    public void Response_without_token_is_an_authentication_error(string json)
    {
        Assert.Throws<AdapterAuthenticationException>(() => DidoxTokenCache.ParseLoginResponse(json));
    }
}
