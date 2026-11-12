// SPDX-License-Identifier: GPL-3.0-only
// Additional terms: see ADDITIONAL_TERMS.md.

using System.Text.Json;
using Wonder.Extensibility.Contracts;

namespace Wonder.Example.Hello;

public sealed class HelloPlugin : IWonderPlugin
{
    public void Activate(IPluginContext context)
    {
        using var configuration = JsonDocument.Parse(context.ConfigurationJson);
        var greeting = "Hello from Wonder Plugins!";
        if (configuration.RootElement.ValueKind == JsonValueKind.Object &&
            configuration.RootElement.TryGetProperty("greeting", out var value) &&
            value.ValueKind == JsonValueKind.String &&
            value.GetString() is { } configuredGreeting &&
            !string.IsNullOrWhiteSpace(configuredGreeting))
        {
            greeting = configuredGreeting;
        }

        context.Log(greeting);
    }

    public void Dispose()
    {
    }
}
