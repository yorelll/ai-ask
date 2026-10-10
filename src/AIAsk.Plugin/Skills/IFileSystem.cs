using System.IO;
using System.Text;

namespace AIAsk.Plugin.Skills;

/// <summary>
/// Small filesystem boundary so skill persistence and validation are unit-testable
/// without relying on Flow or a real plugin settings directory.
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);

    string ReadAllText(string path);

    void WriteAllText(string path, string contents);

    void CreateDirectory(string path);

    void ReplaceFile(string sourceFileName, string destinationFileName);

    string GetFullPath(string path);
}

public sealed class PhysicalFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public string ReadAllText(string path)
    {
        using var reader = new StreamReader(
            path,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void ReplaceFile(string sourceFileName, string destinationFileName)
    {
        if (File.Exists(destinationFileName))
        {
            File.Move(sourceFileName, destinationFileName, overwrite: true);
        }
        else
        {
            File.Move(sourceFileName, destinationFileName);
        }
    }

    public string GetFullPath(string path) => Path.GetFullPath(path);
}
