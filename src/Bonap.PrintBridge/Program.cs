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
        string payload;

        try
        {
            payload = IsDrawerCommand(args)
                ? BuildDrawerPayload(args)
                : BuildReceipt(string.Join(" ", args.Skip(1)));
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine(ex.Message);
            ShowUsage();
            return 1;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("Printing is only supported on Windows. Payload generation succeeded but nothing was sent.");
            return 0;
        }

        var sent = RawPrinterHelper.SendStringToPrinter(printerName, payload);
        Console.WriteLine(sent
            ? $"ESC/POS payload sent to '{printerName}'."
            : "Failed to send payload to the printer.");

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

    private static string BuildDrawerPayload(string[] args)
    {
        if (args.Length is not 2 and not 4)
        {
            throw new ArgumentException("Usage: Bonap.PrintBridge <printerName> --drawer[1] [t1 t2]");
        }

        var pin = args[1] == "--drawer1" ? 1 : 0;
        var timings = args.Length == 4
            ? ParseTimings(args[2], args[3])
            : (t1: 25, t2: 250);

        return EscPos.OpenDrawer(pin, timings.t1, timings.t2);
    }

    private static (int t1, int t2) ParseTimings(string t1Value, string t2Value)
    {
        return (ParseByte(t1Value, "t1"), ParseByte(t2Value, "t2"));
    }

    private static int ParseByte(string value, string name)
    {
        if (!int.TryParse(value, out var parsed))
        {
            throw new ArgumentException($"Invalid {name}: '{value}' is not an integer.");
        }

        if (parsed is < 0 or > 255)
        {
            throw new ArgumentOutOfRangeException(name, parsed, $"{name} must be between 0 and 255.");
        }

        return parsed;
    }

    private static bool IsDrawerCommand(string[] args)
    {
        return args.Length is 2 or 4 && args[1] is "--drawer" or "--drawer1";
    }

    private static void ShowUsage()
    {
        Console.WriteLine("Usage: Bonap.PrintBridge <printerName> <message>");
        Console.WriteLine("Example: Bonap.PrintBridge \"Receipt Printer\" \"Bonjour, monde !\"");
        Console.WriteLine("Drawer: Bonap.PrintBridge <printerName> --drawer[1] [t1 t2]");
        Console.WriteLine("(defaults: t1=25, t2=250; 0 <= t1,t2 <= 255)");
    }
}
