using CaYaScreenBridge.Core.Model;
using Xunit;

namespace CaYaScreenBridge.Core.Tests;

public class EdidBlockTests
{
    /// <summary>
    /// Builds a minimal but structurally valid EDID base block: header, manufacturer, product and
    /// serial, the coarse centimetre size, one detailed timing descriptor carrying the millimetre
    /// size, and text descriptors for the model name and serial number.
    /// </summary>
    private static byte[] BuildEdid(
        string manufacturer = "GSM",
        ushort product = 0x5B09,
        uint serial = 0x12345678,
        int widthCm = 60,
        int heightCm = 34,
        int widthMm = 597,
        int heightMm = 336,
        string modelName = "LG ULTRAFINE",
        string textSerial = "SN12345")
    {
        var edid = new byte[128];

        edid[0] = 0x00;
        for (int i = 1; i <= 6; i++)
        {
            edid[i] = 0xFF;
        }

        edid[7] = 0x00;

        int packed =
            (((manufacturer[0] - 'A' + 1) & 0x1F) << 10) |
            (((manufacturer[1] - 'A' + 1) & 0x1F) << 5) |
            ((manufacturer[2] - 'A' + 1) & 0x1F);

        edid[8] = (byte)(packed >> 8);
        edid[9] = (byte)(packed & 0xFF);

        edid[10] = (byte)(product & 0xFF);
        edid[11] = (byte)(product >> 8);

        edid[12] = (byte)(serial & 0xFF);
        edid[13] = (byte)((serial >> 8) & 0xFF);
        edid[14] = (byte)((serial >> 16) & 0xFF);
        edid[15] = (byte)((serial >> 24) & 0xFF);

        edid[21] = (byte)widthCm;
        edid[22] = (byte)heightCm;

        // Descriptor 1 at offset 54: a detailed timing block, marked by a non zero pixel clock.
        edid[54] = 0x01;
        edid[55] = 0x01;
        edid[54 + 12] = (byte)(widthMm & 0xFF);
        edid[54 + 13] = (byte)(heightMm & 0xFF);
        edid[54 + 14] = (byte)(((widthMm >> 8) << 4) | ((heightMm >> 8) & 0x0F));

        WriteTextDescriptor(edid, 72, 0xFC, modelName);
        WriteTextDescriptor(edid, 90, 0xFF, textSerial);

        return edid;
    }

    private static void WriteTextDescriptor(byte[] edid, int offset, byte tag, string text)
    {
        edid[offset] = 0;
        edid[offset + 1] = 0;
        edid[offset + 2] = 0;
        edid[offset + 3] = tag;
        edid[offset + 4] = 0;

        for (int i = 0; i < 13; i++)
        {
            edid[offset + 5 + i] = i < text.Length ? (byte)text[i] : (byte)0x0A;
        }
    }

    [Fact]
    public void ParsesTheMillimetreSizeFromTheDetailedTimingDescriptor()
    {
        EdidInfo info = EdidBlock.Parse(BuildEdid());

        Assert.True(info.HasSize);
        Assert.Equal(597, info.WidthMm, 6);
        Assert.Equal(336, info.HeightMm, 6);
    }

    [Fact]
    public void ParsesIdentityFields()
    {
        EdidInfo info = EdidBlock.Parse(BuildEdid());

        Assert.Equal("GSM", info.ManufacturerCode);
        Assert.Equal(0x5B09, info.ProductCode);
        Assert.Equal("LG ULTRAFINE", info.ModelName);
        Assert.Equal("SN12345", info.SerialNumber);
    }

    [Fact]
    public void FallsBackToTheCentimetreFieldsWhenNoTimingSizeIsPresent()
    {
        byte[] edid = BuildEdid(widthMm: 0, heightMm: 0);

        EdidInfo info = EdidBlock.Parse(edid);

        Assert.Equal(600, info.WidthMm, 6);
        Assert.Equal(340, info.HeightMm, 6);
    }

    [Fact]
    public void FallsBackToTheNumericSerialWhenNoTextSerialIsPresent()
    {
        byte[] edid = BuildEdid(textSerial: string.Empty);

        EdidInfo info = EdidBlock.Parse(edid);

        Assert.Equal("12345678", info.SerialNumber);
    }

    [Fact]
    public void RejectsABlockWithABadHeader()
    {
        byte[] edid = BuildEdid();
        edid[3] = 0x00;

        Assert.Same(EdidInfo.Empty, EdidBlock.Parse(edid));
    }

    [Fact]
    public void RejectsATruncatedBlock()
    {
        Assert.Same(EdidInfo.Empty, EdidBlock.Parse(new byte[64]));
        Assert.Same(EdidInfo.Empty, EdidBlock.Parse(Array.Empty<byte>()));
    }
}
