using System.Text;
using AIAsk.Plugin.Skills;
using Xunit;

namespace AIAsk.Plugin.Tests.Skills;

public sealed class SkillStorageReadFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AIAsk.SkillStorageTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Load_InvalidUtf8Storage_ThrowsAndPreservesOriginalFile()
    {
        var repository = CreateRepository();
        Directory.CreateDirectory(Path.GetDirectoryName(repository.StoragePath)!);
        var original = new byte[] { 0xFF, 0xFE, 0x00 };
        File.WriteAllBytes(repository.StoragePath, original);

        var exception = Assert.Throws<SkillStorageException>(() => repository.Load());

        Assert.Contains("UTF-8", exception.Message);
        Assert.Equal(original, File.ReadAllBytes(repository.StoragePath));
    }

    [Fact]
    public void Add_InvalidJsonStorage_ThrowsAndDoesNotOverwriteStorage()
    {
        var repository = CreateRepository();
        Directory.CreateDirectory(Path.GetDirectoryName(repository.StoragePath)!);
        const string original = "{ this is not valid json";
        File.WriteAllText(repository.StoragePath, original, new UTF8Encoding(false));
        var skillPath = WriteSkill("skill.md", "contents");

        var exception = Assert.Throws<SkillStorageException>(() => repository.Add("translate", skillPath, global: false));

        Assert.Contains("invalid JSON", exception.Message);
        Assert.Equal(original, File.ReadAllText(repository.StoragePath));
    }

    [Fact]
    public void Load_UnreadableStorageFileSystem_ThrowsExplicitStorageError()
    {
        var storage = Path.Combine(_root, "settings", "skills.json");
        var fileSystem = new ThrowingReadFileSystem(storage);
        var repository = new SkillRepository(_root, Path.Combine(_root, "settings"), fileSystem);

        var exception = Assert.Throws<SkillStorageException>(() => repository.Load());

        Assert.Contains("cannot be read", exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SkillRepository CreateRepository()
    {
        Directory.CreateDirectory(_root);
        return new SkillRepository(_root, Path.Combine(_root, "settings"));
    }

    private string WriteSkill(string name, string contents)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, contents, new UTF8Encoding(false));
        return path;
    }

    private sealed class ThrowingReadFileSystem : IFileSystem
    {
        private readonly string _storage;

        public ThrowingReadFileSystem(string storage)
        {
            _storage = storage;
        }

        public bool FileExists(string path) => string.Equals(path, _storage, StringComparison.OrdinalIgnoreCase);

        public string ReadAllText(string path) => throw new UnauthorizedAccessException("Denied by test filesystem.");

        public void WriteAllText(string path, string contents) => throw new NotSupportedException();

        public void CreateDirectory(string path) => throw new NotSupportedException();

        public void ReplaceFile(string sourceFileName, string destinationFileName) => throw new NotSupportedException();

        public string GetFullPath(string path) => path;
    }
}
