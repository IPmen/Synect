using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Synect.Mcp.Hosting;

/// <summary>
/// Adds the optional 'organization' parameter to the input schema of EVERY tool in tools/list (and the Uzbek example
/// questions of <see cref="Tools.ToolExamples"/> to its description), so that no tool method
/// declares it itself (one mechanism for all tools; the value is resolved by <see cref="OrganizationScopeResolver"/> in
/// <see cref="ToolCallFilter"/> before the tool runs, and tools read the result from McpCaller).
/// </summary>
public static class OrganizationParameter
{
    public const string SingleText =
        "Organization (client company) whose data to use: its TIN (9 or 14 digits, preferred), its name in any spelling or its id. " +
        "Optional when the API key serves one organization. When it serves several and the user did not say which, ask the user " +
        "(the error organization_required lists the organizations); never choose one yourself. Examples: «301234567», «Ромашка», «Romashka MChJ».";

    public const string AllowsAllText = SingleText +
        " \"all\" lists the items of every available organization together, each with its organization.";

    public const string PortfolioText =
        "Optional: one organization (TIN, name or id) to show; by default every organization available to the API key.";

    public const string ItemText =
        "Organization of the item (TIN, name or id). Optional: without it the organization that has this item_id is used.";

    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> Create() => next => async (context, cancellationToken) =>
    {
        var result = await next(context, cancellationToken);
        foreach (var tool in result.Tools)
        {
            tool.InputSchema = AddTo(tool.InputSchema, DescriptionFor(tool.Name));
            // Uzbek example questions live in one place (Tools/ToolExamples.cs) and are added to every description here.
            if (Tools.ToolExamples.For(tool.Name) is { Length: > 0 } examples && tool.Description?.Contains(examples, StringComparison.Ordinal) != true)
                tool.Description += examples;
        }
        return result;
    };

    public static string DescriptionFor(string tool) => OrganizationScopeResolver.ModeOf(tool) switch
    {
        OrganizationMode.AllowsAll => AllowsAllText,
        OrganizationMode.Portfolio => PortfolioText,
        OrganizationMode.ItemLocator => ItemText,
        _ => SingleText,
    };

    public static JsonElement AddTo(JsonElement schema, string description)
    {
        var node = JsonNode.Parse(schema.GetRawText()) as JsonObject ?? new JsonObject { ["type"] = "object" };
        if (node["properties"] is not JsonObject properties)
        {
            properties = new JsonObject();
            node["properties"] = properties;
        }
        properties[OrganizationScopeResolver.ArgumentName] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = description,
        };
        return JsonSerializer.SerializeToElement(node);
    }
}
