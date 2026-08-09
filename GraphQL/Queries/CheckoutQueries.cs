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

    public List<PaymentMethodDto> GetCollectionPaymentMethods([Service] CheckoutService svc)
    {
        return svc.GetPaymentMethods();
    }

    public async Task<Connection<CountryResult>> GetCountries(
        [Service] DOSDbContext db, int first = 250)
    {
        var countries = await db.Countries.OrderBy(c => c.Name).Take(first).ToListAsync();
        var results = countries.Select(c => new CountryResult
        {
            Id = $"/api/shop/countries/{c.Id}", _Id = c.Id, Code = c.Code, Name = c.Name
        }).ToList();
        return ConnectionHelper.ToConnection(results, results.Count, 0, first);
    }

    public async Task<Connection<CountryStateResult>> GetCountryStates(
        [Service] DOSDbContext db,
        int? countryId = null, string? countryCode = null, int? first = 100)
    {
        var query = db.CountryStates.AsQueryable();
        if (countryId.HasValue) query = query.Where(s => s.CountryId == countryId);
        if (!string.IsNullOrEmpty(countryCode)) query = query.Where(s => s.CountryCode == countryCode);

        var states = await query.Take(first ?? 100).ToListAsync();
        var results = states.Select(s => new CountryStateResult
        {
            Id = $"/api/shop/country-states/{s.Id}", _Id = s.Id, Code = s.Code,
            DefaultName = s.DefaultName, CountryId = s.CountryId, CountryCode = s.CountryCode
        }).ToList();
        return ConnectionHelper.ToConnection(results, results.Count, 0, first ?? 100);
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

public class CountryResult
{
    public string Id { get; set; } = "";
    [GraphQLName("_id")] public int _Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
}

public class CountryStateResult
{
    public string Id { get; set; } = "";
    [GraphQLName("_id")] public int _Id { get; set; }
    public string? Code { get; set; }
    public string? DefaultName { get; set; }
    public int? CountryId { get; set; }
    public string? CountryCode { get; set; }
}
