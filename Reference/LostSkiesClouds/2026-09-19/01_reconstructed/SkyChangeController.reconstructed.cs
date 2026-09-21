// MANUAL RECONSTRUCTION FROM IL2CPP/ISIL -- NOT ORIGINAL SOURCE.
// Confidence is HIGH for field reads/writes, branches, constants and calls shown here.
// Event names and the choice to express Update as a forwarding call are HIGH/MEDIUM.
// See 00_report/MANUAL_RECONSTRUCTION_NOTICE_KO.md.

using System.Threading;
using Cysharp.Threading.Tasks;
using Expanse;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using WildSkies.Enemies;

public partial class SkyChangeController
{
    protected override void Awake()
    {
        base.Awake();

        _skyBossService.OnFightJoined += OnFightJoined;
        _skyBossService.OnFightExited += OnFightExited;
        _windwallService.OnLocalPlayerEnteredWindwall += OnLocalPlayerEnteredWindwall;
        _windwallService.OnLocalPlayerExitedWindwall += OnLocalPlayerExitedWindwall;
    }

    private void OnDestroy()
    {
        _skyBossService.OnFightJoined -= OnFightJoined;
        _skyBossService.OnFightExited -= OnFightExited;
        _windwallService.OnLocalPlayerEnteredWindwall -= OnLocalPlayerEnteredWindwall;
        _windwallService.OnLocalPlayerExitedWindwall -= OnLocalPlayerExitedWindwall;
    }

    private void Start()
    {
        _globalLightingVolume.profile.TryGet(out _colorAdjustments);

        if (_colorAdjustments == null)
            Debug.LogWarning("Missing color adjustments in global lighting volume!");

        _activeProfile = _defaultProfile;

        int liveCoverageKeys = _proceduralCloudVolume.m_coverageCurve.keys.Length;
        int defaultCoverageKeys = _defaultProfile._coverageCurve.keys.Length;
        int heraldCoverageKeys = _heraldBattleProfile._coverageCurve.keys.Length;

        if (liveCoverageKeys == defaultCoverageKeys &&
            defaultCoverageKeys == heraldCoverageKeys)
        {
            _blendedCoverageCurve =
                new AnimationCurve(_defaultProfile._coverageCurve.keys);
        }
        else
        {
            Debug.LogError(
                "Coverage curves have different number of keys, cannot blend coverage!");
        }

        if (_colorAdjustments != null)
        {
            int defaultExposureKeys =
                _defaultProfile.ColorAdjustmentPostExposureCurve.keys.Length;
            int heraldExposureKeys =
                _heraldBattleProfile.ColorAdjustmentPostExposureCurve.keys.Length;

            if (defaultExposureKeys != heraldExposureKeys)
            {
                Debug.LogError(
                    "Post exposure curves have different number of keys, cannot blend post exposure!");
            }
            else
            {
                _blendedPostExposureCurve = new AnimationCurve(
                    _defaultProfile.ColorAdjustmentPostExposureCurve.keys);
            }
        }
    }

    public void SetHeraldProfile() =>
        SetSkyProfile(SkyProfileType.HeraldBattle);

    public void SetDefaultProfile() =>
        SetSkyProfile(SkyProfileType.Default);

    private void OnFightJoined(IBoss boss) =>
        SetSkyProfile(SkyProfileType.HeraldBattle);

    private void OnFightExited(IBoss boss) =>
        SetSkyProfile(SkyProfileType.Default);

    private void OnLocalPlayerEnteredWindwall() =>
        SetSkyProfile(SkyProfileType.Windwall);

    private void OnLocalPlayerExitedWindwall() =>
        SetSkyProfile(SkyProfileType.Default);

    public void SetSkyProfile(SkyProfileType type)
    {
        _prevPostExposureCurve =
            new AnimationCurve(_blendedPostExposureCurve.keys);

        _prevProfile = _activeProfile;
        _tempProfile = _prevProfile;

        _tempProfile._coverageCurve =
            new AnimationCurve(_proceduralCloudVolume.m_coverageCurve.keys);
        _tempProfile.CloudCoverage = _creativeCloudVolume.m_coverage;
        _tempProfile.CloudRaininess = _creativeCloudVolume.m_raininess;
        _tempProfile.BossFightValue = _timeOfDayController.BossFightValue;

        switch (type)
        {
            case SkyProfileType.Default:
                _activeProfile = _defaultProfile;
                break;
            case SkyProfileType.HeraldBattle:
                _activeProfile = _heraldBattleProfile;
                break;
            case SkyProfileType.Windwall:
                _activeProfile = _windwall;
                break;
        }

        // Exact native branch: zero-duration profiles return before updating the type.
        if (_activeProfile.LerpTime == 0f)
            return;

        _activeProfileType = type;
        _postExposureTime = 0f;

        if (_cancellationTokenSource != null &&
            !_cancellationTokenSource.IsCancellationRequested)
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
        }

        _cancellationTokenSource = new CancellationTokenSource();

        // The resulting UniTask is ignored by the observed caller.
        ChangeProfileOverTime()
            .AttachExternalCancellation(_cancellationTokenSource.Token);
    }

    private async UniTask ChangeProfileOverTime()
    {
        _time = 0f;

        while (_time < _activeProfile.LerpTime)
        {
            _time += Time.deltaTime;
            float t = Mathf.Clamp01(_time / _activeProfile.LerpTime);

            _creativeCloudVolume.m_coverage = Mathf.Lerp(
                _tempProfile.CloudCoverage,
                _activeProfile.CloudCoverage,
                t);

            _creativeCloudVolume.m_raininess = Mathf.Lerp(
                _tempProfile.CloudRaininess,
                _activeProfile.CloudRaininess,
                t);

            // The native state machine contains a direct backing-field store. The
            // public one-line setter was most likely inlined, so source-like C# uses it.
            _timeOfDayController.UpdateBossFightValue(Mathf.Lerp(
                _tempProfile.BossFightValue,
                _activeProfile.BossFightValue,
                t));

            if (_blendedCoverageCurve != null)
            {
                BlendCoverageCurves(
                    _tempProfile._coverageCurve,
                    _activeProfile._coverageCurve,
                    _blendedCoverageCurve,
                    t);

                _proceduralCloudVolume.m_coverageCurve.CopyFrom(
                    _blendedCoverageCurve);
            }

            await UniTask.WaitForEndOfFrame(
                _cancellationTokenSource.Token);
        }

        _creativeCloudVolume.m_coverage = _activeProfile.CloudCoverage;
        _creativeCloudVolume.m_raininess = _activeProfile.CloudRaininess;
        _timeOfDayController.UpdateBossFightValue(
            _activeProfile.BossFightValue);
    }

    private void Update()
    {
        // Update and UpdatePostExposure share the same optimized native body.
        UpdatePostExposure();
    }

    private void UpdatePostExposure()
    {
        if (_colorAdjustments == null)
            return;

        if (_activeProfile.PostExposureLerpTime > _postExposureTime &&
            _blendedPostExposureCurve != null &&
            _prevPostExposureCurve != null)
        {
            _postExposureTime += Time.deltaTime;

            BlendCoverageCurves(
                _prevPostExposureCurve,
                _activeProfile.ColorAdjustmentPostExposureCurve,
                _blendedPostExposureCurve,
                _postExposureTime / _activeProfile.PostExposureLerpTime);
        }

        if (_blendedPostExposureCurve == null)
            return;

        _timeOfDay = _timeOfDayController.TimeOfDay;

        // ISIL evaluates the curve twice; this preserves that detail.
        _colorAdjustments.postExposure.value =
            _blendedPostExposureCurve.Evaluate(_timeOfDay);
        _exposureCurveValue =
            _blendedPostExposureCurve.Evaluate(_timeOfDay);
    }

    private void BlendCoverageCurves(
        AnimationCurve curveA,
        AnimationCurve curveB,
        AnimationCurve blendCurve,
        float t)
    {
        blendCurve.ClearKeys();

        for (int i = 0;
             i < curveA.keys.Length && i < curveB.keys.Length;
             i++)
        {
            Keyframe a = curveA.keys[i];
            Keyframe b = curveB.keys[i];

            var blended = new Keyframe(
                Mathf.Lerp(a.time, b.time, t),
                Mathf.Lerp(a.value, b.value, t));

            // Weight values themselves are not blended by the observed body.
            blended.weightedMode = a.weightedMode;
            blended.inTangent = Mathf.Lerp(a.inTangent, b.inTangent, t);
            blended.outTangent = Mathf.Lerp(a.outTangent, b.outTangent, t);

            blendCurve.AddKey(blended);
        }
    }
}
