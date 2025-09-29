# ---- Stage 1: Build ----
# Usa la imagen del SDK de .NET 8 para compilar el proyecto
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copia el archivo .csproj y restaura las dependencias primero
# Esto aprovecha el caché de Docker para acelerar futuras compilaciones
COPY ["FarmaciaApiFinal.csproj", "."]
RUN dotnet restore "./FarmaciaApiFinal.csproj"

# Copia el resto de los archivos del proyecto y compila
COPY . .
WORKDIR "/src/."
RUN dotnet build "FarmaciaApiFinal.csproj" -c Release -o /app/build

# Publica la aplicación para crear los artefactos de producción
FROM build AS publish
RUN dotnet publish "FarmaciaApiFinal.csproj" -c Release -o /app/publish /p:UseAppHost=false

# ---- Stage 2: Final ----
# Usa la imagen de ASP.NET mucho más ligera para ejecutar la aplicación
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Expone el puerto 8080 (Cloud Run usará esta variable de entorno)
ENV PORT=8080
EXPOSE 8080

# Copia los archivos publicados de la etapa anterior
COPY --from=publish /app/publish .

# Establece el comando de entrada para ejecutar la API
ENTRYPOINT ["dotnet", "FarmaciaApiFinal.dll"]