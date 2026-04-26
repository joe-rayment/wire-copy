# syntax=docker/dockerfile:1

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files
COPY ["TermReader.sln", "./"]
COPY ["src/TermReader.Domain/TermReader.Domain.csproj", "src/TermReader.Domain/"]
COPY ["src/TermReader.Application/TermReader.Application.csproj", "src/TermReader.Application/"]
COPY ["src/TermReader.Persistence/TermReader.Persistence.csproj", "src/TermReader.Persistence/"]
COPY ["src/TermReader.Infrastructure/TermReader.Infrastructure.csproj", "src/TermReader.Infrastructure/"]
COPY ["src/TermReader.API/TermReader.API.csproj", "src/TermReader.API/"]

# Restore dependencies
RUN dotnet restore "src/TermReader.API/TermReader.API.csproj"

# Copy all source files
COPY . .

# Build the application
WORKDIR "/src/src/TermReader.API"
RUN dotnet build "TermReader.API.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "TermReader.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage — use Debian Bookworm (default), where Chromium is a real package
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app

# Install Chromium and dependencies for browser automation.
# On Debian Bookworm (the base image), chromium is a real .deb package for all
# architectures — no snap stubs. This works on both amd64 and arm64.
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    chromium \
    xvfb \
    fonts-liberation \
    libasound2 \
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

ENTRYPOINT ["xvfb-run", "-a", "--server-args=-screen 0 1920x1080x24", "dotnet", "TermReader.API.dll"]
