using System;
using System.Reflection;
using EFT.CameraControl;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace PiPDisabler
{
    internal static class FovController
    {
        private static SightComponent _cachedSightComponent;
        private static OpticSight _cachedSightComponentForOptic;
        private static float _cachedMagnification;
        private static int _cachedMagnificationFrame = -1;
        private static float _cachedVisualMagnification;
        private static int _cachedVisualMagnificationFrame = -1;
        private static Type _smvcType;
        private static PropertyInfo _sightModProp;
        private static bool _smvcSearched;


        private static float _lastAppliedFov;
        public const float FovChangeThreshold = 0.05f; // degrees

        public static float MagnificationBaselineFov
        {
            get
            {
                if (!Settings.FOVFixBehaviour.Value)
                    return Settings.BaselineFOV.Value;

                var player = Helpers.GetLocalPlayer();
                var pwa = player?.ProceduralWeaponAnimation;
                return pwa.Single_2;
            }
        }

        /// <summary>
        /// Returns true when <paramref name="newFov"/> differs from the last
        /// applied FOV by at least <see cref="FovChangeThreshold"/> degrees.
        /// </summary>
        public static bool HasFovChanged(float newFov)
            => Mathf.Abs(newFov - _lastAppliedFov) >= FovChangeThreshold;

        /// <summary>Records the most-recently applied FOV target.</summary>
        public static void TrackAppliedFov(float fov) => _lastAppliedFov = fov;

        // Dedup logging
        private static float _lastLoggedMag;
        private static string _lastLoggedSource;
        private static float _lastTemplateZoomLog = -1f;
        private static string _lastTemplateZoomLogMode;
        private static float _lastVariableTemplateFovLog = -1f;

        public static float ComputeZoomedFov()
        {
            if (TryGetVariableTemplateFov(out float variableFov, out _, out _, out float smoothT))
            {
                if (Mathf.Abs(variableFov - _lastVariableTemplateFovLog) > 0.05f)
                {
                    _lastVariableTemplateFovLog = variableFov;
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[FovController] variable template FOV={variableFov:F2} deg t={smoothT:F3}");
                }
                return variableFov;
            }

            float magnification = GetEffectiveMagnification();
            float oneXTargetFov = GetOneXTargetFov();
            float resultFov = magnification <= 1.01f
                ? oneXTargetFov
                : MagnificationToFov(magnification, MagnificationBaselineFov);

            // Log on change
            string source = _lastLoggedSource ?? "?";
            if (Mathf.Abs(magnification - _lastLoggedMag) > 0.01f)
            {
                _lastLoggedMag = magnification;
                string mapping = magnification <= 1.01f
                    ? $"(1x override={oneXTargetFov:F1}°)"
                    : $"(baseline={MagnificationBaselineFov:F0}°)";
                PiPDisablerPlugin.DebugLogInfo(
                    $"[FovController] mag={magnification:F2}x → mainFov={resultFov:F1}° " +
                    $"{mapping} [{source}]");
            }

            return resultFov;
        }

        /// <summary>
        /// Returns the current effective magnification from the best available source.
        /// Used by FovController.
        /// Cached per-frame to avoid redundant reflection calls when multiple
        /// systems query magnification in the same frame.
        /// </summary>
        public static float GetEffectiveMagnification()
        {
            // Per-frame cache: multiple callers (Tick, WeaponScaling, etc.) hit this per frame
            int frame = Time.frameCount;
            if (frame == _cachedMagnificationFrame && _cachedMagnification > 0.1f)
                return _cachedMagnification;

            float result = GetEffectiveMagnificationUncached();
            _cachedMagnification = result;
            _cachedMagnificationFrame = frame;
            return result;
        }

        public static float GetEffectiveMagnificationUncached()
        {
            if (TryGetVariableTemplateFov(out float variableFov, out _, out _, out _))
                return FovToMagnification(variableFov, MagnificationBaselineFov);

            float templateMag = GetTemplateZoom();
            return templateMag;
        }

        public static float GetVisualMagnification()
        {
            int frame = Time.frameCount;
            if (frame == _cachedVisualMagnificationFrame && _cachedVisualMagnification > 0.1f)
                return _cachedVisualMagnification;

            float result = GetVisualMagnificationUncached();
            _cachedVisualMagnification = result;
            _cachedVisualMagnificationFrame = frame;
            return result;
        }

        public static float GetVisualMagnificationUncached()
        {
            if (TryGetVariableTemplateFov(out float variableFov, out _, out _, out _))
                return FovToMagnification(variableFov, MagnificationBaselineFov);

            float targetMag = GetTemplateZoom();
            if (targetMag <= 0.1f)
                return 1f;

            if (!CameraClass.Exist || CameraClass.Instance == null)
                return targetMag;

            float currentFov = CameraClass.Instance.Fov;
            if (currentFov <= 0.1f)
                return targetMag;

            float fovMag = FovToMagnification(currentFov, MagnificationBaselineFov);
            var range = GetTemplateZoomRange();
            float minMag = Mathf.Min(range.min, range.max);
            float maxMag = Mathf.Max(range.min, range.max);

            if (minMag > 0.1f && maxMag > 0.1f && !Mathf.Approximately(minMag, maxMag))
                return Mathf.Clamp(fovMag, Mathf.Min(1f, minMag), maxMag);

            return Mathf.Clamp(fovMag, 1f, Mathf.Max(1f, targetMag));
        }

        public static float GetSmoothScopeZoomPosition()
        {
            return TryGetVariableTemplateFov(out _, out _, out _, out float zoomPosition)
                ? zoomPosition
                : 0f;
        }

        public static float GetVisualZoomPosition()
        {
            if (TryGetVariableTemplateFov(out _, out _, out _, out float zoomPosition))
                return zoomPosition;

            float visualMag = GetVisualMagnification();
            var range = GetTemplateZoomRange();
            float minMag = Mathf.Min(range.min, range.max);
            float maxMag = Mathf.Max(range.min, range.max);

            if (minMag > 0.1f && maxMag > 0.1f && !Mathf.Approximately(minMag, maxMag))
                return Mathf.Clamp01(Mathf.InverseLerp(minMag, maxMag, visualMag));

            float targetMag = GetTemplateZoom();
            if (targetMag > 1.01f)
                return Mathf.Clamp01(Mathf.InverseLerp(1f, targetMag, visualMag));

            return 0f;
        }

        public static bool IsSmoothScopeFovActive()
        {
            return TryGetVariableTemplateFov(out _, out _, out _, out _);
        }

        public static bool TryGetCurrentVariableFovRange(
            out float currentFov,
            out float wideFov,
            out float narrowFov,
            out float zoomPosition)
        {
            return TryGetVariableTemplateFov(out currentFov, out wideFov, out narrowFov, out zoomPosition);
        }

        /// <summary>
        /// Converts magnification to main-camera vertical FOV.
        ///   resultFov = 2 * atan(tan(baseFov/2) / magnification)
        /// </summary>
        public static float MagnificationToFov(float magnification, float baseFov)
        {
            if (magnification < 0.1f) magnification = 1f;
            float baseFovRad = baseFov * Mathf.Deg2Rad;
            float resultRad = 2f * Mathf.Atan2(Mathf.Tan(baseFovRad * 0.5f), magnification);
            return resultRad * Mathf.Rad2Deg;
        }

        private static float FovToMagnification(float fov, float baseFov)
        {
            if (fov < 0.1f) return 1f;
            float baseFovRad = baseFov * Mathf.Deg2Rad;
            float fovRad = fov * Mathf.Deg2Rad;
            return Mathf.Max(1f, Mathf.Tan(baseFovRad * 0.5f) / Mathf.Tan(fovRad * 0.5f));
        }

        public static float GetOneXTargetFov()
        {
            var os = ScopeLifecycle.ActiveOptic;
            var player = Helpers.GetLocalPlayer();
            var pwa = player?.ProceduralWeaponAnimation;

            if (os != null && TryGetCurrentTemplateZoomEntry(os, out float currentZoom, out int modeCount))
            {
                if (Mathf.Abs(currentZoom - 1f) <= 0.01f)
                {
                    // FOV Fix behaviour
                    if (Settings.FOVFixBehaviour.Value)
                    {
                        return pwa.Single_2;
                    }

                    // Single-entry 1x mode uses vanilla ADS offset behavior.
                    if (modeCount == 1 && pwa != null)
                        return Mathf.Max(1f, pwa.Single_2 - 15f);

                    // 1x inside a multi-mode stack stays fixed at optic FOV.
                    return 35f;
                }
            }

            if (pwa == null)
                return MagnificationBaselineFov;

            return Mathf.Max(1f, pwa.Single_2);
        }

        private static bool TryGetCurrentTemplateZoomEntry(OpticSight os, out float zoom, out int modeCount)
        {
            zoom = 0f;
            modeCount = 0;

            if (os == null) return false;
            var sc = FindSightComponent(os);
            if (sc == null) return false;

            var state = GetCurrentScopeState(os);
            if (state.index < 0 || state.mode < 0) return false;

            var zooms = sc.Template?.Zooms;
            if (zooms == null || state.index >= zooms.Length) return false;

            var modeZooms = zooms[state.index];
            if (modeZooms == null) return false;

            modeCount = modeZooms.Length;
            if (state.mode >= modeCount) return false;

            zoom = modeZooms[state.mode];
            return true;
        }

        /// <summary>
        /// Called on mode switch to reset log dedup so the next computation logs.
        /// </summary>
        public static void OnModeSwitch()
        {
            _lastLoggedMag = 0f;
            _lastLoggedSource = null;
            // Clear per-scope caches so FindSightComponent re-discovers for new mode
            _cachedSightComponent = null;
            _cachedSightComponentForOptic = null;
            // Invalidate per-frame cache
            _cachedMagnificationFrame = -1;
            _cachedVisualMagnificationFrame = -1;
            // Reset dead-band so the next ApplyFov always fires
            _lastAppliedFov = 0f;
            _lastTemplateZoomLog = -1f;
            _lastTemplateZoomLogMode = null;
            _lastVariableTemplateFovLog = -1f;
        }

        private static bool TryGetVariableTemplateFov(
            out float fov,
            out float wideFov,
            out float narrowFov,
            out float zoomPosition)
        {
            fov = 0f;
            wideFov = 0f;
            narrowFov = 0f;
            zoomPosition = 0f;

            var os = ScopeLifecycle.ActiveOptic;
            if (os == null) return false;

            var handler = FindScopeZoomHandler(os);
            if (handler == null) return false;

            if (!TryGetAdjustableFovBounds(os, handler, out float opticWideFov, out float opticNarrowFov))
                return false;

            float current = handler.FiledOfView;
            if (current <= 0.1f)
                return false;

            float opticHigh = Mathf.Max(opticWideFov, opticNarrowFov);
            float opticLow = Mathf.Min(opticWideFov, opticNarrowFov);
            if (opticHigh <= 0.1f || opticLow <= 0.1f || Mathf.Approximately(opticHigh, opticLow))
                return false;

            var sc = FindSightComponent(os);
            if (!TryGetTemplateZoomRange(sc, out float minZoom, out float maxZoom))
                return false;

            float wideZoom = Mathf.Min(minZoom, maxZoom);
            float narrowZoom = Mathf.Max(minZoom, maxZoom);
            if (Mathf.Approximately(wideZoom, narrowZoom))
                return false;

            float clampedCurrent = Mathf.Clamp(current, opticLow, opticHigh);
            zoomPosition = Mathf.Clamp01(Mathf.InverseLerp(opticHigh, opticLow, clampedCurrent));

            // Optic FOV only supplies position; template zooms define the output FOV.
            wideFov = MagnificationToFov(wideZoom, MagnificationBaselineFov);
            narrowFov = MagnificationToFov(narrowZoom, MagnificationBaselineFov);
            fov = Mathf.Lerp(wideFov, narrowFov, zoomPosition);
            return true;
        }

        private static bool TryGetAdjustableFovBounds(
            OpticSight os,
            ScopeZoomHandler handler,
            out float wideFov,
            out float narrowFov)
        {
            wideFov = 0f;
            narrowFov = 0f;

            var sc = FindSightComponent(os);
            var templateData = sc?.AdjustableOpticData;
            if (templateData != null && templateData.IsAdjustableOptic)
            {
                Vector3 bounds = templateData.MinMaxFov;
                if (bounds.x > 0.1f && bounds.y > 0.1f)
                {
                    wideFov = bounds.x;
                    narrowFov = bounds.y;
                    return true;
                }
            }

            ScopeSmoothCameraData cameraData = handler.CameraData ?? FindScopeSmoothCameraData(os);
            if (cameraData == null)
                return false;

            Vector3 cameraBounds = cameraData.MinMaxFieldOfView;
            if (cameraBounds.x <= 0.1f || cameraBounds.y <= 0.1f)
                return false;

            wideFov = cameraBounds.x;
            narrowFov = cameraBounds.y;
            return true;
        }

        private static ScopeZoomHandler FindScopeZoomHandler(OpticSight os)
        {
            if (os == null) return null;

            ScopeZoomHandler handler = os.GetComponentInParent<ScopeZoomHandler>();
            if (handler != null) return handler;

            handler = os.GetComponentInChildren<ScopeZoomHandler>(true);
            if (handler != null) return handler;

            Transform root = ScopeHierarchy.FindScopeRoot(os.transform);
            if (root == null) return null;

            handler = root.GetComponentInChildren<ScopeZoomHandler>(true);
            if (handler != null) return handler;

            return root.parent != null
                ? root.parent.GetComponentInChildren<ScopeZoomHandler>(true)
                : null;
        }

        private static ScopeSmoothCameraData FindScopeSmoothCameraData(OpticSight os)
        {
            if (os == null) return null;

            ScopeSmoothCameraData data = os.GetComponentInParent<ScopeSmoothCameraData>();
            if (data != null) return data;

            data = os.GetComponentInChildren<ScopeSmoothCameraData>(true);
            if (data != null) return data;

            Transform root = ScopeHierarchy.FindScopeRoot(os.transform);
            if (root == null) return null;

            data = root.GetComponentInChildren<ScopeSmoothCameraData>(true);
            if (data != null) return data;

            return root.parent != null
                ? root.parent.GetComponentInChildren<ScopeSmoothCameraData>(true)
                : null;
        }

        private static float GetTemplateZoom()
        {
            var os = ScopeLifecycle.ActiveOptic;
            if (os == null) return 0f;

            var sc = FindSightComponent(os);
            if (sc == null) return 0f;

            float currentZoom = sc.GetCurrentOpticZoom();
            if (currentZoom > 0.1f)
            {
                LogTemplateZoomVerbose(
                    currentZoom,
                    $"[FovController] Template zoom: {currentZoom:F2}x (stepped)",
                    "stepped");
                return currentZoom;
            }

            return 0f;
        }

        private static void LogTemplateZoomVerbose(float zoom, string message, string mode)
        {
            if (Mathf.Abs(zoom - _lastTemplateZoomLog) <= 0.01f &&
                string.Equals(mode, _lastTemplateZoomLogMode, StringComparison.Ordinal))
                return;

            _lastTemplateZoomLog = zoom;
            _lastTemplateZoomLogMode = mode;
            PiPDisablerPlugin.DebugLogInfo(message);
        }

        /// <summary>
        /// Returns the template min/max zoom for the current scope.
        /// for magnification bounds.
        /// Returns (minZoom, maxZoom). Returns (0,0) if not available.
        /// </summary>
        public static (float min, float max) GetTemplateZoomRange()
        {
            var os = ScopeLifecycle.ActiveOptic;
            if (os == null) return (0f, 0f);

            var sc = FindSightComponent(os);
            return TryGetTemplateZoomRange(sc, out float minZ, out float maxZ)
                ? (minZ, maxZ)
                : (0f, 0f);
        }

        private static bool TryGetTemplateZoomRange(SightComponent sc, out float minZ, out float maxZ)
        {
            minZ = 0f;
            maxZ = 0f;
            if (sc == null) return false;

            try
            {
                minZ = sc.GetMinOpticZoom();
                maxZ = sc.GetMaxOpticZoom();
                if (minZ > 0.1f && maxZ > 0.1f)
                    return true;
            }
            catch { }

            return false;
        }

        public static bool IsOpticAdjustable(OpticSight os)
        {
            if (os == null) return false;
            var sc = FindSightComponent(os);
            if (sc == null) return false;
            try { return sc.AdjustableOpticData.IsAdjustableOptic; }
            catch { return false; }
        }


        /// <summary>
        /// Returns the scope item _id from SightComponent.Item.Template.
        /// </summary>
        public static string GetOpticTemplateId(OpticSight os)
        {
            try
            {
                var t = FindSightComponent(os)?.Item?.Template;
                if (t == null) return "unknown";
                var id = t._id.ToString();
                return string.IsNullOrWhiteSpace(id) ? "unknown" : id;
            }
            catch { return "unknown"; }
        }

        /// <summary>
        /// Returns the scope item _name from SightComponent.Item.Template.
        /// </summary>
        public static string GetOpticTemplateName(OpticSight os)
        {
            try
            {
                var t = FindSightComponent(os)?.Item?.Template;
                if (t == null) return "unknown";
                return string.IsNullOrWhiteSpace(t._name) ? "unknown" : t._name;
            }
            catch { return "unknown"; }
        }

        /// <summary>
        /// Returns (SelectedScopeIndex, SelectedScopeMode) from the SightComponent
        /// for the given optic. Returns (-1, -1) if the values cannot be read.
        /// </summary>
        public static (int index, int mode) GetCurrentScopeState(OpticSight os)
        {
            var sc = FindSightComponent(os);
            if (sc == null) return (-1, -1);
            try { return (sc.SelectedScopeIndex, sc.SelectedScopeMode); }
            catch { return (-1, -1); }
        }

        // =====================================================================
        //  SightComponent discovery via SightModVisualControllers
        // =====================================================================

        /// <summary>
        /// Finds the SightComponent for an OpticSight by walking up the hierarchy
        /// to find SightModVisualControllers, then reading its SightMod property.
        ///
        /// Hierarchy (from SPT4_Modding_Reference):
        ///   Sight Mod GameObject
        ///     ├── SightModVisualControllers  ← has SightMod (SightComponent)
        ///     └── mode_0 / mode_1
        ///          └── OpticSight  ← we start here
        /// </summary>
        private static SightComponent FindSightComponent(OpticSight os)
        {
            // Per-scope cache: SightComponent doesn't change during a scope session
            if (_cachedSightComponentForOptic == os && _cachedSightComponent != null)
                return _cachedSightComponent;

            if (!_smvcSearched)
            {
                _smvcSearched = true;
                DiscoverSightModVisualControllers();
            }

            if (_smvcType == null || _sightModProp == null) return null;

            try
            {
                // Walk up from OpticSight to find SightModVisualControllers
                Component smvc = os.GetComponentInParent(_smvcType);
                if (smvc == null)
                    smvc = os.GetComponentInChildren(_smvcType);

                // Also try walking to scope root explicitly
                if (smvc == null)
                {
                    Transform root = ScopeHierarchy.FindScopeRoot(os.transform);
                    if (root != null)
                    {
                        smvc = root.GetComponentInChildren(_smvcType, true);
                        // SightModVisualControllers sits on the sight mod object,
                        // which may be above scope_xxx
                        if (smvc == null && root.parent != null)
                            smvc = root.parent.GetComponentInChildren(_smvcType, true);
                    }
                }

                if (smvc == null) return null;

                var result = _sightModProp.GetValue(smvc) as SightComponent;
                if (result != null)
                {
                    _cachedSightComponent = result;
                    _cachedSightComponentForOptic = os;
                }
                return result;
            }
            catch (Exception ex)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[FovController] FindSightComponent exception: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Discovers the SightModVisualControllers type by:
        ///   1. Direct name lookup
        ///   2. Assembly scan for MonoBehaviour with "SightMod" property
        ///      whose return type has GetCurrentOpticZoom()
        /// </summary>
        private static void DiscoverSightModVisualControllers()
        {
            // Strategy 1: Direct type name lookup
            string[] typeNames = {
                "SightModVisualControllers",
                "EFT.Visual.SightModVisualControllers",
                "EFT.SightModVisualControllers",
            };

            foreach (var name in typeNames)
            {
                try
                {
                    var t = AccessTools.TypeByName(name);
                    if (t != null && typeof(MonoBehaviour).IsAssignableFrom(t))
                    {
                        var prop = FindSightModProperty(t);
                        if (prop != null)
                        {
                            _smvcType = t;
                            _sightModProp = prop;
                            PiPDisablerPlugin.DebugLogInfo(
                                $"[FovController] Found SightModVisualControllers: {t.FullName}, " +
                                $"SightMod={prop.Name} ({prop.PropertyType.Name})");
                            return;
                        }
                    }
                }
                catch { }
            }

            // Strategy 2: Scan assemblies for MonoBehaviour with a property whose
            // return type has GetCurrentOpticZoom method
            PiPDisablerPlugin.DebugLogInfo(
                "[FovController] SightModVisualControllers name lookup failed, scanning assemblies...");

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (!typeof(MonoBehaviour).IsAssignableFrom(type)) continue;

                        var prop = FindSightModProperty(type);
                        if (prop != null)
                        {
                            _smvcType = type;
                            _sightModProp = prop;
                            PiPDisablerPlugin.DebugLogInfo(
                                $"[FovController] Discovered SightModVisualControllers via scan: " +
                                $"{type.FullName}, SightMod={prop.Name} ({prop.PropertyType.Name})");
                            return;
                        }
                    }
                }
                catch { }
            }

            PiPDisablerPlugin.DebugLogInfo(
                "[FovController] SightModVisualControllers NOT found — template zoom unavailable");
        }

        /// <summary>
        /// Looks for a property on the given type that returns a SightComponent-like object.
        /// Identifies SightComponent by checking for GetCurrentOpticZoom method.
        /// Prefers property named "SightMod" but accepts any match.
        /// </summary>
        private static PropertyInfo FindSightModProperty(Type type)
        {
            PropertyInfo fallback = null;

            foreach (var prop in type.GetProperties(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (prop.PropertyType == typeof(object) || prop.PropertyType.IsPrimitive) continue;

                // Check if the return type looks like SightComponent
                // (has GetCurrentOpticZoom method)
                var getZoom = prop.PropertyType.GetMethod("GetCurrentOpticZoom",
                    BindingFlags.Public | BindingFlags.Instance);
                if (getZoom == null) continue;
                if (getZoom.ReturnType != typeof(float)) continue;

                // Prefer the property named "SightMod"
                if (prop.Name == "SightMod")
                    return prop;

                if (fallback == null)
                    fallback = prop;
            }

            return fallback;
        }
    }
}
