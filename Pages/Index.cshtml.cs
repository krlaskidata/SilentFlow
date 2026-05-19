using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public static class DownloadState
{
    public static int Progress = 0;
}

public class IndexModel : PageModel
{
    private readonly DownloadService _service;

    public IndexModel(DownloadService service)
    {
        _service = service;
    }

    [BindProperty]
    public string Url { get; set; }

    [BindProperty]
    public string Format { get; set; }

    public string Result { get; set; }
    public string Error { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Url))
        {
            Error = "Bitte URL eingeben!";
            return Page();
        }

        DownloadState.Progress = 0;

        var downloadResult = await _service.RunDownloadAsync(Url, Format, p =>
        {
            DownloadState.Progress = p;
        });

        if (!downloadResult.Success)
        {
            Error = downloadResult.Message;
            return Page();
        }

        Result = $"✅ Download fertig! {downloadResult.FilesCreated} Datei(en) in: {downloadResult.DownloadDirectory}";
        return Page();
    }

    public JsonResult OnGetProgress()
    {
        return new JsonResult(DownloadState.Progress);
    }
}