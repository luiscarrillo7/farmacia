# Etapa de build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiamos primero el csproj para restaurar dependencias
COPY farmacia.csproj ./
RUN dotnet restore farmacia.csproj

# Copiamos el resto de archivos
COPY . ./

# Compilamos y publicamos el proyecto principal
RUN dotnet publish farmacia.csproj -c Release -o /app

# Etapa de runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "farmacia.dll"]
