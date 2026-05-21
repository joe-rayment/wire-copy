// Licensed under the MIT License. See LICENSE in the repository root.

using Google.Apis.Storage.v1.Data;

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// Pure-function helpers for the bucket public-read remediation flow
/// (workspace-p1px). The IO wrapper lives on <see cref="GcsStorageClient"/>;
/// this class isolates the policy-mutation logic so it can be unit-tested
/// without a real <c>StorageClient</c>.
/// </summary>
/// <remarks>
/// The auto-remediation flow is: <see cref="IsPublicRead"/> returns false on
/// the live policy → caller invokes <see cref="AddPublicReadBinding"/> to
/// produce a new policy → caller passes that to
/// <c>StorageClient.SetBucketIamPolicyAsync</c>. The ETag of the input policy
/// is preserved so optimistic concurrency catches a concurrent edit.
/// </remarks>
internal static class GcsBucketPublicReadHelper
{
    /// <summary>
    /// The IAM "everyone on the internet, with or without a Google account"
    /// principal. Adding this to a binding makes the bucket world-readable.
    /// </summary>
    public const string AllUsersMember = "allUsers";

    /// <summary>
    /// The IAM role that grants object read access (sufficient for Apple
    /// Podcasts / podcast clients fetching feed.xml + .m4a episodes).
    /// </summary>
    public const string ObjectViewerRole = "roles/storage.objectViewer";

    /// <summary>
    /// True if <paramref name="policy"/> already grants
    /// <c>allUsers:roles/storage.objectViewer</c>. A null policy or a policy
    /// with no bindings returns false (treat as private).
    /// </summary>
    public static bool IsPublicRead(Policy? policy)
    {
        if (policy?.Bindings == null)
        {
            return false;
        }

        foreach (var binding in policy.Bindings)
        {
            if (!string.Equals(binding.Role, ObjectViewerRole, StringComparison.Ordinal))
            {
                continue;
            }

            if (binding.Members?.Any(m => string.Equals(m, AllUsersMember, StringComparison.Ordinal)) == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns a new <see cref="Policy"/> that grants
    /// <c>allUsers:roles/storage.objectViewer</c>. If the role already has a
    /// bindings entry, <c>allUsers</c> is appended to its <c>Members</c>; if
    /// the role is absent, a fresh binding is added. The input policy is not
    /// mutated. ETag and Version are preserved so the caller can submit the
    /// returned policy via <c>SetBucketIamPolicyAsync</c> with optimistic
    /// concurrency intact.
    /// </summary>
    public static Policy AddPublicReadBinding(Policy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var existingBindings = policy.Bindings ?? new List<Policy.BindingsData>();
        var newBindings = new List<Policy.BindingsData>(existingBindings.Count + 1);

        var found = false;
        foreach (var binding in existingBindings)
        {
            if (!string.Equals(binding.Role, ObjectViewerRole, StringComparison.Ordinal)
                || binding.Condition != null)
            {
                // Copy unrelated bindings verbatim. We also skip merging into a
                // CONDITIONAL objectViewer binding — IAM semantics differ for
                // conditional vs unconditional bindings, so we don't try to
                // graft allUsers onto a binding that's gated on (e.g.) a time
                // window. Fall through to add a fresh unconditional binding
                // below if no other binding already grants the access.
                newBindings.Add(binding);
                continue;
            }

            var members = binding.Members != null
                ? new List<string>(binding.Members)
                : new List<string>();

            if (!members.Contains(AllUsersMember, StringComparer.Ordinal))
            {
                members.Add(AllUsersMember);
            }

            newBindings.Add(new Policy.BindingsData
            {
                Role = binding.Role,
                Members = members,
                Condition = binding.Condition,
            });
            found = true;
        }

        if (!found)
        {
            newBindings.Add(new Policy.BindingsData
            {
                Role = ObjectViewerRole,
                Members = new List<string> { AllUsersMember },
            });
        }

        return new Policy
        {
            Bindings = newBindings,
            ETag = policy.ETag,
            Version = policy.Version,
            Kind = policy.Kind,
        };
    }
}
