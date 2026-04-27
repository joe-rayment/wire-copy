// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Credentials;
using TermReader.Domain.Enums;
using TermReader.Domain.ValueObjects.Credentials;
using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI.Components;

namespace TermReader.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles credential management commands (:cred add, :cred rm, :cred test, :cred edit).
/// </summary>
internal static class CredentialCommandHandler
{
    private static readonly Dictionary<string, (string LoginUrl, List<LoginStep> Steps)> KnownSiteDefaults = new()
    {
        ["nytimes.com"] = (
            "https://myaccount.nytimes.com/auth/login",
            new List<LoginStep>
            {
                new("#email", StepValueType.Username, "button[data-testid=submit-email]"),
                new("#password", StepValueType.Password, "button[type=submit]"),
            }),
    };

    internal static async Task HandleCredentialCommand(
        CommandContext ctx, string? subcommand, RenderOptions options, CancellationToken ct)
    {
        var sub = subcommand?.Trim().ToLowerInvariant();

        // Parse subcommand and optional trailing argument (e.g. "rm nytimes.com")
        string? subArg = null;
        if (sub != null)
        {
            var subParts = sub.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            sub = subParts[0];
            subArg = subParts.Length > 1 ? subParts[1].Trim() : null;
        }

        switch (sub)
        {
            case "add":
                await HandleCredentialAdd(ctx, options, ct).ConfigureAwait(false);
                break;

            case "remove" or "rm":
                await HandleCredentialRemove(ctx, subArg, options, ct).ConfigureAwait(false);
                break;

            case "test":
                await HandleCredentialTest(ctx, subArg, options, ct).ConfigureAwait(false);
                break;

            case "edit":
                await HandleCredentialEdit(ctx, subArg, options, ct).ConfigureAwait(false);
                break;

            default:
                await HandleCredentialList(ctx, options, ct).ConfigureAwait(false);
                break;
        }
    }

    private static async Task HandleCredentialList(
        CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISiteCredentialRepository>();
            var credentials = await repo.GetAllAsync(ct).ConfigureAwait(false);

            if (credentials.Count == 0)
            {
                ctx.NavigationService.SetStatusMessage(
                    "No stored credentials. Use :cred add to add one.");
            }
            else
            {
                var list = string.Join(", ", credentials.Select(c =>
                    $"{c.Domain} ({c.CredentialType})"));
                ctx.NavigationService.SetStatusMessage($"Stored credentials: {list}");
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to list credentials");
            ctx.NavigationService.SetStatusMessage("Failed to list credentials");
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    private static async Task HandleCredentialAdd(
        CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        try
        {
            var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);

            // Step 1: Account info
            var step1 = new List<WizardStep>
            {
                new()
                {
                    Title = "Add Credential",
                    Description = "Enter site login credentials",
                    Fields =
                    [
                        new FormFieldConfig
                        {
                            Label = "Domain",
                            Placeholder = "nytimes.com",
                            Validate = v => string.IsNullOrWhiteSpace(v) ? "Domain cannot be empty" : null,
                        },
                        new FormFieldConfig
                        {
                            Label = "Username / Email",
                            Placeholder = "user@example.com",
                            Validate = v => string.IsNullOrWhiteSpace(v) ? "Username cannot be empty" : null,
                        },
                        new FormFieldConfig
                        {
                            Label = "Password",
                            IsSecret = true,
                            Validate = v => string.IsNullOrWhiteSpace(v) ? "Password cannot be empty" : null,
                        },
                    ],
                },
            };

            var accountInfo = await WizardRunner.RunAsync(ctx.InputHandler, step1, palette, ct).ConfigureAwait(false);
            if (accountInfo == null)
            {
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            var domain = accountInfo["Domain"].Trim();
            var username = accountInfo["Username / Email"].Trim();
            var password = accountInfo["Password"].Trim();

            // Check for known site defaults
            var normalizedDomain = domain.ToLowerInvariant();
            var hasDefaults = KnownSiteDefaults.TryGetValue(normalizedDomain, out var defaults);

            string? loginUrl = null;
            string? usernameSelector = null;
            string? passwordSelector = null;
            string? submitSelector = null;
            List<LoginStep>? loginSteps = null;

            if (hasDefaults)
            {
                loginUrl = defaults.LoginUrl;
                loginSteps = defaults.Steps;
                ctx.NavigationService.SetStatusMessage(
                    $"Using known {normalizedDomain} login flow ({loginSteps.Count}-step)");
            }
            else
            {
                // Step 2: Login configuration (unknown sites only)
                var step2 = new List<WizardStep>
                {
                    new()
                    {
                        Title = "Login Configuration",
                        Description = "Optional — CSS selectors for the login form",
                        Fields =
                        [
                            new FormFieldConfig
                            {
                                Label = "Login URL",
                                Placeholder = "https://example.com/login",
                                HelpText = "Enter to skip",
                            },
                            new FormFieldConfig
                            {
                                Label = "Username Selector",
                                Placeholder = "input[name=email]",
                                HelpText = "CSS selector for the username/email field",
                            },
                            new FormFieldConfig
                            {
                                Label = "Password Selector",
                                Placeholder = "input[type=password]",
                                HelpText = "CSS selector for the password field",
                            },
                            new FormFieldConfig
                            {
                                Label = "Submit Selector",
                                Placeholder = "button[type=submit]",
                                HelpText = "CSS selector for the submit button",
                            },
                        ],
                    },
                };

                var loginConfig = await WizardRunner.RunAsync(ctx.InputHandler, step2, palette, ct).ConfigureAwait(false);

                // Cancelled step 2 is OK — save with basic info only
                if (loginConfig != null)
                {
                    loginUrl = EmptyToNull(loginConfig.GetValueOrDefault("Login URL"));
                    usernameSelector = EmptyToNull(loginConfig.GetValueOrDefault("Username Selector"));
                    passwordSelector = EmptyToNull(loginConfig.GetValueOrDefault("Password Selector"));
                    submitSelector = EmptyToNull(loginConfig.GetValueOrDefault("Submit Selector"));
                }
            }

            using var scope = ctx.ScopeFactory.CreateScope();
            var encryption = scope.ServiceProvider.GetRequiredService<ICookieEncryptionService>();
            var repo = scope.ServiceProvider.GetRequiredService<ISiteCredentialRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var encryptedUsername = encryption.Encrypt(username);
            var encryptedPassword = encryption.Encrypt(password);

            var credential = SiteCredential.Create(
                domain,
                CredentialType.FormLogin,
                encryptedUsername,
                encryptedPassword,
                usernameSelector,
                passwordSelector,
                submitSelector,
                loginUrl,
                loginSteps);

            await repo.AddAsync(credential, ct).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

            var msg = loginSteps != null
                ? $"Credential saved for {credential.Domain} (multi-step login)"
                : $"Credential saved for {credential.Domain}";
            ctx.NavigationService.SetStatusMessage(msg);
            ctx.Logger.LogInformation("Added credential for domain: {Domain}", credential.Domain);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to add credential");
            ctx.NavigationService.SetStatusMessage($"Failed to add credential: {ex.Message}");
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static async Task HandleCredentialRemove(
        CommandContext ctx, string? domainArg, RenderOptions options, CancellationToken ct)
    {
        try
        {
            var domain = domainArg;
            if (string.IsNullOrWhiteSpace(domain))
            {
                domain = await ctx.InputHandler.PromptForInputAsync("Domain to remove: ", ct).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(domain))
            {
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            domain = domain.Trim().ToLowerInvariant();

            using var scope = ctx.ScopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISiteCredentialRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var credential = await repo.GetByDomainAsync(domain, ct).ConfigureAwait(false);
            if (credential == null)
            {
                ctx.NavigationService.SetStatusMessage($"No credential found for {domain}");
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            await repo.DeleteAsync(credential.Id, ct).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

            ctx.NavigationService.SetStatusMessage($"Credential removed for {domain}");
            ctx.Logger.LogInformation("Removed credential for domain: {Domain}", domain);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to remove credential");
            ctx.NavigationService.SetStatusMessage($"Failed to remove credential: {ex.Message}");
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    private static async Task HandleCredentialTest(
        CommandContext ctx, string? domainArg, RenderOptions options, CancellationToken ct)
    {
        try
        {
            var domain = domainArg;
            if (string.IsNullOrWhiteSpace(domain))
            {
                domain = await ctx.InputHandler.PromptForInputAsync("Domain to test: ", ct).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(domain))
            {
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            domain = domain.Trim().ToLowerInvariant();

            using var scope = ctx.ScopeFactory.CreateScope();
            var autoLogin = scope.ServiceProvider.GetRequiredService<IAutoLoginService>();

            ctx.NavigationService.SetStatusMessage($"Testing login for {domain}...");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

            var result = await autoLogin.LoginAsync(domain, ct).ConfigureAwait(false);

            if (result.Success)
            {
                ctx.NavigationService.SetStatusMessage($"Login succeeded for {domain}");
            }
            else if (result.ManualLoginRequired)
            {
                ctx.NavigationService.SetStatusMessage(
                    $"Manual login required for {domain}: {result.ErrorMessage}");
            }
            else
            {
                ctx.NavigationService.SetStatusMessage(
                    $"Login failed for {domain}: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to test credential");
            ctx.NavigationService.SetStatusMessage($"Login test failed: {ex.Message}");
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    private static async Task HandleCredentialEdit(
        CommandContext ctx, string? domainArg, RenderOptions options, CancellationToken ct)
    {
        try
        {
            var domain = domainArg;
            if (string.IsNullOrWhiteSpace(domain))
            {
                domain = await ctx.InputHandler.PromptForInputAsync("Domain to edit: ", ct).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(domain))
            {
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            domain = domain.Trim().ToLowerInvariant();

            using var scope = ctx.ScopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISiteCredentialRepository>();
            var encryption = scope.ServiceProvider.GetRequiredService<ICookieEncryptionService>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var credential = await repo.GetByDomainAsync(domain, ct).ConfigureAwait(false);
            if (credential == null)
            {
                ctx.NavigationService.SetStatusMessage($"No credential found for {domain}");
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var currentUsername = encryption.Decrypt(credential.EncryptedUsername);

            // Step 1: Account info (pre-filled)
            var step1 = new List<WizardStep>
            {
                new()
                {
                    Title = $"Edit Credential — {domain}",
                    Description = "Leave empty to keep current value",
                    Fields =
                    [
                        new FormFieldConfig
                        {
                            Label = "Username / Email",
                            InitialValue = currentUsername,
                        },
                        new FormFieldConfig
                        {
                            Label = "Password",
                            IsSecret = true,
                            HelpText = "Enter to keep current password",
                        },
                    ],
                },
            };

            var accountInfo = await WizardRunner.RunAsync(ctx.InputHandler, step1, palette, ct).ConfigureAwait(false);
            if (accountInfo == null)
            {
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            var newUsername = accountInfo["Username / Email"];
            var newPassword = accountInfo["Password"];

            // Step 2: Login configuration (pre-filled, skip for known sites)
            var normalizedDomain = domain.ToLowerInvariant();
            var isKnownSite = KnownSiteDefaults.ContainsKey(normalizedDomain);

            string? newLoginUrl = null;
            string? newUsernameSelector = null;
            string? newPasswordSelector = null;
            string? newSubmitSelector = null;

            if (!isKnownSite)
            {
                var step2 = new List<WizardStep>
                {
                    new()
                    {
                        Title = $"Edit Login Config — {domain}",
                        Description = "Leave empty to keep current value",
                        Fields =
                        [
                            new FormFieldConfig
                            {
                                Label = "Login URL",
                                InitialValue = credential.LoginUrl,
                            },
                            new FormFieldConfig
                            {
                                Label = "Username Selector",
                                InitialValue = credential.UsernameSelector,
                            },
                            new FormFieldConfig
                            {
                                Label = "Password Selector",
                                InitialValue = credential.PasswordSelector,
                            },
                            new FormFieldConfig
                            {
                                Label = "Submit Selector",
                                InitialValue = credential.SubmitSelector,
                            },
                        ],
                    },
                };

                var loginConfig = await WizardRunner.RunAsync(ctx.InputHandler, step2, palette, ct).ConfigureAwait(false);

                // Cancelled step 2 — keep existing login config
                if (loginConfig != null)
                {
                    newLoginUrl = loginConfig.GetValueOrDefault("Login URL");
                    newUsernameSelector = loginConfig.GetValueOrDefault("Username Selector");
                    newPasswordSelector = loginConfig.GetValueOrDefault("Password Selector");
                    newSubmitSelector = loginConfig.GetValueOrDefault("Submit Selector");
                }
            }

            var usernameToEncrypt = string.IsNullOrWhiteSpace(newUsername) ? currentUsername : newUsername.Trim();
            var encryptedUsername = encryption.Encrypt(usernameToEncrypt);

            byte[] encryptedPassword;
            if (string.IsNullOrWhiteSpace(newPassword))
            {
                encryptedPassword = credential.EncryptedPassword;
            }
            else
            {
                encryptedPassword = encryption.Encrypt(newPassword.Trim());
            }

            credential.Update(
                encryptedUsername,
                encryptedPassword,
                !string.IsNullOrWhiteSpace(newUsernameSelector) ? newUsernameSelector.Trim() : credential.UsernameSelector,
                !string.IsNullOrWhiteSpace(newPasswordSelector) ? newPasswordSelector.Trim() : credential.PasswordSelector,
                !string.IsNullOrWhiteSpace(newSubmitSelector) ? newSubmitSelector.Trim() : credential.SubmitSelector,
                !string.IsNullOrWhiteSpace(newLoginUrl) ? newLoginUrl.Trim() : credential.LoginUrl);

            await repo.UpdateAsync(credential, ct).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

            ctx.NavigationService.SetStatusMessage($"Credential updated for {domain}");
            ctx.Logger.LogInformation("Updated credential for domain: {Domain}", domain);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to edit credential");
            ctx.NavigationService.SetStatusMessage($"Failed to edit credential: {ex.Message}");
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }
}
