using System.Buffers.Binary;
using System.Text;

namespace Rampside.Core.Aircraft.Pmdg;

/// <summary>One snapshot of a PMDG client data area, decoded by field name using the parsed header layout.</summary>
public sealed class PmdgDataBlock
{
    private readonly PmdgStructLayout _layout;
    private readonly byte[] _data;

    public PmdgDataBlock(PmdgStructLayout layout, byte[] data)
    {
        if (data.Length < layout.Size)
        {
            throw new ArgumentException($"Expected {layout.Size} bytes for {layout.Name}, got {data.Length}.", nameof(data));
        }

        _layout = layout;
        _data = data;
    }

    public bool? GetBool(string field, int index = 0)
    {
        var f = Element(field, index, out var offset);
        return f is null ? null : _data[offset] != 0;
    }

    public double? GetNumber(string field, int index = 0)
    {
        var f = Element(field, index, out var offset);
        if (f is null)
        {
            return null;
        }

        var span = _data.AsSpan(offset);
        return f.Kind switch
        {
            PmdgFieldKind.Bool => span[0] != 0 ? 1 : 0,
            PmdgFieldKind.Char or PmdgFieldKind.Int8 => (sbyte)span[0],
            PmdgFieldKind.UInt8 => span[0],
            PmdgFieldKind.Int16 => BinaryPrimitives.ReadInt16LittleEndian(span),
            PmdgFieldKind.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(span),
            PmdgFieldKind.Int32 => BinaryPrimitives.ReadInt32LittleEndian(span),
            PmdgFieldKind.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(span),
            PmdgFieldKind.Float32 => BinaryPrimitives.ReadSingleLittleEndian(span),
            PmdgFieldKind.Float64 => BinaryPrimitives.ReadDoubleLittleEndian(span),
            _ => null,
        };
    }

    /// <summary>Reads a zero-terminated char array.</summary>
    public string? GetString(string field)
    {
        var f = _layout.Find(field);
        if (f is null || f.Kind != PmdgFieldKind.Char)
        {
            return null;
        }

        var bytes = _data.AsSpan(f.Offset, f.Size);
        var end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? bytes : bytes.Slice(0, end));
    }

    private PmdgField? Element(string field, int index, out int offset)
    {
        offset = 0;
        var f = _layout.Find(field);
        if (f is null || f.Kind == PmdgFieldKind.Struct || index < 0 || index >= f.Count)
        {
            return null;
        }

        offset = f.Offset + index * f.ElementSize;
        return f;
    }
}

/// <summary>Text content of a PMDG CDU screen.</summary>
public sealed record PmdgCduScreen(IReadOnlyList<string> Lines, bool Powered)
{
    /// <summary>Decodes a CDU screen struct of the form <c>Cell Cells[COLUMNS][ROWS]; bool Powered;</c>.</summary>
    public static PmdgCduScreen? Decode(PmdgSdkHeader header, string screenStruct, byte[] data)
    {
        var screen = header.GetStruct(screenStruct);
        var cells = screen.Find("Cells");
        var powered = screen.Find("Powered");
        if (cells is null || cells.Kind != PmdgFieldKind.Struct || cells.Dimensions.Count != 2 || data.Length < screen.Size)
        {
            return null;
        }

        var symbol = header.GetStruct(cells.TypeName).Find("Symbol");
        if (symbol is null)
        {
            return null;
        }

        int columns = cells.Dimensions[0], rows = cells.Dimensions[1];
        var lines = new string[rows];
        for (var row = 0; row < rows; row++)
        {
            var sb = new StringBuilder(columns);
            for (var column = 0; column < columns; column++)
            {
                var offset = cells.Offset + (column * rows + row) * cells.ElementSize + symbol.Offset;
                sb.Append(ToChar(data[offset]));
            }

            lines[row] = sb.ToString();
        }

        return new PmdgCduScreen(lines, powered is not null && data[powered.Offset] != 0);
    }

    private static char ToChar(byte b) => b switch
    {
        0xA1 => '←',
        0xA2 => '→',
        0xA3 => '↑',
        0xA4 => '↓',
        < 32 => ' ',
        _ => (char)b,
    };
}

/// <summary>
/// The managed SimConnect wrapper needs a fixed-size struct to receive client data, but the PMDG struct size is
/// only known at runtime. Rampside requests a fixed number of fixed-size chunks; the last chunks are shifted
/// back so they never read past the end of the area, and the bytes are put back together here.
/// </summary>
public static class ClientDataChunks
{
    public const int ChunkSize = 256;
    public const int ChunkCount = 16;
    public const int BufferSize = ChunkSize * ChunkCount;

    public static bool Supports(int size) => size >= ChunkSize && size <= BufferSize;

    public static IReadOnlyList<int> Offsets(int size)
    {
        if (!Supports(size))
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, $"Client data size must be between {ChunkSize} and {BufferSize} bytes.");
        }

        return Enumerable.Range(0, ChunkCount).Select(i => Math.Min(i * ChunkSize, size - ChunkSize)).ToArray();
    }

    public static byte[] Assemble(ReadOnlySpan<byte> received, int size)
    {
        var offsets = Offsets(size);
        var result = new byte[size];
        for (var i = 0; i < offsets.Count; i++)
        {
            received.Slice(i * ChunkSize, ChunkSize).CopyTo(result.AsSpan(offsets[i], ChunkSize));
        }

        return result;
    }
}
