using System.Buffers.Binary;
using Rampside.Core.Aircraft.Pmdg;
using Rampside.Core.Profiles;

namespace Rampside.Core.Tests;

/// <summary>Uses a small made-up header in the PMDG style; the real PMDG header is not part of this repository.</summary>
public class PmdgSdkHeaderTests
{
    private const string Header = """
        // Test header
        #define TEST_DATA_NAME     "Test_Data"   // area name
        #define TEST_DATA_ID       0x54455354
        #define EVENT_BASE         0x00011000
        #define EVT_ONE            (EVENT_BASE + 1)     // first
        #define EVT_BIG            (EVENT_BASE + 14005)
        #define COLS 3
        #define ROWS 2
        /* block comment with struct Fake { int x; }; inside */
        struct Cell
        {
            unsigned char Symbol;
            unsigned char Color;
        };

        struct Test_Data
        {
            bool    Flag;            // offset 0
            float   Value;           // offset 4 (aligned)
            char    Text[5];         // offset 8
            unsigned short Count;    // offset 14
            bool    Many[3];         // offset 16
            double  Precise;         // offset 24
            int     Signed;          // offset 32
        };

        struct Screen
        {
            Cell Cells[COLS][ROWS];
            bool Powered;
        };
        """;

    [Fact]
    public void Reads_defines_including_expressions()
    {
        var header = PmdgSdkHeader.Parse(Header);

        Assert.Equal("Test_Data", header.GetString("TEST_DATA_NAME"));
        Assert.Equal(0x54455354, header.GetNumber("TEST_DATA_ID"));
        Assert.Equal(0x11000 + 1, header.GetNumber("EVT_ONE"));
        Assert.Equal(69632 + 14005, header.GetNumber("EVT_BIG"));
        Assert.Null(header.GetNumber("TEST_DATA_NAME"));
        Assert.Null(header.GetNumber("MISSING"));
    }

    [Fact]
    public void Computes_msvc_layout_with_alignment()
    {
        var layout = PmdgSdkHeader.Parse(Header).GetStruct("Test_Data");

        Assert.Equal(0, layout.Find("Flag")!.Offset);
        Assert.Equal(4, layout.Find("Value")!.Offset);
        Assert.Equal(8, layout.Find("Text")!.Offset);
        Assert.Equal(14, layout.Find("Count")!.Offset);
        Assert.Equal(16, layout.Find("Many")!.Offset);
        Assert.Equal(24, layout.Find("Precise")!.Offset);
        Assert.Equal(32, layout.Find("Signed")!.Offset);
        Assert.Equal(40, layout.Size);
        Assert.Equal(8, layout.Alignment);
    }

    [Fact]
    public void Ignores_structs_inside_comments_and_handles_nested_structs()
    {
        var header = PmdgSdkHeader.Parse(Header);

        Assert.False(header.Structs.ContainsKey("Fake"));
        var screen = header.GetStruct("Screen");
        Assert.Equal(new[] { 3, 2 }, screen.Find("Cells")!.Dimensions);
        Assert.Equal(12, screen.Find("Powered")!.Offset);
        Assert.Equal(13, screen.Size);
    }

    [Fact]
    public void Data_block_decodes_values_by_name()
    {
        var layout = PmdgSdkHeader.Parse(Header).GetStruct("Test_Data");
        var bytes = new byte[layout.Size];
        bytes[0] = 1;
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), 12.5f);
        "ABC\0"u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 513);
        bytes[16 + 2] = 1;
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(24), -3.25);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32), -7);

        var block = new PmdgDataBlock(layout, bytes);

        Assert.True(block.GetBool("Flag"));
        Assert.Equal(12.5, block.GetNumber("Value"));
        Assert.Equal("ABC", block.GetString("Text"));
        Assert.Equal(513, block.GetNumber("Count"));
        Assert.False(block.GetBool("Many", 1));
        Assert.True(block.GetBool("Many", 2));
        Assert.Null(block.GetBool("Many", 3));
        Assert.Equal(-3.25, block.GetNumber("Precise"));
        Assert.Equal(-7, block.GetNumber("Signed"));
        Assert.Null(block.GetBool("Unknown"));
    }

    [Fact]
    public void Cdu_screen_is_decoded_column_major()
    {
        var header = PmdgSdkHeader.Parse(Header);
        var bytes = new byte[13];
        // Cells[column][row]: row 0 = "AB?", row 1 = "CD?"
        void Put(int column, int row, char c) => bytes[(column * 2 + row) * 2] = (byte)c;
        Put(0, 0, 'A');
        Put(1, 0, 'B');
        Put(2, 0, (char)0xA2);
        Put(0, 1, 'C');
        Put(1, 1, 'D');
        bytes[12] = 1;

        var screen = PmdgCduScreen.Decode(header, "Screen", bytes)!;

        Assert.Equal(new[] { "AB→", "CD " }, screen.Lines);
        Assert.True(screen.Powered);
    }

    [Fact]
    public void Client_data_chunks_cover_the_area_without_reading_past_its_end()
    {
        var size = 916;
        var offsets = ClientDataChunks.Offsets(size);

        Assert.Equal(ClientDataChunks.ChunkCount, offsets.Count);
        Assert.All(offsets, o => Assert.InRange(o + ClientDataChunks.ChunkSize, ClientDataChunks.ChunkSize, size));

        var original = Enumerable.Range(0, size).Select(i => (byte)(i * 7)).ToArray();
        var received = offsets.SelectMany(o => original.Skip(o).Take(ClientDataChunks.ChunkSize)).ToArray();

        Assert.Equal(original, ClientDataChunks.Assemble(received, size));
    }

    [Fact]
    public void Real_pmdg_737_header_parses_when_installed()
    {
        var path = PmdgSdkLocator.FindHeader(PmdgAircraftSdk.Pmdg737);
        if (path is null)
        {
            return; // PMDG 737 not installed on this machine.
        }

        var header = PmdgSdkHeader.Load(path);
        var sdk = PmdgAircraftSdk.Pmdg737;

        Assert.True(ClientDataChunks.Supports(header.GetStruct(sdk.DataStruct).Size));
        Assert.True(ClientDataChunks.Supports(header.GetStruct(sdk.CduStruct).Size));
        var profileMap = ProfileRepository.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "profiles"))
            .Resolve("737-800 PAX SSW SC").DoorMap;
        Assert.All(Pmdg737.DefaultDoorMap.Concat(profileMap), d =>
        {
            Assert.NotNull(header.GetNumber(d.Event));
            Assert.NotNull(header.GetStruct(sdk.DataStruct).Find(d.Indicator));
        });
    }
}
