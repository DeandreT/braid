using Weft.Contracts.Chat;

namespace Weft.Cli.Http;

internal static partial class ApiOperations
{
    private const string WorkspaceChat = Organization + "/chat";
    public static readonly ApiOperation GetWorkspaceChatAvailability = Get("GetWorkspaceChatAvailability", WorkspaceChat,
        typeof(ChatWorkspaceAvailabilityV1), error: typeof(ChatErrorV1));
    public static readonly ApiOperation GetWorkspaceChatConversations = Get("GetOrganizationChatConversations", WorkspaceChat + "/conversations",
        typeof(ChatOrganizationConversationPageV1), ["limit", "after", "revisionId"], error: typeof(ChatErrorV1));
    public static readonly ApiOperation CreateWorkspaceChatConversation = Send("CreateWorkspaceChatConversation", "POST", WorkspaceChat + "/conversations",
        "application/json", typeof(ChatWorkspaceConversationCreateV1), Created(typeof(ChatOrganizationConversationV1)), error: typeof(ChatErrorV1));
    public static readonly ApiOperation GetWorkspaceChatConversation = Get("GetWorkspaceChatConversation", WorkspaceChat + "/conversations/{conversationId}",
        typeof(ChatWorkspaceConversationDetailV1), error: typeof(ChatErrorV1));
    public static readonly ApiOperation CreateWorkspaceChatTurn = Send("CreateWorkspaceChatTurn", "POST", WorkspaceChat + "/conversations/{conversationId}/turns",
        "application/json", typeof(ChatTurnCreateV1), Created(typeof(ChatTurnV1)), error: typeof(ChatErrorV1));
    public static readonly ApiOperation CancelWorkspaceChatTurn = Send("CancelWorkspaceChatTurn", "POST", WorkspaceChat + "/conversations/{conversationId}/turns/{turnId}/cancel",
        null, null, Ok(typeof(ChatTurnV1)), error: typeof(ChatErrorV1));
    public static readonly ApiOperation StreamWorkspaceChatTurnEvents = new("StreamWorkspaceChatTurnEvents", "GET",
        WorkspaceChat + "/conversations/{conversationId}/turns/{turnId}/events", ["after"], None, null, null, Ok(typeof(ChatTurnEventV1)), typeof(ChatErrorV1), "text/event-stream");

    internal static ApiOperation WorkspaceOperation(ApiOperation operation) => operation.OperationId switch
    {
        "GetChatAvailability" => GetWorkspaceChatAvailability,
        "GetChatConversations" => GetWorkspaceChatConversations,
        "CreateChatConversation" => CreateWorkspaceChatConversation,
        "GetChatConversation" => GetWorkspaceChatConversation,
        "CreateChatTurn" => CreateWorkspaceChatTurn,
        "CancelChatTurn" => CancelWorkspaceChatTurn,
        "StreamChatTurnEvents" => StreamWorkspaceChatTurnEvents,
        _ => throw new ArgumentException("Not a chat operation.", nameof(operation))
    };
}
