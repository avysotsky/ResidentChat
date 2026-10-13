// WSL2 discovery canary ONLY. No external network, Telegram or LLM calls.
// Test is intentionally limited to a deterministic synthetic assertion.
var original = "Synthetic message: elevator";
var serialized = System.Text.Json.JsonSerializer.Serialize(new { text = original });
using var parsed = System.Text.Json.JsonDocument.Parse(serialized);
if (parsed.RootElement.GetProperty("text").GetString() != original)
{
    Console.Error.WriteLine("WSL2 DISCOVERY CANARY FAIL");
    return 1;
}
Console.WriteLine("WSL2 DISCOVERY CANARY PASS");
return 0;
