using iTextSharp.text;
using iTextSharp.text.pdf;
using Microsoft.EntityFrameworkCore;
using DOSApi.Data;
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

    /// <summary>Get or generate order invoice PDF (cached for 10 minutes)</summary>
    public async Task<byte[]> GenerateInvoicePdfAsync(int orderId, int customerId)
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
        var order = await _db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.Payment)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CustomerId == customerId);

        if (order == null)
            throw new InvalidOperationException($"Order {orderId} not found for customer {customerId}.");

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

    private byte[] GeneratePdfBytes(Order order)
    {
        var stream = new MemoryStream();
        var document = new Document(PageSize.A4, 40, 40, 40, 40);
        var writer = PdfWriter.GetInstance(document, stream);

        document.Open();

        var titleFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 24);
        var labelFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 9, BaseColor.DARK_GRAY);
        var dataFont = FontFactory.GetFont(FontFactory.HELVETICA, 9);
        var tableHeaderFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 9, BaseColor.WHITE);
        var tableCellFont = FontFactory.GetFont(FontFactory.HELVETICA, 9);
        var totalFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 11);

        // INVOICE title
        document.Add(new Paragraph("INVOICE", titleFont));
        document.Add(new Paragraph(" "));

        // Top section: Order info on right
        var topTable = new PdfPTable(2);
        topTable.SetTotalWidth(new float[] { 280, 220 });
        topTable.LockedWidth = true;

        var leftCell = new PdfPCell() { Border = 0 };
        leftCell.AddElement(new Paragraph("Digital Darsi", FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 12)));
        topTable.AddCell(leftCell);

        var rightCell = new PdfPCell() { Border = 0, HorizontalAlignment = Element.ALIGN_RIGHT };
        rightCell.AddElement(new Paragraph($"Order #: {order.Id}", dataFont));
        rightCell.AddElement(new Paragraph($"Date: {order.CreatedAt:MMMM dd, yyyy}", dataFont));
        rightCell.AddElement(new Paragraph($"Status: {order.Status}", dataFont));
        topTable.AddCell(rightCell);
        document.Add(topTable);

        document.Add(new Paragraph(" "));

        // Billing and Shipping addresses (clean text, no boxes)
        var addrTable = new PdfPTable(2);
        addrTable.SetTotalWidth(new float[] { 280, 220 });
        addrTable.LockedWidth = true;

        var billingAddr = order.Addresses?.FirstOrDefault(a => a.AddressType == "order_billing");
        var shippingAddr = order.Addresses?.FirstOrDefault(a => a.AddressType == "order_shipping")
                        ?? order.Addresses?.FirstOrDefault(a => a.UseForShipping);

        // Bill To
        var billCell = new PdfPCell() { Border = 0, Padding = 0 };
        billCell.AddElement(new Paragraph("BILL TO:", labelFont));
        if (billingAddr != null)
        {
            billCell.AddElement(new Paragraph($"{billingAddr.FirstName} {billingAddr.LastName}", dataFont));
            billCell.AddElement(new Paragraph($"{billingAddr.AddressLine}", dataFont));
            billCell.AddElement(new Paragraph($"{billingAddr.City}, {billingAddr.State} {billingAddr.Postcode}", dataFont));
            billCell.AddElement(new Paragraph($"Ph: {billingAddr.Phone ?? ""}", dataFont));
        }
        addrTable.AddCell(billCell);

        // Ship To
        var shipCell = new PdfPCell() { Border = 0, Padding = 0 };
        shipCell.AddElement(new Paragraph("SHIP TO:", labelFont));
        if (shippingAddr != null)
        {
            shipCell.AddElement(new Paragraph($"{shippingAddr.FirstName} {shippingAddr.LastName}", dataFont));
            shipCell.AddElement(new Paragraph($"{shippingAddr.AddressLine}", dataFont));
            shipCell.AddElement(new Paragraph($"{shippingAddr.City}, {shippingAddr.State} {shippingAddr.Postcode}", dataFont));
            shipCell.AddElement(new Paragraph($"Ph: {shippingAddr.Phone ?? ""}", dataFont));
        }
        addrTable.AddCell(shipCell);
        document.Add(addrTable);

        document.Add(new Paragraph(" "));

        // Items table (4 columns: Description, Price, Qty, Amount)
        var itemsTable = new PdfPTable(4);
        itemsTable.SetTotalWidth(new float[] { 220, 85, 55, 110 });
        itemsTable.LockedWidth = true;

        // Headers with blue background
        var headers = new[] { "Description", "Price", "Qty", "Amount" };
        foreach (var header in headers)
        {
            var cell = new PdfPCell(new Phrase(header, tableHeaderFont));
            cell.BackgroundColor = new BaseColor(41, 84, 130);
            cell.Padding = 8;
            cell.VerticalAlignment = Element.ALIGN_MIDDLE;
            itemsTable.AddCell(cell);
        }

        // Item rows
        if (order.Items != null)
        {
            foreach (var item in order.Items)
            {
                itemsTable.AddCell(new PdfPCell(new Phrase(item.Name ?? "N/A", tableCellFont)) { Padding = 6, Border = Rectangle.BOTTOM_BORDER | Rectangle.LEFT_BORDER | Rectangle.RIGHT_BORDER });
                itemsTable.AddCell(new PdfPCell(new Phrase($"₹{item.Price ?? 0:F2}", tableCellFont)) { Padding = 6, Border = Rectangle.BOTTOM_BORDER | Rectangle.LEFT_BORDER | Rectangle.RIGHT_BORDER, HorizontalAlignment = Element.ALIGN_RIGHT });
                itemsTable.AddCell(new PdfPCell(new Phrase((item.QtyOrdered ?? 0).ToString(), tableCellFont)) { Padding = 6, Border = Rectangle.BOTTOM_BORDER | Rectangle.LEFT_BORDER | Rectangle.RIGHT_BORDER, HorizontalAlignment = Element.ALIGN_CENTER });
                var total = (item.Price ?? 0) * (item.QtyOrdered ?? 0);
                itemsTable.AddCell(new PdfPCell(new Phrase($"₹{total:F2}", tableCellFont)) { Padding = 6, Border = Rectangle.BOTTOM_BORDER | Rectangle.LEFT_BORDER | Rectangle.RIGHT_BORDER, HorizontalAlignment = Element.ALIGN_RIGHT });
            }
        }

        document.Add(itemsTable);
        document.Add(new Paragraph(" "));

        // Summary section - clean and simple
        var summaryTable = new PdfPTable(2);
        summaryTable.SetTotalWidth(new float[] { 300, 110 });
        summaryTable.LockedWidth = true;

        // Subtotal
        var subtotalLbl = new PdfPCell(new Phrase("Subtotal", dataFont));
        subtotalLbl.Border = 0;
        subtotalLbl.HorizontalAlignment = Element.ALIGN_RIGHT;
        subtotalLbl.Padding = 4;
        summaryTable.AddCell(subtotalLbl);

        var subtotalVal = new PdfPCell(new Phrase($"₹{(order.SubTotal ?? 0):F2}", dataFont));
        subtotalVal.Border = 0;
        subtotalVal.HorizontalAlignment = Element.ALIGN_RIGHT;
        subtotalVal.Padding = 4;
        summaryTable.AddCell(subtotalVal);

        // Shipping
        var shippingLbl = new PdfPCell(new Phrase("Shipping", dataFont));
        shippingLbl.Border = 0;
        shippingLbl.HorizontalAlignment = Element.ALIGN_RIGHT;
        shippingLbl.Padding = 4;
        summaryTable.AddCell(shippingLbl);

        var shippingVal = new PdfPCell(new Phrase($"₹{(order.ShippingAmount ?? 0):F2}", dataFont));
        shippingVal.Border = 0;
        shippingVal.HorizontalAlignment = Element.ALIGN_RIGHT;
        shippingVal.Padding = 4;
        summaryTable.AddCell(shippingVal);

        // Tax
        var taxLbl = new PdfPCell(new Phrase("Tax", dataFont));
        taxLbl.Border = 0;
        taxLbl.HorizontalAlignment = Element.ALIGN_RIGHT;
        taxLbl.Padding = 4;
        summaryTable.AddCell(taxLbl);

        var taxVal = new PdfPCell(new Phrase($"₹{(order.TaxAmount ?? 0):F2}", dataFont));
        taxVal.Border = 0;
        taxVal.HorizontalAlignment = Element.ALIGN_RIGHT;
        taxVal.Padding = 4;
        summaryTable.AddCell(taxVal);

        // Divider line - spans both columns (from text to amount)
        var dividerLbl = new PdfPCell(new Phrase(" ", dataFont));
        dividerLbl.Border = Rectangle.TOP_BORDER;
        dividerLbl.BorderWidthTop = 1f;
        dividerLbl.Padding = 2;
        summaryTable.AddCell(dividerLbl);

        var dividerVal = new PdfPCell(new Phrase(" ", dataFont));
        dividerVal.Border = Rectangle.TOP_BORDER;
        dividerVal.BorderWidthTop = 1f;
        dividerVal.Padding = 2;
        dividerVal.HorizontalAlignment = Element.ALIGN_RIGHT;
        summaryTable.AddCell(dividerVal);

        // Total
        var totalLbl = new PdfPCell(new Phrase("Total", totalFont));
        totalLbl.Border = 0;
        totalLbl.HorizontalAlignment = Element.ALIGN_RIGHT;
        totalLbl.Padding = 6;
        summaryTable.AddCell(totalLbl);

        var totalVal = new PdfPCell(new Phrase($"₹{(order.GrandTotal ?? 0):F2}", totalFont));
        totalVal.Border = 0;
        totalVal.HorizontalAlignment = Element.ALIGN_RIGHT;
        totalVal.Padding = 6;
        summaryTable.AddCell(totalVal);

        document.Add(summaryTable);
        document.Add(new Paragraph(" "));
        document.Add(new Paragraph("Thank you for your business!", FontFactory.GetFont(FontFactory.HELVETICA, 9)));

        document.Close();
        return stream.ToArray();
    }
}
