# TermReader Setup

## Prerequisites

1. **.NET 9.0 SDK** - [Download here](https://dotnet.microsoft.com/download)
2. **FFmpeg** - For audio processing
   ```bash
   brew install ffmpeg
   ```
3. **Google Chrome** - For web browsing with Selenium fallback
4. **ElevenLabs API Key** (optional) - Sign up at [elevenlabs.io](https://elevenlabs.io) for audio generation

## Configuration

### Step 1: Create Secrets File

1. Copy the example secrets file:
   ```bash
   cp secrets.json.example secrets.json
   ```

2. Edit `secrets.json` with your credentials:
   ```json
   {
     "Auth": {
       "Email": "your-email@example.com",
       "Password": "your-password"
     },
     "ElevenLabs": {
       "ApiKey": "your-elevenlabs-api-key"
     }
   }
   ```

**Note**: The `secrets.json` file is already in `.gitignore` and will NOT be committed to the repository.

### Step 2: Install Dependencies

```bash
dotnet restore
```

### Step 3: Test the Application

Run the terminal browser:
```bash
dotnet run --project src/TermReader.API/TermReader.API.csproj -- browse https://example.com
```

Run in test mode (uses mock data, doesn't require credentials):
```bash
dotnet run --project src/TermReader.API/TermReader.API.csproj -- --test
```

## Usage

### Basic Commands

```bash
# Show help
dotnet run --project src/TermReader.API/TermReader.API.csproj -- --help

# Browse a website
dotnet run --project src/TermReader.API/TermReader.API.csproj -- browse https://news.ycombinator.com

# Test mode with mock data
dotnet run --project src/TermReader.API/TermReader.API.csproj -- --test

# Scrape 3 articles
dotnet run --project src/TermReader.API/TermReader.API.csproj -- --count 3

# Scrape with custom budget
dotnet run --project src/TermReader.API/TermReader.API.csproj -- --count 5 --budget 10

# Scrape without logging in (public articles only)
dotnet run --project src/TermReader.API/TermReader.API.csproj -- --count 2 --skip-login

# Use custom voice
dotnet run --project src/TermReader.API/TermReader.API.csproj -- --count 3 --voice "your-voice-id"

# Custom output directory
dotnet run --project src/TermReader.API/TermReader.API.csproj -- --count 3 --output "~/Audiobooks"
```

### Command Line Options

- `-u, --url` - Specific article URL to process
- `-s, --section` - Section to scrape (e.g., technology, politics)
- `-c, --count` - Number of articles to process (default: 5)
- `-v, --voice` - ElevenLabs voice ID to use
- `-b, --budget` - Maximum budget in dollars (default: 5.0)
- `-o, --output` - Output directory path
- `--skip-login` - Skip login (only scrape public articles)
- `--test` - Run with test/mock data
- `--help` - Display help

## Output

The application creates M4B audiobook files in the `output/` directory with:
- Multiple articles combined into a single audiobook
- Chapter markers for each article
- AAC audio codec
- Proper metadata

## Troubleshooting

### ChromeDriver Version Mismatch

If you see "ChromeDriver only supports Chrome version X", the application will automatically use mock data. Selenium Manager will download the correct ChromeDriver version on the next run.

### ElevenLabs API Errors

- **401 Unauthorized**: Check that your API key is correct in `secrets.json`
- **Rate Limiting**: Reduce the number of articles or increase delays
- **Budget Exceeded**: Increase the `--budget` parameter

### Login Fails

- Verify credentials in `secrets.json`
- Try running with `--skip-login` to test with public articles
- Check if the target site's login page structure has changed

### FFmpeg Not Found

```bash
brew install ffmpeg
```

## Alternative Configuration Methods

### Environment Variables

You can also set configuration via environment variables:

```bash
export Auth__Email="your-email@example.com"
export Auth__Password="your-password"
export ElevenLabs__ApiKey="your-api-key"
```

### .NET User Secrets (Original Method)

```bash
dotnet user-secrets set "Auth:Email" "your-email@example.com" --project src/TermReader.API/TermReader.API.csproj
dotnet user-secrets set "Auth:Password" "your-password" --project src/TermReader.API/TermReader.API.csproj
dotnet user-secrets set "ElevenLabs:ApiKey" "your-api-key" --project src/TermReader.API/TermReader.API.csproj
```

## Configuration Priority

The application loads configuration in this order (later sources override earlier ones):

1. `appsettings.json` - Default settings
2. Environment variables
3. `secrets.json` - Your personal secrets file
4. .NET User Secrets - Alternative secrets storage

## Security

- `secrets.json` is in `.gitignore` and will not be committed
- `secrets.json.example` is a template with no real credentials
- Never commit real credentials to the repository
- Keep your `secrets.json` file secure and local only
