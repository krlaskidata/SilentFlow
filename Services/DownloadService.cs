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

    public async Task<DownloadResult> RunDownloadAsync(string url, string format, Func<int, Task> onProgress)
    {
        string downloadPath = Path.Combine(_environment.ContentRootPath, "downloads");
        Directory.CreateDirectory(downloadPath);

        var filesBefore = Directory.GetFiles(downloadPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        string outputTemplate = Path.Combine(downloadPath, "%(title)s.%(ext)s");

        string args = format == "mp3"
            ? $"--no-check-certificate --force-ipv4 -x --audio-format mp3 --newline -o \"{outputTemplate}\" \"{url}\""
            : $"--no-check-certificate --force-ipv4 -f \"bv*+ba/b\" --newline -o \"{outputTemplate}\" \"{url}\"";

        var outputLines = new List<string>();
        var sync = new object();

        var process = new Process();
        process.StartInfo.FileName = "yt-dlp.exe";
        process.StartInfo.Arguments = args;
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
                    await onProgress(percent);
                }
            }
        }

        await Task.WhenAll(
            ConsumeStreamAsync(process.StandardOutput),
            ConsumeStreamAsync(process.StandardError));

        await process.WaitForExitAsync();

        var filesAfter = Directory.GetFiles(downloadPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newFilesCount = filesAfter.Except(filesBefore).Count();
        var alreadyDownloaded = outputLines.Any(l => l.Contains("already been downloaded", StringComparison.OrdinalIgnoreCase));

        if (process.ExitCode != 0)
        {
            var message = string.Join(Environment.NewLine, outputLines.TakeLast(8));
            return DownloadResult.Failed($"yt-dlp Fehler (ExitCode {process.ExitCode}).{Environment.NewLine}{message}");
        }

        if (alreadyDownloaded)
        {
            await onProgress(100);
            return DownloadResult.Succeeded(downloadPath, 0, true);
        }

        if (newFilesCount == 0)
        {
            var message = string.Join(Environment.NewLine, outputLines.TakeLast(8));
            return DownloadResult.Failed($"Kein Download gespeichert.{Environment.NewLine}{message}");
        }

        await onProgress(100);
        return DownloadResult.Succeeded(downloadPath, newFilesCount, false);
    }
}

public record DownloadResult(bool Success, string Message, string DownloadDirectory, int FilesCreated, bool AlreadyExisted)
{
    public static DownloadResult Succeeded(string downloadDirectory, int filesCreated, bool alreadyExisted) =>
        new(true, string.Empty, downloadDirectory, filesCreated, alreadyExisted);

    public static DownloadResult Failed(string message) =>
        new(false, message, string.Empty, 0, false);
}

