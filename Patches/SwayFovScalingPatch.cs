using System.Reflection;
using BepInEx.Configuration;
using EFT.CameraControl;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace PiPDisabler.Patches
{
    internal static class SwayFovScaling
    {
        private const float UnscaledSwayFov = 35f;
        private const float DefaultReductionStrength = 0.5f;
        private const string ReductionStrengthSettingName = "SwayStrength";
        private static FieldInfo _reductionStrengthField;
        private static bool _searchedReductionStrengthField;
        private static float _lastLoggedScale = -1f;
        private static float _lastLoggedStrength = -1f;

        internal static bool TryGetScale(out float scale)
        {
            scale = 1f;

            if (!WeaponMotionSuppressionState.ShouldApply(Settings.ScaleSwayWithCameraFov.Value) ||
                !CameraClass.Exist ||
                CameraClass.Instance == null)
            {
                return false;
            }

            float currentFov = Mathf.Max(1f, CameraClass.Instance.Fov);
            float fovScale = Mathf.Clamp01(currentFov / UnscaledSwayFov);
            float strength = GetReductionStrength();
            scale = Mathf.Lerp(1f, fovScale, strength);
            if (Mathf.Abs(scale - _lastLoggedScale) > 0.01f ||
                Mathf.Abs(strength - _lastLoggedStrength) > 0.01f)
            {
                _lastLoggedScale = scale;
                _lastLoggedStrength = strength;
                PiPDisablerPlugin.DebugLogInfo(
                    $"[SwayFovScaling] currentFov={currentFov:F2} fovScale={fovScale:F3} strength={strength:F3} scale={scale:F3}");
            }
            return scale < 0.999f;
        }

        private static float GetReductionStrength()
        {
            if (!_searchedReductionStrengthField)
            {
                _searchedReductionStrengthField = true;
                _reductionStrengthField = typeof(Settings).GetField(
                    ReductionStrengthSettingName,
                    BindingFlags.Public | BindingFlags.Static);
            }

            var entry = _reductionStrengthField?.GetValue(null) as ConfigEntry<float>;
            return Mathf.Clamp01(entry != null ? entry.Value : DefaultReductionStrength);
        }
    }

    internal sealed class SwayVectorVelocityFovScalingPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(BetterSpring), nameof(BetterSpring.ApplyVelocity),
                new[] { typeof(Vector3) });

        [PatchPrefix]
        private static void Prefix(ref Vector3 val)
        {
            if (!SwayFovScaling.TryGetScale(out float scale)) return;
            val *= scale;
        }
    }

    internal sealed class SwayComponentVelocityFovScalingPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(BetterSpring), nameof(BetterSpring.ApplyVelocity),
                new[] { typeof(int), typeof(float) });

        [PatchPrefix]
        private static void Prefix(ref float val)
        {
            if (!SwayFovScaling.TryGetScale(out float scale)) return;
            val *= scale;
        }
    }
}
