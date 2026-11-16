namespace Braid.Api.Chat;

/// <summary>Server-owned compatibility switch. Catalog edit permission alone never enables model saves.</summary>
public sealed record ChatMappingEditRuntimeOptions(bool LegacyDirectSaveEnabled = false);
