using Bonap.PrintBridge;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Reflection;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();

var bridgeOptions = builder.Configuration.GetSection("Bridge").Get<BridgeOptions>() ?? new BridgeOptions();
var httpsEnabled = false;
string[]? certificatePathAttempts = null;
int? httpsPort = null;
int? httpPort = null;
var defaultPort = bridgeOptions.Port > 0 ? bridgeOptions.Port : 49001;

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
    var filteredConfig = new ConfigurationBuilder()
        .AddInMemoryCollection(
            context.Configuration.AsEnumerable()
                .Where(kv => kv.Key is null || !kv.Key.StartsWith("Kestrel:Endpoints", StringComparison.OrdinalIgnoreCase)))
        .Build();

    options.Configure(filteredConfig.GetSection("Kestrel"), reloadOnChange: false);

    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    void ListenOnce(IPAddress ip, int port, Action<ListenOptions>? configure = null)
    {
        var key = $"{ip}:{port}";
        if (!used.Add(key)) return;

        if (configure == null) options.Listen(ip, port);
        else options.Listen(ip, port, configure);
    }

    var endpointSection = context.Configuration.GetSection("Kestrel:Endpoints:Https");
    var configuredUrl = endpointSection.GetValue<string>("Url");
    var certificateSection = endpointSection.GetSection("Certificate");
    var certificatePath = certificateSection.GetValue<string>("Path");
    var certificatePassword = certificateSection.GetValue<string>("Password");

    var resolvedPath = ResolveCertificatePath(
        certificatePath,
        builder.Environment.ContentRootPath,
        out certificatePathAttempts);

    var preferredPort = bridgeOptions.Port > 0 ? bridgeOptions.Port : 49001;

    if (!string.IsNullOrWhiteSpace(configuredUrl))
    {
        var uri = new UriBuilder(configuredUrl).Uri;
        if (uri.Port > 0) preferredPort = uri.Port;
    }

    var httpsIp = IPAddress.Loopback;
    var httpsPortFinal = PickFreePort(httpsIp, preferredPort);

    httpsPort = httpsPortFinal;

    ListenOnce(httpsIp, httpsPortFinal, listenOptions =>
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

    if (bridgeOptions.EnableHttp)
    {
        var desiredHttp = bridgeOptions.HttpPort > 0 ? bridgeOptions.HttpPort : httpsPortFinal + 1;
        var httpFinal = PickFreePort(IPAddress.Loopback, desiredHttp);
        httpPort = httpFinal;
        ListenOnce(IPAddress.Loopback, httpFinal);
    }
});

var app = builder.Build();

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
        httpsPort,
        httpPort,
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

app.MapPost("/print/raw", (RawPrintRequest request, IOptions<BridgeOptions> options, ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("PrintRaw");

    if (string.IsNullOrWhiteSpace(request.DataBase64))
    {
        return Results.BadRequest(new { ok = false, sent = false, error = "MISSING_BASE64_DATA" });
    }

    if (!string.Equals(request.ContentType, "raw-escpos", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { ok = false, sent = false, error = "INVALID_CONTENT_TYPE" });
    }

    if (!OperatingSystem.IsWindows())
    {
        logger.LogWarning("Raw printing is only supported on Windows.");
        return Results.StatusCode(StatusCodes.Status501NotImplemented);
    }

    try
    {
        var printerName = ResolvePrinterName(options.Value, request.PrinterName);

        var knownPrinters = PrinterInfoProvider.GetPrinterNames();
        if (knownPrinters.Count > 0 && !knownPrinters.Any(name => string.Equals(name, printerName, StringComparison.OrdinalIgnoreCase)))
        {
            return Results.NotFound(new { ok = false, sent = false, error = "printer_not_found" });
        }

        byte[] jobBytes;
        try
        {
            jobBytes = Convert.FromBase64String(request.DataBase64);
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "Invalid base64 payload supplied to /print/raw.");
            return Results.BadRequest(new { ok = false, sent = false, error = "INVALID_BASE64_DATA" });
        }

        var jobName = string.IsNullOrWhiteSpace(request.JobName) ? "Bonap.PrintBridge Document" : request.JobName;
        var cutCommand = EscPos.AsBytes(EscPos.FullCut);
        var finalJob = new byte[jobBytes.Length + cutCommand.Length];
        Buffer.BlockCopy(jobBytes, 0, finalJob, 0, jobBytes.Length);
        Buffer.BlockCopy(cutCommand, 0, finalJob, jobBytes.Length, cutCommand.Length);

        var sent = RawPrinterHelper.SendBytesToPrinter(printerName, finalJob, jobName);

        return sent
            ? Results.Ok(new { ok = true, sent = true })
            : Results.Json(new { ok = false, sent = false, error = "Failed to send raw data to the printer." }, statusCode: StatusCodes.Status502BadGateway);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to send raw print job.");
        return Results.Json(new { ok = false, sent = false, error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
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
    var cutCommand = EscPos.AsBytes(EscPos.FullCut);
    var finalJob = new byte[jobBytes.Length + cutCommand.Length];
    Buffer.BlockCopy(jobBytes, 0, finalJob, 0, jobBytes.Length);
    Buffer.BlockCopy(cutCommand, 0, finalJob, jobBytes.Length, cutCommand.Length);

    var sent = RawPrinterHelper.SendBytesToPrinter(printerName, finalJob, jobName);
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
catch (Exception ex) when (
    ex is AddressInUseException ||
    (ex is IOException && ex.InnerException is SocketException se && se.SocketErrorCode == SocketError.AddressAlreadyInUse)
)
{
    app.Logger.LogCritical(
        ex,
        "Failed to bind to port {Port}. Another process is already listening. Identify and stop the conflicting process (e.g., use `netstat -ano | find \"{PortNetstat}\"` or `lsof -i :{PortLsof}` to find and kill the PID).",
        httpPort ?? httpsPort ?? defaultPort,
        httpPort ?? httpsPort ?? defaultPort,
        httpPort ?? httpsPort ?? defaultPort);
    return 1;
}

static bool CanBind(IPAddress ip, int port)
{
    try
    {
        var listener = new System.Net.Sockets.TcpListener(ip, port);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();
        listener.Stop();
        return true;
    }
    catch
    {
        return false;
    }
}

static int PickFreePort(IPAddress ip, int preferred, int maxTries = 200)
{
    if (preferred <= 0) preferred = 49001;

    for (var p = preferred; p < preferred + maxTries; p++)
        if (CanBind(ip, p)) return p;

    throw new InvalidOperationException($"No free port found starting at {preferred} (tries={maxTries}).");
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

static string ResolveCertificatePath(string? configuredPath, string contentRoot, out string[]? attempts)
{
    var attemptedPaths = new List<string>();

    if (!string.IsNullOrWhiteSpace(configuredPath))
    {
        var resolvedCertificatePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath, contentRoot);

        attemptedPaths.Add(resolvedCertificatePath);
    }

    var repoRootCandidate = ResolveRepoRootCertPath(contentRoot);
    if (!string.IsNullOrWhiteSpace(repoRootCandidate))
    {
        attemptedPaths.Add(repoRootCandidate);
    }

    var appContextPath = Path.Combine(AppContext.BaseDirectory, "certs", "localhost.pfx");
    attemptedPaths.Add(appContextPath);

    var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    var programDataPath = Path.Combine(programData, "BonapPrintBridge", "certs", "localhost.pfx");
    attemptedPaths.Add(programDataPath);

    var resolvedPath = attemptedPaths.FirstOrDefault(File.Exists);
    attempts = attemptedPaths.ToArray();

    if (string.IsNullOrEmpty(resolvedPath))
    {
        throw new InvalidOperationException(
            $"No HTTPS certificate found. Checked paths: {string.Join(", ", attemptedPaths)}");
    }

    return resolvedPath;
}

static string? ResolveRepoRootCertPath(string contentRoot)
{
    var directoryName = new DirectoryInfo(contentRoot).Name;
    if (!directoryName.Equals("Bonap.PrintBridge", StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    var repoRoot = Path.GetFullPath(Path.Combine(contentRoot, "..", ".."));
    return Path.Combine(repoRoot, "certs", "localhost.pfx");
}

internal record PrintRequest(string? PrinterName, string? JobName, string DataBase64, string ContentType);

internal record DrawerRequest(string? PrinterName, int? Pin, int? T1, int? T2);

internal record ReceiptRequest(string? PrinterName, string Text, bool OpenDrawer, int? Pin);
internal record RawPrintRequest(string? PrinterName, string? JobName, string? ContentType, string? DataBase64);

internal class BridgeOptions
{
    public int Port { get; set; } = 49001;

    public bool EnableHttp { get; set; }

    public int HttpPort { get; set; }

    public string? Token { get; set; }

    public string? DefaultPrinterName { get; set; }

    public int DefaultDrawerPin { get; set; } = 0;
}
