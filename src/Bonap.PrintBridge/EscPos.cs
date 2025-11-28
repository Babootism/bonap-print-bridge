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

    public static string OpenDrawer(int pin, int t1 = 25, int t2 = 250)
    {
        if (pin is not 0 and not 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pin), pin, "pin must be 0 or 1.");
        }

        ValidateByte(nameof(t1), t1);
        ValidateByte(nameof(t2), t2);

        return $"\x1Bp{(char)(byte)pin}{(char)(byte)t1}{(char)(byte)t2}";
    }

    private static void ValidateByte(string name, int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 0, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 255, name);
    }

    public static byte[] AsBytes(string value) => Encoding.UTF8.GetBytes(value);
}
