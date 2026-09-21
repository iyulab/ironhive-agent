namespace IronHive.Agent.Context;

/// <summary>
/// Adds a section to what the model is told as system instructions, without replacing the system
/// prompt. A host keeps its default prompt, and each contributor — an application's house rules, a
/// catalogue of available skills, the scratchpad — adds its own block after it.
/// </summary>
/// <remarks>
/// Contributions are composed by <see cref="ContextManager"/> every time history is prepared for the
/// model, in registration order, each as its own system message placed after the leading system
/// messages. They are recomputed each turn and never become part of the stored conversation, so a
/// contributor whose text changes is always current and compaction cannot lose it. An agent loop that
/// runs without a <see cref="ContextManager"/> does not apply contributors.
/// </remarks>
public interface ISystemInstructionContributor
{
    /// <summary>
    /// A short stable name. It identifies the contribution in diagnostics — for example when the
    /// system instructions outgrow the context window and the question is which section did it.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The section to add for the coming turn, or <see langword="null"/> / empty to add nothing.
    /// </summary>
    string? GetInstructions();
}
