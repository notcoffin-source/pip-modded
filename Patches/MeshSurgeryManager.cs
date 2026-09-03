using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.CameraControl;
using EFT.InventoryLogic;
using Comfort.Common;
using UnityEngine;

namespace PiPDisabler
{
    public static class MeshSurgeryManager
    {
        private sealed class CutMeshEntry
        {
            public MeshFilter Filter;
            public Mesh OriginalMesh;
            public Mesh CutMesh;
            public bool Applied;
            public string FilterPath;
        }

        private sealed class CutProfileCache
        {
            public GameObject WeaponRoot;
            public readonly List<CutMeshEntry> Entries = new List<CutMeshEntry>(64);
            public bool Built;
            public bool Dirty = true;
            public string SettingsSignature;
            public string ProfileKey;
        }

        private sealed class RaidWeaponCache
        {
            public string WeaponId;
            public Weapon WeaponItem;
            public readonly Dictionary<string, CutProfileCache> Profiles = new Dictionary<string, CutProfileCache>(4);
        }

        private sealed class LightFxState
        {
            public bool WasActiveSelf;
            public bool DisabledByUs;
        }

        private sealed class SphereState
        {
            public bool WasActiveSelf;
            public bool DisabledByUs;
        }

        private static readonly Dictionary<string, RaidWeaponCache> _raidCaches = new Dictionary<string, RaidWeaponCache>(16);
        private static CutProfileCache _currentWeaponCache;
        private static string _currentWeaponId;
        private static readonly Dictionary<GameObject, LightFxState> _disabledLightFx =
            new Dictionary<GameObject, LightFxState>(32);
        private static readonly Dictionary<GameObject, SphereState> _disabledWeaponSpheres =
            new Dictionary<GameObject, SphereState>(32);
        private static bool _loggedGpuCopy;
        private static int _lastCutAttemptFrame;
        private static object _inventoryEventSource;
        private static Delegate _addItemHandler;
        private static Delegate _removeItemHandler;
        private static int _lastAttemptTargets;
        private static int _lastAttemptEntries;
        private static int _lastAttemptReadableCopyFailures;
        private static bool _lastAttemptWeaponRootFound;
        private static bool _lastAttemptPlaneFound;
        private static string _lastAttemptOptic = "<none>";
        private static string _lastAttemptScopeRoot = "<none>";
        private static string _lastAttemptActiveMode = "<none>";

        public static void ApplyForOptic(OpticSight os)
        {
            if (os == null) return;

            var scopeRoot = ScopeHierarchy.FindScopeRoot(os.transform);
            if (!scopeRoot) return;

            var activeMode = ResolveActiveMode(os, scopeRoot);
            var cache = GetOrCreateCurrentWeaponCache(scopeRoot, activeMode);
            if (cache == null) return;

            string currentSignature = BuildCutSettingsSignature();
            if (cache.Built && !cache.Dirty && !string.Equals(cache.SettingsSignature, currentSignature, StringComparison.Ordinal))
            {
                cache.Dirty = true;
                PiPDisablerPlugin.DebugLogInfo("[MeshSurgery] Cut settings changed; marking weapon cache dirty.");
            }

            if (cache.Dirty || !cache.Built)
            {
                RebuildCutCacheForOptic(cache, os, scopeRoot, activeMode);
            }
            else
            {
                ReapplyCachedCutMeshes(cache, scopeRoot);
                if (cache.Dirty)
                    RebuildCutCacheForOptic(cache, os, scopeRoot, activeMode);
            }
        }

        public static void RestoreForScope(Transform anyTransformUnderScope)
        {
            var scopeRoot = ScopeHierarchy.FindScopeRoot(anyTransformUnderScope);
            if (!scopeRoot) return;

            var cache = _currentWeaponCache;
            if (cache == null || cache.WeaponRoot == null) return;

            if (scopeRoot.gameObject != cache.WeaponRoot && !scopeRoot.IsChildOf(cache.WeaponRoot.transform))
                return;

            RestoreOriginalMeshes(cache);
            RestoreLightEffectMeshesUnderRoot(cache.WeaponRoot.transform);
            RestoreWeaponSphereObjectsUnderRoot(cache.WeaponRoot.transform);
        }

        public static void RestoreAll()
        {
            foreach (var weaponCache in _raidCaches.Values)
            {
                if (weaponCache == null) continue;
                foreach (var profile in weaponCache.Profiles.Values)
                    RestoreOriginalMeshes(profile);
            }

            var lightKeys = _disabledLightFx.Keys.ToArray();
            var sphereKeys = _disabledWeaponSpheres.Keys.ToArray();
            if (lightKeys.Length == 0 && sphereKeys.Length == 0) return;

            foreach (var go in lightKeys)
            {
                if (go != null && _disabledLightFx.TryGetValue(go, out var st) && st != null && st.DisabledByUs)
                {
                    try
                    {
                        if (st.WasActiveSelf && !go.activeSelf)
                            go.SetActive(true);
                    }
                    catch { }
                }
                _disabledLightFx.Remove(go);
            }

            foreach (var go in sphereKeys)
            {
                if (go != null && _disabledWeaponSpheres.TryGetValue(go, out var st) && st != null && st.DisabledByUs)
                {
                    try
                    {
                        if (st.WasActiveSelf && !go.activeSelf)
                            go.SetActive(true);
                    }
                    catch { }
                }
                _disabledWeaponSpheres.Remove(go);
            }
        }

        public static void CleanupForShutdown()
        {
            RestoreAll();
            DestroyCurrentWeaponCache();
            UnbindInventoryEvents();
            _lastCutAttemptFrame = 0;
        }

        /// <summary>
        /// Returns true if the current weapon cache has at least one successfully
        /// applied cut mesh entry. Used by ScopeLifecycle.Tick() to detect whether
        /// the initial mesh surgery silently produced zero cuts (e.g. GPU buffers
        /// not ready on the first frame, or TryGetPlane returned a degenerate position).
        /// </summary>
        public static bool HasSuccessfulCut()
        {
            var cache = _currentWeaponCache;
            if (cache == null) return false;
            if (!cache.Built) return false;
            for (int i = 0; i < cache.Entries.Count; i++)
            {
                var entry = cache.Entries[i];
                if (entry != null && entry.Applied && entry.Filter != null && entry.CutMesh != null)
                    return true;
            }
            return false;
        }

        public static string GetLastAttemptDebugSnapshot()
        {
            return
                $"optic='{_lastAttemptOptic}' scopeRoot='{_lastAttemptScopeRoot}' mode='{_lastAttemptActiveMode}' " +
                $"weaponRootFound={_lastAttemptWeaponRootFound} planeFound={_lastAttemptPlaneFound} " +
                $"targets={_lastAttemptTargets} entries={_lastAttemptEntries} readableCopyFailures={_lastAttemptReadableCopyFailures} " +
                $"frame={_lastCutAttemptFrame}";
        }

        /// <summary>
        /// Force a full rebuild of the current weapon cache.
        /// Called from ScopeLifecycle.Tick() when mesh surgery produced zero entries
        /// on the initial ADS frame (GPU buffers / transform positions weren't ready).
        /// Returns true if the retry produced at least one cut entry.
        /// </summary>
        public static bool RetryPendingCut(OpticSight os)
        {
            if (os == null) return false;

            // Throttle: don't retry more than once every 3 frames
            if (Time.frameCount - _lastCutAttemptFrame < 3) return false;
            _lastCutAttemptFrame = Time.frameCount;

            var cache = _currentWeaponCache;
            if (cache == null)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[MeshSurgery][Retry] No current weapon cache — calling ApplyForOptic. frame={Time.frameCount}");
                ApplyForOptic(os);
                cache = _currentWeaponCache;
                return HasSuccessfulCut();
            }

            // Force a full rebuild by marking dirty
            PiPDisablerPlugin.DebugLogInfo(
                $"[MeshSurgery][Retry] Forcing rebuild: Built={cache.Built} Entries={cache.Entries.Count} frame={Time.frameCount}");
            cache.Dirty = true;
            ApplyForOptic(os);

            bool success = HasSuccessfulCut();
            PiPDisablerPlugin.DebugLogInfo(
                $"[MeshSurgery][Retry] Result: Entries={cache.Entries.Count} success={success} frame={Time.frameCount}");
            return success;
        }

        private static CutProfileCache GetOrCreateCurrentWeaponCache(Transform scopeRoot, Transform activeMode)
        {
            var player = Singleton<GameWorld>.Instance != null ? Singleton<GameWorld>.Instance.MainPlayer : null;
            var fc = player != null ? player.HandsController as Player.FirearmController : null;
            var weapon = fc != null ? fc.Item as Weapon : null;

            var weaponRootTf = FindWeaponTransform(scopeRoot);
            var weaponRoot = weaponRootTf != null ? weaponRootTf.gameObject : null;
            if (weapon == null || weaponRoot == null) return null;

            string weaponId = !string.IsNullOrEmpty(weapon.Id) ? weapon.Id : weapon.TemplateId;
            if (!_raidCaches.TryGetValue(weaponId, out var weaponCache) || weaponCache == null)
            {
                weaponCache = new RaidWeaponCache { WeaponId = weaponId };
                _raidCaches[weaponId] = weaponCache;
            }

            weaponCache.WeaponItem = weapon;

            string profileKey = BuildProfileKey(weaponRootTf, scopeRoot, activeMode, BuildCutSettingsSignature());
            if (!weaponCache.Profiles.TryGetValue(profileKey, out var profileCache) || profileCache == null)
            {
                profileCache = new CutProfileCache
                {
                    WeaponRoot = weaponRoot,
                    ProfileKey = profileKey,
                    Built = false,
                    Dirty = true
                };
                weaponCache.Profiles[profileKey] = profileCache;
            }
            else
            {
                profileCache.WeaponRoot = weaponRoot;
            }

            if (!ReferenceEquals(_currentWeaponCache, profileCache) && _currentWeaponCache != null)
                RestoreOriginalMeshes(_currentWeaponCache);

            if (!string.Equals(_currentWeaponId, weaponId, StringComparison.Ordinal))
            {
                PiPDisablerPlugin.DebugLogInfo($"[MeshSurgery] Weapon cache switched: '{weapon.TemplateId}' ({weaponId})");
            }

            _currentWeaponId = weaponId;
            _currentWeaponCache = profileCache;

            BindInventoryEvents(player);
            return profileCache;
        }

        private static void RebuildCutCacheForOptic(CutProfileCache cache, OpticSight os, Transform scopeRoot, Transform activeMode)
        {
            if (cache == null || os == null || scopeRoot == null) return;
            if (!activeMode) activeMode = os.transform;
            _lastAttemptOptic = os.name;
            _lastAttemptScopeRoot = scopeRoot.name;
            _lastAttemptActiveMode = activeMode.name;
            _lastAttemptWeaponRootFound = false;
            _lastAttemptPlaneFound = false;
            _lastAttemptTargets = 0;
            _lastAttemptEntries = 0;
            _lastAttemptReadableCopyFailures = 0;

            var weaponRootTf = FindWeaponTransform(scopeRoot);
            if (weaponRootTf == null)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[MeshSurgery][DEBUG] FindWeaponTransform returned null for scopeRoot='{scopeRoot.name}' frame={Time.frameCount}");
                return;
            }
            _lastAttemptWeaponRootFound = true;
            cache.WeaponRoot = weaponRootTf.gameObject;

            if (!ScopeHierarchy.TryGetPlane(os, scopeRoot, activeMode,
                out var planePoint, out var planeNormal, out var camPos))
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[MeshSurgery][DEBUG] TryGetPlane FAILED — no plane found. " +
                    $"os='{os.name}' scopeRoot='{scopeRoot.name}' activeMode='{activeMode.name}' frame={Time.frameCount}");
                return;
            }
            _lastAttemptPlaneFound = true;

            bool isCylinderMode = true;
            float plane1Offset = isCylinderMode
                ? PerScopeMeshSurgerySettings.GetPlane1OffsetMeters()
                : PerScopeMeshSurgerySettings.GetPlaneOffsetMeters();
            planePoint += planeNormal * plane1Offset;

            var keepSide = DecideKeepPositive(planePoint, planeNormal, camPos)
                ? MeshPlaneCutter.KeepSide.Positive
                : MeshPlaneCutter.KeepSide.Negative;


            RestoreOriginalMeshes(cache);
            DestroyCutMeshes(cache);
            cache.Entries.Clear();

            var targets = ScopeHierarchy.FindTargetMeshFilters(scopeRoot, activeMode);
            _lastAttemptTargets = targets.Count;
            float cutRadius = 0;

            DisableLightEffectMeshesForScope(scopeRoot);
            DisableWeaponSphereObjects(scopeRoot);

            foreach (var mf in targets)
            {
                if (!mf || !mf.sharedMesh) continue;

                var renderer = mf.GetComponent<Renderer>();
                var boundsCenter = renderer != null ? renderer.bounds.center : mf.transform.position;
                float distFromPlane = Vector3.Distance(boundsCenter, planePoint);

                if (cutRadius > 0f && distFromPlane > cutRadius)
                    continue;

                Mesh originalAsset = mf.sharedMesh;

                try
                {
                    bool isCylinder = true;
                    Mesh readable = MeshPlaneCutter.MakeReadableMeshCopy(originalAsset);
                    if (readable == null)
                    {
                        PiPDisablerPlugin.DebugLogInfo(
                            $"[MeshSurgery][DEBUG] MakeReadableMeshCopy returned null for '{originalAsset.name}' " +
                            $"(isReadable={originalAsset.isReadable} verts={originalAsset.vertexCount}) frame={Time.frameCount}");
                        _lastAttemptReadableCopyFailures++;
                        continue;
                    }

                    if (!_loggedGpuCopy)
                    {
                        _loggedGpuCopy = true;
                        PiPDisablerPlugin.DebugLogInfo(
                            "[MeshSurgery] Created readable mesh copies via GPU buffer. Plane cutting enabled.");
                    }

                    int vertsBefore = readable.vertexCount;
                    bool ok;
                    if (isCylinder)
                    {
                        float nearR = PerScopeMeshSurgerySettings.GetPlane1Radius();
                        float startOff = PerScopeMeshSurgerySettings.GetCutStartOffset();
                        float cutLen = PerScopeMeshSurgerySettings.GetCutLength();
                        float preserve = PerScopeMeshSurgerySettings.GetNearPreserveDepth();
                        float p2 = PerScopeMeshSurgerySettings.GetPlane2PositionNormalized(cutLen);
                        float r2 = PerScopeMeshSurgerySettings.GetPlane2Radius();
                        float p3 = PerScopeMeshSurgerySettings.GetPlane3Position();
                        float r3 = PerScopeMeshSurgerySettings.GetPlane3Radius();
                        float p4 = PerScopeMeshSurgerySettings.GetPlane4Position();
                        float r4 = PerScopeMeshSurgerySettings.GetPlane4Radius();
                        ok = MeshPlaneCutter.CutMeshFrustum(readable, mf.transform,
                            planePoint, planeNormal, nearR, r4, startOff, cutLen,
                            keepInside: false, midRadius: r2, midPosition: p2,
                            nearPreserveDepth: preserve,
                            plane3Radius: r3, plane3Position: p3, plane4Position: p4);
                    }
                    else
                    {
                        ok = MeshPlaneCutter.CutMeshDirect(readable, mf.transform,
                            planePoint, planeNormal, keepSide);
                    }

                    if (!ok)
                    {
                        readable.Clear();
                        readable.name = originalAsset.name + "_CUT_EMPTY";
                    }
                    else
                    {
                        readable.name = originalAsset.name + "_CUT";
                    }

                    PiPDisablerPlugin.DebugLogInfo(
                        $"[MeshSurgery] Cut '{originalAsset.name}': {vertsBefore} → {readable.vertexCount} verts");

                    mf.sharedMesh = readable;
                    cache.Entries.Add(new CutMeshEntry
                    {
                        Filter = mf,
                        OriginalMesh = originalAsset,
                        CutMesh = readable,
                        Applied = true,
                        FilterPath = GetRelativePath(weaponRootTf, mf.transform)
                    });
                }
                catch (Exception ex)
                {
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[MeshSurgery] Failed on '{originalAsset.name}': {ex.Message}");
                }
            }

            cache.Built = true;
            cache.Dirty = false;
            cache.SettingsSignature = BuildCutSettingsSignature();
            _lastCutAttemptFrame = Time.frameCount;
            _lastAttemptEntries = cache.Entries.Count;

            if (cache.Entries.Count == 0)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[MeshSurgery][DEBUG] RebuildCutCache finished with ZERO entries! " +
                    $"targets={targets.Count} os='{os.name}' scopeRoot='{scopeRoot.name}' " +
                    $"activeMode='{activeMode.name}' frame={Time.frameCount}");
            }
            else
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[MeshSurgery][DEBUG] RebuildCutCache OK: {cache.Entries.Count} entries from {targets.Count} targets. frame={Time.frameCount}");
            }
        }

        private static void ReapplyCachedCutMeshes(CutProfileCache cache, Transform scopeRoot)
        {
            var weaponRootTf = FindWeaponTransform(scopeRoot);
            if (weaponRootTf == null)
            {
                cache.Dirty = true;
                return;
            }

            cache.WeaponRoot = weaponRootTf.gameObject;
            if (!TryRebindEntries(cache, weaponRootTf))
            {
                cache.Dirty = true;
                return;
            }

            DisableLightEffectMeshesForScope(scopeRoot);
            DisableWeaponSphereObjects(scopeRoot);

            foreach (var entry in cache.Entries)
            {
                if (entry == null || entry.Filter == null || entry.CutMesh == null)
                    continue;

                entry.Filter.sharedMesh = entry.CutMesh;
                entry.Applied = true;
            }
        }

        private static void RestoreOriginalMeshes(CutProfileCache cache)
        {
            if (cache == null) return;

            foreach (var entry in cache.Entries)
            {
                if (entry == null || entry.Filter == null)
                    continue;

                if (!entry.Applied)
                    continue;

                if (entry.OriginalMesh != null)
                    entry.Filter.sharedMesh = entry.OriginalMesh;

                entry.Applied = false;
            }
        }

        private static void DestroyCutMeshes(CutProfileCache cache)
        {
            if (cache == null) return;

            foreach (var entry in cache.Entries)
            {
                if (entry?.CutMesh != null)
                {
                    try { UnityEngine.Object.Destroy(entry.CutMesh); }
                    catch { }
                }
            }
        }

        private static void DestroyCurrentWeaponCache()
        {
            foreach (var weaponCache in _raidCaches.Values)
            {
                if (weaponCache == null) continue;
                foreach (var profile in weaponCache.Profiles.Values)
                {
                    RestoreOriginalMeshes(profile);
                    DestroyCutMeshes(profile);
                    profile.Entries.Clear();
                }
                weaponCache.Profiles.Clear();
            }
            _raidCaches.Clear();
            _currentWeaponCache = null;
            _currentWeaponId = null;
        }

        private static void BindInventoryEvents(Player player)
        {
            var inventory = player != null ? player.InventoryController : null;
            if (inventory == null || ReferenceEquals(_inventoryEventSource, inventory))
                return;

            UnbindInventoryEvents();

            try
            {
                var type = inventory.GetType();
                var addEvent = type.GetEvent("AddItemEvent");
                var removeEvent = type.GetEvent("RemoveItemEvent");
                if (addEvent == null || removeEvent == null)
                    return;

                _addItemHandler = Delegate.CreateDelegate(addEvent.EventHandlerType, null,
                    typeof(MeshSurgeryManager).GetMethod(nameof(OnItemAdded), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
                _removeItemHandler = Delegate.CreateDelegate(removeEvent.EventHandlerType, null,
                    typeof(MeshSurgeryManager).GetMethod(nameof(OnItemRemoved), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));

                addEvent.AddEventHandler(inventory, _addItemHandler);
                removeEvent.AddEventHandler(inventory, _removeItemHandler);
                _inventoryEventSource = inventory;
            }
            catch (Exception ex)
            {
                PiPDisablerPlugin.DebugLogInfo($"[MeshSurgery] Failed to bind inventory events: {ex.Message}");
            }
        }

        private static void UnbindInventoryEvents()
        {
            if (_inventoryEventSource == null)
                return;

            try
            {
                var type = _inventoryEventSource.GetType();
                var addEvent = type.GetEvent("AddItemEvent");
                var removeEvent = type.GetEvent("RemoveItemEvent");

                if (addEvent != null && _addItemHandler != null)
                    addEvent.RemoveEventHandler(_inventoryEventSource, _addItemHandler);
                if (removeEvent != null && _removeItemHandler != null)
                    removeEvent.RemoveEventHandler(_inventoryEventSource, _removeItemHandler);
            }
            catch { }

            _addItemHandler = null;
            _removeItemHandler = null;
            _inventoryEventSource = null;
        }

        private static void OnItemAdded(GEventArgs2 args)
        {
            if (args == null || args.Status != CommandStatus.Succeed)
                return;

            MarkCacheDirtyIfMeaningful(args.Item, args.To);
        }

        private static void OnItemRemoved(GEventArgs3 args)
        {
            if (args == null || args.Status != CommandStatus.Succeed)
                return;

            MarkCacheDirtyIfMeaningful(args.Item, args.From);
        }

        private static void MarkCacheDirtyIfMeaningful(Item item, ItemAddress address)
        {
            if (address == null)
                return;

            if (IsIgnoredWeaponChange(item, address))
                return;

            var owner = FindOwningWeapon(address, item);
            if (owner == null)
                return;

            string ownerId = !string.IsNullOrEmpty(owner.Id) ? owner.Id : owner.TemplateId;
            if (!_raidCaches.TryGetValue(ownerId, out var weaponCache) || weaponCache == null)
                return;

            foreach (var profile in weaponCache.Profiles.Values)
            {
                if (profile != null)
                    profile.Dirty = true;
            }
        }

        private static Weapon FindOwningWeapon(ItemAddress address, Item changedItem)
        {
            if (changedItem is Weapon changedWeapon)
                return changedWeapon;

            var parents = address.GetAllParentItems(false);
            if (parents == null)
                return null;

            foreach (var parent in parents)
            {
                if (parent is Weapon weapon)
                    return weapon;
            }

            return null;
        }

        private static Transform ResolveActiveMode(OpticSight os, Transform scopeRoot)
        {
            Transform activeMode;
            if (os.transform.name != null &&
                (os.transform.name.StartsWith("mode_", StringComparison.OrdinalIgnoreCase)
                 || os.transform.name.Equals("mode", StringComparison.OrdinalIgnoreCase)))
                activeMode = os.transform;
            else
                activeMode = ScopeHierarchy.FindBestMode(scopeRoot);

            if (!activeMode) activeMode = os.transform;
            return activeMode;
        }

        private static bool TryRebindEntries(CutProfileCache cache, Transform weaponRoot)
        {
            foreach (var entry in cache.Entries)
            {
                if (entry == null || entry.CutMesh == null || string.IsNullOrEmpty(entry.FilterPath))
                    return false;

                var tf = FindRelativeTransform(weaponRoot, entry.FilterPath);
                if (tf == null)
                    return false;

                var mf = tf.GetComponent<MeshFilter>();
                if (mf == null)
                    return false;

                entry.Filter = mf;
                if (entry.OriginalMesh == null)
                    entry.OriginalMesh = mf.sharedMesh;
            }

            return true;
        }

        private static string BuildProfileKey(Transform weaponRoot, Transform scopeRoot, Transform activeMode, string settingsSignature)
        {
            return string.Join("|", new[]
            {
                GetRelativePath(weaponRoot, scopeRoot),
                GetRelativePath(weaponRoot, activeMode),
                settingsSignature ?? string.Empty
            });
        }

        private static string GetRelativePath(Transform root, Transform child)
        {
            if (child == null) return string.Empty;
            if (root == null) return child.name ?? "unnamed";

            var nodes = new List<string>();
            for (var t = child; t != null; t = t.parent)
            {
                nodes.Add(t.name ?? "unnamed");
                if (t == root)
                    break;
            }

            nodes.Reverse();
            return string.Join("/", nodes.ToArray());
        }

        private static Transform FindRelativeTransform(Transform root, string relativePath)
        {
            if (root == null || string.IsNullOrEmpty(relativePath)) return null;
            if (relativePath == root.name) return root;

            var segments = relativePath.Split('/');
            int start = segments.Length > 0 && string.Equals(segments[0], root.name, StringComparison.Ordinal) ? 1 : 0;
            Transform current = root;
            for (int i = start; i < segments.Length; i++)
            {
                if (string.IsNullOrEmpty(segments[i])) continue;
                current = current.Find(segments[i]);
                if (current == null) return null;
            }

            return current;
        }

        private static bool IsIgnoredWeaponChange(Item item, ItemAddress address)
        {
            if (address?.Container is Slot slot &&
                slot.ID == EWeaponModType.mod_magazine.ToString())
                return true;

            if (item is MagazineItemClass)
                return true;

            if (item is AmmoItemClass)
                return true;

            return false;
        }

        private static string BuildCutSettingsSignature()
        {
            return string.Join("|", new[]
            {
                "Cylinder",
                PerScopeMeshSurgerySettings.GetPlaneOffsetMeters().ToString("F4"),
                PerScopeMeshSurgerySettings.GetPlane1OffsetMeters().ToString("F4"),
                PerScopeMeshSurgerySettings.GetPlane1Radius().ToString("F4"),
                PerScopeMeshSurgerySettings.GetCutStartOffset().ToString("F4"),
                PerScopeMeshSurgerySettings.GetCutLength().ToString("F4"),
                PerScopeMeshSurgerySettings.GetNearPreserveDepth().ToString("F4"),
                PerScopeMeshSurgerySettings.GetPlane2PositionNormalized(PerScopeMeshSurgerySettings.GetCutLength()).ToString("F4"),
                PerScopeMeshSurgerySettings.GetPlane2Radius().ToString("F4"),
                PerScopeMeshSurgerySettings.GetPlane3Position().ToString("F4"),
                PerScopeMeshSurgerySettings.GetPlane3Radius().ToString("F4"),
                PerScopeMeshSurgerySettings.GetPlane4Position().ToString("F4"),
                PerScopeMeshSurgerySettings.GetPlane4Radius().ToString("F4"),
            });
        }

        private static Transform FindWeaponTransform(Transform scopeRoot)
        {
            for (var p = scopeRoot; p != null; p = p.parent)
            {
                if (p.name != null && p.name.Equals("weapon", StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }

        private static void DisableWeaponSphereObjects(Transform scopeRoot)
        {
            var weaponRoot = FindWeaponTransform(scopeRoot);
            if (weaponRoot == null) return;

            foreach (var t in weaponRoot.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.gameObject == null) continue;
                if (!t.name.Equals("Sphere", StringComparison.OrdinalIgnoreCase)) continue;

                var go = t.gameObject;
                if (!_disabledWeaponSpheres.TryGetValue(go, out var st))
                {
                    st = new SphereState { WasActiveSelf = go.activeSelf, DisabledByUs = false };
                    _disabledWeaponSpheres[go] = st;
                }

                if (go.activeSelf)
                {
                    go.SetActive(false);
                    st.DisabledByUs = true;
                }
            }
        }

        private static void RestoreWeaponSphereObjectsUnderRoot(Transform weaponRoot)
        {
            if (weaponRoot == null || _disabledWeaponSpheres.Count == 0) return;

            var keys = _disabledWeaponSpheres.Keys.ToArray();
            foreach (var go in keys)
            {
                if (go == null)
                {
                    _disabledWeaponSpheres.Remove(go);
                    continue;
                }

                if (go.transform == null || !go.transform.IsChildOf(weaponRoot))
                    continue;

                if (_disabledWeaponSpheres.TryGetValue(go, out var st) && st != null && st.DisabledByUs)
                {
                    try
                    {
                        if (st.WasActiveSelf && !go.activeSelf)
                            go.SetActive(true);
                    }
                    catch { }
                }

                _disabledWeaponSpheres.Remove(go);
            }
        }

        private static void DisableLightEffectMeshesForScope(Transform scopeRoot)
        {
            var lightFxTargets = ScopeHierarchy.FindLightEffectMeshFilters(scopeRoot);
            if (lightFxTargets.Count == 0) return;

            foreach (var mf in lightFxTargets)
            {
                if (mf == null || mf.gameObject == null) continue;

                var go = mf.gameObject;
                if (!_disabledLightFx.TryGetValue(go, out var st))
                {
                    st = new LightFxState { WasActiveSelf = go.activeSelf, DisabledByUs = false };
                    _disabledLightFx[go] = st;
                }

                if (go.activeSelf)
                {
                    go.SetActive(false);
                    st.DisabledByUs = true;
                }
            }
        }

        private static void RestoreLightEffectMeshesUnderRoot(Transform searchRoot)
        {
            if (searchRoot == null || _disabledLightFx.Count == 0) return;

            var keys = _disabledLightFx.Keys.ToArray();
            foreach (var go in keys)
            {
                if (go == null)
                {
                    _disabledLightFx.Remove(go);
                    continue;
                }

                if (go.transform == null || !go.transform.IsChildOf(searchRoot))
                    continue;

                if (_disabledLightFx.TryGetValue(go, out var st) && st != null && st.DisabledByUs)
                {
                    try
                    {
                        if (st.WasActiveSelf && !go.activeSelf)
                            go.SetActive(true);
                    }
                    catch { }
                }

                _disabledLightFx.Remove(go);
            }
        }

        private static bool DecideKeepPositive(Vector3 planePoint, Vector3 planeNormal, Vector3 camPos)
        {
            float d = Vector3.Dot(planeNormal, camPos - planePoint);
            bool cameraIsPositive = d >= 0f;
            return cameraIsPositive;
        }
    }

    internal static class ScopeHierarchy
    {
        /// <summary>
        /// Find the scope root transform by walking up from any child transform.
        /// Strategy:
        ///   1. First pass: find a parent with mode_* children (multi-mode scopes like Valday)
        ///   2. Fallback: find a parent that has a 'backLens' child (single-mode scopes like Bravo 4x30)
        ///   3. Fallback: find a parent whose name contains 'scope' (broad catch, includes mod_scope)
        /// </summary>
        public static Transform FindScopeRoot(Transform any)
        {
            // Pass 1: mode-based (most specific — handles multi-mode scopes)
            for (var t = any; t != null; t = t.parent)
            {
                if (HasModeChild(t)) return t;
            }

            // Pass 2: backLens-based (handles single-mode scopes with direct backLens child)
            for (var t = any; t != null; t = t.parent)
            {
                if (HasDirectChild(t, "backLens") || HasDirectChild(t, "backlens"))
                {
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[ScopeHierarchy] FindScopeRoot fallback (backLens child): '{t.name}'");
                    return t;
                }
            }

            // Pass 3: name-based (last resort — find something that looks like a scope)
            for (var t = any; t != null; t = t.parent)
            {
                if (t.name != null)
                {
                    var lo = t.name.ToLowerInvariant();
                    if (lo.Contains("scope"))
                    {
                        PiPDisablerPlugin.DebugLogInfo(
                            $"[ScopeHierarchy] FindScopeRoot fallback (name match): '{t.name}'");
                        return t;
                    }
                }
            }

            PiPDisablerPlugin.DebugLogInfo(
                $"[ScopeHierarchy] FindScopeRoot FAILED for '{any?.name}' — no scope root found");
            return null;
        }

        private static bool HasDirectChild(Transform t, string childName)
        {
            if (t == null) return false;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c != null && c.name != null &&
                    c.name.Equals(childName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool HasModeChild(Transform t)
        {
            if (t == null) return false;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c == null || c.name == null) continue;
                // Match "mode_000", "mode_001" etc AND plain "mode"
                if (c.name.StartsWith("mode_", StringComparison.OrdinalIgnoreCase)
                    || c.name.Equals("mode", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool LooksLikeScopeRootByName(Transform t)
        {
            if (t == null || string.IsNullOrEmpty(t.name)) return false;

            var lo = t.name.ToLowerInvariant();
            return lo.Contains("scope")
                || lo.Contains("optic")
                || lo.Contains("sight")
                || lo.Contains("collimator");
        }

        private static bool IsLikelyScopeRootForExclusion(Transform t)
        {
            // Many non-optic tactical devices (DBAL/flashlights/lasers) also expose
            // mode_* children. For sibling-scope exclusion, require additional optic
            // signals so those tactical attachments stay cuttable.
            if (!HasModeChild(t)) return false;

            if (LooksLikeScopeRootByName(t)) return true;

            if (HasDirectChild(t, "backLens") || HasDirectChild(t, "backlens"))
                return true;

            if (FindDeepChild(t, "backLens") != null || FindDeepChild(t, "backlens") != null)
                return true;

            if (FindDeepChild(t, "optic_camera") != null)
                return true;

            // Parent container hint (e.g. mod_scope_XXX/<scopeRoot>)
            var parentName = t.parent != null ? (t.parent.name ?? string.Empty).ToLowerInvariant() : string.Empty;
            return parentName.Contains("scope") || parentName.Contains("optic");
        }

        private static bool IsModeNode(string name)
        {
            if (name == null) return false;
            return name.StartsWith("mode_", StringComparison.OrdinalIgnoreCase)
                || name.Equals("mode", StringComparison.OrdinalIgnoreCase);
        }

        public static Transform FindBestMode(Transform scopeRoot)
        {
            if (scopeRoot == null) return null;

            Transform firstActive = null;
            Transform withBackLens = null;

            for (int i = 0; i < scopeRoot.childCount; i++)
            {
                var c = scopeRoot.GetChild(i);
                if (c == null || !IsModeNode(c.name)) continue;

                if (c.gameObject.activeInHierarchy && firstActive == null)
                    firstActive = c;

                if (c.gameObject.activeInHierarchy)
                {
                    var bl = FindDeepChild(c, "backLens");
                    if (bl != null) { withBackLens = c; break; }
                }
            }

            if (withBackLens != null) return withBackLens;
            if (firstActive != null) return firstActive;

            for (int i = 0; i < scopeRoot.childCount; i++)
            {
                var c = scopeRoot.GetChild(i);
                if (c != null && IsModeNode(c.name))
                    return c;
            }
            return null;
        }

        public static Transform FindDeepChild(Transform root, string nameEquals)
        {
            if (root == null) return null;
            var stack = new Stack<Transform>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                if (t == null) continue;
                if (t.name != null && string.Equals(t.name, nameEquals, StringComparison.OrdinalIgnoreCase))
                    return t;

                for (int i = 0; i < t.childCount; i++)
                    stack.Push(t.GetChild(i));
            }
            return null;
        }

        public static string GetRelativePath(Transform t, Transform root)
        {
            if (t == null) return "null";

            var parts = new List<string>();
            for (var p = t; p != null; p = p.parent)
            {
                parts.Add(p.name ?? "unnamed");
                if (p == root) break;
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private static bool IsLikelyLightEffectMesh(MeshFilter mf, Transform searchRoot)
        {
            if (mf == null || mf.transform == null) return false;

            string goName = (mf.gameObject.name ?? string.Empty).ToLowerInvariant();
            string meshName = (mf.sharedMesh != null ? mf.sharedMesh.name : string.Empty).ToLowerInvariant();

            bool isSphereMesh = goName == "sphere" || meshName == "sphere";
            if (!isSphereMesh) return false;

            // Common EFT flashlight/laser visual emitters are nested under light_* nodes.
            // These are glow helpers and should not be plane-cut, otherwise they can bloom
            // into solid white blobs in the optic image.
            string relPath = GetRelativePath(mf.transform, searchRoot).ToLowerInvariant();
            bool underLightNode = relPath.Contains("/light_") || relPath.EndsWith("/light");
            if (!underLightNode) return false;

            return true;
        }

        private static bool IsTextMeshProOwnedMesh(MeshFilter mf)
        {
            if (mf == null || mf.transform == null) return false;

            for (var t = mf.transform; t != null; t = t.parent)
            {
                var components = t.GetComponents<Component>();
                for (int i = 0; i < components.Length; i++)
                {
                    var component = components[i];
                    if (component == null) continue;

                    var type = component.GetType();
                    string fullName = type.FullName ?? string.Empty;
                    string name = type.Name ?? string.Empty;

                    if (fullName.StartsWith("TMPro.", StringComparison.Ordinal) ||
                        name.StartsWith("TMP_", StringComparison.Ordinal) ||
                        name.StartsWith("TextMeshPro", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool TryGetPlane(OpticSight os, Transform scopeRoot, Transform activeMode,
            out Vector3 planePoint, out Vector3 planeNormal, out Vector3 camPos)
        {
            planePoint = default;
            planeNormal = default;
            camPos = default;

            Transform viewerTf = null;
            try { viewerTf = os != null ? os.ScopeTransform : null; } catch { }

            if (viewerTf != null) camPos = viewerTf.position;
            else { var mc = Helpers.GetMainCamera(); camPos = mc != null ? mc.transform.position : activeMode.position; }

            // Find the best reference transform for the cut plane.
            Transform refTransform = null;

            var backLens = FindDeepChild(activeMode, "backLens");
            if (backLens != null)
            {
                planePoint = backLens.position;
                refTransform = backLens;
            }

            if (refTransform == null)
            {
                try
                {
                    var lr = os != null ? os.LensRenderer : null;
                    if (lr != null)
                    {
                        planePoint = lr.bounds.center;
                        refTransform = lr.transform;
                    }
                }
                catch { }
            }

            if (refTransform == null)
            {
                var lens = scopeRoot.GetComponentsInChildren<Transform>(true)
                    .FirstOrDefault(t =>
                    {
                        if (t == null || t.name == null) return false;
                        var n = t.name.ToLowerInvariant();
                        return n.Contains("lens") || n.Contains("linza") || n.Contains("glass");
                    });

                if (lens != null)
                {
                    planePoint = lens.position;
                    refTransform = lens;
                }
            }

            if (refTransform == null)
            {
                Transform opticCamTf = FindDeepChild(activeMode, "optic_camera");
                if (opticCamTf != null)
                {
                    planePoint = opticCamTf.position + opticCamTf.forward * 0.02f;
                    refTransform = opticCamTf;
                }
            }

            if (refTransform == null)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[ScopeHierarchy][DEBUG] TryGetPlane: ALL fallbacks failed. " +
                    $"os='{(os != null ? os.name : "null")}' activeMode='{activeMode.name}' " +
                    $"scopeRoot='{scopeRoot.name}' frame={Time.frameCount}. " +
                    $"Checked: backLens=null, LensRenderer=null, lens/linza/glass=null, optic_camera=null");
                return false;
            }

            // Determine the plane normal based on config.
            planeNormal = GetConfiguredNormal(refTransform);

            PiPDisablerPlugin.DebugLogInfo(
                $"[ScopeHierarchy][DEBUG] TryGetPlane OK: ref='{refTransform.name}', " +
                $"planePoint={planePoint:F4}, normal={planeNormal:F3}, " +
                $"frame={Time.frameCount}");

            return true;
        }

        private static Vector3 GetConfiguredNormal(Transform refTransform)
        {
            return -refTransform.up;
        }

        public static List<MeshFilter> FindLightEffectMeshFilters(Transform scopeRoot)
        {
            var result = new List<MeshFilter>(8);
            if (scopeRoot == null) return result;

            // Keep search-root expansion aligned with FindTargetMeshFilters.
            Transform searchRoot = scopeRoot;
            for (var p = scopeRoot.parent; p != null; p = p.parent)
            {
                var pName = p.name ?? "";
                var plo = pName.ToLowerInvariant();
                if (plo.Contains("weapon") || plo.Contains("anim"))
                    break;
                if (plo.Contains("scope") || plo.Contains("mod_") || plo.Contains("optic") || plo.Contains("mount") || plo.Contains("receiver") || plo.Contains("reciever"))
                {
                    searchRoot = p;
                    continue;
                }
                break;
            }

            if (PerScopeMeshSurgerySettings.GetExpandSearchToWeaponRoot())
            {
                for (var p = searchRoot.parent; p != null; p = p.parent)
                {
                    if ((p.name ?? "").StartsWith("Weapon_root", StringComparison.OrdinalIgnoreCase))
                    {
                        searchRoot = p;
                        break;
                    }
                }
            }

            foreach (var mf in searchRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf || !mf.sharedMesh) continue;
                if (IsLikelyLightEffectMesh(mf, searchRoot))
                    result.Add(mf);
            }

            return result;
        }

        public static List<MeshFilter> FindTargetMeshFilters(Transform scopeRoot, Transform activeMode)
        {
            if (scopeRoot == null) return new List<MeshFilter>();

            Transform searchRoot = null;
            for (var p = scopeRoot; p != null; p = p.parent)
            {
                if (p.name != null && p.name.Equals("weapon", StringComparison.OrdinalIgnoreCase))
                {
                    searchRoot = p;
                    break;
                }
            }

            if (searchRoot == null)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[MeshSurgery][DebugCandidates] FindTargetMeshFilters could not find weapon root for '{scopeRoot.name}'");
                return new List<MeshFilter>();
            }

            var result = new List<MeshFilter>(64);
            int inspected = 0;

            foreach (var mf in searchRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf || !mf.sharedMesh) continue;
                inspected++;

                string relSearchPath = null;

                if (IsVolatileWeaponPath(relSearchPath))
                {
                    continue;
                }

                if (ContainsPatronToken(relSearchPath) || ContainsPatronToken(mf.gameObject.name) || ContainsPatronToken(mf.sharedMesh.name))
                    continue;

                if (IsTextMeshProOwnedMesh(mf))
                    continue;

                var renderer = mf.GetComponent<Renderer>();
                if (renderer != null && LensTransparency.IsLensSurfaceRenderer(renderer))
                    continue;

                result.Add(mf);
            }

            PiPDisablerPlugin.DebugLogInfo(
                $"[ScopeHierarchy] FindTargets from '{searchRoot.name}': " +
                $"{result.Count} targets");

            return result;
        }

        private static bool IsVolatileWeaponPath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return false;

            var path = relativePath.ToLowerInvariant();
            return path.Contains("patron_in_weapon")
                || path.Contains("mod_magazine")
                || path.Contains("mod_magazine_new");
        }

        private static bool ContainsPatronToken(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            return value.IndexOf("patron", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>

    }
}
