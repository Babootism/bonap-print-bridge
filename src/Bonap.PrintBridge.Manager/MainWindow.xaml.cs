using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Bonap.PrintBridge.Manager;

public partial class MainWindow : Window
{
    private const string DefaultBridgeUrl = "https://127.0.0.1:49001";
    private const int LogLines = 200;
    private readonly string _settingsPath;
    private readonly DispatcherTimer _healthTimer;
    private ManagerSettings _settings = new();
    private HttpClient? _httpClient;

    public MainWindow()
    {
        InitializeComponent();
        _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BonapPrintBridge", "manager.json");
        LoadSettings();
        ApplySettingsToUi();
        ConfigureHttpClient();

        _healthTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _healthTimer.Tick += async (_, _) => await RefreshHealth();
        _healthTimer.Start();

        TestTicketTextBox.Text = "Ticket de test";

        _ = RefreshHealth();
        _ = RefreshPrinters();
    }

    private void LoadSettings()
    {
        _settings = new ManagerSettings
        {
            BridgeUrl = DefaultBridgeUrl,
            Token = string.Empty,
            IgnoreCertificateErrors = true
        };

        if (!File.Exists(_settingsPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var loaded = JsonSerializer.Deserialize<ManagerSettings>(json);
            if (loaded != null)
            {
                _settings = loaded;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to load settings: {ex.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveSettings()
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to save settings: {ex.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplySettingsToUi()
    {
        BridgeUrlTextBox.Text = _settings.BridgeUrl;
        TokenBox.Password = _settings.Token ?? string.Empty;
        IgnoreCertErrorsCheckBox.IsChecked = _settings.IgnoreCertificateErrors;
    }

    private void ConfigureHttpClient()
    {
        _httpClient?.Dispose();

        if (!Uri.TryCreate(_settings.BridgeUrl, UriKind.Absolute, out var uri))
        {
            StatusText.Text = "Invalid bridge URL";
            _httpClient = null;
            return;
        }

        var handler = new HttpClientHandler();
        if (_settings.IgnoreCertificateErrors && uri.IsLoopback)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        _httpClient = new HttpClient(handler)
        {
            BaseAddress = uri
        };

        _httpClient.DefaultRequestHeaders.Remove("X-Bridge-Token");
        if (!string.IsNullOrWhiteSpace(_settings.Token))
        {
            _httpClient.DefaultRequestHeaders.Add("X-Bridge-Token", _settings.Token);
        }
    }

    private async Task RefreshHealth()
    {
        if (_httpClient == null)
        {
            SetStatus(false, "Bridge URL is not valid.");
            return;
        }

        try
        {
            using var response = await _httpClient.GetAsync("health");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var health = JsonSerializer.Deserialize<HealthResponse>(content);
            var ok = health?.Ok ?? false;
            var version = string.IsNullOrWhiteSpace(health?.Version) ? string.Empty : $" (v{health.Version})";
            SetStatus(ok, ok ? $"Healthy{version} - {_settings.BridgeUrl}" : $"Unhealthy{version} - {_settings.BridgeUrl}");
        }
        catch (Exception ex)
        {
            SetStatus(false, $"Offline - {_settings.BridgeUrl} ({ex.Message})");
        }
    }

    private async Task RefreshPrinters()
    {
        if (_httpClient == null)
        {
            MessageBox.Show("Bridge URL is not valid.", "Printers", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            using var response = await _httpClient.GetAsync("printers");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var printers = JsonSerializer.Deserialize<List<PrinterInfo>>(content) ?? new List<PrinterInfo>();
            PrintersComboBox.ItemsSource = printers;
            PrintersComboBox.SelectedItem = printers.FirstOrDefault(p => p.IsDefault) ?? printers.FirstOrDefault();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to load printers: {ex.Message}", "Printers", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshLogs()
    {
        if (_httpClient == null)
        {
            MessageBox.Show("Bridge URL is not valid.", "Logs", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            using var response = await _httpClient.GetAsync($"logs/tail?lines={LogLines}");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            LogsTextBox.Text = content;
            LogsTextBox.ScrollToEnd();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to refresh logs: {ex.Message}", "Logs", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task PrintTestTicket()
    {
        if (_httpClient == null)
        {
            MessageBox.Show("Bridge URL is not valid.", "Print", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var text = TestTicketTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show("Please provide content for the test ticket.", "Print", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var payload = new ReceiptPrintRequest
        {
            PrinterName = (PrintersComboBox.SelectedItem as PrinterInfo)?.Name,
            Text = text,
            OpenDrawer = false,
            Pin = 0
        };

        try
        {
            var json = JsonSerializer.Serialize(payload);
            using var response = await _httpClient.PostAsync("receipt/print", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            MessageBox.Show("Test ticket sent.", "Print", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to send ticket: {ex.Message}", "Print", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task OpenDrawer()
    {
        if (_httpClient == null)
        {
            MessageBox.Show("Bridge URL is not valid.", "Drawer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var payload = new DrawerOpenRequest
        {
            PrinterName = (PrintersComboBox.SelectedItem as PrinterInfo)?.Name,
            Pin = 0
        };

        try
        {
            var json = JsonSerializer.Serialize(payload);
            using var response = await _httpClient.PostAsync("drawer/open", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            MessageBox.Show("Drawer command sent.", "Drawer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to open drawer: {ex.Message}", "Drawer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetStatus(bool healthy, string message)
    {
        StatusIndicator.Fill = new SolidColorBrush(healthy ? Colors.Green : Colors.Red);
        StatusText.Text = message;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.BridgeUrl = string.IsNullOrWhiteSpace(BridgeUrlTextBox.Text) ? DefaultBridgeUrl : BridgeUrlTextBox.Text.Trim();
        _settings.Token = TokenBox.Password;
        _settings.IgnoreCertificateErrors = IgnoreCertErrorsCheckBox.IsChecked != false;
        SaveSettings();
        ConfigureHttpClient();
        _ = RefreshHealth();
        _ = RefreshPrinters();
    }

    private void RefreshPrinters_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshPrinters();
    }

    private void RefreshLogs_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshLogs();
    }

    private void PrintTestTicket_Click(object sender, RoutedEventArgs e)
    {
        _ = PrintTestTicket();
    }

    private void OpenDrawer_Click(object sender, RoutedEventArgs e)
    {
        _ = OpenDrawer();
    }
}

public class ManagerSettings
{
    [JsonPropertyName("bridgeUrl")]
    public string BridgeUrl { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("ignoreCertificateErrors")]
    public bool IgnoreCertificateErrors { get; set; } = true;
}

public class PrinterInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }
}

public class HealthResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

public class ReceiptPrintRequest
{
    [JsonPropertyName("printerName")]
    public string? PrinterName { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("openDrawer")]
    public bool OpenDrawer { get; set; }

    [JsonPropertyName("pin")]
    public int Pin { get; set; } = 0;
}

public class DrawerOpenRequest
{
    [JsonPropertyName("printerName")]
    public string? PrinterName { get; set; }

    [JsonPropertyName("pin")]
    public int Pin { get; set; } = 0;
}
