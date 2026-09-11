using System.Text;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Loop;

/// <summary>
/// Rebuilds the messages a streamed turn should leave in history.
/// </summary>
/// <remarks>
/// <para>
/// The non-streaming path keeps history by handing over the response's own messages, so a turn that
/// invoked tools leaves the assistant message <i>and</i> the Tool-role results that answered it. The
/// streaming path has no response object and must rebuild the same thing from deltas — and it used to
/// rebuild only half: one assistant message carrying every <see cref="FunctionCallContent"/>, with the
/// <see cref="FunctionResultContent"/> that followed each call thrown away. The next request then
/// announced tool calls with nothing answering them, which several providers reject and none can read
/// correctly.
/// </para>
/// <para>
/// Results also mark a boundary. A turn that calls a tool, reads the result, and then calls another is
/// several exchanges, not one — flushing the pending assistant message when results arrive keeps that
/// order instead of merging every round into a single message.
/// </para>
/// </remarks>
internal sealed class StreamingTurnHistoryBuilder
{
    private readonly StringBuilder _text = new();
    private readonly List<FunctionCallContent> _pendingCalls = [];
    private readonly List<ChatMessage> _messages = [];

    public void AppendText(string text) => _text.Append(text);

    public void AppendCall(FunctionCallContent call) => _pendingCalls.Add(call);

    /// <summary>
    /// Records the outcomes of the calls announced so far, closing the assistant message they belong to.
    /// </summary>
    public void AppendResults(IReadOnlyList<FunctionResultContent> results)
    {
        if (results.Count == 0)
        {
            return;
        }

        FlushAssistant();
        _messages.Add(new ChatMessage(ChatRole.Tool, [.. results]));
    }

    /// <summary>
    /// The messages for this turn. Always contains at least one assistant message, even for a turn
    /// that produced no text and called nothing, which is what the previous shape did.
    /// </summary>
    public IReadOnlyList<ChatMessage> Build()
    {
        if (_text.Length > 0 || _pendingCalls.Count > 0 || _messages.Count == 0)
        {
            FlushAssistant();
        }

        return _messages;
    }

    private void FlushAssistant()
    {
        // A message that only calls tools carries no text part: that is what the non-streaming path
        // leaves, and on the wire an empty text part becomes `"content": ""` beside `tool_calls`
        // where the other path omits content. A message with neither text nor calls keeps its empty
        // text, which is the shape a turn that produced nothing has always left.
        var message = _text.Length == 0 && _pendingCalls.Count > 0
            ? new ChatMessage(ChatRole.Assistant, [])
            : new ChatMessage(ChatRole.Assistant, _text.ToString());
        foreach (var call in _pendingCalls)
        {
            message.Contents.Add(call);
        }

        _messages.Add(message);
        _text.Clear();
        _pendingCalls.Clear();
    }
}
