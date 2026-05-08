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

    public void OnGet() { }

    public void OnPost()
    {
        if (string.IsNullOrWhiteSpace(Url))
        {
            Result = "❌ Bitte URL eingeben!";
            return;
        }

        Result = _service.RunDownload(Url, Format);
    }
}
