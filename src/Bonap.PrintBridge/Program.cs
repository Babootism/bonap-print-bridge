 main
using System.Text;

namespace Bonap.PrintBridge;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Bonap Print Bridge - ESC/POS helper\n");

        if (args.Length < 2)
        {
            ShowUsage();
            return 1;
        }

        var printerName = args[0];
 main
        return sent ? 0 : 2;
    }

    private static string BuildReceipt(string message)
    {
        var builder = new StringBuilder();
        builder.Append(EscPos.Initialize);
        builder.Append(EscPos.AlignCenter);
        builder.Append(EscPos.BoldOn);
        builder.Append("BONAP PRINT BRIDGE\n");
        builder.Append(EscPos.BoldOff);
        builder.Append(EscPos.AlignLeft);
        builder.Append(message);
        builder.Append('\n');
        builder.Append(EscPos.Feed(3));
        builder.Append(EscPos.FullCut);
        return builder.ToString();
    }

 main
        Console.WriteLine("Example: Bonap.PrintBridge \"Receipt Printer\" \"Bonjour, monde !\"");
    }
}
