using HotChocolate;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.GraphQL.Types;
using DOSApi.Services;

namespace DOSApi.GraphQL.Queries;

[ExtendObjectType("Query")]
public class CheckoutQueries
{
    public async Task<Connection<CheckoutAddressResult>> GetCollectionGetCheckoutAddresses(
        [Service] AuthService auth, [Service] DOSDbContext db)
    {
        var customerId = auth.GetCurrentCustomerId();
        if (customerId == null) return new Connection<CheckoutAddressResult>();

        var addresses = await db.Addresses
            .Where(a => a.CustomerId == customerId && a.AddressType == "customer_address")
            .ToListAsync();

        var results = addresses.Select(a => new CheckoutAddressResult
        {
            Id = a.Id, AddressType = a.AddressType, FirstName = a.FirstName, LastName = a.LastName,
            CompanyName = a.CompanyName, Address = a.AddressLine, City = a.City, State = a.State,
            Country = a.Country, Postcode = a.Postcode, Email = a.Email, Phone = a.Phone,
            DefaultAddress = a.DefaultAddress, UseForShipping = a.UseForShipping
        }).ToList();

        return ConnectionHelper.ToConnection(results, results.Count, 0, results.Count);
    }

    public async Task<List<ShippingRateDto>> GetCollectionShippingRates(
        [Service] CheckoutService svc, [Service] AuthService auth,
        [Service] CartService cartSvc, [Service] IHttpContextAccessor http)
    {
        var cid = auth.GetCurrentCustomerId();
        var session = http.HttpContext?.Request.Headers["X-Session-Token"].FirstOrDefault();
        var cart = await cartSvc.GetCartAsync(cid, session);
        return await svc.GetShippingRatesAsync(cart?.Id);
    }

    public async Task<List<PaymentMethodDto>> GetCollectionPaymentMethods([Service] CheckoutService svc)
    {
        return await svc.GetPaymentMethodsAsync();
    }
}

public class CheckoutAddressResult
{
    public int Id { get; set; }
    public string AddressType { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? CompanyName { get; set; }
    public string Address { get; set; } = "";
    public string City { get; set; } = "";
    public string? State { get; set; }
    public string? Country { get; set; }
    public string? Postcode { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public bool DefaultAddress { get; set; }
    public bool UseForShipping { get; set; }
}
