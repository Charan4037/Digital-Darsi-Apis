# Digital Darsi API (BagistoApi)

ASP.NET Core 8 Web API that fronts a Bagisto storefront with REST + GraphQL endpoints, JWT auth, and a MySQL-backed catalog seeded from a scraped data set.

## Stack

- .NET 8 / ASP.NET Core
- Entity Framework Core + Pomelo MySQL provider
- HotChocolate (GraphQL)
- JWT bearer authentication
- Swagger / Swashbuckle

## Getting started

### 1. Prerequisites

- .NET 8 SDK
- MySQL 8 with a `bagisto` database
- A running Bagisto backend (for the storefront key / image proxy)

### 2. Configure local secrets

The committed `appsettings.json` ships with placeholder values. Copy the example file and fill in your real values:

```bash
cp appsettings.Development.example.json appsettings.Development.json
```

Then edit `appsettings.Development.json` with:

- `ConnectionStrings:Bagisto` — your MySQL credentials
- `Jwt:Key` — any long random string (≥ 32 chars)
- `App:BaseUrl` — your Bagisto backend URL
- `App:StorefrontKey` — your Bagisto storefront API key

`appsettings.Development.json` is gitignored, so your real secrets stay local.

### 3. Run

```bash
dotnet restore
dotnet run
```

Swagger UI is served at `/swagger` and the GraphQL playground at `/graphql`.

## Project layout

| Folder         | Purpose                                              |
| -------------- | ---------------------------------------------------- |
| `Controllers/` | REST endpoints                                       |
| `GraphQL/`     | HotChocolate queries & mutations                     |
| `Services/`    | Auth, cart, catalog, payment, etc.                   |
| `Data/`        | EF Core `DbContext` and seeders (scraped catalog)    |
| `Models/`      | Domain entities and DTOs                             |
| `Middleware/`  | Custom request pipeline pieces                       |
| `Helpers/`     | Shared utilities                                     |
