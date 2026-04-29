using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirage;
using NuclearOption.Networking;
using NuclearOption.Networking.Lobbies;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.UI;

namespace ThirdFaction;

// ==========================================================
//  ThirdFaction v2.0 — sceneId approach
//  Inspired by MinecrackTyler's NOFactionMod.
//  Creates FactionHQ as a proper Mirage scene object with
//  a unique sceneId, so SyncVar/SyncList/RPC all work
//  natively. Eliminates ~15 patches from v1.
// ==========================================================

[BepInPlugin("com.noms.thirdfaction", "ThirdFaction", "1.5.2")]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    internal static Faction PmcFaction;
    internal static FactionHQ PmcHQ;
    internal static HashSet<int> PmcUnitInstanceIds = new HashSet<int>();
    internal static HashSet<PersistentID> PmcUnitPersistentIds = new HashSet<PersistentID>();
    // Cached so SetupPmcHQ can spawn HQs created AFTER SpawnSceneObjects ran.
    // Without this, scene reloads / late HqFromName lookups produce a PMC HQ
    // whose NetworkIdentity is never registered with the server, IsServer
    // stays false, and any [Server]-guarded call (e.g. FactionHQ.AddAirbase
    // when an airbase is captured for PMC) throws MethodInvocationException.
    internal static ServerObjectManager _cachedSom;

    static ConfigEntry<bool> cfgEnabled;
    static ConfigEntry<string> cfgFactionName;
    static ConfigEntry<string> cfgFactionTag;
    static ConfigEntry<float> cfgStartingBalance;
    static ConfigEntry<string> cfgFactionColor;
    static ConfigEntry<string> cfgLogoPath;

    void Awake()
    {
        Log = Logger;

        cfgEnabled = Config.Bind("General", "Enabled", true, "Enable the third faction");
        cfgFactionName = Config.Bind("General", "FactionName", "PMC", "Name of the third faction");
        cfgFactionTag = Config.Bind("General", "FactionTag", "PMC", "Short tag for MFD display");
        cfgStartingBalance = Config.Bind("General", "StartingBalance", 5000f, "Starting funds for the faction");
        cfgFactionColor = Config.Bind("General", "FactionColor", "0.2,0.8,0.2,1", "RGBA color (comma-separated floats)");
        cfgLogoPath = Config.Bind("General", "LogoPath", "",
            "Path to a PNG logo file for the faction (e.g. BepInEx/plugins/pmc_logo.png). Leave empty to auto-generate from faction color.");

        if (!cfgEnabled.Value) return;

        PmcFaction = CreateFaction();
        var harmony = new Harmony("com.noms.thirdfaction");
        harmony.PatchAll();
        PatchEditorMethods(harmony);
        PatchSpawnSceneObjects(harmony);
        Log.LogInfo($"ThirdFaction v2.0 loaded (sceneId approach, color: {PmcFaction.color})");
    }

    // ==========================================================
    //  Manual patch: ServerObjectManager.SpawnSceneObjects
    //  Creates PMC HQ right before Mirage scans scene objects,
    //  so it gets a real NetId and full network support.
    // ==========================================================
    static void PatchSpawnSceneObjects(Harmony harmony)
    {
        try
        {
            var method = AccessTools.Method(typeof(ServerObjectManager), "SpawnSceneObjects");
            if (method != null)
            {
                harmony.Patch(method,
                    postfix: new HarmonyMethod(typeof(Patch_SpawnSceneObjects), "Postfix"));
                Log.LogInfo("Patched ServerObjectManager.SpawnSceneObjects");
            }
            else
                Log.LogWarning("SpawnSceneObjects not found — using fallback hooks");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"SpawnSceneObjects patch: {ex.Message}");
        }
    }

    // ==========================================================
    //  Faction creation (same as v1)
    // ==========================================================
    static Faction CreateFaction()
    {
        var f = ScriptableObject.CreateInstance<Faction>();
        f.name = cfgFactionName.Value;
        f.factionName = cfgFactionName.Value;
        f.factionTag = cfgFactionTag.Value;
        f.factionExtendedName = cfgFactionName.Value;
        f.color = ParseColor(cfgFactionColor.Value);
        f.selectedColor = Color.Lerp(f.color, Color.white, 0.3f);
        f.factionColorLogo = LoadOrGenerateLogo(f.color);
        f.factionHeaderSprite = f.factionColorLogo;

        AccessTools.Field(typeof(Faction), "convoyGroups")
            ?.SetValue(f, Activator.CreateInstance(
                AccessTools.Field(typeof(Faction), "convoyGroups").FieldType));

        return f;
    }

    static Sprite LoadOrGenerateLogo(Color factionColor)
    {
        string[] candidates = new[]
        {
            cfgLogoPath?.Value,
            Path.Combine(Paths.PluginPath, "pmc_logo.png"),
            Path.Combine(Paths.GameRootPath, "BepInEx", "plugins", "pmc_logo.png"),
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            try
            {
                string path = candidate;
                if (!Path.IsPathRooted(path))
                    path = Path.Combine(Paths.GameRootPath, path);

                if (File.Exists(path))
                {
                    var data = File.ReadAllBytes(path);
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (ImageConversion.LoadImage(tex, data))
                    {
                        var sprite = Sprite.Create(tex,
                            new Rect(0, 0, tex.width, tex.height),
                            new Vector2(0.5f, 0.5f), 100f);
                        Log.LogInfo($"Loaded logo: {path} ({tex.width}x{tex.height})");
                        return sprite;
                    }
                }
            }
            catch { }
        }

        int size = 128;
        var fallbackTex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float center = size / 2f;
        float radius = center - 2f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center, dy = y - center;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                if (dist <= radius)
                    fallbackTex.SetPixel(x, y, factionColor);
                else if (dist <= radius + 1.5f)
                    fallbackTex.SetPixel(x, y, Color.Lerp(factionColor, Color.clear, (dist - radius) / 1.5f));
                else
                    fallbackTex.SetPixel(x, y, Color.clear);
            }
        }
        fallbackTex.Apply();
        Log.LogInfo("Generated fallback logo (colored circle)");
        return Sprite.Create(fallbackTex,
            new Rect(0, 0, size, size),
            new Vector2(0.5f, 0.5f), 100f);
    }

    static Color ParseColor(string s)
    {
        try
        {
            var p = s.Split(',');
            if (p.Length >= 3)
                return new Color(
                    float.Parse(p[0].Trim()),
                    float.Parse(p[1].Trim()),
                    float.Parse(p[2].Trim()),
                    p.Length >= 4 ? float.Parse(p[3].Trim()) : 1f);
        }
        catch { }
        return Color.yellow;
    }

    // ==========================================================
    //  HQ Creation: sceneId approach
    //  Credit: MinecrackTyler (NOFactionMod) for the sceneId idea.
    //  Creates FactionHQ under SceneEssentials/FactionHQ with a
    //  unique _sceneId. SpawnSceneObjects() picks it up and gives
    //  it a real NetId, so all SyncVar/SyncList/RPC work natively.
    // ==========================================================
    internal static FactionHQ SetupPmcHQ()
    {
        if (PmcHQ != null && ((UnityEngine.Object)PmcHQ) != null) return PmcHQ;
        PmcHQ = null;
        PmcUnitInstanceIds.Clear();
        PmcUnitPersistentIds.Clear();
        if (PmcFaction == null) return null;

        try
        {
            // Parent under the same transform as BDF/PALA HQs
            var target = GameObject.Find("SceneEssentials/FactionHQ");
            var go = new GameObject($"FactionHQ_{PmcFaction.factionName}");
            if (target != null)
                go.transform.SetParent(target.transform);

            // NetworkIdentity with unique sceneId
            var nwid = go.AddComponent<NetworkIdentity>();
            var sceneIdField = AccessTools.Field(typeof(NetworkIdentity), "_sceneId");
            var newSceneId = GenerateUniqueSceneID();
            sceneIdField?.SetValue(nwid, newSceneId);

            // FactionHQ component
            FactionHQ hq;
            try { hq = go.AddComponent<FactionHQ>(); }
            catch (Exception ex)
            {
                Log.LogWarning($"FactionHQ AddComponent error (expected): {ex.Message}");
                hq = go.GetComponent<FactionHQ>();
            }
            if (hq == null)
            {
                Log.LogError("Failed to create FactionHQ component");
                UnityEngine.Object.Destroy(go);
                return null;
            }

            // MissionStatsTracker (prevents NullRef in Gun.SpawnBullet)
            var mst = go.AddComponent<MissionStatsTracker>();
            hq.faction = PmcFaction;
            hq.missionStatsTracker = mst;
            mst.hq = hq;

            // Set SyncSettings so Mirage syncs properly
            try
            {
                var ss = new SyncSettings();
                ss.From = SyncFrom.Server;
                ss.To = SyncTo.Owner | SyncTo.ObserversOnly | SyncTo.OwnerAndObservers;
                ss.Timing = SyncTiming.Variable;
                ss.Interval = 0.1f;

                var ssField = AccessTools.Field(typeof(NetworkBehaviour), "_syncSettings")
                    ?? AccessTools.Field(typeof(NetworkBehaviour), "syncSettings");
                if (ssField != null)
                {
                    ssField.SetValue(hq, ss);
                    ssField.SetValue(mst, ss);
                    Log.LogInfo("Set SyncSettings on PMC HQ and MissionStatsTracker");
                }
            }
            catch (Exception ex) { Log.LogWarning($"SyncSettings: {ex.Message}"); }

            // Register faction (NOT in HQLookup — see Patch_RegisterFaction)
            if (!FactionRegistry.factions.Contains(PmcFaction))
                FactionRegistry.factions.Add(PmcFaction);
            FactionRegistry.factionLookup[PmcFaction.factionName] = PmcFaction;

            if (MissionManager.CurrentMission != null)
                MissionManager.CurrentMission.EnsureFactionExists(PmcFaction, out _);

            PmcHQ = hq;
            Log.LogInfo($"PMC HQ created with sceneId={newSceneId} (parent: {target?.name ?? "none"}, factions={FactionRegistry.factions.Count})");

            // Spawn immediately if SpawnSceneObjects has already run for this scene.
            // The Patch_SpawnSceneObjects Postfix only fires once per scene load, so
            // any HQ created later (HqFromName lookups during mission load, scene
            // reloads) needs to be registered here or its NetworkIdentity stays
            // unregistered and IsServer returns false.
            TrySpawnPmcHQ();

            return hq;
        }
        catch (Exception ex)
        {
            Log.LogError($"SetupPmcHQ failed: {ex}");
            return null;
        }
    }

    internal static void TrySpawnPmcHQ()
    {
        if (PmcHQ == null) return;
        if (_cachedSom == null) return;
        try
        {
            var nwid = PmcHQ.GetComponent<NetworkIdentity>();
            if (nwid == null) return;
            if (nwid.NetId != 0) return; // already spawned
            _cachedSom.Spawn(nwid.gameObject);
            Log.LogInfo($"PMC HQ spawned via cached SOM (NetId={nwid.NetId})");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"PMC HQ spawn-on-creation failed (non-fatal): {ex.Message}");
        }
    }

    static ulong GenerateUniqueSceneID()
    {
        var field = AccessTools.Field(typeof(NetworkIdentity), "_sceneId");
        var existing = new HashSet<ulong>();
        if (field != null)
        {
            foreach (var ni in Resources.FindObjectsOfTypeAll<NetworkIdentity>())
            {
                var id = (ulong)field.GetValue(ni);
                if (id != 0) existing.Add(id);
            }
        }

        var rng = new System.Random();
        ulong newId;
        do
        {
            byte[] buf = new byte[8];
            rng.NextBytes(buf);
            newId = BitConverter.ToUInt64(buf, 0);
        } while (newId == 0 || existing.Contains(newId));

        return newId;
    }

    // ==========================================================
    //  Editor patches (manual — types in MissionEditorScripts)
    // ==========================================================
    static void PatchEditorMethods(Harmony harmony)
    {
        var asm = typeof(Unit).Assembly;

        try
        {
            var editorCursorType = asm.GetType("NuclearOption.MissionEditorScripts.EditorCursor");
            if (editorCursorType != null)
            {
                var method = AccessTools.Method(editorCursorType, "SetUnitColor");
                if (method != null)
                {
                    harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(Patch_SetUnitColor), "Prefix"));
                    Log.LogInfo("Patched EditorCursor.SetUnitColor");
                }
            }
        }
        catch (Exception ex) { Log.LogWarning($"EditorCursor patch: {ex.Message}"); }

        try
        {
            var unitPanelType = asm.GetType("NuclearOption.MissionEditorScripts.UnitPanel");
            if (unitPanelType != null)
            {
                var method = AccessTools.Method(unitPanelType, "SetUnitFaction");
                if (method != null)
                {
                    harmony.Patch(method,
                        postfix: new HarmonyMethod(typeof(Patch_SetUnitFaction), "Postfix"));
                    Log.LogInfo("Patched UnitPanel.SetUnitFaction");
                }
            }
        }
        catch (Exception ex) { Log.LogWarning($"UnitPanel patch: {ex.Message}"); }
    }

    // EditorCursor.SetUnitColor — if faction is null but unit is PMC, use PmcFaction
    static class Patch_SetUnitColor
    {
        static void Prefix(Unit unit, ref Faction faction)
        {
            if (faction != null || unit == null || PmcFaction == null) return;
            try
            {
                var saved = unit.SavedUnit;
                if (saved != null && saved.faction == PmcFaction.factionName)
                {
                    faction = PmcFaction;
                    return;
                }
                var hq = unit.NetworkHQ;
                if (hq != null && hq.faction == PmcFaction)
                    faction = PmcFaction;
            }
            catch { }
        }
    }

    // UnitPanel.SetUnitFaction — diagnostic logging
    static class Patch_SetUnitFaction
    {
        static void Postfix(SavedUnit saved, string factionName)
        {
            if (factionName != PmcFaction?.factionName) return;
            var unit = saved?.Unit;
            if (unit == null) return;
            var currentHQ = unit.NetworkHQ;
            Log.LogInfo($"SetUnitFaction(PMC): NetworkHQ = {currentHQ?.faction?.factionName ?? "NULL"}");
        }
    }

    // ==========================================================
    //  SpawnSceneObjects Postfix — create PMC HQ AFTER Mirage
    //  has finished spawning all existing scene objects.
    //  Prefix caused phantom bugs on carriers/destroyers by
    //  injecting a new NetworkIdentity mid-spawn.
    // ==========================================================
    static class Patch_SpawnSceneObjects
    {
        static void Postfix(ServerObjectManager __instance)
        {
            if (PmcFaction == null) return;
            // Cache SOM so any PMC HQ created later (scene reload, late
            // HqFromName lookup) can also be spawned via TrySpawnPmcHQ.
            _cachedSom = __instance;
            SetupPmcHQ();
            TrySpawnPmcHQ();
        }
    }

    // ==========================================================
    //  Patch: Add PMC to every mission's faction list
    // ==========================================================
    [HarmonyPatch(typeof(MissionManager), "SetMission")]
    static class Patch_SetMission
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            if (PmcFaction == null) return;
            var mission = MissionManager.CurrentMission;
            if (mission == null) return;
            if (mission.TryGetFaction(PmcFaction.factionName, out _)) return;

            var mf = new MissionFaction(PmcFaction.factionName);
            mf.startingBalance = cfgStartingBalance.Value;
            mission.factions.Add(mf);
            Log.LogInfo($"Added '{PmcFaction.factionName}' to mission factions");
        }
    }

    // ==========================================================
    //  Patch: FactionFromName — ensure PMC is always findable
    // ==========================================================
    [HarmonyPatch(typeof(FactionRegistry), "FactionFromName")]
    static class Patch_FactionFromName
    {
        [HarmonyPostfix]
        static void Postfix(string factionName, ref Faction __result)
        {
            if (__result == null && factionName == PmcFaction?.factionName)
                __result = PmcFaction;
        }
    }

    // ==========================================================
    //  Patch: HqFromName — intercept PMC lookup.
    //  PMC is kept OUT of HQLookup to avoid breaking JoinMenu
    //  (which only has 2 display slots). This prefix handles
    //  all PMC HQ lookups.
    // ==========================================================
    [HarmonyPatch(typeof(FactionRegistry), "HqFromName")]
    static class Patch_HqFromName
    {
        [HarmonyPrefix]
        static bool Prefix(string factionName, ref FactionHQ __result)
        {
            if (PmcFaction == null || factionName != PmcFaction.factionName)
                return true;

            __result = PmcHQ;
            return false; // skip original to prevent KeyNotFoundException
        }
    }

    // ==========================================================
    //  Patch: RegisterAirbase — prevent duplicate key exception.
    //  Ships with child Airbase components crash during Spawn()
    //  if the key is already registered.
    // ==========================================================
    [HarmonyPatch(typeof(FactionRegistry), "RegisterAirbase")]
    static class Patch_RegisterAirbase
    {
        static Dictionary<string, Airbase> _airbaseLookup;

        [HarmonyPrefix]
        static void Prefix(string key)
        {
            if (string.IsNullOrEmpty(key)) return;

            if (_airbaseLookup == null)
            {
                _airbaseLookup = Traverse.Create(typeof(FactionRegistry))
                    .Field("airbaseLookup")
                    .GetValue<Dictionary<string, Airbase>>();
            }

            if (_airbaseLookup != null && _airbaseLookup.ContainsKey(key))
            {
                Log.LogWarning($"RegisterAirbase: duplicate key '{key}', removing old entry");
                _airbaseLookup.Remove(key);
            }
        }
    }

    // ==========================================================
    //  Patch: RegisterFaction — remove PMC from HQLookup.
    //  When Mirage spawns the PMC HQ, OnStartServer calls
    //  RegisterFaction which adds to HQLookup. We remove it
    //  to keep GetAllHQs() returning only BDF/PALA (prevents
    //  JoinMenu crash — only 2 display slots).
    // ==========================================================
    [HarmonyPatch(typeof(FactionRegistry), "RegisterFaction")]
    static class Patch_RegisterFaction
    {
        [HarmonyPostfix]
        static void Postfix(Faction faction)
        {
            if (PmcFaction == null || faction != PmcFaction) return;

            if (FactionRegistry.HQLookup.ContainsKey(PmcFaction))
            {
                FactionRegistry.HQLookup.Remove(PmcFaction);
                Log.LogInfo("Removed PMC from HQLookup (keeps JoinMenu safe)");
            }
        }
    }

    // ==========================================================
    //  Patch: FactionHQ.Update — guard until fully initialized.
    //  Between SetupPmcHQ() and SpawnSceneObjects completing,
    //  aircraftThreatTracker may be null. Let Update run only
    //  after ServerSetup has initialized everything.
    // ==========================================================
    static FieldInfo _threatField;

    [HarmonyPatch(typeof(FactionHQ), "Update")]
    static class Patch_FactionHQ_Update
    {
        [HarmonyPrefix]
        static bool Prefix(FactionHQ __instance)
        {
            if (__instance.faction != PmcFaction) return true;
            if (_threatField == null)
                _threatField = AccessTools.Field(typeof(FactionHQ), "aircraftThreatTracker");
            return _threatField?.GetValue(__instance) != null;
        }
    }

    // ==========================================================
    //  Patch: FactionHQ.ServerSetup — skip for PMC.
    //  Prevents DeployUnits, DistributeFunds, OnMissionLoad
    //  from running on PMC HQ. These can corrupt Mirage's
    //  spawn pipeline and cause phantom units on BDF/PALA.
    // ==========================================================
    [HarmonyPatch(typeof(FactionHQ), "ServerSetup")]
    static class Patch_ServerSetup
    {
        [HarmonyPrefix]
        static bool Prefix(FactionHQ __instance)
        {
            if (__instance.faction != PmcFaction) return true;
            Log.LogInfo("Skipped ServerSetup for PMC HQ");
            return false;
        }
    }

    // ==========================================================
    //  Patch: FactionHQ.SetupAirbaseFactions — skip for PMC.
    //  Prevents PMC HQ from disrupting BDF/PALA airbase
    //  assignments during its OnStartClient.
    // ==========================================================
    [HarmonyPatch(typeof(FactionHQ), "SetupAirbaseFactions")]
    static class Patch_SetupAirbaseFactions
    {
        [HarmonyPrefix]
        static bool Prefix(FactionHQ __instance)
        {
            if (__instance.faction != PmcFaction) return true;
            Log.LogInfo("Skipped SetupAirbaseFactions for PMC HQ");
            return false;
        }
    }

    // ==========================================================
    //  Patch: FactionHQ.RegisterFactionUnit — redirect to our
    //  HashSets for PMC. SyncList may not work even with sceneId
    //  if ServerSetup is skipped.
    // ==========================================================
    [HarmonyPatch(typeof(FactionHQ), "RegisterFactionUnit")]
    static class Patch_RegisterFactionUnit
    {
        [HarmonyPrefix]
        static bool Prefix(FactionHQ __instance, Unit unit)
        {
            if (__instance.faction != PmcFaction) return true;
            PmcUnitInstanceIds.Add(unit.GetInstanceID());
            PmcUnitPersistentIds.Add(unit.persistentID);
            try
            {
                var map = SceneSingleton<DynamicMap>.i;
                if (map != null) map.AddIcon(unit.persistentID);
            }
            catch { }
            return false;
        }
    }

    // ==========================================================
    //  Patch: FactionHQ.RemoveFactionUnit — redirect for PMC.
    // ==========================================================
    [HarmonyPatch(typeof(FactionHQ), "RemoveFactionUnit")]
    static class Patch_RemoveFactionUnit
    {
        [HarmonyPrefix]
        static bool Prefix(FactionHQ __instance, Unit unit)
        {
            if (__instance.faction != PmcFaction) return true;
            PmcUnitInstanceIds.Remove(unit.GetInstanceID());
            PmcUnitPersistentIds.Remove(unit.persistentID);
            return false;
        }
    }

    // ==========================================================
    //  Patch: MissionStatsTracker.MunitionCost — skip for PMC.
    //  SyncDictionary may not be initialized → NullRef in
    //  Gun.SpawnBullet → unlimited fire rate.
    // ==========================================================
    [HarmonyPatch(typeof(MissionStatsTracker), "MunitionCost")]
    static class Patch_MunitionCost
    {
        [HarmonyPrefix]
        static bool Prefix(Unit unit)
        {
            if (unit == null || PmcHQ == null) return true;
            return !PmcUnitInstanceIds.Contains(unit.GetInstanceID());
        }
    }

    // ==========================================================
    //  Patch: DynamicMap.GenerateMap — include PMC units.
    //  PMC factionUnits SyncList may be empty, so we add
    //  icons from our PmcUnitPersistentIds set.
    // ==========================================================
    [HarmonyPatch(typeof(DynamicMap), "GenerateMap")]
    static class Patch_GenerateMap
    {
        [HarmonyPostfix]
        static void Postfix(DynamicMap __instance, FactionHQ HQ)
        {
            if (PmcHQ == null) return;
            try
            {
                bool isSpectator = (HQ == null);
                if (!isSpectator && HQ != null)
                {
                    foreach (var pid in PmcUnitPersistentIds)
                    {
                        if (HQ.trackingDatabase.ContainsKey(pid))
                            __instance.AddIcon(pid);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"GenerateMap postfix: {ex}");
            }
        }
    }

    // ==========================================================
    //  Patch: SavedUnit.AfterCreate — ensure faction string
    //  is set for PMC units (mission save/reload persistence).
    // ==========================================================
    [HarmonyPatch(typeof(SavedUnit), "AfterCreate")]
    static class Patch_SavedUnit_AfterCreate
    {
        [HarmonyPostfix]
        static void Postfix(SavedUnit __instance, Unit unit)
        {
            if (PmcHQ == null || PmcFaction == null || unit == null) return;

            // Check both NetworkHQ and our tracking set
            var hq = unit.NetworkHQ;
            bool isPmc = (hq != null && hq.faction == PmcFaction)
                || PmcUnitInstanceIds.Contains(unit.GetInstanceID());
            if (!isPmc) return;

            if (string.IsNullOrEmpty(__instance.faction) || __instance.faction != PmcFaction.factionName)
            {
                Log.LogInfo($"AfterCreate fix: {unit.name} faction '{__instance.faction}' → '{PmcFaction.factionName}'");
                __instance.faction = PmcFaction.factionName;
            }
        }
    }

    // ==========================================================
    //  Patch: SpawnFromMissionInEditor — ensure PMC HQ exists
    //  before editor spawns units. Fallback if SpawnSceneObjects
    //  patch didn't fire.
    // ==========================================================
    [HarmonyPatch(typeof(Spawner), "SpawnFromMissionInEditor")]
    static class Patch_SpawnFromMissionInEditor
    {
        [HarmonyPrefix]
        static void Prefix()
        {
            if (PmcFaction == null) return;
            SetupPmcHQ();
        }
    }

    // ==========================================================
    //  Patch: MissionManager.StartMission — ensure PMC HQ exists
    //  before gameplay. Fallback if SpawnSceneObjects patch
    //  didn't fire.
    // ==========================================================
    [HarmonyPatch(typeof(MissionManager), "StartMission")]
    static class Patch_StartMission
    {
        [HarmonyPrefix]
        static void Prefix()
        {
            if (PmcFaction == null) return;
            SetupPmcHQ();
            Log.LogInfo($"StartMission: PmcHQ={(PmcHQ != null ? "ready" : "FAILED")}");
        }
    }

    // ==========================================================
    //  Patch: Leaderboard.OnEnable — expand for PMC
    // ==========================================================
    [HarmonyPatch(typeof(Leaderboard), "OnEnable")]
    static class Patch_Leaderboard_OnEnable
    {
        [HarmonyPrefix]
        static bool Prefix(Leaderboard __instance)
        {
            if (PmcFaction == null || PmcHQ == null) return true;

            try
            {
                var t = Traverse.Create(__instance);
                var displays = (Array)t.Field("factionDisplays").GetValue();
                var lobbyName = t.Field("lobbyName").GetValue<Text>();
                var failPanel = t.Field("missionFailedPanel").GetValue<GameObject>();
                var winPanel = t.Field("missionSucceededPanel").GetValue<GameObject>();
                var resumeBtn = t.Field("resumeButton").GetValue<Button>();
                var rect = t.Field("rectTransform").GetValue<RectTransform>();

                DynamicMap.AllowedToOpen = false;
                __instance.enabled = true;

                // PMC is not in HQLookup, so manually include it
                var hqList = FactionRegistry.GetAllHQs().ToList();
                if (!hqList.Contains(PmcHQ))
                    hqList.Add(PmcHQ);
                var allHQs = hqList.ToArray();

                if (displays.Length < allHQs.Length)
                {
                    displays = ExpandLeaderboardDisplays(displays, allHQs.Length);
                    t.Field("factionDisplays").SetValue(displays);
                }

                int count = Math.Min(allHQs.Length, displays.Length);
                var setFaction = displays.GetType().GetElementType()
                    .GetMethod("SetFaction", BindingFlags.Public | BindingFlags.Instance);

                for (int i = 0; i < count; i++)
                    setFaction.Invoke(displays.GetValue(i), new object[] { allHQs[i] });

                if (GameManager.gameState == GameState.Multiplayer && SteamLobby.instance != null)
                    lobbyName.text = SteamLobby.instance.CurrentLobbyName;

                if (GameManager.gameState == GameState.SinglePlayer)
                {
                    resumeBtn.Select();
                    Time.timeScale = 0f;
                    AudioListener.pause = true;
                    lobbyName.text = MissionManager.CurrentMission.Name;
                }

                if (GameManager.gameResolution == GameResolution.Victory)
                {
                    winPanel.SetActive(true);
                    failPanel.SetActive(false);
                }
                if (GameManager.gameResolution == GameResolution.Defeat)
                {
                    winPanel.SetActive(false);
                    failPanel.SetActive(true);
                }

                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                return false;
            }
            catch (Exception ex)
            {
                Log.LogError($"Leaderboard patch failed, running original: {ex}");
                return true;
            }
        }
    }

    // ==========================================================
    //  Leaderboard display expansion helpers
    // ==========================================================
    static Array ExpandLeaderboardDisplays(Array existing, int targetCount)
    {
        var displayType = typeof(Leaderboard).GetNestedType("LeaderboardFactionDisplay", BindingFlags.NonPublic);
        if (displayType == null) { Log.LogError("LeaderboardFactionDisplay type not found"); return existing; }

        var newArr = Array.CreateInstance(displayType, targetCount);
        Array.Copy(existing, newArr, existing.Length);

        var entriesField = displayType.GetField("entries", BindingFlags.NonPublic | BindingFlags.Instance);
        var titleField = displayType.GetField("titleText", BindingFlags.NonPublic | BindingFlags.Instance);
        var flagField = displayType.GetField("factionFlag", BindingFlags.NonPublic | BindingFlags.Instance);
        var scoreField = displayType.GetField("factionScore", BindingFlags.NonPublic | BindingFlags.Instance);
        var contentField = displayType.GetField("playerListContent", BindingFlags.NonPublic | BindingFlags.Instance);
        var scrollField = displayType.GetField("scrollView", BindingFlags.NonPublic | BindingFlags.Instance);
        var prefabField = displayType.GetField("playerEntryPrefab", BindingFlags.NonPublic | BindingFlags.Instance);

        var src = existing.GetValue(existing.Length - 1);
        var srcScroll = (RectTransform)scrollField.GetValue(src);
        var otherScroll = (RectTransform)scrollField.GetValue(existing.GetValue(0));
        var srcRoot = FindDisplayRoot(srcScroll.transform, otherScroll.transform);

        for (int i = existing.Length; i < targetCount; i++)
        {
            var clonedGo = UnityEngine.Object.Instantiate(srcRoot.gameObject, srcRoot.parent);
            clonedGo.name = $"LeaderboardDisplay_PMC_{i}";
            var clonedRoot = clonedGo.GetComponent<RectTransform>();

            var disp = Activator.CreateInstance(displayType);
            prefabField.SetValue(disp, prefabField.GetValue(src));
            entriesField.SetValue(disp, Activator.CreateInstance(entriesField.FieldType));

            MapField(src, disp, titleField, (RectTransform)srcRoot, clonedRoot);
            MapField(src, disp, flagField, (RectTransform)srcRoot, clonedRoot);
            MapField(src, disp, scoreField, (RectTransform)srcRoot, clonedRoot);
            MapField(src, disp, contentField, (RectTransform)srcRoot, clonedRoot);
            MapField(src, disp, scrollField, (RectTransform)srcRoot, clonedRoot);

            newArr.SetValue(disp, i);
            Log.LogInfo($"Expanded Leaderboard: added display #{i}");
        }
        return newArr;
    }

    // ==========================================================
    //  UI helpers: field mapping, transform paths
    // ==========================================================
    static void MapField(object src, object dst, FieldInfo field, RectTransform srcRoot, RectTransform clonedRoot)
    {
        if (field == null) return;
        var val = field.GetValue(src);
        if (val == null) return;
        if (val is Component comp)
        {
            string path = GetTransformPath(comp.transform, srcRoot.transform);
            if (path == null) return;
            var target = string.IsNullOrEmpty(path) ? clonedRoot.transform : clonedRoot.transform.Find(path);
            if (target != null)
                field.SetValue(dst, target.GetComponent(comp.GetType()));
            else
                Log.LogWarning($"MapField: path '{path}' not found for {field.Name}");
        }
    }

    static string GetTransformPath(Transform child, Transform root)
    {
        if (child == root) return "";
        var parts = new List<string>();
        var cur = child;
        while (cur != null && cur != root)
        {
            parts.Add(cur.name);
            cur = cur.parent;
        }
        if (cur != root) return null;
        parts.Reverse();
        return string.Join("/", parts);
    }

    static Transform FindDisplayRoot(Transform child0, Transform child1)
    {
        var cur = child0;
        while (cur.parent != null)
        {
            foreach (Transform sibling in cur.parent)
            {
                if (sibling != cur && child1.IsChildOf(sibling))
                    return cur;
            }
            cur = cur.parent;
        }
        return child0;
    }
}
