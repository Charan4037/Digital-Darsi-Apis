using Microsoft.EntityFrameworkCore;
using BagistoApi.Data;
using BagistoApi.Models.Customer;
using BagistoApi.Models.Sales;

namespace BagistoApi.Services;

public class AccountService
{
    private readonly BagistoDbContext _db;
    private readonly string _locale;

    public AccountService(BagistoDbContext db, IConfiguration config)
    {
        _db = db;
        _locale = config["App:Locale"] ?? "en";
    }

    public async Task<Customer?> GetProfileAsync(int customerId)
    {
        return await _db.Customers.FindAsync(customerId);
    }

    public async Task<bool> UpdateProfileAsync(int customerId, string? firstName, string? lastName,
        string? phone, string? gender, string? dateOfBirth, bool? newsletter, string? email = null)
    {
        var customer = await _db.Customers.FindAsync(customerId);
        if (customer == null) return false;

        if (firstName != null) customer.FirstName = firstName;
        if (lastName != null) customer.LastName = lastName;
        if (phone != null) customer.Phone = phone;
        if (gender != null) customer.Gender = gender;
        if (dateOfBirth != null && DateTime.TryParse(dateOfBirth, out var dob)) customer.DateOfBirth = dob;
        if (newsletter.HasValue) customer.SubscribedToNewsLetter = newsletter.Value;
        if (email != null) customer.Email = email;
        customer.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<(bool success, string message)> ChangeEmailAsync(int customerId, string newEmail, string currentPassword)
    {
        var customer = await _db.Customers.FindAsync(customerId);
        if (customer == null) return (false, "Customer not found.");

        var pwd = (customer.Password ?? "").Replace("$2y$", "$2a$");
        if (!BCrypt.Net.BCrypt.Verify(currentPassword, pwd))
            return (false, "Current password is incorrect.");

        if (await _db.Customers.AnyAsync(c => c.Email == newEmail && c.Id != customerId))
            return (false, "Email already in use.");

        customer.Email = newEmail;
        customer.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return (true, "Email updated successfully.");
    }

    public async Task<(bool success, string message)> ChangePasswordAsync(int customerId, string currentPassword, string newPassword)
    {
        var customer = await _db.Customers.FindAsync(customerId);
        if (customer == null) return (false, "Customer not found.");

        var pwd = (customer.Password ?? "").Replace("$2y$", "$2a$");
        if (!BCrypt.Net.BCrypt.Verify(currentPassword, pwd))
            return (false, "Current password is incorrect.");

        customer.Password = BCrypt.Net.BCrypt.HashPassword(newPassword);
        customer.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return (true, "Password updated successfully.");
    }

    public async Task<(bool success, string message)> DeleteAccountAsync(int customerId, string password)
    {
        var customer = await _db.Customers.FindAsync(customerId);
        if (customer == null) return (false, "Customer not found.");

        var pwd = (customer.Password ?? "").Replace("$2y$", "$2a$");
        if (!BCrypt.Net.BCrypt.Verify(password, pwd))
            return (false, "Password is incorrect.");

        _db.Customers.Remove(customer);
        await _db.SaveChangesAsync();
        return (true, "Account deleted successfully.");
    }

    public IQueryable<Address> GetAddresses(int customerId)
    {
        return _db.Addresses
            .Where(a => a.CustomerId == customerId && a.AddressType == "customer_address")
            .OrderByDescending(a => a.DefaultAddress)
            .ThenByDescending(a => a.Id);
    }

    public async Task<Address> AddOrUpdateAddressAsync(int customerId, int? addressId,
        string firstName, string lastName, string address, string city,
        string state, string country, string postcode, string phone,
        string? email, bool useForShipping, bool defaultAddress)
    {
        Address addr;
        if (addressId.HasValue && addressId > 0)
        {
            addr = await _db.Addresses.FirstAsync(a => a.Id == addressId.Value && a.CustomerId == customerId);
        }
        else
        {
            addr = new Address { CustomerId = customerId, AddressType = "customer_address", CreatedAt = DateTime.UtcNow };
            _db.Addresses.Add(addr);
        }
  
        addr.FirstName = firstName;
        addr.LastName = lastName;
        addr.AddressLine = address;
        addr.City = city;
        addr.State = state;
        addr.Country = country;
        addr.Postcode = postcode;
        addr.Phone = phone;
        addr.Email = email;
        addr.UseForShipping = useForShipping;
        addr.DefaultAddress = defaultAddress;
        addr.UpdatedAt = DateTime.UtcNow;

        if (defaultAddress)
        {
            await _db.Addresses
                .Where(a => a.CustomerId == customerId && a.Id != addr.Id && a.AddressType == "customer_address")
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.DefaultAddress, false));
        }

        await _db.SaveChangesAsync();
        return addr;
    }

    public async Task<(bool success, string message)> DeleteAddressAsync(int customerId, int addressId)
    {
        var addr = await _db.Addresses.FirstOrDefaultAsync(a => a.Id == addressId && a.CustomerId == customerId);
        if (addr == null) return (false, "Address not found.");

        _db.Addresses.Remove(addr);
        await _db.SaveChangesAsync();
        return (true, "Address deleted successfully.");
    }

    public async Task<(bool success, string message, Address? address)> SetDefaultAddressAsync(int customerId, int addressId)
    {
        var addr = await _db.Addresses.FirstOrDefaultAsync(a =>
            a.Id == addressId
            && a.CustomerId == customerId
            && a.AddressType == "customer_address");
        if (addr == null) return (false, "Address not found.", null);

        await _db.Addresses
            .Where(a => a.CustomerId == customerId
                && a.AddressType == "customer_address"
                && a.Id != addressId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.DefaultAddress, false));

        addr.DefaultAddress = true;
        addr.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return (true, "Default address updated.", addr);
    }

    public IQueryable<Order> GetOrders(int customerId, string? status)
    {
        var q = _db.Orders.Where(o => o.CustomerId == customerId).AsQueryable();
        if (!string.IsNullOrEmpty(status))
            q = q.Where(o => o.Status == status);
        return q.OrderByDescending(o => o.Id);
    }

    public async Task<Order?> GetOrderDetailAsync(int customerId, int orderId)
    {
        return await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.Payment)
            .Include(o => o.Invoices).ThenInclude(i => i.Items)
            .Include(o => o.Shipments).ThenInclude(s => s.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CustomerId == customerId);
    }

    public async Task<List<Address>> GetOrderAddressesAsync(int orderId)
    {
        return await _db.Addresses.Where(a => a.OrderId == orderId).ToListAsync();
    }

    public IQueryable<Invoice> GetInvoices(int customerId, int? orderId, string? state)
    {
        var q = _db.Invoices
            .Include(i => i.Order)
            .Where(i => i.Order != null && i.Order.CustomerId == customerId);

        if (orderId.HasValue) q = q.Where(i => i.OrderId == orderId);
        if (!string.IsNullOrEmpty(state)) q = q.Where(i => i.State == state);
        return q.OrderByDescending(i => i.Id);
    }

    public async Task<Invoice?> GetInvoiceDetailAsync(int customerId, int invoiceId)
    {
        return await _db.Invoices
            .Include(i => i.Items)
            .Include(i => i.Order)
            .FirstOrDefaultAsync(i => i.Id == invoiceId && i.Order != null && i.Order.CustomerId == customerId);
    }

    public IQueryable<Shipment> GetShipments(int orderId, int customerId)
    {
        return _db.Shipments
            .Include(s => s.Items)
            .Where(s => s.OrderId == orderId && s.Order != null && s.Order.CustomerId == customerId);
    }

    public async Task<Shipment?> GetShipmentDetailAsync(int customerId, int shipmentId)
    {
        return await _db.Shipments
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.Id == shipmentId && s.Order != null && s.Order.CustomerId == customerId);
    }

    public async Task<(bool success, string message, int? orderId, int itemsAddedCount)> ReorderAsync(int customerId, int orderId)
    {
        var order = await _db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId && o.CustomerId == customerId);
        if (order == null) return (false, "Order not found.", null, 0);

        // Find or create cart
        var cart = await _db.Carts
            .Include(c => c.Items)
            .Where(c => c.CustomerId == customerId && c.IsActive == true)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync();

        if (cart == null)
        {
            cart = new Models.Cart.Cart
            {
                CustomerId = customerId,
                IsGuest = false,
                ChannelId = 1,
                IsActive = true,
                BaseCurrencyCode = "INR",
                ChannelCurrencyCode = "INR",
                CartCurrencyCode = "INR",
                GlobalCurrencyCode = "INR",
                ExchangeRate = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.Carts.Add(cart);
            await _db.SaveChangesAsync();
        }

        int count = 0;
        foreach (var item in order.Items.Where(i => i.ProductId.HasValue))
        {
            var existing = cart.Items.FirstOrDefault(ci => ci.ProductId == item.ProductId);
            if (existing != null)
            {
                existing.Quantity += (int)(item.QtyOrdered ?? 1);
                existing.Total = existing.Price * existing.Quantity;
                existing.BaseTotal = existing.BasePrice * existing.Quantity;
            }
            else
            {
                cart.Items.Add(new Models.Cart.CartItem
                {
                    CartId = cart.Id,
                    ProductId = item.ProductId!.Value,
                    Sku = item.Sku,
                    Name = item.Name,
                    Type = item.Type,
                    Quantity = (int)(item.QtyOrdered ?? 1),
                    Price = item.Price ?? 0,
                    BasePrice = item.BasePrice ?? 0,
                    Total = (item.Price ?? 0) * (item.QtyOrdered ?? 1),
                    BaseTotal = (item.BasePrice ?? 0) * (item.QtyOrdered ?? 1),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            count++;
        }

        await _db.SaveChangesAsync();
        return (true, "Order items added to cart.", order.Id, count);
    }

    // ─── New Shop API methods ─────────────────────────────────────────

    public async Task<(bool success, string message)> CancelOrderAsync(int customerId, int orderId)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CustomerId == customerId);
        if (order == null) return (false, "Order not found.");
        if (order.Status != "pending") return (false, "Only pending orders can be canceled.");

        // Restore stock for every top-level order item
        foreach (var item in order.Items.Where(i => i.ParentId == null && i.ProductId.HasValue))
        {
            var qty = (int)(item.QtyOrdered ?? 1);
            await _db.ProductInventories
                .Where(inv => inv.ProductId == item.ProductId!.Value)
                .ExecuteUpdateAsync(s => s.SetProperty(inv => inv.Qty, inv => inv.Qty + qty));
        }

        order.Status    = "canceled";
        order.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return (true, "Order canceled successfully.");
    }

    public async Task<(bool success, string message, Refund? refund)> RequestRefundAsync(
        int customerId, int orderId, string reason, List<RefundItemRequest>? items = null)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CustomerId == customerId);
        if (order == null) return (false, "Order not found.", null);

        var refundableStatuses = new[] { "completed", "complete", "delivered", "processing" };
        if (!refundableStatuses.Contains(order.Status, StringComparer.OrdinalIgnoreCase))
            return (false, "Refund can only be requested for completed or processing orders.", null);

        var existing = await _db.Refunds.AnyAsync(r => r.OrderId == orderId);
        if (existing) return (false, "A refund request already exists for this order.", null);

        var now         = DateTime.UtcNow;
        var topItems    = order.Items.Where(i => i.ParentId == null).ToList();
        var toRefund    = items != null && items.Count > 0
            ? topItems.Where(i => items.Any(r => r.OrderItemId == i.Id)).ToList()
            : topItems;

        decimal subTotal = toRefund.Sum(i =>
        {
            var requestedQty = items?.FirstOrDefault(r => r.OrderItemId == i.Id)?.Qty
                               ?? (int)(i.QtyOrdered ?? 1);
            return (i.Price ?? 0) * requestedQty;
        });

        var refund = new Refund
        {
            OrderId           = orderId,
            State             = "pending",
            SubTotal          = subTotal,
            BaseSubTotal      = subTotal,
            GrandTotal        = subTotal,
            BaseGrandTotal    = subTotal,
            BaseCurrencyCode  = order.BaseCurrencyCode,
            OrderCurrencyCode = order.OrderCurrencyCode,
            EmailSent         = false,
            CreatedAt         = now,
            UpdatedAt         = now,
        };
        _db.Refunds.Add(refund);
        await _db.SaveChangesAsync();

        var isFirst = true;
        foreach (var orderItem in toRefund)
        {
            var qty = items?.FirstOrDefault(r => r.OrderItemId == orderItem.Id)?.Qty
                      ?? (int)(orderItem.QtyOrdered ?? 1);

            _db.RefundItems.Add(new RefundItem
            {
                RefundId    = refund.Id,
                OrderItemId = orderItem.Id,
                ProductId   = orderItem.ProductId,
                ProductType = orderItem.Type,
                Name        = orderItem.Name,
                Sku         = orderItem.Sku,
                Qty         = qty,
                Price       = orderItem.Price,
                BasePrice   = orderItem.BasePrice,
                Total       = (orderItem.Price ?? 0) * qty,
                BaseTotal   = (orderItem.BasePrice ?? 0) * qty,
                // Store the refund reason in Additional of the first item
                Additional  = isFirst ? System.Text.Json.JsonSerializer.Serialize(new { reason }) : null,
                CreatedAt   = now,
                UpdatedAt   = now,
            });
            isFirst = false;
        }
        await _db.SaveChangesAsync();
        return (true, "Refund request submitted successfully.", refund);
    }

    public async Task<Refund?> GetOrderRefundAsync(int customerId, int orderId)
    {
        // Verify the order belongs to this customer
        var belongs = await _db.Orders.AnyAsync(o => o.Id == orderId && o.CustomerId == customerId);
        if (!belongs) return null;

        return await _db.Refunds
            .Include(r => r.Items)
            .FirstOrDefaultAsync(r => r.OrderId == orderId);
    }

    public record RefundItemRequest(int OrderItemId, int Qty);

    public IQueryable<Shipment> GetAllShipments(int customerId)
    {
        return _db.Shipments
            .Include(s => s.Items)
            .Include(s => s.Order)
            .Where(s => s.Order != null && s.Order.CustomerId == customerId)
            .OrderByDescending(s => s.Id);
    }

    public IQueryable<ShipmentItem> GetShipmentItems(int customerId)
    {
        return _db.ShipmentItems
            .Include(si => si.Shipment).ThenInclude(s => s!.Order)
            .Where(si => si.Shipment != null && si.Shipment.Order != null && si.Shipment.Order.CustomerId == customerId)
            .OrderByDescending(si => si.Id);
    }
}
