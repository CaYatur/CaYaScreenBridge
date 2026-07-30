using System.Text;

namespace CaYaScreenBridge.Core.Model;

/// <summary>Panel facts recovered from a monitor's EDID block.</summary>
public sealed record EdidInfo(
    double WidthMm,
    double HeightMm,
    string ModelName,
    string SerialNumber,
    string ManufacturerCode,
    ushort ProductCode)
{
    public static readonly EdidInfo Empty = new(0, 0, string.Empty, string.Empty, string.Empty, 0);

    public bool HasSize => WidthMm > 1 && HeightMm > 1;
}

/// <summary>
/// Parses the 128 byte EDID base block.
///
/// EDID is what makes a physical layout possible without asking anyone to measure their monitors: it
/// carries the real panel dimensions in millimetres, plus a model name and serial number that give
/// each display an identity that survives reboots and cable swaps.
/// </summary>
public static class EdidBlock
{
    public static EdidInfo Parse(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < 128 || !HasValidHeader(edid))
        {
            return EdidInfo.Empty;
        }

        string manufacturer = DecodeManufacturer(edid);
        ushort productCode = (ushort)(edid[10] | (edid[11] << 8));

        uint numericSerial = (uint)(edid[12] | (edid[13] << 8) | (edid[14] << 16) | (edid[15] << 24));

        string modelName = string.Empty;
        string textSerial = string.Empty;
        double widthMm = 0;
        double heightMm = 0;

        for (int offset = 54; offset + 18 <= 126; offset += 18)
        {
            // A descriptor whose first two bytes are non zero is a detailed timing descriptor, and
            // those carry the image size at millimetre precision rather than the centimetre
            // precision of the basic parameters block.
            bool isTiming = edid[offset] != 0 || edid[offset + 1] != 0;

            if (isTiming)
            {
                if (widthMm <= 0)
                {
                    int w = edid[offset + 12] | ((edid[offset + 14] >> 4) << 8);
                    int h = edid[offset + 13] | ((edid[offset + 14] & 0x0F) << 8);
                    if (w > 0 && h > 0)
                    {
                        widthMm = w;
                        heightMm = h;
                    }
                }

                continue;
            }

            switch (edid[offset + 3])
            {
                case 0xFC:
                    modelName = DecodeText(edid, offset + 5);
                    break;
                case 0xFF:
                    textSerial = DecodeText(edid, offset + 5);
                    break;
            }
        }

        if (widthMm <= 0 && edid[21] > 0 && edid[22] > 0)
        {
            widthMm = edid[21] * 10.0;
            heightMm = edid[22] * 10.0;
        }

        string serial = textSerial.Length > 0
            ? textSerial
            : numericSerial != 0 ? numericSerial.ToString("X8") : string.Empty;

        return new EdidInfo(widthMm, heightMm, modelName, serial, manufacturer, productCode);
    }

    private static bool HasValidHeader(ReadOnlySpan<byte> edid) =>
        edid[0] == 0x00 && edid[1] == 0xFF && edid[2] == 0xFF && edid[3] == 0xFF &&
        edid[4] == 0xFF && edid[5] == 0xFF && edid[6] == 0xFF && edid[7] == 0x00;

    /// <summary>Bytes 8 and 9 hold three five bit letters, biased so that 1 maps to 'A'.</summary>
    private static string DecodeManufacturer(ReadOnlySpan<byte> edid)
    {
        int packed = (edid[8] << 8) | edid[9];
        Span<char> chars =
        [
            (char)('A' + ((packed >> 10) & 0x1F) - 1),
            (char)('A' + ((packed >> 5) & 0x1F) - 1),
            (char)('A' + (packed & 0x1F) - 1),
        ];

        foreach (char c in chars)
        {
            if (c is < 'A' or > 'Z')
            {
                return string.Empty;
            }
        }

        return new string(chars);
    }

    private static string DecodeText(ReadOnlySpan<byte> edid, int start)
    {
        var builder = new StringBuilder(13);

        for (int i = start; i < start + 13 && i < edid.Length; i++)
        {
            byte b = edid[i];
            if (b == 0x0A)
            {
                break;
            }

            if (b is >= 0x20 and < 0x7F)
            {
                builder.Append((char)b);
            }
        }

        return builder.ToString().Trim();
    }
}
