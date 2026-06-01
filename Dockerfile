# ── Stage 1: Build ─────────────────────────────────────────────────────────────
FROM node:20-alpine AS css-builder
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY tailwind.config.js postcss.config.js ./
COPY src/SterlingLams.Web/wwwroot/css/input.css ./src/SterlingLams.Web/wwwroot/css/input.css
COPY src/SterlingLams.Web/Views ./src/SterlingLams.Web/Views
COPY src/SterlingLams.Web/Areas ./src/SterlingLams.Web/Areas
RUN npm run build:css

# ── Stage 2: .NET Build ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS dotnet-builder
WORKDIR /src

# Restore NuGet packages (cached layer)
COPY SterlingLams.sln ./
COPY src/SterlingLams.Web/SterlingLams.Web.csproj ./src/SterlingLams.Web/
RUN dotnet restore

# Copy rest and build
COPY . .
# Copy compiled Tailwind CSS from previous stage
COPY --from=css-builder /app/src/SterlingLams.Web/wwwroot/css/app.css ./src/SterlingLams.Web/wwwroot/css/app.css

RUN dotnet publish src/SterlingLams.Web/SterlingLams.Web.csproj \
    -c Release -o /app/publish --no-restore \
    /p:BuildTailwind=false

# ── Stage 3: Runtime ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Non-root user for security
RUN adduser --disabled-password --gecos "" appuser && chown -R appuser /app
USER appuser

COPY --from=dotnet-builder --chown=appuser /app/publish .

# EF Core migrations are run before deploy via:
#   dotnet ef database update --project src/SterlingLams.Web
# or via the startup EnsureCreated (development only).

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "SterlingLams.Web.dll"]
