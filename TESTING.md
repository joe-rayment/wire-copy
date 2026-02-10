# Testing Guide

This document explains how to run tests independently in the NYT Audio Scraper project.

## Prerequisites

- .NET 9.0 SDK installed
- All dependencies restored (`dotnet restore`)

## Running All Tests

### Run all tests in the solution
```bash
dotnet test
```

### Run all tests with detailed output
```bash
dotnet test --verbosity detailed
```

### Run all tests in Release configuration
```bash
dotnet build --configuration Release
dotnet test --configuration Release --no-build
```

## Running Specific Test Projects

### Run tests from a specific test project
```bash
dotnet test tests/TermReader.Tests/TermReader.Tests.csproj
```

### Run tests from current directory
```bash
cd tests/TermReader.Tests
dotnet test
```

## Running Individual Tests

### Run a specific test by fully qualified name
```bash
dotnet test --filter "FullyQualifiedName=TermReader.Tests.DependencyInjectionTests.ServiceProvider_ShouldResolveIScraperService"
```

### Run a specific test by method name (shorter)
```bash
dotnet test --filter "Name=ServiceProvider_ShouldResolveIScraperService"
```

### Run tests matching a name pattern
```bash
dotnet test --filter "Name~ShouldResolve"
```

## Running Tests by Class

### Run all tests in a specific test class
```bash
dotnet test --filter "FullyQualifiedName~DependencyInjectionTests"
```

### Shorter version using display name
```bash
dotnet test --filter "ClassName=DependencyInjectionTests"
```

## Running Tests by Category

### Run tests by category/trait (when traits are added)
```bash
dotnet test --filter "Category=Unit"
dotnet test --filter "Category=Integration"
```

### Example of how to add categories to tests
```csharp
[Trait("Category", "Unit")]
[Fact]
public void ServiceProvider_ShouldResolveIScraperService()
{
    // Test implementation
}
```

## Running Tests with Code Coverage

### Using coverlet (if installed)
```bash
dotnet test --collect:"XPlat Code Coverage"
```

### View coverage results
Coverage reports are generated in: `tests/TermReader.Tests/TestResults/*/coverage.cobertura.xml`

## Running Tests in Watch Mode

### Automatically re-run tests on file changes
```bash
cd tests/TermReader.Tests
dotnet watch test
```

## Running Tests with Custom Settings

### Run tests with logger options
```bash
dotnet test --logger:"console;verbosity=detailed"
```

### Run tests and generate TRX report
```bash
dotnet test --logger:"trx;LogFileName=test-results.trx"
```

### Run tests with multiple loggers
```bash
dotnet test --logger:"console;verbosity=detailed" --logger:"trx"
```

## Filtering Tests

### Multiple filter conditions (AND)
```bash
dotnet test --filter "FullyQualifiedName~DependencyInjectionTests&Name~Scraper"
```

### Multiple filter conditions (OR)
```bash
dotnet test --filter "Name~Scraper|Name~Audio"
```

## Running Tests in Parallel

### Disable parallel execution
```bash
dotnet test -- xUnit.ParallelizeTestCollections=false
```

### Set maximum parallel threads
```bash
dotnet test -- xUnit.MaxParallelThreads=1
```

## Debugging Tests

### Run tests in debug mode (with Visual Studio or VS Code)
1. Set breakpoints in your test code
2. Right-click on the test method
3. Select "Debug Test"

### Using command line debugger
```bash
dotnet test --logger:"console;verbosity=detailed" -- xUnit.DiagnosticMessages=true
```

## Common Test Commands Quick Reference

| Command | Description |
|---------|-------------|
| `dotnet test` | Run all tests |
| `dotnet test --filter "Name~Pattern"` | Run tests matching pattern |
| `dotnet test --filter "Category=Unit"` | Run tests with specific category |
| `dotnet test --verbosity detailed` | Run with detailed output |
| `dotnet test --no-build` | Run without building first |
| `dotnet test --configuration Release` | Run in Release mode |
| `dotnet watch test` | Auto re-run on changes |
| `dotnet test --collect:"XPlat Code Coverage"` | Run with coverage |

## Examples for This Project

### Run all dependency injection tests
```bash
dotnet test --filter "FullyQualifiedName~DependencyInjectionTests"
```

### Run specific service resolution test
```bash
dotnet test --filter "Name=ServiceProvider_ShouldResolveIScraperService"
```

### Run all tests that verify service resolution
```bash
dotnet test --filter "Name~ShouldResolve"
```

### Run tests with detailed output and no build
```bash
dotnet test --no-build --verbosity detailed
```

## Continuous Integration

### Run tests in CI/CD pipeline
```bash
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release --no-build --logger:"trx" --collect:"XPlat Code Coverage"
```

## Troubleshooting

### Test discovery issues
If tests aren't being discovered:
```bash
dotnet clean
dotnet build
dotnet test
```

### Clear test cache
```bash
rm -rf tests/TermReader.Tests/bin
rm -rf tests/TermReader.Tests/obj
dotnet restore
dotnet test
```

## Additional Resources

- [xUnit Documentation](https://xunit.net/)
- [.NET Testing Documentation](https://learn.microsoft.com/en-us/dotnet/core/testing/)
- [VSTest Filter Documentation](https://learn.microsoft.com/en-us/dotnet/core/testing/selective-unit-tests)
