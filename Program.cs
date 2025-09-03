using System.Net.Http.Headers;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Configuración
var supabaseUrl = Environment.GetEnvironmentVariable("SUPABASE_URL");
var supabaseKey = Environment.GetEnvironmentVariable("SUPABASE_KEY");

var httpClient = new HttpClient();
httpClient.BaseAddress = new Uri($"{supabaseUrl}/rest/v1/");
httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", supabaseKey);
httpClient.DefaultRequestHeaders.Add("apikey", supabaseKey);

// Endpoint raíz
app.MapGet("/", () => "✅ Minimal API conectada a Supabase!");

// Ejemplo: obtener medicamentos
app.MapGet("/medicamentos", async () =>
{
    var response = await httpClient.GetAsync("medicamentos?select=*");
    response.EnsureSuccessStatusCode();

    var json = await response.Content.ReadAsStringAsync();
    var medicamentos = JsonSerializer.Deserialize<object>(json);
    return Results.Ok(medicamentos);
});
app.MapGet("/stock", async () =>
{
    var response = await httpClient.GetAsync(
        "stock?select=medicamentos(nombre_comercial,presentacion),cantidad"
    );
    response.EnsureSuccessStatusCode();

    var json = await response.Content.ReadAsStringAsync();
    var registros = JsonSerializer.Deserialize<List<JsonElement>>(json);

    // Agrupar stock por medicamento
    var resultado = registros
        .GroupBy(r => new {
            nombre = r.GetProperty("medicamentos").GetProperty("nombre_comercial").GetString(),
            presentacion = r.GetProperty("medicamentos").GetProperty("presentacion").GetString()
        })
        .Select(g => new {
            nombre_comercial = g.Key.nombre,
            presentacion = g.Key.presentacion,
            stock_total = g.Sum(x => x.GetProperty("cantidad").GetInt32())
        });

    return Results.Ok(resultado);
});

app.Run();
