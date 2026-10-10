using System.Text;
using AIAsk.Plugin.Settings;
using AIAsk.Plugin.Skills;
using Xunit;

namespace AIAsk.Plugin.Tests.Settings;

public sealed class SkillSettingsControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AIAsk.NativeSettingsTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void AddEditToggleDelete_UsesSharedRepositoryAndRefreshableRows()
    {
        var controller = CreateController();
        var firstPath = WriteSkill("first.md", "first");
        var secondPath = WriteSkill("second.md", "second");

        var added = controller.Add("translate", firstPath, global: false);
        Assert.True(added.IsSuccess);
        Assert.Single(controller.LoadRows());
        Assert.False(controller.LoadRows()[0].IsGlobal);

        var toggled = controller.SetGlobal("translate", global: true);
        Assert.True(toggled.IsSuccess);
        Assert.True(Assert.Single(controller.LoadRows()).IsGlobal);

        var edited = controller.Edit("translate", "review", secondPath, global: true);
        Assert.True(edited.IsSuccess);
        var row = Assert.Single(controller.LoadRows());
        Assert.Equal("review", row.Alias);
        Assert.Equal(secondPath, row.Path);

        var deleted = controller.Delete("review");
        Assert.True(deleted.IsSuccess);
        Assert.Empty(controller.LoadRows());
    }

    [Fact]
    public void InvalidPath_ReturnsVisibleFailureWithoutWritingSkill()
    {
        var controller = CreateController();

        var result = controller.Add("bad", Path.Combine(_root, "missing.md"), global: false);

        Assert.False(result.IsSuccess);
        Assert.Contains("does not exist", result.Error ?? string.Empty);
        Assert.Empty(controller.LoadRows());
    }

    [Fact]
    public void StorageFailure_IsReturnedToNativeSettingsPanelAdapter()
    {
        var fileSystem = new ThrowingReadFileSystem();
        var repository = new SkillRepository(_root, Path.Combine(_root, "settings"), fileSystem);
        var controller = new SkillSettingsController(repository);

        var result = controller.Add("safe", WriteSkill("safe.md", "body"), global: false);

        Assert.False(result.IsSuccess);
        Assert.Contains("cannot be read", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SkillSettingsController CreateController()
    {
        Directory.CreateDirectory(_root);
        return new SkillSettingsController(new SkillRepository(_root, Path.Combine(_root, "settings")));
    }

    private string WriteSkill(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private sealed class ThrowingReadFileSystem : IFileSystem
    {
        public bool FileExists(string path) => true;
        public string ReadAllText(string path) => throw new IOException("denied");
        public void WriteAllText(string path, string contents) => throw new IOException("denied");
        public void CreateDirectory(string path) { }
        public void ReplaceFile(string sourceFileName, string destinationFileName) { }
        public string GetFullPath(string path) => path;
    }
}
