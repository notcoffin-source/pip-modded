using Bsg.GameSettings;
using Comfort.Common;
using System.Reflection;
using EFT;
using EFT.CameraControl;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace PiPDisabler.Patches
{
    internal sealed class WeaponScalingPatch : ModulePatch
    {
        private static bool _isActive;
        private static bool _suppressCompensationOverride;
        private static float _lastLoggedScale = -1f;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(Player), nameof(Player.SetCompensationScale));
        }
        public static void CaptureBaseState()
        {
            var os = ScopeLifecycle.ActiveOptic;
            if (os == null) { _isActive = false; return; }
            _isActive = true;
        }
        public static void UpdateScale()
        {
            if (!_isActive) return;
            var player = GetMainPlayer();
            if (player == null) return;

            float scale = GetManualScale();
            player.RibcageScaleCurrentTarget = scale;
            player.RibcageScaleCurrent = scale;
            LogScale(scale);
        }

        public static void RestoreScale()
        {
            _isActive = false;
            RestoreVanillaScale();
        }

        public static void RestoreScaleForFreelook()
        {
            if (!_isActive) return;
            RestoreVanillaScale();
        }

        private static void RestoreVanillaScale()
        {
            var player = GetMainPlayer();
            if (player == null) return;
            int settingsFov = GetVanillaSettingsFov();
            _suppressCompensationOverride = true;
            try
            {
                player.OnFovUpdatedEvent(settingsFov);
                player.RibcageScaleCurrent = player.RibcageScaleCurrentTarget;
            }
            finally
            {
                _suppressCompensationOverride = false;
            }
        }

    private static float GetManualScale()
    {
        if (!PerScopeMeshSurgerySettings.TryGetWeaponScale(out float minScale, out float maxScale))
            return Settings.ManualWeaponScale.Value * Settings.GlobalScopeScalingMultiplier.Value;

        minScale *= Settings.GlobalScopeScalingMultiplier.Value;
        maxScale *= Settings.GlobalScopeScalingMultiplier.Value;

        if (TryGetSingleModeScale(minScale, out float singleModeScale))
            return singleModeScale;

            float t = GetCurrentMagnificationT();
            return Mathf.Lerp(minScale, maxScale, t);
        }

        private static bool TryGetSingleModeScale(float targetScale, out float scale)
        {
            scale = 0f;

            var range = FovController.GetTemplateZoomRange();
            float minZoom = Mathf.Min(range.min, range.max);
            float maxZoom = Mathf.Max(range.min, range.max);
            if (minZoom <= 0.1f || maxZoom <= 0.1f || !Mathf.Approximately(minZoom, maxZoom))
                return false;

            if (maxZoom <= 1.01f)
            {
                scale = targetScale;
                return true;
            }

            float t = FovController.GetVisualZoomPosition();
            scale = Mathf.Lerp(1f, targetScale, t);
            return true;
        }

        private static float GetCurrentMagnificationT()
        {
            var range = FovController.GetTemplateZoomRange();
            float minZoom = Mathf.Min(range.min, range.max);
            float maxZoom = Mathf.Max(range.min, range.max);
            if (minZoom <= 0.1f || maxZoom <= 0.1f)
                return FovController.GetVisualZoomPosition();

            if (Mathf.Approximately(minZoom, maxZoom))
                return FovController.GetVisualZoomPosition();

            float currentMagnification = GetCurrentFovMagnification();
            return Mathf.Clamp01(Mathf.InverseLerp(minZoom, maxZoom, currentMagnification));
        }

        private static float GetCurrentFovMagnification()
        {
            if (!CameraClass.Exist)
                return FovController.GetVisualMagnification();

            float currentFov = Mathf.Max(0.1f, CameraClass.Instance.Fov);
            float baseFovRad = FovController.MagnificationBaselineFov * Mathf.Deg2Rad;
            float currentFovRad = currentFov * Mathf.Deg2Rad;
            return Mathf.Max(1f, Mathf.Tan(baseFovRad * 0.5f) / Mathf.Tan(currentFovRad * 0.5f));
        }

        private static void LogScale(float scale)
        {
            if (!Settings.DebugLogging.Value) return;
            if (System.Math.Abs(scale - _lastLoggedScale) < 0.01f) return;

            _lastLoggedScale = scale;
            PiPDisablerPlugin.DebugLogInfo($"[WeaponScaling] manual scale={scale:F3}");
        }

        private static int GetVanillaSettingsFov()
        {
            return (int)Singleton<SharedGameSettingsClass>.Instance.Game.Settings.FieldOfView;
        }

        [PatchPostfix]
        private static void Postfix(Player __instance)
        {
            if (!__instance.IsYourPlayer) return;
            if (_suppressCompensationOverride) return;
            if (!ScopeLifecycle.IsScoped) return;
            if (ScopeLifecycle.IsModBypassedForCurrentScope) return;
            if (!_isActive) return;

            float scale = GetManualScale();
            __instance.RibcageScaleCurrentTarget = scale;
            __instance.RibcageScaleCurrent = scale;
            LogScale(scale);
        }

        private static Player GetMainPlayer()
            => Helpers.GetLocalPlayer();
    }
}
