using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIAsk.Plugin.Skills;

/// <summary>
/// Owns the persisted skills.json document shared by the C# settings UI and
/// runtime /add parser. The repository preserves list order because global
/// system prompt order is meaningful.
/// </summary>
public sealed class SkillRepository
{
    private const int SchemaVersion = 1;
    private const string FileName = "skills.json";
    private static readonly Regex AliasPattern = new(
        @"^[^\s:：/\\]{1,64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _pluginDirectory;
    private readonly string _settingsDirectory;
    private readonly IFileSystem _fileSystem;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public SkillRepository(string pluginDirectory, string settingsDirectory, IFileSystem? fileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        _pluginDirectory = Path.GetFullPath(pluginDirectory);
        _settingsDirectory = Path.GetFullPath(settingsDirectory);
        _fileSystem = fileSystem ?? new PhysicalFileSystem();
    }

    public string StoragePath => Path.Combine(_settingsDirectory, FileName);

    public IReadOnlyList<SkillDefinition> Load()
    {
        lock (_gate)
        {
            return ReadUnsafe().ToArray();
        }
    }

    public SkillDefinition Add(string alias, string path, bool global)
    {
        lock (_gate)
        {
            var skills = ReadUnsafe();
            var definition = ValidateDefinition(alias, path, global);
            EnsureAliasAvailable(skills, definition.Alias, originalAlias: null);
            skills.Add(definition);
            SaveUnsafe(skills);
            return definition;
        }
    }

    public SkillDefinition Edit(string originalAlias, string alias, string path, bool global)
    {
        lock (_gate)
        {
            var skills = ReadUnsafe();
            var originalIndex = FindIndex(skills, originalAlias);
            if (originalIndex < 0)
            {
                throw new InvalidOperationException($"Skill alias not found: {originalAlias}");
            }

            var definition = ValidateDefinition(alias, path, global);
            EnsureAliasAvailable(skills, definition.Alias, originalAlias);
            skills[originalIndex] = definition;
            SaveUnsafe(skills);
            return definition;
        }
    }

    public bool Delete(string alias)
    {
        lock (_gate)
        {
            var skills = ReadUnsafe();
            var index = FindIndex(skills, alias);
            if (index < 0)
            {
                return false;
            }

            skills.RemoveAt(index);
            SaveUnsafe(skills);
            return true;
        }
    }

    public SkillDefinition ToggleGlobal(string alias)
    {
        lock (_gate)
        {
            var skills = ReadUnsafe();
            var index = FindIndex(skills, alias);
            if (index < 0)
            {
                throw new InvalidOperationException($"Skill alias not found: {alias}");
            }

            var previous = skills[index];
            var updated = previous with { Global = !previous.Global };
            skills[index] = updated;
            SaveUnsafe(skills);
            return updated;
        }
    }

    public SkillValidationResult Validate(SkillDefinition skill)
    {
        try
        {
            _ = ValidateDefinition(skill.Alias, skill.Path, skill.Global);
            return SkillValidationResult.Valid;
        }
        catch (ArgumentException exception)
        {
            return SkillValidationResult.Invalid(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return SkillValidationResult.Invalid(exception.Message);
        }
    }

    public string BuildGlobalSystemPrompt()
    {
        lock (_gate)
        {
            var enabled = ReadUnsafe().Where(skill => skill.Global).ToArray();
            var contents = new List<string>();
            foreach (var skill in enabled)
            {
                var path = ResolveSkillPath(skill.Path);
                EnsureReadableUtf8(path);
                var content = _fileSystem.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(content))
                {
                    contents.Add(content);
                }
            }

            return string.Join("\n\n---\n\n", contents);
        }
    }

    public DynamicSkillSelection ParseDynamicAdd(string commandText)
    {
        var remainder = commandText.Trim();
        if (string.IsNullOrEmpty(remainder))
        {
            return DynamicSkillSelection.Failure("Choose one or more skills below, then add your question.");
        }

        var available = Load().ToDictionary(skill => skill.Alias, StringComparer.OrdinalIgnoreCase);
        var first = MatchToken(remainder);
        if (first is null || !available.TryGetValue(first.Value.Token, out var firstSkill))
        {
            return DynamicSkillSelection.Failure("Specify a valid skill alias after /add.");
        }

        var selected = new List<SkillDefinition> { firstSkill };
        var tail = first.Value.Remainder;

        // Colon syntax explicitly chains aliases. Stop at first non-alias; all
        // remaining punctuation remains literal user prompt content.
        while (StartsWithDelimiter(tail))
        {
            tail = RemoveLeadingDelimiter(tail);
            var next = MatchToken(tail);
            if (next is null || !available.TryGetValue(next.Value.Token, out var nextSkill))
            {
                break;
            }

            selected.Add(nextSkill);
            tail = next.Value.Remainder;
        }

        var prompt = tail.Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            return DynamicSkillSelection.Failure("Add your question after the selected skill alias.");
        }

        foreach (var skill in selected)
        {
            var status = Validate(skill);
            if (!status.IsValid)
            {
                return DynamicSkillSelection.Failure($"Skill {skill.Alias} is invalid: {status.Error}");
            }
        }

        return DynamicSkillSelection.Success(selected, prompt);
    }

    /// <summary>
    /// Parses /skills command input. Space form supports quoted Windows paths;
    /// compact colon form does not split drive-letter colons.
    /// </summary>
    public static IReadOnlyList<string> ParseManagementCommand(string commandText)
    {
        var text = commandText.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        var compact = Regex.Match(
            text,
            @"^(add|edit|toggle|global|delete|remove)\s*[:：]\s*(.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        if (!compact.Success)
        {
            return TokenizeQuoted(text);
        }

        var command = compact.Groups[1].Value.ToLowerInvariant();
        var tail = compact.Groups[2].Value;
        if (command is "toggle" or "global" or "delete" or "remove")
        {
            return new[] { command, tail.Trim() };
        }

        if (command == "add")
        {
            var match = Regex.Match(tail, @"^([^\s:：]+)\s*[:：]\s*(.+)$", RegexOptions.Singleline);
            if (!match.Success)
            {
                return new[] { command };
            }

            var (path, flag) = SplitTrailingGlobalFlag(match.Groups[2].Value);
            return flag is null
                ? new[] { command, match.Groups[1].Value, path }
                : new[] { command, match.Groups[1].Value, path, flag };
        }

        var edit = Regex.Match(
            tail,
            @"^([^\s:：]+)\s*[:：]\s*([^\s:：]+)\s*[:：]\s*(.+)$",
            RegexOptions.Singleline);
        if (!edit.Success)
        {
            return new[] { command };
        }

        var (editPath, editFlag) = SplitTrailingGlobalFlag(edit.Groups[3].Value);
        return editFlag is null
            ? new[] { command, edit.Groups[1].Value, edit.Groups[2].Value, editPath }
            : new[] { command, edit.Groups[1].Value, edit.Groups[2].Value, editPath, editFlag };
    }

    private SkillDefinition ValidateDefinition(string alias, string path, bool global)
    {
        var normalizedAlias = alias.Trim().ToLowerInvariant();
        if (!AliasPattern.IsMatch(normalizedAlias))
        {
            throw new ArgumentException("Alias must not contain whitespace, :, ：, /, or \\.", nameof(alias));
        }

        var normalizedPath = ResolveSkillPath(path);
        EnsureReadableUtf8(normalizedPath);
        return new SkillDefinition(normalizedAlias, normalizedPath, global);
    }

    private string ResolveSkillPath(string path)
    {
        var trimmed = path.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Skill path is required.", nameof(path));
        }

        var candidate = Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(_pluginDirectory, trimmed);
        return _fileSystem.GetFullPath(candidate);
    }

    private void EnsureReadableUtf8(string path)
    {
        if (!_fileSystem.FileExists(path))
        {
            throw new InvalidOperationException($"Skill path does not exist: {path}");
        }

        try
        {
            _ = _fileSystem.ReadAllText(path);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException($"Skill file must be UTF-8 text: {path}", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException($"Skill file cannot be read: {path}", exception);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"Skill file cannot be read: {path}", exception);
        }
    }

    private List<SkillDefinition> ReadUnsafe()
    {
        if (!_fileSystem.FileExists(StoragePath))
        {
            return [];
        }

        try
        {
            var json = _fileSystem.ReadAllText(StoragePath);
            var document = JsonSerializer.Deserialize<SkillsDocument>(json, _jsonOptions);
            return document?.Skills?.ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void SaveUnsafe(List<SkillDefinition> skills)
    {
        _fileSystem.CreateDirectory(_settingsDirectory);
        var temporaryPath = StoragePath + ".tmp";
        var json = JsonSerializer.Serialize(new SkillsDocument(SchemaVersion, skills), _jsonOptions);
        _fileSystem.WriteAllText(temporaryPath, json);
        _fileSystem.ReplaceFile(temporaryPath, StoragePath);
    }

    private static int FindIndex(IReadOnlyList<SkillDefinition> skills, string alias) =>
        skills.Select((skill, index) => (skill, index))
            .Where(item => string.Equals(item.skill.Alias, alias.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();

    private static void EnsureAliasAvailable(IReadOnlyList<SkillDefinition> skills, string alias, string? originalAlias)
    {
        var conflict = skills.Any(skill =>
            string.Equals(skill.Alias, alias, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(skill.Alias, originalAlias, StringComparison.OrdinalIgnoreCase));
        if (conflict)
        {
            throw new InvalidOperationException($"Skill alias already exists: {alias}");
        }
    }

    private static (string Token, string Remainder)? MatchToken(string text)
    {
        var match = Regex.Match(text, @"^([^\s:：]+)(.*)$", RegexOptions.Singleline);
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value) : null;
    }

    private static bool StartsWithDelimiter(string text) => text.TrimStart().StartsWith(':') || text.TrimStart().StartsWith('：');

    private static string RemoveLeadingDelimiter(string text)
    {
        var trimmed = text.TrimStart();
        return (trimmed.Length > 0 && (trimmed[0] == ':' || trimmed[0] == '：'))
            ? trimmed[1..].TrimStart()
            : trimmed;
    }

    private static (string Path, string? Flag) SplitTrailingGlobalFlag(string value)
    {
        var match = Regex.Match(value, @"^(.*?)(?:\s*[:：]\s*)(global|on|true|1)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success
            ? (match.Groups[1].Value.Trim(), match.Groups[2].Value.ToLowerInvariant())
            : (value.Trim(), null);
    }

    private static IReadOnlyList<string> TokenizeQuoted(string text)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();
        var quoted = false;
        foreach (var character in text)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (builder.Length > 0)
                {
                    tokens.Add(builder.ToString());
                    builder.Clear();
                }
                continue;
            }

            builder.Append(character);
        }

        if (quoted)
        {
            throw new ArgumentException("Unterminated quoted path.", nameof(text));
        }

        if (builder.Length > 0)
        {
            tokens.Add(builder.ToString());
        }

        return tokens;
    }

    private sealed record SkillsDocument(int Version, IReadOnlyList<SkillDefinition> Skills);
}
