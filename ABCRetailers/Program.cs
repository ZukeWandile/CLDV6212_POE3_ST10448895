using ABCRetailers.Services;
using ABCRetailers.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

// =========================================
// 1️⃣ MVC and Accessor
// =========================================
builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();

// =========================================
// 2️⃣ EF Core: Azure SQL Database
// =========================================
builder.Services.AddDbContext<AuthDbContext>(options =>
{
    var connStr = builder.Configuration.GetConnectionString("AuthDatabase");
    options.UseSqlServer(connStr);
});

// =========================================
// 3️⃣ Azure Functions API Client
// =========================================
builder.Services.AddHttpClient("Functions", (sp, client) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var baseUrl = cfg["FunctionApi:BaseUrl"]
        ?? throw new InvalidOperationException("FunctionApi:BaseUrl missing");

    //  Keep only one `/api/` prefix (handled here)
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/api/");
    client.Timeout = TimeSpan.FromSeconds(100);
});

builder.Services.AddScoped<IFunctionsApi, FunctionsApiClient>();

// =========================================
// 4️⃣ Cookie Authentication (Unified Scheme)
// =========================================
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login/Index";
        options.AccessDeniedPath = "/Login/AccessDenied";
        options.Cookie.Name = "ABCAuthCookie";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(60);
        options.SlidingExpiration = true;
    });

// =========================================
// 5️⃣ Session Setup
// =========================================
builder.Services.AddSession(options =>
{
    options.Cookie.Name = "ABCSession";
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
});

// =========================================
// 6️⃣ File Upload Limit
// =========================================
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 50 * 1024 * 1024; // 50 MB
});

// =========================================
// 7️⃣ Build App
// =========================================
var app = builder.Build();

// =========================================
// 8️⃣ Culture Settings
// =========================================
var culture = new CultureInfo("en-US");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

// =========================================
// 9️⃣ Middleware Pipeline
// =========================================
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// 🟡 Session before Authentication
app.UseSession();

// ✅ Authentication/Authorization
app.UseAuthentication();
app.UseAuthorization();

// =========================================
// 🔟 Routes
// =========================================
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();