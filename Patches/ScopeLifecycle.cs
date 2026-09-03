using EFT;
using EFT.Animations;
using EFT.CameraControl;
using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace PiPDisabler
{
    internal static class ScopeLifecycle
    {
        private static readonly Func<ProceduralWeaponAnimation, bool> _getIsAiming
            = pwa => pwa.IsAiming;
        private static readonly Func<ProceduralWeaponAnimation, ProceduralWeaponAnimation.SightNBone> _getCurrentScope
            = pwa => pwa.CurrentScope;
        private static readonly Func<ProceduralWeaponAnimation.SightNBone, bool> _getIsOptic
            = scope => scope.IsOptic;
        private static bool _scopeAlignmentGateFailureLogged;
        private static float _postSprintAimGateExpiry;
        private static float _postStanceAimGateExpiry;
        private static bool _isScoped;
        private static OpticSight _activeOptic;
        private static OpticSight _lastEnabledOptic;
        private static bool _modBypassedForCurrentScope;
        private static bool _restoreOneXFovOnScopeExit;
        private static bool _meshSurgerySuppressedByReload;
        private static bool _reticleSuppressedByReload;
        private static MovementContext _subscribedMovementContext;

        private static float _postExitRestoreFov;
        private static float _postExitRestoreExpiry;

        public static bool HasPostExitRestore =>
            _postExitRestoreFov > 0.5f &&
            Time.realtimeSinceStartup < _postExitRestoreExpiry;
        public static float PostExitRestoreFov => _postExitRestoreFov;
        public static void ClearPostExitRestore()
        {
            _postExitRestoreFov = 0f;
            _postExitRestoreExpiry = 0f;
        }

        private static readonly HashSet<string> _scopeBlacklistNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string _scopeBlacklistRawCached;
        private static readonly HashSet<string> _scopeWhitelistNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string _scopeWhitelistRawCached;


        public static bool IsScoped => _isScoped;
        public static bool IsModBypassedForCurrentScope => _modBypassedForCurrentScope;
        public static OpticSight ActiveOptic => _activeOptic;

        public static bool ShouldRunUpdateLoop()
        {
            if (_isScoped)
                return true;

            var player = GetLocalPlayer();
            EnsureMovementStateSubscription(player);

            var pwa = player?.ProceduralWeaponAnimation;
            if (pwa == null)
                return false;

            if (!_getIsAiming(pwa))
                return false;

            try
            {
                var currentScope = _getCurrentScope(pwa);
                return currentScope != null && _getIsOptic(currentScope);
            }
            catch
            {
                return false;
            }
        }

        public static bool ShouldSuppressVanillaPiPNow()
        {
            return Settings.ModEnabled.Value &&
                   _isScoped &&
                   !_modBypassedForCurrentScope;
        }

        internal static bool IsThermalOrNightVisionOpticForBypass(OpticSight os)
        {
            return IsThermalOrNightVisionOptic(os);
        }

        internal static bool IsNameBypassed(OpticSight os)
        {
            return ScopeNameMatchesBypassPattern(os);
        }

        internal static bool IsCurrentOrPendingOpticBypassed()
        {
            if (_modBypassedForCurrentScope)
                return true;

            var os = TryGetCurrentScopeOpticFromPwa();
            if (os != null)
                return ShouldBypassForCurrentOptic(os);

            if (_isScoped && _activeOptic != null)
                return ShouldBypassForCurrentOptic(_activeOptic);

            return false;
        }

        internal static bool IsLastOpticNameBypassed()
        {
            var os = _lastEnabledOptic;
            if (os == null) return false;
            try { if (!os.enabled) return false; } catch { return false; }
            return ScopeNameMatchesBypassPattern(os);
        }

        public static void Init()
        {
            PiPDisablerPlugin.DebugLogInfo("[ScopeLifecycle] Init: IsAiming/CurrentScope/IsOptic are public — direct access.");
        }


        public static void OnOpticEnabled(OpticSight os)
        {
            if (os != null)
                _lastEnabledOptic = os;

            if (os != null && ShouldBypassForCurrentOptic(os))
            {
                _activeOptic = os;
                _modBypassedForCurrentScope = true;
                ApplyBypassState(os, reason: "optic enable",
                    restoreFov: false);
            }

            if (!_isScoped && os != null)
            {
                var player = GetLocalPlayer();
                var pwa = player?.ProceduralWeaponAnimation;
                if (pwa != null && _getIsAiming(pwa))
                {
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[ScopeLifecycle] Collimator→optic switch while ADS: " +
                        $"'{os.name}'[{FovController.GetOpticTemplateId(os)}] — treating as scope enter");
                }
            }

            if (_isScoped && os != null && os != _activeOptic)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[ScopeLifecycle] Mode switch while scoped: " +
                    $"'{(_activeOptic != null ? _activeOptic.name : "?")}'[{FovController.GetOpticTemplateId(_activeOptic)}] → " +
                    $"'{os.name}'[{FovController.GetOpticTemplateId(os)}]");

                _activeOptic = os;

                bool bypassForMode = ShouldBypassForCurrentOptic(os);
                if (bypassForMode)
                {
                    bool wasBypassed = _modBypassedForCurrentScope;
                    _modBypassedForCurrentScope = true;
                    ApplyBypassState(os, reason: "mode switch",
                        restoreFov: !wasBypassed);
                    return;
                }

                _modBypassedForCurrentScope = false;

                ReticleRenderer.Cleanup();
                ReticleRenderer.ExtractReticle(os);

                LensTransparency.HideAllLensSurfaces(os);

                ReticleRenderer.SetLensMaskEntries(CollectStencilEntries(os));

                FovController.OnModeSwitch();

                _meshSurgerySuppressedByReload = false;
                MeshSurgeryManager.RestoreForScope(os.transform);
                if (IsReloadActive())
                {
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[ScopeLifecycle] Skipping mode-switch mesh surgery because reload is active. frame={Time.frameCount}");
                    LensTransparency.RestoreAll();
                    SuppressReticleForReload();
                    _meshSurgerySuppressedByReload = true;
                }
                else
                {
                    MeshSurgeryManager.ApplyForOptic(os);
                }
                Patches.WeaponScalingPatch.CaptureBaseState();
                if (!FreelookTracker.IsFreelooking && !_meshSurgerySuppressedByReload)
                {
                    float modeMag = FovController.GetVisualMagnification();
                    ReticleRenderer.Show(os, modeMag);
                    ScopeEffectsRenderer.Show();
                    ApplyFov(true);
                }
                CameraSettingsManager.ApplyForOptic(os);
            }

            CheckAndUpdate("OnOpticEnabled");
        }

        public static void OnOpticDisabled(OpticSight os)
        {
            ReticleRenderer.Hide();
            ScopeEffectsRenderer.Hide();
            CheckAndUpdate("OnOpticDisabled");
        }

        internal static void RestoreBypassedOpticState(OpticSight os, string reason,
            bool restoreFov = false)
        {
            if (os == null) return;
            if (!ShouldBypassForCurrentOptic(os)) return;

            _activeOptic = os;
            _modBypassedForCurrentScope = true;
            ApplyBypassState(os, reason, restoreFov);
        }

        public static void CheckAndUpdate(string caller = "Update")
        {
            _lastCaller = caller;

            bool shouldBeScoped = false;
            bool exitingToNonOpticWhileAiming = false;
            string reason = "unknown";

            try
            {
                var player = GetLocalPlayer();
                EnsureMovementStateSubscription(player);

                if (player == null) { reason = "no player"; goto evaluate; }

                var pwa = player.ProceduralWeaponAnimation;
                if (pwa == null) { reason = "no PWA"; goto evaluate; }

                if (IsPlayerSprinting(player))
                {
                    ArmPostSprintAimGate();
                    reason = "sprinting";
                    goto evaluate;
                }

                bool isAiming = _getIsAiming(pwa);
                if (!isAiming) { reason = "not aiming"; goto evaluate; }

                if (Settings.BypassDuringStanceTransitions.Value && IsStanceTransitionActive(player))
                {
                    ArmPostStanceAimGate();
                    reason = "stance transition";
                    goto evaluate;
                }

                var currentScope = _getCurrentScope(pwa);
                if (currentScope == null) { reason = "no CurrentScope"; goto evaluate; }

                bool isOptic = _getIsOptic(currentScope);

                var enabledOs = FindEnabledOpticFromPWA();

                if (!isOptic)
                {
                    reason = "not optic";
                    exitingToNonOpticWhileAiming = true;
                    goto evaluate;
                }
                else if (enabledOs == null)
                {
                    // Hybrid toggle case: CurrentScope may still report optic while the
                    // enabled OpticSight switched off (e.g., now in collimator mode).
                    // Force scope exit immediately so RestoreAll runs without waiting for
                    // a full ADS exit.
                    shouldBeScoped = false;
                    reason = "optic flag true but no enabled OpticSight";
                    exitingToNonOpticWhileAiming = true;
                    goto evaluate;
                }

                if (ShouldApplyScopeAlignmentGate() && !IsScopeAlignedWithMainCamera(currentScope, enabledOs, out float scopeAngle, out float tolerance))
                {
                    reason = $"scope axis angle {scopeAngle:F2}° exceeds tolerance {tolerance:F2}°";
                    goto evaluate;
                }

                shouldBeScoped = true;
                _activeOptic = enabledOs;
                _lastEnabledOptic = enabledOs;
                _postSprintAimGateExpiry = 0f;
                _postStanceAimGateExpiry = 0f;
                reason = "aiming+optic+enabled OpticSight";
            }
            catch (Exception ex) { reason = $"exception: {ex.Message}"; }

        evaluate:

            if (shouldBeScoped != _isScoped)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[ScopeLifecycle] State change: {(_isScoped ? "SCOPED" : "NOT_SCOPED")} → " +
                    $"{(shouldBeScoped ? "SCOPED" : "NOT_SCOPED")} reason='{reason}' " +
                    $"caller={_lastCaller} frame={Time.frameCount}");
            }

            if (shouldBeScoped && !_isScoped)
            {
                _restoreOneXFovOnScopeExit = false;
                DoScopeEnter();
            }
            else if (!shouldBeScoped && _isScoped)
            {
                _restoreOneXFovOnScopeExit = exitingToNonOpticWhileAiming;
                DoScopeExit();
            }
        }

        // Caller tag for lightweight state-change logging (replaces expensive StackTrace)
        private static string _lastCaller = "?";

        public static void Tick()
        {
            if (!_isScoped) return;
            if (_modBypassedForCurrentScope) return;

            CameraSettingsManager.RefreshScopedLodBias();

            bool freelookJustEnded = FreelookTracker.Tick();

            if (FreelookTracker.IsFreelooking)
            {
                return;
            }

            if (freelookJustEnded)
            {
                LensTransparency.EnsureHidden();
            }

            LensTransparency.EnsureHidden();
            if (_activeOptic != null)
            {
                bool reloadActive = IsReloadActive();
                if (reloadActive && Settings.BypassDuringReload.Value)
                {
                    SuppressReticleForReload();
                    if (!_meshSurgerySuppressedByReload)
                    {
                        MeshSurgeryManager.RestoreForScope(_activeOptic.transform);
                        LensTransparency.RestoreAll();
                        ScopeEffectsRenderer.Hide();
                        _meshSurgerySuppressedByReload = true;
                        PiPDisablerPlugin.DebugLogInfo(
                            $"[ScopeLifecycle] Mesh surgery suspended during reload. frame={Time.frameCount}");
                    }
                }
                else if (_meshSurgerySuppressedByReload)
                {
                    _meshSurgerySuppressedByReload = false;
                    MeshSurgeryManager.ApplyForOptic(_activeOptic);
                    LensTransparency.HideAllLensSurfaces(_activeOptic);
                    ResumeReticleAfterReload();
                    ScopeEffectsRenderer.Show();
                    PiPDisablerPlugin.DebugLogInfo($"[ScopeLifecycle] Mesh surgery resumed after reload. frame={Time.frameCount}");
                }
            }

            if (_activeOptic != null)
            {
                float mag = FovController.GetVisualMagnification();
                ReticleRenderer.UpdateTransform(mag);
                ScopeEffectsRenderer.UpdateTransform();
            }
            Patches.WeaponScalingPatch.UpdateScale();

        }

        public static void ForceExit()
        {
            _meshSurgerySuppressedByReload = false;
            _reticleSuppressedByReload = false;
            FreelookTracker.Reset();
            if (_isScoped)
                DoScopeExit();
            ReticleRenderer.Cleanup();
            ScopeEffectsRenderer.Cleanup();
            Patches.WeaponScalingPatch.RestoreScale();
            CameraSettingsManager.Restore();
            UnsubscribeMovementState();
            _modBypassedForCurrentScope = false;
            _lastEnabledOptic = null;
        }

        public static void SyncState()
        {
            CheckAndUpdate("SyncState");
        }

        public static string GetActiveScopeWhitelistKey()
        {
            var os = _activeOptic != null ? _activeOptic : _lastEnabledOptic;
            return ResolveWhitelistScopeKey(os);
        }

        public static void ToggleActiveScopeWhitelistEntry()
        {
            var player = GetLocalPlayer();
            var pwa = player?.ProceduralWeaponAnimation;
            if (pwa == null || !pwa.IsAiming)
            {
                PiPDisablerPlugin.DebugLogInfo("[ScopeLifecycle] Whitelist toggle ignored: not aiming");
                return;

            }

            var os = _activeOptic;
            if (os == null)
                os = _lastEnabledOptic;

            if (os == null)
            {
                PiPDisablerPlugin.DebugLogInfo("[ScopeLifecycle] Whitelist toggle ignored: no active scope");
                return;
            }

            string scopeName = ResolveWhitelistScopeKey(os);
            if (string.IsNullOrWhiteSpace(scopeName))
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[ScopeLifecycle] Whitelist toggle ignored: no usable scope key for '{os.name}'");
                return;
            }

            RefreshScopeWhitelistCache();

            bool removed;
            if (_scopeWhitelistNames.Contains(scopeName))
            {
                _scopeWhitelistNames.Remove(scopeName);
                removed = true;
            }
            else
            {
                _scopeWhitelistNames.Add(scopeName);
                removed = false;
            }

            Settings.ScopeWhitelistNames.Value = string.Join(";", _scopeWhitelistNames);
            _scopeWhitelistRawCached = Settings.ScopeWhitelistNames.Value ?? string.Empty;

            PiPDisablerPlugin.DebugLogInfo(
                $"[ScopeLifecycle] Whitelist {(removed ? "removed" : "added")}: scopeKey='{scopeName}'");

            if (Settings.ModEnabled.Value && _isScoped)
            {
                ForceExit();
                SyncState();
            }
        }

        public static void ToggleActiveScopeBlacklistEntry()
        {
            var player = GetLocalPlayer();
            var pwa = player?.ProceduralWeaponAnimation;
            if (pwa == null || !pwa.IsAiming)
            {
                PiPDisablerPlugin.DebugLogInfo("[ScopeLifecycle] Blacklist toggle ignored: not aiming");
                return;
            }

            var os = _activeOptic;
            if (os == null)
                os = _lastEnabledOptic;

            if (os == null)
            {
                PiPDisablerPlugin.DebugLogInfo("[ScopeLifecycle] Blacklist toggle ignored: no active scope");
                return;
            }

            string scopeName = ResolveWhitelistScopeKey(os);
            if (string.IsNullOrWhiteSpace(scopeName))
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[ScopeLifecycle] Blacklist toggle ignored: no usable scope key for '{os.name}'");
                return;
            }

            RefreshScopeBlacklistCache();

            bool removed;
            if (_scopeBlacklistNames.Contains(scopeName))
            {
                _scopeBlacklistNames.Remove(scopeName);
                removed = true;
            }
            else
            {
                _scopeBlacklistNames.Add(scopeName);
                removed = false;
            }

            Settings.ScopeBlacklistNames.Value = string.Join(";", _scopeBlacklistNames);
            _scopeBlacklistRawCached = Settings.ScopeBlacklistNames.Value ?? string.Empty;

            PiPDisablerPlugin.DebugLogInfo(
                $"[ScopeLifecycle] Blacklist {(removed ? "removed" : "added")}: scopeKey='{scopeName}'");

            if (Settings.ModEnabled.Value && _isScoped)
            {
                ForceExit();
                SyncState();
            }
        }

        private static string ResolveWhitelistScopeKey(OpticSight os)
        {
            if (os == null) return null;

            // Primary: derive from the object under mod_scope that is not a mount.
            string modScopeName = ResolveNameUnderModScope(os.transform);
            if (!string.IsNullOrWhiteSpace(modScopeName))
                return modScopeName;

            // Prefer the runtime object name over template metadata.
            // Template reflection can momentarily report stale values on mode switches.
            string objectName = NormalizeScopeKey(os.name);
            if (!string.IsNullOrWhiteSpace(objectName))
                return objectName;

            // Fallback for atypical hierarchies.
            string templateName = FovController.GetOpticTemplateName(os);
            if (!string.IsNullOrWhiteSpace(templateName)
                && !string.Equals(templateName, "unknown", StringComparison.OrdinalIgnoreCase))
                return templateName;

            string templateId = FovController.GetOpticTemplateId(os);
            if (!string.IsNullOrWhiteSpace(templateId)
                && !string.Equals(templateId, "unknown", StringComparison.OrdinalIgnoreCase))
                return templateId;

            return null;
        }

        private static string ResolveNameUnderModScope(Transform opticTransform)
        {
            if (opticTransform == null) return null;

            Transform modScope = null;
            for (var t = opticTransform; t != null; t = t.parent)
            {
                if (ContainsCI(t.name, "mod_scope"))
                {
                    modScope = t;
                    break;
                }
            }

            if (modScope == null) return null;

            // Walk up the active optic path: pick first node under mod_scope that
            // is not a mount/mode container.
            for (var t = opticTransform; t != null && t != modScope; t = t.parent)
            {
                if (!IsUsableScopeNameNode(t.name)) continue;
                return NormalizeScopeKey(t.name);
            }

            return null;
        }

        private static bool IsUsableScopeNameNode(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (ContainsCI(name, "mount")) return false;
            if (ContainsCI(name, "mod_scope")) return false;
            if (name.StartsWith("mode_", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Equals("mode", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static string NormalizeScopeKey(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string key = raw.Trim();
            if (key.EndsWith("(Clone)", StringComparison.OrdinalIgnoreCase))
                key = key.Substring(0, key.Length - "(Clone)".Length).Trim();
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }

        private static bool ContainsCI(string s, string token)
        {
            if (s == null || token == null) return false;
            return s.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Called from SetScopeMode patches after EFT applies scope state changes.
        /// Re-applies zoom/FOV immediately while scoped.
        /// </summary>
        public static void OnSetScopeMode()
        {
            RefreshScopeAimTransformsForModeSwitch();
            if (!_isScoped) return;
            if (!Settings.ModEnabled.Value) return;

            PiPDisablerPlugin.DebugLogInfo(
                $"[ScopeLifecycle] SetScopeMode fired while scoped frame={Time.frameCount}");



            var os = _activeOptic;
            if (os == null) return;

            bool shouldBypass = ShouldBypassForCurrentOptic(os);
            if (shouldBypass)
            {
                bool wasBypassed = _modBypassedForCurrentScope;
                _modBypassedForCurrentScope = true;
                ApplyBypassState(os, reason: "set scope mode",
                    restoreFov: !wasBypassed);
                return;
            }

            if (_modBypassedForCurrentScope)
            {
                _modBypassedForCurrentScope = false;
                PiPDisablerPlugin.DebugLogInfo(
                    $"[ScopeLifecycle] Bypass cleared on scope mode for '{os.name}'");
            }

            FovController.OnModeSwitch();
            if (!FreelookTracker.IsFreelooking)
                ApplyFov(true);
        }

        private static void RefreshScopeAimTransformsForModeSwitch()
        {
            try
            {
                var player = GetLocalPlayer();
                var pwa = player?.ProceduralWeaponAnimation;
                if (pwa == null)
                    return;

                pwa.FindAimTransforms();
            }
            catch (Exception ex)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[ScopeLifecycle] Failed to refresh scope aim transforms on mode switch: {ex.Message}");
            }
        }

        internal static bool ShouldBypassForCurrentOptic(OpticSight os)
        {
            if (os == null) return false;

            if (HasTacticalRangeFinder(os))
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[ScopeLifecycle] Rangefinder bypass: '{os.name}' contains TacticalRangeFinderController");
                return true;
            }

            if (ShouldBypassByBlacklist(os))
            {
                return true;
            }

            if (ShouldBypassByWhitelist(os))
            {
                return true;
            }

            if (Settings.AutoDisableForVariableScopes.Value
                && IsThermalOrNightVisionOptic(os))
            {
                return true;
            }

            if (ScopeNameMatchesBypassPattern(os))
            {
                return true;
            }
            return false;
        }

        private static bool HasTacticalRangeFinder(OpticSight os)
        {
            try
            {
                return os.GetComponentInChildren<TacticalRangeFinderController>(true) != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool ShouldBypassByBlacklist(OpticSight os)
        {
            RefreshScopeBlacklistCache();
            if (_scopeBlacklistNames.Count == 0)
                return false;

            string scopeName = ResolveWhitelistScopeKey(os);
            bool blacklisted = !string.IsNullOrEmpty(scopeName)
                && !string.Equals(scopeName, "unknown", StringComparison.OrdinalIgnoreCase)
                && _scopeBlacklistNames.Contains(scopeName);

            if (blacklisted)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[ScopeLifecycle] Blacklist bypass: '{os.name}'[scopeKey={scopeName}] is in ScopeBlacklistNames");
            }

            return blacklisted;
        }

        private static bool ScopeNameMatchesBypassPattern(OpticSight os)
        {
            if (os == null) return false;
            string raw = Settings.AutoBypassNameContains?.Value;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string scopeKey = ResolveWhitelistScopeKey(os) ?? string.Empty;
            string objectName = os.name ?? string.Empty;

            foreach (string token in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = token.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                if (ContainsCI(scopeKey, t) || ContainsCI(objectName, t))
                {
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[ScopeLifecycle] AutoBypassNameContains match: token='{t}'" +
                        $" objectName='{objectName}' scopeKey='{scopeKey}'");
                    return true;
                }
            }
            return false;
        }

        private static bool ShouldBypassByWhitelist(OpticSight os)
        {
            RefreshScopeWhitelistCache();
            if (_scopeWhitelistNames.Count == 0)
                return false;

            string scopeName = ResolveWhitelistScopeKey(os);
            bool allowed = !string.IsNullOrEmpty(scopeName)
                && !string.Equals(scopeName, "unknown", StringComparison.OrdinalIgnoreCase)
                && _scopeWhitelistNames.Contains(scopeName);

            if (!allowed)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[ScopeLifecycle] Whitelist bypass: '{os.name}'[scopeKey={scopeName}] is not in ScopeWhitelistNames");
            }

            return !allowed;
        }

        private static void RefreshScopeBlacklistCache()
        {
            string raw = Settings.ScopeBlacklistNames.Value ?? string.Empty;
            if (string.Equals(raw, _scopeBlacklistRawCached, StringComparison.Ordinal))
                return;

            _scopeBlacklistRawCached = raw;
            _scopeBlacklistNames.Clear();

            var parts = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                string id = part.Trim();
                if (!string.IsNullOrEmpty(id))
                    _scopeBlacklistNames.Add(id);
            }
        }

        private static void RefreshScopeWhitelistCache()
        {
            string raw = Settings.ScopeWhitelistNames.Value ?? string.Empty;
            if (string.Equals(raw, _scopeWhitelistRawCached, StringComparison.Ordinal))
                return;

            _scopeWhitelistRawCached = raw;
            _scopeWhitelistNames.Clear();

            var parts = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                string id = part.Trim();
                if (!string.IsNullOrEmpty(id))
                    _scopeWhitelistNames.Add(id);
            }
        }

        private static bool IsThermalOrNightVisionOptic(OpticSight os)
        {
            if (os == null) return false;

            try
            {
                ScopeData scopeData = os.GetComponentInParent<ScopeData>();
                if (scopeData == null)
                    scopeData = os.GetComponentInChildren<ScopeData>(true);

                // ScopeData may live as a sibling under scope root (not direct parent/child of OpticSight).
                if (scopeData == null)
                {
                    Transform scopeRoot = ScopeHierarchy.FindScopeRoot(os.transform);
                    if (scopeRoot != null)
                    {
                        scopeData = scopeRoot.GetComponentInChildren<ScopeData>(true);
                        if (scopeData == null && scopeRoot.parent != null)
                            scopeData = scopeRoot.parent.GetComponentInChildren<ScopeData>(true);
                    }
                }

                if (scopeData == null)
                    return false;

                bool hasNightVision = scopeData.NightVisionData != null;
                bool hasThermal = scopeData.ThermalVisionData != null;

                if (hasNightVision || hasThermal)
                {
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[ScopeLifecycle] Thermal/NV auto-bypass match: nightVision={hasNightVision} thermal={hasThermal}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[ScopeLifecycle] Thermal/NV detection failed: {ex.Message}");
            }

            return false;
        }

        // ===== State transitions =====

        private static void ApplyBypassState(OpticSight os, string reason,
            bool restoreFov)
        {
            string opticName = os != null ? os.name : "null";
            PiPDisablerPlugin.DebugLogInfo(
                $"[ScopeLifecycle] Bypassing mod for current scope ({reason}): " +
                $"'{opticName}'[{FovController.GetOpticTemplateId(os)}] " +
                $"key='{ResolveWhitelistScopeKey(os)}' adjustable={FovController.IsOpticAdjustable(os)} " +
                $"restoreFov={restoreFov}");

            // On fresh scope enter, forcing RestoreFov() can stomp EFT's own optic zoom
            // for bypassed scopes until the next zoom/mode event. Let vanilla drive FOV
            // immediately on enter, but still restore when switching from a modded mode.
            if (restoreFov)
            {
                RestoreFov();
            }

            _postExitRestoreFov = 0f;
            _postExitRestoreExpiry = 0f;

            Patches.WeaponScalingPatch.RestoreScale();
            ReticleRenderer.Cleanup();
            ScopeEffectsRenderer.Cleanup();
            LensTransparency.RestoreAll();
            CameraSettingsManager.ForceRestore();
            PiPDisabler.RestoreAllCameras();
            Patches.VanillaOpticSuppression.RestoreVanillaOpticState(os);

            if (os != null)
                MeshSurgeryManager.RestoreForScope(os.transform);
            else
                MeshSurgeryManager.RestoreAll();
        }

        private static void DoScopeEnter()
        {
            var os = FindEnabledOpticFromPWA();
            if (os == null)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    "[ScopeLifecycle] ENTER aborted — no OpticSight found");
                return;
            }

            _isScoped = true;
            _activeOptic = os;
            PerScopeMeshSurgerySettings.SetActiveScope(ResolveWhitelistScopeKey(os));

            _modBypassedForCurrentScope = ShouldBypassForCurrentOptic(os);
            if (_modBypassedForCurrentScope)
            {
                ApplyBypassState(os, reason: "scope enter",
                    restoreFov: false);
                return;
            }

            PiPDisablerPlugin.DebugLogInfo(
                $"[ScopeLifecycle] ENTER: '{os.name}'[{FovController.GetOpticTemplateId(os)}] frame={Time.frameCount}");

            // 1. Extract reticle texture BEFORE destroying lens mesh
            ReticleRenderer.ExtractReticle(os);

            // 2. Hide ALL lens surfaces in the scope hierarchy (once)
            LensTransparency.HideAllLensSurfaces(os);

            // 2b. Collect lens renderers for the reticle stencil mask.
            var lensMaskEntries = CollectStencilEntries(os);
            ReticleRenderer.SetLensMaskEntries(lensMaskEntries);
            var occluderRenderers = LensTransparency.CollectHousingRenderers(os);
            ReticleRenderer.SetOccluderMaskRenderers(occluderRenderers);

            // 3. Get magnification for reticle scaling and zoom
            float mag = FovController.GetVisualMagnification();

            // 4. Show reticle overlay at the lens position, scaled for magnification
            ReticleRenderer.Show(os, mag);

            // 4b. Show lens vignette + scope shadow effects
            ScopeEffectsRenderer.Show();

            // 5. Mesh surgery (once) — if it fails (zero entries), Tick() will retry
            _meshSurgerySuppressedByReload = false;
            if (IsReloadActive())
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[ScopeLifecycle] Skipping initial mesh surgery because reload is active. frame={Time.frameCount}");
                LensTransparency.RestoreAll();
                SuppressReticleForReload();
                _meshSurgerySuppressedByReload = true;
            }
            else
            {
                MeshSurgeryManager.ApplyForOptic(os);
            }

            // 6. Swap main camera LOD/culling settings with scope camera settings
            CameraSettingsManager.ApplyForOptic(os);

            // 7. Capture weapon base scale/FOV BEFORE changing FOV (for weapon scaling compensation)
            Patches.WeaponScalingPatch.CaptureBaseState();

            // 8. Apply animated FOV zoom (uses FovAnimationDuration)
            // Reset dead-band so the initial ApplyFov always fires regardless of previous state.
            // Also clear any pending post-exit restore from a previous scope session.
            _postExitRestoreFov = 0f;
            FovController.OnModeSwitch();
            ApplyFov(true);

        }

        private static void DoScopeExit()
        {
            if (!_isScoped) return;

            _meshSurgerySuppressedByReload = false;
            _reticleSuppressedByReload = false;

            // Reset freelook tracking so stale state doesn't persist into next scope
            FreelookTracker.Reset();

            var prevOptic = _activeOptic;
            PiPDisablerPlugin.DebugLogInfo(
                $"[ScopeLifecycle] EXIT: '{(prevOptic != null ? prevOptic.name : "null")}'" +
                $"[{FovController.GetOpticTemplateId(prevOptic)}] frame={Time.frameCount}");


            _isScoped = false;
            _activeOptic = null;
            PerScopeMeshSurgerySettings.ClearActiveScope();

            // If this scope was bypassed, skip mod cleanup paths.
            if (_modBypassedForCurrentScope)
            {
                _modBypassedForCurrentScope = false;
                _restoreOneXFovOnScopeExit = false;
                return;
            }

            PiPDisabler.CleanupVanillaOpticState(prevOptic);

            // 1. Restore FOV with ADS-matched animation timing
            RestoreFov();

            // 1b. Restore normal weapon model scaling (after FOV is back to normal)
            Patches.WeaponScalingPatch.RestoreScale();

            // 3. Hide reticle overlay + scope effects
            bool allowShadowPersist = !_restoreOneXFovOnScopeExit;
            bool keepShadowMask = ScopeEffectsRenderer.OnScopeExit(allowShadowPersist);
            ReticleRenderer.OnScopeExit(keepShadowMask);

            // 4. Restore lens
            LensTransparency.RestoreAll();

            // 5. Restore camera LOD/culling settings
            CameraSettingsManager.Restore();

            // 6. Restore meshes
            if (prevOptic != null)
                MeshSurgeryManager.RestoreForScope(prevOptic.transform);
            else
                MeshSurgeryManager.RestoreAll();

            _restoreOneXFovOnScopeExit = false;
        }

        // ===== FOV Helpers =====

        /// <summary>
        /// Public entry point to re-apply FOV zoom.
        /// Called when scroll wheel changes magnification so the FOV updates
        /// to match the new zoom level without waiting for the next method_23 call.
        /// Uses a short duration for responsive feel.
        /// </summary>
        public static void ReapplyFov()
        {
            if (!_isScoped) return;
            if (_modBypassedForCurrentScope) return;
            if (FreelookTracker.IsFreelooking) return;
            ApplyFov(false); // false = short duration for scroll feel
        }

        /// <summary>
        /// Collects stencil-mask renderers from the scope lens only.
        /// The housing and weapon body are intentionally excluded.
        /// </summary>
        private static List<LensTransparency.LensMaskEntry> CollectStencilEntries(OpticSight os)
        {
            return LensTransparency.CollectLensMaskEntries(os);
        }

        /// <summary>
        /// Apply the FOV zoom for the current scope, with configurable animation duration.
        /// isTransition=true uses FovAnimationDuration config (scope enter / mode switch).
        /// </summary>
        private static void ApplyFov(bool isTransition)
        {
            try
            {
                if (_modBypassedForCurrentScope) return;
                if (!CameraClass.Exist) return;

                float zoomBaseFov = FovController.MagnificationBaselineFov;
                float zoomedFov = FovController.ComputeZoomedFov();
                bool smoothScopeFov = FovController.IsSmoothScopeFovActive();

                if (zoomedFov >= 0.5f && (smoothScopeFov || zoomedFov < zoomBaseFov))
                {
                    float duration = isTransition
                        ? Settings.FovAnimationDuration.Value
                        : 0.1f; // Short duration for variable zoom updates

                    // Skip if the target hasn't changed enough (prevents coroutine restarts
                    // that stall the lerp and cause flashing). Always apply on mode transitions.
                    if (!isTransition && !FovController.HasFovChanged(zoomedFov))
                        return;

                    FovController.TrackAppliedFov(zoomedFov);
                    CameraClass.Instance.SetFov(zoomedFov, duration, false);
                    FreelookTracker.CacheAppliedFov(zoomedFov);
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[ScopeLifecycle] ApplyFov: {zoomedFov:F1}° dur={duration:F2}s");
                }
                else if (isTransition && !smoothScopeFov && zoomedFov >= zoomBaseFov)
                {
                    // High-to-low mode switch where new mode has no zoom:
                    // restore to baseline with configured duration so both directions are consistent
                    float duration = Settings.FovAnimationDuration.Value;
                    FovController.TrackAppliedFov(zoomBaseFov);
                    CameraClass.Instance.SetFov(zoomBaseFov, duration, false);
                    FreelookTracker.CacheAppliedFov(zoomBaseFov);
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[ScopeLifecycle] ApplyFov (restore baseline): {zoomBaseFov:F1}° dur={duration:F2}s");
                }
            }
            catch (Exception ex)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[ScopeLifecycle] ApplyFov error: {ex.Message}");
            }
        }

        /// <summary>
        /// Restore FOV to baseline using the same ADS animation timing.
        /// </summary>
        private static void RestoreFov()
        {
            try
            {
                if (!CameraClass.Exist) return;
                var cc = CameraClass.Instance;
                if (cc == null) return;

                var player = GetLocalPlayer();
                if (player == null) return;
                var pwa = player.ProceduralWeaponAnimation;
                if (pwa == null) return;

                float baseFov = pwa.Single_2;
                float targetFov = _restoreOneXFovOnScopeExit
                    ? Mathf.Max(1f, baseFov - 15f)
                    : baseFov;
                if (targetFov > 30f)
                {
                    float duration = Settings.FovAnimationDuration.Value;
                    // Arm the post-exit suppressor BEFORE calling SetFov so that any
                    // concurrent method_23 tick in the same frame is already blocked.
                    _postExitRestoreFov = targetFov;
                    float suppressFor = Mathf.Max(duration, 0.05f) + 0.05f;
                    _postExitRestoreExpiry = Time.realtimeSinceStartup + suppressFor;
                    FovController.TrackAppliedFov(targetFov);
                    cc.SetFov(targetFov, duration, true);
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[ScopeLifecycle] RestoreFov: {targetFov:F1}° dur={duration:F2}s suppress={suppressFor:F2}s");
                }
            }
            catch (Exception ex)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[ScopeLifecycle] RestoreFov error: {ex.Message}");
            }
        }

        // ===== Other Helpers =====

        private static Player GetLocalPlayer()
            => Helpers.GetLocalPlayer();

        private static bool IsReloadActive()
        {
            var player = GetLocalPlayer();
            var pwa = player.ProceduralWeaponAnimation;
            var BipodActive = pwa.IsBipodUsed;
            if (BipodActive)
                return false;
            var field = AccessTools.Field(typeof(ProceduralWeaponAnimation), "_tacticalReload");
            var blender = field.GetValue(pwa);
            var valueProp = AccessTools.Property(blender.GetType(), "Value");
            float blendValue = (float)valueProp.GetValue(blender, null);
            return blendValue > (Mathf.Epsilon + Settings.ReloadBypassModifier.Value);
        }

        private static void ArmPostSprintAimGate()
        {
            _postSprintAimGateExpiry = Time.realtimeSinceStartup + Mathf.Max(0f, Settings.PostSprintAimGateDuration.Value);
        }

        private static void EnsureMovementStateSubscription(Player player)
        {
            var movementContext = player?.MovementContext;
            if (movementContext == _subscribedMovementContext)
                return;

            UnsubscribeMovementState();

            if (movementContext == null)
                return;

            movementContext.OnStateChanged += OnMovementStateChanged;
            _subscribedMovementContext = movementContext;
        }

        private static void UnsubscribeMovementState()
        {
            if (_subscribedMovementContext == null)
                return;

            try { _subscribedMovementContext.OnStateChanged -= OnMovementStateChanged; }
            catch { }
            _subscribedMovementContext = null;
        }

        private static void OnMovementStateChanged(EPlayerState previousState, EPlayerState nextState)
        {
            if (previousState != EPlayerState.Sprint || nextState == EPlayerState.Sprint)
                return;

            ArmPostSprintAimGate();
            PiPDisablerPlugin.DebugLogInfo(
                $"[ScopeLifecycle] Post-sprint aim gate armed by MovementContext state change: {previousState} -> {nextState}");
        }

        private static void ArmPostStanceAimGate()
        {
            _postStanceAimGateExpiry = Time.realtimeSinceStartup + Mathf.Max(0f, Settings.PostStanceAimGateDuration.Value);
        }

        private static bool ShouldApplyScopeAlignmentGate()
        {
            float now = Time.realtimeSinceStartup;
            return now < _postSprintAimGateExpiry || now < _postStanceAimGateExpiry;
        }

        private static bool IsPlayerSprinting(Player player)
        {
            try
            {
                if (player == null)
                    return false;

                if (player.Physical != null && player.Physical.Sprinting)
                    return true;

                var movementContext = player.MovementContext;
                return movementContext != null &&
                       (movementContext.IsSprintEnabled ||
                        movementContext.CurrentState != null &&
                        movementContext.CurrentState.Name == EPlayerState.Sprint);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsStanceTransitionActive(Player player)
        {
            try
            {
                var movementContext = player?.MovementContext;
                if (movementContext?.CurrentState == null)
                    return false;

                EPlayerState state = movementContext.CurrentState.Name;
                return state == EPlayerState.Transit2Prone ||
                       state == EPlayerState.Prone2Stand;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsScopeAlignedWithMainCamera(ProceduralWeaponAnimation.SightNBone currentScope, OpticSight enabledOptic, out float angle, out float tolerance)
        {
            angle = 0f;
            tolerance = Mathf.Max(0f, Settings.ScopeAlignmentAngleTolerance.Value);

            try
            {
                Camera mainCamera = Helpers.GetMainCamera();
                Transform mainCameraTransform = mainCamera != null ? mainCamera.transform : null;

                if (mainCameraTransform == null || !TryGetScopeAxisDirection(currentScope, enabledOptic, out Vector3 scopeAxis))
                    throw new MissingMemberException("Missing scope axis or main camera transform");

                angle = Vector3.Angle(scopeAxis, mainCameraTransform.forward);
                return angle <= tolerance;
            }
            catch (Exception ex)
            {
                if (!_scopeAlignmentGateFailureLogged)
                {
                    _scopeAlignmentGateFailureLogged = true;
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[ScopeLifecycle] Could not evaluate scope alignment gate; allowing scope activation. {ex.Message}");
                }
                angle = 0f;
                return true;
            }
        }

        private static bool TryGetScopeAxisDirection(ProceduralWeaponAnimation.SightNBone currentScope, OpticSight enabledOptic, out Vector3 direction)
        {
            direction = Vector3.zero;

            if (currentScope?.Bone != null)
            {
                Transform bone = currentScope.Bone;
                direction = bone.name == "aim_camera" ? -bone.up : -bone.forward;
                return direction.sqrMagnitude > Mathf.Epsilon;
            }

            if (PiPDisabler.OpticCameraTransform != null)
            {
                direction = PiPDisabler.OpticCameraTransform.forward;
                return direction.sqrMagnitude > Mathf.Epsilon;
            }

            if (enabledOptic != null)
            {
                direction = enabledOptic.transform.forward;
                return direction.sqrMagnitude > Mathf.Epsilon;
            }

            return false;
        }

        private static void SuppressReticleForReload()
        {
            if (_reticleSuppressedByReload) return;
            ReticleRenderer.Hide();
            _reticleSuppressedByReload = true;
        }

        private static void ResumeReticleAfterReload()
        {
            if (!_reticleSuppressedByReload) return;
            if (_activeOptic == null) return;
            float mag = FovController.GetVisualMagnification();
            ReticleRenderer.Show(_activeOptic, mag);
            _reticleSuppressedByReload = false;
        }

        private static OpticSight TryGetCurrentScopeOpticFromPwa()
        {
            try
            {
                var player = GetLocalPlayer();
                var pwa = player?.ProceduralWeaponAnimation;
                if (pwa == null) return null;

                var currentScope = _getCurrentScope(pwa);
                if (currentScope == null || !_getIsOptic(currentScope))
                    return null;

                return currentScope.ScopePrefabCache?.CurrentModOpticSight;
            }
            catch
            {
                return null;
            }
        }

        private static OpticSight FindEnabledOpticFromPWA()
        {
            if (_activeOptic != null && _activeOptic.isActiveAndEnabled)
                return _activeOptic;

            if (_lastEnabledOptic != null && _lastEnabledOptic.isActiveAndEnabled)
                return _lastEnabledOptic;

            var currentOptic = TryGetCurrentScopeOpticFromPwa();
            if (currentOptic != null && currentOptic.isActiveAndEnabled)
                return currentOptic;

            // During rapid transitions (mode switch), the incoming optic may not
            // be marked enabled yet. Trust the cache rather than doing a scene scan.
            // The OnEnable patch will fire imminently and update the cache.
            return null;
        }

    }
}
