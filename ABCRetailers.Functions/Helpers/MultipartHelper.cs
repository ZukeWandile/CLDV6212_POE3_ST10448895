using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System.Text;

namespace ABCRetailers.Functions.Helpers;

// Helper for parsing multipart/form-data requests
public static class MultipartHelper
{
    // Represents a file part in the form
    public sealed record FilePart(string FieldName, string FileName, Stream Data);

    // Represents the full parsed form: text fields + file uploads
    public sealed record FormData(IReadOnlyDictionary<string, string> Text, IReadOnlyList<FilePart> Files);

    // Parses the incoming multipart/form-data stream
    public static async Task<FormData> ParseAsync(Stream body, string contentType)
    {
        // Extract boundary from Content-Type header
        var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(contentType).Boundary).Value
                       ?? throw new InvalidOperationException("Multipart boundary missing");

        var reader = new MultipartReader(boundary, body);
        var text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<FilePart>();

        // Read each section of the multipart body
        for (var section = await reader.ReadNextSectionAsync(); section != null; section = await reader.ReadNextSectionAsync())
        {
            var cd = ContentDispositionHeaderValue.Parse(section.ContentDisposition);

            // Handle file uploads
            if (cd.IsFileDisposition())
            {
                var fieldName = cd.Name.Value?.Trim('"') ?? "file";
                var fileName = cd.FileName.Value?.Trim('"') ?? "upload.bin";
                var ms = new MemoryStream();
                await section.Body.CopyToAsync(ms);
                ms.Position = 0; // Reset stream position
                files.Add(new FilePart(fieldName, fileName, ms));
            }
            // Handle text fields
            else if (cd.IsFormDisposition())
            {
                var fieldName = cd.Name.Value?.Trim('"') ?? "";
                using var sr = new StreamReader(section.Body, Encoding.UTF8);
                text[fieldName] = await sr.ReadToEndAsync();
            }
        }

        // Return parsed form data
        return new FormData(text, files);
    }
}