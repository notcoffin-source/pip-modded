using Comfort.Common;
using EFT.CameraControl;
using PiPDisabler.Patches;
using UnityEngine;

namespace PiPDisabler
{
    internal static class CameraSettingsManager
    {
        private const float LodBiasRefreshThreshold = 0.01f;
        private static float _savedLodBias;
        private static bool _applied;
        private static bool _pendingRestore;

        public static void ApplyForOptic(OpticSight os)
        {
            if (os == null)
                return;

            var cam = Helpers.GetMainCamera();
            if (cam == null)
                return;

            if (!_applied)
            {
                _savedLodBias = QualitySettings.lodBias;
                _applied = true;
            }

            float magnification = GetMagnificationFromFov(FovController.ComputeZoomedFov(), 0f);

            MainCameraLodBiasOverride.Activate();
            ApplyScopedLodBias(magnification, force: true);
        }

        public static void RefreshScopedLodBias()
        {
            if (!_applied)
                return;

            MainCameraLodBiasOverride.Activate();
            ApplyScopedLodBias(GetCurrentMainCameraMagnification(), force: false);
        }

        public static void Restore()
        {
            if (!_applied)
                return;

            if (Settings.KeepScopedLodBiasUntilInventory.Value)
            {
                _pendingRestore = true;
                return;
            }

            PerformRestore();
        }

        public static void ForceRestore()
        {
            _pendingRestore = false;
            PerformRestore();
        }

        public static void RestoreIfPending()
        {
            if (!_pendingRestore)
                return;

            _pendingRestore = false;
            PerformRestore();
        }

        private static void PerformRestore()
        {
            if (!_applied)
                return;

            MainCameraLodBiasOverride.Deactivate();
            QualitySettings.lodBias = _savedLodBias;
            _applied = false;
        }

        private static void ApplyScopedLodBias(float magnification, bool force)
        {
            float newLodBias = ComputeScopedLodBias(magnification);
            if (!force && Mathf.Abs(QualitySettings.lodBias - newLodBias) < LodBiasRefreshThreshold)
                return;

            QualitySettings.lodBias = newLodBias;
            PiPDisablerPlugin.DebugLogInfo($"[LodBias] LodBias = {newLodBias:F3}");
        }

        private static float ComputeScopedLodBias(float magnification)
        {
            float manualLodBias = Settings.ManualLodBias.Value;
            float minimumLodBias = GetGameSettingsLodBiasFloor();

            if (manualLodBias > 0f)
                return Mathf.Max(manualLodBias, minimumLodBias);

            if (manualLodBias == 0f)
                return ClampScopedLodBias(magnification * Settings.AutoLodBiasMultiplier.Value, minimumLodBias);

            return ClampScopedLodBias(_savedLodBias * Mathf.Max(magnification, 1f), minimumLodBias);
        }

        private static float ClampScopedLodBias(float value, float minimum)
            => Mathf.Max(minimum, Mathf.Min(value, 20f));

        private static float GetGameSettingsLodBiasFloor()
        {
            try
            {
                if (Singleton<SharedGameSettingsClass>.Instantiated)
                    return Mathf.Max(0.01f, Singleton<SharedGameSettingsClass>.Instance.Graphics.Settings.LodBias.Value);
            }
            catch { }

            return Mathf.Max(0.01f, _savedLodBias);
        }

        private static float GetCurrentMainCameraMagnification()
        {
            float currentFov = 0f;

            try
            {
                if (CameraClass.Exist && CameraClass.Instance != null)
                    currentFov = CameraClass.Instance.Fov;
                else
                {
                    var cam = Helpers.GetMainCamera();
                    if (cam != null)
                        currentFov = cam.fieldOfView;
                }
            }
            catch { }

            return GetMagnificationFromFov(currentFov, 0f);
        }

        private static float GetMagnificationFromFov(float fov, float fallbackFov)
        {
            float effectiveFov = fov > 0.1f ? fov : fallbackFov;
            if (effectiveFov <= 0.1f)
                effectiveFov = FovController.ComputeZoomedFov();
            if (effectiveFov <= 0.1f)
                return 1f;

            return Mathf.Max(35f / effectiveFov, 0.1f);
        }
    }
}
