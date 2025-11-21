// Educational and personal use only.

using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces;

namespace NYTAudioScraper.Infrastructure.Audio;

public class BudgetService : IBudgetService
{
    private readonly ILogger<BudgetService> _logger;
    private decimal _totalSpent;
    private readonly object _lock = new();

    public BudgetService(ILogger<BudgetService> logger)
    {
        _logger = logger;
    }

    public decimal MaxBudget { get; set; } = 10.0m; // Default $10 budget

    public decimal TotalSpent => _totalSpent;

    public decimal RemainingBudget => MaxBudget - _totalSpent;

    public bool CanAfford(decimal estimatedCost)
    {
        lock (_lock)
        {
            var canAfford = _totalSpent + estimatedCost <= MaxBudget;

            if (!canAfford)
            {
                _logger.LogWarning(
                    "Budget exceeded: Estimated cost ${EstimatedCost:F4} would exceed remaining budget ${RemainingBudget:F4}",
                    estimatedCost,
                    RemainingBudget);
            }

            return canAfford;
        }
    }

    public void RecordExpense(decimal amount)
    {
        lock (_lock)
        {
            _totalSpent += amount;
            _logger.LogInformation(
                "Recorded expense: ${Amount:F4} | Total spent: ${TotalSpent:F4} | Remaining: ${Remaining:F4}",
                amount,
                _totalSpent,
                RemainingBudget);
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _totalSpent = 0;
            _logger.LogInformation("Budget reset. Max budget: ${MaxBudget:F2}", MaxBudget);
        }
    }

    public BudgetSummary GetSummary()
    {
        lock (_lock)
        {
            return new BudgetSummary
            {
                MaxBudget = MaxBudget,
                TotalSpent = _totalSpent,
                RemainingBudget = RemainingBudget,
                PercentageUsed = MaxBudget > 0 ? (_totalSpent / MaxBudget) * 100 : 0
            };
        }
    }
}
