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

    return Results.Ok(new { token, nombre = usuario.GetProperty("nombre").GetString() });
});

/* ===========================================================
   🔹 CRUD MEDICAMENTOS
   =========================================================== */
// GET
app.MapGet("/medicamentos", async () =>
{
    var response = await httpClient.GetAsync("medicamentos?select=*");
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
   🔹 CRUD MOVIMIENTOS DE STOCK
   =========================================================== */
// GET
app.MapGet("/movimientos", async () =>
{
    var response = await httpClient.GetAsync("movimientos_stock?select=*");
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync();
    return Results.Content(json, "application/json");
});

// POST (registrar ingreso o salida)
app.MapPost("/movimientos", async (HttpContext ctx) =>
{
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var content = new StringContent(body, Encoding.UTF8, "application/json");
    var response = await httpClient.PostAsync("movimientos_stock", content);
    var result = await response.Content.ReadAsStringAsync();
    return Results.Content(result, "application/json");
});

/* ===========================================================
   🔹 STOCK AGRUPADO
   =========================================================== */
app.MapGet("/stock", async () =>
{
    var response = await httpClient.GetAsync(
        "movimientos_stock?select=medicamentos(nombre_comercial,presentacion),lote,fecha_vencimiento,cantidad,tipo"
    );
    response.EnsureSuccessStatusCode();

    var json = await response.Content.ReadAsStringAsync();
    var registros = JsonSerializer.Deserialize<List<JsonElement>>(json);

    var resultado = registros!
        .GroupBy(r => new
        {
            nombre = r.GetProperty("medicamentos").GetProperty("nombre_comercial").GetString(),
            presentacion = r.GetProperty("medicamentos").GetProperty("presentacion").GetString(),
            lote = r.GetProperty("lote").GetString(),
            vencimiento = r.GetProperty("fecha_vencimiento").GetDateTime()
        })
        .Select(g => new
        {
            nombre_comercial = g.Key.nombre,
            presentacion = g.Key.presentacion,
            lote = g.Key.lote,
            fecha_vencimiento = g.Key.vencimiento,
            stock_total = g.Sum(x =>
            {
                var tipo = x.GetProperty("tipo").GetString();
                var cantidad = x.GetProperty("cantidad").GetInt32();
                return tipo == "INGRESO" ? cantidad : -cantidad;
            })
        });

    return Results.Ok(resultado);
});

app.Run();
