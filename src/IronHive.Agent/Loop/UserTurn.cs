using Microsoft.Extensions.AI;

namespace IronHive.Agent.Loop;

/// <summary>The message a turn starts from, checked the same way by every built-in loop.</summary>
internal static class UserTurn
{
    /// <summary>
    /// <paramref name="message"/> when it can start a turn: a <see cref="ChatRole.User"/> message with at least one
    /// content part. An image-only message is a valid request; an empty one is not.
    /// </summary>
    public static ChatMessage Checked(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Role != ChatRole.User)
        {
            throw new ArgumentException(
                $"A turn starts from a user message; this one has role '{message.Role}'. To hand back tool results, append them to the history and call ContinueAsync.",
                nameof(message));
        }

        if (message.Contents.Count == 0)
        {
            throw new ArgumentException("A turn needs at least one content part (text, an image, ...).", nameof(message));
        }

        return message;
    }
}
