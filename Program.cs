using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// --- SERVICES ---
builder.Services.AddCors(options => {
    options.AddPolicy("AllowFrontend", policy => {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options => {
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});
builder.Services.AddHttpClient("Supabase", client => {
    var supabaseUrl = Environment.GetEnvironmentVariable("SUPABASE_URL");
    var supabaseKey = Environment.GetEnvironmentVariable("SUPABASE_KEY");
    if (string.IsNullOrEmpty(supabaseUrl) || string.IsNullOrEmpty(supabaseKey))
        throw new Exception("Supabase environment variables not configured.");
    client.BaseAddress = new Uri($"{supabaseUrl}/rest/v1/");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", supabaseKey);
    client.DefaultRequestHeaders.Add("apikey", supabaseKey);
    client.DefaultRequestHeaders.Add("Prefer", "return=representation");
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => {
        var supabaseUrl = Environment.GetEnvironmentVariable("SUPABASE_URL");
        options.Authority = $"{supabaseUrl}/auth/v1";
        options.Audience = "authenticated";
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// --- MIDDLEWARE ---
app.UseCors("AllowFrontend");
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) {
    app.UseSwagger();
    app.UseSwaggerUI();
}

// --- ENDPOINTS ---
app.MapGet("/", () => Results.Ok(new { status = "✅ Farmacia API activa" }));

app.MapGet("/me", async (HttpContext httpContext, IHttpClientFactory factory) => {
    var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
    return await HandleSupabaseRequest(http => http.GetAsync($"perfiles?id=eq.{userId}&select=*"), factory);
}).RequireAuthorization();

app.MapGet("/medicamentos", async (IHttpClientFactory factory) => 
    await HandleSupabaseRequest(http => http.GetAsync("medicamentos?select=*&order=nombre"), factory));
app.MapGet("/medicamentos/{id}", async (long id, IHttpClientFactory factory) => 
    await HandleSupabaseRequest(http => http.GetAsync($"medicamentos?id=eq.{id}&select=*"), factory));

// POST - Crear nuevo medicamento
app.MapPost("/medicamentos", async ([FromBody] MedicamentoRequest request, IHttpClientFactory factory) => {
    if (string.IsNullOrWhiteSpace(request.Nombre))
        return Results.BadRequest("El nombre del medicamento es requerido");
    if (request.PrecioCompra <= 0 || request.PrecioVenta <= 0)
        return Results.BadRequest("Los precios deben ser mayores a 0");
    
    return await HandleSupabaseRequest(async http => {
        var payload = new {
            nombre = request.Nombre,
            descripcion = request.Descripcion,
            presentacion = request.Presentacion,
            precio_compra = request.PrecioCompra,
            precio_venta = request.PrecioVenta,
            stock_minimo = request.StockMinimo,
            requiere_receta = request.RequiereReceta
        };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await http.PostAsync("medicamentos", content);
    }, factory);
}).RequireAuthorization();

// PUT - Actualizar medicamento existente
app.MapPut("/medicamentos/{id}", async (long id, [FromBody] MedicamentoRequest request, IHttpClientFactory factory) => {
    if (string.IsNullOrWhiteSpace(request.Nombre))
        return Results.BadRequest("El nombre del medicamento es requerido");
    if (request.PrecioCompra <= 0 || request.PrecioVenta <= 0)
        return Results.BadRequest("Los precios deben ser mayores a 0");
    
    return await HandleSupabaseRequest(async http => {
        var payload = new {
            nombre = request.Nombre,
            descripcion = request.Descripcion,
            presentacion = request.Presentacion,
            precio_compra = request.PrecioCompra,
            precio_venta = request.PrecioVenta,
            stock_minimo = request.StockMinimo,
            requiere_receta = request.RequiereReceta
        };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await http.PatchAsync($"medicamentos?id=eq.{id}", content);
    }, factory);
}).RequireAuthorization();

// DELETE - Eliminar medicamento
app.MapDelete("/medicamentos/{id}", async (long id, IHttpClientFactory factory) =>
{
    return await HandleSupabaseRequest(async http =>
    {
        return await http.DeleteAsync($"medicamentos?id=eq.{id}");
    }, factory);
}).RequireAuthorization();


app.MapGet("/lotes", async (IHttpClientFactory factory) => 
    await HandleSupabaseRequest(http => http.GetAsync("lotes?select=*,medicamentos(*),proveedores(nombre)&cantidad_actual=gt.0&order=fecha_vencimiento.asc"), factory));

app.MapGet("/clientes", async (IHttpClientFactory factory) => 
    await HandleSupabaseRequest(http => http.GetAsync("clientes?select=*&order=apellido,nombre"), factory));

app.MapGet("/proveedores", async (IHttpClientFactory factory) => 
    await HandleSupabaseRequest(http => http.GetAsync("proveedores?select=*&order=nombre"), factory));

app.MapGet("/ventas", async (IHttpClientFactory factory) => 
    await HandleSupabaseRequest(http => http.GetAsync("ventas?select=*,clientes(nombre,apellido,dni),perfiles(nombre,apellido)&order=fecha.desc"), factory));

app.MapPost("/ventas", async ([FromBody] VentaRequest request, IHttpClientFactory factory) => {
    if (request.Items == null || !request.Items.Any())
        return Results.BadRequest("La venta debe tener al menos un item");
    return await HandleSupabaseRequest(async http => {
        var payload = new { p_cliente_id = request.ClienteId, p_usuario_id = request.UsuarioId, p_items = request.Items };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await http.PostAsync("rpc/registrar_venta_y_actualizar_stock", content);
    }, factory);
});

app.Run();

// --- MODELS AND HELPERS (Must be at the very end) ---
async Task<IResult> HandleSupabaseRequest(Func<HttpClient, Task<HttpResponseMessage>> request, IHttpClientFactory factory) {
    try {
        using var httpClient = factory.CreateClient("Supabase");
        var response = await request(httpClient);
        var content = await response.Content.ReadAsStringAsync();
        return !response.IsSuccessStatusCode 
            ? Results.Problem(detail: content, statusCode: (int)response.StatusCode) 
            : Results.Content(content, "application/json");
    } catch (Exception ex) { return Results.Problem(ex.Message); }
}

public class VentaRequest {
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
public class MedicamentoRequest {
    [JsonPropertyName("nombre")]
    [Required]
    [MaxLength(100)]
    public string Nombre { get; set; } = string.Empty;
    
    [JsonPropertyName("descripcion")]
    public string? Descripcion { get; set; }
    
    [JsonPropertyName("presentacion")]
    [MaxLength(50)]
    public string? Presentacion { get; set; }
    
    [JsonPropertyName("precioCompra")]
    [Required]
    [Range(0.01, double.MaxValue, ErrorMessage = "El precio de compra debe ser mayor a 0")]
    public decimal PrecioCompra { get; set; }
    
    [JsonPropertyName("precioVenta")]
    [Required]
    [Range(0.01, double.MaxValue, ErrorMessage = "El precio de venta debe ser mayor a 0")]
    public decimal PrecioVenta { get; set; }
    
    [JsonPropertyName("stockMinimo")]
    [Range(0, int.MaxValue)]
    public int StockMinimo { get; set; } = 0;
    
    [JsonPropertyName("requiereReceta")]
    public bool RequiereReceta { get; set; } = false;
}