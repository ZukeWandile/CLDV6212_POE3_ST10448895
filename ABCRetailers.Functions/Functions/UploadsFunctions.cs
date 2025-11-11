using ABCRetailers.Functions.Helpers;
using Azure.Storage.Blobs;
using Azure.Storage.Files.Shares;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;

namespace ABCRetailers.Functions.Functions;

public class UploadsFunctions
{
    private readonly string _conn;
    private readonly string _proofs;
    private readonly string _share;
    private readonly string _shareDir;
    private readonly string _table;

    public UploadsFunctions(IConfiguration cfg)
    {
        _conn = "DefaultEndpointsProtocol=https;AccountName=zuke;AccountKey=MD9PvkJYOqVFwuQFs2B/aBZDw/ySqZIZe7+FAoAOnSv9fxfxKeUA3nQhWV9q09GL4ytbcSXArws0+AStr4qfBg==;EndpointSuffix=core.windows.net";
        _proofs = cfg["BLOB_PAYMENT_PROOFS"] ?? "payment-proofs";
        _share = cfg["FILESHARE_CONTRACTS"] ?? "contracts";
        _shareDir = cfg["FILESHARE_DIR_PAYMENTS"] ?? "payments";
        _table = "Uploads"; // Table for upload metadata
    }

    //  GET /api/uploads — list all uploaded documents
    [Function("Uploads_List")]
    public async Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "uploads")] HttpRequestData req)
    {
        var table = new TableClient(_conn, _table);
        await table.CreateIfNotExistsAsync();

        var items = new List<UploadedDocumentDto>();

        await foreach (var entity in table.QueryAsync<TableEntity>(e => e.PartitionKey == "Upload"))
        {
            items.Add(new UploadedDocumentDto(
                Id: entity.RowKey,
                FileName: entity.GetString("FileName") ?? "",
                OrderId: entity.GetString("OrderId"),
                CustomerName: entity.GetString("CustomerName"),
                UploadedAt: entity.GetDateTimeOffset("UploadedAt") ?? DateTimeOffset.UtcNow,
                BlobUrl: entity.GetString("BlobUrl") ?? "",
                FileSize: entity.GetInt64("FileSize") ?? 0
            ));
        }

        return HttpJson.Ok(req, items.OrderByDescending(x => x.UploadedAt).ToList());
    }

    //  POST /api/uploads/proof-of-payment — handles file upload
    [Function("Uploads_ProofOfPayment")]
    public async Task<HttpResponseData> Proof(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "uploads/proof-of-payment")] HttpRequestData req)
    {
        var contentType = req.Headers.TryGetValues("Content-Type", out var ct) ? ct.First() : "";
        if (!contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return HttpJson.Bad(req, "Expected multipart/form-data");

        var form = await MultipartHelper.ParseAsync(req.Body, contentType);
        var file = form.Files.FirstOrDefault(f => f.FieldName == "ProofOfPayment");
        if (file is null || file.Data.Length == 0)
            return HttpJson.Bad(req, "ProofOfPayment file is required");

        var orderId = form.Text.GetValueOrDefault("OrderId");
        var customerName = form.Text.GetValueOrDefault("CustomerName");

        // Upload to Blob Storage
        var container = new BlobContainerClient(_conn, _proofs);
        await container.CreateIfNotExistsAsync();
        var blobName = $"{Guid.NewGuid():N}-{file.FileName}";
        var blob = container.GetBlobClient(blobName);

        //  Measure BEFORE disposing
        var fileSize = file.Data.Length;

        using (var uploadStream = file.Data)
        {
            await blob.UploadAsync(uploadStream);
        }

        //  Save metadata to Table Storage
        var table = new TableClient(_conn, _table);
        await table.CreateIfNotExistsAsync();

        var uploadEntity = new TableEntity("Upload", Guid.NewGuid().ToString())
        {
            ["FileName"] = blobName,
            ["OrderId"] = orderId ?? "",
            ["CustomerName"] = customerName ?? "",
            ["UploadedAt"] = DateTimeOffset.UtcNow,
            ["BlobUrl"] = blob.Uri.ToString(),
            ["FileSize"] = fileSize
        };

        await table.AddEntityAsync(uploadEntity);

        // Write metadata to Azure File Share
        var share = new ShareClient(_conn, _share);
        await share.CreateIfNotExistsAsync();
        var root = share.GetRootDirectoryClient();
        var dir = root.GetSubdirectoryClient(_shareDir);
        await dir.CreateIfNotExistsAsync();
        var fileClient = dir.GetFileClient(blobName + ".txt");

        var meta = $"UploadedAtUtc: {DateTimeOffset.UtcNow:O}\nOrderId: {orderId}\nCustomerName: {customerName}\nBlobUrl: {blob.Uri}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(meta);

        using var ms = new MemoryStream(bytes);
        await fileClient.CreateAsync(ms.Length);
        await fileClient.UploadAsync(ms);

        return HttpJson.Ok(req, new { fileName = blobName, blobUrl = blob.Uri.ToString() });
    }

    public record UploadedDocumentDto(
        string Id,
        string FileName,
        string? OrderId,
        string? CustomerName,
        DateTimeOffset UploadedAt,
        string BlobUrl,
        long FileSize);
}
