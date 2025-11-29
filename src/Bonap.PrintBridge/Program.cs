using System.Collections.Generic;
using System.Drawing.Printing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using Bonap.PrintBridge;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();

var bridgeOptions = builder.Configuration.GetSection("Bridge").Get<BridgeOptions>() ?? new BridgeOptions();
var httpsEnabled = true;
string[]? certificatePathAttempts = null;

var logPath = EnsureLogPath();
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz ";
});
builder.Logging.AddProvider(new FileLoggerProvider(logPath));

builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection("Bridge"));
builder.Services.AddCors(options =>
{
    options.AddPolicy("BonapPrintBridgeCors", policy =>
    {
        policy.WithOrigins("https://bonap.ceramix.ovh")
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

builder.WebHost.ConfigureKestrel((context, options) =>
{
    var endpointSection = context.Configuration.GetSection("Kestrel:Endpoints:Https");
    var url = endpointSection.GetValue<string>("Url") ?? $"https://127.0.0.1:{bridgeOptions.Port}";
    var certificateSection = endpointSection.GetSection("Certificate");
    var certificatePath = certificateSection.GetValue<string>("Path");
    var certificatePassword = certificateSection.GetValue<string>("Password");

    var attemptedPaths = new List<string>();

    if (!string.IsNullOrWhiteSpace(certificatePath))
    {
        var resolvedCertificatePath = Path.IsPathRooted(certificatePath)
            ? certificatePath
            : Path.GetFullPath(certificatePath, builder.Environment.ContentRootPath);

        attemptedPaths.Add(resolvedCertificatePath);
    }

    var fallbackCertificatePath = Path.GetFullPath(
        Path.Combine(builder.Environment.ContentRootPath, "..", "..", "certs", "localhost.pfx"));

    if (!attemptedPaths.Contains(fallbackCertificatePath, StringComparer.OrdinalIgnoreCase))
    {
        attemptedPaths.Add(fallbackCertificatePath);
    }

    var resolvedPath = attemptedPaths.FirstOrDefault(File.Exists);
    certificatePathAttempts = attemptedPaths.ToArray();

    if (!string.IsNullOrEmpty(resolvedPath))
    {
        var uri = new Uri(url);
        options.Listen(IPAddress.Parse(uri.Host), uri.Port, listenOptions =>
        {
            if (string.IsNullOrWhiteSpace(certificatePassword))
            {
                listenOptions.UseHttps(resolvedPath);
            }
            else
            {
                listenOptions.UseHttps(resolvedPath, certificatePassword);
            }
        });

        httpsEnabled = true;
    }
    else
    {
        httpsEnabled = false;
        options.Listen(IPAddress.Loopback, bridgeOptions.Port);
    }
});

var app = builder.Build();

if (!httpsEnabled)
{
    app.Logger.LogWarning(
        "HTTPS is disabled because no certificate was found. Tried paths: {Paths}",
        certificatePathAttempts ?? Array.Empty<string>());
}

app.UseCors("BonapPrintBridgeCors");

app.Use(async (context, next) =>
{
    if (!IsAuthorized(context, bridgeOptions.Token))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
        return;
    }

    await next();
});

app.MapGet("/health", () =>
{
    var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    return Results.Ok(new
    {
        ok = true,
        httpsEnabled,
        version,
        time = DateTimeOffset.UtcNow
    });
});

app.MapGet("/printers", () =>
{
    if (!OperatingSystem.IsWindows())
    {
        return Results.Ok(Array.Empty<object>());
    }

    var defaultPrinter = new PrinterSettings().PrinterName;
    var printers = PrinterSettings.InstalledPrinters
        .Cast<string>()
        .Select(name => new { name, isDefault = string.Equals(name, defaultPrinter, StringComparison.OrdinalIgnoreCase) })
        .ToArray();

    return Results.Ok(printers);
});

app.MapPost("/print", (PrintRequest request, IOptions<BridgeOptions> options, ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("Print");
    if (string.IsNullOrWhiteSpace(request.DataBase64))
    {
        return Results.BadRequest(new { error = "dataBase64 is required." });
    }

    if (!string.Equals(request.ContentType, "raw-escpos", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Unsupported contentType. Only 'raw-escpos' is accepted." });
    }

    if (!OperatingSystem.IsWindows())
    {
        logger.LogWarning("Raw printing is only supported on Windows.");
        return Results.StatusCode(StatusCodes.Status501NotImplemented);
    }

    var printerName = ResolvePrinterName(options.Value, request.PrinterName);
    byte[] jobBytes;

    try
    {
        jobBytes = Convert.FromBase64String(request.DataBase64);
    }
    catch (FormatException)
    {
        return Results.BadRequest(new { error = "Invalid base64 payload." });
    }

    var jobName = string.IsNullOrWhiteSpace(request.JobName) ? "Bonap.PrintBridge Document" : request.JobName;
    var sent = RawPrinterHelper.SendBytesToPrinter(printerName, jobBytes, jobName);
    return sent
        ? Results.Ok(new { sent = true })
        : Results.Problem("Failed to send raw data to the printer.", statusCode: StatusCodes.Status502BadGateway);
});

app.MapPost("/drawer/open", (DrawerRequest request, IOptions<BridgeOptions> options, ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("Drawer");
    if (!OperatingSystem.IsWindows())
    {
        logger.LogWarning("Drawer opening is only supported on Windows.");
        return Results.StatusCode(StatusCodes.Status501NotImplemented);
    }

    var printerName = ResolvePrinterName(options.Value, request.PrinterName);
    var pin = request.Pin ?? options.Value.DefaultDrawerPin;
    var t1 = request.T1 ?? 25;
    var t2 = request.T2 ?? 250;

    try
    {
        var payload = EscPos.OpenDrawer(pin, t1, t2);
        var sent = RawPrinterHelper.SendStringToPrinter(printerName, payload, "Bonap.PrintBridge Drawer");
        return sent
            ? Results.Ok(new { opened = true })
            : Results.Problem("Failed to send drawer command.", statusCode: StatusCodes.Status502BadGateway);
    }
    catch (ArgumentOutOfRangeException ex)
    {
        logger.LogWarning(ex, "Invalid drawer parameters provided.");
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/receipt/print", (ReceiptRequest request, IOptions<BridgeOptions> options, ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("Receipt");
    if (!OperatingSystem.IsWindows())
    {
        logger.LogWarning("Receipt printing is only supported on Windows.");
        return Results.StatusCode(StatusCodes.Status501NotImplemented);
    }

    if (string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.BadRequest(new { error = "Text is required." });
    }

    var printerName = ResolvePrinterName(options.Value, request.PrinterName);
    var pin = request.Pin ?? options.Value.DefaultDrawerPin;

    var builder = new StringBuilder();
    builder.Append(EscPos.Initialize);
    builder.Append(request.Text);
    builder.Append('\n');
    builder.Append(EscPos.Feed(3));
    builder.Append(EscPos.FullCut);

    if (request.OpenDrawer)
    {
        builder.Append(EscPos.OpenDrawer(pin));
    }

    var sent = RawPrinterHelper.SendBytesToPrinter(printerName, EscPos.AsBytes(builder.ToString()), "Bonap.PrintBridge Receipt");
    return sent
        ? Results.Ok(new { printed = true })
        : Results.Problem("Failed to send receipt to printer.", statusCode: StatusCodes.Status502BadGateway);
});

app.Run();

static bool IsAuthorized(HttpContext context, string? expectedToken)
{
    if (string.IsNullOrWhiteSpace(expectedToken))
    {
        return false;
    }

    if (HttpMethods.IsOptions(context.Request.Method))
    {
        return true;
    }

    if (!context.Request.Headers.TryGetValue("X-Bridge-Token", out var provided))
    {
        return false;
    }

    return string.Equals(provided.ToString(), expectedToken, StringComparison.Ordinal);
}

static string ResolvePrinterName(BridgeOptions options, string? requestedPrinter)
{
    if (!string.IsNullOrWhiteSpace(requestedPrinter))
    {
        return requestedPrinter;
    }

    if (!string.IsNullOrWhiteSpace(options.DefaultPrinterName))
    {
        return options.DefaultPrinterName;
    }

    if (OperatingSystem.IsWindows())
    {
        return new PrinterSettings().PrinterName;
    }

    throw new InvalidOperationException("No printer specified and no default printer configured.");
}

static string EnsureLogPath()
{
    var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    var logDirectory = Path.Combine(programData, "BonapPrintBridge", "logs");
    Directory.CreateDirectory(logDirectory);
    return Path.Combine(logDirectory, "bridge.log");
}

internal record PrintRequest(string? PrinterName, string? JobName, string DataBase64, string ContentType);

internal record DrawerRequest(string? PrinterName, int? Pin, int? T1, int? T2);

internal record ReceiptRequest(string? PrinterName, string Text, bool OpenDrawer, int? Pin);

internal class BridgeOptions
{
    public int Port { get; set; } = 49001;

    public string? Token { get; set; }

    public string? DefaultPrinterName { get; set; }

    public int DefaultDrawerPin { get; set; } = 0;
}
