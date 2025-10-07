using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// --- CONFIGURACIÓN DE VARIABLES ---
var supabaseUrl = Environment.GetEnvironmentVariable("SUPABASE_URL");
var supabaseKey = Environment.GetEnvironmentVariable("SUPABASE_KEY");
var supabaseJwtSecret = Environment.GetEnvironmentVariable("SUPABASE_JWT_SECRET");

if (string.IsNullOrEmpty(supabaseUrl) || string.IsNullOrEmpty(supabaseKey))
    throw new Exception("Supabase environment variables not configured.");

// --- SERVICES ---
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});

builder.Services.AddHttpClient("Supabase", client =>
{
    client.BaseAddress = new Uri($"{supabaseUrl}/rest/v1/");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", supabaseKey);
    client.DefaultRequestHeaders.Add("apikey", supabaseKey);
    client.DefaultRequestHeaders.Add("Prefer", "return=representation");
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// JWT Authentication
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.SaveToken = true;

        if (!string.IsNullOrEmpty(supabaseJwtSecret))
        {
            var key = Encoding.UTF8.GetBytes(supabaseJwtSecret);
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = true,
                ValidIssuer = $"{supabaseUrl}/auth/v1",
                ValidateAudience = true,
                ValidAudience = "authenticated",
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };
        }
        else
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = false,
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = false
            };
        }
    });

builder.Services.AddAuthorization();

var app = builder.Build();

// --- MIDDLEWARE ---
app.UseCors("AllowFrontend");
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// --- ENDPOINTS ---
app.MapGet("/", () => Results.Ok(new
{
    status = "✅ Farmacia API activa",
    timestamp = DateTime.UtcNow
}));

// --- PERFIL AUTENTICADO ---
app.MapGet("/me", async (HttpContext httpContext, IHttpClientFactory factory) =>
{
    var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? httpContext.User.FindFirstValue("sub")
               ?? httpContext.User.FindFirstValue("user_id");

    if (string.IsNullOrEmpty(userId))
        return Results.Unauthorized();

    return await HandleSupabaseRequest(http => http.GetAsync($"perfiles?id=eq.{userId}&select=*"), factory);
}).RequireAuthorization();

// --- MEDICAMENTOS ---
// GET: Listar todos los medicamentos
app.MapGet("/medicamentos", async (IHttpClientFactory factory) =>
    await HandleSupabaseRequest(http => http.GetAsync("medicamentos?select=*&order=nombre_comercial"), factory));

// GET: Obtener medicamento por ID
app.MapGet("/medicamentos/{id}", async (long id, IHttpClientFactory factory) =>
    await HandleSupabaseRequest(http => http.GetAsync($"medicamentos?id=eq.{id}&select=*"), factory));

// POST: Crear nuevo medicamento
app.MapPost("/medicamentos", async ([FromBody] MedicamentoRequest request, IHttpClientFactory factory, HttpContext httpContext) =>
{
    Console.WriteLine($"📝 POST /medicamentos - Usuario: {httpContext.User.Identity?.IsAuthenticated}");

    if (string.IsNullOrWhiteSpace(request.NombreComercial))
        return Results.BadRequest(new { error = "El nombre comercial es requerido" });

    if (request.PrecioVenta <= 0)
        return Results.BadRequest(new { error = "El precio de venta debe ser mayor a 0" });

    return await HandleSupabaseRequest(async http =>
    {
        var payload = new
        {
            nombre_comercial = request.NombreComercial.Trim(),
            nombre_generico = request.NombreGenerico?.Trim(),
            concentracion = request.Concentracion?.Trim(),
            forma_farmaceutica = request.FormaFarmaceutica?.Trim(),
            categoria = request.Categoria?.Trim(),
            laboratorio = request.Laboratorio?.Trim(),
            precio_venta = request.PrecioVenta
        };

        Console.WriteLine($"📦 Payload: {JsonSerializer.Serialize(payload)}");

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await http.PostAsync("medicamentos", content);
    }, factory);
});

// PUT: Actualizar medicamento
app.MapPut("/medicamentos/{id}", async (long id, [FromBody] MedicamentoRequest request, IHttpClientFactory factory) =>
{
    if (string.IsNullOrWhiteSpace(request.NombreComercial))
        return Results.BadRequest(new { error = "El nombre comercial es requerido" });

    if (request.PrecioVenta <= 0)
        return Results.BadRequest(new { error = "El precio de venta debe ser mayor a 0" });

    return await HandleSupabaseRequest(async http =>
    {
        var payload = new
        {
            nombre_comercial = request.NombreComercial.Trim(),
            nombre_generico = request.NombreGenerico?.Trim(),
            concentracion = request.Concentracion?.Trim(),
            forma_farmaceutica = request.FormaFarmaceutica?.Trim(),
            categoria = request.Categoria?.Trim(),
            laboratorio = request.Laboratorio?.Trim(),
            precio_venta = request.PrecioVenta
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await http.PatchAsync($"medicamentos?id=eq.{id}", content);
    }, factory);
}).RequireAuthorization();

// DELETE: Eliminar medicamento
app.MapDelete("/medicamentos/{id}", async (long id, IHttpClientFactory factory) =>
{
    return await HandleSupabaseRequest(http => http.DeleteAsync($"medicamentos?id=eq.{id}"), factory);
}).RequireAuthorization();

// GET: Vista de medicamentos con stock
app.MapGet("/medicamentos-con-stock", async (IHttpClientFactory factory) =>
    await HandleSupabaseRequest(http => http.GetAsync("medicamentos_con_stock?select=*&order=nombre_comercial"), factory));

// --- LOTES ---
app.MapGet("/lotes", async (IHttpClientFactory factory) =>
    await HandleSupabaseRequest(http => http.GetAsync("lotes?select=*,medicamentos(*),proveedores(nombre)&cantidad_actual=gt.0&order=fecha_vencimiento.asc"), factory));

app.MapPost("/lotes", async ([FromBody] LoteRequest request, IHttpClientFactory factory) =>
{
    if (request.CantidadInicial <= 0)
        return Results.BadRequest(new { error = "La cantidad debe ser mayor a 0" });
    if (request.FechaVencimiento <= DateTime.Now)
        return Results.BadRequest(new { error = "La fecha de vencimiento debe ser futura" });

    return await HandleSupabaseRequest(async http =>
    {
        var payload = new
        {
            medicamento_id = request.MedicamentoId,
            proveedor_id = request.ProveedorId,
            fecha_ingreso = request.FechaIngreso,
            fecha_vencimiento = request.FechaVencimiento,
            cantidad_inicial = request.CantidadInicial,
            cantidad_actual = request.CantidadActual,
            precio_compra = request.PrecioCompra
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        Console.WriteLine($"📦 Enviando a Supabase: {JsonSerializer.Serialize(payload)}");

        return await http.PostAsync("lotes", content);
    }, factory);
});

// --- CLIENTES, PROVEEDORES Y VENTAS ---
app.MapGet("/clientes", async (IHttpClientFactory factory) =>
    await HandleSupabaseRequest(http => http.GetAsync("clientes?select=*&order=apellido,nombre"), factory));

app.MapGet("/proveedores", async (IHttpClientFactory factory) =>
    await HandleSupabaseRequest(http => http.GetAsync("proveedores?select=*&order=nombre"), factory));

app.MapGet("/ventas", async (IHttpClientFactory factory) =>
    await HandleSupabaseRequest(http => http.GetAsync("ventas?select=*,clientes(nombre,apellido,dni),perfiles(nombre,apellido)&order=fecha.desc"), factory));

app.MapPost("/ventas", async ([FromBody] VentaRequest request, IHttpClientFactory factory) =>
{
    Console.WriteLine("🧾 POST /ventas recibido");
    Console.WriteLine($"📥 JSON recibido: {JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true })}");

    if (request.Items == null || !request.Items.Any())
        return Results.BadRequest(new { error = "La venta debe tener al menos un item" });

    var payload = new
    {
        p_cliente_id = request.ClienteId,
        p_usuario_id = request.UsuarioId,
        p_items = request.Items
    };

    Console.WriteLine($"📤 Enviando a Supabase: {JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true })}");

    return await HandleSupabaseRequest(async http =>
    {
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await http.PostAsync("rpc/registrar_venta_y_actualizar_stock", content);
    }, factory);
});


app.Run();

// --- HELPER ---
async Task<IResult> HandleSupabaseRequest(Func<HttpClient, Task<HttpResponseMessage>> request, IHttpClientFactory factory)
{
    try
    {
        using var httpClient = factory.CreateClient("Supabase");
        var response = await request(httpClient);
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"❌ Supabase error: {response.StatusCode} - {content}");
            return Results.Problem(detail: content, statusCode: (int)response.StatusCode);
        }

        return Results.Content(content, "application/json");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Exception: {ex.Message}");
        return Results.Problem(ex.Message);
    }
}

// --- MODELOS ---
public class MedicamentoRequest
{
    [JsonPropertyName("nombreComercial")]
    [Required(ErrorMessage = "El nombre comercial es requerido")]
    public string NombreComercial { get; set; } = string.Empty;

    [JsonPropertyName("nombreGenerico")]
    [Required(ErrorMessage = "El nombre genérico es requerido")]
    public string NombreGenerico { get; set; } = string.Empty;

    [JsonPropertyName("concentracion")]
    public string? Concentracion { get; set; }

    [JsonPropertyName("formaFarmaceutica")]
    public string? FormaFarmaceutica { get; set; }

    [JsonPropertyName("categoria")]
    public string? Categoria { get; set; }

    [JsonPropertyName("laboratorio")]
    public string? Laboratorio { get; set; }

    [JsonPropertyName("precioVenta")]
    [Range(0.01, double.MaxValue, ErrorMessage = "El precio debe ser mayor a 0")]
    public decimal PrecioVenta { get; set; }
}

public class LoteRequest
{
    [JsonPropertyName("medicamento_id")]
    public long MedicamentoId { get; set; }

    [JsonPropertyName("proveedor_id")]
    public long ProveedorId { get; set; }

    [JsonPropertyName("fecha_ingreso")]
    public DateTime FechaIngreso { get; set; }

    [JsonPropertyName("fecha_vencimiento")]
    public DateTime FechaVencimiento { get; set; }

    [JsonPropertyName("cantidad_inicial")]
    public int CantidadInicial { get; set; }

    [JsonPropertyName("cantidad_actual")]
    public int CantidadActual { get; set; }

    [JsonPropertyName("precio_compra")]
    public decimal PrecioCompra { get; set; }
}

public class VentaRequest
{
    [JsonPropertyName("usuarioId")]
    public Guid UsuarioId { get; set; }

    [JsonPropertyName("clienteId")]
    public long? ClienteId { get; set; }

    [JsonPropertyName("items")]
    [Required]
    public List<VentaItem> Items { get; set; } = new();
}

public class VentaItem
{
    [JsonPropertyName("medicamento_id")]
    public long MedicamentoId { get; set; }

    [JsonPropertyName("cantidad")]
    [Required]
    public int Cantidad { get; set; }
}
