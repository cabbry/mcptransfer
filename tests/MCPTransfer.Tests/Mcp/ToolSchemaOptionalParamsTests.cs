using System.Reflection;
using MCPTransfer.Agent.Mcp;

namespace MCPTransfer.Tests.Mcp;

/// <summary>
/// Regression for a live finding: the inbox tool was invoked from an MCP host
/// with no arguments and failed with "the arguments dictionary is missing a
/// value for the required parameter 'sinceBlock'". The MCP SDK derives a tool
/// parameter's required-ness from <see cref="ParameterInfo.HasDefaultValue"/>:
/// a nullable parameter WITHOUT a default ('string? mime', 'ulong? sinceBlock')
/// is still emitted as <c>required</c> in the tool's input schema, so a host
/// that omits it gets a hard error instead of the documented default behaviour.
/// The optional params therefore carry a '= null' default (commit e8bce11).
///
/// Asserting <c>HasDefaultValue</c> IS the contract: the SDK's required list is
/// exactly the JSON-bound parameters without a default, so pinning the default
/// here guarantees these stay optional without standing up the full DI-backed
/// schema generator (which needs a live <c>McpAgentContext</c>).
/// </summary>
public class ToolSchemaOptionalParamsTests
{
    // (tool method, parameter name) pairs that MUST stay optional: omitting
    // them from a tools/call is part of the documented contract.
    public static TheoryData<string, string> OptionalParams() => new()
    {
        { nameof(TransferTools.Inbox), "sinceBlock" },
        { nameof(TransferTools.SendFile), "mime" },
        { nameof(TransferTools.ReceiveFile), "expectHash" },
    };

    [Theory]
    [MemberData(nameof(OptionalParams))]
    public void OptionalToolParam_CarriesDefault_SoSdkLeavesItOutOfRequired(
        string methodName, string paramName)
    {
        var method = typeof(TransferTools).GetMethod(methodName)
            ?? throw new InvalidOperationException($"TransferTools.{methodName} not found.");

        var parameter = method.GetParameters().Single(p => p.Name == paramName);

        Assert.True(
            parameter.HasDefaultValue,
            $"{methodName}.{paramName} must keep its '= null' default; without it the MCP "
            + "SDK marks the parameter required and a host that omits it gets a hard error.");
    }
}
