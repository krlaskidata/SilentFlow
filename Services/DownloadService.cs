using System.Diagnostics;

public class DownloadService
{
    public string RunDownload(string url, string format)
    {
        try
        {
            string downloadPath = "downloads";
            Directory.CreateDirectory(downloadPath);

            string args;

            if (format == "mp3")
            {
                // ✅ Audio extrahieren + SSL Fix + stabil
                args = $"--no-check-certificate --force-ipv4 -x --audio-format mp3 " +
                       $"-o \"{downloadPath}/%(title)s.%(ext)s\" \"{url}\"";
            }
            else
            {
                // ✅ Bestes Video + Audio + SSL Fix
                args = $"--no-check-certificate --force-ipv4 -f \"bv*+ba/b\" " +
                       $"-o \"{downloadPath}/%(title)s.%(ext)s\" \"{url}\"";
            }

            var process = new Process();
            process.StartInfo.FileName = "yt-dlp.exe";
            process.StartInfo.Arguments = args;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            process.Start();

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            process.WaitForExit();

            // ✅ Echte Fehler anzeigen
            if (!string.IsNullOrWhiteSpace(error))
            {
                return "❌ Fehler:\n" + error;
            }

            // ✅ Erfolgsnachricht
            return "✅ Download fertig! → Schau im Ordner /downloads";
        }
        catch (Exception ex)
        {
            return "❌ Exception: " + ex.Message;
        }
    }
}
