using System.Text;

namespace Bonap.PrintBridge;

public static class EscPos
{
    public const string Initialize = "\x1B@";
    public const string AlignLeft = "\x1Ba\x00";
    public const string AlignCenter = "\x1Ba\x01";
    public const string AlignRight = "\x1Ba\x02";
    public const string BoldOn = "\x1BE\x01";
    public const string BoldOff = "\x1BE\x00";
    public const string DoubleHeightWidthOn = "\x1D!\x11";
    public const string DoubleHeightWidthOff = "\x1D!\x00";
    public const string FullCut = "\x1DV\x42\x00";

    public static string Feed(int lines)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lines);
        return $"\x1Bd{(byte)lines}";
    }

    public static byte[] OpenDrawer(byte pin = 0, byte t1 = 25, byte t2 = 250)
    {
        if (pin > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pin), "Pin must be 0 or 1.");
        }

        return new[] { (byte)0x1B, (byte)0x70, pin, t1, t2 };
    }

    public static byte[] Cut() => new byte[] { 0x1D, 0x56, 0x41, 0x00 };

    public static byte[] AsBytes(string value) => Encoding.UTF8.GetBytes(value);
}
