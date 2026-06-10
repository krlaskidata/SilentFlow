using System.Diagnostics;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

public class DownloadService
{
    private readonly IWebHostEnvironment _environment;
    private static readonly Regex ProgressRegex = new(@"\[download\]\s+(\d{1,3}(?:\.\d+)?)%", RegexOptions.Compiled);
    private static readonly SemaphoreSlim _concurrencyLimiter = new(5, 5);
    private static readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)> _ipTracker = new();
    private const int DownloadsPerWindow = 5;
    private static readonly TimeSpan DownloadRateWindow = TimeSpan.FromMinutes(5);
    private const long MinimumFreeDiskSpaceBytes = 2_500_000_000L;

    public DownloadService(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public async Task<DownloadResult> RunDownloadAsync(string url, string format, Func<int, Task> onProgress, string ip = "unknown", CancellationToken cancellationToken = default, string? cookiesBrowser = null, string? cookiesFilePath = null)
    {
        if (!IsValidUrl(url))
            return DownloadResult.Failed("Ungültige oder nicht erlaubte URL.");

        if (IsIpRateLimited(ip))
            return DownloadResult.Failed("Zu viele Anfragen. Bitte warte einige Minuten und versuche es erneut.");

        string downloadPath = Path.Combine(_environment.ContentRootPath, "downloads");
        Directory.CreateDirectory(downloadPath);

        if (!HasSufficientDiskSpace(downloadPath))
            return DownloadResult.Failed("Nicht genügend Speicherplatz verfügbar. Bitte versuche es später erneut.");

        CleanupOldDownloads(downloadPath);

        var filesBefore = Directory.GetFiles(downloadPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        string outputTemplate = Path.Combine(downloadPath, "%(title)s.%(ext)s");

        if (!string.IsNullOrWhiteSpace(cookiesFilePath) && !File.Exists(cookiesFilePath))
            return DownloadResult.Failed($"Cookies-Datei nicht gefunden: {cookiesFilePath}");

        if (!await _concurrencyLimiter.WaitAsync(TimeSpan.FromSeconds(10)))
            return DownloadResult.Failed("Der Server ist gerade ausgelastet. Bitte kurz warten und es erneut versuchen.");

        var outputLines = new List<string>();
        var sync = new object();

        var process = new Process();
        process.StartInfo.FileName = "yt-dlp.exe";

        if (!string.IsNullOrWhiteSpace(cookiesFilePath))
        {
            process.StartInfo.ArgumentList.Add("--cookies");
            process.StartInfo.ArgumentList.Add(cookiesFilePath);
        }
        else if (!string.IsNullOrEmpty(cookiesBrowser) && cookiesBrowser != "none")
        {
            process.StartInfo.ArgumentList.Add("--cookies-from-browser");
            process.StartInfo.ArgumentList.Add(cookiesBrowser);
        }

        process.StartInfo.ArgumentList.Add("--force-ipv4");
        process.StartInfo.ArgumentList.Add("--no-check-certificates");
        process.StartInfo.ArgumentList.Add("--windows-filenames");
        process.StartInfo.ArgumentList.Add("--trim-filenames");
        process.StartInfo.ArgumentList.Add("120");
        process.StartInfo.ArgumentList.Add("--max-filesize");
        process.StartInfo.ArgumentList.Add("2G");

        if (format == "mp3")
        {
            process.StartInfo.ArgumentList.Add("-x");
            process.StartInfo.ArgumentList.Add("--audio-format");
            process.StartInfo.ArgumentList.Add("mp3");
        }
        else
        {
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add("bv*+ba/b");
        }

        process.StartInfo.ArgumentList.Add("--newline");
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add(outputTemplate);
        process.StartInfo.ArgumentList.Add(url);

        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.WorkingDirectory = _environment.ContentRootPath;

        if (!process.Start())
        {
            _concurrencyLimiter.Release();
            return DownloadResult.Failed("yt-dlp konnte nicht gestartet werden.");
        }

        async Task ConsumeStreamAsync(StreamReader reader, CancellationToken token)
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(token);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                lock (sync)
                {
                    outputLines.Add(line);
                }

                var match = ProgressRegex.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                if (double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var percentRaw))
                {
                    var percent = (int)Math.Clamp(Math.Round(percentRaw), 0, 100);
                    await onProgress(percent);
                }
            }
        }

        using var downloadTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(downloadTimeout.Token, cancellationToken);

        try
        {
            await Task.WhenAll(
                ConsumeStreamAsync(process.StandardOutput, linked.Token),
                ConsumeStreamAsync(process.StandardError, linked.Token));

            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return cancellationToken.IsCancellationRequested
                ? DownloadResult.Failed("Download wurde abgebrochen.")
                : DownloadResult.Failed("Download-Timeout: yt-dlp hat nach 10 Minuten nicht geantwortet.");
        }
        finally
        {
            _concurrencyLimiter.Release();
        }

        var filesAfter = Directory.GetFiles(downloadPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newFiles = filesAfter.Except(filesBefore)
            .Select(Path.GetFileName)
            .Where(f => !string.IsNullOrEmpty(f))
            .Cast<string>()
            .ToList();
        var alreadyDownloaded = outputLines.Any(l => l.Contains("already been downloaded", StringComparison.OrdinalIgnoreCase));

        if (process.ExitCode != 0)
        {
            var errorText = string.Join(" ", RelevantLines(outputLines));
            return DownloadResult.Failed(TranslateError(errorText));
        }

        if (alreadyDownloaded)
        {
            await onProgress(100);
            return DownloadResult.Succeeded(downloadPath, [], true);
        }

        if (newFiles.Count == 0)
        {
            var message = string.Join(Environment.NewLine, RelevantLines(outputLines));
            return DownloadResult.Failed($"Kein Download gespeichert.{Environment.NewLine}{message}");
        }

        await onProgress(100);
        return DownloadResult.Succeeded(downloadPath, newFiles, false);
    }

    public async Task<string> UpdateYtDlpAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var process = new Process();
        process.StartInfo.FileName = "yt-dlp.exe";
        process.StartInfo.Arguments = "--update-to nightly";
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.WorkingDirectory = _environment.ContentRootPath;

        if (!process.Start())
            return "yt-dlp konnte nicht gestartet werden.";

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(cts.Token);

            var combined = (stdoutTask.Result + stderrTask.Result).Trim();
            return string.IsNullOrWhiteSpace(combined) ? "Update abgeschlossen." : combined;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return "Update-Timeout: yt-dlp hat nicht rechtzeitig geantwortet.";
        }
    }

    private static void CleanupOldDownloads(string downloadPath)
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        foreach (var file in Directory.GetFiles(downloadPath))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch { }
        }
    }

    private static IEnumerable<string> RelevantLines(List<string> lines) =>
        lines
            .Where(l => l.StartsWith('[') || l.StartsWith("ERROR:") || l.StartsWith("WARNING:") || l.StartsWith("Merging"))
            .TakeLast(8);

    private static string TranslateError(string raw)
    {
        if (raw.Contains("CERTIFICATE_VERIFY_FAILED", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("SSL", StringComparison.OrdinalIgnoreCase))
            return "SSL-Fehler beim Verbinden. Bitte yt-dlp aktualisieren oder später erneut versuchen.";

        if (raw.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("not available", StringComparison.OrdinalIgnoreCase))
            return "Das Video ist nicht verfügbar oder wurde gelöscht.";

        if (raw.Contains("Private video", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("private", StringComparison.OrdinalIgnoreCase))
            return "Privates Video — kein Zugriff möglich.";

        if (raw.Contains("Login required", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("Sign in", StringComparison.OrdinalIgnoreCase))
            return "Anmeldung beim Dienst erforderlich. Cookies werden benötigt.";

        if (raw.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase))
            return "Diese URL wird nicht unterstützt.";

        if (raw.Contains("No video formats found", StringComparison.OrdinalIgnoreCase))
            return "Kein kompatibles Videoformat gefunden.";

        if (raw.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase))
            return "Zugriff verweigert (403). Der Dienst blockiert den Download.";

        if (raw.Contains("HTTP Error 404", StringComparison.OrdinalIgnoreCase))
            return "Inhalt nicht gefunden (404). Bitte URL überprüfen.";

        if (raw.Contains("already been downloaded", StringComparison.OrdinalIgnoreCase))
            return "Bereits heruntergeladen.";

        return "Download fehlgeschlagen. Bitte URL überprüfen und erneut versuchen.";
    }

    private static bool HasSufficientDiskSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path) ?? path;
            return new DriveInfo(root).AvailableFreeSpace >= MinimumFreeDiskSpaceBytes;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsIpRateLimited(string ip)
    {
        if (ip == "unknown") return false;

        var now = DateTime.UtcNow;
        bool limited = false;

        if (_ipTracker.Count > 500)
        {
            foreach (var key in _ipTracker.Keys.ToList())
            {
                if (_ipTracker.TryGetValue(key, out var stale) && now - stale.WindowStart > DownloadRateWindow)
                    _ipTracker.TryRemove(key, out _);
            }
        }

        _ipTracker.AddOrUpdate(
            ip,
            _ => (now, 1),
            (_, existing) =>
            {
                if (now - existing.WindowStart > DownloadRateWindow)
                    return (now, 1);
                limited = existing.Count >= DownloadsPerWindow;
                return limited ? existing : (existing.WindowStart, existing.Count + 1);
            });

        return limited;
    }

    private static bool IsValidUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048)
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var host = uri.Host.ToLowerInvariant();

        if (host == "localhost" || host == "::1" || host == "0.0.0.0")
            return false;

        if (IPAddress.TryParse(host, out var ip))
        {
            var bytes = ip.GetAddressBytes();

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                if (bytes[0] == 127) return false;
                if (bytes[0] == 10) return false;
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return false;
                if (bytes[0] == 192 && bytes[1] == 168) return false;
                if (bytes[0] == 169 && bytes[1] == 254) return false;
            }

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                if (ip.Equals(IPAddress.IPv6Loopback)) return false;
                if (ip.IsIPv6LinkLocal) return false;
            }
        }

        return true;
    }
}

public record DownloadResult(bool Success, string Message, string DownloadDirectory, int FilesCreated, bool AlreadyExisted, IReadOnlyList<string> FileNames)
{
    public static DownloadResult Succeeded(string downloadDirectory, IReadOnlyList<string> fileNames, bool alreadyExisted) =>
        new(true, string.Empty, downloadDirectory, fileNames.Count, alreadyExisted, fileNames);

    public static DownloadResult Failed(string message) =>
        new(false, message, string.Empty, 0, false, []);
}

