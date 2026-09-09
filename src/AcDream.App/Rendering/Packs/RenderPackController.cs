using AcDream.App.Plugins;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.Plugin.Abstractions.Rendering;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Rendering.Packs;

internal sealed record RenderPackCatalogEntry(
    RenderPackDescriptor Descriptor,
    IRenderPackAssets Assets,
    long RegistrationId,
    bool IsCompatible,
    string? IncompatibilityReason,
    IReadOnlyDictionary<string, string?> PresetIncompatibilityReasons);

internal sealed class RenderPackCatalog
{
    private readonly Dictionary<string, RenderPackCatalogEntry> _entries;

    private RenderPackCatalog(Dictionary<string, RenderPackCatalogEntry> entries)
    {
        _entries = entries;
    }

    internal IReadOnlyList<RenderPackCatalogEntry> Entries =>
        _entries.Values
            .OrderBy(static value => value.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static value => value.Descriptor.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal bool TryGet(string id, out RenderPackCatalogEntry entry) =>
        _entries.TryGetValue(id, out entry!);

    internal static RenderPackCatalog Build(
        IEnumerable<BufferedRenderPackRegistration> registrations,
        RenderPackHostCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(capabilities);
        var entries = new Dictionary<string, RenderPackCatalogEntry>(
            StringComparer.OrdinalIgnoreCase);

        foreach (BufferedRenderPackRegistration registration in registrations)
        {
            RenderPackDescriptor descriptor = registration.Descriptor;
            RenderPackValidationResult validation =
                RenderPackValidator.ValidateDescriptor(descriptor, capabilities);
            if (entries.ContainsKey(descriptor.Id))
                continue;

            IReadOnlyDictionary<string, string?> presetReasons =
                descriptor.QualityPresets is null
                    ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    : descriptor.QualityPresets
                        .Where(static preset => preset is not null
                            && !string.IsNullOrWhiteSpace(preset.Id))
                        .GroupBy(static preset => preset.Id, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            static group => group.Key,
                            group =>
                            {
                                RenderPackValidationResult result =
                                    RenderPackValidator.ValidatePresetCompatibility(
                                        descriptor,
                                        group.First(),
                                        capabilities);
                                return result.Success ? null : result.Reason;
                            },
                            StringComparer.OrdinalIgnoreCase);
            entries.Add(
                descriptor.Id,
                new RenderPackCatalogEntry(
                    descriptor,
                    registration.Assets,
                    registration.RegistrationId,
                    validation.Success,
                    validation.Reason,
                    presetReasons));
        }

        return new RenderPackCatalog(entries);
    }
}

internal interface IRenderPackRuntime : IDisposable
{
    RenderPackDescriptor Descriptor { get; }

    RenderQualityPreset Preset { get; }
}

internal interface IDefaultWorldPathRenderPackRuntime : IRenderPackRuntime
{
}

internal interface IRenderPackRuntimeFactory
{
    IRenderPackRuntime Build(
        RenderPackDescriptor descriptor,
        ValidatedRenderPackShaderAssets assets,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string> userSettingOverrides);
}

internal enum RenderPackActivationState
{
    Retail,
    CandidatePending,
    Active,
    FailedToRetail,
}

internal readonly record struct RenderPackActivationSnapshot(
    RenderPackActivationState State,
    RenderPackSelectionSettings Selection,
    string? ActivePackDisplayName,
    string? Reason,
    long ActivationGeneration);

internal readonly record struct RenderPackActivationExtent(
    int Width,
    int Height,
    int SampleCount)
{
    internal void Validate()
    {
        if (Width <= 0 || Height <= 0 || SampleCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(Width), "Activation extent must be positive.");
    }
}

internal sealed class RenderPackController :
    IDisposable,
    IRenderPackDiagnosticsSnapshotSource
{
    private readonly Func<RenderPackCatalog> _catalog;
    private readonly IRenderPackRuntimeFactory _factory;
    private readonly IRenderPackReceiverPipelineCoordinator? _receiverPipelines;
    private readonly IRenderPackPreparationScheduler _preparationScheduler;
    private readonly RenderPackCatalogSource? _catalogSource;
    private readonly Dictionary<RenderPackSelectionSettings, long> _failedSelections = [];
    private readonly RenderPackPerformanceWindow _performance = new();
    private RenderPackSelectionSettings? _pending;
    private AtmosphericAutoQualityController? _autoQuality;
    private AtmosphericQualityLevel? _pendingAutoQuality;
    private string? _pendingAutoFallbackReason;
    private PendingPreparation? _preparation;
    private IRenderPackRuntime? _active;
    private long _activeRegistrationId;
    private IRenderPackRuntime? _observedPerformanceRuntime;
    private long _observedResourceGeneration = -1;
    private RenderPackActivationSnapshot _snapshot = new(
        RenderPackActivationState.Retail,
        RenderPackSelectionSettings.Retail,
        ActivePackDisplayName: null,
        Reason: null,
        ActivationGeneration: 0);
    private bool _disposed;
    private int _catalogChanged;

    internal RenderPackController(
        Func<RenderPackCatalog> catalog,
        IRenderPackRuntimeFactory factory,
        IRenderPackReceiverPipelineCoordinator? receiverPipelines = null,
        IRenderPackPreparationScheduler? preparationScheduler = null,
        RenderPackCatalogSource? catalogSource = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _receiverPipelines = receiverPipelines;
        _preparationScheduler = preparationScheduler
            ?? ThreadPoolRenderPackPreparationScheduler.Instance;
        _catalogSource = catalogSource;
        if (catalogSource is not null)
            catalogSource.Changed += OnCatalogChanged;
    }

    internal RenderPackActivationSnapshot Snapshot => _snapshot;

    internal IRenderPackRuntime? ActiveRuntime => _active;

    internal RenderPackPerformanceSnapshot Performance => _performance.Snapshot();

    internal int MinimumPerformanceSampleCount =>
        _performance.MinimumSampleCount;

    internal bool TryResetPerformanceEvidence(out string error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_snapshot.State != RenderPackActivationState.Active
            || _active is not IRenderPackRuntimePerformanceSource source)
        {
            error = "an active render pack with performance diagnostics is required";
            return false;
        }
        if (_autoQuality is not null)
        {
            error = "performance evidence reset requires an explicit quality preset";
            return false;
        }

        RenderPackRuntimePerformanceMetrics metrics =
            source.CapturePerformanceMetrics();
        Validate(in metrics);
        _performance.Reset();
        _observedPerformanceRuntime = _active;
        _observedResourceGeneration = metrics.ResourceGeneration;
        error = string.Empty;
        return true;
    }

    internal AtmosphericAutoQualitySnapshot? AutoQuality => _autoQuality?.Snapshot;

    public RenderPackDiagnosticsSnapshot CaptureDiagnostics()
    {
        RenderPackRuntimeDiagnostics runtime =
            (_active as IRenderPackRuntimeDiagnosticsSource)?.CaptureDiagnostics()
            ?? RenderPackRuntimeDiagnostics.Empty(_snapshot.Selection.PresetId);
        RenderPackPerformanceSnapshot performance = _performance.Snapshot();
        return new RenderPackDiagnosticsSnapshot(
            _snapshot.State,
            _snapshot.Selection.PackId,
            _snapshot.Selection.PackVersion,
            _snapshot.Selection.PresetId,
            _active?.Preset.Id ?? runtime.EffectiveQuality,
            _snapshot.Reason,
            _snapshot.ActivationGeneration,
            runtime.RetainedGpuBytes,
            runtime.TransientGpuBytes,
            runtime.ImageCount,
            runtime.BufferCount,
            runtime.DrawCalls,
            runtime.DispatchCalls,
            runtime.ShadowCasterCount,
            runtime.CascadeDrawCount,
            runtime.CpuClassificationCalls,
            runtime.SunElevationDegrees,
            runtime.ActiveDayGroup,
            runtime.Weather,
            runtime.WeatherIntensity,
            runtime.Outdoor,
            runtime.DirectionalShadowStrength,
            runtime.Passes,
            performance)
        {
            CpuStages = runtime.CpuStages,
            DirectionalShadowSourceKind = runtime.DirectionalShadowSourceKind,
            DirectionalShadowSourceObjectIndex =
                runtime.DirectionalShadowSourceObjectIndex,
            DirectionalShadowSourceGfxObjId =
                runtime.DirectionalShadowSourceGfxObjId,
            DirectionalShadowSurfaceToLightDirection =
                runtime.DirectionalShadowSurfaceToLightDirection,
            DirectionalShadowLightElevationSin =
                runtime.DirectionalShadowLightElevationSin,
            ShadowTransformChurn = runtime.ShadowTransformChurn,
            SharedWorldTransformUsedInstances =
                runtime.SharedWorldTransformUsedInstances,
        };
    }

    internal void Request(
        RenderPackSelectionSettings? selection,
        bool explicitUserChoice = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RenderPackSelectionSettings normalized = Normalize(selection);
        if (explicitUserChoice)
            _failedSelections.Remove(normalized);
        if (normalized == _snapshot.Selection
            && _pending is null)
            return;
        _pendingAutoQuality = null;
        _pendingAutoFallbackReason = null;
        _pending = normalized;
        _snapshot = _snapshot with
        {
            State = RenderPackActivationState.CandidatePending,
            Selection = normalized,
            ActivePackDisplayName = _active?.Descriptor.DisplayName,
            Reason = $"Preparing render pack '{normalized.PackId}'.",
        };
    }

    internal RenderPackActivationSnapshot ApplyAtFrameBoundary(
        RenderPackActivationExtent extent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        extent.Validate();

        if (ApplyCatalogChangeAtFrameBoundary() is { } catalogFailure)
            return catalogFailure;

        if (_preparation is { } preparation)
        {
            if (!preparation.Work.IsCompleted)
                return _snapshot;

            _preparation = null;
            if (preparation.Work.IsCanceled)
            {
                preparation.Dispose();
                return Fail(
                    preparation.Plan.Selection,
                    PreparationFailure(
                        preparation.Plan.Selection,
                        "candidate preparation was cancelled"),
                    preparation.Plan.Entry.RegistrationId);
            }
            if (preparation.Work.IsFaulted)
            {
                Exception failure = preparation.Work.Exception!.GetBaseException();
                preparation.Dispose();
                if (VulkanRenderFailurePolicy.IsFatal(failure))
                    throw failure;
                return Fail(
                    preparation.Plan.Selection,
                    PreparationFailure(preparation.Plan.Selection, failure.Message),
                    preparation.Plan.Entry.RegistrationId);
            }

            if (_pending is not null
                || preparation.Plan.Selection != _snapshot.Selection)
            {
                preparation.Dispose();
                if (_pending is null
                    && _snapshot.State == RenderPackActivationState.FailedToRetail
                    && _snapshot.Selection.IsRetail)
                {
                    return _snapshot;
                }
                if (_pending is null)
                    _pending = _snapshot.Selection;
            }
            else if (preparation.Plan.Extent != extent)
            {
                preparation.Dispose();
                StartPreparation(preparation.Plan with { Extent = extent });
                return _snapshot;
            }
            else
            {
                PreparationOutcome outcome = preparation.TakeOutcome();
                if (outcome.FailureReason is not null)
                {
                    string? retirementFailure = outcome.DisposeResources();
                    return Fail(
                        preparation.Plan.Selection,
                        outcome.FailureReason
                        + FormatRetirementFailure(retirementFailure),
                        preparation.Plan.Entry.RegistrationId);
                }
                return PublishPreparedCandidate(preparation.Plan, outcome);
            }
        }

        if (_pending is { } selection)
        {
            _pending = null;
            if (selection.IsRetail)
            {
                string? retirementFailure = RetireActive();
                return PublishRetail(selection, retirementFailure);
            }

            ActivationPlan? plan = PlanSelection(selection, in extent);
            if (plan is null)
                return _snapshot;
            StartPreparation(plan);
            if (_preparation!.Work.IsCompleted)
                return ApplyAtFrameBoundary(extent);
            return _snapshot;
        }

        if (_pendingAutoFallbackReason is { } fallbackReason)
        {
            _pendingAutoFallbackReason = null;
            return Fail(_snapshot.Selection, fallbackReason);
        }

        if (_pendingAutoQuality is { } quality)
        {
            _pendingAutoQuality = null;
            ActivationPlan? plan = PlanAutomaticQuality(quality, in extent);
            if (plan is null)
                return _snapshot;
            StartPreparation(plan);
            if (_preparation!.Work.IsCompleted)
                return ApplyAtFrameBoundary(extent);
        }
        return _snapshot;
    }

    private ActivationPlan? PlanSelection(
        RenderPackSelectionSettings selection,
        in RenderPackActivationExtent extent)
    {
        RenderPackCatalog catalog = _catalog();
        if (!catalog.TryGet(selection.PackId, out RenderPackCatalogEntry entry))
        {
            if (AlreadyFailed(selection, registrationId: 0))
                return null;
            Fail(selection, $"Render pack '{selection.PackId}' is not installed.", 0);
            return null;
        }
        if (AlreadyFailed(selection, entry.RegistrationId))
            return null;
        if (!entry.IsCompatible)
        {
            Fail(selection, entry.IncompatibilityReason ?? "The render pack is incompatible.");
            return null;
        }
        if (!string.Equals(
                entry.Descriptor.PackVersion.ToString(),
                selection.PackVersion,
                StringComparison.Ordinal))
        {
            Fail(
                selection,
                $"Render pack '{selection.PackId}' version {selection.PackVersion ?? "(missing)"} "
                + $"was selected, but version {entry.Descriptor.PackVersion} is installed.");
            return null;
        }

        RenderQualityPreset? selectedPreset = entry.Descriptor.QualityPresets.FirstOrDefault(
            value => string.Equals(value.Id, selection.PresetId, StringComparison.OrdinalIgnoreCase));
        if (selectedPreset is null)
        {
            Fail(selection, $"Render pack preset '{selection.PresetId}' is not available.");
            return null;
        }
        if (entry.PresetIncompatibilityReasons.TryGetValue(
                selectedPreset.Id,
                out string? presetFailure)
            && presetFailure is not null)
        {
            Fail(selection, presetFailure);
            return null;
        }

        RenderPackValidationResult userSettings =
            RenderPackSettingResolution.ValidateUserOverrides(
                entry.Descriptor,
                selection.SettingOverrides);
        if (!userSettings.Success)
        {
            Fail(selection, userSettings.Reason!);
            return null;
        }

        bool automaticSelector =
            selectedPreset.Semantic == RenderQualitySemantic.Automatic;
        bool automatic = TryResolveAutomaticSetting(
            entry.Descriptor,
            selectedPreset,
            selection.SettingOverrides,
            out bool configuredAutomatic)
                ? configuredAutomatic
                : automaticSelector;
        RenderQualityPreset preset = selectedPreset;
        AtmosphericQualityLevel autoInitial = AtmosphericQualityLevel.Medium;
        AtmosphericQualityLevel autoMaximum = AtmosphericQualityLevel.High;
        if (automaticSelector || automatic)
        {
            if (!TryResolveAutomaticRange(
                    entry,
                    out preset,
                    out autoInitial,
                    out autoMaximum,
                    out string? automaticFailure))
            {
                Fail(selection, automaticFailure!);
                return null;
            }
            if (automatic
                && !automaticSelector
                && TryQualityLevel(selectedPreset.Semantic, out AtmosphericQualityLevel preferred))
            {
                preset = selectedPreset;
                autoInitial = preferred;
            }
        }

        AutomaticPublication automaticPublication = automatic
            ? new AutomaticPublication(
                AutomaticPublicationMode.Initialise,
                AutomaticBudgets(entry),
                autoInitial,
                autoMaximum)
            : AutomaticPublication.Disabled;
        return new ActivationPlan(
            entry,
            selection,
            preset,
            extent,
            RequirePerformanceSource: automatic,
            automaticPublication);
    }

    internal void ObserveActiveFrame(
        in RenderPackFramePerformanceObservation observation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Validate(in observation);
        IRenderPackRuntime? active = _active;
        if (active is not IRenderPackRuntimePerformanceSource source)
            return;

        RenderPackRuntimePerformanceMetrics metrics =
            source.CapturePerformanceMetrics();
        Validate(in metrics);
        if (!ReferenceEquals(active, _observedPerformanceRuntime)
            || metrics.ResourceGeneration != _observedResourceGeneration)
        {
            _performance.Reset();
            _observedPerformanceRuntime = active;
            _observedResourceGeneration = metrics.ResourceGeneration;
            return;
        }
        if (!observation.StableFrameBoundary
            || !metrics.HasResolvedGpuMeasurement)
        {
            return;
        }

        _performance.Observe(
            observation.PackAddedCpuMilliseconds,
            observation.AbsoluteEnhancedWorldReceiverCpuMilliseconds,
            hasResolvedGpuMeasurement: true,
            metrics.InclusiveResolvedGpuMilliseconds,
            metrics.RetainedGpuBytes,
            metrics.TransientGpuBytes);
        if (_autoQuality is null
            || _pendingAutoQuality is not null
            || _pendingAutoFallbackReason is not null
            || _snapshot.State != RenderPackActivationState.Active)
        {
            return;
        }

        RenderPackPerformanceSnapshot performance = _performance.Snapshot();
        var measurement = new AtmosphericQualityMeasurement(
            performance.InclusiveGpuMillisecondsP99,
            performance.IncrementalCpuMillisecondsP99,
            performance.ResidentGpuBytes,
            StableFrameBoundary: true);
        AtmosphericQualityBudget budget = _autoQuality.CurrentBudget;
        long priorGeneration = _autoQuality.Snapshot.ChangeGeneration;
        AtmosphericAutoQualitySnapshot quality = _autoQuality.Observe(in measurement);
        if (quality.ChangeGeneration == priorGeneration)
            return;

        if (quality.SafeFallbackToRetailRequested)
        {
            _pendingAutoFallbackReason = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "Automatic quality disabled render pack '{0}' because {1} remained "
                + "over its declared performance budget for {2} stable samples: "
                + "GPU p99 {3:F3} ms (budget {4:F3} ms), CPU p99 {5:F3} ms "
                + "(budget {6:F3} ms), resident GPU bytes {7} (budget {8}).",
                _snapshot.Selection.PackId,
                quality.Current,
                AtmosphericAutoQualityController.DowngradeHysteresisFrames,
                measurement.InclusivePackGpuMillisecondsP99,
                budget.GpuMillisecondsP99,
                measurement.IncrementalCpuMillisecondsP99,
                budget.CpuMillisecondsP99,
                measurement.ResidentGpuBytes,
                budget.ResidentGpuBytes);
            return;
        }

        _pendingAutoQuality = quality.Current;
    }

    private ActivationPlan? PlanAutomaticQuality(
        AtmosphericQualityLevel quality,
        in RenderPackActivationExtent extent)
    {
        RenderPackSelectionSettings selection = _snapshot.Selection;
        if (_active is null
            || _autoQuality is null
            || _snapshot.State != RenderPackActivationState.Active)
        {
            return null;
        }

        RenderPackCatalog catalog = _catalog();
        if (!catalog.TryGet(selection.PackId, out RenderPackCatalogEntry entry))
        {
            Fail(selection, $"Render pack '{selection.PackId}' is no longer installed.");
            return null;
        }
        if (!entry.IsCompatible)
        {
            Fail(selection, entry.IncompatibilityReason ?? "The render pack is incompatible.");
            return null;
        }
        if (!string.Equals(
                entry.Descriptor.PackVersion.ToString(),
                selection.PackVersion,
                StringComparison.Ordinal))
        {
            Fail(
                selection,
                $"Render pack '{selection.PackId}' changed version during automatic quality selection.");
            return null;
        }

        RenderQualityPreset? preset = FindEffectivePreset(entry, quality);
        if (preset is null)
        {
            Fail(
                selection,
                $"Automatic quality cannot select a missing or ineligible "
                + $"'{QualitySemantic(quality)}' semantic preset.");
            return null;
        }
        if (entry.PresetIncompatibilityReasons.TryGetValue(
                preset.Id,
                out string? presetFailure)
            && presetFailure is not null)
        {
            Fail(selection, presetFailure);
            return null;
        }

        RenderPackValidationResult userSettings =
            RenderPackSettingResolution.ValidateUserOverrides(
                entry.Descriptor,
                selection.SettingOverrides);
        if (!userSettings.Success)
        {
            Fail(selection, userSettings.Reason!);
            return null;
        }

        _snapshot = _snapshot with
        {
            State = RenderPackActivationState.CandidatePending,
            ActivePackDisplayName = _active.Descriptor.DisplayName,
            Reason = $"Preparing automatic quality preset '{preset.DisplayName}'.",
        };
        return new ActivationPlan(
            entry,
            selection,
            preset,
            extent,
            RequirePerformanceSource: true,
            AutomaticPublication.Preserve);
    }

    private void StartPreparation(ActivationPlan plan)
    {
        var preparation = new PendingPreparation(plan);
        _preparation = preparation;
        _snapshot = _snapshot with
        {
            State = RenderPackActivationState.CandidatePending,
            Selection = plan.Selection,
            ActivePackDisplayName = _active?.Descriptor.DisplayName,
            Reason = $"Preparing render pack '{plan.Selection.PackId}' preset "
                + $"'{plan.Preset.DisplayName}'.",
        };
        try
        {
            preparation.Work = _preparationScheduler.Schedule(
                () => preparation.Outcome = PrepareCandidate(plan));
        }
        catch (Exception error) when (VulkanRenderFailurePolicy.IsFatal(error))
        {
            _preparation = null;
            preparation.Dispose();
            throw;
        }
        catch (Exception error) when (!VulkanRenderFailurePolicy.IsFatal(error))
        {
            preparation.Outcome = PreparationOutcome.Failed(
                PreparationFailure(plan.Selection, error.GetBaseException().Message));
            preparation.Work = Task.CompletedTask;
        }
    }

    private PreparationOutcome PrepareCandidate(ActivationPlan plan)
    {
        IRenderPackRuntime? candidate = null;
        IRenderPackReceiverPipelineCandidate? receiverCandidate = null;
        try
        {
            RenderPackValidationResult assets = RenderPackValidator.ValidateSelectedAssets(
                plan.Entry.Descriptor,
                plan.Entry.Assets,
                out ValidatedRenderPackShaderAssets? validatedAssets);
            if (!assets.Success)
                return PreparationOutcome.Failed(assets.Reason!);

            candidate = _factory.Build(
                plan.Entry.Descriptor,
                validatedAssets!,
                plan.Preset,
                plan.Selection.SettingOverrides);
            if (candidate is null)
                throw new InvalidOperationException("The render-pack factory returned no candidate.");
            if (!ReferenceEquals(candidate.Descriptor, plan.Entry.Descriptor)
                && candidate.Descriptor != plan.Entry.Descriptor)
                throw new InvalidOperationException("The candidate does not represent the selected descriptor.");
            if (!ReferenceEquals(candidate.Preset, plan.Preset)
                && candidate.Preset != plan.Preset)
                throw new InvalidOperationException("The candidate does not represent the selected preset.");
            if (plan.RequirePerformanceSource
                && candidate is not IRenderPackRuntimePerformanceSource)
            {
                throw new NotSupportedException(
                    "Automatic quality requires allocation-free runtime performance metrics.");
            }
            if (candidate is IAtmosphericWorldGraphRuntime graph)
            {
                _ = graph.PrepareWorldTarget(
                    plan.Extent.Width,
                    plan.Extent.Height,
                    plan.Extent.SampleCount);
            }
            else if (plan.RequirePerformanceSource)
            {
                throw new NotSupportedException(
                    "Automatic quality requires a complete off-side world graph candidate.");
            }

            IDirectionalShadowReceiverSource? receiverSource =
                candidate is IDirectionalShadowWorldGraphRuntime directional
                    ? directional.DirectionalShadowReceivers
                    : null;
            if (receiverSource is not null && _receiverPipelines is null)
            {
                throw new NotSupportedException(
                    "Directional-shadow activation requires an atomic receiver-pipeline coordinator.");
            }
            if (_receiverPipelines is not null)
            {
                receiverCandidate = _receiverPipelines.Prepare(
                    receiverSource,
                    plan.Extent.SampleCount);
            }
            PreparationOutcome outcome = PreparationOutcome.Ready(
                candidate,
                receiverCandidate);
            candidate = null;
            receiverCandidate = null;
            return outcome;
        }
        catch (Exception error) when (VulkanRenderFailurePolicy.IsFatal(error))
        {
            BestEffortDisposeForFatal(receiverCandidate);
            BestEffortDisposeForFatal(candidate);
            throw;
        }
        catch (Exception error) when (!VulkanRenderFailurePolicy.IsFatal(error))
        {
            return new PreparationOutcome(
                candidate,
                receiverCandidate,
                PreparationFailure(
                    plan.Selection,
                    error.GetBaseException().Message));
        }
    }

    private RenderPackActivationSnapshot PublishPreparedCandidate(
        ActivationPlan plan,
        PreparationOutcome outcome)
    {
        IRenderPackRuntime? candidate = outcome.TakeRuntime();
        IRenderPackReceiverPipelineCandidate? receiverCandidate =
            outcome.TakeReceiverCandidate();
        try
        {
            IRenderPackRuntime? previous = _active;
            if (receiverCandidate is not null)
            {
                _receiverPipelines!.Publish(receiverCandidate);
                receiverCandidate = null;
            }
            _active = candidate;
            _activeRegistrationId = plan.Entry.RegistrationId;
            candidate = null;
            ResetPerformanceTracking(_active);
            previous?.Dispose();
            ApplyAutomaticPublication(plan.AutomaticPublication);
            _snapshot = new RenderPackActivationSnapshot(
                RenderPackActivationState.Active,
                plan.Selection,
                plan.Entry.Descriptor.DisplayName,
                Reason: null,
                ActivationGeneration: checked(_snapshot.ActivationGeneration + 1));
            return _snapshot;
        }
        catch (Exception error) when (VulkanRenderFailurePolicy.IsFatal(error))
        {
            BestEffortDisposeForFatal(receiverCandidate);
            BestEffortDisposeForFatal(candidate);
            throw;
        }
        catch (Exception error) when (!VulkanRenderFailurePolicy.IsFatal(error))
        {
            string? receiverRetirement = TryDisposeReceiverCandidate(receiverCandidate);
            string? candidateRetirement = TryDispose(candidate);
            return Fail(
                plan.Selection,
                $"Render pack '{plan.Selection.PackId}' could not be published: "
                + error.GetBaseException().Message
                + FormatRetirementFailure(receiverRetirement)
                + FormatRetirementFailure(candidateRetirement),
                plan.Entry.RegistrationId);
        }
        finally
        {
            outcome.Dispose();
        }
    }

    private void ApplyAutomaticPublication(AutomaticPublication publication)
    {
        if (publication.Mode == AutomaticPublicationMode.Preserve)
            return;
        _pendingAutoQuality = null;
        _pendingAutoFallbackReason = null;
        _autoQuality = publication.Mode == AutomaticPublicationMode.Initialise
            ? new AtmosphericAutoQualityController(
                publication.Budgets!,
                publication.Initial,
                AtmosphericQualityLevel.Low,
                publication.Maximum)
            : null;
    }

    private static string PreparationFailure(
        RenderPackSelectionSettings selection,
        string reason) =>
        $"Render pack '{selection.PackId}' could not be prepared: {reason}";

    internal void OnRuntimeFailure(string reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        RenderPackSelectionSettings failed = _snapshot.Selection;
        if (!failed.IsRetail)
            _failedSelections[failed] = _activeRegistrationId;
        string? retirementFailure = RetireActive();
        ClearAutomaticQuality();
        _snapshot = new RenderPackActivationSnapshot(
            RenderPackActivationState.FailedToRetail,
            RenderPackSelectionSettings.Retail,
            ActivePackDisplayName: null,
            reason + FormatRetirementFailure(retirementFailure),
            ActivationGeneration: checked(_snapshot.ActivationGeneration + 1));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_catalogSource is not null)
            _catalogSource.Changed -= OnCatalogChanged;
        _pending = null;
        if (_preparation is { } preparation)
        {
            _preparation = null;
            try
            {
                preparation.Work.GetAwaiter().GetResult();
            }
            catch (Exception error) when (!VulkanRenderFailurePolicy.IsFatal(error))
            {
            }
            finally
            {
                preparation.Dispose();
            }
        }
        ClearAutomaticQuality();
        RetireActive();
    }

    private RenderPackActivationSnapshot Fail(
        RenderPackSelectionSettings selection,
        string reason,
        long? registrationId = null)
    {
        _failedSelections[selection] = registrationId
            ?? CurrentRegistrationId(selection);
        string? retirementFailure = RetireActive();
        ClearAutomaticQuality();
        _snapshot = new RenderPackActivationSnapshot(
            RenderPackActivationState.FailedToRetail,
            RenderPackSelectionSettings.Retail,
            ActivePackDisplayName: null,
            reason + FormatRetirementFailure(retirementFailure),
            ActivationGeneration: checked(_snapshot.ActivationGeneration + 1));
        return _snapshot;
    }

    private RenderPackActivationSnapshot PublishRetail(
        RenderPackSelectionSettings requested,
        string? reason)
    {
        ClearAutomaticQuality();
        _snapshot = new RenderPackActivationSnapshot(
            reason is null
                ? RenderPackActivationState.Retail
                : RenderPackActivationState.FailedToRetail,
            RenderPackSelectionSettings.Retail,
            ActivePackDisplayName: null,
            reason,
            ActivationGeneration: checked(_snapshot.ActivationGeneration + 1));
        return _snapshot;
    }

    private string? RetireActive()
    {
        IRenderPackRuntime? active = _active;
        _active = null;
        _activeRegistrationId = 0;
        ResetPerformanceTracking(null);
        string? receiverFailure = TryClearReceiverPipelines();
        string? runtimeFailure = TryDispose(active);
        return CombineRetirementFailures(receiverFailure, runtimeFailure);
    }

    private bool AlreadyFailed(
        RenderPackSelectionSettings selection,
        long registrationId)
    {
        if (!_failedSelections.TryGetValue(selection, out long failedRegistrationId))
            return false;
        if (failedRegistrationId != registrationId)
        {
            _failedSelections.Remove(selection);
            return false;
        }

        RetireActive();
        PublishRetail(
            selection,
            "This pack selection already failed for the current registration "
                + "and will not be retried.");
        return true;
    }

    private long CurrentRegistrationId(RenderPackSelectionSettings selection)
    {
        if (selection.IsRetail)
            return 0;
        return _catalog().TryGet(selection.PackId, out RenderPackCatalogEntry entry)
            ? entry.RegistrationId
            : 0;
    }

    private void OnCatalogChanged(long revision)
    {
        _ = revision;
        Interlocked.Exchange(ref _catalogChanged, 1);
    }

    private RenderPackActivationSnapshot? ApplyCatalogChangeAtFrameBoundary()
    {
        RenderPackCatalogSource? source = _catalogSource;
        if (source is null || Interlocked.Exchange(ref _catalogChanged, 0) == 0)
            return null;

        RenderPackSelectionSettings selection = _snapshot.Selection;
        if (selection.IsRetail)
            return null;

        long selectedRegistrationId = _preparation?.Plan.Entry.RegistrationId
            ?? _activeRegistrationId;
        if (selectedRegistrationId == 0)
        {
            return null;
        }
        RenderPackCatalog catalog = source.Snapshot();
        if (!catalog.TryGet(selection.PackId, out RenderPackCatalogEntry current))
        {
            _pending = null;
            return Fail(
                selection,
                $"Render pack '{selection.PackId}' was withdrawn; acdream's default renderer is active.",
                selectedRegistrationId);
        }
        if (!string.Equals(
                current.Descriptor.PackVersion.ToString(),
                selection.PackVersion,
                StringComparison.Ordinal)
            || (selectedRegistrationId != 0
                && current.RegistrationId != selectedRegistrationId))
        {
            _pending = null;
            return Fail(
                selection,
                $"Render pack '{selection.PackId}' was replaced by registration "
                    + $"version {current.Descriptor.PackVersion}; reselect it to activate the update.",
                selectedRegistrationId);
        }
        return null;
    }

    private void ResetPerformanceTracking(IRenderPackRuntime? runtime)
    {
        _performance.Reset();
        _observedPerformanceRuntime = runtime;
        _observedResourceGeneration =
            runtime is IRenderPackRuntimePerformanceSource source
                ? source.CapturePerformanceMetrics().ResourceGeneration
                : -1;
    }

    private void ClearAutomaticQuality()
    {
        _autoQuality = null;
        _pendingAutoQuality = null;
        _pendingAutoFallbackReason = null;
    }

    private static RenderQualityPreset? FindEffectivePreset(
        RenderPackCatalogEntry entry,
        AtmosphericQualityLevel quality) =>
        entry.Descriptor.QualityPresets.FirstOrDefault(preset =>
            preset.AutoEligible
            && preset.Semantic == QualitySemantic(quality));

    private static bool TryResolveAutomaticSetting(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string> userOverrides,
        out bool automatic)
    {
        RenderSettingDeclaration? setting = descriptor.Settings.FirstOrDefault(value =>
            value.Semantic == RenderSettingSemantic.AutomaticQuality);
        if (setting is null)
        {
            automatic = false;
            return false;
        }

        automatic = bool.Parse(RenderPackSettingResolution.Resolve(
            setting,
            preset,
            userOverrides));
        return true;
    }

    private static bool TryResolveAutomaticRange(
        RenderPackCatalogEntry entry,
        out RenderQualityPreset preset,
        out AtmosphericQualityLevel initial,
        out AtmosphericQualityLevel maximum,
        out string? failure)
    {
        preset = null!;
        initial = AtmosphericQualityLevel.Low;
        maximum = AtmosphericQualityLevel.Low;
        failure = null;

        RenderQualityPreset? low = FindEffectivePreset(
            entry,
            AtmosphericQualityLevel.Low);
        if (low is null)
        {
            failure = "Automatic quality requires an AutoEligible Low semantic preset as its safe fallback.";
            return false;
        }
        if (entry.PresetIncompatibilityReasons.TryGetValue(
                low.Id,
                out string? lowFailure)
            && lowFailure is not null)
        {
            failure = "Automatic quality cannot support Low on this host: " + lowFailure;
            return false;
        }

        preset = low;
        RenderQualityPreset? medium = FindEffectivePreset(
            entry,
            AtmosphericQualityLevel.Medium);
        if (medium is null
            || (entry.PresetIncompatibilityReasons.TryGetValue(
                    medium.Id,
                    out string? mediumFailure)
                && mediumFailure is not null))
        {
            return true;
        }

        preset = medium;
        initial = AtmosphericQualityLevel.Medium;
        maximum = AtmosphericQualityLevel.Medium;
        RenderQualityPreset? high = FindEffectivePreset(
            entry,
            AtmosphericQualityLevel.High);
        if (high is not null
            && (!entry.PresetIncompatibilityReasons.TryGetValue(
                    high.Id,
                    out string? highFailure)
                || highFailure is null))
        {
            maximum = AtmosphericQualityLevel.High;
        }
        return true;
    }

    private static AtmosphericQualityBudget[] AutomaticBudgets(
        RenderPackCatalogEntry entry) =>
    [
        Budget(entry, AtmosphericQualityLevel.Low),
        Budget(entry, AtmosphericQualityLevel.Medium),
        Budget(entry, AtmosphericQualityLevel.High),
    ];

    private static AtmosphericQualityBudget Budget(
        RenderPackCatalogEntry entry,
        AtmosphericQualityLevel level) =>
        AtmosphericQualityBudget.FromPreset(
            entry.Descriptor.QualityPresets.Single(preset =>
                preset.Semantic == QualitySemantic(level)));

    private static RenderQualitySemantic QualitySemantic(
        AtmosphericQualityLevel quality) => quality switch
        {
            AtmosphericQualityLevel.Low => RenderQualitySemantic.Low,
            AtmosphericQualityLevel.Medium => RenderQualitySemantic.Medium,
            AtmosphericQualityLevel.High => RenderQualitySemantic.High,
            _ => throw new ArgumentOutOfRangeException(nameof(quality)),
        };

    private static bool TryQualityLevel(
        RenderQualitySemantic semantic,
        out AtmosphericQualityLevel quality)
    {
        quality = semantic switch
        {
            RenderQualitySemantic.Low => AtmosphericQualityLevel.Low,
            RenderQualitySemantic.Medium => AtmosphericQualityLevel.Medium,
            RenderQualitySemantic.High => AtmosphericQualityLevel.High,
            _ => default,
        };
        return semantic is RenderQualitySemantic.Low
            or RenderQualitySemantic.Medium
            or RenderQualitySemantic.High;
    }

    private enum AutomaticPublicationMode : byte
    {
        Disable,
        Initialise,
        Preserve,
    }

    private readonly record struct AutomaticPublication(
        AutomaticPublicationMode Mode,
        AtmosphericQualityBudget[]? Budgets,
        AtmosphericQualityLevel Initial,
        AtmosphericQualityLevel Maximum)
    {
        internal static AutomaticPublication Disabled { get; } = new(
            AutomaticPublicationMode.Disable,
            Budgets: null,
            AtmosphericQualityLevel.Low,
            AtmosphericQualityLevel.Low);

        internal static AutomaticPublication Preserve { get; } = new(
            AutomaticPublicationMode.Preserve,
            Budgets: null,
            AtmosphericQualityLevel.Low,
            AtmosphericQualityLevel.Low);
    }

    private sealed record ActivationPlan(
        RenderPackCatalogEntry Entry,
        RenderPackSelectionSettings Selection,
        RenderQualityPreset Preset,
        RenderPackActivationExtent Extent,
        bool RequirePerformanceSource,
        AutomaticPublication AutomaticPublication);

    private sealed class PendingPreparation(ActivationPlan plan) : IDisposable
    {
        private PreparationOutcome? _outcome;

        internal ActivationPlan Plan { get; } = plan;

        internal Task Work { get; set; } = Task.CompletedTask;

        internal PreparationOutcome? Outcome
        {
            set => _outcome = value;
        }

        internal PreparationOutcome TakeOutcome()
        {
            if (!Work.IsCompleted)
                throw new InvalidOperationException("Render-pack preparation is not complete.");
            PreparationOutcome outcome = _outcome
                ?? throw new InvalidOperationException(
                    "Render-pack preparation completed without an outcome.");
            _outcome = null;
            return outcome;
        }

        public void Dispose()
        {
            _outcome?.Dispose();
            _outcome = null;
        }
    }

    private sealed class PreparationOutcome(
        IRenderPackRuntime? runtime,
        IRenderPackReceiverPipelineCandidate? receiverCandidate,
        string? failureReason) : IDisposable
    {
        private IRenderPackRuntime? _runtime = runtime;
        private IRenderPackReceiverPipelineCandidate? _receiverCandidate = receiverCandidate;

        internal string? FailureReason { get; } = failureReason;

        internal static PreparationOutcome Ready(
            IRenderPackRuntime runtime,
            IRenderPackReceiverPipelineCandidate? receiverCandidate) =>
            new(runtime, receiverCandidate, null);

        internal static PreparationOutcome Failed(string reason) =>
            new(null, null, reason);

        internal IRenderPackRuntime TakeRuntime()
        {
            IRenderPackRuntime runtime = _runtime
                ?? throw new InvalidOperationException(
                    "The prepared render-pack outcome has no runtime.");
            _runtime = null;
            return runtime;
        }

        internal IRenderPackReceiverPipelineCandidate? TakeReceiverCandidate()
        {
            IRenderPackReceiverPipelineCandidate? candidate = _receiverCandidate;
            _receiverCandidate = null;
            return candidate;
        }

        internal string? DisposeResources()
        {
            string? receiverFailure = TryDisposeReceiverCandidate(_receiverCandidate);
            _receiverCandidate = null;
            string? runtimeFailure = TryDispose(_runtime);
            _runtime = null;
            return CombineRetirementFailures(receiverFailure, runtimeFailure);
        }

        public void Dispose() => _ = DisposeResources();
    }

    private static void Validate(in RenderPackFramePerformanceObservation value)
    {
        if (!double.IsFinite(value.PackAddedCpuMilliseconds)
            || value.PackAddedCpuMilliseconds < 0d
            || !double.IsFinite(value.AbsoluteEnhancedWorldReceiverCpuMilliseconds)
            || value.AbsoluteEnhancedWorldReceiverCpuMilliseconds < 0d
            || value.ViewportWidth <= 0
            || value.ViewportHeight <= 0
            || value.SampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Render-pack frame measurements and target dimensions must be valid.");
        }
    }

    private static void Validate(in RenderPackRuntimePerformanceMetrics value)
    {
        if (value.ResourceGeneration < 0
            || (value.HasResolvedGpuMeasurement
                && (!double.IsFinite(value.InclusiveResolvedGpuMilliseconds)
                    || value.InclusiveResolvedGpuMilliseconds < 0d))
            || value.RetainedGpuBytes < 0
            || value.TransientGpuBytes < 0)
        {
            throw new InvalidOperationException(
                "The render-pack runtime published invalid performance metrics.");
        }
    }

    private static string? TryDispose(IRenderPackRuntime? runtime)
    {
        if (runtime is null)
            return null;
        try
        {
            runtime.Dispose();
            return null;
        }
        catch (Exception error) when (!VulkanRenderFailurePolicy.IsFatal(error))
        {
            return error.GetBaseException().Message;
        }
    }

    private static string? TryDisposeReceiverCandidate(
        IRenderPackReceiverPipelineCandidate? candidate)
    {
        if (candidate is null)
            return null;
        try
        {
            candidate.Dispose();
            return null;
        }
        catch (Exception error) when (!VulkanRenderFailurePolicy.IsFatal(error))
        {
            return error.GetBaseException().Message;
        }
    }

    private string? TryClearReceiverPipelines()
    {
        if (_receiverPipelines is null)
            return null;
        try
        {
            _receiverPipelines.Clear();
            return null;
        }
        catch (Exception error) when (!VulkanRenderFailurePolicy.IsFatal(error))
        {
            return error.GetBaseException().Message;
        }
    }

    private static string? CombineRetirementFailures(string? first, string? second) =>
        first is null ? second : second is null ? first : first + "; " + second;

    private static void BestEffortDisposeForFatal(IDisposable? value)
    {
        if (value is null)
            return;
        try
        {
            value.Dispose();
        }
        catch
        {
        }
    }

    private static string FormatRetirementFailure(string? reason) =>
        reason is null ? string.Empty : $" Pack resource retirement also failed: {reason}";

    private static RenderPackSelectionSettings Normalize(
        RenderPackSelectionSettings? selection)
    {
        if (selection is null
            || string.IsNullOrWhiteSpace(selection.PackId)
            || string.IsNullOrWhiteSpace(selection.PresetId))
            return RenderPackSelectionSettings.Retail;
        if (selection.IsRetail)
            return RenderPackSelectionSettings.Retail;
        return selection;
    }
}
