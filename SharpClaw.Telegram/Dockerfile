FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching.
COPY SharpClaw.slnx .
COPY SharpClaw.Telegram/SharpClaw.Telegram.csproj SharpClaw.Telegram/
RUN dotnet restore SharpClaw.Telegram/SharpClaw.Telegram.csproj

# Copy everything and publish.
COPY SharpClaw.Telegram/ SharpClaw.Telegram/
RUN dotnet publish SharpClaw.Telegram/SharpClaw.Telegram.csproj -c Release -o /app --no-restore

# ── Runtime image ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app .

ENTRYPOINT ["dotnet", "SharpClaw.Telegram.dll"]
