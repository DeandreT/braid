namespace Braid.Contracts.Chat;

/// <summary>Independent of the live turn's event fence. Pending never claims that a version was saved.</summary>
public sealed record ChatMappingWriteReceiptV1(string RequestId, string Status, ChatTurnEventV1? Write);
public sealed record ChatMappingWriteReceiptPageV1(IReadOnlyList<ChatMappingWriteReceiptV1> Receipts);
