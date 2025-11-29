using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Bonap.PrintBridge;

internal static class PrinterInfoProvider
{
    private const int PrinterEnumLocal = 0x00000002;
    private const int PrinterEnumConnections = 0x00000004;

    private const int ErrorFileNotFound = 2;
    private const int ErrorInvalidPrinterName = 1801;

    public static IReadOnlyCollection<string> GetPrinterNames()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<string>();
        }

        var flags = PrinterEnumLocal | PrinterEnumConnections;
        var sizeNeeded = 0u;
        var printersReturned = 0u;

        EnumPrinters(flags, null, 4, IntPtr.Zero, 0, out sizeNeeded, out printersReturned);

        if (sizeNeeded == 0)
        {
            return Array.Empty<string>();
        }

        var buffer = Marshal.AllocHGlobal((int)sizeNeeded);
        try
        {
            if (!EnumPrinters(flags, null, 4, buffer, sizeNeeded, out sizeNeeded, out printersReturned))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var result = new List<string>((int)printersReturned);
            var current = buffer;
            var structSize = Marshal.SizeOf<PRINTER_INFO_4>();

            for (var i = 0; i < printersReturned; i++)
            {
                var info = Marshal.PtrToStructure<PRINTER_INFO_4>(current);
                if (info != null && !string.IsNullOrWhiteSpace(info.Value.pPrinterName))
                {
                    result.Add(info.Value.pPrinterName);
                }

                current = IntPtr.Add(current, structSize);
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string? GetDefaultPrinterName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var bufferSize = 0;
        GetDefaultPrinter(null!, ref bufferSize);

        if (bufferSize <= 0)
        {
            return null;
        }

        var builder = new StringBuilder(bufferSize);
        if (!GetDefaultPrinter(builder, ref bufferSize))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorFileNotFound || error == ErrorInvalidPrinterName
                ? null
                : throw new Win32Exception(error);
        }

        return builder.ToString();
    }

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumPrinters(
        int Flags,
        string? Name,
        uint Level,
        IntPtr pPrinterEnum,
        uint cbBuf,
        out uint pcbNeeded,
        out uint pcReturned);

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetDefaultPrinter(StringBuilder pszBuffer, ref int pcchBuffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PRINTER_INFO_4
    {
        public string pPrinterName;
        public string pServerName;
        public uint Attributes;
    }
}
