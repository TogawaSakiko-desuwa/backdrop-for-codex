using System.Text.Json;

namespace BackdropForCodex.Core.Tests.Injection;

internal static class InjectionScriptPayloadTestHelper
{
    private const string PayloadPrefix = "const cfg = ";
    private const string PayloadSuffix = ";\n  const globalObject";

    internal static string ExtractPayloadJson(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var payloadStart = script.IndexOf(PayloadPrefix, StringComparison.Ordinal);
        if (payloadStart < 0)
        {
            throw new InvalidOperationException("The install script does not contain its payload prefix.");
        }

        payloadStart += PayloadPrefix.Length;
        var payloadEnd = script.IndexOf(PayloadSuffix, payloadStart, StringComparison.Ordinal);
        if (payloadEnd <= payloadStart)
        {
            throw new InvalidOperationException("The install script does not contain its payload suffix.");
        }

        return script[payloadStart..payloadEnd];
    }

    internal static string ExtractStyleSheet(string script)
    {
        using var payload = JsonDocument.Parse(ExtractPayloadJson(script));
        return payload.RootElement.GetProperty("styleSheet").GetString() ??
            throw new InvalidOperationException("The install payload stylesheet is null.");
    }
}
