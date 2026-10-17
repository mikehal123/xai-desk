using System.Text;
using System.Text.Json;

namespace Sovereign.Desktop.Connector;

/// <summary>MCP dispatch for the in-app swarm manager. Stdio uses Content-Length framing.</summary>
public static class SwarmManagerMcp
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static int RunStdio()
    {
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        while (ReadMessage(input) is { } body)
        {
            using var document = JsonDocument.Parse(body);
            var message = document.RootElement;
            if (!message.TryGetProperty("method", out var methodElement)) continue;
            var hasId = message.TryGetProperty("id", out var id);
            var result = Handle(methodElement.GetString() ?? "", message, null);
            if (!hasId || result is null) continue;
            WriteMessage(output, JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, Json));
        }
        return 0;
    }

    public static object? Handle(string method, JsonElement message, string? root)
    {
        if (method == "notifications/initialized") return null;
        if (method == "initialize")
            return new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { tools = new { } },
                serverInfo = new { name = "si-desk-swarm-manager", version = "swarm-manager" }
            };
        if (method == "tools/list") return new { tools = SwarmManagerTools.Definitions() };
        if (method != "tools/call") return Error("Unknown method.");
        if (!message.TryGetProperty("params", out var parameters) || !parameters.TryGetProperty("name", out var nameElement))
            return Error("Missing tool name.");
        var name = nameElement.GetString() ?? "";
        var arguments = parameters.TryGetProperty("arguments", out var args) ? args : default;
        try
        {
            var text = SwarmManagerTools.Call(name, arguments, root);
            return new { content = new[] { new { type = "text", text } }, isError = false };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or KeyNotFoundException or JsonException)
        {
            return Error(ex.Message);
        }
    }

    public static byte[]? ReadMessage(Stream input)
    {
        var header = new StringBuilder();
        var previous = -1;
        while (true)
        {
            var next = input.ReadByte();
            if (next < 0) return header.Length == 0 ? null : throw new IOException("MCP header ended early.");
            header.Append((char)next);
            if (previous == '\r' && next == '\n' && header.Length >= 4 && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n')
                break;
            previous = next;
            if (header.Length > 8192) throw new IOException("MCP header is too large.");
        }
        var length = 0;
        foreach (var line in header.ToString().Split("\r\n"))
        {
            const string marker = "Content-Length:";
            if (!line.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(line[marker.Length..].Trim(), out length) || length < 0)
                throw new IOException("MCP Content-Length is not a number.");
        }
        if (length == 0) throw new IOException("MCP header has no Content-Length.");
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = input.Read(body, read, length - read);
            if (count == 0) throw new IOException("MCP body ended early.");
            read += count;
        }
        return body;
    }

    public static void WriteMessage(Stream output, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var prefix = Encoding.ASCII.GetBytes("Content-Length: " + body.Length.ToString() + "\r\n\r\n");
        output.Write(prefix);
        output.Write(body);
        output.Flush();
    }

    static object Error(string message) => new { content = new[] { new { type = "text", text = message } }, isError = true };
}
