var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Variable de entorno ejemplo
var saludo = Environment.GetEnvironmentVariable("SALUDO") ?? "Hola desde ASP.NET en Cloud Run 🚀";

app.MapGet("/", () => saludo);

app.Run();