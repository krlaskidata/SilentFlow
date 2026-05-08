using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

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

    // ✅ DAS IST DER FIX
    public string Error { get; set; }

    public void OnGet() { }

    public void OnPost()
    {
        Error = null;
        Result = null;

        if (string.IsNullOrWhiteSpace(Url))
        {
            Error = "❌ Bitte URL eingeben!";
            return;
        }

        try
        {
            Result = _service.RunDownload(Url, Format);
        }
        catch (Exception ex)
        {
            Error = "❌ Fehler: " + ex.Message;
        }
    }
}