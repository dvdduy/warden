FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Only the three projects Warden.ControlPlane.Api actually depends on -- copying and
# restoring the whole solution (Warden.Agent, Warden.Demo, every test project) would work
# but builds an image slower than necessary for no benefit. csproj files first, on their
# own layer, so `docker build` only re-restores when a project reference or package
# actually changes, not on every source edit.
COPY NuGet.Config ./
COPY src/Warden.Core/Warden.Core.csproj src/Warden.Core/
COPY src/Warden.ControlPlane/Warden.ControlPlane.csproj src/Warden.ControlPlane/
COPY src/Warden.ControlPlane.Api/Warden.ControlPlane.Api.csproj src/Warden.ControlPlane.Api/
RUN dotnet restore src/Warden.ControlPlane.Api/Warden.ControlPlane.Api.csproj

COPY src/Warden.Core/ src/Warden.Core/
COPY src/Warden.ControlPlane/ src/Warden.ControlPlane/
COPY src/Warden.ControlPlane.Api/ src/Warden.ControlPlane.Api/

RUN dotnet publish src/Warden.ControlPlane.Api/Warden.ControlPlane.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Warden.ControlPlane.Api.dll"]
