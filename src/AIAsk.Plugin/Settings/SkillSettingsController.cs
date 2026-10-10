using AIAsk.Plugin.Skills;

namespace AIAsk.Plugin.Settings;

/// <summary>
/// UI-independent adapter for the native WPF skill settings panel. Both the
/// panel and later plugin shell use this controller so all mutations flow
/// through <see cref="SkillRepository"/> and its atomic persistence rules.
/// </summary>
public sealed class SkillSettingsController
{
    private readonly SkillRepository _repository;

    public SkillSettingsController(SkillRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public IReadOnlyList<SkillSettingsRow> LoadRows()
    {
        return _repository.Load().Select(ToRow).ToArray();
    }

    public SkillSettingsOperationResult Add(string alias, string path, bool global)
    {
        try
        {
            return SkillSettingsOperationResult.Success(ToRow(_repository.Add(alias, path, global)));
        }
        catch (Exception exception) when (IsHandled(exception))
        {
            return SkillSettingsOperationResult.Failure(exception.Message);
        }
    }

    public SkillSettingsOperationResult Edit(string originalAlias, string alias, string path, bool global)
    {
        try
        {
            return SkillSettingsOperationResult.Success(ToRow(_repository.Edit(originalAlias, alias, path, global)));
        }
        catch (Exception exception) when (IsHandled(exception))
        {
            return SkillSettingsOperationResult.Failure(exception.Message);
        }
    }

    public SkillSettingsOperationResult Delete(string alias)
    {
        try
        {
            if (!_repository.Delete(alias))
            {
                return SkillSettingsOperationResult.Failure($"Skill alias not found: {alias}");
            }

            return SkillSettingsOperationResult.Success(null);
        }
        catch (Exception exception) when (IsHandled(exception))
        {
            return SkillSettingsOperationResult.Failure(exception.Message);
        }
    }

    public SkillSettingsOperationResult SetGlobal(string alias, bool global)
    {
        try
        {
            var current = _repository.Load().FirstOrDefault(skill =>
                string.Equals(skill.Alias, alias, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                return SkillSettingsOperationResult.Failure($"Skill alias not found: {alias}");
            }

            var updated = current.Global == global ? current : _repository.ToggleGlobal(alias);
            return SkillSettingsOperationResult.Success(ToRow(updated));
        }
        catch (Exception exception) when (IsHandled(exception))
        {
            return SkillSettingsOperationResult.Failure(exception.Message);
        }
    }

    private SkillSettingsRow ToRow(SkillDefinition skill)
    {
        var validation = _repository.Validate(skill);
        return new SkillSettingsRow(skill.Alias, skill.Path, skill.Global, validation.IsValid, validation.Error);
    }

    private static bool IsHandled(Exception exception) =>
        exception is ArgumentException or InvalidOperationException or SkillStorageException;
}

/// <summary>One native settings-grid row.</summary>
public sealed class SkillSettingsRow
{
    public SkillSettingsRow(string alias, string path, bool isGlobal, bool isValid, string? status)
    {
        Alias = alias;
        Path = path;
        IsGlobal = isGlobal;
        IsValid = isValid;
        Status = isValid ? "Valid" : status ?? "Invalid";
    }

    public string Alias { get; }

    public string Path { get; }

    public bool IsGlobal { get; set; }

    public bool IsValid { get; }

    public string Status { get; }
}

public sealed record SkillSettingsOperationResult(bool IsSuccess, SkillSettingsRow? Skill, string? Error)
{
    public static SkillSettingsOperationResult Success(SkillSettingsRow? skill) => new(true, skill, null);

    public static SkillSettingsOperationResult Failure(string error) => new(false, null, error);
}
