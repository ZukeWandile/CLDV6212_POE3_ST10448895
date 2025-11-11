using System;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Azure.Storage.Queues;
using Azure.Data.Tables;
using System.Text.Json;
using ABCRetailers.Functions.Entities;

namespace ABCRetailers.Functions.Functions
{
    // Processes messages from order and stock queues
    public class QueueProcessorFunctions
    {
        private readonly string _conn;           // Storage connection string
        private readonly string _ordersTable;    // Orders table name
        private readonly string _productsTable;  // Products table name

        // Load config from environment variables
        public QueueProcessorFunctions()
        {
            _conn = "DefaultEndpointsProtocol=https;AccountName=zuke;AccountKey=MD9PvkJYOqVFwuQFs2B/aBZDw/ySqZIZe7+FAoAOnSv9fxfxKeUA3nQhWV9q09GL4ytbcSXArws0+AStr4qfBg==;EndpointSuffix=core.windows.net";
            _ordersTable = Environment.GetEnvironmentVariable("TABLE_ORDER") ?? "order";
            _productsTable = Environment.GetEnvironmentVariable("TABLE_PRODUCT") ?? "product";
        }

        // Triggered by messages in the order notifications queue
        [Function("OrderNotifications_Processor")]
        public async Task OrderNotificationsProcessor(
            [QueueTrigger("%QUEUE_ORDER_NOTIFICATIONS%", Connection = "STORAGE_CONNECTION")] string message,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("OrderNotifications_Processor");
            log.LogInformation($"^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^");
            log.LogInformation($"Received order notification: {message}");

            try
            {
                using var orderRequest = JsonSerializer.Deserialize<JsonDocument>(message);

                // Check for message type
                if (!orderRequest.RootElement.TryGetProperty("Type", out var typeProperty))
                {
                    log.LogWarning("Message does not contain 'Type' property");
                    return;
                }

                var type = typeProperty.GetString();
                log.LogInformation($"Message Type: {type}");

                // Handle CreateOrder messages
                if (type == "CreateOrder")
                {
                    log.LogInformation("Processing CreateOrder...");

                    var orders = new TableClient(_conn, _ordersTable);
                    var products = new TableClient(_conn, _productsTable);
                    await orders.CreateIfNotExistsAsync();

                    // Build order entity from message
                    var order = new OrderEntity
                    {
                        CustomerId = orderRequest.RootElement.GetProperty("CustomerId").GetString(),
                        ProductId = orderRequest.RootElement.GetProperty("ProductId").GetString(),
                        ProductName = orderRequest.RootElement.GetProperty("ProductName").GetString(),
                        Quantity = orderRequest.RootElement.GetProperty("Quantity").GetInt32(),
                        UnitPrice = orderRequest.RootElement.GetProperty("UnitPrice").GetDouble(),
                        OrderDateUtc = DateTimeOffset.UtcNow,
                        Status = "Submitted"
                    };

                    log.LogInformation($"Creating order with ID: {order.RowKey}");
                    await orders.AddEntityAsync(order);
                    log.LogInformation($"✓ Order {order.RowKey} created in table storage");

                    // Update product stock
                    var productId = order.ProductId;
                    var quantity = order.Quantity;

                    log.LogInformation($"Updating product stock for ProductId: {productId}");
                    var product = (await products.GetEntityAsync<ProductEntity>("Product", productId)).Value;
                    product.StockAvailable -= quantity;
                    await products.UpdateEntityAsync(product, product.ETag, TableUpdateMode.Replace);
                    log.LogInformation($"✓ Product stock updated. New stock: {product.StockAvailable}");

                    // Send processed message to next queue
                    var outputQueueName = Environment.GetEnvironmentVariable("QUEUE_ORDER_PROCESSED");
                    var queueClient = new QueueClient(_conn, outputQueueName);
                    await queueClient.CreateIfNotExistsAsync();

                    var processedMsg = new
                    {
                        Type = "OrderCreated",
                        OrderId = order.RowKey,
                        order.CustomerId,
                        CustomerName = orderRequest.RootElement.GetProperty("CustomerName").GetString(),
                        order.ProductId,
                        order.ProductName,
                        order.Quantity,
                        order.UnitPrice,
                        TotalAmount = order.UnitPrice * order.Quantity,
                        order.OrderDateUtc,
                        order.Status
                    };

                    await queueClient.SendMessageAsync(JsonSerializer.Serialize(processedMsg));
                    log.LogInformation($"✓ Message added to queue: {outputQueueName}");
                }
                // Handle status update messages
                else if (type == "OrderStatusUpdated")
                {
                    log.LogInformation("Processing OrderStatusUpdated...");
                    var outputQueueName = Environment.GetEnvironmentVariable("QUEUE_ORDER_PROCESSED");
                    var queueClient = new QueueClient(_conn, outputQueueName);
                    await queueClient.CreateIfNotExistsAsync();
                    await queueClient.SendMessageAsync(message);
                    log.LogInformation($"✓ Status update forwarded to: {outputQueueName}");
                }
                else
                {
                    log.LogWarning($"Unknown message type: {type}");
                }

                log.LogInformation($"^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^");
            }
            catch (Exception ex)
            {
                log.LogError($" Error processing order notification: {ex.Message}");
                log.LogError($"Stack trace: {ex.StackTrace}");
                throw; // Will move to poison queue after retries
            }
        }

        // Triggered by messages in the stock updates queue
        [Function("StockUpdates_Processor")]
        public async Task StockUpdatesProcessor(
            [QueueTrigger("%QUEUE_STOCK_UPDATES%", Connection = "STORAGE_CONNECTION")] string message,
            FunctionContext ctx)
        {
            var log = ctx.GetLogger("StockUpdates_Processor");
            log.LogInformation($"Received stock update: {message}");

            var outputQueueName = Environment.GetEnvironmentVariable("QUEUE_STOCK_PROCESSED");
            var queueClient = new QueueClient(_conn, outputQueueName);
            await queueClient.CreateIfNotExistsAsync();

            var processedMessage = $"Stock updated successfully: {message}";
            await queueClient.SendMessageAsync(processedMessage);

            log.LogInformation($"Message added to queue: {outputQueueName}");
        }
    }
}