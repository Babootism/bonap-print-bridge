using System.Collections.Generic;
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
        var command = args[1];

        if (IsDrawerCommand(command))
        {
            var pin = command.Equals("--drawer1", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0;
            var drawerPayload = BuildDrawerPayload(pin);
            var drawerSent = TrySendRaw(printerName, drawerPayload);
            return drawerSent ? 0 : 2;
        }

        var message = string.Join(" ", args.Skip(1));
        var payload = BuildReceipt(message);
        var sent = TrySendRaw(printerName, EscPos.AsBytes(payload));
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

    private static byte[] BuildDrawerPayload(byte pin)
    {
        var buffer = new List<byte>();
        buffer.AddRange(EscPos.OpenDrawer(pin));
        buffer.AddRange(EscPos.Cut());
        return buffer.ToArray();
    }

    private static bool TrySendRaw(string printerName, byte[] payload)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("Printing is only supported on Windows. Payload generation succeeded but nothing was sent.");
            return true;
        }

        var sent = RawPrinterHelper.SendBytesToPrinter(printerName, payload);
        Console.WriteLine(sent
            ? $"ESC/POS payload sent to '{printerName}'."
            : "Failed to send payload to the printer.");
        return sent;
    }

    private static bool IsDrawerCommand(string argument)
    {
        return argument.Equals("--drawer", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--drawer1", StringComparison.OrdinalIgnoreCase);
    }

    private static void ShowUsage()
    {
        Console.WriteLine("Usage: Bonap.PrintBridge <printerName> <message>");
        Console.WriteLine("       Bonap.PrintBridge <printerName> --drawer");
        Console.WriteLine("       Bonap.PrintBridge <printerName> --drawer1");
        Console.WriteLine("Example: Bonap.PrintBridge \"Receipt Printer\" \"Bonjour, monde !\"");
    }
}
