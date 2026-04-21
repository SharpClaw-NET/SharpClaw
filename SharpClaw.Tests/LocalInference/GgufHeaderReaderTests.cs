using System.Text;
using SharpClaw.Application.Core.LocalInference;

namespace SharpClaw.Tests.LocalInference;

[TestFixture]
public class GgufHeaderReaderTests
{
    // Writes a minimal valid GGUF file with one metadata KV pair.
    private static string WriteGgufFile(string architectureValue)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".gguf");
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // Magic
        w.Write("GGUF"u8.ToArray());
        // Version
        w.Write((uint)3);
        // Tensor count
        w.Write((ulong)0);
        // KV count = 1
        w.Write((ulong)1);

        // Key: "general.architecture"
        var key      = Encoding.UTF8.GetBytes("general.architecture");
        var value    = Encoding.UTF8.GetBytes(architectureValue);
        w.Write((ulong)key.Length);
        w.Write(key);
        w.Write((uint)8); // GGUF_TYPE_STRING
        w.Write((ulong)value.Length);
        w.Write(value);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    [Test]
    public async Task ReadArchitectureAsync_ValidGguf_ReturnsArchitecture()
    {
        var path = WriteGgufFile("llama");
        try
        {
            var result = await GgufHeaderReader.ReadArchitectureAsync(path);

            result.Should().Be("llama");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task ReadArchitectureAsync_WhisperGguf_ReturnsWhisper()
    {
        var path = WriteGgufFile("whisper");
        try
        {
            var result = await GgufHeaderReader.ReadArchitectureAsync(path);

            result.Should().Be("whisper");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task ReadArchitectureAsync_NotGgufFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]); // PNG magic
        try
        {
            var result = await GgufHeaderReader.ReadArchitectureAsync(path);

            result.Should().BeNull();
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task ReadArchitectureAsync_EmptyFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllBytes(path, []);
        try
        {
            var result = await GgufHeaderReader.ReadArchitectureAsync(path);

            result.Should().BeNull();
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task ReadArchitectureAsync_NonExistentFile_ReturnsNull()
    {
        var result = await GgufHeaderReader.ReadArchitectureAsync(
            Path.Combine(Path.GetTempPath(), "does_not_exist.gguf"));

        result.Should().BeNull();
    }
}
