namespace ABCRetailers.Models
{
    public class UploadedDocument
    {
        public string Id { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string? OrderId { get; set; }
        public string? CustomerName { get; set; }
        public DateTimeOffset UploadedAt { get; set; }
        public string BlobUrl { get; set; } = string.Empty;
        public long FileSize { get; set; }
    }
}