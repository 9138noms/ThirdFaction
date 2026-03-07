using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.Networking.Lobbies;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.UI;

namespace ThirdFaction;

[BepInPlugin("com.noms.thirdfaction", "ThirdFaction", "1.0.0")]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    internal static Faction PmcFaction;
    internal static FactionHQ PmcHQ;
    internal static HashSet<int> PmcUnitInstanceIds = new HashSet<int>();
    internal static HashSet<PersistentID> PmcUnitPersistentIds = new HashSet<PersistentID>();
    internal static List<Airbase> PmcAirbases = new List<Airbase>();

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
        Log.LogInfo($"ThirdFaction loaded (color: {PmcFaction.color})");
    }

    static Faction CreateFaction()
    {
        var f = ScriptableObject.CreateInstance<Faction>();
        f.name = cfgFactionName.Value;
        f.factionName = cfgFactionName.Value;
        f.factionTag = cfgFactionTag.Value;
        f.factionExtendedName = cfgFactionName.Value;
        f.color = ParseColor(cfgFactionColor.Value);
        f.selectedColor = Color.Lerp(f.color, Color.white, 0.3f);

        // Load or generate faction logo
        f.factionColorLogo = LoadOrGenerateLogo(f.color);
        f.factionHeaderSprite = f.factionColorLogo; // JoinMenu uses this for faction flag

        // Initialize private convoyGroups to prevent NRE
        AccessTools.Field(typeof(Faction), "convoyGroups")
            ?.SetValue(f, Activator.CreateInstance(
                AccessTools.Field(typeof(Faction), "convoyGroups").FieldType));

        return f;
    }

    static Sprite LoadOrGenerateLogo(Color factionColor)
    {
        // Try loading logo: config path first, then auto-detect pmc_logo.png
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

        // Generate a simple colored circle as fallback
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
    //  Manual patches for editor namespace types
    //  (NuclearOption.MissionEditorScripts.*)
    // ==========================================================
    static void PatchEditorMethods(Harmony harmony)
    {
        var asm = typeof(Unit).Assembly;

        // Patch EditorCursor.SetUnitColor — force PMC color
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
        catch (Exception ex)
        {
            Log.LogWarning($"EditorCursor patch: {ex.Message}");
        }

        // Patch UnitPanel.SetUnitFaction — diagnostic + force HQ
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
        catch (Exception ex)
        {
            Log.LogWarning($"UnitPanel patch: {ex.Message}");
        }
    }

    // ==========================================================
    //  EditorCursor.SetUnitColor Prefix — if faction is null
    //  but the unit is assigned to PMC, use PmcFaction directly.
    //  This bypasses any SyncVar issues with NetworkHQ.
    // ==========================================================
    static class Patch_SetUnitColor
    {
        static void Prefix(Unit unit, ref Faction faction)
        {
            if (faction != null || unit == null || PmcFaction == null) return;

            try
            {
                // Check saved unit's faction name (most reliable)
                var saved = unit.SavedUnit;
                if (saved != null && saved.faction == PmcFaction.factionName)
                {
                    faction = PmcFaction;
                    return;
                }

                // Fallback: check NetworkHQ
                var hq = unit.NetworkHQ;
                if (hq != null && hq.faction == PmcFaction)
                    faction = PmcFaction;
            }
            catch { }
        }
    }

    // ==========================================================
    //  UnitPanel.SetUnitFaction Postfix — verify NetworkHQ was
    //  set correctly, force-set via reflection if SyncVar rejected
    // ==========================================================
    static class Patch_SetUnitFaction
    {
        static void Postfix(SavedUnit saved, string factionName)
        {
            if (factionName != PmcFaction?.factionName) return;

            var unit = saved?.Unit;
            if (unit == null) return;

            var currentHQ = unit.NetworkHQ;
            Log.LogInfo($"SetUnitFaction(PMC): NetworkHQ = {currentHQ?.faction?.factionName ?? "NULL"}");

            // If NetworkHQ wasn't set (SyncVar issue), force it
            if (currentHQ == null || currentHQ.faction != PmcFaction)
            {
                var hq = EnsurePmcHQ();
                if (hq == null) return;

                try
                {
                    // Try direct property set again
                    unit.NetworkHQ = hq;
                    Log.LogInfo($"Re-set NetworkHQ: {unit.NetworkHQ?.faction?.factionName ?? "still NULL"}");
                }
                catch { }

                // If still null, force the SyncVar backing field
                if (unit.NetworkHQ == null || unit.NetworkHQ.faction != PmcFaction)
                {
                    try
                    {
                        var hqField = AccessTools.Field(typeof(Unit), "HQ");
                        if (hqField != null)
                        {
                            var syncvar = hqField.GetValue(unit);
                            var valueProp = AccessTools.Property(syncvar.GetType(), "Value");
                            if (valueProp != null)
                            {
                                valueProp.SetValue(syncvar, hq);
                                hqField.SetValue(unit, syncvar); // write back (struct)
                                Log.LogInfo($"Force-set HQ field: {unit.NetworkHQ?.faction?.factionName ?? "still NULL"}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.LogWarning($"Force-set HQ failed: {ex.Message}");
                    }
                }
            }
        }
    }

    // ==========================================================
    //  Shared: Lazily create or get the PMC FactionHQ.
    //  Creates a fresh HQ (no cloning), sets faction to PMC,
    //  registers in FactionRegistry. Works in editor and gameplay.
    // ==========================================================
    static bool _creatingHQ;

    internal static FactionHQ EnsurePmcHQ()
    {
        // Unity null check: destroyed objects compare == null via overloaded operator
        if (PmcHQ != null && ((UnityEngine.Object)PmcHQ) != null) return PmcHQ;
        PmcHQ = null; // clear stale reference

        // Scene reload: clear all stale tracking data
        PmcUnitInstanceIds.Clear();
        PmcUnitPersistentIds.Clear();
        PmcAirbases.Clear();

        if (_creatingHQ) return null;

        // Clean stale PMC entry from HQLookup if it somehow exists
        if (FactionRegistry.HQLookup.ContainsKey(PmcFaction))
        {
            FactionRegistry.HQLookup.Remove(PmcFaction);
            Log.LogInfo("Removed stale PMC HQ from HQLookup");
        }

        // No source HQ needed — we create a fresh FactionHQ instead of cloning

        _creatingHQ = true;
        try
        {
            // === PHANTOM PLACEMENT FIX ===
            // Previously, we cloned an existing FactionHQ via Object.Instantiate().
            // This required temporarily zeroing the source HQ's NetworkIdentity
            // to prevent Mirage registry collisions. However, the zero/clone/restore
            // cycle corrupted Mirage's ServerObjectManager internal state, causing
            // ALL subsequent ServerObjectManager.Spawn() calls to produce "phantom"
            // units (visible in editor but vanish on Play). The corruption spread
            // to BDF/PALA ships too, not just PMC.
            //
            // FIX: Create a fresh FactionHQ from scratch using AddComponent.
            // No cloning = no source HQ manipulation = no Mirage corruption.
            // Since PMC HQ is kept OUT of HQLookup and all lifecycle methods
            // are skipped via Harmony patches, we only need the object as a
            // reference for faction identification and trackingDatabase.
            var newGO = new GameObject($"FactionHQ_{PmcFaction.factionName}");
            newGO.AddComponent<Mirage.NetworkIdentity>();

            FactionHQ newHQ;
            try
            {
                newHQ = newGO.AddComponent<FactionHQ>();
            }
            catch (Exception ex2)
            {
                Log.LogWarning($"FactionHQ AddComponent partial error (expected): {ex2.Message}");
                newHQ = newGO.GetComponent<FactionHQ>();
            }

            if (newHQ == null)
            {
                Log.LogError("Failed to create FactionHQ component");
                UnityEngine.Object.Destroy(newGO);
                return null;
            }

            newHQ.faction = PmcFaction;
            newHQ.enabled = false;

            // Copy Server + ServerObjectManager from an existing HQ.
            // IsServer checks: Server != null && ServerObjectManager != null && Server.Active
            try
            {
                var existingHQ = FactionRegistry.GetAllHQs().FirstOrDefault();
                if (existingHQ != null)
                {
                    var existingId = existingHQ.GetComponent<Mirage.NetworkIdentity>();
                    var pmcId = newGO.GetComponent<Mirage.NetworkIdentity>();
                    if (existingId != null && pmcId != null)
                    {
                        var serverProp = AccessTools.Property(typeof(Mirage.NetworkIdentity), "Server");
                        var serverVal = serverProp.GetValue(existingId);
                        if (serverVal != null)
                        {
                            serverProp.SetValue(pmcId, serverVal);
                        }

                        // Also copy ServerObjectManager (required for IsServer)
                        var somField = AccessTools.Field(typeof(Mirage.NetworkIdentity), "ServerObjectManager");
                        if (somField != null)
                        {
                            var somVal = somField.GetValue(existingId);
                            if (somVal != null)
                                somField.SetValue(pmcId, somVal);
                        }

                        // Also copy Client (required for ServerRpc/CmdUpdateTrackingInfo)
                        var clientProp = AccessTools.Property(typeof(Mirage.NetworkIdentity), "Client");
                        if (clientProp != null)
                        {
                            var clientVal = clientProp.GetValue(existingId);
                            if (clientVal != null)
                                clientProp.SetValue(pmcId, clientVal);
                        }

                        Log.LogInfo($"Set PMC NetworkIdentity: IsServer={pmcId.IsServer}, Server={serverVal != null}, SOM={somField?.GetValue(pmcId) != null}");
                    }
                }
            }
            catch (Exception ex2)
            {
                Log.LogWarning($"Failed to set Server on PMC NetworkIdentity: {ex2.Message}");
            }

            // Initialize trackingDatabase if not auto-initialized by field initializer
            try
            {
                if (newHQ.trackingDatabase == null)
                {
                    var field = AccessTools.Field(typeof(FactionHQ), "trackingDatabase");
                    if (field != null)
                    {
                        field.SetValue(newHQ, Activator.CreateInstance(field.FieldType));
                        Log.LogInfo($"Initialized trackingDatabase ({field.FieldType.Name})");
                    }
                }
            }
            catch (Exception ex2)
            {
                Log.LogWarning($"trackingDatabase init: {ex2.Message}");
            }

            // Register PMC in factions and factionLookup but NOT HQLookup.
            // HQLookup is a Dictionary — adding PMC changes iteration order
            // of GetAllHQs() which breaks BDF/PALA UI ordering.
            // HqFromName("PMC") is handled via Patch_HqFromName postfix.
            if (!FactionRegistry.factions.Contains(PmcFaction))
                FactionRegistry.factions.Add(PmcFaction);
            FactionRegistry.factionLookup[PmcFaction.factionName] = PmcFaction;

            // Ensure MissionFaction exists
            if (MissionManager.CurrentMission != null)
                MissionManager.CurrentMission.EnsureFactionExists(PmcFaction, out _);

            // Initialize aircraftThreatTracker (normally done in ServerSetup)
            try
            {
                var threatField = AccessTools.Field(typeof(FactionHQ), "aircraftThreatTracker");
                if (threatField != null)
                {
                    threatField.SetValue(newHQ, new ThreatTracker(newHQ, 0.1f,
                        new TypeIdentity(0f, 1f, 0f, 0f, 0f)));
                    Log.LogInfo("Initialized aircraftThreatTracker for PMC HQ");
                }
            }
            catch (Exception ex2)
            {
                Log.LogWarning($"aircraftThreatTracker init: {ex2.Message}");
            }

            // Initialize missionStatsTracker (prevents NullRef in Gun.SpawnBullet
            // which caused unlimited fire rate on PMC units)
            try
            {
                if (newHQ.missionStatsTracker == null)
                {
                    var tracker = newGO.AddComponent<MissionStatsTracker>();
                    tracker.hq = newHQ;
                    newHQ.missionStatsTracker = tracker;
                    Log.LogInfo("Initialized missionStatsTracker for PMC HQ");
                }
            }
            catch (Exception ex2)
            {
                Log.LogWarning($"missionStatsTracker init: {ex2.Message}");
            }

            newGO.AddComponent<PmcTrackingUpdater>();
            newGO.AddComponent<PmcDeployer>();

            PmcHQ = newHQ;
            Log.LogInfo($"PMC FactionHQ created (fresh, no clone, {FactionRegistry.factions.Count} factions)");
            return newHQ;
        }
        catch (Exception ex)
        {
            Log.LogError($"EnsurePmcHQ failed: {ex}");
            return null;
        }
        finally
        {
            _creatingHQ = false;
        }
    }

    // ==========================================================
    //  Patch 1: Add PMC to every mission's faction list
    //  This makes PMC appear in the editor's faction dropdown
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
    //  Patch 2: FactionFromName — ensure PMC Faction is always
    //  findable even if not yet in factionLookup dictionary
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
    //  Patch 3: HqFromName — intercept PMC lookup with Prefix
    //  to prevent KeyNotFoundException and lazily create HQ
    // ==========================================================
    [HarmonyPatch(typeof(FactionRegistry), "HqFromName")]
    static class Patch_HqFromName
    {
        [HarmonyPrefix]
        static bool Prefix(string factionName, ref FactionHQ __result)
        {
            if (PmcFaction == null || factionName != PmcFaction.factionName)
                return true;

            // PMC is NOT in HQLookup, so always intercept here
            if (PmcHQ != null && ((UnityEngine.Object)PmcHQ) != null)
            {
                __result = PmcHQ;
                return false;
            }

            // Lazy create PMC HQ
            __result = EnsurePmcHQ();
            return false; // skip original to prevent KeyNotFoundException
        }
    }

    // ==========================================================
    //  Patch 3a: FactionRegistry.RegisterAirbase — prevent duplicate
    //  key exception. Ships with child Airbase components (Destroyer,
    //  FleetCarrier, AssaultCarrier) crash during ServerObjectManager
    //  .Spawn() because the scene already has built-in units with
    //  the same airbase key registered. This exception propagates up
    //  through SpawnShip → RegisterNewUnit never called → phantom.
    //  Fix: remove existing entry before Add() runs.
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
                Log.LogWarning($"RegisterAirbase: duplicate key '{key}', removing old entry to prevent crash");
                _airbaseLookup.Remove(key);
            }
        }
    }

    // ==========================================================
    //  Patch 3a-2: FactionHQ.AddAirbase — bypass [Server] for PMC.
    //  Manually executes AddAirbase body (add to SyncList, set
    //  sorting flag) via reflection, skipping the IsServer check
    //  that throws MethodInvocationException.
    //  airbasesUnsorted is SyncList<NetworkBehaviorSyncvar<Airbase>>.
    // ==========================================================
    // SyncList reflection fields removed — PMC uses plain PmcAirbases list

    [HarmonyPatch(typeof(FactionHQ), "AddAirbase")]
    static class Patch_AddAirbase
    {
        [HarmonyPrefix]
        static bool Prefix(FactionHQ __instance, Airbase airbase)
        {
            if (__instance == null) return true;
            if (__instance != PmcHQ && __instance.faction != PmcFaction) return true;

            // Use plain List instead of SyncList (SyncList fails on non-network HQ)
            if (airbase != null && !PmcAirbases.Contains(airbase))
            {
                PmcAirbases.Add(airbase);
                Log.LogInfo($"PMC AddAirbase: {airbase.name} (total: {PmcAirbases.Count})");
            }
            return false; // skip original (would throw MethodInvocationException)
        }
    }

    // ==========================================================
    //  Patch 3a-3: FactionHQ.RemoveAirbase — bypass [Server] for PMC.
    // ==========================================================
    [HarmonyPatch(typeof(FactionHQ), "RemoveAirbase")]
    static class Patch_RemoveAirbase
    {
        [HarmonyPrefix]
        static bool Prefix(FactionHQ __instance, Airbase airbase)
        {
            if (__instance == null) return true;
            if (__instance != PmcHQ && __instance.faction != PmcFaction) return true;

            if (airbase != null)
            {
                PmcAirbases.Remove(airbase);
                Log.LogInfo($"PMC RemoveAirbase: {airbase.name} (total: {PmcAirbases.Count})");
            }
            return false;
        }
    }

    // ==========================================================
    //  Patch 3a-4: FactionHQ.GetAirbases — return PmcAirbases for
    //  PMC HQ. The SyncList on a non-network-spawned HQ doesn't
    //  work, so we use our own plain List instead.
    // ==========================================================
    [HarmonyPatch(typeof(FactionHQ), "GetAirbases")]
    static class Patch_GetAirbases
    {
        [HarmonyPrefix]
        static bool Prefix(FactionHQ __instance, ref IEnumerable<Airbase> __result)
        {
            if (__instance != PmcHQ) return true;
            __result = PmcAirbases;
            return false;
        }
    }

    // ==========================================================
    //  Patch 3a-4b: FactionHQ.AddSupplyUnit — skip [Server] check
    //  for PMC HQ. IsServer is false on PmcHQ (non-network-spawned),
    //  causing ReturnToInventory to throw before setting UnitState.
    //  Returned — breaking the landing recognition flow.
    //  Also updates PmcDeployer's own supply tracking.
    // ==========================================================
    [HarmonyPatch(typeof(FactionHQ), "AddSupplyUnit")]
    static class Patch_AddSupplyUnit
    {
        [HarmonyPrefix]
        static bool Prefix(FactionHQ __instance, UnitDefinition unitDefinition, int amount)
        {
            if (__instance != PmcHQ) return true;

            // Update our own supply tracking
            PmcDeployer.ModifySupply(unitDefinition, amount);

            // Also try to modify via SyncDictionary (might work locally).
            try
            {
                __instance.ModifyUnitSupply(unitDefinition, amount);
            }
            catch { }
            return false;
        }
    }

    // ==========================================================
    //  Patch 3a-5: FactionHQ.TryGetNearestAirbase — use PmcAirbases
    //  for PMC HQ. The original iterates airbasesUnsorted (SyncList)
    //  directly, NOT through GetAirbases(). For PmcHQ, the SyncList
    //  is empty, so AI always gets airbase=null → ejects.
    //  All GetNearestAirbase overloads delegate to this method.
    // ==========================================================
    [HarmonyPatch(typeof(FactionHQ), "TryGetNearestAirbase",
        new[] { typeof(Vector3), typeof(float), typeof(Airbase), typeof(RunwayQuery) },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal })]
    static class Patch_TryGetNearestAirbase
    {
        [HarmonyPrefix]
        static bool Prefix(FactionHQ __instance, Vector3 fromPosition, float range,
            out Airbase nearestAirbase, RunwayQuery query, ref bool __result)
        {
            if (__instance != PmcHQ)
            {
                nearestAirbase = null;
                return true;
            }

            // Reimplement using PmcAirbases instead of airbasesUnsorted
            nearestAirbase = null;
            bool found = false;
            float bestDistSq = range * range;
            foreach (var airbase in PmcAirbases)
            {
                if (airbase == null) continue;
                if (!airbase.IsSuitable(query)) continue;
                float distSq = FastMath.SquareDistance(fromPosition, airbase.center.position);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    nearestAirbase = airbase;
                    found = true;
                }
            }
            __result = found;
            return false; // skip original
        }
    }

    // ==========================================================
    //  Patch 3a-6: FactionHQ.AnyNearAirbase — use PmcAirbases
    //  for PMC HQ. Same issue as TryGetNearestAirbase: original
    //  iterates airbasesUnsorted directly. Used by AIPilotTaxiState.
    // ==========================================================
    [HarmonyPatch(typeof(FactionHQ), "AnyNearAirbase")]
    static class Patch_AnyNearAirbase
    {
        [HarmonyPrefix]
        static bool Prefix(FactionHQ __instance, Vector3 fromPosition,
            out Airbase airbase, ref bool __result)
        {
            if (__instance != PmcHQ)
            {
                airbase = null;
                return true;
            }

            // Reimplement using PmcAirbases
            airbase = null;
            foreach (var ab in PmcAirbases)
            {
                if (ab == null) continue;
                float radius = ab.GetRadius();
                if (FastMath.InRange(fromPosition, ab.center.position, radius))
                {
                    airbase = ab;
                    __result = true;
                    return false;
                }
            }
            __result = false;
            return false;
        }
    }

    // ==========================================================
    //  Patch 3b: Pilot.SetStartingAiState — diagnostics + safety net.
    // ==========================================================
    [HarmonyPatch(typeof(Pilot), "SetStartingAiState")]
    static class Patch_PilotStartAI
    {
        [HarmonyPrefix]
        static void Prefix(Pilot __instance)
        {
            if (PmcFaction == null) return;

            var aircraft = __instance.aircraft;
            if (aircraft == null) return;

            var hq = aircraft.NetworkHQ;
            var hqFaction = hq?.faction?.factionName ?? "NULL";
            var isPmc = hq != null && hq.faction == PmcFaction;

            // Log ALL SetStartingAiState calls so we can see which units get AI
            Log.LogInfo($"SetStartingAiState: {aircraft.name} | HQ={hqFaction} | isPMC={isPmc} | radarAlt={aircraft.radarAlt:F0}");

            if (hq != null) return; // has HQ, original method handles it

            // Null NetworkHQ — try to assign PMC HQ
            if (PmcHQ != null)
            {
                aircraft.NetworkHQ = PmcHQ;
                Log.LogInfo($"  → Assigned PMC HQ (result={aircraft.NetworkHQ?.faction?.factionName ?? "still NULL"})");
            }
        }
    }

    // ==========================================================
    //  Patch 3c: Spawner.SpawnAircraft — track PMC spawns.
    //  We do NOT touch any SyncLists (factionUnits, factionRadarReturn)
    //  on the PMC HQ clone because SyncList operations on a
    //  non-network-spawned clone cause silent corruption.
    //  Instead we use our own plain HashSets for tracking.
    // ==========================================================
    [HarmonyPatch(typeof(Spawner), "SpawnAircraft")]
    static class Patch_SpawnAircraft
    {
        [HarmonyPostfix]
        static void Postfix(Aircraft __result, FactionHQ HQ)
        {
            if (PmcFaction == null || HQ == null || __result == null) return;
            if (HQ.faction != PmcFaction && HQ != PmcHQ) return;

            var actualHQ = __result.NetworkHQ;
            Log.LogInfo($"SpawnAircraft(PMC): {__result.name} | NetworkHQ={actualHQ?.faction?.factionName ?? "NULL"} | HQ param={HQ.faction?.factionName}");

            // Track in our own plain collections (no SyncList!)
            PmcUnitInstanceIds.Add(__result.GetInstanceID());
            PmcUnitPersistentIds.Add(__result.persistentID);
            Log.LogInfo($"  → Tracked: instanceIds={PmcUnitInstanceIds.Count}, persistentIds={PmcUnitPersistentIds.Count}");
        }
    }

    // ==========================================================
    //  Patch 3d: Unit.NetworkHQ setter — intercept PMC.
    //  PmcHQ has NetId=0. Problems with SyncVar:
    //  1) SyncVarEqual(PmcHQ, null) both NetId=0 → skips setter
    //  2) Even if force-written, Mirage sync cycle clears
    //     _behaviour cache and NetId=0 lookup → null again
    //  Solution: Don't write to SyncVar. Track the unit and
    //  manually execute HQChanged logic (SetHQ + onChangeFaction)
    //  so airbases, UnitRegistry, etc. react to the faction change.
    // ==========================================================
    static FieldInfo _onChangeFactionField;
    static MethodInfo _captureFactionMethod;

    [HarmonyPatch(typeof(Unit), "set_NetworkHQ")]
    static class Patch_NetworkHQ_Setter
    {
        [HarmonyPrefix]
        static bool Prefix(Unit __instance, FactionHQ value)
        {
            if (value == null || PmcHQ == null) return true;
            if (value != PmcHQ && value.faction != PmcFaction) return true;

            // Track this unit as PMC (getter postfix handles reads)
            int instanceId = __instance.GetInstanceID();
            bool isNew = PmcUnitInstanceIds.Add(instanceId);
            PmcUnitPersistentIds.Add(__instance.persistentID);
            if (isNew)
            {
                Log.LogInfo($"PMC unit tracked: {__instance.name} id={instanceId}");

                // Add to DynamicMap so icon appears immediately
                try
                {
                    var map = SceneSingleton<DynamicMap>.i;
                    if (map != null)
                        map.AddIcon(__instance.persistentID);
                }
                catch { }
            }

            // Manually execute HQChanged logic without writing SyncVar.
            // 1) Update PersistentUnit in UnitRegistry
            try
            {
                if (UnitRegistry.persistentUnitLookup.TryGetValue(
                        __instance.persistentID, out var pu))
                    pu.SetHQ(PmcHQ);
            }
            catch { }

            // 2) Fire onChangeFaction so subscribed components react
            try
            {
                if (_onChangeFactionField == null)
                    _onChangeFactionField = AccessTools.Field(typeof(Unit), "onChangeFaction");
                if (_onChangeFactionField != null)
                {
                    var handler = _onChangeFactionField.GetValue(__instance) as Action<Unit>;
                    handler?.Invoke(__instance);
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"onChangeFaction invoke: {ex.Message}");
            }

            // 3) Handle attached airbases (carriers).
            //    Airbase.OnStartServer may have already run before this setter,
            //    assigning the carrier's airbase to BDF/PALA. Re-capture for PMC.
            try
            {
                var airbases = __instance.GetComponentsInChildren<Airbase>();
                if (airbases != null && airbases.Length > 0)
                {
                    if (_captureFactionMethod == null)
                        _captureFactionMethod = AccessTools.Method(typeof(Airbase), "CaptureFaction");

                    foreach (var ab in airbases)
                    {
                        if (ab == null) continue;
                        // Check if airbase is assigned to wrong faction
                        var currentHQ = Traverse.Create(ab).Property("CurrentHQ").GetValue<FactionHQ>();
                        if (currentHQ != PmcHQ)
                        {
                            _captureFactionMethod?.Invoke(ab, new object[] { PmcHQ });
                            Log.LogInfo($"Re-captured airbase '{ab.name}' for PMC (was: {currentHQ?.faction?.factionName ?? "null"})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Carrier airbase re-capture: {ex.Message}");
            }

            // Don't write to SyncVar — getter postfix handles all reads
            return false;
        }
    }

    // ==========================================================
    //  Patch 3d-2: FactionHQ.RegisterFactionUnit — skip SyncList
    //  for PMC. The clone's SyncList operations fail because
    //  its Identity has no Server/SyncVarSender. Use our plain
    //  HashSets instead.
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

            // DynamicMap has NO event subscription — we must manually
            // add icons when new PMC units register. This fixes the
            // "PMC units not visible on editor/spectator map" bug.
            try
            {
                var map = SceneSingleton<DynamicMap>.i;
                if (map != null)
                    map.AddIcon(unit.persistentID);
            }
            catch { }

            // Fire activeAIAircraft tracking for AI aircraft
            if (unit is Aircraft aircraft && aircraft.Player == null)
            {
                try
                {
                    var aiList = AccessTools.Field(typeof(FactionHQ), "activeAIAircraft")
                        ?.GetValue(__instance) as List<Aircraft>;
                    aiList?.Add(aircraft);
                }
                catch { }
            }

            return false; // Skip original (SyncList would fail)
        }
    }

    // ==========================================================
    //  Patch 3d-3: FactionHQ.RemoveFactionUnit — skip SyncList
    //  for PMC.
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
    //  Patch 3e: Unit.NetworkHQ getter — fallback for PMC units.
    //  SyncVar stores by NetId; PmcHQ has NetId=0 so it resolves
    //  to null. This returns PmcHQ for known PMC units, with
    //  SavedUnit.faction and MapHQ as fallbacks for edge cases.
    // ==========================================================
    [HarmonyPatch(typeof(Unit), "get_NetworkHQ")]
    static class Patch_NetworkHQ_Getter
    {
        [HarmonyPostfix]
        static void Postfix(Unit __instance, ref FactionHQ __result)
        {
            if (__result != null || PmcHQ == null) return;

            // Fast path: known PMC unit
            if (PmcUnitInstanceIds.Contains(__instance.GetInstanceID()))
            {
                __result = PmcHQ;
                return;
            }

            // Fallback: check SavedUnit faction (catches editor-placed units)
            try
            {
                var saved = __instance.SavedUnit;
                if (saved != null && saved.faction == PmcFaction.factionName)
                {
                    PmcUnitInstanceIds.Add(__instance.GetInstanceID());
                    __result = PmcHQ;
                    return;
                }
            }
            catch { }

            // Fallback: check MapHQ (set during mission loading)
            try
            {
                if (__instance.MapHQ != null && __instance.MapHQ.faction == PmcFaction)
                {
                    PmcUnitInstanceIds.Add(__instance.GetInstanceID());
                    __result = PmcHQ;
                }
            }
            catch { }
        }
    }

    // ==========================================================
    //  Patch 4: RegisterFaction — intentionally empty.
    //  Adding PMC to factions here caused early HqFromName calls
    //  (even during MainMenu!), which cloned+spawned a FactionHQ
    //  with the source's sceneId, overwriting BDF/PALA in Mirage's
    //  scene objects dictionary → all AI broken.
    //  PMC is registered lazily in EnsurePmcHQ() instead.
    // ==========================================================
    [HarmonyPatch(typeof(FactionRegistry), "RegisterFaction")]
    static class Patch_RegisterFaction
    {
        [HarmonyPostfix]
        static void Postfix(Faction faction, FactionHQ HQ)
        {
            // Intentionally empty — PMC is registered in EnsurePmcHQ
        }
    }

    // ==========================================================
    //  Patch 4b: FactionHQ.SetupAirbaseFactions — skip for PMC.
    //  The clone has no airbases, but calling RefreshAirbases()
    //  during its OnStartClient disrupts BDF airbase assignments.
    // ==========================================================
    [HarmonyPatch(typeof(FactionHQ), "SetupAirbaseFactions")]
    static class Patch_SetupAirbaseFactions
    {
        [HarmonyPrefix]
        static bool Prefix(FactionHQ __instance)
        {
            if (__instance.faction == PmcFaction)
            {
                Log.LogInfo("Skipped SetupAirbaseFactions for PMC HQ");
                return false;
            }
            return true;
        }
    }

    // ==========================================================
    //  Patch 4c: FactionHQ.ServerSetup — skip for PMC.
    //  Prevents clone from running DeployUnits, DistributeFunds,
    //  OnMissionLoad which can interfere with existing factions.
    // ==========================================================
    [HarmonyPatch(typeof(FactionHQ), "ServerSetup")]
    static class Patch_ServerSetup
    {
        [HarmonyPrefix]
        static bool Prefix(FactionHQ __instance)
        {
            if (__instance.faction == PmcFaction)
            {
                Log.LogInfo("Skipped ServerSetup for PMC HQ");
                return false;
            }
            return true;
        }
    }

    // ==========================================================
    //  Patch 4e: FactionHQ.Update — skip for PMC clone.
    //  Update() calls aircraftThreatTracker.CheckThreats() which
    //  NREs every frame because ServerSetup (which initializes
    //  aircraftThreatTracker) is skipped for PMC. The clone
    //  inherits IsServer=true from the source, so the guard
    //  passes and it crashes. This NRE every frame is the primary
    //  cause of BDF/PALA AI freezing.
    // ==========================================================
    [HarmonyPatch(typeof(FactionHQ), "Update")]
    static class Patch_FactionHQ_Update
    {
        [HarmonyPrefix]
        static bool Prefix(FactionHQ __instance)
        {
            return __instance.faction != PmcFaction;
        }
    }

    // ==========================================================
    //  Patch 4c-2: MissionStatsTracker.MunitionCost — skip for PMC.
    //  Even with missionStatsTracker initialized, MunitionCost
    //  crashes at offset 0x00015 because internal SyncDictionary
    //  (currentUnits) isn't properly initialized. This NullRef
    //  aborts Gun.SpawnBullet before cooldown is updated → infinite
    //  fire rate. Skipping for PMC is safe (just stats tracking).
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
    //  Patch 4c-3: DynamicMap.GenerateMap — include PMC units.
    //  PMC factionUnits SyncList is empty (RegisterFactionUnit
    //  is skipped), so GenerateMap finds no PMC units. This
    //  postfix adds PMC units from our PmcUnitPersistentIds set.
    //  Also handles spectator mode (HQ==null) where base game's
    //  GenerateMap(null) iterates allUnits but may miss PMC units
    //  if they aren't properly tracked.
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

                // In PLAY mode: only show PMC units detected by player's radar
                // (i.e. in player HQ's trackingDatabase). No ESP!
                // In SPECTATOR/EDITOR mode: base game's GenerateMap(null)
                // already adds ALL units from UnitRegistry.allUnits,
                // so PMC units are included automatically.
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
                Log.LogError($"GenerateMap postfix exception: {ex}");
            }
        }
    }

    // ==========================================================
    //  Patch 4c-4: DynamicMap.SetFaction — ensure PMC HQ's units
    //  and airbases are included in spectator mode. The base game
    //  iterates GetAllHQs() which excludes PMC. We add an explicit
    //  PMC pass after the base method runs.
    // ==========================================================
    [HarmonyPatch(typeof(DynamicMap), "SetFaction")]
    static class Patch_SetFaction
    {
        [HarmonyPostfix]
        static void Postfix(DynamicMap __instance, FactionHQ HQ)
        {
            // Base game doesn't show airbases in spectator mode
            // (showAirbases=false). We follow the same convention.
            // PMC units are already included via GenerateMap(null)
            // which iterates UnitRegistry.allUnits.
        }
    }

    // ==========================================================
    //  Patch 4d: FactionHQ.RpcUpdateTrackingInfo — bypass RPC
    //  for PMC HQ. Without spawnableObjects registration, the
    //  client-side object isn't set up, so RPCs are silently
    //  dropped. We directly call the UserCode implementation
    //  instead, which populates trackingDatabase properly.
    // ==========================================================
    static MethodInfo _userCodeRpcUpdateTracking;

    [HarmonyPatch(typeof(FactionHQ), "RpcUpdateTrackingInfo")]
    static class Patch_RpcUpdateTrackingInfo
    {
        [HarmonyPrefix]
        static bool Prefix(FactionHQ __instance, PersistentID id)
        {
            if (__instance.faction != PmcFaction) return true;

            // Find the UserCode method once (it has a mangled name)
            if (_userCodeRpcUpdateTracking == null)
            {
                _userCodeRpcUpdateTracking = typeof(FactionHQ).GetMethods(
                        BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name.Contains("UserCode_RpcUpdateTrackingInfo"));

                if (_userCodeRpcUpdateTracking == null)
                {
                    Log.LogWarning("Could not find UserCode_RpcUpdateTrackingInfo method");
                    return true; // fall through to original
                }
                Log.LogInfo($"Found UserCode method: {_userCodeRpcUpdateTracking.Name}");
            }

            try
            {
                _userCodeRpcUpdateTracking.Invoke(__instance, new object[] { id });
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Direct tracking update failed: {ex.Message}");
            }
            return false; // skip original RPC send (would fail anyway)
        }
    }

    // ==========================================================
    //  Patch 4d-2: FactionHQ.CmdUpdateTrackingInfo — bypass ServerRpc
    //  for PMC. MissileWarning.Update calls this, but PMC HQ has no
    //  Client → "Server RPC can only be called when client is active".
    //  Redirect to the UserCode method directly.
    // ==========================================================
    [HarmonyPatch(typeof(FactionHQ), "CmdUpdateTrackingInfo")]
    static class Patch_CmdUpdateTrackingInfo
    {
        [HarmonyPrefix]
        static bool Prefix(FactionHQ __instance, PersistentID id)
        {
            if (__instance.faction != PmcFaction) return true;

            if (_userCodeRpcUpdateTracking == null)
            {
                _userCodeRpcUpdateTracking = typeof(FactionHQ).GetMethods(
                        BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name.Contains("UserCode_RpcUpdateTrackingInfo"));
            }
            if (_userCodeRpcUpdateTracking != null)
            {
                try { _userCodeRpcUpdateTracking.Invoke(__instance, new object[] { id }); }
                catch { }
            }
            return false;
        }
    }

    // ==========================================================
    //  Patch 4e: FactionHQ.RequestTrackingStates — skip for PMC.
    //  Called from CmdSetFaction when player joins faction.
    //  Uses RpcGetTrackingStateBatched which crashes on non-network HQ.
    // ==========================================================
    [HarmonyPatch(typeof(FactionHQ), "RequestTrackingStates")]
    static class Patch_RequestTrackingStates
    {
        [HarmonyPrefix]
        static bool Prefix(FactionHQ __instance)
        {
            if (__instance.faction != PmcFaction) return true;
            Log.LogInfo("RequestTrackingStates: skipped for PMC (non-network HQ)");
            return false;
        }
    }

    // ==========================================================
    //  Patch 4f: Spawner.AllowedToSpawn — diagnostic logging for PMC
    // ==========================================================
    [HarmonyPatch(typeof(Spawner), "AllowedToSpawn")]
    static class Patch_AllowedToSpawn
    {
        [HarmonyPostfix]
        static void Postfix(bool __result, Airbase airbase, Player player)
        {
            if (PmcHQ == null) return;
            var playerHQ = player?.HQ;
            var airbaseHQ = airbase?.CurrentHQ;
            if (playerHQ == PmcHQ || airbaseHQ == PmcHQ)
            {
                Log.LogInfo($"AllowedToSpawn: result={__result}, " +
                    $"player.HQ={playerHQ?.faction?.factionName ?? "null"} (isPmc={playerHQ == PmcHQ}), " +
                    $"airbase.CurrentHQ={airbaseHQ?.faction?.factionName ?? "null"} (isPmc={airbaseHQ == PmcHQ}), " +
                    $"sameRef={playerHQ == airbaseHQ}");
            }
        }
    }

    // ==========================================================
    //  Patch 5: JoinMenu.OnEnable — expand factionDisplays for PMC
    // ==========================================================
    [HarmonyPatch(typeof(JoinMenu), "OnEnable")]
    static class Patch_JoinMenu_OnEnable
    {
        [HarmonyPrefix]
        static bool Prefix(JoinMenu __instance)
        {
            // PMC is AI-only — let original 2-faction JoinMenu run unmodified
            return true;
        }
    }

    static Array ExpandJoinMenuDisplays(JoinMenu joinMenu, Array existing, int targetCount)
    {
        var displayType = typeof(JoinMenu).GetNestedType("JoinFactionDisplay", BindingFlags.NonPublic);
        if (displayType == null) { Log.LogError("JoinFactionDisplay type not found"); return existing; }

        var newArr = Array.CreateInstance(displayType, targetCount);
        Array.Copy(existing, newArr, existing.Length);

        var rootField = displayType.GetField("rootContainer", BindingFlags.NonPublic | BindingFlags.Instance);
        var joinBtnField = displayType.GetField("joinButton", BindingFlags.NonPublic | BindingFlags.Instance);
        var nameField = displayType.GetField("factionName", BindingFlags.NonPublic | BindingFlags.Instance);
        var scoreField = displayType.GetField("factionScore", BindingFlags.NonPublic | BindingFlags.Instance);
        var flagField = displayType.GetField("factionFlag", BindingFlags.NonPublic | BindingFlags.Instance);
        var contentField = displayType.GetField("playerListContent", BindingFlags.NonPublic | BindingFlags.Instance);
        var scrollField = displayType.GetField("scrollView", BindingFlags.NonPublic | BindingFlags.Instance);
        var prefabField = displayType.GetField("playerEntryPrefab", BindingFlags.NonPublic | BindingFlags.Instance);
        var listField = displayType.GetField("playerList", BindingFlags.NonPublic | BindingFlags.Instance);

        var src = existing.GetValue(existing.Length - 1);
        var srcRoot = (RectTransform)rootField.GetValue(src);

        for (int i = existing.Length; i < targetCount; i++)
        {
            var clonedGo = UnityEngine.Object.Instantiate(srcRoot.gameObject, srcRoot.parent);
            clonedGo.name = $"FactionDisplay_PMC_{i}";
            var clonedRoot = clonedGo.GetComponent<RectTransform>();

            var disp = Activator.CreateInstance(displayType);
            rootField.SetValue(disp, clonedRoot);
            prefabField.SetValue(disp, prefabField.GetValue(src));
            listField.SetValue(disp, new List<GameObject>());

            MapField(src, disp, joinBtnField, srcRoot, clonedRoot);
            MapField(src, disp, nameField, srcRoot, clonedRoot);
            MapField(src, disp, scoreField, srcRoot, clonedRoot);
            MapField(src, disp, flagField, srcRoot, clonedRoot);
            MapField(src, disp, contentField, srcRoot, clonedRoot);
            MapField(src, disp, scrollField, srcRoot, clonedRoot);

            // Wire join button to JoinMenu.JoinFaction(index)
            var btn = (Button)joinBtnField.GetValue(disp);
            if (btn != null)
            {
                btn.onClick.RemoveAllListeners();
                int idx = i;
                btn.onClick.AddListener(() => joinMenu.JoinFaction(idx));
            }

            newArr.SetValue(disp, i);
            Log.LogInfo($"Expanded JoinMenu: added display #{i}");
        }

        // Reposition all displays to distribute evenly
        try
        {
            var root0 = (RectTransform)rootField.GetValue(newArr.GetValue(0));
            var root1 = (RectTransform)rootField.GetValue(newArr.GetValue(1));
            bool isStretch = (root0.anchorMin.x != root0.anchorMax.x);

            if (isStretch)
            {
                // Stretch-anchored: redistribute anchor ranges
                float xMin = Math.Min(root0.anchorMin.x, root1.anchorMin.x);
                float xMax = Math.Max(root0.anchorMax.x, root1.anchorMax.x);
                float seg = (xMax - xMin) / targetCount;
                float pad = seg * 0.01f;

                for (int j = 0; j < targetCount; j++)
                {
                    var rt = (RectTransform)rootField.GetValue(newArr.GetValue(j));
                    rt.anchorMin = new Vector2(xMin + seg * j + pad, root0.anchorMin.y);
                    rt.anchorMax = new Vector2(xMin + seg * (j + 1) - pad, root0.anchorMax.y);
                    rt.offsetMin = new Vector2(2, root0.offsetMin.y);
                    rt.offsetMax = new Vector2(-2, root0.offsetMax.y);
                }
            }
            else
            {
                // Fixed-position: scale down and reposition
                float origSpacing = root1.anchoredPosition.x - root0.anchoredPosition.x;
                float scaleX = (float)(existing.Length) / targetCount;
                float newSpacing = origSpacing * scaleX;

                for (int j = 0; j < targetCount; j++)
                {
                    var rt = (RectTransform)rootField.GetValue(newArr.GetValue(j));
                    rt.localScale = new Vector3(scaleX, 1f, 1f);
                    rt.anchoredPosition = new Vector2(
                        root0.anchoredPosition.x + newSpacing * j,
                        root0.anchoredPosition.y);
                }
            }
            Log.LogInfo($"Repositioned {targetCount} JoinMenu displays (stretch={isStretch})");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"JoinMenu reposition: {ex.Message}");
        }

        return newArr;
    }

    // ==========================================================
    //  Patch 6: Leaderboard.OnEnable — expand for PMC
    // ==========================================================
    [HarmonyPatch(typeof(Leaderboard), "OnEnable")]
    static class Patch_Leaderboard_OnEnable
    {
        [HarmonyPrefix]
        static bool Prefix(Leaderboard __instance)
        {
            if (PmcFaction == null) return true;

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
                var hqList2 = FactionRegistry.GetAllHQs().ToList();
                if (PmcHQ != null && !hqList2.Contains(PmcHQ))
                    hqList2.Add(PmcHQ);
                var allHQs = hqList2.ToArray();

                // Dynamically expand factionDisplays array if needed
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

        // Find display root: walk up from scrollView until sibling contains other display's scrollView
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
    //  Helpers: UI display cloning for JoinMenu / Leaderboard
    // ==========================================================

    /// <summary>Map a Component field from source display to cloned display by transform path.</summary>
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

    /// <summary>Get the relative transform path from child to root (e.g. "Panel/Button").</summary>
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

    /// <summary>Walk up from child0 to find the display root (a sibling of child1's ancestor).</summary>
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

    // ==========================================================
    //  Patch 6b: SavedUnit.AfterCreate — ensure faction string
    //  is always set for PMC units. Without this, if the getter
    //  fails to return PmcHQ at AfterCreate time, faction stays
    //  empty and the unit won't respawn on mission reload.
    // ==========================================================
    [HarmonyPatch(typeof(SavedUnit), "AfterCreate")]
    static class Patch_SavedUnit_AfterCreate
    {
        [HarmonyPostfix]
        static void Postfix(SavedUnit __instance, Unit unit)
        {
            if (PmcHQ == null || PmcFaction == null || unit == null) return;
            if (!PmcUnitInstanceIds.Contains(unit.GetInstanceID())) return;

            if (string.IsNullOrEmpty(__instance.faction) || __instance.faction != PmcFaction.factionName)
            {
                Log.LogInfo($"AfterCreate fix: {unit.name} faction '{__instance.faction}' → '{PmcFaction.factionName}'");
                __instance.faction = PmcFaction.factionName;
            }
        }
    }

    // ==========================================================
    //  Patch 6c: Spawner.SpawnFromMissionInEditor — ensure PmcHQ
    //  exists before the editor tries to spawn PMC units.
    //  Without this, HqFromName("PMC") during editor re-spawn
    //  could fail if no source HQ is available yet.
    // ==========================================================
    [HarmonyPatch(typeof(Spawner), "SpawnFromMissionInEditor")]
    static class Patch_SpawnFromMissionInEditor
    {
        [HarmonyPrefix]
        static void Prefix()
        {
            if (PmcFaction == null) return;
            EnsurePmcHQ();
            Log.LogInfo($"SpawnFromMissionInEditor: PmcHQ={(PmcHQ != null ? "ready" : "FAILED")}");
        }
    }

    // ==========================================================
    //  Patch 6d: MissionManager.StartMission — ensure PmcHQ
    //  exists before gameplay spawns PMC units.
    // ==========================================================
    [HarmonyPatch(typeof(MissionManager), "StartMission")]
    static class Patch_StartMission
    {
        [HarmonyPrefix]
        static void Prefix()
        {
            if (PmcFaction == null) return;
            EnsurePmcHQ();
            Log.LogInfo($"StartMission: PmcHQ={(PmcHQ != null ? "ready" : "FAILED")}, factions={FactionRegistry.factions.Count}");
        }
    }

    // ==========================================================
    //  Patch 7: InfoPanel_Faction.SetFaction — REMOVED.
    //  The original code handles BDF/PALA correctly via hardcoded
    //  "Boscali"/"Primeva" defaults. For PMC players, HqFromName("PMC")
    //  is handled by Patch_HqFromName. The previous N-faction patch
    //  caused both panels to show the same faction when playerHQ=null.
    // ==========================================================
}

// ==========================================================
//  PmcTrackingUpdater: Periodically scans all enemy units and
//  directly populates PMC HQ's trackingDatabase.
//  This bypasses the radar→detection→RPC pipeline which
//  doesn't work for a clone HQ without full network setup.
// ==========================================================
public class PmcTrackingUpdater : MonoBehaviour
{
    FactionHQ _hq;
    MethodInfo _userCodeMethod;
    float _nextScan;
    bool _loggedOnce;

    void Start()
    {
        _hq = GetComponent<FactionHQ>();
        _userCodeMethod = typeof(FactionHQ)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name.Contains("UserCode_RpcUpdateTrackingInfo"));

        if (_userCodeMethod != null)
            Plugin.Log.LogInfo($"PmcTrackingUpdater: found {_userCodeMethod.Name}");
        else
            Plugin.Log.LogWarning("PmcTrackingUpdater: UserCode_RpcUpdateTrackingInfo not found, using fallback");
    }

    void Update()
    {
        if (_hq == null || Time.time < _nextScan) return;
        _nextScan = Time.time + 2f;

        int scanned = 0;
        int dbBefore = _hq.trackingDatabase.Count;
        try
        {
            // 1) Add OTHER factions' units to PMC's trackingDatabase
            foreach (var otherHQ in FactionRegistry.GetAllHQs())
            {
                if (otherHQ == _hq) continue;

                var unitIds = new List<PersistentID>(otherHQ.factionUnits);
                foreach (var unitId in unitIds)
                {
                    scanned++;
                    if (_userCodeMethod != null)
                        _userCodeMethod.Invoke(_hq, new object[] { unitId });
                    else
                    {
                        if (!UnitRegistry.TryGetUnit(unitId, out var unit)) continue;
                        if (_hq.trackingDatabase.ContainsKey(unitId))
                            _hq.trackingDatabase[unitId].UpdateInfo(unit.GlobalPosition());
                        else
                            _hq.trackingDatabase.Add(unitId, new TrackingInfo(unit));
                    }
                }
            }

            // PMC units are detected by OTHER factions' radars naturally.
            // The radar system uses NetworkHQ comparison (our getter postfix
            // returns PmcHQ → different from player HQ → treated as enemy).
            // No forced trackingDatabase injection needed.
        }
        catch (Exception ex)
        {
            if (!_loggedOnce)
            {
                Plugin.Log.LogWarning($"PmcTrackingUpdater error: {ex}");
                _loggedOnce = true;
            }
        }

        int dbAfter = _hq.trackingDatabase.Count;
        if (!_loggedOnce)
        {
            Plugin.Log.LogInfo($"PmcTrackingUpdater: scanned={scanned} enemies, trackingDB {dbBefore}→{dbAfter}, pmcInstanceIds={Plugin.PmcUnitInstanceIds.Count}");
            _loggedOnce = true;
        }
    }
}

// ==========================================================
//  PmcDeployer: Handles AI aircraft and vehicle deployment
//  for PMC HQ. The base game's FactionHQ.Update → DeployUnits
//  is skipped for PMC, and AircraftSupply/VehicleSupply
//  SyncDictionaries don't work on non-network HQ.
//  This component maintains its own supply tracking and
//  periodically deploys units using PmcAirbases/depots.
// ==========================================================
public class PmcDeployer : MonoBehaviour
{
    internal static Dictionary<UnitDefinition, int> AircraftSupplyOwn = new();
    internal static Dictionary<UnitDefinition, int> VehicleSupplyOwn = new();

    FactionHQ _hq;
    float _nextDeploy;
    bool _supplyInitialized;
    int _aiLimit = 8;
    bool _loggedInit;

    void Start()
    {
        _hq = GetComponent<FactionHQ>();
    }

    void InitSupply()
    {
        if (_supplyInitialized) return;
        var mission = MissionManager.CurrentMission;
        if (mission == null) return;
        if (!mission.TryGetFaction(Plugin.PmcFaction.factionName, out var mf)) return;

        AircraftSupplyOwn.Clear();
        VehicleSupplyOwn.Clear();

        foreach (var supply in mf.supplies)
        {
            if (Encyclopedia.Lookup.TryGetValue(supply.unitType, out var def))
            {
                if (def is AircraftDefinition ad)
                    AircraftSupplyOwn[ad] = supply.count;
                else if (def is VehicleDefinition vd)
                    VehicleSupplyOwn[vd] = supply.count;
            }
        }

        _aiLimit = mf.AIAircraftLimit;
        if (_aiLimit <= 0) _aiLimit = 8; // sane default
        _supplyInitialized = true;

        if (!_loggedInit)
        {
            Plugin.Log.LogInfo($"PmcDeployer: initialized supply — {AircraftSupplyOwn.Count} aircraft types, {VehicleSupplyOwn.Count} vehicle types, AI limit={_aiLimit}");
            _loggedInit = true;
        }
    }

    void Update()
    {
        if (_hq == null || Time.time < _nextDeploy) return;
        _nextDeploy = Time.time + 5f;
        if (!MissionManager.IsRunning) return;

        InitSupply();
        if (!_supplyInitialized) return;

        try { DeployAircraft(); } catch (Exception ex) { Plugin.Log.LogWarning($"PmcDeployer aircraft: {ex.Message}"); }
        try { DeployVehicles(); } catch (Exception ex) { Plugin.Log.LogWarning($"PmcDeployer vehicles: {ex.Message}"); }
    }

    void DeployAircraft()
    {
        // Count active PMC AI aircraft
        int active = 0;
        foreach (var unit in UnitRegistry.allUnits)
        {
            if (unit == null || unit.disabled) continue;
            if (unit is Aircraft && Plugin.PmcUnitInstanceIds.Contains(unit.GetInstanceID()))
                active++;
        }
        if (active >= _aiLimit) return;

        // Shuffle aircraft list for variety
        var defs = new List<AircraftDefinition>(Encyclopedia.i.aircraft);
        for (int i = 0; i < defs.Count; i++)
        {
            int j = UnityEngine.Random.Range(i, defs.Count);
            (defs[i], defs[j]) = (defs[j], defs[i]);
        }

        foreach (var def in defs)
        {
            if (!AircraftSupplyOwn.TryGetValue(def, out var count) || count <= 0)
                continue;

            foreach (var airbase in Plugin.PmcAirbases)
            {
                if (airbase == null) continue;
                if (!airbase.CanSpawnAircraft(def)) continue;

                try
                {
                    // Get loadout and fuel from standard loadout
                    Loadout loadout = null;
                    float fuelLevel = def.aircraftParameters.DefaultFuelLevel;
                    var stdLoadout = def.aircraftParameters.GetRandomStandardLoadout(def, _hq);
                    if (stdLoadout != null)
                    {
                        loadout = stdLoadout.loadout;
                        fuelLevel = stdLoadout.FuelRatio;
                    }
                    var liveryKey = GetRandomPmcLivery(def);

                    // Try through Airbase.TrySpawnAircraft (carrier has IsServer=true)
                    var result = airbase.TrySpawnAircraft(null, def,
                        liveryKey, loadout, fuelLevel);

                    if (result.Allowed)
                    {
                        // Supply decrement happens inside TrySpawnAircraft
                        // via AddSupplyUnit(-1) → our patch → ModifySupply
                        Plugin.Log.LogInfo($"PmcDeployer: deployed {def.name} at {airbase.name} (supply: {count}→{count - 1})");
                        return; // one per cycle
                    }
                }
                catch (Mirage.MethodInvocationException)
                {
                    // Airbase/Hangar [Server] check failed — fall back to direct spawn
                    try
                    {
                        var spawnPos = airbase.center.GlobalPosition();
                        spawnPos = new GlobalPosition(spawnPos.x, spawnPos.y + 200f, spawnPos.z);
                        var rot = airbase.center.rotation;
                        var vel = rot * Vector3.forward * 80f;
                        var liveryKey = GetRandomPmcLivery(def);

                        NetworkSceneSingleton<Spawner>.i.SpawnAircraft(
                            null, def.unitPrefab, null,
                            def.aircraftParameters.DefaultFuelLevel,
                            liveryKey, spawnPos, rot, vel,
                            null, Plugin.PmcHQ, null, 1f, 0.5f);

                        AircraftSupplyOwn[def] = count - 1;
                        Plugin.Log.LogInfo($"PmcDeployer: direct-spawned {def.name} near {airbase.name}");
                        return;
                    }
                    catch (Exception ex2)
                    {
                        Plugin.Log.LogWarning($"PmcDeployer fallback spawn failed: {ex2.Message}");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"PmcDeployer: TrySpawnAircraft failed: {ex.Message}");
                }
            }
        }
    }

    void DeployVehicles()
    {
        // Access PmcHQ's depotSorted via reflection
        var depotField = AccessTools.Field(typeof(FactionHQ), "depotSorted");
        if (depotField == null) return;
        var depotList = depotField.GetValue(_hq);
        if (depotList == null) return;

        // depotSorted is List<(VehicleDepot depot, float distance)>
        var countProp = depotList.GetType().GetProperty("Count");
        int depotCount = (int)(countProp?.GetValue(depotList) ?? 0);
        if (depotCount == 0) return;

        var itemMethod = depotList.GetType().GetProperty("Item");

        foreach (var kvp in new Dictionary<UnitDefinition, int>(VehicleSupplyOwn))
        {
            if (kvp.Value <= 0) continue;
            if (!(kvp.Key is VehicleDefinition vdef)) continue;

            for (int i = 0; i < depotCount; i++)
            {
                try
                {
                    var item = itemMethod.GetValue(depotList, new object[] { i });
                    // ValueTuple<VehicleDepot, float>
                    var depotFieldItem = item.GetType().GetField("Item1");
                    var depot = depotFieldItem?.GetValue(item) as VehicleDepot;
                    if (depot == null || depot.disabled) continue;

                    if (depot.TrySpawnVehicle(vdef))
                    {
                        VehicleSupplyOwn[kvp.Key] = kvp.Value - 1;
                        Plugin.Log.LogInfo($"PmcDeployer: deployed vehicle {vdef.name} at depot");
                        break;
                    }
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Pick a random livery for PMC aircraft.
    /// Priority: workshop/AppData custom skins > all built-in skins (any faction).
    /// Passes null faction to GetLiveryOptions so ALL liveries are included.
    /// </summary>
    internal static LiveryKey GetRandomPmcLivery(AircraftDefinition def)
    {
        try
        {
            var options = new List<(LiveryKey key, string label)>();
            AircraftSelectionMenu.GetLiveryOptions(options, def, null, true);

            if (options.Count == 0)
                return new LiveryKey(0);

            // Prefer custom skins (workshop/AppData)
            var customSkins = new List<LiveryKey>();
            var builtinSkins = new List<LiveryKey>();
            foreach (var opt in options)
            {
                if (opt.key.Type != LiveryKey.KeyType.Builtin)
                    customSkins.Add(opt.key);
                else
                    builtinSkins.Add(opt.key);
            }

            if (customSkins.Count > 0)
                return customSkins[UnityEngine.Random.Range(0, customSkins.Count)];

            // Fall back to ALL built-in skins (BDF + PALA + neutral)
            if (builtinSkins.Count > 0)
                return builtinSkins[UnityEngine.Random.Range(0, builtinSkins.Count)];
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"GetRandomPmcLivery failed: {ex.Message}");
        }

        return new LiveryKey(0);
    }

    internal static void ModifySupply(UnitDefinition def, int amount)
    {
        if (def is AircraftDefinition)
        {
            if (AircraftSupplyOwn.ContainsKey(def))
                AircraftSupplyOwn[def] = Math.Max(0, AircraftSupplyOwn[def] + amount);
            else if (amount > 0)
                AircraftSupplyOwn[def] = amount;
        }
        else if (def is VehicleDefinition)
        {
            if (VehicleSupplyOwn.ContainsKey(def))
                VehicleSupplyOwn[def] = Math.Max(0, VehicleSupplyOwn[def] + amount);
            else if (amount > 0)
                VehicleSupplyOwn[def] = amount;
        }
    }
}
