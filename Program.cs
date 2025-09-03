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

app.Run();
