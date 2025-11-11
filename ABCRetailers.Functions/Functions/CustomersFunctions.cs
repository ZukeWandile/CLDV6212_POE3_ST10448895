using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using ABCRetailers.Functions.Entities;
using ABCRetailers.Functions.Helpers;
using ABCRetailers.Functions.Models;

namespace ABCRetailers.Functions.Functions;

// Azure Function class for handling customer operations
public class CustomersFunctions
{
    private readonly string _conn;  // Storage connection string
    private readonly string _table; // Table name for customers

    // Load config values from local.settings.json or environment
    public CustomersFunctions(IConfiguration cfg)
    {
        _conn = "DefaultEndpointsProtocol=https;AccountName=zuke;AccountKey=MD9PvkJYOqVFwuQFs2B/aBZDw/ySqZIZe7+FAoAOnSv9fxfxKeUA3nQhWV9q09GL4ytbcSXArws0+AStr4qfBg==;EndpointSuffix=core.windows.net";
        _table = cfg["TABLE_CUSTOMER"] ?? "Customer";
    }

    // GET /api/customers — list all customers
    [Function("Customers_List")]
    public async Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "customers")] HttpRequestData req)
    {
        var table = new TableClient(_conn, _table);
        await table.CreateIfNotExistsAsync(); // Ensure table exists

        var items = new List<CustomerDto>();
        await foreach (var e in table.QueryAsync<CustomerEntity>(x => x.PartitionKey == "Customer"))
            items.Add(Map.ToDto(e)); // Convert entity to DTO

        return HttpJson.Ok(req, items); // Return 200 OK with list
    }

    // GET /api/customers/{id} — get a single customer by ID
    [Function("Customers_Get")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "customers/{id}")] HttpRequestData req, string id)
    {
        var table = new TableClient(_conn, _table);
        try
        {
            var e = await table.GetEntityAsync<CustomerEntity>("Customer", id);
            return HttpJson.Ok(req, Map.ToDto(e.Value)); // Return 200 OK with customer
        }
        catch
        {
            return HttpJson.NotFound(req, "Customer not found"); // Return 404 if missing
        }
    }

    // DTO used for creating/updating customers
    public record CustomerCreateUpdate(string? Name, string? Surname, string? Username, string? Email, string? ShippingAddress);

    // POST /api/customers — create a new customer
    [Function("Customers_Create")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "customers")] HttpRequestData req)
    {
        var input = await HttpJson.ReadAsync<CustomerCreateUpdate>(req);
        if (input is null || string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Email))
            return HttpJson.Bad(req, "Name and Email are required"); // Validate input

        var table = new TableClient(_conn, _table);
        await table.CreateIfNotExistsAsync();

        var e = new CustomerEntity
        {
            Name = input.Name!,
            Surname = input.Surname ?? "",
            Username = input.Username ?? "",
            Email = input.Email!,
            ShippingAddress = input.ShippingAddress ?? ""
        };
        await table.AddEntityAsync(e); // Save to table

        return HttpJson.Created(req, Map.ToDto(e));
    }

    // PUT /api/customers/{id} — update an existing customer
    [Function("Customers_Update")]
    public async Task<HttpResponseData> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "customers/{id}")] HttpRequestData req, string id)
    {
        var input = await HttpJson.ReadAsync<CustomerCreateUpdate>(req);
        if (input is null) return HttpJson.Bad(req, "Invalid body");

        var table = new TableClient(_conn, _table);
        try
        {
            var resp = await table.GetEntityAsync<CustomerEntity>("Customer", id);
            var e = resp.Value;

            // Update only fields that were provided
            e.Name = input.Name ?? e.Name;
            e.Surname = input.Surname ?? e.Surname;
            e.Username = input.Username ?? e.Username;
            e.Email = input.Email ?? e.Email;
            e.ShippingAddress = input.ShippingAddress ?? e.ShippingAddress;

            await table.UpdateEntityAsync(e, e.ETag, TableUpdateMode.Replace); // Save changes
            return HttpJson.Ok(req, Map.ToDto(e));
        }
        catch
        {
            return HttpJson.NotFound(req, "Customer not found"); // Return 404 if missing
        }
    }

    // DELETE /api/customers/{id} — delete a customer
    [Function("Customers_Delete")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "customers/{id}")] HttpRequestData req, string id)
    {
        var table = new TableClient(_conn, _table);
        await table.DeleteEntityAsync("Customer", id); // Remove from table
        return HttpJson.NoContent(req);
    }
}