using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PiPDisabler
{
    [Serializable]
    internal sealed class ScopeMeshSurgerySettingsEntry
    {
        public string ScopeKey;
        public float PlaneOffsetMeters;
        public float Plane1Radius;
        public float Plane1OffsetMeters;
        public float Plane2Position;
        public float Plane2Radius;
        public float Plane3Position;
        public float Plane3Radius;
        public float Plane4Position;
        public float Plane4Radius;
        public float CutStartOffset;
        public float CutLength;
        public float NearPreserveDepth;
        public float ReticleBaseSize;
        public float ReticleSizeMultiplier = 1f;
        public float MeshReticleMinScale;
        public float MeshReticleMaxScale;
        public float WeaponScaleMinMagnification;
        public float WeaponScaleMaxMagnification;
        public float WeaponScaleMultiplier = 1f;
        public float VisualRecoilCompensation;
        public float VignetteOpacity;
        public float VignetteRadius;
        public float VignetteSoftness;
        public bool ExpandSearchToWeaponRoot;
    }

    [Serializable]
    internal sealed class ScopeMeshSurgerySettingsFile
    {
        public List<ScopeMeshSurgerySettingsEntry> Entries = new List<ScopeMeshSurgerySettingsEntry>();
    }

    internal static class PerScopeMeshSurgerySettings
    {
        private static ScopeMeshSurgerySettingsFile _file = new ScopeMeshSurgerySettingsFile();
        private static bool _loaded;
        private static string _activeScopeKey;
        private static bool _syncingCustomConfig;

        private static string FilePath => Path.Combine(GetPluginRootDirectory(), "custom_mesh_surgery_settings.json");

        private static ScopeMeshSurgerySettingsEntry ActiveScopeOverride => GetActiveOverride();

        internal static float GetPlaneOffsetMeters() => ActiveScopeOverride != null ? ActiveScopeOverride.PlaneOffsetMeters : Settings.PlaneOffsetMeters.Value;
        internal static float GetPlane1Radius() => ActiveScopeOverride != null ? ActiveScopeOverride.Plane1Radius : Settings.Plane1Radius.Value;
        internal static float GetPlane1OffsetMeters() => ActiveScopeOverride != null ? ActiveScopeOverride.Plane1OffsetMeters : Settings.Plane1OffsetMeters.Value;
        internal static float GetPlane2Position() => ActiveScopeOverride != null ? ActiveScopeOverride.Plane2Position : Settings.Plane2Position.Value;
        internal static float GetPlane2PositionNormalized(float cutLength)
        {
            const float legacyReferenceCutLength = 0.755493f;
            float p2LegacyNormalized = Mathf.Clamp01(GetPlane2Position());
            float anchoredDepth = p2LegacyNormalized * legacyReferenceCutLength;
            return cutLength > 1e-5f ? Mathf.Clamp01(anchoredDepth / cutLength) : 0f;
        }
        internal static float GetPlane2Radius() => ActiveScopeOverride != null ? ActiveScopeOverride.Plane2Radius : Settings.Plane2Radius.Value;
        internal static float GetPlane3Position() => ActiveScopeOverride != null ? ActiveScopeOverride.Plane3Position : Settings.Plane3Position.Value;
        internal static float GetPlane3Radius() => ActiveScopeOverride != null ? ActiveScopeOverride.Plane3Radius : Settings.Plane3Radius.Value;
        internal static float GetPlane4Position() => ActiveScopeOverride != null ? ActiveScopeOverride.Plane4Position : Settings.Plane4Position.Value;
        internal static float GetPlane4Radius() => ActiveScopeOverride != null ? ActiveScopeOverride.Plane4Radius : Settings.Plane4Radius.Value;
        internal static float GetCutStartOffset() => ActiveScopeOverride != null ? ActiveScopeOverride.CutStartOffset : Settings.CutStartOffset.Value;
        internal static float GetCutLength() => ActiveScopeOverride != null ? ActiveScopeOverride.CutLength : Settings.CutLength.Value;
        internal static float GetNearPreserveDepth() => ActiveScopeOverride != null ? ActiveScopeOverride.NearPreserveDepth : Settings.NearPreserveDepth.Value;
        internal static float GetReticleBaseSize()
        {
            var entry = ActiveScopeOverride;
            if (entry == null)
                return Settings.ReticleBaseSize.Value;

            float multiplier = entry.ReticleSizeMultiplier > 0f ? entry.ReticleSizeMultiplier : 1f;
            return entry.ReticleBaseSize * multiplier;
        }
        internal static float GetMeshReticleMinScale() => ActiveScopeOverride != null ? ActiveScopeOverride.MeshReticleMinScale : Settings.MeshReticleMinScale.Value;
        internal static float GetMeshReticleMaxScale() => ActiveScopeOverride != null ? ActiveScopeOverride.MeshReticleMaxScale : Settings.MeshReticleMaxScale.Value;
        internal static bool TryGetWeaponScale(out float minMagnificationScale, out float maxMagnificationScale)
        {
            minMagnificationScale = 0f;
            maxMagnificationScale = 0f;
            var entry = ActiveScopeOverride;
            if (entry == null || entry.WeaponScaleMinMagnification <= 0f || entry.WeaponScaleMaxMagnification <= 0f)
                return false;

            float multiplier = entry.WeaponScaleMultiplier > 0f ? entry.WeaponScaleMultiplier : 1f;
            minMagnificationScale = entry.WeaponScaleMinMagnification * multiplier;
            maxMagnificationScale = entry.WeaponScaleMaxMagnification * multiplier;
            return true;
        }
        internal static float GetVignetteOpacity() => GetPositiveOrDefault(GetLiveVignetteValue(Settings.CustomVignetteOpacity.Value, ActiveScopeOverride?.VignetteOpacity ?? 0f), Settings.VignetteOpacity.Value);
        internal static float GetVignetteRadius() => GetPositiveOrDefault(GetLiveVignetteValue(Settings.CustomVignetteRadius.Value, ActiveScopeOverride?.VignetteRadius ?? 0f), Settings.VignetteRadius.Value);
        internal static float GetVignetteSoftness() => GetPositiveOrDefault(GetLiveVignetteValue(Settings.CustomVignetteSoftness.Value, ActiveScopeOverride?.VignetteSoftness ?? 0f), Settings.VignetteSoftness.Value);
        internal static float GetVisualRecoilCompensation()
        {
            var entry = ActiveScopeOverride;
            return entry != null && Mathf.Abs(entry.VisualRecoilCompensation) > 0.0001f
                ? entry.VisualRecoilCompensation
                : Settings.VisualRecoilCompensation.Value;
        }
        internal static bool GetExpandSearchToWeaponRoot() => ActiveScopeOverride != null ? ActiveScopeOverride.ExpandSearchToWeaponRoot : Settings.ExpandSearchToWeaponRoot.Value;


        internal static void SetActiveScope(string scopeKey)
        {
            _activeScopeKey = string.IsNullOrWhiteSpace(scopeKey) ? null : scopeKey.Trim();
            SyncCustomConfigFromOverride();
        }

        /// <summary>
        /// Populates the Custom* BepInEx config entries from the active scope's saved JSON values.
        /// This ensures the config manager shows the actual per-scope settings so the user can
        /// see and adjust them without having to remember/re-enter every value manually.
        /// </summary>
        private static void SyncCustomConfigFromOverride()
        {
            var entry = GetActiveOverride();

            try
            {
                _syncingCustomConfig = true;
                if (entry == null)
                {
                    Settings.CustomVignetteOpacity.Value = 0f;
                    Settings.CustomVignetteRadius.Value = 0f;
                    Settings.CustomVignetteSoftness.Value = 0f;
                    return;
                }

                Settings.CustomPlaneOffsetMeters.Value = entry.PlaneOffsetMeters;
                Settings.CustomPlane1Radius.Value = entry.Plane1Radius;
                Settings.CustomPlane1OffsetMeters.Value = entry.Plane1OffsetMeters;
                Settings.CustomPlane2Position.Value = entry.Plane2Position;
                Settings.CustomPlane2Radius.Value = entry.Plane2Radius;
                Settings.CustomPlane3Position.Value = entry.Plane3Position;
                Settings.CustomPlane3Radius.Value = entry.Plane3Radius;
                Settings.CustomPlane4Position.Value = entry.Plane4Position;
                Settings.CustomPlane4Radius.Value = entry.Plane4Radius;
                Settings.CustomCutStartOffset.Value = entry.CutStartOffset;
                Settings.CustomCutLength.Value = entry.CutLength;
                Settings.CustomNearPreserveDepth.Value = entry.NearPreserveDepth;
                Settings.CustomReticleBaseSize.Value = entry.ReticleBaseSize;
                Settings.CustomReticleSizeMultiplier.Value = entry.ReticleSizeMultiplier > 0f ? entry.ReticleSizeMultiplier : 1f;
                Settings.CustomMeshReticleMinScale.Value = entry.MeshReticleMinScale;
                Settings.CustomMeshReticleMaxScale.Value = entry.MeshReticleMaxScale;
                Settings.CustomWeaponScaleMinMagnification.Value = entry.WeaponScaleMinMagnification;
                Settings.CustomWeaponScaleMaxMagnification.Value = entry.WeaponScaleMaxMagnification;
                Settings.CustomWeaponScaleMultiplier.Value = entry.WeaponScaleMultiplier > 0f ? entry.WeaponScaleMultiplier : 1f;
                Settings.CustomVisualRecoilCompensation.Value = entry.VisualRecoilCompensation;
                Settings.CustomVignetteOpacity.Value = entry.VignetteOpacity;
                Settings.CustomVignetteRadius.Value = entry.VignetteRadius;
                Settings.CustomVignetteSoftness.Value = entry.VignetteSoftness;
                Settings.CustomExpandSearchToWeaponRoot.Value = entry.ExpandSearchToWeaponRoot;
                PiPDisablerPlugin.DebugLogInfo($"[CustomMeshSettings] Loaded saved settings for scope '{entry.ScopeKey}' into Custom config entries.");
            }
            catch (Exception ex)
            {
                PiPDisablerPlugin.DebugLogInfo($"[CustomMeshSettings] Failed to sync Custom config entries from override: {ex.Message}");
            }
            finally
            {
                _syncingCustomConfig = false;
            }
        }

        internal static void ClearActiveScope()
        {
            _activeScopeKey = null;
        }

        internal static ScopeMeshSurgerySettingsEntry GetActiveOverride()
        {
            EnsureLoaded();
            if (string.IsNullOrWhiteSpace(_activeScopeKey))
                return null;

            for (int i = 0; i < _file.Entries.Count; i++)
            {
                var entry = _file.Entries[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.ScopeKey))
                    continue;
                if (string.Equals(entry.ScopeKey, _activeScopeKey, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }

            return null;
        }

        internal static bool SaveCustomSettingsForScope(string scopeKey)
        {
            if (string.IsNullOrWhiteSpace(scopeKey))
                return false;

            EnsureLoaded();

            ScopeMeshSurgerySettingsEntry target = null;
            for (int i = 0; i < _file.Entries.Count; i++)
            {
                var candidate = _file.Entries[i];
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.ScopeKey))
                    continue;
                if (string.Equals(candidate.ScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase))
                {
                    target = candidate;
                    break;
                }
            }

            if (target == null)
            {
                target = new ScopeMeshSurgerySettingsEntry { ScopeKey = scopeKey };
                _file.Entries.Add(target);
            }

            target.PlaneOffsetMeters = Settings.CustomPlaneOffsetMeters.Value;
            target.Plane1Radius = Settings.CustomPlane1Radius.Value;
            target.Plane1OffsetMeters = Settings.CustomPlane1OffsetMeters.Value;
            target.Plane2Position = Settings.CustomPlane2Position.Value;
            target.Plane2Radius = Settings.CustomPlane2Radius.Value;
            target.Plane3Position = Settings.CustomPlane3Position.Value;
            target.Plane3Radius = Settings.CustomPlane3Radius.Value;
            target.Plane4Position = Settings.CustomPlane4Position.Value;
            target.Plane4Radius = Settings.CustomPlane4Radius.Value;
            target.CutStartOffset = Settings.CustomCutStartOffset.Value;
            target.CutLength = Settings.CustomCutLength.Value;
            target.NearPreserveDepth = Settings.CustomNearPreserveDepth.Value;
            target.ReticleBaseSize = Settings.CustomReticleBaseSize.Value;
            target.ReticleSizeMultiplier = Settings.CustomReticleSizeMultiplier.Value;
            target.MeshReticleMinScale = Settings.CustomMeshReticleMinScale.Value;
            target.MeshReticleMaxScale = Settings.CustomMeshReticleMaxScale.Value;
            target.WeaponScaleMinMagnification = Settings.CustomWeaponScaleMinMagnification.Value;
            target.WeaponScaleMaxMagnification = Settings.CustomWeaponScaleMaxMagnification.Value;
            target.WeaponScaleMultiplier = Settings.CustomWeaponScaleMultiplier.Value;
            target.VisualRecoilCompensation = Settings.CustomVisualRecoilCompensation.Value;
            target.VignetteOpacity = Settings.CustomVignetteOpacity.Value;
            target.VignetteRadius = Settings.CustomVignetteRadius.Value;
            target.VignetteSoftness = Settings.CustomVignetteSoftness.Value;
            target.ExpandSearchToWeaponRoot = Settings.CustomExpandSearchToWeaponRoot.Value;
            WriteToDisk();
            return true;
        }

        internal static void SaveActiveScopeVisualSettings()
        {
            if (_syncingCustomConfig || string.IsNullOrWhiteSpace(_activeScopeKey))
                return;

            EnsureLoaded();

            var target = GetOrCreateEntry(_activeScopeKey);
            target.VignetteOpacity = Settings.CustomVignetteOpacity.Value;
            target.VignetteRadius = Settings.CustomVignetteRadius.Value;
            target.VignetteSoftness = Settings.CustomVignetteSoftness.Value;
            WriteToDisk();
        }


        internal static bool DeleteCustomSettingsForScope(string scopeKey)
        {
            if (string.IsNullOrWhiteSpace(scopeKey))
                return false;

            EnsureLoaded();

            for (int i = _file.Entries.Count - 1; i >= 0; i--)
            {
                var candidate = _file.Entries[i];
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.ScopeKey))
                    continue;

                if (!string.Equals(candidate.ScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                _file.Entries.RemoveAt(i);
                WriteToDisk();
                return true;
            }

            return false;
        }

        private static ScopeMeshSurgerySettingsEntry GetOrCreateEntry(string scopeKey)
        {
            for (int i = 0; i < _file.Entries.Count; i++)
            {
                var candidate = _file.Entries[i];
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.ScopeKey))
                    continue;
                if (string.Equals(candidate.ScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            var target = new ScopeMeshSurgerySettingsEntry { ScopeKey = scopeKey };
            CopyGlobalSettingsTo(target);
            _file.Entries.Add(target);
            return target;
        }

        private static void CopyGlobalSettingsTo(ScopeMeshSurgerySettingsEntry target)
        {
            target.PlaneOffsetMeters = Settings.PlaneOffsetMeters.Value;
            target.Plane1Radius = Settings.Plane1Radius.Value;
            target.Plane1OffsetMeters = Settings.Plane1OffsetMeters.Value;
            target.Plane2Position = Settings.Plane2Position.Value;
            target.Plane2Radius = Settings.Plane2Radius.Value;
            target.Plane3Position = Settings.Plane3Position.Value;
            target.Plane3Radius = Settings.Plane3Radius.Value;
            target.Plane4Position = Settings.Plane4Position.Value;
            target.Plane4Radius = Settings.Plane4Radius.Value;
            target.CutStartOffset = Settings.CutStartOffset.Value;
            target.CutLength = Settings.CutLength.Value;
            target.NearPreserveDepth = Settings.NearPreserveDepth.Value;
            target.ReticleBaseSize = Settings.ReticleBaseSize.Value;
            target.ReticleSizeMultiplier = 1f;
            target.MeshReticleMinScale = Settings.MeshReticleMinScale.Value;
            target.MeshReticleMaxScale = Settings.MeshReticleMaxScale.Value;
            target.WeaponScaleMinMagnification = 0f;
            target.WeaponScaleMaxMagnification = 0f;
            target.WeaponScaleMultiplier = 1f;
            target.VisualRecoilCompensation = 0f;
            target.VignetteOpacity = Settings.CustomVignetteOpacity.Value;
            target.VignetteRadius = Settings.CustomVignetteRadius.Value;
            target.VignetteSoftness = Settings.CustomVignetteSoftness.Value;
            target.ExpandSearchToWeaponRoot = Settings.ExpandSearchToWeaponRoot.Value;
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;

            _loaded = true;
            try
            {
                if (!File.Exists(FilePath))
                    return;

                string json = File.ReadAllText(FilePath);
                var parsed = JsonConvert.DeserializeObject<ScopeMeshSurgerySettingsFile>(json);
                if (parsed != null && parsed.Entries != null)
                    _file = parsed;
            }
            catch (Exception ex)
            {
                PiPDisablerPlugin.DebugLogInfo($"[CustomMeshSettings] Failed to load settings json: {ex.Message}");
                _file = new ScopeMeshSurgerySettingsFile();
            }
        }

        private static void WriteToDisk()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_file, Formatting.Indented);
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex)
            {
                PiPDisablerPlugin.DebugLogInfo($"[CustomMeshSettings] Failed to save settings json: {ex.Message}");
            }
        }

        private static float GetPositiveOrDefault(float value, float defaultValue)
        {
            return value > 0f ? value : defaultValue;
        }

        private static float GetLiveVignetteValue(float liveValue, float savedValue)
        {
            return string.IsNullOrWhiteSpace(_activeScopeKey) ? savedValue : liveValue;
        }

        private static string GetPluginRootDirectory()
        {
            string pluginDir = null;
            pluginDir = Path.GetDirectoryName(typeof(PerScopeMeshSurgerySettings).Assembly.Location);
            return pluginDir;
        }
    }

}
