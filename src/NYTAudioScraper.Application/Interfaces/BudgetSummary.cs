namespace NYTAudioScraper.Application.Interfaces;

/// <summary>
/// Summary of budget state
/// </summary>
public class BudgetSummary
{
    public decimal MaxBudget { get; init; }
    public decimal TotalSpent { get; init; }
    public decimal RemainingBudget { get; init; }
    public decimal PercentageUsed { get; init; }

    public override string ToString()
    {
        return $"Budget: ${TotalSpent:F2}/${MaxBudget:F2} ({PercentageUsed:F1}% used)";
    }
}
