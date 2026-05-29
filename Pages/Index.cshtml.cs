using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public static class DownloadState
{
    public static volatile int Progress = 0;
}

public class IndexModel : PageModel
{
    private readonly DownloadService _service;
    private readonly IWebHostEnvironment _environment;

    public IndexModel(DownloadService service, IWebHostEnvironment environment)
    {
        _service = service;
        _environment = environment;
    }

    [BindProperty]
    public string Url { get; set; }

    [BindProperty]
    public string Format { get; set; }

    public string Result { get; set; }
    public string Error { get; set; }
    public string? ThumbnailFileName { get; set; }
    public string? MediaFileName { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Url))
        {
            Error = "Bitte URL eingeben!";
            return Page();
        }

        if (!Uri.TryCreate(Url, UriKind.Absolute, out var parsedUri) ||
            (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
        {
            Error = "Nur HTTP(S)-URLs sind erlaubt.";
            return Page();
        }

        DownloadState.Progress = 0;

        var downloadResult = await _service.RunDownloadAsync(Url, Format ?? "mp4", p =>
        {
            DownloadState.Progress = p;
        });

        if (!downloadResult.Success)
        {
            Error = downloadResult.Message;
            return Page();
        }

        ThumbnailFileName = downloadResult.ThumbnailPath != null ? Path.GetFileName(downloadResult.ThumbnailPath) : null;
        MediaFileName = downloadResult.MediaPath != null ? Path.GetFileName(downloadResult.MediaPath) : null;

        Result = downloadResult.AlreadyExisted
            ? $"✅ Bereits vorhanden im Ordner: {downloadResult.DownloadDirectory}"
            : $"✅ Download fertig! {downloadResult.FilesCreated} Datei(en) in: {downloadResult.DownloadDirectory}";
        return Page();
    }

    public JsonResult OnGetProgress()
    {
        return new JsonResult(DownloadState.Progress);
    }

    public IActionResult OnGetFile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest();

        var safeName = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(safeName))
            return BadRequest();

        var downloadsDir = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, "downloads"));
        var fullPath = Path.GetFullPath(Path.Combine(downloadsDir, safeName));

        if (!fullPath.StartsWith(downloadsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return BadRequest();

        if (!System.IO.File.Exists(fullPath))
            return NotFound();

        var contentType = Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".mp4" => "video/mp4",
            ".m4a" => "audio/mp4",
            ".webm" => "video/webm",
            ".ogg" or ".oga" => "audio/ogg",
            ".opus" => "audio/opus",
            ".flac" => "audio/flac",
            ".mkv" => "video/x-matroska",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };

        return PhysicalFile(fullPath, contentType);
    }
}