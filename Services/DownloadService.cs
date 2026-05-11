using System.Diagnostics;
using System;

public class DownloadService
{
    public async Task RunDownloadAsync(string url, string format, Action<int> onProgress)
    {
        string downloadPath = "downloads";
        Directory.CreateDirectory(downloadPath);

        string args = format == "mp3"
            ? $"--no-check-certificate --force-ipv4 -x --audio-format mp3 --newline -o \"{downloadPath}/%(title)s.%(ext)s\" \"{url}\""
            : $"--no-check-certificate --force-ipv4 -f \"bv*+ba/b\" --newline -o \"{downloadPath}/%(title)s.%(ext)s\" \"{url}\"";

        var process = new Process();
        process.StartInfo.FileName = "yt-dlp.exe";
        process.StartInfo.Arguments = args;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        process.Start();

        while (!process.StandardOutput.EndOfStream)
        {
            var line = await process.StandardOutput.ReadLineAsync();

            if (line != null && line.Contains("%"))
            {
                // Beispiel: [download]  47.2% of ...
                var percentText = line.Split('%')[0];
                var numbers = new string(percentText.Where(char.IsDigit).ToArray());

                if (int.TryParse(numbers, out int percent))
                {
                    onProgress(percent);
                }
            }
        }

        await process.WaitForExitAsync();
    }
}
