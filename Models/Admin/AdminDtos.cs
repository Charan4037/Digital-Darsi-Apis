using System.ComponentModel.DataAnnotations;

namespace DOSApi.Models.Admin;

// ??? Dashboard DTOs ??????????????????????????????????????????????????????
public class DashboardResponse
{
    public DashboardData Data { get; set; } = new();
}

public class DashboardData
{
    public DashboardStats Stats { get; set; } = new();
    public List<RecentVendorDto> RecentVendors { get; set; } = new();
    public List<RecentOrderDto> RecentOrders { get; set; } = new();
}

public class DashboardStats
{
    public int TotalVendors { get; set; }
    public int ActiveVendors { get; set; }
    public int TotalProducts { get; set; }
    public int TotalOrders { get; set; }
    public int TotalCustomers { get; set; }
    public decimal TotalRevenue { get; set; }
    public decimal MonthRevenue { get; set; }
    public int PendingOrders { get; set; }
}

// ??? Vendor DTOs ????????????????????????????????????????????????????????
public class VendorListResponse
{
    public List<VendorDto> Data { get; set; } = new();
    public PaginationMeta Meta { get; set; } = new();
}

public class VendorDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? NameTe { get; set; }
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string City { get; set; } = "";
    public int Products { get; set; }
    public int Orders { get; set; }
    public decimal Revenue { get; set; }
    public double Rating { get; set; }
    public bool Active { get; set; }
    public DateTime JoinedAt { get; set; }
}

public class CreateVendorRequest
{
    [Required]
    public string Name { get; set; } = "";
    public string? NameTe { get; set; }
    public bool Active { get; set; } = true;
}

public class UpdateVendorRequest
{
    [Required]
    public string Name { get; set; } = "";
    public string? NameTe { get; set; }
    public bool Active { get; set; } = true;
}

public class RecentVendorDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Products { get; set; }
    public int Orders { get; set; }
    public decimal Revenue { get; set; }
    public bool Active { get; set; }
}

public class VendorDetailResponse
{
    public VendorDetailDto Data { get; set; } = new();
}

public class VendorDetailDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? NameTe { get; set; }
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string City { get; set; } = "";
    public double Rating { get; set; }
    public bool Active { get; set; }
    public DateTime JoinedAt { get; set; }
    public VendorStatsDto Stats { get; set; } = new();
    public List<VendorProductDto> RecentProducts { get; set; } = new();
    public List<RecentOrderDto> RecentOrders { get; set; } = new();
}

public class VendorStatsDto
{
    public int TotalProducts { get; set; }
    public int InStockProducts { get; set; }
    public int TotalOrders { get; set; }
    public int PendingOrders { get; set; }
    public decimal TotalRevenue { get; set; }
    public int CompletedOrders { get; set; }
}

public class VendorProductDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public decimal? SpecialPrice { get; set; }
    public string CategoryName { get; set; } = "";
    public bool InStock { get; set; }
}

// ??? Product DTOs ???????????????????????????????????????????????????????
public class ProductListResponse
{
    public List<AdminProductDto> Data { get; set; } = new();
    public PaginationMeta Meta { get; set; } = new();
}

public class AdminProductDto
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public decimal? SpecialPrice { get; set; }
    public string CategoryName { get; set; } = "";
    public int? CategoryId { get; set; }
    public string VendorName { get; set; } = "";
    public bool InStock { get; set; }
    public int StockQty { get; set; }
    public double AvgRating { get; set; }
    public int ReviewsCount { get; set; }
    public bool Active { get; set; }
    public int? ImageId { get; set; }
    public string? ImageUrl { get; set; }
    public int VariantCount { get; set; }
    public string? ShortDescription { get; set; }
    public string? Description { get; set; }
    public string? NameTe { get; set; }
    public string? ShortDescriptionTe { get; set; }
    public string? DescriptionTe { get; set; }
    public int MinQty { get; set; } = 1;
    public int? MaxQty { get; set; }
}

// ─── Product Variant DTOs ───────────────────────────────────────────────
public class AdminProductVariantDto
{
    public int Id { get; set; }
    public string Value { get; set; } = ""; // e.g. "1kg"
    public string Label { get; set; } = ""; // e.g. "Weight"
    public decimal Price { get; set; }
    public decimal? SpecialPrice { get; set; }
    public int StockQty { get; set; }
    public bool InStock { get; set; }
    public bool Active { get; set; }
    public int MinQty { get; set; } = 1;
    public int? MaxQty { get; set; }
}

public class VariantListResponse
{
    public List<AdminProductVariantDto> Data { get; set; } = new();
}

// ─── Product Image DTOs ─────────────────────────────────────────────────
public class AdminProductImageDto
{
    public int Id { get; set; }
    public string? Url { get; set; }
    public int Position { get; set; }
}

public class CreateVariantRequest
{
    [Required]
    public string Value { get; set; } = "";
    public string? Label { get; set; }
    [Required]
    public decimal Price { get; set; }
    public decimal? SpecialPrice { get; set; }
    public int StockQty { get; set; }
    public bool Active { get; set; } = true;
    public int? MinQty { get; set; }
    public int? MaxQty { get; set; }
}

public class UpdateVariantRequest : CreateVariantRequest { }

public class CreateProductRequest
{
    [Required]
    public string Name { get; set; } = "";
    [Required]
    public string Sku { get; set; } = "";
    [Required]
    public string VendorName { get; set; } = "";
    public string? CategoryName { get; set; }
    public int? CategoryId { get; set; }
    [Required]
    public decimal Price { get; set; }
    public decimal? SpecialPrice { get; set; }
    public int StockQty { get; set; }
    public bool InStock { get; set; }
    public bool Active { get; set; } = true;
    public string? ShortDescription { get; set; }
    public string? Description { get; set; }
    public string? NameTe { get; set; }
    public string? ShortDescriptionTe { get; set; }
    public string? DescriptionTe { get; set; }
    public int? MinQty { get; set; }
    public int? MaxQty { get; set; }
}

public class UpdateProductRequest
{
    [Required]
    public string Name { get; set; } = "";
    [Required]
    public string Sku { get; set; } = "";
    [Required]
    public string VendorName { get; set; } = "";
    public string? CategoryName { get; set; }
    public int? CategoryId { get; set; }
    [Required]
    public decimal Price { get; set; }
    public decimal? SpecialPrice { get; set; }
    public int StockQty { get; set; }
    public bool InStock { get; set; }
    public bool Active { get; set; }
    public string? ShortDescription { get; set; }
    public string? Description { get; set; }
    public string? NameTe { get; set; }
    public string? ShortDescriptionTe { get; set; }
    public string? DescriptionTe { get; set; }
    public int? MinQty { get; set; }
    public int? MaxQty { get; set; }
}

public class UpdateStatusRequest
{
    public bool? Active { get; set; }
    public string? Status { get; set; }
}

public class UpdateOrderStatusRequest
{
    [Required]
    public string Status { get; set; } = "";
}

// ??? Order DTOs ?????????????????????????????????????????????????????????
public class OrderListResponse
{
    public List<OrderListDto> Data { get; set; } = new();
    public PaginationMeta Meta { get; set; } = new();
}

public class OrderListDto
{
    public int Id { get; set; }
    public string IncrementId { get; set; } = "";
    public DateTime PlacedAt { get; set; }
    public string Status { get; set; } = "";
    public decimal GrandTotal { get; set; }
    public int ItemsCount { get; set; }
    public string CustomerName { get; set; } = "";
    public string CustomerPhone { get; set; } = "";
    public string VendorName { get; set; } = "";
    public string PaymentMethod { get; set; } = "";
    public bool IsCod { get; set; }
    public string DeliveryAddress { get; set; } = "";
}

public class OrderDetailResponse
{
    public OrderDetailDto Data { get; set; } = new();
}

public class OrderDetailDto
{
    public int Id { get; set; }
    public string IncrementId { get; set; } = "";
    public DateTime PlacedAt { get; set; }
    public string Status { get; set; } = "";
    public decimal GrandTotal { get; set; }
    public int ItemsCount { get; set; }
    public string VendorName { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string CustomerPhone { get; set; } = "";
    public string PaymentMethod { get; set; } = "";
    public bool IsCod { get; set; }
    /// <summary>Null until the order is actually delivered — see
    /// AccountService.ResolveDeliveredAt. Paired with RefundWindowDays so
    /// the admin app can compute the same refund-window cutoff the backend
    /// enforces for customers, without re-deriving the business rule.</summary>
    public DateTime? DeliveredAt { get; set; }
    public int RefundWindowDays { get; set; }
    public string DeliveryAddress { get; set; } = "";
    public List<OrderItemDto> Items { get; set; } = new();
}

public class OrderItemDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Qty { get; set; }
    public int QtyCanceled { get; set; }
    public decimal Price { get; set; }
}

// ??? Category DTOs ??????????????????????????????????????????????????????
public class CategoryListResponse
{
    public List<AdminCategoryDto> Data { get; set; } = new();
}

public class AdminCategoryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Active { get; set; }
    public int VendorCount { get; set; }
    public int ProductCount { get; set; }
    public int? ParentId { get; set; }
    public string? ParentName { get; set; }
    public string? LogoUrl { get; set; }
    public string? BannerUrl { get; set; }
    public string? NameTe { get; set; }
    public string? DescriptionTe { get; set; }
}

public class CreateCategoryRequest
{
    [Required]
    public string Name { get; set; } = "";
    [Required]
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public bool Active { get; set; } = true;
    public int? ParentId { get; set; }
    public string? NameTe { get; set; }
    public string? DescriptionTe { get; set; }
}

public class UpdateCategoryRequest
{
    [Required]
    public string Name { get; set; } = "";
    [Required]
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public bool Active { get; set; }
    public int? ParentId { get; set; }
    public string? NameTe { get; set; }
    public string? DescriptionTe { get; set; }
}

// ??? Customer DTOs ??????????????????????????????????????????????????????
public class CustomerListResponse
{
    public List<CustomerDto> Data { get; set; } = new();
    public PaginationMeta Meta { get; set; } = new();
}

public class CustomerDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string City { get; set; } = "";
    public int Orders { get; set; }
    public decimal TotalSpent { get; set; }
    public DateTime JoinedAt { get; set; }
    public bool Active { get; set; }
}

// ??? Transaction DTOs ???????????????????????????????????????????????????
public class TransactionListResponse
{
    public List<TransactionDto> Data { get; set; } = new();
    public PaginationMeta Meta { get; set; } = new();
    public TransactionSummary Summary { get; set; } = new();
}

public class TransactionDto
{
    public int Id { get; set; }
    public string OrderId { get; set; } = "";
    public string? VendorName { get; set; }
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public bool IsCredit { get; set; }
    public string Status { get; set; } = "";
    public string PaymentMethod { get; set; } = "";
}

public class TransactionSummary
{
    public decimal TotalSettled { get; set; }
    public decimal TotalPending { get; set; }
    public decimal TotalRefunded { get; set; }
}

public class VendorTransactionListResponse
{
    public List<VendorTransactionDto> Data { get; set; } = new();
    public PaginationMeta Meta { get; set; } = new();
    public VendorTransactionSummary Summary { get; set; } = new();
}

public class VendorTransactionDto
{
    public int Id { get; set; }
    public string OrderId { get; set; } = "";
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public string Type { get; set; } = ""; // credit or debit
    public string Status { get; set; } = ""; // settled, pending, or failed
    public string PaymentMethod { get; set; } = "";
    public string? Note { get; set; }
}

public class VendorTransactionSummary
{
    public decimal TotalSettled { get; set; }
    public decimal TotalPending { get; set; }
    public decimal TotalRefunds { get; set; }
}

// ??? Response Wrappers ???????????????????????????????????????????????????
public class MessageResponse
{
    public string Message { get; set; } = "";
}

public class CreatedResponse<T>
{
    public T? Data { get; set; }
    public string Message { get; set; } = "";
}

public class UpdatedResponse<T>
{
    public T? Data { get; set; }
    public string Message { get; set; } = "";
}

public class PaginationMeta
{
    public int Total { get; set; }
    public int CurrentPage { get; set; }
    public int LastPage { get; set; }
    public int PerPage { get; set; }
}

public class RecentOrderDto
{
    public int Id { get; set; }
    public string IncrementId { get; set; } = "";
    public string Status { get; set; } = "";
    public decimal GrandTotal { get; set; }
    public string CustomerName { get; set; } = "";
    public string VendorName { get; set; } = "";
}

public class VendorProductListResponse
{
    public List<AdminProductDto> Data { get; set; } = new();
    public PaginationMeta Meta { get; set; } = new();
}

public class VendorCategoryListResponse
{
    public List<AdminCategoryDto> Data { get; set; } = new();
}

public class VendorOrderListResponse
{
    public List<OrderListDto> Data { get; set; } = new();
    public PaginationMeta Meta { get; set; } = new();
}

// ─── RBAC DTOs ───────────────────────────────────────────────────────────
public class RoleListResponse
{
    public List<RoleDto> Data { get; set; } = new();
}

public class RoleDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; }
    public int AdminUserCount { get; set; }
}

public class PermissionDto
{
    public string FeatureKey { get; set; } = "";
    public string Label { get; set; } = "";
    public string Section { get; set; } = "";
}

public class PermissionListResponse
{
    public List<PermissionDto> Data { get; set; } = new();
}

public class PermissionGrantDto
{
    public string FeatureKey { get; set; } = "";
    public string Label { get; set; } = "";
    public string Section { get; set; } = "";
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
}

public class RoleDetailResponse
{
    public RoleDetailDto Data { get; set; } = new();
}

public class RoleDetailDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; }
    public List<PermissionGrantDto> Permissions { get; set; } = new();
}

public class PermissionGrantRequest
{
    [Required]
    public string FeatureKey { get; set; } = "";
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
}

public class CreateRoleRequest
{
    [Required]
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<PermissionGrantRequest> Permissions { get; set; } = new();
}

public class UpdateRoleRequest
{
    [Required]
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<PermissionGrantRequest> Permissions { get; set; } = new();
}

public class AssignRoleRequest
{
    [Required]
    public int RoleId { get; set; }
}

public class AdminUserDto
{
    public int CustomerId { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public int RoleId { get; set; }
    public string RoleName { get; set; } = "";
    public string RoleSlug { get; set; } = "";
}

public class AdminUserListResponse
{
    public List<AdminUserDto> Data { get; set; } = new();
}

public class MyPermissionsResponse
{
    public Dictionary<string, PermissionFlags> Data { get; set; } = new();
    public RoleInfoDto? Role { get; set; }
}

public class PermissionFlags
{
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
}

public class RoleInfoDto
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
}
