using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// 🔹 CORS SOLO para tu frontend en Vercel
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowVercel",
        policy => policy
            .WithOrigins("https://farmacia-frontend-phi.vercel.app") // tu dominio de Vercel
            .AllowAnyMethod()
            .AllowAnyHeader());
});

var app = builder.Build();
app.UseCors("AllowVercel");

// Configuración de Supabase
var supabaseUrl = Environment.GetEnvironmentVariable("SUPABASE_URL");
var supabaseKey = Environment.GetEnvironmentVariable("SUPABASE_KEY");

var httpClient = new HttpClient();
httpClient.BaseAddress = new Uri($"{supabaseUrl}/rest/v1/");
httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", supabaseKey);
httpClient.DefaultRequestHeaders.Add("apikey", supabaseKey);

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

// ✅ Endpoint raíz
app.MapGet("/", () => "✅ Minimal API conectada a Supabase!");

/* ===========================================================
   🔹 LOGIN
   =========================================================== */
app.MapPost("/login", async (HttpContext ctx) =>
{
    var body = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(ctx.Request.Body);
    if (body == null || !body.ContainsKey("dni") || !body.ContainsKey("password"))
        return Results.BadRequest(new { message = "DNI y password requeridos" });

    var dni = body["dni"];
    var password = body["password"];

    // Buscar usuario por DNI
    var response = await httpClient.GetAsync($"usuarios?dni=eq.{dni}&select=*");
    if (!response.IsSuccessStatusCode) return Results.Unauthorized();

    var json = await response.Content.ReadAsStringAsync();
    var usuarios = JsonSerializer.Deserialize<List<JsonElement>>(json);
    if (usuarios == null || usuarios.Count == 0) return Results.Unauthorized();

    var usuario = usuarios[0];
    var passwordHash = usuario.GetProperty("password_hash").GetString();

    // ⚠️ Aquí deberías usar hashing real (ej: BCrypt)
    if (password != passwordHash) return Results.Unauthorized();

    // Generar token básico (puedes cambiarlo a JWT luego)
    var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray());

    return Results.Ok(new { 
        token, 
        nombre = usuario.GetProperty("nombre").GetString(),
        apellido = usuario.GetProperty("apellido").GetString(),
        rol = usuario.GetProperty("rol").GetString(),
        id = usuario.GetProperty("id").GetString()
    });
});

/* ===========================================================
   🔹 CRUD MEDICAMENTOS
   =========================================================== */
// GET con filtros opcionales
app.MapGet("/medicamentos", async (string? categoria = null, string? search = null) =>
{
    var query = "medicamentos?select=*,proveedores(nombre)";
    
    if (!string.IsNullOrEmpty(categoria))
        query += $"&categoria=eq.{categoria}";
    
    if (!string.IsNullOrEmpty(search))
        query += $"&or=(nombre_comercial.ilike.*{search}*,nombre_generico.ilike.*{search}*)";

    var response = await httpClient.GetAsync(query);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync();
    return Results.Content(json, "application/json");
});

// GET por ID
app.MapGet("/medicamentos/{id}", async (Guid id) =>
{
    var response = await httpClient.GetAsync($"medicamentos?id=eq.{id}&select=*,proveedores(nombre)");
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync();
    return Results.Content(json, "application/json");
});

// POST
app.MapPost("/medicamentos", async (HttpContext ctx) =>
{
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var content = new StringContent(body, Encoding.UTF8, "application/json");
    var response = await httpClient.PostAsync("medicamentos", content);
    var result = await response.Content.ReadAsStringAsync();
    return Results.Content(result, "application/json");
});

// PUT
app.MapPut("/medicamentos/{id}", async (Guid id, HttpContext ctx) =>
{
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var content = new StringContent(body, Encoding.UTF8, "application/json");
    var response = await httpClient.PatchAsync($"medicamentos?id=eq.{id}", content);
    var result = await response.Content.ReadAsStringAsync();
    return Results.Content(result, "application/json");
});

// DELETE
app.MapDelete("/medicamentos/{id}", async (Guid id) =>
{
    var response = await httpClient.DeleteAsync($"medicamentos?id=eq.{id}");
    var result = await response.Content.ReadAsStringAsync();
    return Results.Content(result, "application/json");
});

/* ===========================================================
   🔹 PROVEEDORES
   =========================================================== */
app.MapGet("/proveedores", async () =>
{
    var response = await httpClient.GetAsync("proveedores?select=*&order=nombre.asc");
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync();
    return Results.Content(json, "application/json");
});

app.MapPost("/proveedores", async (HttpContext ctx) =>
{
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var content = new StringContent(body, Encoding.UTF8, "application/json");
    var response = await httpClient.PostAsync("proveedores", content);
    var result = await response.Content.ReadAsStringAsync();
    return Results.Content(result, "application/json");
});

app.MapPut("/proveedores/{id}", async (Guid id, HttpContext ctx) =>
{
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var content = new StringContent(body, Encoding.UTF8, "application/json");
    var response = await httpClient.PatchAsync($"proveedores?id=eq.{id}", content);
    var result = await response.Content.ReadAsStringAsync();
    return Results.Content(result, "application/json");
});

app.MapDelete("/proveedores/{id}", async (Guid id) =>
{
    var response = await httpClient.DeleteAsync($"proveedores?id=eq.{id}");
    var result = await response.Content.ReadAsStringAsync();
    return Results.Content(result, "application/json");
});

/* ===========================================================
   🔹 STOCK REAL (tabla stock)
   =========================================================== */
app.MapGet("/stock", async (bool? porVencer = null) =>
{
    var query = "stock?select=*,medicamentos(nombre_comercial,nombre_generico,presentacion)&cantidad=gt.0";
    
    // Filtro para medicamentos por vencer (próximos 90 días)
    if (porVencer == true)
    {
        var fechaLimite = DateTime.Now.AddDays(90).ToString("yyyy-MM-dd");
        query += $"&fecha_vencimiento=lte.{fechaLimite}";
    }
    
    query += "&order=fecha_vencimiento.asc";

    var response = await httpClient.GetAsync(query);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync();
    return Results.Content(json, "application/json");
});

// Stock por medicamento específico
app.MapGet("/stock/medicamento/{medicamentoId}", async (Guid medicamentoId) =>
{
    var response = await httpClient.GetAsync(
        $"stock?medicamento_id=eq.{medicamentoId}&select=*,medicamentos(nombre_comercial,presentacion)&cantidad=gt.0&order=fecha_vencimiento.asc"
    );
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync();
    return Results.Content(json, "application/json");
});

// Ingreso de stock
app.MapPost("/stock", async (HttpContext ctx) =>
{
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var content = new StringContent(body, Encoding.UTF8, "application/json");
    var response = await httpClient.PostAsync("stock", content);
    var result = await response.Content.ReadAsStringAsync();
    return Results.Content(result, "application/json");
});

/* ===========================================================
   🔹 MOVIMIENTOS DE STOCK (historial)
   =========================================================== */
app.MapGet("/movimientos", async (string? tipo = null) =>
{
    var query = "movimientos_stock?select=*,medicamentos(nombre_comercial,presentacion),proveedores(nombre)";
    
    if (!string.IsNullOrEmpty(tipo))
        query += $"&tipo=eq.{tipo}";
    
    query += "&order=fecha_movimiento.desc";

    var response = await httpClient.GetAsync(query);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync();
    return Results.Content(json, "application/json");
});

app.MapPost("/movimientos", async (HttpContext ctx) =>
{
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var content = new StringContent(body, Encoding.UTF8, "application/json");
    
    // Registrar el movimiento
    var response = await httpClient.PostAsync("movimientos_stock", content);
    var result = await response.Content.ReadAsStringAsync();
    
    // 🔧 FIX: Solo actualizar tabla stock para INGRESOS
    var movimiento = JsonSerializer.Deserialize<JsonElement>(body);
    if (movimiento.GetProperty("tipo").GetString() == "INGRESO")
    {
        // Verificar si ya existe stock para este medicamento/lote
        var medicamentoId = movimiento.GetProperty("medicamento_id").GetString();
        var lote = movimiento.GetProperty("lote").GetString();
        
        var existingStockResponse = await httpClient.GetAsync($"stock?medicamento_id=eq.{medicamentoId}&lote=eq.{lote}");
        var existingStockJson = await existingStockResponse.Content.ReadAsStringAsync();
        var existingStock = JsonSerializer.Deserialize<List<JsonElement>>(existingStockJson);
        
        if (existingStock != null && existingStock.Count > 0)
        {
            // Actualizar stock existente
            var stockId = existingStock[0].GetProperty("id").GetString();
            var cantidadActual = existingStock[0].GetProperty("cantidad").GetInt32();
            var nuevaCantidad = cantidadActual + movimiento.GetProperty("cantidad").GetInt32();
            
            var updateData = new { cantidad = nuevaCantidad };
            var updateContent = new StringContent(JsonSerializer.Serialize(updateData), Encoding.UTF8, "application/json");
            await httpClient.PatchAsync($"stock?id=eq.{stockId}", updateContent);
        }
        else
        {
            // Crear nuevo registro de stock
            var stockData = new
            {
                medicamento_id = medicamentoId,
                lote = lote,
                fecha_vencimiento = movimiento.GetProperty("fecha_vencimiento").GetString(),
                cantidad = movimiento.GetProperty("cantidad").GetInt32(),
                precio_unitario = movimiento.TryGetProperty("precio_unitario", out var precio) ? precio.GetDecimal() : 0
            };
            
            var stockContent = new StringContent(JsonSerializer.Serialize(stockData), Encoding.UTF8, "application/json");
            await httpClient.PostAsync("stock", stockContent);
        }
    }
    
    return Results.Content(result, "application/json");
});

/* ===========================================================
   🔹 CLIENTES
   =========================================================== */
app.MapGet("/clientes", async (string? search = null) =>
{
    var query = "clientes?select=*";
    
    if (!string.IsNullOrEmpty(search))
        query += $"&or=(nombre.ilike.*{search}*,apellido.ilike.*{search}*,dni.ilike.*{search}*)";
    
    query += "&order=nombre.asc";

    var response = await httpClient.GetAsync(query);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync();
    return Results.Content(json, "application/json");
});

app.MapPost("/clientes", async (HttpContext ctx) =>
{
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var content = new StringContent(body, Encoding.UTF8, "application/json");
    var response = await httpClient.PostAsync("clientes", content);
    var result = await response.Content.ReadAsStringAsync();
    return Results.Content(result, "application/json");
});

app.MapPut("/clientes/{id}", async (Guid id, HttpContext ctx) =>
{
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var content = new StringContent(body, Encoding.UTF8, "application/json");
    var response = await httpClient.PatchAsync($"clientes?id=eq.{id}", content);
    var result = await response.Content.ReadAsStringAsync();
    return Results.Content(result, "application/json");
});

app.MapDelete("/clientes/{id}", async (Guid id) =>
{
    var response = await httpClient.DeleteAsync($"clientes?id=eq.{id}");
    var result = await response.Content.ReadAsStringAsync();
    return Results.Content(result, "application/json");
});

/* ===========================================================
   🔹 VENTAS
   =========================================================== */
app.MapGet("/ventas", async (string? fecha = null) =>
{
    var query = "ventas?select=*,clientes(nombre,apellido,dni),usuarios(nombre,apellido),detalle_ventas(cantidad,precio_unitario,subtotal,stock(medicamentos(nombre_comercial)))";
    
    if (!string.IsNullOrEmpty(fecha))
        query += $"&fecha=gte.{fecha}&fecha=lt.{DateTime.Parse(fecha).AddDays(1):yyyy-MM-dd}";
    
    query += "&order=fecha.desc";

    var response = await httpClient.GetAsync(query);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync();
    return Results.Content(json, "application/json");
});

app.MapPost("/ventas", async (HttpContext ctx) =>
{
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var content = new StringContent(body, Encoding.UTF8, "application/json");
    var response = await httpClient.PostAsync("ventas", content);
    var result = await response.Content.ReadAsStringAsync();
    return Results.Content(result, "application/json");
});

// POST detalle de venta (los triggers se encargan de descontar stock automáticamente)
app.MapPost("/detalle-ventas", async (HttpContext ctx) =>
{
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var content = new StringContent(body, Encoding.UTF8, "application/json");
    var response = await httpClient.PostAsync("detalle_ventas", content);
    var result = await response.Content.ReadAsStringAsync();
    return Results.Content(result, "application/json");
});

/* ===========================================================
   🔹 REPORTES Y VISTAS
   =========================================================== */
// Medicamentos por vencer
app.MapGet("/reportes/por-vencer", async () =>
{
    var response = await httpClient.GetAsync("vista_medicamentos_por_vencer");
    if (response.IsSuccessStatusCode)
    {
        var json = await response.Content.ReadAsStringAsync();
        return Results.Content(json, "application/json");
    }
    
    // Fallback si la vista no existe
    var fechaLimite = DateTime.Now.AddDays(90).ToString("yyyy-MM-dd");
    var fallbackResponse = await httpClient.GetAsync($"stock?select=*,medicamentos(nombre_comercial,presentacion)&fecha_vencimiento=lte.{fechaLimite}&cantidad=gt.0&order=fecha_vencimiento.asc");
    var fallbackJson = await fallbackResponse.Content.ReadAsStringAsync();
    return Results.Content(fallbackJson, "application/json");
});

// Ventas diarias
app.MapGet("/reportes/ventas-diarias", async (int dias = 30) =>
{
    var response = await httpClient.GetAsync($"vista_ventas_diarias?fecha=gte.{DateTime.Now.AddDays(-dias):yyyy-MM-dd}");
    if (response.IsSuccessStatusCode)
    {
        var json = await response.Content.ReadAsStringAsync();
        return Results.Content(json, "application/json");
    }
    
    // Fallback si la vista no existe
    var fechaInicio = DateTime.Now.AddDays(-dias).ToString("yyyy-MM-dd");
    var fallbackResponse = await httpClient.GetAsync($"ventas?select=fecha,total&fecha=gte.{fechaInicio}&order=fecha.desc");
    var fallbackJson = await fallbackResponse.Content.ReadAsStringAsync();
    return Results.Content(fallbackJson, "application/json");
});

// Stock crítico (menos de 10 unidades)
app.MapGet("/reportes/stock-critico", async () =>
{
    var response = await httpClient.GetAsync("stock?select=*,medicamentos(nombre_comercial,presentacion)&cantidad=lt.10&cantidad=gt.0&order=cantidad.asc");
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync();
    return Results.Content(json, "application/json");
});

// Resumen del dashboard
app.MapGet("/reportes/dashboard", async () =>
{
    try 
    {
        // Total medicamentos
        var medResponse = await httpClient.GetAsync("medicamentos?select=count");
        var totalMeds = 0;
        if (medResponse.IsSuccessStatusCode)
        {
            var medJson = await medResponse.Content.ReadAsStringAsync();
            var medData = JsonSerializer.Deserialize<List<JsonElement>>(medJson);
            if (medData != null && medData.Count > 0)
                totalMeds = medData[0].GetProperty("count").GetInt32();
        }
        
        // Stock crítico
        var stockCriticoResponse = await httpClient.GetAsync("stock?select=count&cantidad=lt.10&cantidad=gt.0");
        var stockCritico = 0;
        if (stockCriticoResponse.IsSuccessStatusCode)
        {
            var stockJson = await stockCriticoResponse.Content.ReadAsStringAsync();
            var stockData = JsonSerializer.Deserialize<List<JsonElement>>(stockJson);
            if (stockData != null && stockData.Count > 0)
                stockCritico = stockData[0].GetProperty("count").GetInt32();
        }
        
        // Medicamentos por vencer (próximos 90 días)
        var fechaLimite = DateTime.Now.AddDays(90).ToString("yyyy-MM-dd");
        var porVencerResponse = await httpClient.GetAsync($"stock?select=count&fecha_vencimiento=lte.{fechaLimite}&cantidad=gt.0");
        var porVencer = 0;
        if (porVencerResponse.IsSuccessStatusCode)
        {
            var vencerJson = await porVencerResponse.Content.ReadAsStringAsync();
            var vencerData = JsonSerializer.Deserialize<List<JsonElement>>(vencerJson);
            if (vencerData != null && vencerData.Count > 0)
                porVencer = vencerData[0].GetProperty("count").GetInt32();
        }
        
        var resumen = new
        {
            total_medicamentos = totalMeds,
            stock_critico = stockCritico,
            por_vencer = porVencer,
            fecha_actualizacion = DateTime.Now
        };
        
        return Results.Ok(resumen);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error al generar reporte: {ex.Message}");
    }
});

app.Run();