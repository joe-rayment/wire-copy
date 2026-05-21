// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Google.Apis.Storage.v1.Data;
using WireCopy.Infrastructure.Podcast;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// Unit tests for <see cref="GcsBucketPublicReadHelper"/> — the pure-function
/// helpers underneath the workspace-p1px bucket-auto-remediation flow.
/// The IO wrapper (<c>GcsStorageClient.MakeBucketPublicAsync</c>) is covered
/// separately by integration tests; this class pins the policy-mutation
/// semantics so we don't accidentally regress them when refactoring.
/// </summary>
[Trait("Category", "Unit")]
public class GcsBucketPublicReadHelperTests
{
    [Fact]
    public void IsPublicRead_NullPolicy_ReturnsFalse()
    {
        GcsBucketPublicReadHelper.IsPublicRead(null).Should().BeFalse();
    }

    [Fact]
    public void IsPublicRead_NullBindings_ReturnsFalse()
    {
        var policy = new Policy { Bindings = null };
        GcsBucketPublicReadHelper.IsPublicRead(policy).Should().BeFalse();
    }

    [Fact]
    public void IsPublicRead_EmptyBindings_ReturnsFalse()
    {
        var policy = new Policy { Bindings = new List<Policy.BindingsData>() };
        GcsBucketPublicReadHelper.IsPublicRead(policy).Should().BeFalse();
    }

    [Fact]
    public void IsPublicRead_OnlyOwnerBinding_ReturnsFalse()
    {
        var policy = new Policy
        {
            Bindings = new List<Policy.BindingsData>
            {
                new()
                {
                    Role = "roles/storage.admin",
                    Members = new List<string> { "user:owner@example.com" },
                },
            },
        };

        GcsBucketPublicReadHelper.IsPublicRead(policy).Should().BeFalse();
    }

    [Fact]
    public void IsPublicRead_ObjectViewerWithoutAllUsers_ReturnsFalse()
    {
        var policy = new Policy
        {
            Bindings = new List<Policy.BindingsData>
            {
                new()
                {
                    Role = "roles/storage.objectViewer",
                    Members = new List<string> { "user:viewer@example.com" },
                },
            },
        };

        GcsBucketPublicReadHelper.IsPublicRead(policy).Should().BeFalse();
    }

    [Fact]
    public void IsPublicRead_AllUsersOnObjectViewer_ReturnsTrue()
    {
        var policy = new Policy
        {
            Bindings = new List<Policy.BindingsData>
            {
                new()
                {
                    Role = "roles/storage.objectViewer",
                    Members = new List<string> { "allUsers" },
                },
            },
        };

        GcsBucketPublicReadHelper.IsPublicRead(policy).Should().BeTrue();
    }

    [Fact]
    public void IsPublicRead_AllUsersAmongOtherMembers_ReturnsTrue()
    {
        var policy = new Policy
        {
            Bindings = new List<Policy.BindingsData>
            {
                new()
                {
                    Role = "roles/storage.objectViewer",
                    Members = new List<string> { "user:viewer@example.com", "allUsers", "user:other@example.com" },
                },
            },
        };

        GcsBucketPublicReadHelper.IsPublicRead(policy).Should().BeTrue();
    }

    [Fact]
    public void AddPublicReadBinding_EmptyPolicy_AddsFreshBinding()
    {
        var policy = new Policy
        {
            Bindings = new List<Policy.BindingsData>(),
            ETag = "etag-1",
        };

        var updated = GcsBucketPublicReadHelper.AddPublicReadBinding(policy);

        updated.Should().NotBeSameAs(policy, "the helper must return a new policy, not mutate the input");
        updated.ETag.Should().Be("etag-1", "the helper must preserve ETag for optimistic concurrency");
        updated.Bindings.Should().HaveCount(1);
        updated.Bindings[0].Role.Should().Be("roles/storage.objectViewer");
        updated.Bindings[0].Members.Should().ContainSingle().Which.Should().Be("allUsers");
        GcsBucketPublicReadHelper.IsPublicRead(updated).Should().BeTrue();
    }

    [Fact]
    public void AddPublicReadBinding_ExistingObjectViewerWithoutAllUsers_AppendsAllUsers()
    {
        var policy = new Policy
        {
            Bindings = new List<Policy.BindingsData>
            {
                new()
                {
                    Role = "roles/storage.objectViewer",
                    Members = new List<string> { "user:existing@example.com" },
                },
            },
            ETag = "etag-2",
        };

        var updated = GcsBucketPublicReadHelper.AddPublicReadBinding(policy);

        updated.ETag.Should().Be("etag-2");
        updated.Bindings.Should().HaveCount(1, "the existing role should be reused, not duplicated");
        updated.Bindings[0].Members.Should().BeEquivalentTo(new[] { "user:existing@example.com", "allUsers" });
        GcsBucketPublicReadHelper.IsPublicRead(updated).Should().BeTrue();
    }

    [Fact]
    public void AddPublicReadBinding_AlreadyPublic_NoDuplicate()
    {
        var policy = new Policy
        {
            Bindings = new List<Policy.BindingsData>
            {
                new()
                {
                    Role = "roles/storage.objectViewer",
                    Members = new List<string> { "allUsers" },
                },
            },
        };

        var updated = GcsBucketPublicReadHelper.AddPublicReadBinding(policy);

        updated.Bindings.Should().HaveCount(1);
        updated.Bindings[0].Members.Should().ContainSingle().Which.Should().Be("allUsers");
    }

    [Fact]
    public void AddPublicReadBinding_DoesNotMutateInputPolicy()
    {
        var inputMembers = new List<string> { "user:existing@example.com" };
        var policy = new Policy
        {
            Bindings = new List<Policy.BindingsData>
            {
                new()
                {
                    Role = "roles/storage.objectViewer",
                    Members = inputMembers,
                },
            },
        };

        _ = GcsBucketPublicReadHelper.AddPublicReadBinding(policy);

        inputMembers.Should().ContainSingle().Which.Should().Be("user:existing@example.com",
            "the helper must not mutate the input policy's member list");
        policy.Bindings.Should().HaveCount(1);
    }

    [Fact]
    public void AddPublicReadBinding_PreservesOtherRoleBindings()
    {
        var policy = new Policy
        {
            Bindings = new List<Policy.BindingsData>
            {
                new()
                {
                    Role = "roles/storage.admin",
                    Members = new List<string> { "user:admin@example.com" },
                },
                new()
                {
                    Role = "roles/storage.objectCreator",
                    Members = new List<string> { "serviceAccount:writer@example.iam.gserviceaccount.com" },
                },
            },
        };

        var updated = GcsBucketPublicReadHelper.AddPublicReadBinding(policy);

        updated.Bindings.Should().HaveCount(3, "the helper must add a new objectViewer binding alongside the existing roles");
        updated.Bindings.Should().Contain(b => b.Role == "roles/storage.admin");
        updated.Bindings.Should().Contain(b => b.Role == "roles/storage.objectCreator");
        updated.Bindings.Should().Contain(b => b.Role == "roles/storage.objectViewer" && b.Members.Contains("allUsers"));
    }

    [Fact]
    public void AddPublicReadBinding_ConditionalObjectViewer_AddsFreshUnconditionalBinding()
    {
        // A binding gated on a time-window condition is functionally different
        // from an unconditional public-read grant; grafting allUsers onto it
        // would silently constrain the public access. The helper must instead
        // add a fresh unconditional binding next to it.
        var policy = new Policy
        {
            Bindings = new List<Policy.BindingsData>
            {
                new()
                {
                    Role = "roles/storage.objectViewer",
                    Members = new List<string> { "user:scheduled@example.com" },
                    Condition = new Expr { Title = "expires2027", Expression = "request.time < timestamp(\"2027-01-01T00:00:00Z\")" },
                },
            },
        };

        var updated = GcsBucketPublicReadHelper.AddPublicReadBinding(policy);

        updated.Bindings.Should().HaveCount(2);
        var conditional = updated.Bindings.Single(b => b.Condition != null);
        conditional.Members.Should().ContainSingle().Which.Should().Be("user:scheduled@example.com",
            "we must not modify a conditional binding");
        var unconditional = updated.Bindings.Single(b => b.Condition == null && b.Role == "roles/storage.objectViewer");
        unconditional.Members.Should().ContainSingle().Which.Should().Be("allUsers");
    }
}
