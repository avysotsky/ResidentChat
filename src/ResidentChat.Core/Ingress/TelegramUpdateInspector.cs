using System.Text.Json;

namespace ResidentChat.Core.Ingress;

public enum TelegramParseKind { Invalid, Unsupported, GroupMessage }

public readonly record struct TelegramUpdateResult(
    TelegramParseKind Kind, long UpdateId = 0, long ChatId = 0);

/// <summary>Extract only metadata needed for admission; never retain or log message text.</summary>
public static class TelegramUpdateInspector
{
    public static TelegramUpdateResult Inspect(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("update_id", out var updateElement)
            || updateElement.ValueKind != JsonValueKind.Number
            || !updateElement.TryGetInt64(out long updateId)
            || updateId < 0)
        {
            return new(TelegramParseKind.Invalid);
        }

        if (!root.TryGetProperty("message", out var message))
            return new(TelegramParseKind.Unsupported);

        if (message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("chat", out var chat)
            || chat.ValueKind != JsonValueKind.Object
            || !chat.TryGetProperty("id", out var chatIdElement)
            || chatIdElement.ValueKind != JsonValueKind.Number
            || !chatIdElement.TryGetInt64(out long chatId)
            || !chat.TryGetProperty("type", out var chatType)
            || chatType.ValueKind != JsonValueKind.String)
        {
            return new(TelegramParseKind.Invalid);
        }

        string? type = chatType.GetString();
        return type is "group" or "supergroup"
            ? new(TelegramParseKind.GroupMessage, updateId, chatId)
            : new(TelegramParseKind.Unsupported);
    }
}
