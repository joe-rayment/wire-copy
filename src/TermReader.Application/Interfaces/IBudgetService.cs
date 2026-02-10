// <copyright file="IBudgetService.cs" company="TermReader">
// Educational and personal use only.
// </copyright>


namespace TermReader.Application.Interfaces;

/// <summary>
/// Service for managing ElevenLabs API budget and tracking costs
/// </summary>
public interface IBudgetService
{
    /// <summary>
    /// Gets or sets the maximum budget allowed for this session
    /// </summary>
    decimal MaxBudget { get; set; }

    /// <summary>
    /// Gets the total amount spent so far
    /// </summary>
    decimal TotalSpent { get; }

    /// <summary>
    /// Gets the remaining budget amount
    /// </summary>
    decimal RemainingBudget { get; }

    /// <summary>
    /// Checks if the estimated cost can be afforded within the remaining budget
    /// </summary>
    /// <param name="estimatedCost">The estimated cost to check</param>
    /// <returns>True if the cost can be afforded, false otherwise</returns>
    bool CanAfford(decimal estimatedCost);

    /// <summary>
    /// Records an expense against the budget
    /// </summary>
    /// <param name="amount">The amount to record</param>
    void RecordExpense(decimal amount);

    /// <summary>
    /// Resets the budget tracker (clears total spent)
    /// </summary>
    void Reset();

    /// <summary>
    /// Gets a summary of the current budget state
    /// </summary>
    /// <returns>Budget summary with current state</returns>
    BudgetSummary GetSummary();
}
