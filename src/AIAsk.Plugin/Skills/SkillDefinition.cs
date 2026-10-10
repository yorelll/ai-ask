namespace AIAsk.Plugin.Skills;

/// <summary>
/// A skill file that may be loaded globally or for one explicit /add request.
/// </summary>
public sealed record SkillDefinition(string Alias, string Path, bool Global);

public sealed class SkillStorageException : InvalidOperationException
{
    public SkillStorageException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed record SkillValidationResult(bool IsValid, string? Error)
{
    public static SkillValidationResult Valid { get; } = new(true, null);

    public static SkillValidationResult Invalid(string error) => new(false, error);
}

public sealed record DynamicSkillSelection(
    IReadOnlyList<SkillDefinition>? Skills,
    string? Prompt,
    string? Error)
{
    public bool IsSuccess => Skills is not null && Prompt is not null && Error is null;

    public static DynamicSkillSelection Failure(string error) => new(null, null, error);

    public static DynamicSkillSelection Success(IReadOnlyList<SkillDefinition> skills, string prompt) =>
        new(skills, prompt, null);
}
