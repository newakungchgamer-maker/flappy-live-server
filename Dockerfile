FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Tools/LicenseServer/LicenseServer.csproj Tools/LicenseServer/
RUN dotnet restore Tools/LicenseServer/LicenseServer.csproj

COPY Tools/LicenseServer/ Tools/LicenseServer/
RUN dotnet publish Tools/LicenseServer/LicenseServer.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "LicenseServer.dll"]
