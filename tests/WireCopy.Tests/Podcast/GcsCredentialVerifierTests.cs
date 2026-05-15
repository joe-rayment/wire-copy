// Licensed under the MIT License. See LICENSE in the repository root.

using System.Net;
using FluentAssertions;
using Google;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Infrastructure.Podcast;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// Unit tests for the four-step verify probe (workspace-cgnt).
/// Substitutes a deterministic <see cref="IGcsVerifyOperations"/> fake so
/// each failure class can be exercised without hitting GCS. Past attempts
/// at the verify step substituted a GET-bucket-only check; these tests
/// document the four real steps (Auth → Upload → Download+compare →
/// Delete) and the expected failure-class mapping.
/// </summary>
[Trait("Category", "Unit")]
public class GcsCredentialVerifierTests
{
    private const string Bucket = "wirecopy-tests";

    [Fact]
    public async Task VerifyAsync_AllStepsPass_ReturnsSuccessWithFourTimings()
    {
        var fake = new FakeOps();
        var result = await GcsCredentialVerifier.VerifyAsync(fake, Bucket, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.FailedAt.Should().Be(GcsVerifyStep.None);
        result.FailureClass.Should().Be(GcsVerifyFailureClass.None);
        result.ProbeObjectName.Should().StartWith("wirecopy-verify-");
        result.ProbeObjectName.Should().EndWith(".txt");

        fake.AuthCalls.Should().Be(1);
        fake.UploadCalls.Should().Be(1);
        fake.DownloadCalls.Should().Be(1);
        fake.DeleteCalls.Should().Be(1);

        // Each step's elapsed time must be non-negative; on fast machines
        // the duration may round to zero ticks but should never be negative.
        result.AuthDuration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        result.UploadDuration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        result.DownloadDuration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        result.DeleteDuration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task VerifyAsync_UploadForbidden_ReturnsIamMissingAndStopsBeforeDownload()
    {
        var fake = new FakeOps
        {
            UploadException = new GoogleApiException("Storage", "objects.create denied")
            {
                HttpStatusCode = HttpStatusCode.Forbidden,
            },
        };

        var result = await GcsCredentialVerifier.VerifyAsync(fake, Bucket, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailedAt.Should().Be(GcsVerifyStep.Upload);
        result.FailureClass.Should().Be(GcsVerifyFailureClass.IamMissing);
        result.Message.Should().Contain("Storage Object Admin");

        fake.AuthCalls.Should().Be(1);
        fake.UploadCalls.Should().Be(1);
        fake.DownloadCalls.Should().Be(0); // must NOT proceed past upload failure
        fake.DeleteCalls.Should().Be(0);
    }

    [Fact]
    public async Task VerifyAsync_DownloadReturnsCorruptedBytes_ReturnsReadbackMismatchAndAttemptsCleanup()
    {
        var fake = new FakeOps
        {
            CorruptDownload = true,
        };

        var result = await GcsCredentialVerifier.VerifyAsync(fake, Bucket, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailedAt.Should().Be(GcsVerifyStep.Download);
        result.FailureClass.Should().Be(GcsVerifyFailureClass.ReadbackMismatch);
        result.Message.Should().Contain("read back");

        // Cleanup must still attempt to delete the sentinel object so the
        // bucket isn't littered.
        fake.DeleteCalls.Should().Be(1);
    }

    [Fact]
    public async Task VerifyAsync_BucketNotFound_ReturnsBucketNotFoundFromUpload()
    {
        var fake = new FakeOps
        {
            UploadException = new GoogleApiException("Storage", "bucket does not exist")
            {
                HttpStatusCode = HttpStatusCode.NotFound,
            },
        };

        var result = await GcsCredentialVerifier.VerifyAsync(fake, Bucket, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailedAt.Should().Be(GcsVerifyStep.Upload);
        result.FailureClass.Should().Be(GcsVerifyFailureClass.BucketNotFound);
        result.Message.Should().Contain("Bucket not found");
    }

    [Fact]
    public async Task VerifyAsync_BillingDisabledMessage_ReturnsBillingDisabledClass()
    {
        var fake = new FakeOps
        {
            UploadException = new GoogleApiException("Storage", "The project to be billed is associated with an absent billing account: billingDisabled.")
            {
                HttpStatusCode = HttpStatusCode.Forbidden,
            },
        };

        var result = await GcsCredentialVerifier.VerifyAsync(fake, Bucket, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailedAt.Should().Be(GcsVerifyStep.Upload);
        result.FailureClass.Should().Be(GcsVerifyFailureClass.BillingDisabled);
        result.Message.Should().Contain("Billing");
    }

    [Fact]
    public async Task VerifyAsync_AuthFailureFromOps_ReturnsInvalidServiceAccountJson()
    {
        var fake = new FakeOps
        {
            AuthFailure = (GcsVerifyFailureClass.InvalidServiceAccountJson, "key file not parseable"),
        };

        var result = await GcsCredentialVerifier.VerifyAsync(fake, Bucket, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailedAt.Should().Be(GcsVerifyStep.Auth);
        result.FailureClass.Should().Be(GcsVerifyFailureClass.InvalidServiceAccountJson);

        // Auth failure must short-circuit — never call upload/download/delete.
        fake.UploadCalls.Should().Be(0);
        fake.DownloadCalls.Should().Be(0);
        fake.DeleteCalls.Should().Be(0);
    }

    [Fact]
    public async Task VerifyAsync_NetworkExceptionDuringUpload_ReturnsNetworkFailure()
    {
        var fake = new FakeOps
        {
            UploadException = new HttpRequestException("connection refused"),
        };

        var result = await GcsCredentialVerifier.VerifyAsync(fake, Bucket, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailedAt.Should().Be(GcsVerifyStep.Upload);
        result.FailureClass.Should().Be(GcsVerifyFailureClass.NetworkFailure);
    }

    [Fact]
    public void ClassifyAuthException_FileNotFound_ReturnsInvalidServiceAccountJson()
    {
        var cls = GcsCredentialVerifier.ClassifyAuthException(new FileNotFoundException("missing"));
        cls.Should().Be(GcsVerifyFailureClass.InvalidServiceAccountJson);
    }

    [Fact]
    public void ClassifyStorageException_RegionConstraint_ReturnsRegionBlock()
    {
        var ex = new GoogleApiException("Storage", "locationConstraint not satisfied")
        {
            HttpStatusCode = HttpStatusCode.PreconditionFailed,
        };
        GcsCredentialVerifier.ClassifyStorageException(ex)
            .Should().Be(GcsVerifyFailureClass.RegionBlock);
    }

    /// <summary>
    /// workspace-4l1l: IProgress&lt;GcsVerifyStep&gt; must fire once per step
    /// (Auth, Upload, Download, Delete) on the happy path. We assert SET
    /// membership rather than exact order because Progress&lt;T&gt; dispatches
    /// callbacks via the captured sync context (the threadpool here) and
    /// reordering is allowed; the UI uses each report as a "set current
    /// step" signal, not a sequence.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_ProgressReports_FireOncePerStepOnHappyPath()
    {
        var fake = new FakeOps();
        var reported = new System.Collections.Concurrent.ConcurrentBag<GcsVerifyStep>();
        var progress = new Progress<GcsVerifyStep>(s => reported.Add(s));

        var result = await GcsCredentialVerifier.VerifyAsync(
            fake, Bucket, CancellationToken.None, clock: null, progress: progress);

        result.Success.Should().BeTrue();

        for (var i = 0; i < 50 && reported.Count < 4; i++)
        {
            await Task.Delay(20);
        }

        reported.Should().BeEquivalentTo(new[]
        {
            GcsVerifyStep.Auth,
            GcsVerifyStep.Upload,
            GcsVerifyStep.Download,
            GcsVerifyStep.Delete,
        });
    }

    [Fact]
    public async Task VerifyAsync_ProgressReports_StopAtFailingStep()
    {
        var fake = new FakeOps
        {
            UploadException = new GoogleApiException("Storage", "objects.create denied")
            {
                HttpStatusCode = HttpStatusCode.Forbidden,
            },
        };
        var reported = new System.Collections.Concurrent.ConcurrentBag<GcsVerifyStep>();
        var progress = new Progress<GcsVerifyStep>(s => reported.Add(s));

        var result = await GcsCredentialVerifier.VerifyAsync(
            fake, Bucket, CancellationToken.None, clock: null, progress: progress);

        result.Success.Should().BeFalse();
        result.FailedAt.Should().Be(GcsVerifyStep.Upload);

        for (var i = 0; i < 50 && reported.Count < 2; i++)
        {
            await Task.Delay(20);
        }

        // Settle: catch any spurious late reports that would indicate the
        // verifier did not short-circuit on failure.
        await Task.Delay(60);

        // Auth + Upload fire; Download + Delete do NOT.
        reported.Should().BeEquivalentTo(new[] { GcsVerifyStep.Auth, GcsVerifyStep.Upload });
    }

    /// <summary>
    /// Deterministic fake of <see cref="IGcsVerifyOperations"/> that records
    /// call counts and lets each step be configured to throw.
    /// </summary>
    private sealed class FakeOps : IGcsVerifyOperations
    {
        public int AuthCalls { get; private set; }

        public int UploadCalls { get; private set; }

        public int DownloadCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public (GcsVerifyFailureClass Class, string Message)? AuthFailure { get; init; }

        public Exception? UploadException { get; init; }

        public Exception? DownloadException { get; init; }

        public Exception? DeleteException { get; init; }

        public bool CorruptDownload { get; init; }

        private byte[]? _lastUpload;

        public Task<(GcsVerifyFailureClass? Failure, string? Message)> AuthenticateAsync(CancellationToken ct)
        {
            AuthCalls++;
            if (AuthFailure is { } f)
            {
                return Task.FromResult<(GcsVerifyFailureClass?, string?)>((f.Class, f.Message));
            }

            return Task.FromResult<(GcsVerifyFailureClass?, string?)>((null, null));
        }

        public Task UploadAsync(string bucket, string objectName, byte[] content, CancellationToken ct)
        {
            UploadCalls++;
            if (UploadException != null)
            {
                throw UploadException;
            }

            _lastUpload = content;
            return Task.CompletedTask;
        }

        public Task<byte[]> DownloadAsync(string bucket, string objectName, CancellationToken ct)
        {
            DownloadCalls++;
            if (DownloadException != null)
            {
                throw DownloadException;
            }

            if (CorruptDownload)
            {
                return Task.FromResult(new byte[] { 0xff, 0x00 });
            }

            return Task.FromResult(_lastUpload ?? Array.Empty<byte>());
        }

        public Task DeleteAsync(string bucket, string objectName, CancellationToken ct)
        {
            DeleteCalls++;
            if (DeleteException != null)
            {
                throw DeleteException;
            }

            return Task.CompletedTask;
        }
    }
}
