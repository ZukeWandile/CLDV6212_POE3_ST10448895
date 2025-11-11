using ABCRetailers.Data;
using ABCRetailers.Models;
using ABCRetailers.Models.ViewModels;
using ABCRetailers.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ABCRetailers.Controllers
{
    public class LoginController : Controller
    {
        private readonly AuthDbContext _db;
        private readonly IFunctionsApi _functionsApi;
        private readonly ILogger<LoginController> _logger;

        public LoginController(AuthDbContext db, IFunctionsApi functionsApi, ILogger<LoginController> logger)
        {
            _db = db;
            _functionsApi = functionsApi;
            _logger = logger;
        }

        // =====================================
        // GET: /Login
        // =====================================
        [HttpGet]
        [AllowAnonymous]
        public IActionResult Index(string? returnUrl = null)
        {
            ViewData["ReturnUrl"] = returnUrl;
            return View(new LoginViewModel());
        }

        // =====================================
        // POST: /Login
        // =====================================
        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Index(LoginViewModel model, string? returnUrl = null)
        {
            if (!ModelState.IsValid)
                return View(model);

            try
            {
                // 1️⃣ Verify user in SQL database
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == model.Username);
                if (user == null)
                {
                    ViewBag.Error = "Invalid username or password.";
                    return View(model);
                }

                //  For now, simple password check (later replace with hashing)
                if (user.PasswordHash != model.Password)
                {
                    ViewBag.Error = "Invalid username or password.";
                    return View(model);
                }

                // 2️⃣ Fetch customer record ONLY for customers
                string customerId = "";

                if (user.Role == "Customer")
                {
                    var customer = await _functionsApi.GetCustomerByUsernameAsync(user.Username);
                    if (customer == null)
                    {
                        _logger.LogWarning("No matching customer found in Azure for username {Username}", user.Username);
                        ViewBag.Error = "No customer record found in the system. Please contact support.";
                        return View(model);
                    }
                    customerId = customer.Id;
                }

                // 3️⃣ Build authentication claims
                var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role)
        };

                // Only add CustomerId for actual customers
                if (!string.IsNullOrEmpty(customerId))
                {
                    claims.Add(new Claim("CustomerId", customerId));
                }

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var principal = new ClaimsPrincipal(identity);

                // 4️⃣ Sign-in with unified cookie scheme
                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    principal,
                    new AuthenticationProperties
                    {
                        IsPersistent = true,
                        ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(60)
                    });

                // 5️⃣ Store session data
                HttpContext.Session.SetString("Username", user.Username);
                HttpContext.Session.SetString("Role", user.Role);
                if (!string.IsNullOrEmpty(customerId))
                {
                    HttpContext.Session.SetString("CustomerId", customerId);
                }

                _logger.LogInformation(" User {Username} logged in as {Role}", user.Username, user.Role);

                // 6️⃣ Redirect appropriately
                if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                    return Redirect(returnUrl);

                return user.Role switch
                {
                    "Admin" => RedirectToAction("AdminDashboard", "Home"),
                    _ => RedirectToAction("CustomerDashboard", "Home")
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected login error for user {Username}", model.Username);
                ViewBag.Error = "Unexpected error occurred during login. Please try again later.";
                return View(model);
            }
        }
        // =====================================
        // GET: /Login/Register
        // =====================================
        [HttpGet]
        [AllowAnonymous]
        public IActionResult Register()
        {
            return View(new RegisterViewModel());
        }

        // =====================================
        // POST: /Login/Register
        // =====================================
        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(RegisterViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            // 1️⃣ Check duplicate username
            var exists = await _db.Users.AnyAsync(u => u.Username == model.Username);
            if (exists)
            {
                ViewBag.Error = "Username already exists.";
                return View(model);
            }

            try
            {
                // 2️⃣ Save local user (SQL)
                var user = new User
                {
                    Username = model.Username,
                    PasswordHash = model.Password, // TODO: Replace with hashed password later
                    Role = model.Role
                };
                _db.Users.Add(user);
                await _db.SaveChangesAsync();

                _logger.LogInformation(" User saved to SQL: {Username} as {Role}", model.Username, model.Role);

                // 3️⃣ Only create Customer record in Azure if role is "Customer"
                if (model.Role == "Customer")
                {
                    var customer = new Customer
                    {
                        Username = model.Username,
                        Name = model.FirstName,
                        Surname = model.LastName,
                        Email = model.Email ?? "",  //  Ensure not null
                        ShippingAddress = model.ShippingAddress ?? ""
                    };

                    _logger.LogInformation(" Creating customer in Azure: {Username}", customer.Username);

                    try
                    {
                        var createdCustomer = await _functionsApi.CreateCustomerAsync(customer);
                        _logger.LogInformation(" Customer created in Azure with ID: {Id}", createdCustomer.Id);
                    }
                    catch (Exception azureEx)
                    {
                        _logger.LogError(azureEx, "❌ Failed to create customer in Azure for {Username}", model.Username);
                        // Continue anyway - user is created in SQL
                    }
                }
                else
                {
                    _logger.LogInformation(" Admin user registered - no customer record created in Azure");
                }

                TempData["Success"] = "Registration successful! Please log in.";
                return RedirectToAction("Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Registration failed for user {Username}", model.Username);
                ViewBag.Error = $"Could not complete registration: {ex.Message}";
                return View(model);
            }
        }

        // =====================================
        // LOGOUT
        // =====================================
        [Authorize]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            HttpContext.Session.Clear();
            return RedirectToAction("Index", "Home");
        }

        // =====================================
        // ACCESS DENIED
        // =====================================
        [HttpGet]
        [AllowAnonymous]
        public IActionResult AccessDenied() => View();
    }
}