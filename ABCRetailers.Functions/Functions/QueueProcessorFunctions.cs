using ABCRetailers.Functions.Entities;
using ABCRetailers.Functions.Helpers;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ABCRetailers.Functions.Functions;

public class QueueProcessorFunctions
{
    private readonly string _conn;
    private readonly string _ordersTable;
    private readonly string _productsTable;
    private readonly string _queueStock;
    private readonly ILogger<QueueProcessorFunctions> _logger;

    public QueueProcessorFunctions(IConfiguration cfg, ILogger<QueueProcessorFunctions> logger)
    {
        _conn = "DefaultEndpointsProtocol=https;AccountName=zuke;AccountKey=MD9PvkJYOqVFwuQFs2B/aBZDw/ySqZIZe7+FAoAOnSv9fxfxKeUA3nQhWV9q09GL4ytbcSXArws0+AStr4qfBg==;EndpointSuffix=core.windows.net";
        _ordersTable = cfg["TABLE_ORDER"] ?? "Order";
        _productsTable = cfg["TABLE_PRODUCT"] ?? "Product";
        _queueStock = cfg["QUEUE_STOCK_UPDATES"] ?? "stock-updates";
        _logger = logger;
    }

    // QUEUE TRIGGER: Process order creation from queue
    [Function("OrderNotifications_Processor")]
    public async Task ProcessOrderNotification(
        [QueueTrigger("order-notifications")] string message)
    {
        _logger.LogInformation($"Processing order notification: {message}");

        try
        {
            var orderRequest = JsonSerializer.Deserialize<OrderRequest>(message);

            if (orderRequest?.Type == "CreateOrder")
            {
                await CreateOrderFromQueue(orderRequest);
            }
            else if (orderRequest?.Type == "OrderStatusUpdated")
            {
                _logger.LogInformation($"Order status updated: {message}");
                // Handle status update notifications if needed
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process order notification");
            throw; // Re-throw to move message to poison queue
        }
    }

    //  QUEUE TRIGGER: Process stock updates from queue
    [Function("StockUpdates_Processor")]
    public async Task ProcessStockUpdate(
        [QueueTrigger("stock-updates")] string message)
    {
        _logger.LogInformation($"Processing stock update: {message}");

        try
        {
            var stockUpdate = JsonSerializer.Deserialize<StockUpdateRequest>(message);

            if (stockUpdate != null)
            {
                await UpdateProductStock(stockUpdate);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process stock update");
            throw;
        }
    }

    // Helper: Create order in Table Storage
    private async Task CreateOrderFromQueue(OrderRequest request)
    {
        var ordersTable = new TableClient(_conn, _ordersTable);
        await ordersTable.CreateIfNotExistsAsync();

        var orderId = Guid.NewGuid().ToString();
        var orderEntity = new OrderEntity
        {
            PartitionKey = "Order",
            RowKey = orderId,
            CustomerId = request.CustomerId,
            ProductId = request.ProductId,
            ProductName = request.ProductName,
            Quantity = request.Quantity,
            UnitPrice = (double)request.UnitPrice,
            OrderDateUtc = DateTimeOffset.UtcNow,
            Status = "Submitted"
        };

        await ordersTable.AddEntityAsync(orderEntity);
        _logger.LogInformation($"Order created: {orderId}");

        // Send stock update request to queue
        var stockQueue = new QueueClient(_conn, _queueStock,
            new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        await stockQueue.CreateIfNotExistsAsync();

        var stockUpdate = new StockUpdateRequest
        {
            ProductId = request.ProductId,
            QuantityChange = -request.Quantity, // Decrease stock
            ProductETag = request.ProductETag
        };

        await stockQueue.SendMessageAsync(JsonSerializer.Serialize(stockUpdate));
        _logger.LogInformation($"Stock update queued for product: {request.ProductId}");
    }

    // Helper: Update product stock
    private async Task UpdateProductStock(StockUpdateRequest request)
    {
        var productsTable = new TableClient(_conn, _productsTable);

        try
        {
            var response = await productsTable.GetEntityAsync<ProductEntity>("Product", request.ProductId);
            var product = response.Value;

            product.StockAvailable += request.QuantityChange;

            if (product.StockAvailable < 0)
            {
                _logger.LogWarning($"Stock for product {request.ProductId} went negative: {product.StockAvailable}");
                product.StockAvailable = 0;
            }

            await productsTable.UpdateEntityAsync(product, product.ETag, TableUpdateMode.Replace);
            _logger.LogInformation($"Stock updated for product {request.ProductId}: {product.StockAvailable}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to update stock for product: {request.ProductId}");
            throw;
        }
    }

    // DTOs for queue messages
    private class OrderRequest
    {
        public string? Type { get; set; }
        public string CustomerId { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string ProductId { get; set; } = "";
        public string ProductName { get; set; } = "";
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public int StockAvailable { get; set; }
        public string? ProductETag { get; set; }
    }

    private class StockUpdateRequest
    {
        public string ProductId { get; set; } = "";
        public int QuantityChange { get; set; }
        public string? ProductETag { get; set; }
    }
}
