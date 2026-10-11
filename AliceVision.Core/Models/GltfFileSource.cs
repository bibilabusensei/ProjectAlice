using System.Buffers.Binary;

namespace AliceVision.Core.Models;

// Only referenced ranges are read. A large GLB is never copied wholesale into phone RAM.
internal abstract class GltfBinarySource
{
    public abstract int Length { get; }
    public abstract ReadOnlySpan<byte> Slice(int offset, int length);
    public abstract byte[] ReadBytes(int offset, int length);
    public abstract Func<byte[]> CaptureReader(int offset, int length);
}

internal sealed class GltfMemorySource(ReadOnlyMemory<byte> bytes) : GltfBinarySource
{
    public override int Length => bytes.Length;
    public override ReadOnlySpan<byte> Slice(int offset, int length) => bytes.Span.Slice(offset, length);
    public override byte[] ReadBytes(int offset, int length) => bytes.Slice(offset, length).ToArray();
    public override Func<byte[]> CaptureReader(int offset, int length)
    {
        _ = bytes.Slice(offset, length);
        return () => bytes.Slice(offset, length).ToArray();
    }
}

internal static class GltfFileSource
{
    public static GltfDocument Read(string path,
        Func<ReadOnlyMemory<byte>, GltfBinarySource, GltfDocument> parse)
    {
        path = Path.GetFullPath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.RandomAccess);
        long fileLength = file.Length;
        if (fileLength is < 28 or > GltfDocument.MaximumFileBytes)
            throw new InvalidDataException("GLB 文件为空或超过 1 GB（1024 MB）。");
        Span<byte> header = stackalloc byte[12];
        file.ReadExactly(header);
        if (U32(header, 0) != 0x46546c67 || U32(header, 4) != 2 || U32(header, 8) != fileLength)
            throw new InvalidDataException("需要完整的 GLB 2.0 / VRM 二进制模型。");
        byte[]? json = null;
        int binaryOffset = -1, binaryLength = 0;
        long offset = 12;
        Span<byte> chunk = stackalloc byte[8];
        while (offset < fileLength)
        {
            if (fileLength - offset < 8) throw new InvalidDataException("GLB 块头不完整。");
            file.Position = offset;
            file.ReadExactly(chunk);
            uint length = U32(chunk, 0), type = U32(chunk, 4);
            offset += 8;
            if (length == 0 || length % 4 != 0 || length > fileLength - offset)
                throw new InvalidDataException("GLB 块长度或对齐无效。");
            if (type == 0x4e4f534a && json is null && binaryOffset < 0 && offset == 20)
            {
                if (length > 8 * 1024 * 1024) throw new InvalidDataException("模型 JSON 过大。");
                json = new byte[checked((int)length)];
                file.ReadExactly(json);
            }
            else if (type == 0x004e4942 && json is not null && binaryOffset < 0)
            {
                binaryOffset = checked((int)offset);
                binaryLength = checked((int)length);
            }
            else throw new InvalidDataException("仅支持一个 JSON 块和一个嵌入 BIN 块。");
            offset += length;
        }
        if (json is null || binaryOffset < 0) throw new InvalidDataException("模型缺少嵌入几何数据。");
        var stamp = FileStamp.Read(path);
        var result = parse(json, new FileBinarySource(file, path, stamp, binaryOffset, binaryLength));
        if (file.Length != fileLength || FileStamp.Read(path) != stamp)
            throw new InvalidDataException("读取期间模型文件发生变化，请重新导入。");
        return result;
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));

    private readonly record struct FileStamp(long Length, DateTime Written, DateTime Created)
    {
        public static FileStamp Read(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("模型源文件不可用，请重新导入。");
            return new(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
        }
    }

    private sealed class FileBinarySource(FileStream file, string path, FileStamp stamp, int start, int length) : GltfBinarySource
    {
        private const int PageSize = 64 * 1024;
        private readonly byte[] _page = new byte[PageSize + 16];
        private int _pageStart = -1, _pageCount;
        public override int Length => length;

        private void ValidateRange(int offset, int count)
        {
            if (offset < 0 || count < 0 || (long)offset + count > Length)
                throw new InvalidDataException("模型缓冲区范围无效。");
        }

        public override byte[] ReadBytes(int offset, int count)
        {
            ValidateRange(offset, count);
            byte[] result = new byte[count];
            file.Position = (long)start + offset;
            file.ReadExactly(result);
            return result;
        }

        public override Func<byte[]> CaptureReader(int offset, int count)
        {
            ValidateRange(offset, count);
            long absoluteOffset = (long)start + offset;
            string sourcePath = path;
            FileStamp expected = stamp;
            // The document outlives the parsing stream. Capture only the exact
            // validated range and file identity, not its handle or decoded data.
            return () =>
            {
                using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
                if (source.Length != expected.Length || FileStamp.Read(sourcePath) != expected ||
                    absoluteOffset < 0 || count > source.Length - absoluteOffset)
                    throw new InvalidDataException("模型源文件已变化，请重新导入；未继续加载纹理。");
                byte[] result = new byte[count];
                source.Position = absoluteOffset;
                source.ReadExactly(result);
                if (source.Length != expected.Length || FileStamp.Read(sourcePath) != expected)
                    throw new InvalidDataException("读取纹理期间模型源文件发生变化，请重新导入。");
                return result;
            };
        }

        public override ReadOnlySpan<byte> Slice(int offset, int count)
        {
            ValidateRange(offset, count);
            if (count == 0) return ReadOnlySpan<byte>.Empty;
            if (count > 16)
            {
                return ReadBytes(offset, count);
            }
            if (_pageStart < 0 || offset < _pageStart || (long)offset + count > _pageStart + _pageCount)
            {
                _pageStart = offset / PageSize * PageSize;
                _pageCount = Math.Min(_page.Length, Length - _pageStart);
                file.Position = (long)start + _pageStart;
                file.ReadExactly(_page.AsSpan(0, _pageCount));
            }
            return _page.AsSpan(offset - _pageStart, count);
        }
    }
}
