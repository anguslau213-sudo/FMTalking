using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using ZstdSharp;

namespace FmPlayerTalk;

internal sealed class ManagerAssistantSaveFile : IDisposable
{
    private const int HeaderSize = 26;
    private const int TrailerOffsetField = 9;
    private const int TrailerHeaderSize = 9;
    private const int DirectoryLimit = 64 * 1024 * 1024;
    private const int BufferSize = 128 * 1024;
    private static readonly byte[] FileMagic = { 0x02, 0x01, (byte)'f', (byte)'m', (byte)'f', (byte)'.' };
    private static readonly byte[] SectionMagic = { 0x03, 0x01, (byte)'t', (byte)'a', (byte)'d', (byte)'.' };
    private readonly record struct Section(long Offset, long CompressedSize, long Size);
    private readonly FileStream stream;
    private readonly string path;
    private readonly DateTime writeTime;
    private readonly long length;
    private readonly Dictionary<string, Section> sections;

    private ManagerAssistantSaveFile(string path, FileStream stream, DateTime writeTime, long length,
        Dictionary<string, Section> sections)
    {
        this.path = path;
        this.stream = stream;
        this.writeTime = writeTime;
        this.length = length;
        this.sections = sections;
    }

    internal static ManagerAssistantSaveFile Open(string path, CancellationToken token)
    {
        var writeTime = File.GetLastWriteTimeUtc(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, BufferSize);
        try
        {
            var length = stream.Length;
            var header = new byte[HeaderSize];
            ReadAll(stream, header, token);
            if (!header.AsSpan().StartsWith(FileMagic))
                throw new InvalidDataException("The save file does not have the FMF signature.");
            var trailer = checked(TrailerOffsetField + (long)U64(header, TrailerOffsetField));
            var archiveBytes = length - trailer - TrailerHeaderSize;
            if (trailer < HeaderSize || archiveBytes <= 0 || archiveBytes > DirectoryLimit)
                throw new InvalidDataException("The save file directory is out of bounds.");

            stream.Position = trailer;
            var trailerHeader = new byte[TrailerHeaderSize];
            ReadAll(stream, trailerHeader, token);
            if (!trailerHeader.AsSpan().StartsWith(FileMagic))
                throw new InvalidDataException("The save file directory does not have the FMF signature.");

            using var payload = new MemoryStream();
            using var archive = new ZstdSectionStream(stream, archiveBytes);
            using var decompressor = new DecompressionStream(archive);
            var buffer = new byte[BufferSize];
            try
            {
                while (decompressor.Read(buffer, 0, buffer.Length) is var count && count != 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (payload.Length + count > DirectoryLimit)
                        throw new InvalidDataException("The save file directory exceeds the size limit.");
                    payload.Write(buffer, 0, count);
                }
            }
            catch (ZstdException error)
            {
                throw new InvalidDataException("The save file directory could not be read.", error);
            }
            var sections = ReadDirectory(payload.GetBuffer().AsSpan(0, (int)payload.Length), trailer, token);
            return new ManagerAssistantSaveFile(path, stream, writeTime, length, sections);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal byte[] ReadSection(string name, ushort schema, int limit, CancellationToken token)
    {
        if (!sections.TryGetValue(name, out var section) || section.Size < 8 || section.Size > limit)
            throw new InvalidDataException("A required save section is absent or exceeds the size limit.");
        stream.Position = section.Offset;
        using var compressed = new ZstdSectionStream(stream, section.CompressedSize);
        using var decompressor = new DecompressionStream(compressed);
        var bytes = new byte[(int)section.Size];
        try
        {
            ReadAll(decompressor, bytes, token);
            if (decompressor.ReadByte() != -1 || compressed.Remaining != 0)
                throw new InvalidDataException("A required save section has an unexpected length.");
        }
        catch (ZstdException error)
        {
            throw new InvalidDataException("A required save section could not be read.", error);
        }
        if (!bytes.AsSpan().StartsWith(SectionMagic) ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2)) != schema)
            throw new InvalidDataException("A required save section has an unsupported layout.");
        return bytes;
    }

    internal bool IsUnchanged()
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length == length && info.LastWriteTimeUtc == writeTime && stream.Length == length;
    }

    public void Dispose() => stream.Dispose();

    private static Dictionary<string, Section> ReadDirectory(ReadOnlySpan<byte> payload, long trailer,
        CancellationToken token)
    {
        if (payload.Length < 8 || U32(payload, 0) > Math.Min(4096, payload.Length - 8))
            throw new InvalidDataException("The save file directory header is incomplete.");
        var cursor = 8 + (int)U32(payload, 0);
        var sections = new Dictionary<string, Section>(StringComparer.Ordinal);
        var entryCount = 0;
        while (cursor <= payload.Length - 4)
        {
            token.ThrowIfCancellationRequested();
            var nameLength = U32(payload, cursor);
            if (nameLength < 1 || nameLength > 256 || ++entryCount > 100_000 ||
                nameLength > payload.Length - cursor - 52)
                break;
            var nameBytes = payload.Slice(cursor + 4, (int)nameLength);
            var extensionAt = cursor + 4 + (int)nameLength;
            if (U32(payload, extensionAt) != 4 || payload[extensionAt + 4] != '.')
                break;
            var name = Encoding.ASCII.GetString(nameBytes);
            var extension = Encoding.ASCII.GetString(payload.Slice(extensionAt + 4, 4));
            var fields = extensionAt + 8;
            cursor = fields + 40;
            if (extension != ".dat" || (name != "save_game_summary" && name != "game_info" &&
                name != "humans" && name != "game_db"))
                continue;
            var offset = checked(HeaderSize + (long)U64(payload, fields));
            var compressedSize = checked((long)U64(payload, fields + 8));
            var size = checked((long)U64(payload, fields + 16));
            if (offset >= trailer || compressedSize <= 0 || compressedSize > trailer - offset ||
                size > Math.Max(DirectoryLimit, 64 * compressedSize) ||
                !sections.TryAdd(name, new Section(offset, compressedSize, size)))
                throw new InvalidDataException("A required save section points outside the save.");
        }
        if (sections.Count != 4)
            throw new InvalidDataException("A required save section is missing from the save directory.");
        return sections;
    }

    private static void ReadAll(Stream input, byte[] bytes, CancellationToken token)
    {
        for (var position = 0; position < bytes.Length;)
        {
            token.ThrowIfCancellationRequested();
            var read = input.Read(bytes, position, Math.Min(BufferSize, bytes.Length - position));
            if (read == 0)
                throw new EndOfStreamException("The save data ended early.");
            position += read;
        }
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));

    private sealed class ZstdSectionStream : Stream
    {
        private readonly Stream source;
        internal long Remaining { get; private set; }

        internal ZstdSectionStream(Stream source, long length)
        {
            this.source = source;
            Remaining = length;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = source.Read(buffer[..(int)Math.Min(buffer.Length, Remaining)]);
            Remaining -= read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
