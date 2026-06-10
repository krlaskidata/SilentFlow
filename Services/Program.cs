using SilentFlow.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile(Path.Combine("Properties", "appsettings.json"), optional: true, reloadOnChange: true)
    .AddJsonFile(Path.Combine("Properties", $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true);

builder.Services.AddHttpContextAccessor();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization();

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

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self' ws: wss:; frame-ancestors 'none';";
    await next();
});

app.UseRateLimiter();
app.MapStaticAssets();
app.UseAuthentication();
app.UseAuthorization();
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

app.MapPost("/login-action", async (HttpContext context, IConfiguration config, IFormCollection form) =>
{
    var password = form["password"].ToString();
    var configured = config["Auth:Password"] ?? "";

    if (string.IsNullOrEmpty(configured) ||
        !CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(password)),
            SHA256.HashData(Encoding.UTF8.GetBytes(configured))))
    {
        return Results.Redirect("/login?failed=true");
    }

    var claims = new[] { new Claim(ClaimTypes.Name, "owner") };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(new ClaimsPrincipal(identity));
    return Results.Redirect("/");
}).DisableAntiforgery();

app.MapGet("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

app.Run();
