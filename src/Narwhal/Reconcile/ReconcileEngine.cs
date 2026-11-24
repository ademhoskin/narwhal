using System.Globalization;
using Narwhal.Cli;
using Narwhal.Cloud;
using Narwhal.Config;
using Narwhal.Podman;

namespace Narwhal.Reconcile;

public sealed record ReconcileOutcome(
    ReconcileStatusDocument Status,
    ReconcileExitCode ExitCode,
    bool Busy = false,
    string? Message = null,
    bool RestartRequested = false);

/// <summary>One-shot reconcile engine shared by the timer and manual invocation.</summary>
public sealed class ReconcileEngine
{
    private readonly AgentConfig _config;
    private readonly ReconcileStore _store;
    private readonly IRegistryClient _registry;
    private readonly IContainerRuntime _runtime;
    private readonly ISecretResolver _secrets;
    private readonly Reporter _reporter;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _time;
    private readonly IStatusChannel? _statusChannel;
    private readonly string? _statusKeyId;
    private readonly AgentUpgrader? _agentUpgrader;
    private string? _hostId;
    private List<string> _cycleSecretValues = [];
    private readonly string _startedAt;

    public ReconcileEngine(
        AgentConfig config,
        ReconcileStore store,
        IRegistryClient registry,
        IContainerRuntime runtime,
        ISecretResolver secrets,
        Reporter reporter,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? time = null,
        IStatusChannel? statusChannel = null,
        string? statusKeyId = null,
        AgentUpgrader? agentUpgrader = null)
    {
        _config = config;
        _store = store;
        _registry = registry;
        _runtime = runtime;
        _secrets = secrets;
        _reporter = reporter;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _time = time ?? TimeProvider.System;
        _startedAt = Timestamp(_time.GetUtcNow());
        _statusChannel = statusChannel;
        _statusKeyId = statusKeyId;
        _agentUpgrader = agentUpgrader;
    }

    public async Task<ReconcileOutcome> RunOnceAsync(CancellationToken cancellationToken)
    {
        _cycleSecretValues = [];
        var started = _time.GetUtcNow();
        AgentRevertedUpgrade? reverted = null;
        if (_agentUpgrader is not null)
        {
            reverted = await _agentUpgrader.RevertExpiredAsync(cancellationToken);
            reverted ??= await _agentUpgrader.ReadRevertedAsync(cancellationToken);
        }
        var state = _store.LoadStateForRecovery(out var stateLost, out var stateWarning);
        if (stateWarning is not null)
            _reporter.Warn(stateWarning);
        var freshRevert = reverted is not null && ApplyRevertedAgent(state, reverted);

        var statusStore = new StatusStore(_config.StateDirectory);
        var history = statusStore.NewestHistory();
        var queued = statusStore.NewestQueued();
        _hostId = await HostIdentity.ResolveAsync(
            _config.Target.Provider,
            _config.HostId,
            cancellationToken);
        var hostIdChanged = _hostId is not null &&
                            state.LastHostId is not null &&
                            !string.Equals(_hostId, state.LastHostId, StringComparison.Ordinal);
        var cacheMissing = state.ActivePayloadDigest is not null &&
                           !_store.TryLoadArtifact(state.ActivePayloadDigest, out _);
        var cacheLost = cacheMissing && !state.ArtifactCacheMissing;

        await ApplyStatusOrderingAsync(
            state,
            stateLost,
            history,
            queued,
            hostIdChanged || cacheLost,
            cancellationToken);
        state.StatusEpoch = state.StatusEpoch == 0 ? 1 : state.StatusEpoch;
        state.StatusStateLost = state.StatusStateLost || stateLost;
        state.ArtifactCacheMissing = cacheMissing;
        if (_hostId is not null)
            state.LastHostId = _hostId;
        _store.SaveState(state);

        var status = NewStatus(state, started);
        if (freshRevert)
            await PublishTransitionAsync(state, status, ReconcilePhases.Degraded, cancellationToken);
        var outcome = await RunCycleAsync(state, status, started, stateLost, freshRevert, cancellationToken);
        return await PublishStatusAsync(state, outcome, cancellationToken);
    }

    private async Task ApplyStatusOrderingAsync(
        ReconcileState state,
        bool stateLost,
        StatusSnapshot? history,
        StatusSnapshot? queued,
        bool identityOrCacheLost,
        CancellationToken cancellationToken)
    {
        VerifiedStatus? remote = null;
        if (_statusChannel is not null)
            remote = await _statusChannel.TryReadLatestAsync(cancellationToken);

        var remoteEpoch = remote?.Document.Epoch ?? 0;
        var remoteSeq = remote?.Document.StatusSeq ?? 0;
        var baselineEpoch = Math.Max(
            state.StatusEpoch,
            Math.Max(
                state.LastKnownStatusEpoch,
                Math.Max(remoteEpoch, Math.Max(history?.Epoch ?? 0, queued?.Epoch ?? 0))));
        var baselineSeq = Math.Max(
            state.StatusSeq,
            Math.Max(
                state.LastKnownStatusSeq,
                Math.Max(remoteSeq, Math.Max(history?.StatusSeq ?? 0, queued?.StatusSeq ?? 0))));

        state.StatusEpoch = stateLost || identityOrCacheLost
            ? Math.Max(1, baselineEpoch + 1)
            : Math.Max(1, baselineEpoch);
        state.StatusSeq = baselineSeq;
        state.LastKnownStatusEpoch = Math.Max(state.LastKnownStatusEpoch, baselineEpoch);
        state.LastKnownStatusSeq = Math.Max(state.LastKnownStatusSeq, baselineSeq);
        if (remote is null)
            return;

        state.HighestGeneration = Math.Max(state.HighestGeneration, remote.Document.HighestGeneration);
        if (stateLost)
        {
            state.BlockedGeneration = remote.Document.BlockedGeneration;
            var remoteBlocked = AgentDigest.TryNormalize(remote.Document.Host.BlockedAgentDigest);
            if (state.BlockedAgentDigest is null)
                state.BlockedAgentDigest = remoteBlocked;
        }
    }

    private async Task<ReconcileOutcome> PublishStatusAsync(
        ReconcileState state,
        ReconcileOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (_statusChannel is null || _statusKeyId is null)
            return outcome;

        var now = _time.GetUtcNow();
        if (!StatusPublicationPolicy.ShouldPublish(state, outcome.Status, now))
            return outcome;

        outcome.Status.StatusSeq = ++state.StatusSeq;
        outcome.Status.Epoch = state.StatusEpoch;
        state.LastKnownStatusEpoch = state.StatusEpoch;
        state.LastKnownStatusSeq = state.StatusSeq;
        RedactStatus(outcome.Status);

        try
        {
            var result = await _statusChannel.PublishAsync(outcome.Status, cancellationToken);
            if (result.Published)
            {
                state.StatusPublishPending = false;
                state.LastStatusFingerprint = StatusPublicationPolicy.Fingerprint(outcome.Status);
                state.LastStatusPublishedAt = Timestamp(now);
            }
            else
            {
                state.StatusPublishPending = true;
                if (result.Warning is not null)
                    _reporter.Warn(result.Warning);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            state.StatusPublishPending = true;
            _reporter.Warn($"status publish failed: {e.Message}");
        }

        try
        {
            _store.SaveState(state);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ReconcileException)
        {
            _reporter.Warn($"cannot persist status sequence: {e.Message}");
        }

        return outcome;
    }

    private void RedactStatus(ReconcileStatusDocument status)
    {
        if (_cycleSecretValues.Count == 0 && _config.LogRedactPatterns.Count == 0)
            return;

        if (status.LastError is not null)
        {
            status.LastError.Message = StatusLogs.RedactText(
                status.LastError.Message,
                _cycleSecretValues,
                _config.LogRedactPatterns);
        }

        status.LogsTail = StatusLogs.BoundAndRedact(
            status.LogsTail,
            _cycleSecretValues,
            _config.LogRedactPatterns);
    }

    private string RedactMessage(string message) =>
        StatusLogs.RedactText(message, _cycleSecretValues, _config.LogRedactPatterns);

    private LastErrorInfo RedactError(LastErrorInfo error) => new()
    {
        Code = error.Code,
        Service = error.Service,
        Message = RedactMessage(error.Message),
        At = error.At,
    };

    private void MergeSecretValues(IEnumerable<ServicePlan>? plans)
    {
        if (plans is null)
            return;

        _cycleSecretValues = _cycleSecretValues
            .Concat(plans.SelectMany(plan => plan.Secrets).Select(secret => secret.Value))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private async Task CaptureFailureLogsAsync(
        ReconcileStatusDocument status,
        string? service,
        CancellationToken cancellationToken)
    {
        if (service is null)
            return;

        try
        {
            var container = ReconcileNames.Container(_config.Project, service);
            var lines = await _runtime.LogsTailAsync(container, 50, cancellationToken);
            var at = Timestamp(_time.GetUtcNow());
            var entries = lines.Select(line => new StatusLogLine
            {
                Service = service,
                At = at,
                Line = StatusLogs.RedactText(line, _cycleSecretValues, _config.LogRedactPatterns),
            });
            status.LogsTail = StatusLogs.BoundAndRedact(
                entries,
                _cycleSecretValues,
                _config.LogRedactPatterns);
        }
        catch (Exception e) when (e is PodmanException or ProcessFailureException or IOException or UnauthorizedAccessException)
        {
            _reporter.Warn($"cannot collect failure logs for '{service}': {e.Message}");
        }
    }

    /// <summary>
    /// Publishes an in-flight phase. A failure is queued and never changes the cycle outcome.
    /// </summary>
    private async Task PublishTransitionAsync(
        ReconcileState state,
        ReconcileStatusDocument status,
        string phase,
        CancellationToken cancellationToken,
        LastErrorInfo? error = null)
    {
        status.Phase = phase;
        status.LastError = error ?? state.LastError;
        status.Updated = Timestamp(_time.GetUtcNow());
        status.HighestGeneration = state.HighestGeneration;
        status.BlockedGeneration = state.BlockedGeneration;
        RedactStatus(status);

        if (_statusChannel is null || _statusKeyId is null)
            return;

        status.StatusSeq = ++state.StatusSeq;
        state.LastKnownStatusEpoch = state.StatusEpoch;
        state.LastKnownStatusSeq = state.StatusSeq;
        try
        {
            _store.SaveState(state);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ReconcileException)
        {
            _reporter.Warn($"cannot persist status sequence: {e.Message}");
        }

        try
        {
            var result = await _statusChannel.PublishAsync(status, cancellationToken);
            if (result.Published)
            {
                state.StatusPublishPending = false;
                state.LastStatusFingerprint = StatusPublicationPolicy.Fingerprint(status);
                state.LastStatusPublishedAt = Timestamp(_time.GetUtcNow());
            }
            else
            {
                state.StatusPublishPending = true;
                if (result.Warning is not null)
                    _reporter.Warn(result.Warning);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            state.StatusPublishPending = true;
            _reporter.Warn($"status publish failed: {e.Message}");
        }

        try
        {
            _store.SaveState(state);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ReconcileException)
        {
            _reporter.Warn($"cannot persist status sequence: {e.Message}");
        }
    }

    private async Task<ReconcileOutcome> RunCycleAsync(
        ReconcileState state,
        ReconcileStatusDocument status,
        DateTimeOffset started,
        bool stateLost,
        bool freshRevert,
        CancellationToken cancellationToken)
    {
        var journal = _store.LoadJournal(out var warning);
        if (warning is not null)
        {
            _reporter.Warn(warning);
            var alreadyLost = state.StatusStateLost;
            state.StatusStateLost = true;
            if (!alreadyLost)
            {
                state.StatusEpoch++;
                state.LastKnownStatusEpoch = state.StatusEpoch;
                status.Epoch = state.StatusEpoch;
                _store.SaveState(state);
            }
        }
        else if (!stateLost)
        {
            state.StatusStateLost = false;
        }

        try
        {
            switch (DecideRecovery(journal))
            {
                case RecoveryAction.ClearCommitted:
                    _store.ClearJournal();
                    journal = null;
                    break;
                case RecoveryAction.FinishRollback:
                    ReconcileStateFromJournal(state, journal!);
                    var error = journal!.LastError ?? new LastErrorInfo
                    {
                        Code = "health_failed",
                        Message = "recovered an interrupted apply",
                        At = Timestamp(_time.GetUtcNow()),
                    };
                    return await RollbackAsync(state, journal, status, error, cancellationToken);
                case RecoveryAction.ResumeApplying:
                    ReconcileStateFromJournal(state, journal!);
                    _store.SaveState(state);
                    break;
            }

            var trust = _store.LoadTrustSet();
            var resolved = await ResolveDesiredAsync(state, trust, cancellationToken);
            var document = resolved.Verified.Document;
            var generation = document.Generation;

            if (resolved.Verified.NextTrustSet is { } next && !TrustSetsEqual(trust, next))
            {
                _store.SaveTrustSet(next);
                trust = next;
            }

            _store.SaveArtifact(
                resolved.Verified.PayloadDigest,
                resolved.Payload,
                resolved.Signature,
                resolved.Manifest.Digest,
                resolved.Manifest.Bytes);

            if (_agentUpgrader is not null && document.Agent is { } agentSpec)
            {
                var upgrade = await _agentUpgrader.EvaluateAsync(agentSpec, state, cancellationToken);
                switch (upgrade.Action)
                {
                    case AgentUpgradeAction.Staged:
                        SetObserved(status, generation, resolved);
                        state.Phase = ReconcilePhases.Idle;
                        state.LastError = null;
                        _store.SaveState(state);
                        Finish(status, state);
                        return new ReconcileOutcome(
                            status,
                            ReconcileExitCode.Success,
                            Message: "agent upgrade staged",
                            RestartRequested: true);
                    case AgentUpgradeAction.Blocked:
                        SetObserved(status, generation, resolved);
                        if (!freshRevert || state.LastError?.Code != AgentUpgrader.UpgradeFailedCode)
                        {
                            state.Phase = ReconcilePhases.Degraded;
                            state.LastError = new LastErrorInfo
                            {
                                Code = AgentUpgrader.UpgradeBlockedCode,
                                Message = upgrade.Message ?? "agent binary is blocked",
                                At = Timestamp(_time.GetUtcNow()),
                            };
                        }

                        _store.SaveState(state);
                        Finish(status, state);
                        _reporter.Warn($"agent_upgrade_blocked: {upgrade.Message}");
                        return new ReconcileOutcome(status, ReconcileExitCode.Success);
                    case AgentUpgradeAction.Failed when upgrade.DigestBlocked:
                        SetObserved(status, generation, resolved);
                        state.BlockedAgentDigest = AgentDigest.FromBinarySha256(agentSpec.BinarySha256);
                        if (_agentUpgrader.InstalledVersion is { } installed)
                            state.AgentRollbackVersion = installed;
                        state.Phase = ReconcilePhases.Degraded;
                        state.LastError = new LastErrorInfo
                        {
                            Code = AgentUpgrader.UpgradeFailedCode,
                            Message = upgrade.Message ?? "agent upgrade failed",
                            At = Timestamp(_time.GetUtcNow()),
                        };
                        _store.SaveState(state);
                        Finish(status, state);
                        return new ReconcileOutcome(status, ReconcileExitCode.Success);
                    case AgentUpgradeAction.Failed:
                        _reporter.Warn($"agent upgrade deferred: {upgrade.Message}");
                        break;
                    case AgentUpgradeAction.DowngradeRefused:
                        break;
                }
            }

            var plans = await ServicePlanner.BuildAsync(document, _secrets, cancellationToken);
            MergeSecretValues(plans);
            EnsureBindMounts(plans, path => File.Exists(path) || Directory.Exists(path));

            var observations = await _runtime.ListAsync(_config.Project, cancellationToken);
            var imageMatches = await CheckImagesAsync(plans, observations, cancellationToken);
            var diff = ServiceDiffer.Diff(plans, observations, imageMatches);

            status.Target.Ingress = document.Target.Ingress.Select(rule => new StatusIngress
            {
                Protocol = rule.Protocol,
                Port = rule.Port,
                Source = rule.Source.ToList(),
            }).ToList();
            status.Observed = new StatusObserved
            {
                Generation = generation,
                PayloadDigest = resolved.Verified.PayloadDigest,
                ManifestDigest = resolved.Manifest.Digest,
                KeyId = resolved.Verified.VerifyingKeyIds.FirstOrDefault(),
            };
            status.Attempts = new StatusAttempts
            {
                Generation = generation,
                Count = 1,
                LastAt = Timestamp(_time.GetUtcNow()),
            };
            status.Services = DescribeServices(plans, diff);

            if (!diff.HasMutations)
            {
                RecordActiveGeneration(state, generation, resolved.Verified.PayloadDigest, resolved.Manifest.Digest);
                state.BlockedGeneration = state.BlockedGeneration == generation ? null : state.BlockedGeneration;
                state.Phase = ReconcilePhases.Idle;
                state.RollbackFailed = false;
                state.LastError = null;
                state.LastRejectedManifestDigest = null;
                state.LastRejectedCode = null;
                _store.SaveState(state);
                if (journal is not null)
                    ClearJournal();

                Finish(status, state);
                await MarkAgentHealthyAsync(cancellationToken);
                _reporter.Ok($"generation {generation} is active; no changes");
                return new ReconcileOutcome(status, ReconcileExitCode.Success);
            }

            var applyJournal = new ReconcileJournal
            {
                Generation = generation,
                PayloadDigest = resolved.Verified.PayloadDigest,
                ManifestDigest = resolved.Manifest.Digest,
                Phase = JournalPhases.Applying,
                HighestGeneration = Math.Max(state.HighestGeneration, generation),
                HighestPayloadDigest = resolved.Verified.PayloadDigest,
                BlockedGeneration = state.BlockedGeneration == generation ? null : state.BlockedGeneration,
                ActiveGeneration = state.ActiveGeneration,
                ActivePayloadDigest = state.ActivePayloadDigest,
                ActiveManifestDigest = state.ActiveManifestDigest,
                Actions = diff.Changes.Select(change => new JournalAction { Service = change.Plan.Name, Action = change.Action })
                    .Concat(diff.Orphans.Select(orphan => new JournalAction
                    {
                        Service = orphan.Service.Length == 0 ? orphan.Name : orphan.Service,
                        Action = ServiceActions.Stop,
                    }))
                    .ToList(),
                StartedAt = Timestamp(started),
            };
            _store.SaveJournal(applyJournal);

            try
            {
                var needsPull = imageMatches.Values.Any(match => !match) ||
                                diff.Changes.Any(change =>
                                    change.Action is ServiceActions.Create or ServiceActions.Recreate);
                if (needsPull)
                    await PublishTransitionAsync(state, status, ReconcilePhases.Pulling, cancellationToken);
                await PublishTransitionAsync(state, status, ReconcilePhases.Applying, cancellationToken);
                await ApplyAsync(diff, generation, applyJournal, cancellationToken);
            }
            catch (ApplyFailureException failure)
            {
                await CaptureFailureLogsAsync(status, failure.Service, cancellationToken);
                var error = RedactError(new LastErrorInfo
                {
                    Code = failure.Code,
                    Service = failure.Service,
                    Message = failure.Message,
                    At = Timestamp(_time.GetUtcNow()),
                });
                var failedAction = applyJournal.Actions.FirstOrDefault(action =>
                    string.Equals(action.Service, failure.Service, StringComparison.Ordinal));
                if (failedAction is not null && failure.MutationStarted)
                    failedAction.Touched = true;
                applyJournal.Phase = JournalPhases.RollingBack;
                applyJournal.BlockedGeneration = generation;
                applyJournal.LastError = error;
                _store.SaveJournal(applyJournal);
                state.HighestGeneration = Math.Max(state.HighestGeneration, generation);
                state.HighestPayloadDigest = resolved.Verified.PayloadDigest;
                state.BlockedGeneration = generation;
                state.LastPayloadDigest = resolved.Verified.PayloadDigest;
                state.LastManifestDigest = resolved.Manifest.Digest;
                state.Phase = ReconcilePhases.RollingBack;
                state.LastError = error;
                _store.SaveState(state);
                return await RollbackAsync(state, applyJournal, status, error, cancellationToken);
            }
            catch (Exception e) when (e is PodmanException or RegistryException or ProcessFailureException or IOException)
            {
                var error = RedactError(new LastErrorInfo
                {
                    Code = e is RegistryException ? "image_pull_failed" : "apply_failed",
                    Message = e.Message,
                    At = Timestamp(_time.GetUtcNow()),
                });
                applyJournal.Phase = JournalPhases.RollingBack;
                applyJournal.BlockedGeneration = generation;
                applyJournal.LastError = error;
                _store.SaveJournal(applyJournal);
                state.HighestGeneration = Math.Max(state.HighestGeneration, generation);
                state.HighestPayloadDigest = resolved.Verified.PayloadDigest;
                state.BlockedGeneration = generation;
                state.Phase = ReconcilePhases.RollingBack;
                state.LastError = error;
                _store.SaveState(state);
                return await RollbackAsync(state, applyJournal, status, error, cancellationToken);
            }

            await PublishTransitionAsync(state, status, ReconcilePhases.Verifying, cancellationToken);
            try
            {
                await RemoveOrphansAsync(diff, cancellationToken);
            }
            catch (Exception e) when (e is PodmanException or ProcessFailureException or IOException)
            {
                _reporter.Warn($"cannot remove orphan containers: {e.Message}");
            }

            RecordActiveGeneration(state, generation, resolved.Verified.PayloadDigest, resolved.Manifest.Digest);
            state.Phase = ReconcilePhases.Idle;
            state.RollbackFailed = false;
            state.LastError = null;
            state.LastRejectedManifestDigest = null;
            state.LastRejectedCode = null;
            _store.SaveState(state);
            ClearJournal();

            status.Phase = ReconcilePhases.Idle;
            try
            {
                status.Services = await DescribeActualServicesAsync(plans, cancellationToken);
            }
            catch (Exception e) when (e is PodmanException or ProcessFailureException or IOException)
            {
                _reporter.Warn($"cannot read container health for status: {e.Message}");
            }

            Finish(status, state);
            await MarkAgentHealthyAsync(cancellationToken);
            _reporter.Ok($"applied generation {generation}");
            return new ReconcileOutcome(status, ReconcileExitCode.Success);
        }
        catch (CloudArtifactException e)
        {
            return Fail(state, status, e.Code, e.Message, ReconcileExitCode.Verification, cancellationToken);
        }
        catch (ReconcileException e)
        {
            if (e.Code is CloudArtifactErrorCodes.GenerationReplay)
                status.Phase = ReconcilePhases.ReplayRejected;
            return Fail(state, status, e.Code, e.Message, e.ExitCode, cancellationToken, e.Service);
        }
        catch (RegistryException e)
        {
            var code = e.Auth ? ReconcilePhases.AuthFailed : ReconcilePhases.RegistryUnavailable;
            return Fail(state, status, code, e.Message, ReconcileExitCode.Runtime, cancellationToken);
        }
        catch (PodmanException e)
        {
            return Fail(state, status, "apply_failed", e.Message, ReconcileExitCode.Runtime, cancellationToken);
        }
        catch (ProcessFailureException e)
        {
            return Fail(state, status, "apply_failed", e.Message, ReconcileExitCode.Runtime, cancellationToken);
        }
    }

    /// <summary>Recovery decision from design section 3.6. Exposed for tests.</summary>
    public static RecoveryAction DecideRecovery(ReconcileJournal? journal)
    {
        if (journal is null)
            return RecoveryAction.Normal;
        if (string.Equals(journal.Phase, JournalPhases.Committed, StringComparison.Ordinal))
            return RecoveryAction.ClearCommitted;
        if (string.Equals(journal.Phase, JournalPhases.RollingBack, StringComparison.Ordinal))
            return RecoveryAction.FinishRollback;
        if (string.Equals(journal.Phase, JournalPhases.Applying, StringComparison.Ordinal))
            return journal.BlockedGeneration == journal.Generation
                ? RecoveryAction.FinishRollback
                : RecoveryAction.ResumeApplying;
        return RecoveryAction.Normal;
    }

    private async Task<ResolvedDesired> ResolveDesiredAsync(
        ReconcileState state,
        TrustSet trust,
        CancellationToken cancellationToken)
    {
        RegistryManifest manifest;
        try
        {
            manifest = await _registry.GetManifestAsync(_config.DesiredRepository, "desired", cancellationToken);
        }
        catch (RegistryException e) when (!e.Auth)
        {
            var cached = LoadCachedArtifact(state);
            if (cached is null)
                throw new ReconcileException(
                    "registry_unavailable",
                    $"cannot resolve desired artifact: {e.Message}",
                    ReconcileExitCode.Runtime,
                    ReconcilePhases.RegistryUnavailable);

            _reporter.Warn("registry is unavailable; reconciling from the cached artifact");
            var layers = OciManifestParser.Parse(cached.Artifact.Manifest, _config.DesiredRepository, "desired");
            var cachedManifest = new RegistryManifest(
                cached.Artifact.ManifestDigest,
                cached.Artifact.Manifest,
                layers,
                DesiredArtifactLayout.Select(layers));
            var resolved = await VerifyAsync(
                state,
                trust,
                cachedManifest,
                cached.Artifact.Payload,
                cached.Artifact.Signature,
                cached.Artifact.ManifestDigest,
                cancellationToken,
                new GenerationState());
            if (resolved.Verified.Document.Generation != cached.Generation)
            {
                throw new ReconcileException(
                    "cache_mismatch",
                    "cached payload does not match the persisted active generation",
                    ReconcileExitCode.Verification);
            }

            return resolved;
        }

        if (string.Equals(state.LastRejectedManifestDigest, manifest.Digest, StringComparison.Ordinal))
        {
            throw new ReconcileException(
                state.LastRejectedCode ?? CloudArtifactErrorCodes.SchemaInvalid,
                "the current desired manifest digest was already rejected",
                ReconcileExitCode.Verification);
        }

        var payload = await _registry.GetBlobAsync(_config.DesiredRepository, manifest.Selection.Payload.Digest, cancellationToken);
        var signature = await _registry.GetBlobAsync(_config.DesiredRepository, manifest.Selection.Signature.Digest, cancellationToken);
        return await VerifyAsync(state, trust, manifest, payload, signature, manifest.Digest, cancellationToken);
    }

    private async Task<ResolvedDesired> VerifyAsync(
        ReconcileState state,
        TrustSet trust,
        RegistryManifest manifest,
        byte[] payload,
        byte[] signature,
        string manifestDigest,
        CancellationToken cancellationToken,
        GenerationState? generationState = null)
    {
        VerifiedDesiredArtifact verified;
        try
        {
            verified = DesiredArtifactVerifier.Verify(payload, signature, new ArtifactVerificationContext
            {
                TrustSet = trust,
                Target = _config.Expectation,
                Now = _time.GetUtcNow(),
                Generation = generationState ?? new GenerationState
                {
                    HighestGeneration = state.HighestGeneration,
                    HighestPayloadDigest = state.HighestPayloadDigest,
                    BlockedGeneration = state.BlockedGeneration,
                },
                Layers = manifest.Layers,
            });
        }
        catch (CloudArtifactException e)
        {
            state.LastRejectedManifestDigest = manifestDigest;
            state.LastRejectedCode = e.Code;
            throw;
        }

        if (verified.Previous is { } previous)
            await VerifyPreviousAsync(trust, previous, cancellationToken);

        return new ResolvedDesired(manifest, payload, signature, verified);
    }

    private async Task VerifyPreviousAsync(TrustSet trust, PreviousArtifact previous, CancellationToken cancellationToken)
    {
        var reference = "g" + previous.Generation.ToString(CultureInfo.InvariantCulture);
        RegistryManifest manifest;
        byte[] payload;
        byte[] signature;
        try
        {
            manifest = await _registry.GetManifestAsync(_config.DesiredRepository, reference, cancellationToken);
            payload = await _registry.GetBlobAsync(_config.DesiredRepository, manifest.Selection.Payload.Digest, cancellationToken);
            signature = await _registry.GetBlobAsync(_config.DesiredRepository, manifest.Selection.Signature.Digest, cancellationToken);
        }
        catch (RegistryException e) when (!e.Auth)
        {
            if (_store.TryLoadArtifact(previous.Digest, out var cached) &&
                string.Equals(cached.ManifestDigest, previous.ManifestDigest, StringComparison.Ordinal))
            {
                payload = cached.Payload;
                signature = cached.Signature;
                manifest = new RegistryManifest(
                    cached.ManifestDigest,
                    cached.Manifest,
                    OciManifestParser.Parse(cached.Manifest, _config.DesiredRepository, reference),
                    DesiredArtifactLayout.Select(OciManifestParser.Parse(cached.Manifest, _config.DesiredRepository, reference)));
            }
            else
            {
                throw new ReconcileException(
                    "previous_unavailable",
                    $"cannot resolve previous generation {previous.Generation}: {e.Message}",
                    ReconcileExitCode.Verification);
            }
        }

        if (!string.Equals(manifest.Digest, previous.ManifestDigest, StringComparison.Ordinal))
            throw new ReconcileException(
                CloudArtifactErrorCodes.DigestMismatch,
                $"previous manifest digest does not match g{previous.Generation}",
                ReconcileExitCode.Verification);

        if (!string.Equals(CloudEncoding.Sha256Digest(payload), previous.Digest, StringComparison.Ordinal))
            throw new ReconcileException(
                CloudArtifactErrorCodes.DigestMismatch,
                $"previous payload digest does not match g{previous.Generation}",
                ReconcileExitCode.Verification);

        var verified = DesiredArtifactVerifier.Verify(payload, signature, new ArtifactVerificationContext
        {
            TrustSet = trust,
            Target = _config.Expectation,
            Now = _time.GetUtcNow(),
            Generation = new GenerationState(),
            Layers = manifest.Layers,
        });

        if (!string.Equals(verified.PayloadDigest, previous.Digest, StringComparison.Ordinal))
            throw new ReconcileException(
                CloudArtifactErrorCodes.DigestMismatch,
                $"previous payload does not match the signed reference",
                ReconcileExitCode.Verification);

        _store.SaveArtifact(verified.PayloadDigest, payload, signature, manifest.Digest, manifest.Bytes);
    }

    private CachedArtifactCandidate? LoadCachedArtifact(ReconcileState state)
    {
        if (state.ActiveGeneration is { } activeGeneration &&
            state.ActivePayloadDigest is { } activeDigest &&
            _store.TryLoadArtifact(activeDigest, out var active))
        {
            return new CachedArtifactCandidate(active, activeGeneration);
        }

        if (state.HighestGeneration > 0 &&
            state.HighestGeneration != state.BlockedGeneration &&
            state.HighestPayloadDigest is { } highestDigest &&
            _store.TryLoadArtifact(highestDigest, out var highest))
        {
            return new CachedArtifactCandidate(highest, state.HighestGeneration);
        }

        return null;
    }

    private static void RecordActiveGeneration(ReconcileState state, long generation, string payloadDigest, string manifestDigest)
    {
        if (generation >= state.HighestGeneration)
        {
            state.HighestGeneration = generation;
            state.HighestPayloadDigest = payloadDigest;
        }

        state.LastPayloadDigest = payloadDigest;
        state.LastManifestDigest = manifestDigest;
        state.ActiveGeneration = generation;
        state.ActivePayloadDigest = payloadDigest;
        state.ActiveManifestDigest = manifestDigest;
    }

    private bool ApplyRevertedAgent(ReconcileState state, AgentRevertedUpgrade reverted)
    {
        var digest = AgentDigest.FromBinarySha256(reverted.BinarySha256);
        state.BlockedAgentDigest = digest;
        if (reverted.PreviousVersion is { } previous)
            state.AgentRollbackVersion = previous;

        if (string.Equals(state.LastAgentRevertDigest, digest, StringComparison.Ordinal))
            return false;

        state.LastAgentRevertDigest = digest;
        state.Phase = ReconcilePhases.Degraded;
        state.LastError = new LastErrorInfo
        {
            Code = AgentUpgrader.UpgradeFailedCode,
            Message = reverted.PreviousVersion is { } known
                ? $"agent {reverted.Version} did not report boot-ok; reverted to {known}"
                : $"agent {reverted.Version} did not report boot-ok",
            At = Timestamp(_time.GetUtcNow()),
        };
        return true;
    }

    private async Task MarkAgentHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_agentUpgrader is not null)
            {
                if (!await _agentUpgrader.MarkHealthyAsync(cancellationToken))
                    _reporter.Warn("agent boot-ok was deferred because upgrade state is busy");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or AgentConfigException)
        {
            _reporter.Warn($"cannot record agent boot-ok: {e.Message}");
        }
    }

    private static void SetObserved(
        ReconcileStatusDocument status,
        long generation,
        ResolvedDesired resolved)
    {
        status.Observed = new StatusObserved
        {
            Generation = generation,
            PayloadDigest = resolved.Verified.PayloadDigest,
            ManifestDigest = resolved.Manifest.Digest,
            KeyId = resolved.Verified.VerifyingKeyIds.FirstOrDefault(),
        };
    }

    private async Task ApplyAsync(DiffPlan diff, long generation, ReconcileJournal journal, CancellationToken cancellationToken)
    {
        await _runtime.EnsureNetworkAsync(_config.Project, _config.Network, cancellationToken);

        foreach (var change in diff.Changes)
        {
            foreach (var volume in change.Plan.Spec.Volumes)
            {
                var source = volume.Split(':')[0];
                if (!source.StartsWith('/'))
                    await _runtime.EnsureVolumeAsync(_config.Project, source, cancellationToken);
            }
        }

        foreach (var change in diff.Changes)
        {
            if (change.Action == ServiceActions.None)
                continue;

            var plan = change.Plan;
            var name = ReconcileNames.Container(_config.Project, plan.Name);
            var journalAction = journal.Actions.FirstOrDefault(action =>
                string.Equals(action.Service, plan.Name, StringComparison.Ordinal));
            var touched = false;

            void Touch()
            {
                touched = true;
                if (journalAction is null || journalAction.Touched)
                    return;
                journalAction.Touched = true;
                _store.SaveJournal(journal);
            }

            _reporter.Step($"{change.Action} {plan.Name}");
            try
            {
                await EnsureImageAsync(plan, cancellationToken);

                if (change.Action == ServiceActions.Recreate)
                {
                    Touch();
                    await _runtime.RemoveAsync(name, cancellationToken);
                }

                if (change.Action is ServiceActions.Create or ServiceActions.Recreate)
                {
                    Touch();
                    foreach (var secret in plan.Secrets)
                        await _runtime.EnsureSecretAsync(secret.PodmanName, secret.Value, cancellationToken);
                    await _runtime.CreateAsync(_config.Project, plan, name, _config.Network, generation, cancellationToken);
                }
                else if (change.Action == ServiceActions.Start)
                {
                    Touch();
                    await _runtime.StartAsync(name, cancellationToken);
                }

                await HealthGateAsync(plan, name, cancellationToken);
                if (journalAction is not null)
                {
                    journalAction.Completed = true;
                    _store.SaveJournal(journal);
                }
            }
            catch (ApplyFailureException)
            {
                throw;
            }
            catch (Exception e) when (e is PodmanException or RegistryException or ProcessFailureException or IOException)
            {
                throw new ApplyFailureException(
                    e is RegistryException ? "image_pull_failed" : "apply_failed",
                    plan.Name,
                    e.Message,
                    touched);
            }
        }
    }

    private async Task EnsureImageAsync(ServicePlan plan, CancellationToken cancellationToken)
    {
        if (await _runtime.ImageMatchesDigestAsync(plan.ImageReference, plan.Image.Repository, plan.Spec.Image, cancellationToken))
            return;

        await _runtime.PullAsync(plan.ImageReference, cancellationToken);
        if (!await _runtime.ImageMatchesDigestAsync(plan.ImageReference, plan.Image.Repository, plan.Spec.Image, cancellationToken))
        {
            throw new ApplyFailureException(
                "image_digest_mismatch",
                plan.Name,
                $"pulled image does not report {plan.Image.Repository}@{plan.Spec.Image}");
        }
    }

    private async Task HealthGateAsync(ServicePlan plan, string name, CancellationToken cancellationToken)
    {
        if (plan.Spec.Health is not { } health)
        {
            await _delay(TimeSpan.FromSeconds(5), cancellationToken);
            var state = await _runtime.StateAsync(name, cancellationToken);
            if (!string.Equals(state, "running", StringComparison.Ordinal))
                throw new ApplyFailureException("health_exited", plan.Name, $"container is {state ?? "missing"}", mutationStarted: true);
            return;
        }

        var interval = TimeSpan.FromSeconds(health.Interval);
        var attempts = health.Retries + (health.StartPeriod ?? 0) / Math.Max(1, health.Interval);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var containerState = await _runtime.StateAsync(name, cancellationToken);
            if (containerState is "exited" or "dead")
                throw new ApplyFailureException("health_exited", plan.Name, $"container is {containerState}", mutationStarted: true);

            var status = await _runtime.HealthAsync(name, cancellationToken);
            if (string.Equals(status, "healthy", StringComparison.Ordinal))
                return;
            if (string.Equals(status, "unhealthy", StringComparison.Ordinal))
                throw new ApplyFailureException("health_unhealthy", plan.Name, "container reported unhealthy", mutationStarted: true);

            if (attempt + 1 < attempts)
                await _delay(interval, cancellationToken);
        }

        throw new ApplyFailureException("health_timeout", plan.Name, "container did not become healthy in time", mutationStarted: true);
    }

    private async Task RemoveOrphansAsync(DiffPlan diff, CancellationToken cancellationToken)
    {
        foreach (var orphan in diff.Orphans)
        {
            _reporter.Detail($"removing orphan {orphan.Name}");
            await _runtime.StopAsync(orphan.Name, cancellationToken);
            await _runtime.RemoveAsync(orphan.Name, cancellationToken);
        }
    }

    private async Task<ReconcileOutcome> RollbackAsync(
        ReconcileState state,
        ReconcileJournal journal,
        ReconcileStatusDocument status,
        LastErrorInfo error,
        CancellationToken cancellationToken)
    {
        var activeGeneration = journal.ActiveGeneration ?? state.ActiveGeneration;
        var activeDigest = state.ActivePayloadDigest ?? journal.ActivePayloadDigest;
        IReadOnlyList<ServicePlan>? previousPlans = null;
        if (activeGeneration is not null && activeDigest is not null && _store.TryLoadArtifact(activeDigest, out var cached))
        {
            try
            {
                var trust = _store.LoadTrustSet();
                var layers = OciManifestParser.Parse(cached.Manifest, _config.DesiredRepository, "g" + activeGeneration);
                var verified = DesiredArtifactVerifier.Verify(cached.Payload, cached.Signature, new ArtifactVerificationContext
                {
                    TrustSet = trust,
                    Target = _config.Expectation,
                    Now = _time.GetUtcNow(),
                    Generation = new GenerationState(),
                    Layers = layers,
                });
                if (string.Equals(verified.PayloadDigest, activeDigest, StringComparison.Ordinal))
                    previousPlans = await ServicePlanner.BuildAsync(verified.Document, _secrets, cancellationToken);
            }
            catch (Exception e) when (e is CloudArtifactException or ReconcileException or IOException)
            {
                _reporter.Warn($"cannot use the cached active payload for rollback: {e.Message}");
            }
        }

        MergeSecretValues(previousPlans);
        error = RedactError(error);

        state.HighestGeneration = Math.Max(state.HighestGeneration, journal.HighestGeneration);
        state.BlockedGeneration = journal.Generation;
        state.LastPayloadDigest = journal.PayloadDigest;
        state.LastManifestDigest = journal.ManifestDigest;
        state.Phase = ReconcilePhases.RollingBack;
        state.LastError = error;
        _store.SaveState(state);

        journal.Phase = JournalPhases.RollingBack;
        journal.LastError = error;
        journal.BlockedGeneration = journal.Generation;
        _store.SaveJournal(journal);

        await PublishTransitionAsync(state, status, ReconcilePhases.RollingBack, cancellationToken, error);

        var changed = journal.Actions
            .Where(action => action.Touched &&
                             action.Action is ServiceActions.Create or ServiceActions.Recreate or ServiceActions.Start)
            .Select(action => action.Service)
            .Distinct(StringComparer.Ordinal)
            .Reverse()
            .ToList();

        var previousByName = previousPlans?.ToDictionary(plan => plan.Name, StringComparer.Ordinal);
        var rollbackSucceeded = previousPlans is not null;
        foreach (var service in changed)
        {
            var name = ReconcileNames.Container(_config.Project, service);
            if (previousByName is not null && previousByName.TryGetValue(service, out var previous))
            {
                try
                {
                    await EnsureImageAsync(previous, cancellationToken);
                    await _runtime.RemoveAsync(name, cancellationToken);
                    foreach (var secret in previous.Secrets)
                        await _runtime.EnsureSecretAsync(secret.PodmanName, secret.Value, cancellationToken);
                    await _runtime.CreateAsync(_config.Project, previous, name, _config.Network, activeGeneration ?? 0, cancellationToken);
                    await HealthGateAsync(previous, name, cancellationToken);
                }
                catch (Exception e) when (e is PodmanException or RegistryException or ApplyFailureException or ReconcileException)
                {
                    _reporter.Warn($"rollback of '{service}' failed: {e.Message}");
                    rollbackSucceeded = false;
                    await SafeStopAsync(name, cancellationToken);
                }
            }
            else
            {
                await SafeStopAsync(name, cancellationToken);
            }
        }

        state.ActiveGeneration = activeGeneration;
        state.RollbackFailed = !rollbackSucceeded;
        state.Phase = rollbackSucceeded && activeGeneration is not null
            ? ReconcilePhases.Degraded
            : ReconcilePhases.Failed;
        state.HighestGeneration = Math.Max(state.HighestGeneration, journal.HighestGeneration);
        state.BlockedGeneration = journal.Generation;
        state.LastError = rollbackSucceeded
            ? error
            : new LastErrorInfo
            {
                Code = "rollback_failed",
                Service = error.Service,
                Message = "the previous generation could not be restored",
                At = Timestamp(_time.GetUtcNow()),
            };
        _store.SaveState(state);

        journal.Phase = JournalPhases.Committed;
        _store.SaveJournal(journal);
        _store.ClearJournal();

        status.Phase = state.Phase;
        status.Observed = new StatusObserved
        {
            Generation = journal.Generation,
            PayloadDigest = journal.PayloadDigest,
            ManifestDigest = journal.ManifestDigest,
        };
        status.HighestGeneration = state.HighestGeneration;
        status.BlockedGeneration = state.BlockedGeneration;
        status.Active = state.ActiveGeneration is { } statusGeneration
            ? new StatusActive
            {
                Generation = statusGeneration,
                PayloadDigest = state.ActivePayloadDigest ?? journal.ActivePayloadDigest ?? "",
                State = state.Phase == ReconcilePhases.Degraded ? "degraded" : "failed",
            }
            : null;
        status.LastError = state.LastError;
        status.Services = previousPlans is null
            ? new Dictionary<string, StatusService>(StringComparer.Ordinal)
            : await DescribeActualServicesAsync(previousPlans, cancellationToken);
        Finish(status, state);

        return new ReconcileOutcome(status, ReconcileExitCode.Runtime);
    }

    private async Task SafeStopAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await _runtime.StopAsync(name, cancellationToken);
        }
        catch (PodmanException)
        {
        }
    }

    private ReconcileOutcome Fail(
        ReconcileState state,
        ReconcileStatusDocument status,
        string code,
        string message,
        ReconcileExitCode exitCode,
        CancellationToken cancellationToken,
        string? service = null)
    {
        var priorError = state.LastError;
        var phase = code switch
        {
            CloudArtifactErrorCodes.GenerationReplay => ReconcilePhases.ReplayRejected,
            CloudArtifactErrorCodes.GenerationBlocked
                when state.ActiveGeneration is not null && !state.RollbackFailed => ReconcilePhases.Degraded,
            ReconcilePhases.RegistryUnavailable => ReconcilePhases.RegistryUnavailable,
            ReconcilePhases.AuthFailed => ReconcilePhases.AuthFailed,
            _ => ReconcilePhases.Failed,
        };

        var error = code == CloudArtifactErrorCodes.GenerationBlocked && state.RollbackFailed && priorError is not null
            ? RedactError(priorError)
            : RedactError(new LastErrorInfo
            {
                Code = code,
                Service = service,
                Message = RedactMessage(message),
                At = Timestamp(_time.GetUtcNow()),
            });
        state.Phase = phase;
        state.LastError = error;
        try
        {
            _store.SaveState(state);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ReconcileException)
        {
            _reporter.Warn($"cannot persist state: {e.Message}");
        }

        status.Phase = phase;
        status.LastError = error;
        status.HighestGeneration = state.HighestGeneration;
        status.BlockedGeneration = state.BlockedGeneration;
        status.Active = state.ActiveGeneration is { } activeGeneration && state.ActivePayloadDigest is { } activeDigest
            ? new StatusActive
            {
                Generation = activeGeneration,
                PayloadDigest = activeDigest,
                State = phase switch
                {
                    ReconcilePhases.Degraded => "degraded",
                    _ when state.RollbackFailed => "failed",
                    _ => "healthy",
                },
            }
            : null;
        Finish(status, state);
        _reporter.Error(error.Message);
        return new ReconcileOutcome(status, exitCode);
    }

    private async Task<Dictionary<string, bool>> CheckImagesAsync(
        IReadOnlyList<ServicePlan> plans,
        IReadOnlyList<ContainerObservation> observations,
        CancellationToken cancellationToken)
    {
        var byName = plans.ToDictionary(plan => plan.Name, StringComparer.Ordinal);
        var matches = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            if (!byName.TryGetValue(observation.Service, out var plan) || observation.ImageReference.Length == 0)
            {
                matches[observation.Name] = false;
                continue;
            }

            matches[observation.Name] = await _runtime.ImageMatchesDigestAsync(
                observation.ImageReference,
                plan.Image.Repository,
                plan.Spec.Image,
                cancellationToken);
        }

        return matches;
    }

    private Dictionary<string, StatusService> DescribeServices(IReadOnlyList<ServicePlan> plans, DiffPlan diff)
    {
        var byName = diff.Changes.ToDictionary(change => change.Plan.Name, StringComparer.Ordinal);
        var services = new Dictionary<string, StatusService>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            byName.TryGetValue(plan.Name, out var change);
            var project = change?.Observed?.Project.Length > 0 ? change.Observed.Project : _config.Project;
            services[plan.Name] = new StatusService
            {
                Container = ReconcileNames.Container(project, plan.Name),
                ImageDigest = plan.Spec.Image,
                State = change?.Observed?.State ?? "created",
                Health = change?.Observed?.Health,
            };
        }

        return services;
    }

    private async Task<Dictionary<string, StatusService>> DescribeActualServicesAsync(
        IReadOnlyList<ServicePlan> plans,
        CancellationToken cancellationToken)
    {
        var observations = await _runtime.ListAsync(_config.Project, cancellationToken);
        var byName = observations
            .Where(o => o.Service.Length > 0)
            .GroupBy(o => o.Service, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var services = new Dictionary<string, StatusService>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            byName.TryGetValue(plan.Name, out var observed);
            services[plan.Name] = new StatusService
            {
                Container = observed?.Name ?? ReconcileNames.Container(_config.Project, plan.Name),
                ImageDigest = plan.Spec.Image,
                State = observed?.State ?? "missing",
                Health = observed?.Health,
            };
        }

        return services;
    }

    internal static void EnsureBindMounts(IReadOnlyList<ServicePlan> plans, Func<string, bool> exists)
    {
        foreach (var plan in plans)
        {
            foreach (var volume in plan.Spec.Volumes)
            {
                var source = volume.Split(':')[0];
                if (source.StartsWith('/') && !exists(source))
                {
                    throw new ReconcileException(
                        "bind_mount_missing",
                        $"service '{plan.Name}' bind mount does not exist: {source}",
                        ReconcileExitCode.Runtime);
                }
            }
        }
    }

    private void ClearJournal()
    {
        _store.SaveJournal(new ReconcileJournal { Schema = ReconcileSchema.Journal, Phase = JournalPhases.Committed });
        _store.ClearJournal();
    }

    private static void ReconcileStateFromJournal(ReconcileState state, ReconcileJournal journal)
    {
        state.HighestGeneration = Math.Max(state.HighestGeneration, journal.HighestGeneration);
        state.HighestPayloadDigest = journal.HighestPayloadDigest ?? state.HighestPayloadDigest;
        state.BlockedGeneration = journal.BlockedGeneration;
        state.ActiveGeneration = journal.ActiveGeneration ?? state.ActiveGeneration;
        state.ActivePayloadDigest ??= journal.ActivePayloadDigest;
        state.ActiveManifestDigest ??= journal.ActiveManifestDigest;
        state.LastPayloadDigest = journal.PayloadDigest;
        state.LastManifestDigest = journal.ManifestDigest;
        state.Phase = journal.Phase == JournalPhases.RollingBack ? ReconcilePhases.RollingBack : ReconcilePhases.Applying;
    }

    internal static bool TrustSetsEqual(TrustSet left, TrustSet right)
    {
        if (left.Keys.Count != right.Keys.Count)
            return false;

        foreach (var key in left.Keys)
        {
            if (!right.TryFind(key.KeyId, out var other) ||
                !key.PublicKey.AsSpan().SequenceEqual(other.PublicKey) ||
                key.NotAfter != other.NotAfter)
                return false;
        }

        return true;
    }

    private ReconcileStatusDocument NewStatus(ReconcileState state, DateTimeOffset started) => new()
    {
        Project = _config.Project,
        Target = new StatusTarget
        {
            Provider = _config.Target.Provider,
            Region = _config.Target.Region,
            Arch = _config.Target.Arch,
            Selector = new Dictionary<string, string>(_config.Target.Selector, StringComparer.Ordinal),
        },
        Epoch = state.StatusEpoch,
        StatusSeq = state.StatusSeq,
        Updated = Timestamp(started),
        Host = new StatusHost
        {
            Id = _hostId,
            BootId = TryReadBootId(),
            StartedAt = _startedAt,
            NarwhaldVersion = _agentUpgrader?.InstalledVersion ?? App.Version,
            StatusKeyId = _statusKeyId,
            AgentRollbackVersion = state.AgentRollbackVersion,
            BlockedAgentDigest = AgentDigest.TryNormalize(state.BlockedAgentDigest),
        },
        Phase = state.Phase,
        HighestGeneration = state.HighestGeneration,
        BlockedGeneration = state.BlockedGeneration,
        Active = state.ActiveGeneration is { } generation && state.ActivePayloadDigest is { } digest
            ? new StatusActive { Generation = generation, PayloadDigest = digest }
            : null,
        LastError = state.LastError,
    };

    private void Finish(ReconcileStatusDocument status, ReconcileState state)
    {
        status.Phase = state.Phase;
        status.Epoch = state.StatusEpoch;
        status.StatusSeq = state.StatusSeq;
        status.Updated = Timestamp(_time.GetUtcNow());
        status.HighestGeneration = state.HighestGeneration;
        status.BlockedGeneration = state.BlockedGeneration;
        status.Host.AgentRollbackVersion = state.AgentRollbackVersion;
        status.Host.BlockedAgentDigest = AgentDigest.TryNormalize(state.BlockedAgentDigest);
        if (state.ActiveGeneration is { } generation && state.ActivePayloadDigest is { } digest)
        {
            status.Active = new StatusActive
            {
                Generation = generation,
                PayloadDigest = digest,
                State = state.Phase switch
                {
                    ReconcilePhases.Degraded => "degraded",
                    _ when state.RollbackFailed => "failed",
                    _ => "healthy",
                },
            };
        }

        status.LastError = state.LastError;
    }

    private static string? TryReadBootId()
    {
        try
        {
            var value = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            return value.Length == 0 ? null : value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private sealed record CachedArtifactCandidate(CachedArtifact Artifact, long Generation);

    private sealed record ResolvedDesired(
        RegistryManifest Manifest,
        byte[] Payload,
        byte[] Signature,
        VerifiedDesiredArtifact Verified);

    private sealed class ApplyFailureException(string code, string service, string message, bool mutationStarted = false)
        : Exception(message)
    {
        public string Code { get; } = code;
        public string Service { get; } = service;
        public bool MutationStarted { get; } = mutationStarted;
    }
}

public enum RecoveryAction
{
    Normal,
    ClearCommitted,
    ResumeApplying,
    FinishRollback,
}
