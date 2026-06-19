# syntax=docker/dockerfile:1

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files
COPY ["WireCopy.sln", "./"]
COPY ["src/WireCopy.Domain/WireCopy.Domain.csproj", "src/WireCopy.Domain/"]
COPY ["src/WireCopy.Application/WireCopy.Application.csproj", "src/WireCopy.Application/"]
COPY ["src/WireCopy.Persistence/WireCopy.Persistence.csproj", "src/WireCopy.Persistence/"]
COPY ["src/WireCopy.Infrastructure/WireCopy.Infrastructure.csproj", "src/WireCopy.Infrastructure/"]
COPY ["src/WireCopy.API/WireCopy.API.csproj", "src/WireCopy.API/"]
COPY ["src/WireCopy.Web/WireCopy.Web.csproj", "src/WireCopy.Web/"]

# Restore dependencies
RUN dotnet restore "src/WireCopy.API/WireCopy.API.csproj"

# Copy all source files
COPY . .

# Build the application
WORKDIR "/src/src/WireCopy.API"
RUN dotnet build "WireCopy.API.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "WireCopy.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage — use Debian Bookworm (default), where Chromium is a real package
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

# Install Chromium and dependencies for browser automation.
# On Debian Bookworm (the base image), chromium is a real .deb package for all
# architectures — no snap stubs. This works on both amd64 and arm64.
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    chromium \
    xvfb \
    fonts-liberation \
    libasound2t64 \
    libatk-bridge2.0-0 \
    libatk1.0-0 \
    libatspi2.0-0 \
    libcups2 \
    libdbus-1-3 \
    libdrm2 \
    libgbm1 \
    libgtk-3-0 \
    libnspr4 \
    libnss3 \
    libwayland-client0 \
    libxcomposite1 \
    libxdamage1 \
    libxfixes3 \
    libxkbcommon0 \
    libxrandr2 \
    xdg-utils \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user
RUN useradd -m -u 1000 appuser && \
    mkdir -p /app/output /app/logs /app/cookies && \
    chown -R appuser:appuser /app

# Copy published app
COPY --from=publish --chown=appuser:appuser /app/publish .

# Switch to non-root user
USER appuser

# Create volume mount points
VOLUME ["/app/output", "/app/logs"]

# Set environment variables
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DISPLAY=:99

ENTRYPOINT ["xvfb-run", "-a", "--server-args=-screen 0 1920x1080x24", "dotnet", "WireCopy.API.dll"]

# ---------------------------------------------------------------------------
# Browser-hosted shell image (the "Cloud Shell" web pane). Build with:
#   docker build --target final-web -t wirecopy-web .
# then browse to http://localhost:5099. The web host spawns the unmodified
# WireCopy.API under a PTY (terminal → xterm.js) and streams its browser into
# the web pane. Chromium runs HEADFUL under Xvfb (DISPLAY=:99) in the container
# (workspace-8ne3) — a real browser defeats the bot detection that blocks headless,
# and the virtual framebuffer keeps it a single window streamed to the pane.
# ---------------------------------------------------------------------------
FROM build AS publish-web
WORKDIR "/src/src/WireCopy.Web"
RUN dotnet publish "WireCopy.Web.csproj" -c Release -o /app/publish-web /p:UseAppHost=false
WORKDIR "/src/src/WireCopy.API"
RUN dotnet publish "WireCopy.API.csproj" -c Release -o /app/publish-web /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final-web
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates chromium xvfb fonts-liberation \
    libasound2t64 libatk-bridge2.0-0 libatk1.0-0 libatspi2.0-0 libcups2 \
    libdbus-1-3 libdrm2 libgbm1 libgtk-3-0 libnspr4 libnss3 libwayland-client0 \
    libxcomposite1 libxdamage1 libxfixes3 libxkbcommon0 libxrandr2 xdg-utils \
    && rm -rf /var/lib/apt/lists/*

RUN useradd -m -u 1000 appuser && \
    mkdir -p /app/output /app/logs /app/cookies && \
    chown -R appuser:appuser /app

COPY --from=publish-web --chown=appuser:appuser /app/publish-web .

USER appuser
VOLUME ["/app/output", "/app/logs"]
EXPOSE 5099

ENV ASPNETCORE_ENVIRONMENT=Production
ENV DISPLAY=:99
# The host listens on all interfaces so the port is reachable from the host machine; the API child is
# spawned via `dotnet WireCopy.API.dll`. No GUI in the container, so don't try to auto-open a browser.
ENV WIRECOPY_WEB_URL=http://0.0.0.0:5099
ENV WIRECOPY_TERMINAL_APP=dotnet
ENV WIRECOPY_TERMINAL_ARGS=/app/WireCopy.API.dll
ENV WIRECOPY_TERMINAL_CWD=/app
ENV WIRECOPY_NO_OPEN=1

ENTRYPOINT ["xvfb-run", "-a", "--server-args=-screen 0 1920x1080x24", "dotnet", "WireCopy.Web.dll"]
