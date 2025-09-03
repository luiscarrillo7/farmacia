using System.Net.Http.Headers;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// 🔹 CORS SOLO para tu frontend en Vercel
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowVercel",
        policy => policy
            .WithOrigins("https://farmacia-frontend-phi.vercel.app") // SOLO este dominio
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

// ✅ Endpoint raíz
app.MapGet("/", () => "✅ Minimal API conectada a Supabase!");

// ✅ Obtener medicamentos
app.MapGet("/medicamentos", async () =>
{
    var response = await httpClient.GetAsync("medicamentos?select=*");
    response.EnsureSuccessStatusCode();

    var json = await response.Content.ReadAsStringAsync();
    var medicamentos = JsonSerializer.Deserialize<object>(json);
    return Results.Ok(medicamentos);
});

// ✅ Obtener stock con filtros opcionales (?min=50&max=200)
app.MapGet("/stock", async (int? min, int? max) =>
{
    var response = await httpClient.GetAsync(
        "stock?select=medicamentos(nombre_comercial,presentacion),cantidad"
    );
    response.EnsureSuccessStatusCode();

    var json = await response.Content.ReadAsStringAsync();
    var registros = JsonSerializer.Deserialize<List<JsonElement>>(json);

    var resultado = registros
        .GroupBy(r => new
        {
            nombre = r.GetProperty("medicamentos").GetProperty("nombre_comercial").GetString(),
            presentacion = r.GetProperty("medicamentos").GetProperty("presentacion").GetString()
        })
        .Select(g => new
        {
            nombre_comercial = g.Key.nombre,
            presentacion = g.Key.presentacion,
            stock_total = g.Sum(x => x.GetProperty("cantidad").GetInt32())
        });

    // 🔹 Filtros opcionales
    if (min.HasValue)
        resultado = resultado.Where(r => r.stock_total >= min.Value);

    if (max.HasValue)
        resultado = resultado.Where(r => r.stock_total <= max.Value);

    return Results.Ok(resultado);
});

app.Run();
