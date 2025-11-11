using ABCRetailers.Functions.Entities;
using ABCRetailers.Functions.Helpers;
using ABCRetailers.Functions.Models;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace ABCRetailers.Functions.Functions;

// Azure Function class for handling order operations
public class OrdersFunctions
{
    // Storage connection and table/queue names
    private readonly string _conn;
    private readonly string _ordersTable;
    private readonly string _productsTable;
    private readonly string _customersTable;
    private readonly string _queueOrder;
    private readonly string _queueStock;

    // Load config values from local.settings.json or environment
    public OrdersFunctions(IConfiguration cfg)
    {
        _conn = "DefaultEndpointsProtocol=https;AccountName=zuke;AccountKey=MD9PvkJYOqVFwuQFs2B/aBZDw/ySqZIZe7+FAoAOnSv9fxfxKeUA3nQhWV9q09GL4ytbcSXArws0+AStr4qfBg==;EndpointSuffix=core.windows.net";
        _ordersTable = cfg["TABLE_ORDER"] ?? "Order";
        _productsTable = cfg["TABLE_PRODUCT"] ?? "Product";
        _customersTable = cfg["TABLE_CUSTOMER"] ?? "Customer";
        _queueOrder = cfg["QUEUE_ORDER_NOTIFICATIONS"] ?? "order-notifications";
        _queueStock = cfg["QUEUE_STOCK_UPDATES"] ?? "stock-updates";
    }

    // GET /api/orders — list all orders
    [Function("Orders_List")]
    public async Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "orders")] HttpRequestData req)
    {
        var table = new TableClient(_conn, _ordersTable);
        await table.CreateIfNotExistsAsync();

        var items = new List<OrderDto>();
        await foreach (var e in table.QueryAsync<OrderEntity>(x => x.PartitionKey == "Order"))
            items.Add(Map.ToDto(e)); // Convert entity to DTO

        var ordered = items.OrderByDescending(o => o.OrderDateUtc).ToList(); // Sort newest first
        return HttpJson.Ok(req, ordered);
    }

    // GET /api/orders/{id} — get a single order by ID
    [Function("Orders_Get")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "orders/{id}")] HttpRequestData req, string id)
    {
        var table = new TableClient(_conn, _ordersTable);
        try
        {
            var e = await table.GetEntityAsync<OrderEntity>("Order", id);
            return HttpJson.Ok(req, Map.ToDto(e.Value));
        }
        catch
        {
            return HttpJson.NotFound(req, "Order not found");
        }
    }

    // DTO used for creating an order
    public record OrderCreate(string CustomerId, string ProductId, int Quantity);

    // POST /api/orders — create a new order (via queue)
    [Function("Orders_Create")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders")] HttpRequestData req)
    {
        var input = await HttpJson.ReadAsync<OrderCreate>(req);
        if (input is null || string.IsNullOrWhiteSpace(input.CustomerId) ||
            string.IsNullOrWhiteSpace(input.ProductId) || input.Quantity < 1)
            return HttpJson.Bad(req, "CustomerId, ProductId, Quantity >= 1 required");

        var products = new TableClient(_conn, _productsTable);
        var customers = new TableClient(_conn, _customersTable);
        await products.CreateIfNotExistsAsync();
        await customers.CreateIfNotExistsAsync();

        // Validate customer and product references
        ProductEntity product;
        CustomerEntity customer;

        try { product = (await products.GetEntityAsync<ProductEntity>("Product", input.ProductId)).Value; }
        catch { return HttpJson.Bad(req, "Invalid ProductId"); }

        try { customer = (await customers.GetEntityAsync<CustomerEntity>("Customer", input.CustomerId)).Value; }
        catch { return HttpJson.Bad(req, "Invalid CustomerId"); }

        if (product.StockAvailable < input.Quantity)
            return HttpJson.Bad(req, $"Insufficient stock. Available: {product.StockAvailable}");

        // Send order request to queue (async processing)
        var queueOrder = new QueueClient(_conn, _queueOrder,
            new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        await queueOrder.CreateIfNotExistsAsync();

        var orderRequest = new
        {
            Type = "CreateOrder",
            CustomerId = input.CustomerId,
            CustomerName = $"{customer.Name} {customer.Surname}",
            ProductId = input.ProductId,
            ProductName = product.ProductName,
            Quantity = input.Quantity,
            UnitPrice = product.Price,
            StockAvailable = product.StockAvailable,
            ProductETag = product.ETag
        };

        await queueOrder.SendMessageAsync(
            JsonSerializer.Serialize(orderRequest),
            visibilityTimeout: TimeSpan.FromSeconds(5)
        );

        return HttpJson.Ok(req, new
        {
            Message = "Order request submitted for processing",
            CustomerId = input.CustomerId,
            ProductId = input.ProductId,
            Quantity = input.Quantity
        });
    }

    // DTO used for updating order status
    public record OrderStatusUpdate(string Status);

    // PATCH /api/orders/{id}/status — update order status
    [Function("Orders_UpdateStatus")]
    public async Task<HttpResponseData> UpdateStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", "post", "put", Route = "orders/{id}/status")]
        HttpRequestData req, string id)
    {
        var input = await HttpJson.ReadAsync<OrderStatusUpdate>(req);
        if (input is null || string.IsNullOrWhiteSpace(input.Status))
            return HttpJson.Bad(req, "Status is required");

        var orders = new TableClient(_conn, _ordersTable);
        try
        {
            var resp = await orders.GetEntityAsync<OrderEntity>("Order", id);
            var e = resp.Value;
            var previous = e.Status;

            e.Status = input.Status;
            await orders.UpdateEntityAsync(e, e.ETag, TableUpdateMode.Replace);

            // Notify via queue
            var queueOrder = new QueueClient(_conn, _queueOrder,
                new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
            await queueOrder.CreateIfNotExistsAsync();
            var statusMsg = new
            {
                Type = "OrderStatusUpdated",
                OrderId = e.RowKey,
                PreviousStatus = previous,
                NewStatus = e.Status,
                UpdatedDateUtc = DateTimeOffset.UtcNow,
                UpdatedBy = "System"
            };
            await queueOrder.SendMessageAsync(JsonSerializer.Serialize(statusMsg));

            return HttpJson.Ok(req, Map.ToDto(e));
        }
        catch
        {
            return HttpJson.NotFound(req, "Order not found");
        }
    }

    // DELETE /api/orders/{id} — delete an order
    [Function("Orders_Delete")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "orders/{id}")]
        HttpRequestData req, string id)
    {
        var table = new TableClient(_conn, _ordersTable);
        await table.DeleteEntityAsync("Order", id);
        return HttpJson.NoContent(req);
    }
}