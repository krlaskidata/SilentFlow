using System.Diagnostics;
using System;
using System.Text.RegularExpressions;

public class DownloadService
{
    private readonly IWebHostEnvironment _environment;
    private static readonly Regex ProgressRegex = new(@"\[download\]\s+(\d{1,3}(?:\.\d+)?)%", RegexOptions.Compiled);

    public DownloadService(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public static bool IsSpotifyUrl(string url) =>
        url.Contains("spotify.com", StringComparison.OrdinalIgnoreCase);

    public async Task<DownloadResult> RunDownloadAsync(string url, string format, Action<int> onProgress)
    {
        string downloadPath = Path.Combine(_environment.ContentRootPath, "downloads");
        Directory.CreateDirectory(downloadPath);

        var filesBefore = Directory.GetFiles(downloadPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        string outputTemplate = Path.Combine(downloadPath, "%(title)s.%(ext)s");

        if (IsSpotifyUrl(url)) format = "mp3";

        var outputLines = new List<string>();
        var sync = new object();

        var process = new Process();
        process.StartInfo.FileName = "yt-dlp.exe";
        if (format == "mp3")
        {
            foreach (var arg in new[] { "--no-check-certificate", "--force-ipv4", "-x",
                                        "--audio-format", "mp3", "--embed-thumbnail",
                                        "--add-metadata", "--write-thumbnail", "--newline",
                                        "-o", outputTemplate, url })
                process.StartInfo.ArgumentList.Add(arg);
        }
        else
        {
            foreach (var arg in new[] { "--no-check-certificate", "--force-ipv4",
                                        "-f", "bv*+ba/b", "--newline",
                                        "-o", outputTemplate, url })
                process.StartInfo.ArgumentList.Add(arg);
        }

        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.WorkingDirectory = _environment.ContentRootPath;

        if (!process.Start())
        {
            return DownloadResult.Failed("yt-dlp konnte nicht gestartet werden.");
        }

        async Task ConsumeStreamAsync(StreamReader reader)
        {
            while (true)
            {
                var line = await reader.ReadLineAsync();
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
                    onProgress(percent);
                }
            }
        }

        await Task.WhenAll(
            ConsumeStreamAsync(process.StandardOutput),
            ConsumeStreamAsync(process.StandardError));

        await process.WaitForExitAsync();

        var filesAfter = Directory.GetFiles(downloadPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newFiles = filesAfter.Except(filesBefore).ToList();
        var alreadyDownloaded = outputLines.Any(l => l.Contains("already been downloaded", StringComparison.OrdinalIgnoreCase));

        if (process.ExitCode != 0)
        {
            var message = string.Join(Environment.NewLine, outputLines.TakeLast(8))
                .Replace(downloadPath, "[downloads]", StringComparison.OrdinalIgnoreCase);
            return DownloadResult.Failed($"yt-dlp Fehler (ExitCode {process.ExitCode}).{Environment.NewLine}{message}");
        }

        if (alreadyDownloaded)
        {
            onProgress(100);
            return DownloadResult.Succeeded(downloadPath, 0, true, null, null);
        }

        if (newFiles.Count == 0)
        {
            var message = string.Join(Environment.NewLine, outputLines.TakeLast(8))
                .Replace(downloadPath, "[downloads]", StringComparison.OrdinalIgnoreCase);
            return DownloadResult.Failed($"Kein Download gespeichert.{Environment.NewLine}{message}");
        }

        var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };
        var mediaExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".mp4", ".webm", ".ogg", ".opus", ".flac", ".mkv" };
        string? thumbnailPath = newFiles.FirstOrDefault(f => imageExts.Contains(Path.GetExtension(f)));
        string? mediaPath = newFiles.FirstOrDefault(f => mediaExts.Contains(Path.GetExtension(f)));

        onProgress(100);
        return DownloadResult.Succeeded(downloadPath, newFiles.Count, false, thumbnailPath, mediaPath);
    }
}

public record DownloadResult(bool Success, string Message, string DownloadDirectory, int FilesCreated, bool AlreadyExisted, string? ThumbnailPath, string? MediaPath)
{
    public static DownloadResult Succeeded(string downloadDirectory, int filesCreated, bool alreadyExisted, string? thumbnailPath, string? mediaPath) =>
        new(true, string.Empty, downloadDirectory, filesCreated, alreadyExisted, thumbnailPath, mediaPath);

    public static DownloadResult Failed(string message) =>
        new(false, message, string.Empty, 0, false, null, null);
}

