using SilentFlow.Components;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile(Path.Combine("Properties", "appsettings.json"), optional: true, reloadOnChange: true)
    .AddJsonFile(Path.Combine("Properties", $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true);

builder.Services.AddHttpContextAccessor();

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("page", limiterOptions =>
    {
        limiterOptions.PermitLimit = 60;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        limiterOptions.QueueLimit = 0;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddScoped<DownloadService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRateLimiter();
app.MapStaticAssets();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/download/{filename}", (string filename, IWebHostEnvironment env) =>
{
    var safeName = Path.GetFileName(filename);
    if (string.IsNullOrEmpty(safeName) || safeName != filename)
        return Results.BadRequest();

    var filePath = Path.Combine(env.ContentRootPath, "downloads", safeName);
    if (!File.Exists(filePath))
        return Results.NotFound();

    var contentType = Path.GetExtension(safeName).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".m4a" => "audio/mp4",
        ".opus" => "audio/ogg",
        _ => "application/octet-stream"
    };

    return Results.File(filePath, contentType, safeName, enableRangeProcessing: true);
});

app.Run();
