FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching.
COPY SharpClaw.slnx .
COPY SharpClaw.Core/SharpClaw.Core.csproj SharpClaw.Core/
COPY SharpClaw.Copilot/SharpClaw.Copilot.csproj SharpClaw.Copilot/
COPY SharpClaw.Api/SharpClaw.Api.csproj SharpClaw.Api/
RUN dotnet restore SharpClaw.Api/SharpClaw.Api.csproj

# Copy everything and publish.
COPY . .
RUN dotnet publish SharpClaw.Api/SharpClaw.Api.csproj -c Release -o /app --no-restore

# ── Runtime image ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install Node.js (needed for MCP servers that use npx).
RUN apt-get update && apt-get install -y --no-install-recommends nodejs npm && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "SharpClaw.Api.dll"]
