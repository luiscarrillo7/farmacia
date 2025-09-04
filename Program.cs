using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);

// ============================================================================
// 🔹 CONFIGURACIÓN DE SERVICIOS
// ============================================================================
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            policy.WithOrigins("http://localhost:3000", "http://localhost:5173")
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
        else
        {
            policy.WithOrigins("https://farmacia-frontend-phi.vercel.app")
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
    });
});

// Configuración de JSON
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});

// HttpClient para Supabase
builder.Services.AddHttpClient("Supabase", client =>
{
    var supabaseUrl = Environment.GetEnvironmentVariable("SUPABASE_URL");
    var supabaseKey = Environment.GetEnvironmentVariable("SUPABASE_KEY");
    
    if (string.IsNullOrEmpty(supabaseUrl) || string.IsNullOrEmpty(supabaseKey))
        throw new Exception("⚠️ Variables de entorno SUPABASE_URL o SUPABASE_KEY no configuradas.");
    
    client.BaseAddress = new Uri($"{supabaseUrl}/rest/v1/");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", supabaseKey);
    client.DefaultRequestHeaders.Add("apikey", supabaseKey);
    client.DefaultRequestHeaders.Add("Prefer", "return=representation");
});

// Configuración adicional de servicios
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// ============================================================================
// 🔹 CONFIGURACIÓN DE MIDDLEWARE
// ============================================================================
app.UseCors("AllowFrontend");

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Cliente HTTP desde DI
var httpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();

// ============================================================================
// 🔹 FUNCIONES HELPER
// ============================================================================
static async Task<IResult> HandleSupabaseRequest(Func<HttpClient, Task<HttpResponseMessage>> request, IHttpClientFactory factory)
{
    try
    {
        using var httpClient = factory.CreateClient("Supabase");
        var response = await request(httpClient);
        
        var content = await response.Content.ReadAsStringAsync();
        
        if (!response.IsSuccessStatusCode)
        {
            return Results.Problem(
                detail: content,
                statusCode: (int)response.StatusCode,
                title: $"Error de Supabase: {response.StatusCode}"
            );
        }
        
        return Results.Content(content, "application/json");
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem($"Error de conexión: {ex.Message}", statusCode: 503);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error interno: {ex.Message}", statusCode: 500);
    }
}

static async Task<T?> GetSupabaseData<T>(HttpClient client, string endpoint)
{
    var response = await client.GetAsync(endpoint);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync();
    return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}

static bool IsValidGuid(string? id)
{
    return !string.IsNullOrEmpty(id) && Guid.TryParse(id, out _);
}

static string EscapeForLike(string? input)
{
    if (string.IsNullOrEmpty(input)) return string.Empty;
    return Uri.EscapeDataString(input);
}

// ============================================================================
// 🔹 ENDPOINT RAÍZ - INFORMACIÓN DE LA API
// ============================================================================
app.MapGet("/", () => Results.Ok(new 
{ 
    status = "✅ Farmacia API activa",
    version = "1.0",
    environment = app.Environment.EnvironmentName,
    timestamp = DateTime.UtcNow,
    endpoints = new[]
    {
        "GET /login - Autenticación de usuario",
        "GET /medicamentos - Lista de medicamentos",
        "POST /medicamentos - Crear medicamento",
        "PUT /medicamentos/{id} - Actualizar medicamento",
        "GET /stock - Consultar stock",
        "POST /stock - Agregar stock",
        "GET /clientes - Lista de clientes", 
        "POST /clientes - Crear cliente",
        "PUT /clientes/{id} - Actualizar cliente",
        "GET /proveedores - Lista de proveedores",
        "POST /proveedores - Crear proveedor",
        "GET /ventas - Lista de ventas",
        "GET /ventas/{id} - Detalle de venta",
        "POST /ventas - Registrar venta",
        "GET /movimientos - Movimientos de inventario",
        "POST /movimientos - Registrar movimiento",
        "GET /reportes/ventas-diarias - Reporte de ventas",
        "GET /reportes/medicamentos-populares - Medicamentos más vendidos",
        "GET /reportes/stock-bajo - Stock bajo",
        "GET /health - Estado de la API"
    }
}))
.WithName("GetApiInfo")
.WithOpenApi();

// ============================================================================
// 🔹 AUTENTICACIÓN
// ============================================================================
app.MapPost("/login", async (LoginRequest request) =>
{
    if (!ModelState.IsValid(request))
        return Results.BadRequest("Datos de login inválidos");

    return await HandleSupabaseRequest(async httpClient =>
    {
        var response = await httpClient.GetAsync($"usuarios?dni=eq.{request.Dni}&select=*");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var usuarios = JsonSerializer.Deserialize<List<JsonElement>>(json);
        
        if (usuarios == null || usuarios.Count == 0) 
            return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("Usuario no encontrado", Encoding.UTF8, "text/plain")
            };

        var usuario = usuarios[0];
        var passwordHash = usuario.GetProperty("password_hash").GetString();

        // TODO: Implementar hash real de passwords (bcrypt, etc.)
        if (request.Password != passwordHash) 
            return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("Credenciales incorrectas", Encoding.UTF8, "text/plain")
            };

        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var userData = new
        {
            token,
            nombre = usuario.GetProperty("nombre").GetString(),
            apellido = usuario.GetProperty("apellido").GetString(),
            rol = usuario.GetProperty("rol").GetString(),
            id = usuario.GetProperty("id").GetString()
        };

        var resultJson = JsonSerializer.Serialize(userData);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(resultJson, Encoding.UTF8, "application/json")
        };
    }, httpClientFactory);
})
.WithName("Login")
.WithOpenApi();

// ============================================================================
// 🔹 MEDICAMENTOS
// ============================================================================
app.MapGet("/medicamentos", async (string? categoria, string? search) =>
{
    return await HandleSupabaseRequest(async httpClient =>
    {
        var query = new StringBuilder("medicamentos?select=*,proveedores(nombre)");

        if (!string.IsNullOrEmpty(categoria))
            query.Append($"&categoria=eq.{EscapeForLike(categoria)}");

        if (!string.IsNullOrEmpty(search))
            query.Append($"&or=(nombre_comercial.ilike.*{EscapeForLike(search)}*,nombre_generico.ilike.*{EscapeForLike(search)}*)");

        query.Append("&order=nombre_comercial");
        return await httpClient.GetAsync(query.ToString());
    }, httpClientFactory);
})
.WithName("GetMedicamentos")
.WithOpenApi();

app.MapPost("/medicamentos", async (JsonElement medicamento) =>
{
    return await HandleSupabaseRequest(async httpClient =>
    {
        var content = new StringContent(JsonSerializer.Serialize(medicamento), Encoding.UTF8, "application/json");
        return await httpClient.PostAsync("medicamentos", content);
    }, httpClientFactory);
})
.WithName("CreateMedicamento")
.WithOpenApi();

app.MapPut("/medicamentos/{id:guid}", async (Guid id, JsonElement medicamento) =>
{
    return await HandleSupabaseRequest(async httpClient =>
    {
        var content = new StringContent(JsonSerializer.Serialize(medicamento), Encoding.UTF8, "application/json");
        return await httpClient.PatchAsync($"medicamentos?id=eq.{id}", content);
    }, httpClientFactory);
})
.WithName("UpdateMedicamento")
.WithOpenApi();

// ============================================================================
// 🔹 STOCK
// ============================================================================
app.MapGet("/stock", async (bool? porVencer, int? diasVencimiento) =>
{
    return await HandleSupabaseRequest(async httpClient =>
    {
        var query = new StringBuilder("stock?select=*,medicamentos(nombre_comercial,nombre_generico,presentacion,categoria)&cantidad=gt.0");

        if (porVencer == true)
        {
            var dias = diasVencimiento ?? 90;
            var fechaLimite = DateTime.Now.AddDays(dias).ToString("yyyy-MM-dd");
            query.Append($"&fecha_vencimiento=lte.{fechaLimite}");
        }

        query.Append("&order=fecha_vencimiento.asc");
        return await httpClient.GetAsync(query.ToString());
    }, httpClientFactory);
})
.WithName("GetStock")
.WithOpenApi();

app.MapPost("/stock", async (JsonElement stock) =>
{
    return await HandleSupabaseRequest(async httpClient =>
    {
        var content = new StringContent(JsonSerializer.Serialize(stock), Encoding.UTF8, "application/json");
        return await httpClient.PostAsync("stock", content);
    }, httpClientFactory);
})
.WithName("CreateStock")
.WithOpenApi();

// ============================================================================
// 🔹 CLIENTES
// ============================================================================
app.MapGet("/clientes", async (string? search) =>
{
    return await HandleSupabaseRequest(async httpClient =>
    {
        var query = "clientes?select=*&order=apellido,nombre";
        
        if (!string.IsNullOrEmpty(search))
            query += $"&or=(nombre.ilike.*{EscapeForLike(search)}*,apellido.ilike.*{EscapeForLike(search)}*,dni.ilike.*{EscapeForLike(search)}*)";

        return await httpClient.GetAsync(query);
    }, httpClientFactory);
})
.WithName("GetClientes")
.WithOpenApi();

app.MapPost("/clientes", async (JsonElement cliente) =>
{
    return await HandleSupabaseRequest(async httpClient =>
    {
        var content = new StringContent(JsonSerializer.Serialize(cliente), Encoding.UTF8, "application/json");
        return await httpClient.PostAsync("clientes", content);
    }, httpClientFactory);
})
.WithName("CreateCliente")
.WithOpenApi();

app.MapPut("/clientes/{id:guid}", async (Guid id, JsonElement cliente) =>
{
    return await HandleSupabaseRequest(async httpClient =>
    {
        var content = new StringContent(JsonSerializer.Serialize(cliente), Encoding.UTF8, "application/json");
        return await httpClient.PatchAsync($"clientes?id=eq.{id}", content);
    }, httpClientFactory);
})
.WithName("UpdateCliente")
.WithOpenApi();

// ============================================================================
// 🔹 PROVEEDORES
// ============================================================================
app.MapGet("/proveedores", async () =>
{
    return await HandleSupabaseRequest(async httpClient =>
        await httpClient.GetAsync("proveedores?select=*&order=nombre"), httpClientFactory);
})
.WithName("GetProveedores")
.WithOpenApi();

app.MapPost("/proveedores", async (JsonElement proveedor) =>
{
    return await HandleSupabaseRequest(async httpClient =>
    {
        var content = new StringContent(JsonSerializer.Serialize(proveedor), Encoding.UTF8, "application/json");
        return await httpClient.PostAsync("proveedores", content);
    }, httpClientFactory);
})
.WithName("CreateProveedor")
.WithOpenApi();

// ============================================================================
// 🔹 MOVIMIENTOS DE INVENTARIO
// ============================================================================
app.MapGet("/movimientos", async (DateTime? fechaDesde, DateTime? fechaHasta, string? tipo) =>
{
    return await HandleSupabaseRequest(async httpClient =>
    {
        var query = new StringBuilder("movimientos_stock?select=*,medicamentos(nombre_comercial)&order=fecha_movimiento.desc");

        if (fechaDesde.HasValue)
            query.Append($"&fecha_movimiento=gte.{fechaDesde:yyyy-MM-dd}");
        
        if (fechaHasta.HasValue)
            query.Append($"&fecha_movimiento=lte.{fechaHasta:yyyy-MM-dd}");
        
        if (!string.IsNullOrEmpty(tipo))
            query.Append($"&tipo=eq.{tipo}");

        return await httpClient.GetAsync(query.ToString());
    }, httpClientFactory);
})
.WithName("GetMovimientos")
.WithOpenApi();

app.MapPost("/movimientos", async (MovimientoRequest request) =>
{
    if (!ModelState.IsValid(request))
        return Results.BadRequest("Datos de movimiento inválidos");

    return await HandleSupabaseRequest(async httpClient =>
    {
        // Obtener información del stock
        var stockResponse = await httpClient.GetAsync($"stock?id=eq.{request.StockId}&select=medicamento_id,cantidad");
        stockResponse.EnsureSuccessStatusCode();
        var stockJson = await stockResponse.Content.ReadAsStringAsync();
        var stocks = JsonSerializer.Deserialize<List<JsonElement>>(stockJson);
        
        if (stocks == null || stocks.Count == 0)
            return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            {
                Content = new StringContent("Stock no encontrado", Encoding.UTF8, "text/plain")
            };

        var stock = stocks[0];
        var medicamentoId = stock.GetProperty("medicamento_id").GetString();
        var cantidadActual = stock.GetProperty("cantidad").GetInt32();

        // Validar stock suficiente para salidas
        if (request.Tipo == "SALIDA" && cantidadActual < request.Cantidad)
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            {
                Content = new StringContent($"Stock insuficiente. Disponible: {cantidadActual}, solicitado: {request.Cantidad}", Encoding.UTF8, "text/plain")
            };
        }

        var movimiento = new
        {
            medicamento_id = medicamentoId,
            tipo = request.Tipo,
            cantidad = request.Cantidad,
            motivo = request.Motivo,
            fecha_movimiento = DateTime.UtcNow,
            usuario = "sistema" // TODO: obtener del token de autenticación
        };

        var content = new StringContent(JsonSerializer.Serialize(movimiento), Encoding.UTF8, "application/json");
        return await httpClient.PostAsync("movimientos_stock", content);
    }, httpClientFactory);
})
.WithName("CreateMovimiento")
.WithOpenApi();

// ============================================================================
// 🔹 VENTAS
// ============================================================================
app.MapGet("/ventas", async (DateTime? fechaDesde, DateTime? fechaHasta, int? limit) =>
{
    return await HandleSupabaseRequest(async httpClient =>
    {
        var query = new StringBuilder("ventas?select=*,clientes(nombre,apellido),usuarios(nombre,apellido)&order=fecha.desc");
        
        if (fechaDesde.HasValue)
            query.Append($"&fecha=gte.{fechaDesde:yyyy-MM-dd}");
        
        if (fechaHasta.HasValue)
            query.Append($"&fecha=lte.{fechaHasta:yyyy-MM-dd}");
        
        if (limit.HasValue && limit > 0)
            query.Append($"&limit={Math.Min(limit.Value, 1000)}"); // Límite máximo de seguridad

        return await httpClient.GetAsync(query.ToString());
    }, httpClientFactory);
})
.WithName("GetVentas")
.WithOpenApi();

app.MapGet("/ventas/{id:guid}", async (Guid id) =>
{
    return await HandleSupabaseRequest(async httpClient =>
        await httpClient.GetAsync($"ventas?id=eq.{id}&select=*,clientes(*),usuarios(*),detalle_ventas(*,stock(*,medicamentos(*)))"), 
        httpClientFactory);
})
.WithName("GetVentaById")
.WithOpenApi();

app.MapPost("/ventas", async (VentaRequest request) =>
{
    if (!ModelState.IsValid(request))
        return Results.BadRequest("Datos de venta inválidos");

    if (request.Items == null || !request.Items.Any())
        return Results.BadRequest("La venta debe tener al menos un item");

    using var httpClient = httpClientFactory.CreateClient("Supabase");
    
    try
    {
        // Validar stock antes de procesar
        foreach (var item in request.Items)
        {
            var stockResponse = await httpClient.GetAsync($"stock?id=eq.{item.StockId}&select=cantidad");
            stockResponse.EnsureSuccessStatusCode();
            var stockJson = await stockResponse.Content.ReadAsStringAsync();
            var stocks = JsonSerializer.Deserialize<List<JsonElement>>(stockJson);
            
            if (stocks == null || stocks.Count == 0)
                return Results.BadRequest($"Stock no encontrado: {item.StockId}");
            
            var cantidadDisponible = stocks[0].GetProperty("cantidad").GetInt32();
            if (cantidadDisponible < item.Cantidad)
                return Results.BadRequest($"Stock insuficiente para item {item.StockId}. Disponible: {cantidadDisponible}, solicitado: {item.Cantidad}");
        }

        // Crear venta
        var total = request.Items.Sum(i => i.Cantidad * i.PrecioUnitario);
        var ventaData = new
        {
            cliente_id = request.ClienteId,
            usuario_id = request.UsuarioId,
            total = total,
            fecha = DateTime.UtcNow
        };

        var ventaContent = new StringContent(JsonSerializer.Serialize(ventaData), Encoding.UTF8, "application/json");
        var ventaResponse = await httpClient.PostAsync("ventas", ventaContent);
        ventaResponse.EnsureSuccessStatusCode();

        var ventaJson = await ventaResponse.Content.ReadAsStringAsync();
        var venta = JsonSerializer.Deserialize<List<JsonElement>>(ventaJson)![0];
        var ventaId = venta.GetProperty("id").GetString();

        // Crear detalles de venta
        foreach (var item in request.Items)
        {
            var detalle = new
            {
                venta_id = ventaId,
                stock_id = item.StockId,
                cantidad = item.Cantidad,
                precio_unitario = item.PrecioUnitario
            };

            var detalleContent = new StringContent(JsonSerializer.Serialize(detalle), Encoding.UTF8, "application/json");
            var detalleResponse = await httpClient.PostAsync("detalle_ventas", detalleContent);
            detalleResponse.EnsureSuccessStatusCode();
        }

        return Results.Ok(new 
        { 
            message = "✅ Venta registrada exitosamente", 
            ventaId, 
            total,
            items = request.Items.Count,
            fecha = DateTime.UtcNow
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error al procesar venta: {ex.Message}", statusCode: 500);
    }
})
.WithName("CreateVenta")
.WithOpenApi();

// ============================================================================
// 🔹 REPORTES
// ============================================================================
app.MapGet("/reportes/ventas-diarias", async (int dias = 30) =>
{
    if (dias <= 0 || dias > 365)
        return Results.BadRequest("Los días deben estar entre 1 y 365");

    return await HandleSupabaseRequest(async httpClient =>
    {
        var desde = DateTime.Now.AddDays(-dias).ToString("yyyy-MM-dd");
        
        // Intentar usar la vista optimizada
        var response = await httpClient.GetAsync($"vista_ventas_diarias?fecha=gte.{desde}&order=fecha.desc");
        
        if (!response.IsSuccessStatusCode)
        {
            // Fallback: consulta directa
            response = await httpClient.GetAsync($"ventas?select=fecha,total&fecha=gte.{desde}&order=fecha.desc");
        }
        
        return response;
    }, httpClientFactory);
})
.WithName("GetVentasDiarias")
.WithOpenApi();

app.MapGet("/reportes/medicamentos-populares", async (int dias = 30, int limit = 10) =>
{
    if (dias <= 0 || dias > 365)
        return Results.BadRequest("Los días deben estar entre 1 y 365");
    
    if (limit <= 0 || limit > 100)
        return Results.BadRequest("El límite debe estar entre 1 y 100");

    return await HandleSupabaseRequest(async httpClient =>
    {
        var desde = DateTime.Now.AddDays(-dias).ToString("yyyy-MM-dd");
        var query = $"detalle_ventas?select=cantidad,stock(medicamentos(nombre_comercial)),ventas(fecha)&ventas.fecha=gte.{desde}&limit={limit}";
        return await httpClient.GetAsync(query);
    }, httpClientFactory);
})
.WithName("GetMedicamentosPopulares")
.WithOpenApi();

app.MapGet("/reportes/stock-bajo", async (int minimo = 10) =>
{
    if (minimo < 0)
        return Results.BadRequest("El mínimo no puede ser negativo");

    return await HandleSupabaseRequest(async httpClient =>
    {
        var query = $"stock?select=*,medicamentos(nombre_comercial,presentacion)&cantidad=lt.{minimo}&order=cantidad.asc";
        return await httpClient.GetAsync(query);
    }, httpClientFactory);
})
.WithName("GetStockBajo")
.WithOpenApi();

// ============================================================================
// 🔹 HEALTH CHECK
// ============================================================================
app.MapGet("/health", async () =>
{
    try
    {
        using var httpClient = httpClientFactory.CreateClient("Supabase");
        var response = await httpClient.GetAsync("usuarios?select=count&limit=1");
        
        return Results.Ok(new 
        { 
            status = "healthy",
            database = response.IsSuccessStatusCode ? "connected" : "disconnected",
            timestamp = DateTime.UtcNow,
            version = "1.0"
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Database connection failed: {ex.Message}", statusCode: 503);
    }
})
.WithName("HealthCheck")
.WithOpenApi();

// ============================================================================
// 🔹 INICIAR APLICACIÓN
// ============================================================================
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Add($"http://0.0.0.0:{port}");

Console.WriteLine($"🚀 Farmacia API iniciándose en puerto {port}...");
Console.WriteLine($"🌍 Entorno: {app.Environment.EnvironmentName}");
Console.WriteLine($"⚡ Swagger disponible en: /swagger");

app.Run();

// ============================================================================
// 🔹 MODELOS Y RECORDS (AL FINAL DEL ARCHIVO)
// ============================================================================
public record LoginRequest([Required] string Dni, [Required] string Password);
public record VentaRequest([Required] string UsuarioId, string? ClienteId, [Required] List<VentaItem> Items);
public record VentaItem([Required] string StockId, [Required] int Cantidad, [Required] decimal PrecioUnitario);
public record MovimientoRequest([Required] string StockId, [Required] string Tipo, [Required] int Cantidad, string? Motivo);

// ============================================================================
// 🔹 CLASE HELPER PARA VALIDACIONES
// ============================================================================
public static class ModelState
{
    public static bool IsValid<T>(T model) where T : class
    {
        var validationResults = new List<ValidationResult>();
        var validationContext = new ValidationContext(model);
        return Validator.TryValidateObject(model, validationContext, validationResults, true);
    }
}