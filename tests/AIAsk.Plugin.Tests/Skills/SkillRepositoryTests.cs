using System.Text;
using AIAsk.Plugin.Skills;
using Xunit;

namespace AIAsk.Plugin.Tests.Skills;

public sealed class SkillRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AIAsk.SkillTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Add_LoadAndToggle_PreservesOrderAndWritesSchema()
    {
        var repository = CreateRepository();
        var firstPath = WriteSkill("first.md", "first system prompt");
        var secondPath = WriteSkill("second.md", "second system prompt");

        repository.Add("first", firstPath, global: true);
        repository.Add("second", secondPath, global: false);
        var toggled = repository.ToggleGlobal("second");

        var skills = repository.Load();

        Assert.True(toggled.Global);
        Assert.Collection(
            skills,
            first => Assert.Equal("first", first.Alias),
            second => Assert.Equal("second", second.Alias));
        Assert.Contains("\"version\": 1", File.ReadAllText(repository.StoragePath));
    }

    [Fact]
    public void Add_ResolvesRelativePathAgainstPluginDirectory()
    {
        var pluginDirectory = Path.Combine(_root, "plugin");
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(Path.Combine(pluginDirectory, "translate.md"), "translate");
        var repository = new SkillRepository(pluginDirectory, Path.Combine(_root, "settings"));

        var result = repository.Add("translate", "translate.md", global: false);

        Assert.Equal(Path.Combine(pluginDirectory, "translate.md"), result.Path);
    }

    [Fact]
    public void Add_AllowsUnicodeAliasAndRejectsInvalidAliasCharacters()
    {
        var repository = CreateRepository();
        var path = WriteSkill("skill.md", "body");

        var skill = repository.Add("翻译", path, global: false);

        Assert.Equal("翻译", skill.Alias);
        foreach (var invalid in new[] { "has space", "has:colon", "has：colon", "a/b", "a\\b" })
        {
            var error = Assert.Throws<ArgumentException>(() => repository.Add(invalid, path, global: false));
            Assert.Contains("Alias", error.Message);
        }
    }

    [Fact]
    public void Add_RejectsDuplicateAliasCaseInsensitively()
    {
        var repository = CreateRepository();
        var first = WriteSkill("first.md", "first");
        var second = WriteSkill("second.md", "second");
        repository.Add("Translate", first, global: false);

        var error = Assert.Throws<InvalidOperationException>(() => repository.Add("translate", second, global: false));

        Assert.Contains("already exists", error.Message);
    }

    [Fact]
    public void Edit_ChangesMetadataWithoutModifyingSkillFile()
    {
        var repository = CreateRepository();
        var first = WriteSkill("first.md", "original skill contents");
        var second = WriteSkill("second.md", "new skill contents");
        repository.Add("old", first, global: false);

        var updated = repository.Edit("old", "new", second, global: true);

        Assert.Equal("new", updated.Alias);
        Assert.True(updated.Global);
        Assert.Equal("original skill contents", File.ReadAllText(first));
        Assert.Single(repository.Load());
    }

    [Fact]
    public void Delete_RemovesRecordWithoutRemovingSkillFile()
    {
        var repository = CreateRepository();
        var path = WriteSkill("skill.md", "body");
        repository.Add("skill", path, global: false);

        Assert.True(repository.Delete("skill"));
        Assert.False(repository.Delete("skill"));
        Assert.Empty(repository.Load());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void BuildGlobalSystemPrompt_UsesEnabledSkillsInStorageOrder()
    {
        var repository = CreateRepository();
        repository.Add("one", WriteSkill("one.md", "first"), global: true);
        repository.Add("two", WriteSkill("two.md", "second"), global: false);
        repository.Add("three", WriteSkill("three.md", "third"), global: true);

        var prompt = repository.BuildGlobalSystemPrompt();

        Assert.Equal("first\n\n---\n\nthird", prompt);
    }

    [Fact]
    public void Validate_ReportsMissingPathAndInvalidUtf8()
    {
        var repository = CreateRepository();
        var missing = repository.Validate(new SkillDefinition("missing", Path.Combine(_root, "none.md"), false));
        var invalidPath = Path.Combine(_root, "bad.md");
        File.WriteAllBytes(invalidPath, new byte[] { 0xFF, 0xFE, 0x00 });
        var invalidUtf8 = repository.Validate(new SkillDefinition("bad", invalidPath, false));

        Assert.False(missing.IsValid);
        Assert.Contains("does not exist", missing.Error ?? string.Empty);
        Assert.False(invalidUtf8.IsValid);
        Assert.Contains("UTF-8", invalidUtf8.Error ?? string.Empty);
    }

    [Fact]
    public void ParseDynamicAdd_SpaceFormPreservesPromptColons()
    {
        var repository = RepositoryWithSkills();

        var result = repository.ParseDynamicAdd("translate Please explain 1:2：3");

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "translate" }, result.Skills!.Select(skill => skill.Alias));
        Assert.Equal("Please explain 1:2：3", result.Prompt);
    }

    [Fact]
    public void ParseDynamicAdd_ColonFormSelectsSkillsAndPreservesPromptColons()
    {
        var repository = RepositoryWithSkills();

        var result = repository.ParseDynamicAdd("translate:review:标题：正文:1:2");

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "translate", "review" }, result.Skills!.Select(skill => skill.Alias));
        Assert.Equal("标题：正文:1:2", result.Prompt);
    }

    [Theory]
    [InlineData("translate", "Add your question")]
    [InlineData("missing question", "Specify a valid skill")]
    public void ParseDynamicAdd_ReturnsClearErrors(string input, string errorText)
    {
        var repository = RepositoryWithSkills();

        var result = repository.ParseDynamicAdd(input);

        Assert.False(result.IsSuccess);
        Assert.Contains(errorText, result.Error);
    }

    [Theory]
    [InlineData("add translate C:\\My Skills\\translate.md global", new[] { "add", "translate", "C:\\My Skills\\translate.md", "global" })]
    [InlineData("add:translate:C:\\Skills\\translate.md:global", new[] { "add", "translate", "C:\\Skills\\translate.md", "global" })]
    [InlineData("edit:old:new:C:\\My Skills\\translate.md:on", new[] { "edit", "old", "new", "C:\\My Skills\\translate.md", "on" })]
    [InlineData("toggle：translate", new[] { "toggle", "translate" })]
    public void ParseManagementCommand_SupportsWindowsPathsAndDelimiters(string input, string[] expected)
    {
        Assert.Equal(expected, SkillRepository.ParseManagementCommand(input));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SkillRepository CreateRepository() => new(_root, Path.Combine(_root, "settings"));

    private SkillRepository RepositoryWithSkills()
    {
        var repository = CreateRepository();
        repository.Add("translate", WriteSkill("translate.md", "translate"), global: false);
        repository.Add("review", WriteSkill("review.md", "review"), global: false);
        return repository;
    }

    private string WriteSkill(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
