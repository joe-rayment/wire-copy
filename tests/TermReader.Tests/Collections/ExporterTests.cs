// Licensed under the MIT License. See LICENSE in the repository root.

using System.Xml.Linq;
using FluentAssertions;
using TermReader.Application.Interfaces;
using TermReader.Domain.Entities.Collections;
using TermReader.Infrastructure.Collections;
using Xunit;

namespace TermReader.Tests.Collections;

[Trait("Category", "Unit")]
public class UrlListExporterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly UrlListExporter _sut;

    public UrlListExporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"termreader-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _sut = new UrlListExporter();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Format_ReturnsUrls()
    {
        _sut.Format.Should().Be("urls");
    }

    [Fact]
    public async Task ExportAsync_WritesOneUrlPerLine()
    {
        // Arrange
        var collection = Collection.Create("Test");
        collection.AddItem("https://example.com/1", "First");
        collection.AddItem("https://example.com/2", "Second");
        collection.AddItem("https://example.com/3", "Third");

        var outputPath = Path.Combine(_tempDir, "test.urls");
        var options = new ExportOptions(outputPath, IncludeDatestamp: false);

        // Act
        await _sut.ExportAsync(collection, options);

        // Assert
        var lines = await File.ReadAllLinesAsync(outputPath);
        lines.Should().HaveCount(3);
        lines[0].Should().Be("https://example.com/1");
        lines[1].Should().Be("https://example.com/2");
        lines[2].Should().Be("https://example.com/3");
    }

    [Fact]
    public async Task ExportAsync_WithDatestamp_IncludesHeader()
    {
        // Arrange
        var collection = Collection.Create("My Links");
        collection.AddItem("https://example.com", "Example");

        var outputPath = Path.Combine(_tempDir, "test.urls");
        var options = new ExportOptions(outputPath, IncludeDatestamp: true);

        // Act
        await _sut.ExportAsync(collection, options);

        // Assert
        var lines = await File.ReadAllLinesAsync(outputPath);
        lines.Should().HaveCountGreaterThan(1);
        lines[0].Should().StartWith("# My Links - exported ");
        lines[1].Should().BeEmpty();
        lines[2].Should().Be("https://example.com");
    }

    [Fact]
    public async Task ExportAsync_WithCustomName_UsesCustomNameInHeader()
    {
        // Arrange
        var collection = Collection.Create("Original Name");
        collection.AddItem("https://example.com", "Example");

        var outputPath = Path.Combine(_tempDir, "test.urls");
        var options = new ExportOptions(outputPath, IncludeDatestamp: true, CustomName: "Custom Name");

        // Act
        await _sut.ExportAsync(collection, options);

        // Assert
        var lines = await File.ReadAllLinesAsync(outputPath);
        lines[0].Should().StartWith("# Custom Name - exported ");
    }

    [Fact]
    public async Task ExportAsync_EmptyCollection_WritesEmptyFile()
    {
        // Arrange
        var collection = Collection.Create("Empty");
        var outputPath = Path.Combine(_tempDir, "test.urls");
        var options = new ExportOptions(outputPath, IncludeDatestamp: false);

        // Act
        await _sut.ExportAsync(collection, options);

        // Assert
        var lines = await File.ReadAllLinesAsync(outputPath);
        lines.Should().BeEmpty();
    }

    [Fact]
    public async Task ExportAsync_CreatesDirectoryIfNotExists()
    {
        // Arrange
        var collection = Collection.Create("Test");
        collection.AddItem("https://example.com", "Example");

        var nestedDir = Path.Combine(_tempDir, "nested", "dir");
        var outputPath = Path.Combine(nestedDir, "test.urls");
        var options = new ExportOptions(outputPath, IncludeDatestamp: false);

        // Act
        await _sut.ExportAsync(collection, options);

        // Assert
        File.Exists(outputPath).Should().BeTrue();
    }

    [Fact]
    public async Task ExportAsync_NullCollection_ThrowsArgumentNullException()
    {
        // Arrange
        var outputPath = Path.Combine(_tempDir, "test.urls");
        var options = new ExportOptions(outputPath);

        // Act
        var act = () => _sut.ExportAsync(null!, options);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("collection");
    }

    [Fact]
    public async Task ExportAsync_NullOptions_ThrowsArgumentNullException()
    {
        // Arrange
        var collection = Collection.Create("Test");

        // Act
        var act = () => _sut.ExportAsync(collection, null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("options");
    }
}

[Trait("Category", "Unit")]
public class OpmlExporterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly OpmlExporter _sut;

    public OpmlExporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"termreader-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _sut = new OpmlExporter();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Format_ReturnsOpml()
    {
        _sut.Format.Should().Be("opml");
    }

    [Fact]
    public async Task ExportAsync_WritesValidOpml()
    {
        // Arrange
        var collection = Collection.Create("Test Links");
        collection.AddItem("https://example.com/1", "First Article");
        collection.AddItem("https://example.com/2", "Second Article");

        var outputPath = Path.Combine(_tempDir, "test.opml");
        var options = new ExportOptions(outputPath, IncludeDatestamp: false);

        // Act
        await _sut.ExportAsync(collection, options);

        // Assert
        var doc = XDocument.Load(outputPath);
        var opml = doc.Root!;
        opml.Name.LocalName.Should().Be("opml");
        opml.Attribute("version")!.Value.Should().Be("2.0");

        var head = opml.Element("head")!;
        head.Element("title")!.Value.Should().Be("Test Links");

        var body = opml.Element("body")!;
        var outlines = body.Elements("outline").ToList();
        outlines.Should().HaveCount(2);

        outlines[0].Attribute("text")!.Value.Should().Be("First Article");
        outlines[0].Attribute("url")!.Value.Should().Be("https://example.com/1");
        outlines[0].Attribute("type")!.Value.Should().Be("link");

        outlines[1].Attribute("text")!.Value.Should().Be("Second Article");
        outlines[1].Attribute("url")!.Value.Should().Be("https://example.com/2");
    }

    [Fact]
    public async Task ExportAsync_WithDatestamp_IncludesDateCreated()
    {
        // Arrange
        var collection = Collection.Create("Test");
        collection.AddItem("https://example.com", "Example");

        var outputPath = Path.Combine(_tempDir, "test.opml");
        var options = new ExportOptions(outputPath, IncludeDatestamp: true);

        // Act
        await _sut.ExportAsync(collection, options);

        // Assert
        var doc = XDocument.Load(outputPath);
        var head = doc.Root!.Element("head")!;
        head.Element("dateCreated").Should().NotBeNull();
    }

    [Fact]
    public async Task ExportAsync_WithoutDatestamp_OmitsDateCreated()
    {
        // Arrange
        var collection = Collection.Create("Test");
        collection.AddItem("https://example.com", "Example");

        var outputPath = Path.Combine(_tempDir, "test.opml");
        var options = new ExportOptions(outputPath, IncludeDatestamp: false);

        // Act
        await _sut.ExportAsync(collection, options);

        // Assert
        var doc = XDocument.Load(outputPath);
        var head = doc.Root!.Element("head")!;
        head.Element("dateCreated").Should().BeNull();
    }

    [Fact]
    public async Task ExportAsync_WithCustomName_UsesCustomName()
    {
        // Arrange
        var collection = Collection.Create("Original");
        collection.AddItem("https://example.com", "Example");

        var outputPath = Path.Combine(_tempDir, "test.opml");
        var options = new ExportOptions(outputPath, IncludeDatestamp: false, CustomName: "Custom Title");

        // Act
        await _sut.ExportAsync(collection, options);

        // Assert
        var doc = XDocument.Load(outputPath);
        doc.Root!.Element("head")!.Element("title")!.Value.Should().Be("Custom Title");
    }

    [Fact]
    public async Task ExportAsync_EmptyCollection_WritesOpmlWithNoOutlines()
    {
        // Arrange
        var collection = Collection.Create("Empty");
        var outputPath = Path.Combine(_tempDir, "test.opml");
        var options = new ExportOptions(outputPath, IncludeDatestamp: false);

        // Act
        await _sut.ExportAsync(collection, options);

        // Assert
        var doc = XDocument.Load(outputPath);
        var body = doc.Root!.Element("body")!;
        body.Elements("outline").Should().BeEmpty();
    }

    [Fact]
    public async Task ExportAsync_CreatesDirectoryIfNotExists()
    {
        // Arrange
        var collection = Collection.Create("Test");
        collection.AddItem("https://example.com", "Example");

        var nestedDir = Path.Combine(_tempDir, "nested", "dir");
        var outputPath = Path.Combine(nestedDir, "test.opml");
        var options = new ExportOptions(outputPath, IncludeDatestamp: false);

        // Act
        await _sut.ExportAsync(collection, options);

        // Assert
        File.Exists(outputPath).Should().BeTrue();
    }

    [Fact]
    public async Task ExportAsync_IncludesCreatedAttributeOnOutlines()
    {
        // Arrange
        var collection = Collection.Create("Test");
        collection.AddItem("https://example.com", "Example");

        var outputPath = Path.Combine(_tempDir, "test.opml");
        var options = new ExportOptions(outputPath, IncludeDatestamp: false);

        // Act
        await _sut.ExportAsync(collection, options);

        // Assert
        var doc = XDocument.Load(outputPath);
        var outline = doc.Root!.Element("body")!.Element("outline")!;
        outline.Attribute("created").Should().NotBeNull();
    }

    [Fact]
    public async Task ExportAsync_NullCollection_ThrowsArgumentNullException()
    {
        // Arrange
        var outputPath = Path.Combine(_tempDir, "test.opml");
        var options = new ExportOptions(outputPath);

        // Act
        var act = () => _sut.ExportAsync(null!, options);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("collection");
    }

    [Fact]
    public async Task ExportAsync_NullOptions_ThrowsArgumentNullException()
    {
        // Arrange
        var collection = Collection.Create("Test");

        // Act
        var act = () => _sut.ExportAsync(collection, null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("options");
    }
}
