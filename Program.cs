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
app.MapGet("/", () => "✅ Minimal API conectada a Supabse!");

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
    var response = await httpClient.GetAsync("proveedores?select=*");
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
        $"stock?medicamento_id=eq.{medicamentoId}&select=*&cantidad=gt.0&order=fecha_vencimiento.asc"
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
    var query = "movimientos_stock?select=*,medicamentos(nombre_comercial,presentacion)";
    
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
    
    // Si es un ingreso, también agregar/actualizar en la tabla stock
    var movimiento = JsonSerializer.Deserialize<JsonElement>(body);
    if (movimiento.GetProperty("tipo").GetString() == "INGRESO")
    {
        var stockData = new
        {
            medicamento_id = movimiento.GetProperty("medicamento_id").GetString(),
            lote = movimiento.GetProperty("lote").GetString(),
            fecha_vencimiento = movimiento.GetProperty("fecha_vencimiento").GetString(),
            cantidad = movimiento.GetProperty("cantidad").GetInt32(),
            precio_unitario = movimiento.TryGetProperty("precio_unitario", out var precio) ? precio.GetDecimal() : 0
        };
        
        var stockContent = new StringContent(JsonSerializer.Serialize(stockData), Encoding.UTF8, "application/json");
        await httpClient.PostAsync("stock", stockContent);
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

/* ===========================================================
   🔹 REPORTES Y VISTAS
   =========================================================== */
// Medicamentos por vencer
app.MapGet("/reportes/por-vencer", async () =>
{
    var response = await httpClient.GetAsync("vista_medicamentos_por_vencer");
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync();
    return Results.Content(json, "application/json");
});

// Ventas diarias
app.MapGet("/reportes/ventas-diarias", async (int dias = 30) =>
{
    var response = await httpClient.GetAsync($"vista_ventas_diarias?fecha=gte.{DateTime.Now.AddDays(-dias):yyyy-MM-dd}");
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync();
    return Results.Content(json, "application/json");
});

// Stock crítico (menos de 10 unidades)
app.MapGet("/reportes/stock-critico", async () =>
{
    var response = await httpClient.GetAsync("stock?select=*,medicamentos(nombre_comercial,presentacion)&cantidad=lt.10&cantidad=gt.0");
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync();
    return Results.Content(json, "application/json");
});

app.Run();