using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using ABCRetailers.Models;
using ABCRetailers.Services;

namespace ABCRetailers.Controllers
{
    [Authorize]
    public class UploadController : Controller
    {
        private readonly IFunctionsApi _api;
        private readonly ILogger<UploadController> _logger;

        public UploadController(IFunctionsApi api, ILogger<UploadController> logger)
        {
            _api = api;
            _logger = logger;
        }

        //  ROUTE BASED ON ROLE
        public async Task<IActionResult> Index()
        {
            // If Admin: Show all uploads
            if (User.IsInRole("Admin"))
            {
                try
                {
                    var uploads = await _api.GetUploadedDocumentsAsync();
                    return View("AdminUploads", uploads);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load uploaded documents");
                    TempData["Error"] = "Failed to load uploaded documents.";
                    return View("AdminUploads", new List<UploadedDocument>());
                }
            }

            // If Customer: Show upload form
            return View("CustomerUpload", new FileUploadModel());
        }

        //  CUSTOMER UPLOAD - POST
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Customer")]
        public async Task<IActionResult> Index(FileUploadModel model)
        {
            if (!ModelState.IsValid)
                return View("CustomerUpload", model);

            try
            {
                if (model.ProofOfPayment is null || model.ProofOfPayment.Length == 0)
                {
                    ModelState.AddModelError("ProofOfPayment", "Please select a file to upload.");
                    return View("CustomerUpload", model);
                }

                // Auto-fill customer name from logged-in user
                var customerName = User.Identity?.Name;

                var fileName = await _api.UploadProofOfPaymentAsync(
                    model.ProofOfPayment,
                    model.OrderId,
                    customerName
                );

                TempData["Success"] = $"File uploaded successfully! File name: {fileName}";
                return View("CustomerUpload", new FileUploadModel());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upload failed");
                ModelState.AddModelError("", $"Error uploading file: {ex.Message}");
                return View("CustomerUpload", model);
            }
        }
    }
}