using FastDrop.Infrastructure.Storage;

namespace FastDrop.Tests.Storage;

public class CompositeStreamTests
{
    [Fact]
    public async Task ReadAsync_and_Seek_work_across_chunk_boundaries()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"fastdrop-composite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var first = Path.Combine(directory, "000000");
            var second = Path.Combine(directory, "000001");
            await File.WriteAllBytesAsync(first, "hello"u8.ToArray());
            await File.WriteAllBytesAsync(second, "world"u8.ToArray());

            await using var stream = new CompositeStream(new[] { first, second });
            var allBytes = new byte[10];
            var read = await stream.ReadAsync(allBytes);

            Assert.Equal(10, read);
            Assert.Equal("helloworld", System.Text.Encoding.UTF8.GetString(allBytes));

            stream.Seek(3, SeekOrigin.Begin);
            var resumedBytes = new byte[4];
            read = await stream.ReadAsync(resumedBytes);

            Assert.Equal(4, read);
            Assert.Equal("lowo", System.Text.Encoding.UTF8.GetString(resumedBytes));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
