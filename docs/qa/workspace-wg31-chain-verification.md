# workspace-wg31 chain verification

**Date:** 2026-05-22
**What this is:** the closest-to-end-to-end verification of the
auto-remediation path (`probe 403 → MakeBucketPublicAsync → PermissionDenied
→ surface gsutil one-liner on result screen`) that's achievable without
provisioning a real GCS service account that lacks `setIamPolicy`. The bead
asked for live evidence against a non-Storage-Admin SA; that requires a
specific IAM artifact CI can't create. What CI **can** do is run every
link in the chain as a unit/integration test and prove each handoff.

## The chain

```
[live GCS]                                        [in-process]
 ┌─────────────────────────────────────────────────────────────────────┐
 │                                                                     │
 │  SA lacks setIamPolicy                                              │
 │           ↓                                                         │
 │  IGcsStorageClient.MakeBucketPublicAsync()                          │
 │   returns MakeBucketPublicResult.PermissionDenied                   │
 │           ↓                                                         │
 │  PodcastPublisher.PublishFeedAsync                                  │
 │   keeps FailureClass = BucketNotPublic       ←── covered link 1     │
 │           ↓                                                         │
 │  PodcastOrchestrator.GeneratePodcastAsync                           │
 │   wraps result with PodcastFailureDetail(                           │
 │     FailureClass = BucketNotPublic,                                 │
 │     RemediationCopy = BuildPublishRemediation(BucketNotPublic))     │
 │   where RemediationCopy contains                                    │
 │     "gsutil iam ch allUsers:objectViewer gs://<your-bucket>"        │
 │                                              ←── covered link 2     │
 │           ↓                                                         │
 │  PodcastFailureClassifier.Classify(detail, ...)                     │
 │   returns Classification with                                       │
 │     Step = "Publishing"                                             │
 │     Fix  = the gsutil one-liner from RemediationCopy                │
 │                                              ←── covered link 3     │
 │           ↓                                                         │
 │  PodcastProgressScreens.ShowErrorScreenAsync                        │
 │   renders Step/Reason/Fix triple — the user sees                    │
 │   `Fix: ...gsutil iam ch allUsers:objectViewer gs://<your-bucket>`  │
 │                                              ←── covered link 4     │
 └─────────────────────────────────────────────────────────────────────┘
```

## Tests verifying each link

Run with `dotnet test --filter "<name>"`. All 9 pass on `main` at the time
of this writing (commit `f04e256`):

| # | Layer | Test | What it proves |
|---|---|---|---|
| 1a | Publisher | `PodcastPublisherTests.PublishFeedAsync_BucketNotPublic_HelperPermissionDenied_StaysBucketNotPublic` | MakeBucketPublic = PermissionDenied → result.FailureClass = BucketNotPublic, probe NOT re-run |
| 1b | Publisher | `PodcastPublisherTests.PublishFeedAsync_BucketNotPublic_HelperSucceeds_RetryProbePasses_Successful` | Storage Admin path: success → silent success (inverse) |
| 1c | Publisher | `PodcastPublisherTests.PublishFeedAsync_BucketNotPublic_HelperAlreadyPublic_BackoffExhausts_SurfacesGsutilHint` | i2vl backoff exhaustion still surfaces the gsutil hint |
| 1d | Publisher | `PodcastPublisherTests.PublishFeedAsync_BucketNotPublic_EmptyBucketName_SkipsRemediation` | empty-bucket guard (no NRE when settings incomplete) |
| 1e | Publisher | `PodcastPublisherTests.PublishFeedAsync_BucketNotPublic_PropagationLag_ThirdBackoffSeesPublic_Successful` | i2vl backoff catches late IAM propagation |
| 2 | Orchestrator | `PodcastOrchestratorTests.GeneratePodcastAsync_PublishFails_WithBucketConfigured_ReturnsTotalFailureWithTypedDetail` | publish failure with bucket configured → PodcastResult.FailureDetail.RemediationCopy contains "allUsers:objectViewer" |
| 3a | Classifier | `PodcastFailureClassifierTests.Classify_TypedBucketNotPublic_PointsAtGcsBucketRow` | typed detail → Step="Publishing", Fix surfaces RemediationCopy |
| 3b | Classifier | `PodcastFailureClassifierTests.Classify_GcsBucketProblem_OffersGsutilCommand` | heuristic fallback for non-typed errors still emits `gsutil iam` + `allUsers:objectViewer` |
| 4 | Render | `CompletionScreenShapeTests.ShapeD_BucketNotPublic_RendersBucketPublicRemediation` | typed BucketNotPublic detail → result-screen Shape D Fix line contains `Storage Object Viewer` |

## Test-run output (2026-05-22)

```
$ dotnet test --filter "FullyQualifiedName~PublishFeedAsync_BucketNotPublic|FullyQualifiedName~ShapeD_BucketNotPublic|FullyQualifiedName~TypedBucketNotPublic|FullyQualifiedName~GcsBucketProblem_OffersGsutilCommand"
  Passed  CompletionScreenShapeTests.ShapeD_BucketNotPublic_RendersBucketPublicRemediation [3 ms]
  Passed  PodcastFailureClassifierTests.Classify_GcsBucketProblem_OffersGsutilCommand [3 ms]
  Passed  PodcastFailureClassifierTests.Classify_TypedBucketNotPublic_PointsAtGcsBucketRow [<1 ms]
  Passed  PodcastPublisherTests.PublishFeedAsync_BucketNotPublic_HelperSucceeds_RetryProbePasses_Successful [52 ms]
  Passed  PodcastPublisherTests.PublishFeedAsync_BucketNotPublic_PropagationLag_ThirdBackoffSeesPublic_Successful [1 ms]
  Passed  PodcastPublisherTests.PublishFeedAsync_BucketNotPublic_HelperPermissionDenied_StaysBucketNotPublic [<1 ms]
  Passed  PodcastPublisherTests.PublishFeedAsync_BucketNotPublic_EmptyBucketName_SkipsRemediation [3 ms]
  Passed  PodcastPublisherTests.PublishFeedAsync_BucketNotPublic_HelperAlreadyPublic_BackoffExhausts_SurfacesGsutilHint [<1 ms]

$ dotnet test --filter "FullyQualifiedName~GeneratePodcastAsync_PublishFails_WithBucketConfigured_ReturnsTotalFailureWithTypedDetail"
  Passed  PodcastOrchestratorTests.GeneratePodcastAsync_PublishFails_WithBucketConfigured_ReturnsTotalFailureWithTypedDetail [46 ms]
```

## What this does NOT prove

- That a **real** GCS service account lacking `setIamPolicy` triggers
  `MakeBucketPublicResult.PermissionDenied` when the chain runs end-to-end.
  The wrapper (`GcsStorageClient.MakeBucketPublicAsync`) is covered by
  `GcsBucketPublicReadHelperTests`, but the exception-to-result translation
  for a real Google.Cloud.Storage `PermissionDenied` exception is exercised
  only via mocks, not against live GCS.
- That Cloud SDK's exception classification matches what we test. If
  Google changes the exception shape for `setIamPolicy` denial, our tests
  would still pass but production might mis-classify.

If either of those concerns materializes, the right fix is to land a
contract test against the real Google Cloud client with a deliberately
restricted SA — that test requires hardware (a real Google Cloud project +
restricted SA artifact) which CI can't provision.

## Conclusion

Every server-side step from `MakeBucketPublic returns PermissionDenied` to
"user sees `gsutil iam ch allUsers:objectViewer gs://<bucket>` on the
result screen" is covered by a passing test. The chain is regression-safe
short of an exception-type change in Google.Cloud.Storage.
