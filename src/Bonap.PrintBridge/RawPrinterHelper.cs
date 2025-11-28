using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Bonap.PrintBridge;

public static class RawPrinterHelper
{
    [SupportedOSPlatform("windows")]
    public static bool SendStringToPrinter(string printerName, string data)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Raw printer access requires Windows.");
        }

        var bytes = Encoding.UTF8.GetBytes(data);
 main
        var pBytes = Marshal.AllocCoTaskMem(bytes.Length);

        try
        {
            Marshal.Copy(bytes, 0, pBytes, bytes.Length);
            return SendBytesToPrinter(printerName, pBytes, bytes.Length);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pBytes);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool SendBytesToPrinter(string printerName, IntPtr bytes, int count)
    {
        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
        {
            return false;
        }

        var docInfo = new DOC_INFO_1
        {
            pDocName = "Bonap.PrintBridge Document",
            pDataType = "RAW"
        };

        var success = false;

        try
        {
            if (!StartDocPrinter(hPrinter, 1, ref docInfo))
            {
                return false;
            }

            if (!StartPagePrinter(hPrinter))
            {
                return false;
            }

            success = WritePrinter(hPrinter, bytes, count, out _);
        }
        finally
        {
            EndPagePrinter(hPrinter);
            EndDocPrinter(hPrinter);
            ClosePrinter(hPrinter);
        }

        return success;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOC_INFO_1
    {
        [MarshalAs(UnmanagedType.LPStr)]
        public string? pDocName;

        [MarshalAs(UnmanagedType.LPStr)]
        public string? pOutputFile;

        [MarshalAs(UnmanagedType.LPStr)]
        public string? pDataType;
    }

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, EntryPoint = "OpenPrinterA")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool OpenPrinter(string src, out IntPtr hPrinter, IntPtr pd);

    [DllImport("winspool.drv", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, EntryPoint = "StartDocPrinterA")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, ref DOC_INFO_1 di);

    [DllImport("winspool.drv", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true, ExactSpelling = true, EntryPoint = "WritePrinter")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr bytes, int count, out int written);
}
