using System.Text;
using IronHive.Agent.Tools;

namespace IronHive.Agent.Tests.Tools;

/// <summary>
/// <c>WriteFile</c> keeps an existing file's encoding and line endings, as <c>EditFile</c> does: a model writes <c>\n</c>
/// and plain UTF-8, and before this a rewrite changed every line ending and dropped the byte-order mark, and an append
/// added UTF-8 bytes to a UTF-16 file.
/// </summary>
public class WriteFileFormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"write-file-{Guid.NewGuid():N}");
    private readonly ToolProvider _tools;

    public WriteFileFormatTests()
    {
        Directory.CreateDirectory(_dir);
        _tools = new ToolProvider(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        GC.SuppressFinalize(this);
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task Overwriting_keeps_the_byte_order_mark_and_the_files_line_endings()
    {
        File.WriteAllText(PathOf("a.cs"), "class A\r\n{\r\n}\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        await _tools.WriteFile("a.cs", "class B\n{\n}\n");

        var bytes = File.ReadAllBytes(PathOf("a.cs"));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("class B\r\n{\r\n}\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public async Task Appending_to_a_UTF16_file_writes_UTF16_with_no_second_mark()
    {
        File.WriteAllText(PathOf("log.txt"), "first\r\n", new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

        await _tools.WriteFile("log.txt", "second\n", append: true);

        var bytes = File.ReadAllBytes(PathOf("log.txt"));
        Assert.Equal([0xFF, 0xFE], bytes[..2]);
        Assert.Equal("first\r\nsecond\r\n", Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
    }

    [Fact]
    public async Task Content_that_carries_CRLF_itself_is_written_as_given()
    {
        File.WriteAllText(PathOf("lf.txt"), "x\ny\n");

        await _tools.WriteFile("lf.txt", "a\r\nb\r\n");

        Assert.Equal("a\r\nb\r\n", File.ReadAllText(PathOf("lf.txt")));
    }

    [Fact]
    public async Task A_new_file_is_UTF8_without_a_mark_and_written_as_given()
    {
        await _tools.WriteFile("new.txt", "héllo\nworld\n");

        var bytes = File.ReadAllBytes(PathOf("new.txt"));
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("héllo\nworld\n", Encoding.UTF8.GetString(bytes));
    }

    // Positive control for the newline rule: an LF file stays LF.
    [Fact]
    public async Task An_LF_file_stays_LF()
    {
        File.WriteAllText(PathOf("unix.sh"), "#!/bin/sh\necho 1\n");

        await _tools.WriteFile("unix.sh", "#!/bin/sh\necho 2\n");

        Assert.Equal("#!/bin/sh\necho 2\n", File.ReadAllText(PathOf("unix.sh")));
    }
}
