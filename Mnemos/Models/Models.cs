namespace Mnemos.Models;

public record ChatMessage(
    string Uuid,
    string Sender, 
    string Text,
    string? Thinking,
    string CreatedAt,
    string ConversationUuid,
    int Index = 0
);

public record Conversation(
    string Uuid,
    string Name,
    string CreatedAt,
    List<ChatMessage> Messages
);