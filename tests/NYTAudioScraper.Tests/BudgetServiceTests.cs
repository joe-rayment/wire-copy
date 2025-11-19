// <copyright file="BudgetServiceTests.cs" company="NYTAudioScraper">
// Copyright (c) NYTAudioScraper. All rights reserved.
// </copyright>

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NYTAudioScraper.Infrastructure.Audio;
using Xunit;

namespace NYTAudioScraper.Tests;

public class BudgetServiceTests
{
    private readonly BudgetService _budgetService;
    private readonly ILogger<BudgetService> _logger;

    public BudgetServiceTests()
    {
        _logger = Substitute.For<ILogger<BudgetService>>();
        _budgetService = new BudgetService(_logger)
        {
            MaxBudget = 10.0m
        };
    }

    [Fact]
    public void CanAfford_WithinBudget_ReturnsTrue()
    {
        // Arrange
        var estimatedCost = 5.0m;

        // Act
        var result = _budgetService.CanAfford(estimatedCost);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void CanAfford_ExceedsBudget_ReturnsFalse()
    {
        // Arrange
        var estimatedCost = 15.0m;

        // Act
        var result = _budgetService.CanAfford(estimatedCost);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void CanAfford_ExactlyAtBudget_ReturnsTrue()
    {
        // Arrange
        var estimatedCost = 10.0m;

        // Act
        var result = _budgetService.CanAfford(estimatedCost);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void RecordExpense_UpdatesTotalSpent()
    {
        // Arrange
        var expense = 3.5m;

        // Act
        _budgetService.RecordExpense(expense);

        // Assert
        _budgetService.TotalSpent.Should().Be(3.5m);
        _budgetService.RemainingBudget.Should().Be(6.5m);
    }

    [Fact]
    public void RecordExpense_MultipleExpenses_AccumulatesCorrectly()
    {
        // Act
        _budgetService.RecordExpense(2.0m);
        _budgetService.RecordExpense(3.0m);
        _budgetService.RecordExpense(1.5m);

        // Assert
        _budgetService.TotalSpent.Should().Be(6.5m);
        _budgetService.RemainingBudget.Should().Be(3.5m);
    }

    [Fact]
    public void CanAfford_AfterExpenses_ConsidersPreviousSpending()
    {
        // Arrange
        _budgetService.RecordExpense(7.0m);

        // Act
        var canAfford2More = _budgetService.CanAfford(2.0m);
        var canAfford5More = _budgetService.CanAfford(5.0m);

        // Assert
        canAfford2More.Should().BeTrue();
        canAfford5More.Should().BeFalse();
    }

    [Fact]
    public void Reset_ClearsTotalSpent()
    {
        // Arrange
        _budgetService.RecordExpense(5.0m);

        // Act
        _budgetService.Reset();

        // Assert
        _budgetService.TotalSpent.Should().Be(0);
        _budgetService.RemainingBudget.Should().Be(10.0m);
    }

    [Fact]
    public void GetSummary_ReturnsCorrectValues()
    {
        // Arrange
        _budgetService.RecordExpense(3.0m);

        // Act
        var summary = _budgetService.GetSummary();

        // Assert
        summary.MaxBudget.Should().Be(10.0m);
        summary.TotalSpent.Should().Be(3.0m);
        summary.RemainingBudget.Should().Be(7.0m);
        summary.PercentageUsed.Should().Be(30.0m);
    }

    [Fact]
    public void GetSummary_WhenFullySpent_Shows100Percent()
    {
        // Arrange
        _budgetService.RecordExpense(10.0m);

        // Act
        var summary = _budgetService.GetSummary();

        // Assert
        summary.PercentageUsed.Should().Be(100.0m);
    }

    [Fact]
    public void GetSummary_ToString_FormatsCorrectly()
    {
        // Arrange
        _budgetService.RecordExpense(3.75m);

        // Act
        var summary = _budgetService.GetSummary();
        var summaryString = summary.ToString();

        // Assert
        summaryString.Should().Contain("$3.75");
        summaryString.Should().Contain("$10.00");
        summaryString.Should().Contain("37.5%");
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(5, 5, true)]
    [InlineData(5, 5.01, false)]
    [InlineData(9.99, 0.01, true)]
    [InlineData(9.99, 0.02, false)]
    public void CanAfford_VariousScenarios_WorksCorrectly(decimal spent, decimal estimated, bool expectedResult)
    {
        // Arrange
        if (spent > 0)
        {
            _budgetService.RecordExpense(spent);
        }

        // Act
        var result = _budgetService.CanAfford(estimated);

        // Assert
        result.Should().Be(expectedResult);
    }

    [Fact]
    public void MaxBudget_CanBeChanged_UpdatesRemainingBudget()
    {
        // Arrange
        _budgetService.RecordExpense(5.0m);

        // Act
        _budgetService.MaxBudget = 20.0m;

        // Assert
        _budgetService.RemainingBudget.Should().Be(15.0m);
    }

    [Fact]
    public void BudgetService_WithZeroMaxBudget_HandlesPercentageCorrectly()
    {
        // Arrange
        _budgetService.MaxBudget = 0;
        _budgetService.RecordExpense(0);

        // Act
        var summary = _budgetService.GetSummary();

        // Assert
        summary.PercentageUsed.Should().Be(0);
    }
}
