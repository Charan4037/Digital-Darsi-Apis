using iTextSharp.text;
using iTextSharp.text.pdf;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
using DOSApi.Helpers;
using DOSApi.Models;
using DOSApi.Models.Sales;
using System.Collections.Concurrent;

namespace DOSApi.Services;

/// <summary>Generates order invoices as PDFs with smart caching (10-minute TTL)</summary>
public class OrderInvoiceService
{
    private readonly DOSDbContext _db;
    private readonly IConfiguration _config;

    // In-memory cache: key = "order_{orderId}", value = (pdfBytes, expiresAt)
    private static readonly ConcurrentDictionary<string, (byte[] bytes, DateTime expiresAt)> _pdfCache = new();

    // Cache TTL: 10 minutes — balances freshness with speed
    private static readonly TimeSpan CACHE_TTL = TimeSpan.FromMinutes(10);

    public OrderInvoiceService(DOSDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    /// <summary>Get or generate order invoice PDF for the owning customer (cached for 10 minutes).
    /// Enforces that the order belongs to <paramref name="customerId"/> — use this from
    /// customer-facing endpoints only.</summary>
    public Task<byte[]> GenerateInvoicePdfAsync(int orderId, int customerId) =>
        GenerateInvoicePdfAsync(orderId, (int?)customerId);

    /// <summary>Get or generate order invoice PDF with no customer-ownership check (cached for
    /// 10 minutes) — use this from admin/vendor endpoints that already gate access another way.</summary>
    public Task<byte[]> GenerateInvoicePdfAsync(int orderId) =>
        GenerateInvoicePdfAsync(orderId, (int?)null);

    private async Task<byte[]> GenerateInvoicePdfAsync(int orderId, int? customerId)
    {
        var cacheKey = $"order_{orderId}";
        var now = DateTime.UtcNow;

        // Check cache first — if valid, return instantly
        if (_pdfCache.TryGetValue(cacheKey, out var cached))
        {
            if (now < cached.expiresAt)
                return cached.bytes; // Cache hit — instant return

            // Cache expired, remove it
            _pdfCache.TryRemove(cacheKey, out _);
        }

        // Cache miss or expired — generate fresh PDF
        var orderQuery = _db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.Payment)
            .Include(o => o.ExtraCharges)
            .Where(o => o.Id == orderId);

        if (customerId.HasValue)
            orderQuery = orderQuery.Where(o => o.CustomerId == customerId.Value);

        var order = await orderQuery.FirstOrDefaultAsync();

        if (order == null)
            throw new InvalidOperationException(customerId.HasValue
                ? $"Order {orderId} not found for customer {customerId}."
                : $"Order {orderId} not found.");

        if (order.Items == null || order.Items.Count == 0)
            throw new InvalidOperationException($"Order {orderId} has no items to invoice.");

        // Load addresses separately (Addresses navigation is ignored in DbContext)
        var addresses = await _db.Addresses
            .AsNoTracking()
            .Where(a => a.OrderId == orderId)
            .ToListAsync();
        order.Addresses = addresses;

        // Generate PDF in memory
        byte[] pdfBytes;
        try
        {
            pdfBytes = GeneratePdfBytes(order);
        }
        catch (Exception ex)
        {
            var detail = $"{ex.GetType().Name}: {ex.Message}";
            if (ex.InnerException != null)
                detail += $" | Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
            Console.WriteLine($"[PDF Error] Order {orderId}: {detail}\n{ex.StackTrace}");
            throw new InvalidOperationException($"Failed to generate PDF for order {orderId}: {detail}", ex);
        }

        if (pdfBytes == null || pdfBytes.Length == 0)
            throw new InvalidOperationException($"Generated PDF for order {orderId} is empty.");

        // Cache it for 10 minutes
        _pdfCache[cacheKey] = (pdfBytes, now.Add(CACHE_TTL));

        return pdfBytes;
    }

    /// <summary>Rupee amounts print as "Rs. 1,234.56" — the base-14 Helvetica
    /// font iTextSharp 5.x uses has no glyph for the U+20B9 rupee sign, so a
    /// literal ₹ renders as a hollow box (the exact defect visible in the old
    /// WooCommerce-store invoices this layout matches).</summary>
    private static string Money(decimal? v) => PriceFormatter.Format(v ?? 0, "Rs. ");

    private class InvoiceFooter : PdfPageEventHelper
    {
        private readonly Font _font = FontFactory.GetFont(FontFactory.HELVETICA, 9);

        public override void OnEndPage(PdfWriter writer, Document document)
        {
            var footer = new Phrase($"- {writer.PageNumber} -", _font);
            ColumnText.ShowTextAligned(
                writer.DirectContent, Element.ALIGN_CENTER, footer,
                document.PageSize.Width / 2, document.BottomMargin - 20, 0);
        }
    }

    private byte[] GeneratePdfBytes(Order order)
    {
        var stream = new MemoryStream();
        var document = new Document(PageSize.A4, 40, 40, 40, 40);
        var writer = PdfWriter.GetInstance(document, stream);
        writer.PageEvent = new InvoiceFooter();

        document.Open();

        var storeNameFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 14);
        var boldFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 10);
        var dataFont = FontFactory.GetFont(FontFactory.HELVETICA, 10);
        var sectionHeaderFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 10, Font.UNDERLINE);
        var tableHeaderFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 10);
        var tableCellFont = FontFactory.GetFont(FontFactory.HELVETICA, 10);
        var totalFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 11);

        // Store name / Order # / date
        document.Add(new Paragraph("DarsiOnlineStore", storeNameFont));
        document.Add(new Paragraph(" "));
        var orderNumber = !string.IsNullOrWhiteSpace(order.IncrementId) ? order.IncrementId : order.Id.ToString();
        document.Add(new Paragraph($"Order# {orderNumber}", boldFont));
        var orderDate = order.CreatedAt ?? DateTime.UtcNow;
        document.Add(new Paragraph($"Date: {orderDate:dddd, MMMM d, yyyy}", dataFont));
        document.Add(new Paragraph(" "));

        // Billing / Shipping information — underlined column headers
        var billingAddr = order.Addresses?.FirstOrDefault(a => a.AddressType == "order_billing");
        var shippingAddr = order.Addresses?.FirstOrDefault(a => a.AddressType == "order_shipping")
                        ?? order.Addresses?.FirstOrDefault(a => a.UseForShipping);

        var addrHeaderTable = new PdfPTable(2);
        addrHeaderTable.SetTotalWidth(new float[] { 260, 260 });
        addrHeaderTable.LockedWidth = true;
        addrHeaderTable.AddCell(new PdfPCell(new Phrase("Billing Information", sectionHeaderFont)) { Border = 0, PaddingBottom = 4 });
        addrHeaderTable.AddCell(new PdfPCell(new Phrase("Shipping Information", sectionHeaderFont)) { Border = 0, PaddingBottom = 4 });
        document.Add(addrHeaderTable);

        var addrTable = new PdfPTable(2);
        addrTable.SetTotalWidth(new float[] { 260, 260 });
        addrTable.LockedWidth = true;

        var paymentTitle = order.Payment?.MethodTitle ?? order.Payment?.Method ?? "N/A";
        if (string.Equals(order.Payment?.Method, "cashondelivery", StringComparison.OrdinalIgnoreCase)
            && !paymentTitle.Contains("COD"))
            paymentTitle += " (COD)";
        var shippingTitle = order.ShippingTitle ?? order.ShippingMethod ?? "N/A";

        var billCell = new PdfPCell() { Border = 0, Padding = 0, PaddingTop = 2 };
        if (billingAddr != null)
        {
            billCell.AddElement(new Paragraph($"Name: {billingAddr.FirstName} {billingAddr.LastName}", dataFont));
            billCell.AddElement(new Paragraph($"Phone: {billingAddr.Phone ?? ""}", dataFont));
            billCell.AddElement(new Paragraph($"Address: {billingAddr.AddressLine}, {billingAddr.City}, {billingAddr.Postcode}", dataFont));
        }
        billCell.AddElement(new Paragraph($"Payment method: {paymentTitle}", dataFont));
        addrTable.AddCell(billCell);

        var shipCell = new PdfPCell() { Border = 0, Padding = 0, PaddingTop = 2 };
        if (shippingAddr != null)
        {
            shipCell.AddElement(new Paragraph($"Name: {shippingAddr.FirstName} {shippingAddr.LastName}", dataFont));
            shipCell.AddElement(new Paragraph($"Phone: {shippingAddr.Phone ?? ""}", dataFont));
            shipCell.AddElement(new Paragraph($"Address: {shippingAddr.AddressLine}, {shippingAddr.City}, {shippingAddr.Postcode}", dataFont));
        }
        shipCell.AddElement(new Paragraph($"Shipping method: {shippingTitle}", dataFont));
        addrTable.AddCell(shipCell);
        document.Add(addrTable);

        document.Add(new Paragraph(" "));

        // Items table: Name, Price, Qty, Total
        var itemsTable = new PdfPTable(4);
        itemsTable.SetTotalWidth(new float[] { 250, 90, 60, 120 });
        itemsTable.LockedWidth = true;

        var headers = new[] { "Name", "Price", "Qty", "Total" };
        var headerAligns = new[] { Element.ALIGN_LEFT, Element.ALIGN_RIGHT, Element.ALIGN_RIGHT, Element.ALIGN_RIGHT };
        for (int i = 0; i < headers.Length; i++)
        {
            itemsTable.AddCell(new PdfPCell(new Phrase(headers[i], tableHeaderFont))
            {
                Border = Rectangle.BOTTOM_BORDER,
                BorderWidthBottom = 1f,
                Padding = 6,
                HorizontalAlignment = headerAligns[i]
            });
        }

        if (order.Items != null)
        {
            foreach (var item in order.Items)
            {
                var qty = item.QtyOrdered ?? 0;
                var total = (item.Price ?? 0) * qty;

                itemsTable.AddCell(new PdfPCell(new Phrase(item.Name ?? "N/A", tableCellFont)) { Border = 0, PaddingTop = 8, PaddingBottom = 8 });
                itemsTable.AddCell(new PdfPCell(new Phrase(Money(item.Price), tableCellFont)) { Border = 0, PaddingTop = 8, PaddingBottom = 8, HorizontalAlignment = Element.ALIGN_RIGHT });
                itemsTable.AddCell(new PdfPCell(new Phrase(qty.ToString(), tableCellFont)) { Border = 0, PaddingTop = 8, PaddingBottom = 8, HorizontalAlignment = Element.ALIGN_RIGHT });
                itemsTable.AddCell(new PdfPCell(new Phrase(Money(total), tableCellFont)) { Border = 0, PaddingTop = 8, PaddingBottom = 8, HorizontalAlignment = Element.ALIGN_RIGHT });
            }
        }

        document.Add(itemsTable);
        document.Add(new Paragraph(" "));

        // Totals — right-aligned lines, order total in bold. Extra charges
        // are itemized the same way the checkout review screen shows them
        // (Handling Charges, Processing Fee, Cold Chain Fee, ...) rather
        // than one lumped fee — falls back to a single line for orders
        // placed before order_extra_charges existed.
        document.Add(new Paragraph($"Sub-total: {Money(order.SubTotal)}", dataFont) { Alignment = Element.ALIGN_RIGHT });

        if (order.ExtraCharges != null && order.ExtraCharges.Count > 0)
        {
            foreach (var charge in order.ExtraCharges.OrderBy(c => c.SortOrder).ThenBy(c => c.Id))
            {
                document.Add(new Paragraph($"{charge.Name}: {Money(charge.Amount)}", dataFont) { Alignment = Element.ALIGN_RIGHT });
            }
        }
        else if (order.ExtraChargesTotal is > 0)
        {
            document.Add(new Paragraph($"Additional Charges: {Money(order.ExtraChargesTotal)}", dataFont) { Alignment = Element.ALIGN_RIGHT });
        }

        document.Add(new Paragraph($"Shipping: {Money(order.ShippingAmount)}", dataFont) { Alignment = Element.ALIGN_RIGHT });
        document.Add(new Paragraph($"Tax: {Money(order.TaxAmount)}", dataFont) { Alignment = Element.ALIGN_RIGHT });
        document.Add(new Paragraph($"Order total {Money(order.GrandTotal)}", totalFont) { Alignment = Element.ALIGN_RIGHT });

        document.Close();
        return stream.ToArray();
    }
}
