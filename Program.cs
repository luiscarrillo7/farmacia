using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// 🔹 CORS SOLO para tu frontend en Vercel y localhost (desarrollo)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend",
        policy => policy
            .WithOrigins(
                "https://farmacia-frontend-phi.vercel.app",
                "http://localhost:5173"
            )
            .AllowAnyMethod()
            .AllowAnyHeader());
});

var app = builder.Build();
app.UseCors("AllowFrontend");

// Configuración de Supabase
var supabaseUrl = Environment.GetEnvironmentVariable("SUPABASE_URL");
var supabaseKey = Environment.GetEnvironmentVariable("SUPABASE_KEY");

if (string.IsNullOrEmpty(supabaseUrl) || string.IsNullOrEmpty(supabaseKey))
{
    throw new Exception("⚠️ SUPABASE_URL o SUPABASE_KEY no configurados.");
}

var httpClient = new HttpClient
{
    BaseAddress = new Uri($"{supabaseUrl}/rest/v1/")
};
httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", supabaseKey);
httpClient.DefaultRequestHeaders.Add("apikey", supabaseKey);

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

// ✅ Endpoint raíz
app.MapGet("/", () => Results.Ok(new { status = "✅ Minimal API conectada a Supabase!" }));

/* ===========================================================
   🔹 LOGIN
   =========================================================== */
app.MapPost("/login", async (HttpContext ctx) =>
{
    try
    {
        var body = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(ctx.Request.Body);
        if (body == null || !body.ContainsKey("dni") || !body.ContainsKey("password"))
            return Results.BadRequest(new { error = "DNI y password requeridos" });

        var dni = body["dni"];
        var password = body["password"];

        var response = await httpClient.GetAsync($"usuarios?dni=eq.{dni}&select=*");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var usuarios = JsonSerializer.Deserialize<List<JsonElement>>(json);
        if (usuarios == null || usuarios.Count == 0) return Results.Unauthorized();

        var usuario = usuarios[0];
        var passwordHash = usuario.GetProperty("password_hash").GetString();

        if (password != passwordHash) return Results.Unauthorized();

        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray());

        return Results.Ok(new
        {
            token,
            nombre = usuario.GetProperty("nombre").GetString(),
            apellido = usuario.GetProperty("apellido").GetString(),
            rol = usuario.GetProperty("rol").GetString(),
            id = usuario.GetProperty("id").GetString()
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Error en login: {ex.Message}" });
    }
});

/* ===========================================================
   🔹 CRUD MEDICAMENTOS
   =========================================================== */
app.MapGet("/medicamentos", async (string? categoria = null, string? search = null) =>
{
    try
    {
        var query = new StringBuilder("medicamentos?select=*,proveedores(nombre)");

        if (!string.IsNullOrEmpty(categoria))
            query.Append($"&categoria=eq.{categoria}");

        if (!string.IsNullOrEmpty(search))
            query.Append($"&or=(nombre_comercial.ilike.*{search}*,nombre_generico.ilike.*{search}*)");

        var response = await httpClient.GetAsync(query.ToString());
        response.EnsureSuccessStatusCode();
        return Results.Content(await response.Content.ReadAsStringAsync(), "application/json");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Error en medicamentos: {ex.Message}" });
    }
});

/* ===========================================================
   🔹 STOCK
   =========================================================== */
app.MapGet("/stock", async (bool? porVencer = null) =>
{
    try
    {
        var query = new StringBuilder("stock?select=*,medicamentos(nombre_comercial,nombre_generico,presentacion)&cantidad=gt.0");

        if (porVencer == true)
        {
            var fechaLimite = DateTime.UtcNow.AddDays(90).ToString("yyyy-MM-dd");
            query.Append($"&fecha_vencimiento=lte.{fechaLimite}");
        }

        query.Append("&order=fecha_vencimiento.asc");

        var response = await httpClient.GetAsync(query.ToString());
        response.EnsureSuccessStatusCode();
        return Results.Content(await response.Content.ReadAsStringAsync(), "application/json");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Error en stock: {ex.Message}" });
    }
});

/* ===========================================================
   🔹 CLIENTES
   =========================================================== */
app.MapGet("/clientes", async () =>
{
    try
    {
        var response = await httpClient.GetAsync("clientes?select=*");
        response.EnsureSuccessStatusCode();
        return Results.Content(await response.Content.ReadAsStringAsync(), "application/json");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Error en clientes: {ex.Message}" });
    }
});

/* ===========================================================
   🔹 PROVEEDORES
   =========================================================== */
app.MapGet("/proveedores", async () =>
{
    try
    {
        var response = await httpClient.GetAsync("proveedores?select=*");
        response.EnsureSuccessStatusCode();
        return Results.Content(await response.Content.ReadAsStringAsync(), "application/json");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Error en proveedores: {ex.Message}" });
    }
});

/* ===========================================================
   🔹 MOVIMIENTOS DE INVENTARIO
   =========================================================== */
app.MapPost("/movimientos", async (HttpContext ctx) =>
{
    try
    {
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
        if (body.ValueKind == JsonValueKind.Undefined) return Results.BadRequest(new { error = "JSON inválido" });

        var movimiento = new
        {
            stock_id = body.GetProperty("stock_id").GetString(),
            tipo = body.GetProperty("tipo").GetString(),
            cantidad = body.GetProperty("cantidad").GetInt32(),
            motivo = body.GetProperty("motivo").GetString(),
            fecha = DateTime.UtcNow
        };

        var content = new StringContent(JsonSerializer.Serialize(movimiento), Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "movimientos") { Content = content };
        request.Headers.Add("Prefer", "return=representation");

        var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return Results.Ok(new { message = "✅ Movimiento registrado" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Error en movimientos: {ex.Message}" });
    }
});

/* ===========================================================
   🔹 VENTAS (CABECERA + DETALLE)
   =========================================================== */
app.MapPost("/ventas", async (HttpContext ctx) =>
{
    try
    {
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
        if (body.ValueKind == JsonValueKind.Undefined) return Results.BadRequest(new { error = "JSON inválido" });

        var clienteId = body.TryGetProperty("cliente_id", out var c) ? c.GetString() : null;
        var usuarioId = body.GetProperty("usuario_id").GetString();
        var items = body.GetProperty("items").EnumerateArray();

        // Crear venta con total inicial = 0
        var ventaData = new
        {
            cliente_id = clienteId,
            usuario_id = usuarioId,
            total = 0,
            fecha = DateTime.UtcNow
        };

        var ventaContent = new StringContent(JsonSerializer.Serialize(ventaData), Encoding.UTF8, "application/json");
        var requestVenta = new HttpRequestMessage(HttpMethod.Post, "ventas") { Content = ventaContent };
        requestVenta.Headers.Add("Prefer", "return=representation");

        var ventaResponse = await httpClient.SendAsync(requestVenta);
        ventaResponse.EnsureSuccessStatusCode();

        var ventaJson = await ventaResponse.Content.ReadAsStringAsync();
        var venta = JsonSerializer.Deserialize<List<JsonElement>>(ventaJson)![0];
        var ventaId = venta.GetProperty("id").GetString();

        decimal total = 0;

        foreach (var item in items)
        {
            var detalle = new
            {
                venta_id = ventaId,
                stock_id = item.GetProperty("stock_id").GetString(),
                cantidad = item.GetProperty("cantidad").GetInt32(),
                precio_unitario = item.GetProperty("precio_unitario").GetDecimal()
            };

            total += detalle.cantidad * detalle.precio_unitario;

            var detalleContent = new StringContent(JsonSerializer.Serialize(detalle), Encoding.UTF8, "application/json");
            var requestDetalle = new HttpRequestMessage(HttpMethod.Post, "detalle_ventas") { Content = detalleContent };
            requestDetalle.Headers.Add("Prefer", "return=representation");

            await httpClient.SendAsync(requestDetalle);
        }

        // Actualizar total con PATCH
        var updateData = new { total };
        var updateContent = new StringContent(JsonSerializer.Serialize(updateData), Encoding.UTF8, "application/json");
        var requestUpdate = new HttpRequestMessage(new HttpMethod("PATCH"), $"ventas?id=eq.{ventaId}") { Content = updateContent };
        var updateResponse = await httpClient.SendAsync(requestUpdate);
        updateResponse.EnsureSuccessStatusCode();

        return Results.Ok(new { message = "✅ Venta registrada", ventaId, total });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Error al registrar venta: {ex.Message}" });
    }
});

/* ===========================================================
   🔹 REPORTES
   =========================================================== */
app.MapGet("/reportes/ventas-diarias", async (int dias = 30) =>
{
    try
    {
        var desde = DateTime.UtcNow.AddDays(-dias).ToString("yyyy-MM-dd");
        var response = await httpClient.GetAsync($"vista_ventas_diarias?fecha=gte.{desde}");
        if (!response.IsSuccessStatusCode)
        {
            var fallback = await httpClient.GetAsync($"ventas?select=fecha,total&fecha=gte.{desde}&order=fecha.desc");
            fallback.EnsureSuccessStatusCode();
            return Results.Content(await fallback.Content.ReadAsStringAsync(), "application/json");
        }
        return Results.Content(await response.Content.ReadAsStringAsync(), "application/json");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Error en reporte ventas diarias: {ex.Message}" });
    }
});

app.Run();
