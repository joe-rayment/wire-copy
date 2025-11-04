# syntax=docker/dockerfile:1

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files
COPY ["NYTAudioScraper.sln", "./"]
COPY ["src/NYTAudioScraper.Domain/NYTAudioScraper.Domain.csproj", "src/NYTAudioScraper.Domain/"]
COPY ["src/NYTAudioScraper.Application/NYTAudioScraper.Application.csproj", "src/NYTAudioScraper.Application/"]
COPY ["src/NYTAudioScraper.Infrastructure/NYTAudioScraper.Infrastructure.csproj", "src/NYTAudioScraper.Infrastructure/"]
COPY ["src/NYTAudioScraper.API/NYTAudioScraper.API.csproj", "src/NYTAudioScraper.API/"]

# Restore dependencies
RUN dotnet restore "src/NYTAudioScraper.API/NYTAudioScraper.API.csproj"

# Copy all source files
COPY . .

# Build the application
WORKDIR "/src/src/NYTAudioScraper.API"
RUN dotnet build "NYTAudioScraper.API.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "NYTAudioScraper.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app

# Install dependencies for browser automation (Chrome/Chromium) and audio processing (FFmpeg)
RUN apt-get update && apt-get install -y \
    ca-certificates \
    chromium \
    chromium-driver \
    xvfb \
    ffmpeg \
    wget \
    gnupg \
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

# Set Chrome path for Selenium
ENV CHROME_BIN=/usr/bin/chromium
ENV CHROMEDRIVER_PATH=/usr/bin/chromedriver

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

ENTRYPOINT ["xvfb-run", "-a", "--server-args=-screen 0 1920x1080x24", "dotnet", "NYTAudioScraper.API.dll"]
