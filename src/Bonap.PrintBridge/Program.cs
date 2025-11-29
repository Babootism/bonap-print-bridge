using Bonap.PrintBridge;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();

var bridgeOptions = builder.Configuration.GetSection("Bridge").Get<BridgeOptions>() ?? new BridgeOptions();
var httpsEnabled = true;
string[]? certificatePathAttempts = null;
var selectedPort = bridgeOptions.Port > 0 ? bridgeOptions.Port : 49001;

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
    var configuredUrl = endpointSection.GetValue<string>("Url");
    var url = string.IsNullOrWhiteSpace(configuredUrl)
        ? $"https://127.0.0.1:{selectedPort}"
        : configuredUrl;
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
        var uriBuilder = new UriBuilder(url)
        {
            Port = selectedPort
        };
        var uri = uriBuilder.Uri;
        selectedPort = uri.Port;
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
        selectedPort = bridgeOptions.Port > 0 ? bridgeOptions.Port : selectedPort;
        options.Listen(IPAddress.Loopback, selectedPort);
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
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Request");
    var sw = Stopwatch.StartNew();

    await next();

    sw.Stop();
    logger.LogInformation(
        "{Method} {Path} responded {StatusCode} in {ElapsedMs}ms",
        context.Request.Method,
        context.Request.Path,
        context.Response.StatusCode,
        sw.ElapsedMilliseconds);
});

app.Use(async (context, next) =>
{
    if (!IsAuthorized(context, bridgeOptions.Token))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { ok = false, error = "unauthorized" });
        return;
    }

    await next();
});

app.UseStaticFiles();

app.MapGet("/health", (IServiceProvider services) =>
{
    var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    var addressesFeature = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
    var listeningUrls = addressesFeature?.Addresses?.ToArray() ?? Array.Empty<string>();

    return Results.Ok(new
    {
        ok = true,
        httpsEnabled,
        version,
        time = DateTimeOffset.UtcNow,
        port = selectedPort,
        listeningUrls
    });
});

app.MapGet("/admin", (IWebHostEnvironment env) =>
{
    var adminPath = Path.Combine(env.WebRootPath ?? string.Empty, "admin.html");
    if (File.Exists(adminPath))
    {
        return Results.File(adminPath, "text/html");
    }

    return Results.NotFound();
});

app.MapGet("/printers", () =>
{
    if (!OperatingSystem.IsWindows())
    {
        return Results.Ok(Array.Empty<object>());
    }

    var defaultPrinter = PrinterInfoProvider.GetDefaultPrinterName();
    var printers = PrinterInfoProvider.GetPrinterNames()
        .Select(name => new { name, isDefault = string.Equals(name, defaultPrinter, StringComparison.OrdinalIgnoreCase) })
        .ToArray();

    return Results.Ok(printers);
});

app.MapGet("/logs/tail", (HttpRequest request) =>
{
    var linesQuery = request.Query["lines"].FirstOrDefault();
    var lineCount = 200;

    if (!string.IsNullOrWhiteSpace(linesQuery) && int.TryParse(linesQuery, out var parsed) && parsed > 0)
    {
        lineCount = parsed;
    }

    if (!File.Exists(logPath))
    {
        return Results.File(Array.Empty<byte>(), "text/plain");
    }

    var lines = ReadLastLines(logPath, lineCount);
    return Results.Text(string.Join(Environment.NewLine, lines), "text/plain", Encoding.UTF8);
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
        var payload = EscPos.AsBytes(EscPos.OpenDrawer(pin, t1, t2));
        var sent = RawPrinterHelper.SendBytesToPrinter(printerName, payload, "Bonap.PrintBridge Drawer");
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

try
{
    app.Run();
    return 0;
}
catch (AddressInUseException ex)
{
    app.Logger.LogCritical(
        ex,
        "Failed to bind to port {Port}. Another process is already listening. Identify and stop the conflicting process (e.g., use `netstat -ano | find \"{Port}\"` or `lsof -i :{Port}` to find and kill the PID).",
        selectedPort);
    return 1;
}

static bool IsAuthorized(HttpContext context, string? expectedToken)
{
    if (context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    if (string.IsNullOrWhiteSpace(expectedToken))
    {
        return false;
    }

    if (HttpMethods.IsOptions(context.Request.Method))
    {
        return true;
    }

    var providedToken = context.Request.Headers.TryGetValue("X-Bridge-Token", out var provided)
        ? provided.ToString()
        : context.Request.Query["token"].FirstOrDefault();

    return string.Equals(providedToken, expectedToken, StringComparison.Ordinal);
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
        return PrinterInfoProvider.GetDefaultPrinterName()
            ?? throw new InvalidOperationException("No default printer configured.");
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

static IEnumerable<string> ReadLastLines(string path, int lineCount)
{
    var lines = new List<string>(lineCount);

    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(fs, Encoding.UTF8);

    while (!reader.EndOfStream)
    {
        lines.Add(reader.ReadLine() ?? string.Empty);
        if (lines.Count > lineCount)
        {
            lines.RemoveAt(0);
        }
    }

    return lines;
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
