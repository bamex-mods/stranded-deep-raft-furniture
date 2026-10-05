using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using Bamex.StrandedDeep.ModSettings;
using HarmonyLib;
using UnityEngine;
using Beam;
using Beam.Crafting;
using Rewired;

namespace BamEx.StrandedDeep.RaftFurniture
{
    [BepInPlugin(
        "bamex.strandeddeep.raftfurniture",
        "BamEx Raft Furniture",
        "0.2.14"
    )]
    [BepInDependency(
        "com.bamex.strandeddeep.modsettings",
        BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance;
        internal static BepInEx.Logging.ManualLogSource Log;

        private Harmony _harmony;
        private float _nextFurnitureCheck;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Logger.LogInfo("BamEx Raft Furniture v0.2.14 loading");

            RaftFurnitureSettings.Initialize(Config);
            RaftFurnitureSettings.RegisterModSettings();

            _harmony = new Harmony("bamex.strandeddeep.raftfurniture");
            _harmony.PatchAll();

            RaftAttachmentRegistry.Initialize();
            RaftAttachmentRegistry.InstallNativeSaveHook(_harmony);
            RaftAttachmentDiagnosticRegistry.Initialize();

            Logger.LogInfo("BamEx Raft Furniture loaded");
            Logger.LogInfo("Raft floor layer: 31");
            Logger.LogInfo("Enabled: persistent raft attachment + universal deck-plane reposition");
        }

        private void Update()
        {
            // Reposition input must be sampled every frame; the legacy maintenance
            // below remains intentionally throttled to once per second.
            try
            {
                RaftRepositionController.UpdateInput();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Raft reposition input failed: " + ex.Message);
            }

            if (Time.unscaledTime < _nextFurnitureCheck)
                return;

            _nextFurnitureCheck = Time.unscaledTime + 1.0f;

            try
            {
                // Diagnostic tick runs before the legacy recovery/physics maintenance.
                // If another game system has just dropped the parent link or changed
                // Rigidbody state, we want to record that transition before legacy v0.1.10
                // repairs it and hides the evidence.
                RaftAttachmentRegistry.Tick();
                RaftAttachmentDiagnosticRegistry.Tick();

                RaftFurnitureRuntime.MaintainAttachedConstructionObjects();
                RaftFurnitureRuntime.MaintainAttachedTerrainConstructions();
                RaftFurnitureRuntime.MaintainCampfireFollowers();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Raft furniture maintenance failed: " + ex.Message);
            }
        }

        private void FixedUpdate()
        {
            try
            {
                RaftAttachmentRegistry.FollowAttachedFurniture();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Raft attachment fixed follower failed: " + ex.Message);
            }
        }

        private void LateUpdate()
        {
            try
            {
                RaftAttachmentRegistry.FollowAttachedFurniture();
                RaftRepositionController.LateTick();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Raft attachment/reposition late update failed: " + ex.Message);
            }
        }

        internal void QueueAfterDrag(ConstructionObject obj)
        {
            if (obj == null)
                return;

            StartCoroutine(AfterDragSequence(obj));
        }

        private IEnumerator AfterDragSequence(ConstructionObject obj)
        {
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();

            if (obj == null)
                yield break;

            RaftFurnitureRuntime.HandleConstructionObjectDropped(obj);
        }

        private void OnDestroy()
        {
            RaftAttachmentDiagnosticRegistry.Shutdown();
            RaftAttachmentRegistry.Shutdown();
            RaftFurnitureSettings.UnregisterModSettings();

            if (_harmony != null)
                _harmony.UnpatchSelf();

            if (ReferenceEquals(Instance, this))
                Instance = null;
        }
    }

    internal static class RaftFurnitureSettings
    {
        private const string ModId = "raftfurniture";

        private static ConfigEntry<bool> _allowWaterStillOnRaft;
        private static ConfigEntry<bool> _allowCampfireStructuresOnRaft;
        private static ConfigEntry<bool> _allowLightHookOnRaft;
        private static ConfigEntry<bool> _allowContainerShelfReposition;
        private static ConfigEntry<bool> _allowManualReposition;

        internal static bool AllowWaterStillOnRaft
        {
            get { return _allowWaterStillOnRaft == null || _allowWaterStillOnRaft.Value; }
        }

        internal static bool AllowCampfireStructuresOnRaft
        {
            get { return _allowCampfireStructuresOnRaft == null || _allowCampfireStructuresOnRaft.Value; }
        }

        internal static bool AllowLightHookOnRaft
        {
            get { return _allowLightHookOnRaft == null || _allowLightHookOnRaft.Value; }
        }

        internal static bool AllowContainerShelfReposition
        {
            get { return _allowContainerShelfReposition == null || _allowContainerShelfReposition.Value; }
        }

        internal static bool AllowManualReposition
        {
            get { return _allowManualReposition != null && _allowManualReposition.Value; }
        }

        internal static void Initialize(ConfigFile config)
        {
            if (config == null)
                return;

            _allowWaterStillOnRaft = config.Bind(
                "Raft Furniture",
                "AllowWaterStillOnRaft",
                true,
                "Разрешить размещение и свободное перемещение Water Still на плоту.");

            _allowCampfireStructuresOnRaft = config.Bind(
                "Raft Furniture",
                "AllowCampfireStructuresOnRaft",
                true,
                "Разрешить Camp Fire / Fire Pit / Smoker на плоту и их свободное перемещение.");

            _allowLightHookOnRaft = config.Bind(
                "Raft Furniture",
                "AllowLightHookOnRaft",
                true,
                "Разрешить размещение и свободное перемещение Light Hook на плоту.");

            _allowContainerShelfReposition = config.Bind(
                "Raft Furniture",
                "AllowContainerShelfReposition",
                true,
                "Разрешить свободное перемещение штатной Container Shelf. Штатное строительство полки не отключается.");

            _allowManualReposition = config.Bind(
                "Raft Furniture",
                "AllowManualReposition",
                false,
                "Разрешить ручное перемещение raft-объектов через custom L2/LT reposition. Не влияет на placement, attachment, follower или persistence.");
        }

        internal static void RegisterModSettings()
        {
            try
            {
                ModSettingsClient.RegisterModLocalized(
                    ModId,
                    "ПЛОТ — МЕБЕЛЬ И ПОСТРОЙКИ",
                    "RAFT FURNITURE & STRUCTURES",
                    500);

                ModSettingsClient.AddToggleLocalized(
                    ModId,
                    "water_still",
                    "Водосборник на плоту",
                    "Water Still on Raft",
                    100,
                    "Вкл.",
                    "On",
                    "Выкл.",
                    "Off",
                    delegate { return AllowWaterStillOnRaft; },
                    delegate(bool value) { _allowWaterStillOnRaft.Value = value; });

                ModSettingsClient.AddToggleLocalized(
                    ModId,
                    "campfire_structures",
                    "Костёр и коптильня на плоту",
                    "Camp Fire Structures on Raft",
                    200,
                    "Вкл.",
                    "On",
                    "Выкл.",
                    "Off",
                    delegate { return AllowCampfireStructuresOnRaft; },
                    delegate(bool value) { _allowCampfireStructuresOnRaft.Value = value; });

                ModSettingsClient.AddToggleLocalized(
                    ModId,
                    "light_hook",
                    "Крюк для факела на плоту",
                    "Light Hook on Raft",
                    300,
                    "Вкл.",
                    "On",
                    "Выкл.",
                    "Off",
                    delegate { return AllowLightHookOnRaft; },
                    delegate(bool value) { _allowLightHookOnRaft.Value = value; });

                ModSettingsClient.AddToggleLocalized(
                    ModId,
                    "container_shelf_reposition",
                    "Свободное перемещение полок",
                    "Free Container Shelf Repositioning",
                    400,
                    "Вкл.",
                    "On",
                    "Выкл.",
                    "Off",
                    delegate { return AllowContainerShelfReposition; },
                    delegate(bool value) { _allowContainerShelfReposition.Value = value; });

                ModSettingsClient.AddToggleLocalized(
                    ModId,
                    "manual_reposition",
                    "Ручное перемещение объектов",
                    "Manual Object Repositioning",
                    500,
                    "Вкл.",
                    "On",
                    "Выкл.",
                    "Off",
                    delegate { return AllowManualReposition; },
                    delegate(bool value)
                    {
                        _allowManualReposition.Value = value;
                        if (!value)
                            RaftRepositionController.CancelActiveFromExternal("MANUAL_REPOSITION_DISABLED");
                    });

                ModSettingsClient.AddButtonLocalized(
                    ModId,
                    "emergency_upright",
                    "Аварийно выровнять плот",
                    "Emergency Raft Upright",
                    600,
                    "Выровнять",
                    "LEVEL RAFT",
                    delegate
                    {
                        RaftEmergencyRecovery.LevelNearestRaft();
                    });
            }
            catch (Exception ex)
            {
                if (Plugin.Log != null)
                    Plugin.Log.LogWarning("Mod Settings registration failed: " + ex.Message);
            }
        }

        internal static void UnregisterModSettings()
        {
            try
            {
                ModSettingsClient.RemoveMod(ModId);
            }
            catch
            {
            }
        }
    }

    internal static class RaftFurnitureRuntime
    {
        internal const int RaftFloorLayer = 31;
        internal const int RaftFloorMask = unchecked((int)0x80000000);

        private static readonly FieldInfo SnapBaseLayerMaskField =
            AccessTools.Field(typeof(PlacementModifier_SnapBase), "_layermask");

        private static readonly FieldInfo CheckSnappedLayerMaskField =
            AccessTools.Field(typeof(ConnectorCheck_CheckSnapped), "_layerMask");

        private static readonly FieldInfo StructureDisableGhostCollisionOnSnapField =
            AccessTools.Field(
                typeof(Construction_STRUCTURE),
                "_disableGhostCollisionOnSnap"
            );

        private static readonly PropertyInfo StructureProperty =
            AccessTools.Property(typeof(Constructing), "Structure");

        private static readonly PropertyInfo SnappedConnectorProperty =
            AccessTools.Property(typeof(Constructing), "SnappedConnector");

        private static readonly PropertyInfo TransformParentProperty =
            AccessTools.Property(typeof(BaseObject), "TransformParent");

        private static readonly MethodInfo ParentMethod =
            AccessTools.Method(typeof(BaseObject), "Parent");

        private static readonly PropertyInfo IsDraggingProperty =
            AccessTools.Property(typeof(InteractiveObject), "IsDragging");

        private static readonly FieldInfo CrafterGhostField =
            AccessTools.Field(typeof(Crafter), "_ghost");

        private static readonly FieldInfo CrafterPlacingCraftableField =
            AccessTools.Field(typeof(Crafter), "_placingCraftable");

        private static Crafter[] CachedCrafters =
            new Crafter[0];

        private static float NextCrafterCacheRefresh;

        private static readonly HashSet<int> SmokerGhostOverrideLog =
            new HashSet<int>();

        private static readonly FieldInfo ConstructingPlacementModifiersField =
            AccessTools.Field(typeof(Constructing), "_placementModifiers");

        private static readonly FieldInfo ConstructionObjectPlacementModifiersField =
            AccessTools.Field(typeof(ConstructionObject), "_placementModifiers");

        private static readonly FieldInfo ConstructingRotationModeField =
            AccessTools.Field(typeof(Constructing), "_rotationMode");

        private static readonly FieldInfo ConstructingSnappedField =
            AccessTools.Field(typeof(Constructing), "_snapped");

        private static readonly FieldInfo ConstructingSnappedConnectorField =
            AccessTools.Field(typeof(Constructing), "_snappedConnector");

        private static readonly FieldInfo ConstructingPrevSnappedConnectorField =
            AccessTools.Field(typeof(Constructing), "_prevSnappedConnector");

        private static readonly PropertyInfo BeamRayBaseRayProperty =
            AccessTools.Property(typeof(BeamRay), "BaseRay");

        private static readonly PropertyInfo BeamRayLengthProperty =
            AccessTools.Property(typeof(BeamRay), "Length");

        private static readonly MethodInfo SetCollisionForConstructionsMethod =
            AccessTools.Method(
                AccessTools.TypeByName("Beam.Crafting.GhostExtensions"),
                "SetCollisionForConstructions"
            );

        private static readonly HashSet<int> GhostCollisionLog =
            new HashSet<int>();

        private static readonly HashSet<int> SmallStructureSnapLog =
            new HashSet<int>();

        private static readonly FieldInfo CampfireCookablesField =
            AccessTools.Field(typeof(Construction_CAMPFIRE), "_cookables");

        private static readonly FieldInfo CampfireAttachedPositionField =
            AccessTools.Field(typeof(Construction_CAMPFIRE), "_attachedPosition");

        private static readonly FieldInfo SmokerAttachingPositionsField =
            AccessTools.Field(typeof(Constructing_SMOKER), "_attachingPositions");

        private static readonly HashSet<int> ReanchoredCookableLog =
            new HashSet<int>();

        private static readonly HashSet<int> RepairedSmallStructureLog =
            new HashSet<int>();

        private static readonly HashSet<int> RecoveredFurnitureLog =
            new HashSet<int>();

        private static readonly Dictionary<int, Construction_STRUCTURE_RAFT> FurnitureDragOriginRafts =
            new Dictionary<int, Construction_STRUCTURE_RAFT>();

        private static readonly Dictionary<int, Transform> CookableTargetCache =
            new Dictionary<int, Transform>();

        internal struct LayerMaskState
        {
            public bool Changed;
            public int OldValue;
        }

        internal struct SmokerOrderState
        {
            public bool Changed;
            public IList<Constructing> List;
            public Construction_CAMPFIRE Fire;
            public int OriginalIndex;
        }

        internal static bool IsAllowedPlacementObject(GameObject go)
        {
            if (go == null)
                return false;

            Constructing constructing = go.GetComponent<Constructing>();
            if (constructing != null &&
                IsAllowedTerrainConstructing(constructing) &&
                IsRaftPlacementEnabled(constructing))
            {
                return true;
            }

            ConstructionObject constructionObject = go.GetComponent<ConstructionObject>();
            if (constructionObject != null && IsAllowedFurnitureObject(constructionObject))
                return true;

            return false;
        }

        internal static bool IsAllowedTerrainConstructing(Constructing constructing)
        {
            if (constructing == null)
                return false;

            if (constructing is Construction_CAMPFIRE)
                return true;

            if (constructing is Beam.Constructing_STILL)
                return true;

            Constructing_HOOK hook = constructing as Constructing_HOOK;
            if (hook != null && IsLightHook(hook))
                return true;

            return false;
        }

        internal static bool IsRaftPlacementEnabled(Constructing constructing)
        {
            if (constructing == null)
                return false;

            if (constructing is Construction_CAMPFIRE)
                return RaftFurnitureSettings.AllowCampfireStructuresOnRaft;

            if (constructing is Beam.Constructing_STILL)
                return RaftFurnitureSettings.AllowWaterStillOnRaft;

            Constructing_HOOK hook = constructing as Constructing_HOOK;
            if (hook != null && IsLightHook(hook))
                return RaftFurnitureSettings.AllowLightHookOnRaft;

            return true;
        }

        internal static bool IsRepositionEnabled(Constructing constructing)
        {
            return IsRaftPlacementEnabled(constructing);
        }

        internal static bool IsAllowedFurnitureObject(ConstructionObject obj)
        {
            if (obj == null || obj.gameObject == null)
                return false;

            string name = obj.gameObject.name ?? String.Empty;
            string upperName = name.ToUpperInvariant();

            if (upperName.Contains("WOOD_TABLE") ||
                upperName.Contains("PLANK_TABLE") ||
                upperName.Contains("CORRUGATED_TABLE"))
                return true;

            try
            {
                string crafting = obj.CraftingType.ToString().ToUpperInvariant();

                if (crafting.Contains("INTERACTIVE_TYPE_CHAIR") ||
                    crafting.Contains("INTERACTIVE_TYPE_TABLE"))
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static bool IsLightHook(Constructing_HOOK hook)
        {
            if (hook == null || hook.gameObject == null)
                return false;

            string name = hook.gameObject.name ?? String.Empty;
            return name.IndexOf("LIGHT_HOOK", StringComparison.OrdinalIgnoreCase) >= 0;
        }


        private static Construction_STRUCTURE_RAFT GetRaftFromHit(
            RaycastHit hit)
        {
            if (hit.collider == null)
                return null;

            Construction_RAFT raftPiece =
                hit.collider.GetComponent<Construction_RAFT>();

            if (raftPiece == null)
                raftPiece =
                    hit.collider.GetComponentInParent<Construction_RAFT>();

            if (raftPiece == null)
                return null;

            return GetStructure(raftPiece) as Construction_STRUCTURE_RAFT;
        }

        private static void IgnoreGhostCollisionWithRaftDeck(
            Transform ghostTransform,
            Construction_STRUCTURE_RAFT raft)
        {
            if (ghostTransform == null || raft == null)
                return;

            try
            {
                Ghost ghost = ghostTransform.GetComponent<Ghost>();
                if (ghost == null)
                    return;

                List<Constructing> raftDeckPieces =
                    new List<Constructing>();

                IList<Constructing> constructions = raft.Constructions;

                if (constructions != null)
                {
                    for (int i = 0; i < constructions.Count; i++)
                    {
                        Constructing c = constructions[i];

                        if (c == null)
                            continue;

                        if (c is Construction_RAFT)
                            raftDeckPieces.Add(c);
                    }
                }

                if (raftDeckPieces.Count == 0)
                    return;

                if (SetCollisionForConstructionsMethod != null)
                {
                    SetCollisionForConstructionsMethod.Invoke(
                        null,
                        new object[]
                        {
                            ghost,
                            raftDeckPieces,
                            true
                        }
                    );
                }
                else
                {
                    Collider[] ghostColliders =
                        ghostTransform.GetComponentsInChildren<Collider>(true);

                    Collider[] raftColliders =
                        raft.transform.GetComponentsInChildren<Collider>(true);

                    for (int g = 0; g < ghostColliders.Length; g++)
                    {
                        Collider ghostCollider = ghostColliders[g];
                        if (ghostCollider == null)
                            continue;

                        for (int r = 0; r < raftColliders.Length; r++)
                        {
                            Collider raftCollider = raftColliders[r];

                            if (raftCollider == null ||
                                raftCollider.gameObject.layer != RaftFloorLayer)
                                continue;

                            Physics.IgnoreCollision(
                                ghostCollider,
                                raftCollider,
                                true
                            );
                        }
                    }
                }

                int ghostId = ghost.GetInstanceID();

                if (GhostCollisionLog.Add(ghostId))
                {
                    Info(
                        "Ignoring ghost collision with " +
                        raftDeckPieces.Count +
                        " raft deck pieces for " +
                        ghostTransform.gameObject.name
                    );
                }
            }
            catch (Exception ex)
            {
                Warn(
                    "Could not ignore raft-deck ghost collision: " +
                    ex.Message
                );
            }
        }

        internal static bool TryHandleConstructingRaftPlacement(
            Constructing constructing,
            Transform ghost,
            BeamRay ray,
            float yRotation,
            out bool result)
        {
            result = false;

            if (constructing == null ||
                ghost == null ||
                !IsAllowedTerrainConstructing(constructing) ||
                !IsRaftPlacementEnabled(constructing))
                return false;

            RaycastHit raftHit;
            if (!TryRaycastRaftFloor(ray, out raftHit))
                return false;

            Construction_STRUCTURE_RAFT raft =
                GetRaftFromHit(raftHit);

            if (raft == null)
                return false;

            IgnoreGhostCollisionWithRaftDeck(
                ghost,
                raft
            );

            Beam.Crafting.Placement placement = new Beam.Crafting.Placement();
            placement.Position = ghost.position;
            placement.Rotation = ghost.rotation;

            try
            {
                Beam.Crafting.PlacementModifier[] modifiers =
                    ConstructingPlacementModifiersField != null
                        ? ConstructingPlacementModifiersField.GetValue(constructing) as Beam.Crafting.PlacementModifier[]
                        : null;

                if (modifiers != null)
                {
                    for (int i = 0; i < modifiers.Length; i++)
                    {
                        Beam.Crafting.PlacementModifier modifier = modifiers[i];
                        if (modifier != null)
                            placement = modifier.Modify(placement, ray);
                    }
                }
            }
            catch (Exception ex)
            {
                Warn("Free raft placement modifiers failed for " +
                     SafeName(constructing) + ": " + ex.Message);

                placement.Position = raftHit.point;
                placement.Rotation = Quaternion.FromToRotation(Vector3.up, raftHit.normal);
            }

            Quaternion finalRotation = placement.Rotation;

            if (ShouldApplyConstructingYRotation(constructing))
                finalRotation = finalRotation * Quaternion.Euler(0.0f, yRotation, 0.0f);

            ghost.position = placement.Position;
            ghost.rotation = finalRotation;

            ClearConstructingSnapState(constructing);

            result = true;
            return true;
        }

        internal static bool TryHandleFurnitureRaftPlacement(
            ConstructionObject obj,
            Transform ghost,
            BeamRay ray,
            float yRotation,
            out bool result)
        {
            result = false;

            if (obj == null ||
                ghost == null ||
                !IsAllowedFurnitureObject(obj))
                return false;

            RaycastHit raftHit;
            if (!TryRaycastRaftFloor(ray, out raftHit))
                return false;

            Construction_STRUCTURE_RAFT raft =
                GetRaftFromHit(raftHit);

            if (raft == null)
                return false;

            IgnoreGhostCollisionWithRaftDeck(
                ghost,
                raft
            );

            Beam.Crafting.Placement placement = new Beam.Crafting.Placement();
            placement.Position = ghost.position;
            placement.Rotation = ghost.rotation;

            try
            {
                Beam.Crafting.PlacementModifier[] modifiers =
                    ConstructionObjectPlacementModifiersField != null
                        ? ConstructionObjectPlacementModifiersField.GetValue(obj) as Beam.Crafting.PlacementModifier[]
                        : null;

                if (modifiers != null)
                {
                    for (int i = 0; i < modifiers.Length; i++)
                    {
                        Beam.Crafting.PlacementModifier modifier = modifiers[i];
                        if (modifier != null)
                            placement = modifier.Modify(placement, ray);
                    }
                }
            }
            catch (Exception ex)
            {
                Warn("Free raft furniture placement modifiers failed for " +
                     SafeName(obj) + ": " + ex.Message);

                placement.Position = raftHit.point;
                placement.Rotation = Quaternion.FromToRotation(Vector3.up, raftHit.normal);
            }

            ghost.position = placement.Position;
            ghost.rotation =
                placement.Rotation *
                Quaternion.Euler(0.0f, yRotation, 0.0f);

            result = true;
            return true;
        }

        private static bool TryRaycastRaftFloor(
            BeamRay beamRay,
            out RaycastHit hit)
        {
            hit = new RaycastHit();

            try
            {
                if (BeamRayBaseRayProperty == null ||
                    BeamRayLengthProperty == null)
                    return false;

                object boxedRay = beamRay;

                Ray ray =
                    (Ray)BeamRayBaseRayProperty.GetValue(boxedRay, null);

                float length =
                    Convert.ToSingle(
                        BeamRayLengthProperty.GetValue(boxedRay, null)
                    );

                return Physics.Raycast(
                    ray,
                    out hit,
                    length,
                    RaftFloorMask,
                    QueryTriggerInteraction.Ignore
                );
            }
            catch
            {
                return false;
            }
        }

        private static bool ShouldApplyConstructingYRotation(
            Constructing constructing)
        {
            if (constructing == null || ConstructingRotationModeField == null)
                return true;

            try
            {
                object value =
                    ConstructingRotationModeField.GetValue(constructing);

                return Convert.ToInt32(value) != 0;
            }
            catch
            {
                return true;
            }
        }

        private static void ClearConstructingSnapState(
            Constructing constructing)
        {
            if (constructing == null)
                return;

            try
            {
                if (ConstructingSnappedField != null)
                    ConstructingSnappedField.SetValue(constructing, false);

                if (ConstructingSnappedConnectorField != null)
                    ConstructingSnappedConnectorField.SetValue(constructing, null);

                if (ConstructingPrevSnappedConnectorField != null)
                    ConstructingPrevSnappedConnectorField.SetValue(constructing, null);
            }
            catch
            {
            }
        }

        internal static void ExtendSnapToTerrainMask(
            PlacementModifier_SnapToTerrain modifier,
            ref LayerMaskState state)
        {
            state = new LayerMaskState();

            if (modifier == null ||
                SnapBaseLayerMaskField == null ||
                !IsAllowedPlacementObject(modifier.gameObject))
                return;

            try
            {
                LayerMask oldMask = (LayerMask)SnapBaseLayerMaskField.GetValue(modifier);
                int oldValue = oldMask.value;
                int newValue = oldValue | RaftFloorMask;

                if (newValue == oldValue)
                    return;

                LayerMask expanded = oldMask;
                expanded.value = newValue;

                state.Changed = true;
                state.OldValue = oldValue;

                SnapBaseLayerMaskField.SetValue(modifier, expanded);
            }
            catch (Exception ex)
            {
                Warn("Could not extend SnapToTerrain mask: " + ex.Message);
            }
        }

        internal static void RestoreSnapToTerrainMask(
            PlacementModifier_SnapToTerrain modifier,
            LayerMaskState state)
        {
            if (!state.Changed || modifier == null || SnapBaseLayerMaskField == null)
                return;

            try
            {
                LayerMask mask = (LayerMask)SnapBaseLayerMaskField.GetValue(modifier);
                mask.value = state.OldValue;
                SnapBaseLayerMaskField.SetValue(modifier, mask);
            }
            catch
            {
            }
        }

        internal static void ExtendCheckSnappedMask(
            ConnectorCheck_CheckSnapped check,
            ref LayerMaskState state)
        {
            state = new LayerMaskState();

            if (check == null ||
                CheckSnappedLayerMaskField == null ||
                !IsAllowedPlacementObject(check.gameObject))
                return;

            try
            {
                LayerMask oldMask = (LayerMask)CheckSnappedLayerMaskField.GetValue(check);
                int oldValue = oldMask.value;
                int newValue = oldValue | RaftFloorMask;

                if (newValue == oldValue)
                    return;

                LayerMask expanded = oldMask;
                expanded.value = newValue;

                state.Changed = true;
                state.OldValue = oldValue;

                CheckSnappedLayerMaskField.SetValue(check, expanded);
            }
            catch (Exception ex)
            {
                Warn("Could not extend CheckSnapped mask: " + ex.Message);
            }
        }

        internal static void RestoreCheckSnappedMask(
            ConnectorCheck_CheckSnapped check,
            LayerMaskState state)
        {
            if (!state.Changed || check == null || CheckSnappedLayerMaskField == null)
                return;

            try
            {
                LayerMask mask = (LayerMask)CheckSnappedLayerMaskField.GetValue(check);
                mask.value = state.OldValue;
                CheckSnappedLayerMaskField.SetValue(check, mask);
            }
            catch
            {
            }
        }

        internal static Construction_STRUCTURE_RAFT FindRaftBelow(Vector3 position)
        {
            RaycastHit hit;

            Vector3 origin = position + Vector3.up * 1.5f;

            if (!Physics.Raycast(
                    origin,
                    Vector3.down,
                    out hit,
                    4.0f,
                    RaftFloorMask,
                    QueryTriggerInteraction.Ignore))
                return null;

            if (hit.collider == null || hit.collider.gameObject.layer != RaftFloorLayer)
                return null;

            Construction_RAFT raftPiece = hit.collider.GetComponent<Construction_RAFT>();

            if (raftPiece == null)
                raftPiece = hit.collider.GetComponentInParent<Construction_RAFT>();

            if (raftPiece == null)
                return null;

            Construction_STRUCTURE structure = GetStructure(raftPiece);
            return structure as Construction_STRUCTURE_RAFT;
        }

        internal static Construction_STRUCTURE GetStructure(Constructing constructing)
        {
            if (constructing == null || StructureProperty == null)
                return null;

            try
            {
                return StructureProperty.GetValue(constructing, null) as Construction_STRUCTURE;
            }
            catch
            {
                return null;
            }
        }


        internal static Construction_STRUCTURE_RAFT FindParentRaftStructure(
            Transform transform)
        {
            if (transform == null)
                return null;

            Transform current = transform.parent;

            while (current != null)
            {
                Construction_STRUCTURE_RAFT raft =
                    current.GetComponent<Construction_STRUCTURE_RAFT>();

                if (raft != null)
                    return raft;

                current = current.parent;
            }

            return null;
        }

        private static bool IsCampfireUpgrade(Constructing constructing)
        {
            return constructing is Construction_PIT ||
                   constructing is Constructing_SMOKER ||
                   constructing is Constructing_SPIT;
        }

        internal static void SetStructure(
            Constructing constructing,
            Construction_STRUCTURE structure)
        {
            if (constructing == null || StructureProperty == null)
                return;

            try
            {
                StructureProperty.SetValue(constructing, structure, null);
            }
            catch (Exception ex)
            {
                Warn("Could not set Structure for " + SafeName(constructing) + ": " + ex.Message);
            }
        }

        internal static Connector GetSnappedConnector(Constructing constructing)
        {
            if (constructing == null || SnappedConnectorProperty == null)
                return null;

            try
            {
                return SnappedConnectorProperty.GetValue(constructing, null) as Connector;
            }
            catch
            {
                return null;
            }
        }

        internal static void PrepareConstructingForRaft(Constructing constructing)
        {
            if (constructing == null ||
                !IsAllowedTerrainConstructing(constructing) ||
                !IsRaftPlacementEnabled(constructing))
            {
                return;
            }

            // Camp Fire must keep its vanilla STRUCTURE_SMALL.
            // Fire Pit and Smoker depend on that exact structure chain.
            if (constructing is Construction_CAMPFIRE)
                return;

            Construction_STRUCTURE_RAFT raft =
                FindRaftBelow(constructing.transform.position);

            if (raft == null)
                return;

            SetStructure(constructing, raft);
        }

        internal static void RepairSmallStructurePoseOnRaft(
            Construction_STRUCTURE smallStructure,
            Construction_STRUCTURE_RAFT raft)
        {
            if (smallStructure == null || raft == null)
                return;

            try
            {
                Vector3 originalPosition =
                    smallStructure.transform.position;

                RaycastHit[] hits =
                    Physics.RaycastAll(
                        originalPosition + Vector3.up * 3.0f,
                        Vector3.down,
                        8.0f,
                        RaftFloorMask,
                        QueryTriggerInteraction.Ignore
                    );

                if (hits == null || hits.Length == 0)
                    return;

                Array.Sort(
                    hits,
                    delegate(RaycastHit a, RaycastHit b)
                    {
                        return a.distance.CompareTo(b.distance);
                    }
                );

                RaycastHit? selected = null;

                for (int i = 0; i < hits.Length; i++)
                {
                    Construction_STRUCTURE_RAFT hitRaft =
                        GetRaftFromHit(hits[i]);

                    if (hitRaft == raft)
                    {
                        selected = hits[i];
                        break;
                    }
                }

                if (!selected.HasValue)
                    return;

                RaycastHit hit =
                    selected.Value;

                // PlacementModifier_SnapToTerrain in this game uses:
                // hit.point.y + _snapOffset.y, where the default prefab
                // snap offset is (0, 0.1, 0). Re-apply the same rule
                // after Load, when buoyancy may already have moved the raft.
                Vector3 repairedPosition =
                    originalPosition;

                repairedPosition.y =
                    hit.point.y + 0.1f;

                smallStructure.transform.position =
                    repairedPosition;

                // Re-align the construction to the current raft plane while
                // preserving its visual heading as closely as possible.
                Vector3 up =
                    raft.transform.up;

                Vector3 forward =
                    Vector3.ProjectOnPlane(
                        smallStructure.transform.forward,
                        up
                    );

                if (forward.sqrMagnitude < 0.0001f)
                {
                    forward =
                        Vector3.ProjectOnPlane(
                            raft.transform.forward,
                            up
                        );
                }

                if (forward.sqrMagnitude >= 0.0001f)
                {
                    smallStructure.transform.rotation =
                        Quaternion.LookRotation(
                            forward.normalized,
                            up
                        );
                }

                float delta =
                    Vector3.Distance(
                        originalPosition,
                        repairedPosition
                    );

                int id =
                    smallStructure.GetInstanceID();

                if (delta > 0.01f &&
                    RepairedSmallStructureLog.Add(id))
                {
                    Info(
                        "Repaired raft STRUCTURE_SMALL deck pose after load: " +
                        smallStructure.gameObject.name +
                        " delta=" +
                        delta.ToString("F3")
                    );
                }
            }
            catch (Exception ex)
            {
                Warn(
                    "Could not repair raft STRUCTURE_SMALL deck pose: " +
                    ex.Message
                );
            }
        }

        private static bool SameCookableObject(
            object candidate,
            Component cooking)
        {
            if (candidate == null || cooking == null)
                return false;

            if (ReferenceEquals(candidate, cooking))
                return true;

            Component component =
                candidate as Component;

            if (component != null &&
                component.gameObject == cooking.gameObject)
                return true;

            GameObject gameObject =
                candidate as GameObject;

            if (gameObject != null &&
                gameObject == cooking.gameObject)
                return true;

            return false;
        }

        private static Transform GetTransformFromAttachmentSlot(
            object slot,
            Component cooking,
            out bool slotMatchesCookable)
        {
            slotMatchesCookable = false;

            if (slot == null)
                return null;

            Transform target = null;

            try
            {
                Type type =
                    slot.GetType();

                FieldInfo[] fields =
                    type.GetFields(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic
                    );

                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field =
                        fields[i];

                    object value;

                    try
                    {
                        value =
                            field.GetValue(slot);
                    }
                    catch
                    {
                        continue;
                    }

                    Transform transform =
                        value as Transform;

                    if (transform != null)
                        target = transform;

                    if (SameCookableObject(
                            value,
                            cooking))
                    {
                        slotMatchesCookable = true;
                    }
                }

                PropertyInfo[] properties =
                    type.GetProperties(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic
                    );

                for (int i = 0; i < properties.Length; i++)
                {
                    PropertyInfo property =
                        properties[i];

                    if (property.GetIndexParameters().Length != 0 ||
                        property.GetGetMethod(true) == null)
                        continue;

                    object value;

                    try
                    {
                        value =
                            property.GetValue(
                                slot,
                                null
                            );
                    }
                    catch
                    {
                        continue;
                    }

                    Transform transform =
                        value as Transform;

                    if (transform != null)
                        target = transform;

                    if (SameCookableObject(
                            value,
                            cooking))
                    {
                        slotMatchesCookable = true;
                    }
                }
            }
            catch
            {
            }

            return target;
        }

        private static List<Transform> GetSmokerAttachmentTargets(
            Constructing_SMOKER smoker,
            Component cooking,
            out Transform exactTarget)
        {
            exactTarget = null;

            List<Transform> targets =
                new List<Transform>();

            if (smoker == null)
                return targets;

            try
            {
                IEnumerable slots =
                    SmokerAttachingPositionsField != null
                        ? SmokerAttachingPositionsField.GetValue(smoker) as IEnumerable
                        : null;

                if (slots != null)
                {
                    foreach (object slot in slots)
                    {
                        bool matches;

                        Transform target =
                            GetTransformFromAttachmentSlot(
                                slot,
                                cooking,
                                out matches
                            );

                        if (target != null &&
                            !targets.Contains(target))
                        {
                            targets.Add(target);
                        }

                        if (matches &&
                            target != null)
                        {
                            exactTarget =
                                target;
                        }
                    }
                }
            }
            catch
            {
            }

            // Reflection fallback: the prefab has a dedicated
            // "AttachingPoints" transform. Use its children in stable order.
            if (targets.Count == 0)
            {
                Transform container =
                    smoker.transform.Find("AttachingPoints");

                if (container != null)
                {
                    for (int i = 0; i < container.childCount; i++)
                    {
                        Transform child =
                            container.GetChild(i);

                        if (child != null)
                            targets.Add(child);
                    }
                }
            }

            return targets;
        }

        internal static void SyncAttachedCookables(
            Construction_STRUCTURE smallStructure)
        {
            if (smallStructure == null ||
                smallStructure.Constructions == null)
                return;

            Construction_CAMPFIRE fire = null;
            Constructing_SMOKER smoker = null;

            for (int i = 0; i < smallStructure.Constructions.Count; i++)
            {
                Constructing constructing =
                    smallStructure.Constructions[i];

                if (constructing is Construction_CAMPFIRE)
                    fire = constructing as Construction_CAMPFIRE;

                if (constructing is Constructing_SMOKER)
                    smoker = constructing as Constructing_SMOKER;
            }

            if (fire == null ||
                CampfireCookablesField == null)
                return;

            IEnumerable cookables = null;

            try
            {
                cookables =
                    CampfireCookablesField.GetValue(fire)
                    as IEnumerable;
            }
            catch
            {
                return;
            }

            if (cookables == null)
                return;

            Transform fireTarget = null;

            if (CampfireAttachedPositionField != null)
            {
                try
                {
                    fireTarget =
                        CampfireAttachedPositionField.GetValue(fire)
                        as Transform;
                }
                catch
                {
                }
            }

            List<Component> cookingObjects =
                new List<Component>();

            foreach (object item in cookables)
            {
                Component cooking =
                    item as Component;

                if (cooking != null &&
                    cooking.gameObject != null)
                {
                    cookingObjects.Add(cooking);
                }
            }

            if (cookingObjects.Count == 0)
                return;

            HashSet<int> activeCookableIds =
                new HashSet<int>();

            for (int i = 0; i < cookingObjects.Count; i++)
            {
                Component cooking =
                    cookingObjects[i];

                int cookingId =
                    cooking.GetInstanceID();

                activeCookableIds.Add(
                    cookingId
                );

                Transform target = null;

                if (!CookableTargetCache.TryGetValue(
                        cookingId,
                        out target) ||
                    target == null)
                {
                    Transform exactTarget;

                    List<Transform> smokerTargets =
                        GetSmokerAttachmentTargets(
                            smoker,
                            cooking,
                            out exactTarget
                        );

                    target =
                        exactTarget;

                    if (target == null &&
                        smokerTargets.Count > 0)
                    {
                        // Stable fallback if the game's slot object does not
                        // expose the attached cookable reference.
                        target =
                            smokerTargets[
                                i % smokerTargets.Count
                            ];
                    }

                    if (target == null)
                        target = fireTarget;

                    if (target != null)
                    {
                        CookableTargetCache[cookingId] =
                            target;
                    }
                }

                if (target == null)
                    continue;

                Transform cookableTransform =
                    cooking.transform;

                float distance =
                    Vector3.Distance(
                        cookableTransform.position,
                        target.position
                    );

                // The vanilla game keeps attached food as a separate object
                // in SaveContainer. On a static island that is fine; on our
                // moving raft it must be explicitly kept on the station.
                cookableTransform.position =
                    target.position;

                cookableTransform.rotation =
                    target.rotation;

                Rigidbody rb =
                    cooking.GetComponent<Rigidbody>();

                if (rb != null)
                {
                    rb.velocity =
                        Vector3.zero;

                    rb.angularVelocity =
                        Vector3.zero;

                    rb.isKinematic =
                        true;

                    rb.useGravity =
                        false;
                }

                if (distance > 0.15f)
                {
                    int id =
                        cooking.GetInstanceID();

                    if (ReanchoredCookableLog.Add(id))
                    {
                        Info(
                            "Re-anchored raft cookable " +
                            cooking.gameObject.name +
                            " to cooking station; distance=" +
                            distance.ToString("F3")
                        );
                    }
                }
            }

            // A removed/picked-up food item disappears from _cookables.
            // Drop its cached target so re-attaching the same object to a
            // different slot resolves the new attachment correctly.
            List<int> staleIds =
                new List<int>();

            foreach (KeyValuePair<int, Transform> pair in CookableTargetCache)
            {
                if (!activeCookableIds.Contains(pair.Key))
                    staleIds.Add(pair.Key);
            }

            for (int i = 0; i < staleIds.Count; i++)
                CookableTargetCache.Remove(staleIds[i]);
        }

        internal static Construction_STRUCTURE_RAFT GetRaftForSmallStructure(
            Construction_STRUCTURE smallStructure)
        {
            if (smallStructure == null)
                return null;

            RaftSmallStructureFollower follower =
                smallStructure.GetComponent<RaftSmallStructureFollower>();

            if (follower != null && follower.Raft != null)
                return follower.Raft;

            Construction_STRUCTURE_RAFT parentRaft =
                FindParentRaftStructure(smallStructure.transform);

            if (parentRaft != null)
                return parentRaft;

            if (smallStructure.Constructions != null)
            {
                for (int i = 0; i < smallStructure.Constructions.Count; i++)
                {
                    Constructing c = smallStructure.Constructions[i];

                    if (c == null)
                        continue;

                    Construction_STRUCTURE_RAFT raft =
                        FindRaftBelow(c.transform.position);

                    if (raft != null)
                        return raft;
                }
            }

            return null;
        }

        private static void RestoreVanillaSmallStructureParent(
            Construction_STRUCTURE smallStructure,
            Construction_STRUCTURE_RAFT raft)
        {
            if (smallStructure == null || raft == null)
                return;

            if (!smallStructure.transform.IsChildOf(raft.transform))
                return;

            try
            {
                Vector3 worldPosition = smallStructure.transform.position;
                Quaternion worldRotation = smallStructure.transform.rotation;

                // Vanilla STRUCTURE_SMALL:
                // parent = SaveContainer (same parent as STRUCTURE_RAFT)
                // TransformParent = null
                Transform vanillaParent = raft.transform.parent;

                smallStructure.transform.SetParent(
                    vanillaParent,
                    true
                );

                if (TransformParentProperty != null)
                    TransformParentProperty.SetValue(
                        smallStructure,
                        null,
                        null
                    );

                smallStructure.transform.position = worldPosition;
                smallStructure.transform.rotation = worldRotation;
                smallStructure.transform.localScale = Vector3.one;

                Info(
                    "Migrated STRUCTURE_SMALL out of raft hierarchy: " +
                    smallStructure.gameObject.name
                );
            }
            catch (Exception ex)
            {
                Warn(
                    "Could not restore vanilla STRUCTURE_SMALL parent: " +
                    ex.Message
                );
            }
        }

        internal static void AttachSmallStructureFollower(
            Construction_STRUCTURE smallStructure,
            Construction_STRUCTURE_RAFT raft)
        {
            if (smallStructure == null || raft == null)
                return;

            RestoreVanillaSmallStructureParent(
                smallStructure,
                raft
            );

            IgnoreBuiltCollisionWithRaft(
                smallStructure.transform,
                raft,
                true
            );

            RaftSmallStructureFollower follower =
                smallStructure.GetComponent<RaftSmallStructureFollower>();

            if (follower == null)
                follower =
                    smallStructure.gameObject.AddComponent<RaftSmallStructureFollower>();

            follower.Initialize(
                smallStructure,
                raft
            );
        }

        internal static void MaintainCampfireFollowers()
        {
            Construction_CAMPFIRE[] fires =
                UnityEngine.Object.FindObjectsOfType<Construction_CAMPFIRE>();

            for (int i = 0; i < fires.Length; i++)
            {
                Construction_CAMPFIRE fire = fires[i];

                if (fire == null)
                    continue;

                Construction_STRUCTURE smallStructure =
                    GetStructure(fire);

                if (smallStructure == null ||
                    smallStructure is Construction_STRUCTURE_RAFT)
                    continue;

                Construction_STRUCTURE_RAFT raft =
                    GetRaftForSmallStructure(smallStructure);

                if (raft == null)
                    continue;

                AttachSmallStructureFollower(
                    smallStructure,
                    raft
                );
            }
        }

        internal static void MaintainAttachedTerrainConstructions()
        {
            Constructing_HOOK[] hooks =
                UnityEngine.Object.FindObjectsOfType<Constructing_HOOK>();

            for (int i = 0; i < hooks.Length; i++)
            {
                Constructing_HOOK hook = hooks[i];

                if (hook == null || !IsLightHook(hook))
                    continue;

                Construction_STRUCTURE_RAFT raft =
                    FindParentRaftStructure(hook.transform);

                if (raft != null)
                    IgnoreBuiltCollisionWithRaft(
                        hook.transform,
                        raft,
                        true
                    );
            }

            Beam.Constructing_STILL[] stills =
                UnityEngine.Object.FindObjectsOfType<Beam.Constructing_STILL>();

            for (int i = 0; i < stills.Length; i++)
            {
                Beam.Constructing_STILL still = stills[i];

                if (still == null)
                    continue;

                Construction_STRUCTURE_RAFT raft =
                    FindParentRaftStructure(still.transform);

                if (raft != null)
                    IgnoreBuiltCollisionWithRaft(
                        still.transform,
                        raft,
                        true
                    );
            }
        }

        internal static void FinalizeConstructingOnRaft(Constructing constructing)

        {
            if (constructing == null || !IsAllowedTerrainConstructing(constructing))
                return;

            Construction_STRUCTURE_RAFT raft =
                FindRaftBelow(constructing.transform.position);

            if (constructing is Construction_CAMPFIRE)
            {
                if (raft == null)
                    return;

                Construction_STRUCTURE smallStructure =
                    GetStructure(constructing);

                if (smallStructure == null ||
                    smallStructure is Construction_STRUCTURE_RAFT)
                {
                    Warn(
                        "Camp Fire did not create vanilla STRUCTURE_SMALL; " +
                        "leaving it untouched."
                    );
                    return;
                }

                // Physics-safe architecture:
                // keep STRUCTURE_SMALL in its vanilla SaveContainer hierarchy.
                // A runtime follower moves it with the raft.
                // Its kinematic child rigidbodies never collide with the raft.
                AttachSmallStructureFollower(
                    smallStructure,
                    raft
                );

                Info(
                    "Attached Camp Fire STRUCTURE_SMALL to raft follower: " +
                    SafeName(constructing)
                );
                return;
            }

            if (raft == null)
                raft = GetStructure(constructing) as Construction_STRUCTURE_RAFT;

            if (raft == null)
                return;

            SetStructure(constructing, raft);

            IgnoreBuiltCollisionWithRaft(
                constructing.transform,
                raft,
                true
            );

            EnsureBaseObjectParent(constructing, raft.transform);
            EnsureConstructionListed(raft, constructing);
            AlignToRaft(constructing.transform);

            IgnoreBuiltCollisionWithRaft(
                constructing.transform,
                raft,
                true
            );

            Rigidbody rb = constructing.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
                rb.useGravity = false;
                rb.constraints = RigidbodyConstraints.None;
            }

            Info("Attached " + SafeName(constructing) + " to raft.");
        }

        private static void RefreshCrafterCacheIfNeeded()
        {
            if (Time.unscaledTime < NextCrafterCacheRefresh &&
                CachedCrafters != null &&
                CachedCrafters.Length > 0)
                return;

            NextCrafterCacheRefresh =
                Time.unscaledTime + 1.0f;

            try
            {
                CachedCrafters =
                    UnityEngine.Object.FindObjectsOfType<Crafter>();
            }
            catch
            {
                CachedCrafters =
                    new Crafter[0];
            }
        }

        internal static bool ShouldSuppressSmokerGhostCollision(
            Ghost ghost)
        {
            if (!RaftFurnitureSettings.AllowCampfireStructuresOnRaft)
                return false;

            if (ghost == null ||
                CrafterGhostField == null ||
                CrafterPlacingCraftableField == null)
                return false;

            RefreshCrafterCacheIfNeeded();

            for (int i = 0; i < CachedCrafters.Length; i++)
            {
                Crafter crafter = CachedCrafters[i];

                if (crafter == null)
                    continue;

                Ghost crafterGhost = null;
                object placingCraftable = null;

                try
                {
                    crafterGhost =
                        CrafterGhostField.GetValue(crafter) as Ghost;

                    if (crafterGhost != ghost)
                        continue;

                    placingCraftable =
                        CrafterPlacingCraftableField.GetValue(crafter);
                }
                catch
                {
                    continue;
                }

                Constructing_SMOKER smoker =
                    placingCraftable as Constructing_SMOKER;

                if (smoker == null ||
                    !smoker.IsSnapped)
                    return false;

                Connector snappedConnector =
                    GetSnappedConnector(smoker);

                if (snappedConnector == null ||
                    snappedConnector.Constructing == null)
                    return false;

                Construction_PIT firePit =
                    snappedConnector.Constructing as Construction_PIT;

                if (firePit == null)
                    return false;

                Construction_STRUCTURE smallStructure =
                    GetStructure(firePit);

                if (smallStructure == null ||
                    smallStructure is Construction_STRUCTURE_RAFT)
                    return false;

                Construction_STRUCTURE_RAFT raft =
                    GetRaftForSmallStructure(smallStructure);

                if (raft == null)
                    return false;

                // Keep the real collision exclusions from v0.1f active.
                // This does not touch the raft hierarchy or rigidbodies.
                HandleUpgradeGhostOnRaft(
                    smoker,
                    ghost.transform
                );

                int ghostId = ghost.GetInstanceID();

                if (SmokerGhostOverrideLog.Add(ghostId))
                {
                    Info(
                        "Suppressing Ghost.Colliding for raft Smoker snapped to Fire Pit: ghost #" +
                        ghostId
                    );
                }

                return true;
            }

            return false;
        }

        internal static void HandleUpgradeGhostOnRaft(
            Constructing constructing,
            Transform ghost)
        {
            if (!RaftFurnitureSettings.AllowCampfireStructuresOnRaft)
                return;

            if (constructing == null ||
                ghost == null ||
                !IsCampfireUpgrade(constructing))
                return;

            Connector snappedConnector =
                GetSnappedConnector(constructing);

            if (snappedConnector == null ||
                snappedConnector.Constructing == null)
                return;

            Construction_STRUCTURE smallStructure =
                GetStructure(snappedConnector.Constructing);

            if (smallStructure == null)
                return;

            Construction_STRUCTURE_RAFT raft =
                GetRaftForSmallStructure(smallStructure);

            if (raft == null)
                return;

            IgnoreGhostCollisionWithRaftDeck(
                ghost,
                raft
            );

            IgnoreGhostCollisionWithSmallStructure(
                ghost,
                smallStructure
            );
        }

        private static void IgnoreGhostCollisionWithSmallStructure(
            Transform ghostTransform,
            Construction_STRUCTURE smallStructure)
        {
            if (ghostTransform == null || smallStructure == null)
                return;

            try
            {
                Ghost ghost = ghostTransform.GetComponent<Ghost>();

                if (ghost == null ||
                    SetCollisionForConstructionsMethod == null ||
                    smallStructure.Constructions == null)
                    return;

                SetCollisionForConstructionsMethod.Invoke(
                    null,
                    new object[]
                    {
                        ghost,
                        smallStructure.Constructions,
                        true
                    }
                );
            }
            catch (Exception ex)
            {
                Warn(
                    "Could not ignore ghost collision with STRUCTURE_SMALL: " +
                    ex.Message
                );
            }
        }

        internal static void IgnoreBuiltCollisionWithRaft(
            Transform objectRoot,
            Construction_STRUCTURE_RAFT raft,
            bool ignore)
        {
            if (objectRoot == null || raft == null)
                return;

            try
            {
                Collider[] objectColliders =
                    objectRoot.GetComponentsInChildren<Collider>(true);

                if (objectColliders == null ||
                    objectColliders.Length == 0)
                    return;

                IList<Constructing> constructions =
                    raft.Constructions;

                if (constructions == null)
                    return;

                for (int i = 0; i < constructions.Count; i++)
                {
                    Construction_RAFT raftPiece =
                        constructions[i] as Construction_RAFT;

                    if (raftPiece == null)
                        continue;

                    Collider[] raftColliders =
                        raftPiece.GetComponentsInChildren<Collider>(true);

                    for (int a = 0; a < objectColliders.Length; a++)
                    {
                        Collider objectCollider =
                            objectColliders[a];

                        if (objectCollider == null)
                            continue;

                        for (int b = 0; b < raftColliders.Length; b++)
                        {
                            Collider raftCollider =
                                raftColliders[b];

                            if (raftCollider == null ||
                                objectCollider == raftCollider)
                                continue;

                            Physics.IgnoreCollision(
                                objectCollider,
                                raftCollider,
                                ignore
                            );
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Warn(
                    "Could not update built collision with raft: " +
                    ex.Message
                );
            }
        }

        private static void EnsureConstructionListed(
            Construction_STRUCTURE_RAFT raft,
            Constructing constructing)
        {
            if (raft == null || constructing == null)
                return;

            try
            {
                IList<Constructing> list = raft.Constructions;
                if (list != null && !list.Contains(constructing))
                    raft.AddConstruction(constructing);
            }
            catch (Exception ex)
            {
                Warn("Could not add construction to raft list: " + ex.Message);
            }
        }

        internal static void AttachFurnitureObject(
            ConstructionObject obj,
            Construction_STRUCTURE_RAFT raft,
            string attachmentReason)
        {
            if (obj == null || raft == null || !IsAllowedFurnitureObject(obj))
                return;

            IgnoreBuiltCollisionWithRaft(
                obj.transform,
                raft,
                true
            );

            EnsureBaseObjectParent(obj, raft.transform);
            AlignToRaft(obj.transform);
            ForceFurniturePhysicsForRaft(obj);

            IgnoreBuiltCollisionWithRaft(
                obj.transform,
                raft,
                true
            );

            Info("Attached furniture " + SafeName(obj) + " to raft.");

            // HEURISTIC_RECOVERY is registered after deck-height repair below,
            // so a first-load record captures the corrected pose. If a record
            // already existed, the diagnostic registry intentionally preserves
            // its previous authoritative local pose instead of masking drift.
            if (!String.Equals(
                    attachmentReason,
                    "HEURISTIC_RECOVERY",
                    StringComparison.Ordinal))
            {
                RaftAttachmentDiagnosticRegistry.MarkAttached(
                    obj,
                    raft,
                    attachmentReason
                );

                RaftAttachmentRegistry.Attach(
                    obj,
                    raft,
                    attachmentReason
                );
            }
        }

        internal static void HandleConstructionObjectPlaced(ConstructionObject obj)
        {
            if (obj == null || !IsAllowedFurnitureObject(obj))
                return;

            Construction_STRUCTURE_RAFT raft = FindRaftBelow(obj.transform.position);
            if (raft != null)
                AttachFurnitureObject(obj, raft, "PLACED");
        }

        internal static void HandleConstructionObjectBeginDrag(InteractiveObject interactive)
        {
            ConstructionObject obj = interactive as ConstructionObject;

            if (obj == null || !IsAllowedFurnitureObject(obj))
                return;

            Construction_STRUCTURE_RAFT oldRaft =
                FindParentRaftStructure(obj.transform);

            if (oldRaft == null)
                oldRaft = RaftAttachmentRegistry.GetAttachedRaft(obj);

            if (oldRaft == null)
                return;

            int id = obj.GetInstanceID();

            if (oldRaft != null)
            {
                FurnitureDragOriginRafts[id] = oldRaft;

                // Keep collisions with the raft ignored for the whole drag.
                // Re-enabling them while the table is touching or slightly
                // intersecting the deck can inject a huge impulse into the
                // dynamic raft and flip it. If the object is dropped on land,
                // collision is restored after the drag has finished.
                IgnoreBuiltCollisionWithRaft(
                    obj.transform,
                    oldRaft,
                    true
                );
            }

            RaftAttachmentDiagnosticRegistry.MarkDragging(
                obj,
                oldRaft,
                "BEGIN_DRAG"
            );

            RaftAttachmentRegistry.BeginDrag(
                obj,
                oldRaft
            );

            DetachBaseObject(obj);

            Rigidbody rb = obj.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = false;
                rb.useGravity = true;
                rb.constraints = RigidbodyConstraints.None;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.WakeUp();
            }

            Info("Detached furniture for collision-safe drag: " + SafeName(obj));
        }

        internal static void HandleConstructionObjectEndDrag(InteractiveObject interactive)
        {
            ConstructionObject obj = interactive as ConstructionObject;

            if (obj == null || !IsAllowedFurnitureObject(obj))
                return;

            if (Plugin.Instance != null)
                Plugin.Instance.QueueAfterDrag(obj);
        }

        internal static void HandleConstructionObjectDropped(ConstructionObject obj)
        {
            if (obj == null || !IsAllowedFurnitureObject(obj))
                return;

            int id = obj.GetInstanceID();
            Construction_STRUCTURE_RAFT oldRaft = null;
            FurnitureDragOriginRafts.TryGetValue(id, out oldRaft);
            FurnitureDragOriginRafts.Remove(id);

            Construction_STRUCTURE_RAFT raft = FindRaftBelow(obj.transform.position);

            if (raft != null)
            {
                if (oldRaft != null && oldRaft != raft)
                {
                    IgnoreBuiltCollisionWithRaft(
                        obj.transform,
                        oldRaft,
                        false
                    );
                }

                AttachFurnitureObject(obj, raft, "DROP_RAFT");
                return;
            }

            if (oldRaft != null)
            {
                // The object is now clear of the deck, so restoring normal
                // collision cannot kick the raft.
                IgnoreBuiltCollisionWithRaft(
                    obj.transform,
                    oldRaft,
                    false
                );
            }

            if (IsParentedToRaft(obj.transform))
                DetachBaseObject(obj);

            RaftAttachmentDiagnosticRegistry.MarkDetached(
                obj,
                oldRaft,
                "DROP_OFF_RAFT"
            );

            RaftAttachmentRegistry.Detach(
                obj,
                oldRaft,
                "DROP_OFF_RAFT"
            );
        }

        internal static void MaintainAttachedConstructionObjects()
        {
            ConstructionObject[] objects =
                UnityEngine.Object.FindObjectsOfType<ConstructionObject>();

            for (int i = 0; i < objects.Length; i++)
            {
                ConstructionObject obj = objects[i];

                if (obj == null || !IsAllowedFurnitureObject(obj))
                    continue;

                RaftAttachmentRegistry.Observe(obj);
                RaftAttachmentDiagnosticRegistry.Observe(obj);

                if (IsDragging(obj))
                    continue;

                // v0.2.0c: once the registry knows an object, the persistent
                // attachment state is authoritative. Transform parenting and
                // FindRaftBelow() are no longer allowed to overwrite it.
                //
                // Attached records are already materialized by Observe()/Tick()
                // and followed in FixedUpdate/LateUpdate. Detached records stay
                // detached until an explicit drop back onto a raft calls Attach().
                if (RaftAttachmentRegistry.HasRecord(obj))
                    continue;

                if (IsParentedToRaft(obj.transform))
                {
                    // Migration path for legacy furniture that already belongs
                    // to a raft through the old parent hierarchy but has no
                    // persistent v0.2 attachment record yet. Capture that
                    // association once, then let the registry own it.
                    Construction_STRUCTURE_RAFT parentRaft =
                        FindParentRaftStructure(obj.transform);

                    if (parentRaft != null)
                    {
                        RaftAttachmentDiagnosticRegistry.MarkAttached(
                            obj,
                            parentRaft,
                            "LEGACY_PARENT_MIGRATION"
                        );

                        RaftAttachmentRegistry.Attach(
                            obj,
                            parentRaft,
                            "LEGACY_PARENT_MIGRATION"
                        );

                        Info(
                            "Migrated parented raft furniture into persistent attachment registry: " +
                            SafeName(obj));
                    }

                    continue;
                }

                // Last-resort migration for old saves that predate persistent
                // association and have neither a registry record nor a raft
                // parent. Raycast is geometry discovery only; after this one
                // migration the new registry becomes authoritative.
                Construction_STRUCTURE_RAFT recoveredRaft =
                    FindRaftBelow(obj.transform.position);

                if (recoveredRaft != null)
                {
                    AttachFurnitureObject(
                        obj,
                        recoveredRaft,
                        "HEURISTIC_RECOVERY"
                    );
                    RepairRecoveredFurnitureDeckHeight(obj, recoveredRaft);

                    RaftAttachmentDiagnosticRegistry.MarkAttached(
                        obj,
                        recoveredRaft,
                        "HEURISTIC_RECOVERY"
                    );

                    RaftAttachmentRegistry.Attach(
                        obj,
                        recoveredRaft,
                        "HEURISTIC_RECOVERY"
                    );

                    int id = obj.GetInstanceID();
                    if (RecoveredFurnitureLog.Add(id))
                    {
                        Info(
                            "Recovered legacy unparented raft furniture after load: " +
                            SafeName(obj));
                    }
                }
            }
        }

        private static void RepairRecoveredFurnitureDeckHeight(
            ConstructionObject obj,
            Construction_STRUCTURE_RAFT raft)
        {
            if (obj == null || raft == null)
                return;

            RaycastHit hit;
            Vector3 raftUp = raft.transform.up.normalized;
            Vector3 origin = obj.transform.position + raftUp * 2.5f;

            if (!Physics.Raycast(
                    origin,
                    -raftUp,
                    out hit,
                    6.0f,
                    RaftFloorMask,
                    QueryTriggerInteraction.Ignore))
            {
                return;
            }

            Construction_STRUCTURE_RAFT hitRaft = GetRaftFromHit(hit);
            if (hitRaft != raft)
                return;

            float supportProjection;

            if (!TryGetFurnitureBottomProjection(
                    obj.transform,
                    raftUp,
                    out supportProjection))
            {
                return;
            }

            float deckProjection = Vector3.Dot(hit.point, raftUp);
            float delta = deckProjection - supportProjection + 0.015f;

            // Ignore tiny numerical noise, and reject absurd repairs rather
            // than teleporting a badly-corrupted object by metres.
            if (Mathf.Abs(delta) < 0.005f)
                return;

            if (Mathf.Abs(delta) > 2.0f)
            {
                Warn(
                    "Rejected recovered furniture deck-height repair for " +
                    SafeName(obj) + " delta=" + delta.ToString("F3"));
                return;
            }

            obj.transform.position += raftUp * delta;
            Physics.SyncTransforms();
            ForceFurniturePhysicsForRaft(obj);

            Info(
                "Repaired recovered furniture deck height: " +
                SafeName(obj) + " delta=" + delta.ToString("F3"));
        }

        internal static bool TryGetFurnitureBottomProjection(
            Transform root,
            Vector3 axis,
            out float minimumProjection)
        {
            minimumProjection = float.PositiveInfinity;

            if (root == null)
                return false;

            Collider[] colliders =
                root.GetComponentsInChildren<Collider>(true);

            bool found = false;

            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];

                if (collider == null ||
                    !collider.enabled ||
                    collider.isTrigger)
                {
                    continue;
                }

                BoxCollider box = collider as BoxCollider;

                if (box != null)
                {
                    Vector3 half = box.size * 0.5f;

                    for (int sx = -1; sx <= 1; sx += 2)
                    {
                        for (int sy = -1; sy <= 1; sy += 2)
                        {
                            for (int sz = -1; sz <= 1; sz += 2)
                            {
                                Vector3 local = box.center +
                                    new Vector3(
                                        half.x * sx,
                                        half.y * sy,
                                        half.z * sz);

                                Vector3 world =
                                    box.transform.TransformPoint(local);

                                float projection =
                                    Vector3.Dot(world, axis);

                                if (projection < minimumProjection)
                                    minimumProjection = projection;
                            }
                        }
                    }

                    found = true;
                    continue;
                }

                Bounds bounds = collider.bounds;
                Vector3 center = bounds.center;
                Vector3 ext = bounds.extents;

                for (int sx = -1; sx <= 1; sx += 2)
                {
                    for (int sy = -1; sy <= 1; sy += 2)
                    {
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            Vector3 world = center +
                                new Vector3(
                                    ext.x * sx,
                                    ext.y * sy,
                                    ext.z * sz);

                            float projection =
                                Vector3.Dot(world, axis);

                            if (projection < minimumProjection)
                                minimumProjection = projection;
                        }
                    }
                }

                found = true;
            }

            return found;
        }

        internal static bool TrySnapTransformToRaftDeck(
            Transform root,
            Construction_STRUCTURE_RAFT raft,
            Vector3 desiredWorldPoint,
            float localYaw,
            float clearance,
            out RaycastHit deckHit)
        {
            deckHit = new RaycastHit();

            if (root == null || raft == null)
                return false;

            Vector3 raftUp = raft.transform.up.normalized;
            Vector3 origin = desiredWorldPoint + raftUp * 3.0f;

            if (!Physics.Raycast(
                    origin,
                    -raftUp,
                    out deckHit,
                    8.0f,
                    RaftFloorMask,
                    QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            Construction_STRUCTURE_RAFT hitRaft =
                GetRaftFromHit(deckHit);

            if (hitRaft != raft)
                return false;

            root.rotation =
                raft.transform.rotation *
                Quaternion.Euler(0.0f, localYaw, 0.0f);

            // Put the pivot safely above the hit first, then solve the exact
            // collider-bottom-to-deck offset along raft.up. This makes height
            // independent from whatever vertical position vanilla drag produced.
            root.position = deckHit.point + raftUp * 2.0f;
            Physics.SyncTransforms();

            float bottomProjection;
            if (TryGetFurnitureBottomProjection(
                    root,
                    raftUp,
                    out bottomProjection))
            {
                float deckProjection =
                    Vector3.Dot(deckHit.point, raftUp);

                float delta =
                    deckProjection - bottomProjection + clearance;

                root.position += raftUp * delta;
            }
            else
            {
                root.position = deckHit.point + raftUp * clearance;
            }

            Physics.SyncTransforms();
            return true;
        }

        internal static float GetLocalYaw(
            Transform root,
            Construction_STRUCTURE_RAFT raft)
        {
            if (root == null || raft == null)
                return 0.0f;

            Quaternion localRotation =
                Quaternion.Inverse(raft.transform.rotation) *
                root.rotation;

            float yaw = Mathf.Repeat(localRotation.eulerAngles.y, 360.0f);
            if (yaw > 180.0f)
                yaw -= 360.0f;

            return yaw;
        }

        internal static Construction_CAMPFIRE FindCampfireInStructure(
            Construction_STRUCTURE structure)
        {
            if (structure == null || structure.Constructions == null)
                return null;

            for (int i = 0; i < structure.Constructions.Count; i++)
            {
                Construction_CAMPFIRE fire =
                    structure.Constructions[i] as Construction_CAMPFIRE;

                if (fire != null)
                    return fire;
            }

            return null;
        }

        internal static bool IsContainerShelfTransform(Transform transform)
        {
            if (transform == null)
                return false;

            string name = transform.name ?? String.Empty;
            string upper = name.ToUpperInvariant();

            if ((upper.Contains("CONTAINER") && upper.Contains("SHELF")) ||
                upper.Contains("CONTAINERSHELF"))
            {
                return true;
            }

            Component[] components = transform.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null)
                    continue;

                string typeName = component.GetType().Name;
                if (String.Equals(
                        typeName,
                        "VehicleConnector_Container_Shelf",
                        StringComparison.OrdinalIgnoreCase) ||
                    typeName.IndexOf(
                        "Container_Shelf",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsDragging(InteractiveObject interactive)
        {
            if (interactive == null || IsDraggingProperty == null)
                return false;

            try
            {
                object value = IsDraggingProperty.GetValue(interactive, null);
                if (value is bool)
                    return (bool)value;
            }
            catch
            {
            }

            return false;
        }

        private static bool IsParentedToRaft(Transform transform)
        {
            if (transform == null)
                return false;

            Transform current = transform.parent;

            while (current != null)
            {
                if (current.GetComponent<Construction_STRUCTURE_RAFT>() != null)
                    return true;

                current = current.parent;
            }

            return false;
        }

        internal static void ForceFurniturePhysicsForRaft(ConstructionObject obj)
        {
            Rigidbody rb = obj.GetComponent<Rigidbody>();
            if (rb == null)
                return;

            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.constraints = RigidbodyConstraints.None;
            rb.Sleep();
        }

        internal static void EnsureBaseObjectParent(BaseObject obj, Transform parent)
        {
            if (obj == null || parent == null)
                return;

            try
            {
                if (obj.transform.parent == parent)
                    return;

                Vector3 worldPosition = obj.transform.position;
                Quaternion worldRotation = obj.transform.rotation;

                obj.transform.SetParent(null, true);

                if (TransformParentProperty != null)
                    TransformParentProperty.SetValue(obj, parent, null);

                if (ParentMethod != null)
                    ParentMethod.Invoke(obj, null);
                else
                    obj.transform.SetParent(parent, true);

                obj.transform.position = worldPosition;
                obj.transform.rotation = worldRotation;
                obj.transform.localScale = Vector3.one;
            }
            catch (Exception ex)
            {
                Warn("Parenting failed for " + SafeName(obj) + ": " + ex.Message);

                try
                {
                    obj.transform.SetParent(parent, true);
                }
                catch
                {
                }
            }
        }

        private static void DetachBaseObject(BaseObject obj)
        {
            if (obj == null)
                return;

            try
            {
                Vector3 worldPosition = obj.transform.position;
                Quaternion worldRotation = obj.transform.rotation;

                obj.transform.SetParent(null, true);

                if (TransformParentProperty != null)
                    TransformParentProperty.SetValue(obj, null, null);

                if (ParentMethod != null)
                    ParentMethod.Invoke(obj, null);

                obj.transform.position = worldPosition;
                obj.transform.rotation = worldRotation;
                obj.transform.localScale = Vector3.one;
            }
            catch (Exception ex)
            {
                Warn("Detach failed for " + SafeName(obj) + ": " + ex.Message);

                try
                {
                    obj.transform.SetParent(null, true);
                }
                catch
                {
                }
            }
        }

        private static void AlignToRaft(Transform transform)
        {
            if (transform == null || transform.parent == null)
                return;

            Vector3 localEuler = transform.localEulerAngles;
            transform.localRotation = Quaternion.Euler(0.0f, localEuler.y, 0.0f);
        }

        internal static Construction_CAMPFIRE FindSnappedCampfire(Constructing constructing)
        {
            Constructing current = constructing;
            HashSet<int> visited = new HashSet<int>();

            for (int i = 0; i < 8; i++)
            {
                if (current == null)
                    return null;

                if (!visited.Add(current.GetInstanceID()))
                    return null;

                Construction_CAMPFIRE fire = current as Construction_CAMPFIRE;
                if (fire != null)
                    return fire;

                Connector connector = GetSnappedConnector(current);
                if (connector == null)
                    return null;

                current = connector.Constructing;
            }

            return null;
        }

        internal static void PrepareSmokerOrder(
            Constructing_SMOKER smoker,
            ref SmokerOrderState state)
        {
            state = new SmokerOrderState();

            if (smoker == null)
                return;

            Construction_CAMPFIRE fire = FindSnappedCampfire(smoker);
            if (fire == null)
                return;

            Construction_STRUCTURE_RAFT raft =
                GetStructure(fire) as Construction_STRUCTURE_RAFT;

            if (raft == null)
                return;

            IList<Constructing> list = raft.Constructions;
            if (list == null)
                return;

            int index = list.IndexOf(fire);
            if (index < 0)
                return;

            int firstFireIndex = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is Construction_CAMPFIRE)
                {
                    firstFireIndex = i;
                    break;
                }
            }

            if (firstFireIndex < 0 || firstFireIndex == index)
                return;

            state.Changed = true;
            state.List = list;
            state.Fire = fire;
            state.OriginalIndex = index;

            list.Remove(fire);
            list.Insert(firstFireIndex, fire);
        }

        internal static void RestoreSmokerOrder(SmokerOrderState state)
        {
            if (!state.Changed ||
                state.List == null ||
                state.Fire == null)
                return;

            try
            {
                state.List.Remove(state.Fire);

                int index = state.OriginalIndex;
                if (index < 0)
                    index = 0;
                if (index > state.List.Count)
                    index = state.List.Count;

                state.List.Insert(index, state.Fire);
            }
            catch
            {
            }
        }

        private static string SafeName(UnityEngine.Object obj)
        {
            if (obj == null)
                return "<null>";

            return obj.name + " #" + obj.GetInstanceID();
        }

        private static void Info(string message)
        {
            if (Plugin.Log != null)
                Plugin.Log.LogInfo("[RaftFurniture] " + message);
        }

        private static void Warn(string message)
        {
            if (Plugin.Log != null)
                Plugin.Log.LogWarning("[RaftFurniture] " + message);
        }
    }

    internal static class RaftEmergencyRecovery
    {
        private const float MaxRecoveryDistance = 100.0f;

        internal static void LevelNearestRaft()
        {
            try
            {
                float distance;
                Construction_STRUCTURE_RAFT raft =
                    FindNearestRaft(out distance);

                if (raft == null)
                {
                    Emit(
                        "NO_RAFT",
                        "No suitable raft found near either local player."
                    );
                    return;
                }

                if (distance > MaxRecoveryDistance)
                {
                    Emit(
                        "RAFT_TOO_FAR",
                        "nearestDistance=" +
                        distance.ToString("F2", CultureInfo.InvariantCulture)
                    );
                    return;
                }

                RaftRepositionController.CancelActiveFromExternal(
                    "EMERGENCY_UPRIGHT"
                );

                Rigidbody rb = raft.GetComponent<Rigidbody>();

                if (rb == null)
                    rb = raft.GetComponentInParent<Rigidbody>();

                Transform root =
                    rb != null
                        ? rb.transform
                        : raft.transform;

                if (root == null)
                {
                    Emit("NO_ROOT", "Raft root transform is unavailable.");
                    return;
                }

                Vector3 position = root.position;
                Vector3 oldEuler = root.eulerAngles;

                Vector3 flatForward =
                    Vector3.ProjectOnPlane(
                        root.forward,
                        Vector3.up
                    );

                Quaternion upright;

                if (flatForward.sqrMagnitude > 0.001f)
                {
                    upright = Quaternion.LookRotation(
                        flatForward.normalized,
                        Vector3.up
                    );
                }
                else
                {
                    upright = Quaternion.Euler(
                        0.0f,
                        root.eulerAngles.y,
                        0.0f
                    );
                }

                float oldSpeed =
                    rb != null
                        ? rb.velocity.magnitude
                        : 0.0f;

                float oldAngularSpeed =
                    rb != null
                        ? rb.angularVelocity.magnitude
                        : 0.0f;

                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.position = position;
                    rb.rotation = upright;
                }

                root.position = position;
                root.rotation = upright;

                Physics.SyncTransforms();

                // Attached furniture uses raft-local poses, so refresh it
                // immediately without modifying attachment records.
                RaftAttachmentRegistry.FollowAttachedFurniture();

                Emit(
                    "UPRIGHT",
                    "raftInstance=" + raft.GetInstanceID() +
                    " distance=" +
                    distance.ToString("F2", CultureInfo.InvariantCulture) +
                    " position=" + FormatVector(position) +
                    " oldEuler=" + FormatVector(oldEuler) +
                    " newEuler=" + FormatVector(root.eulerAngles) +
                    " stoppedSpeed=" +
                    oldSpeed.ToString("F2", CultureInfo.InvariantCulture) +
                    " stoppedAngularSpeed=" +
                    oldAngularSpeed.ToString("F2", CultureInfo.InvariantCulture)
                );
            }
            catch (Exception ex)
            {
                Emit(
                    "ERROR",
                    ex.GetType().Name + ": " + ex.Message
                );
            }
        }

        private static Construction_STRUCTURE_RAFT FindNearestRaft(
            out float distance)
        {
            distance = float.MaxValue;

            Construction_STRUCTURE_RAFT[] rafts =
                UnityEngine.Object.FindObjectsOfType<
                    Construction_STRUCTURE_RAFT>();

            if (rafts == null || rafts.Length == 0)
                return null;

            Camera[] cameras = Camera.allCameras;

            bool hasSplitGameplayCamera = false;

            if (cameras != null)
            {
                for (int i = 0; i < cameras.Length; i++)
                {
                    Camera camera = cameras[i];

                    if (!IsUsableCamera(camera))
                        continue;

                    if (IsSplitCamera(camera))
                    {
                        hasSplitGameplayCamera = true;
                        break;
                    }
                }
            }

            Construction_STRUCTURE_RAFT best = null;
            float bestSqr = float.MaxValue;

            if (cameras != null)
            {
                for (int r = 0; r < rafts.Length; r++)
                {
                    Construction_STRUCTURE_RAFT raft = rafts[r];

                    if (raft == null)
                        continue;

                    for (int c = 0; c < cameras.Length; c++)
                    {
                        Camera camera = cameras[c];

                        if (!IsUsableCamera(camera))
                            continue;

                        if (hasSplitGameplayCamera &&
                            !IsSplitCamera(camera))
                        {
                            continue;
                        }

                        float sqr =
                            (
                                raft.transform.position -
                                camera.transform.position
                            ).sqrMagnitude;

                        if (sqr < bestSqr)
                        {
                            bestSqr = sqr;
                            best = raft;
                        }
                    }
                }
            }

            if (best == null && Camera.main != null)
            {
                for (int r = 0; r < rafts.Length; r++)
                {
                    Construction_STRUCTURE_RAFT raft = rafts[r];

                    if (raft == null)
                        continue;

                    float sqr =
                        (
                            raft.transform.position -
                            Camera.main.transform.position
                        ).sqrMagnitude;

                    if (sqr < bestSqr)
                    {
                        bestSqr = sqr;
                        best = raft;
                    }
                }
            }

            if (best == null && rafts.Length == 1)
            {
                best = rafts[0];
                bestSqr = 0.0f;
            }

            if (best != null)
                distance = Mathf.Sqrt(bestSqr);

            return best;
        }

        private static bool IsUsableCamera(Camera camera)
        {
            return camera != null &&
                   camera.enabled &&
                   camera.targetTexture == null;
        }

        private static bool IsSplitCamera(Camera camera)
        {
            if (!IsUsableCamera(camera))
                return false;

            Rect rect = camera.rect;

            return rect.width < 0.99f ||
                   rect.height < 0.99f;
        }

        private static string FormatVector(Vector3 value)
        {
            return
                "(" +
                value.x.ToString("F3", CultureInfo.InvariantCulture) +
                "," +
                value.y.ToString("F3", CultureInfo.InvariantCulture) +
                "," +
                value.z.ToString("F3", CultureInfo.InvariantCulture) +
                ")";
        }

        private static void Emit(
            string eventName,
            string details)
        {
            string line =
                DateTime.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss.fff",
                    CultureInfo.InvariantCulture
                ) +
                " [" + eventName + "] " +
                details;

            if (Plugin.Log != null)
                Plugin.Log.LogWarning("[RaftEmergency] " + line);

            try
            {
                string directory = Path.Combine(
                    Paths.ConfigPath,
                    "StrandedDeepRaftFurniture"
                );

                Directory.CreateDirectory(directory);

                string file = Path.Combine(
                    directory,
                    "EmergencyRecovery.log"
                );

                File.AppendAllText(
                    file,
                    line + Environment.NewLine
                );
            }
            catch
            {
            }
        }
    }
    internal enum RaftAttachmentDiagnosticState
    {
        Attached,
        Dragging,
        Detached
    }

    internal sealed class RaftAttachmentDiagnosticRecord
    {
        internal string ObjectKey;
        internal string ObjectReferenceId;
        internal string RaftReferenceId;
        internal string PlacementProfileId;

        internal ConstructionObject Object;
        internal Construction_STRUCTURE_RAFT Raft;

        // v0.2 target semantics: X/Z + height + yaw relative to the raft.
        // For this diagnostic build HeightOffset is the captured local Y.
        internal float LocalX;
        internal float LocalZ;
        internal float HeightOffset;
        internal float LocalYaw;

        internal RaftAttachmentDiagnosticState State;
        internal int Revision;

        internal bool SnapshotInitialized;
        internal string LastParentPath;
        internal string LastParentRaftReferenceId;
        internal bool LastHasRigidbody;
        internal bool LastIsKinematic;
        internal bool LastUseGravity;
        internal string LastWatchdogSignature;
        internal bool MissingObjectLogged;
        internal bool MissingRaftLogged;
    }

    /// <summary>
    /// Diagnostic observer retained from v0.2.0a.
    ///
    /// It records an explicit furniture -> raft association and the captured
    /// raft-local surface pose, but it does NOT drive transforms, alter placement,
    /// or persist authority across process restarts yet. The legacy v0.1.10 code
    /// remains responsible for actual parenting/physics. This lets us catch the
    /// exact transition that destroys ownership without changing the phenomenon.
    /// </summary>
    internal static class RaftAttachmentDiagnosticRegistry
    {
        private const string DiagnosticProfileId = "FURNITURE_FREE_DECK_DIAGNOSTIC";
        private const float PositionErrorThreshold = 0.20f;
        private const float RotationErrorThreshold = 5.0f;

        private static readonly Dictionary<string, RaftAttachmentDiagnosticRecord> Records =
            new Dictionary<string, RaftAttachmentDiagnosticRecord>(StringComparer.OrdinalIgnoreCase);

        private static string DiagnosticDirectory;
        private static string DiagnosticFile;
        private static bool Initialized;

        internal static void Initialize()
        {
            if (Initialized)
                return;

            Initialized = true;

            try
            {
                DiagnosticDirectory = Path.Combine(
                    Paths.ConfigPath,
                    "StrandedDeepRaftFurniture"
                );

                Directory.CreateDirectory(DiagnosticDirectory);
                DiagnosticFile = Path.Combine(
                    DiagnosticDirectory,
                    "AttachmentDiagnostic.log"
                );
            }
            catch (Exception ex)
            {
                DiagnosticDirectory = null;
                DiagnosticFile = null;

                if (Plugin.Log != null)
                    Plugin.Log.LogWarning(
                        "[AttachmentDiag] Could not initialize diagnostic file: " +
                        ex.Message
                    );
            }

            EmitRaw("");
            EmitRaw("============================================================");
            EmitRaw("SESSION START v0.2.14 diagnostic observer " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            EmitRaw("Mode: observer only; production follower/persistence handled by RaftAttachmentRegistry");
            EmitRaw("============================================================");

            Emit(
                "INIT",
                "Attachment registry/watchdog initialized. file=" +
                (DiagnosticFile ?? "<unavailable>")
            );
        }

        internal static void Shutdown()
        {
            if (!Initialized)
                return;

            Emit("SESSION", "Shutdown. records=" + Records.Count);
            EmitRaw("SESSION END " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            Initialized = false;
        }

        internal static void Tick()
        {
            EnsureInitialized();

            if (Records.Count == 0)
                return;

            List<RaftAttachmentDiagnosticRecord> snapshot =
                new List<RaftAttachmentDiagnosticRecord>(Records.Values);

            for (int i = 0; i < snapshot.Count; i++)
            {
                RaftAttachmentDiagnosticRecord record = snapshot[i];
                if (record == null || record.State != RaftAttachmentDiagnosticState.Attached)
                    continue;

                ResolveMissingRuntimeObjects(record);

                if (record.Object == null)
                    continue;

                Evaluate(record, "TICK_PRE_MAINTENANCE");
            }
        }

        internal static void Observe(ConstructionObject obj)
        {
            EnsureInitialized();

            if (!IsDiagnosticFurniture(obj))
                return;

            string key = GetObjectKey(obj);
            RaftAttachmentDiagnosticRecord record;

            if (!Records.TryGetValue(key, out record))
            {
                Construction_STRUCTURE_RAFT parentRaft = FindParentRaft(obj.transform);

                if (parentRaft != null)
                {
                    MarkAttached(obj, parentRaft, "DISCOVER_PARENTED");
                }

                return;
            }

            if (record.Object != obj)
            {
                record.Object = obj;
                record.MissingObjectLogged = false;

                Emit(
                    "REBIND_OBJECT",
                    "object=" + record.ObjectReferenceId +
                    " instance=" + obj.GetInstanceID()
                );
            }

            ResolveMissingRuntimeObjects(record);
            Evaluate(record, "MAINTENANCE_OBSERVE");
        }

        internal static void MarkAttached(
            ConstructionObject obj,
            Construction_STRUCTURE_RAFT raft,
            string reason)
        {
            EnsureInitialized();

            if (!IsDiagnosticFurniture(obj) || raft == null)
                return;

            string key = GetObjectKey(obj);
            string objectReferenceId = GetReferenceId(obj);
            string raftReferenceId = GetReferenceId(raft);

            RaftAttachmentDiagnosticRecord record;
            bool existed = Records.TryGetValue(key, out record);

            if (!existed)
            {
                record = new RaftAttachmentDiagnosticRecord();
                record.ObjectKey = key;
                record.ObjectReferenceId = objectReferenceId;
                record.PlacementProfileId = DiagnosticProfileId;
                Records[key] = record;
            }

            bool preserveExistingPose =
                existed &&
                record.State == RaftAttachmentDiagnosticState.Attached &&
                String.Equals(
                    reason,
                    "HEURISTIC_RECOVERY",
                    StringComparison.Ordinal
                ) &&
                String.Equals(
                    record.RaftReferenceId,
                    raftReferenceId,
                    StringComparison.OrdinalIgnoreCase
                );

            record.Object = obj;
            record.Raft = raft;
            record.ObjectReferenceId = objectReferenceId;
            record.RaftReferenceId = raftReferenceId;
            record.State = RaftAttachmentDiagnosticState.Attached;
            record.Revision++;
            record.MissingObjectLogged = false;
            record.MissingRaftLogged = false;
            record.LastWatchdogSignature = null;

            if (!preserveExistingPose)
                CaptureSurfacePose(record, obj, raft);

            Emit(
                "ATTACHED",
                "reason=" + reason +
                " existing=" + existed +
                " preservePose=" + preserveExistingPose +
                " " + BuildContext(record, obj, raft)
            );

            CaptureSnapshot(record, obj);
        }

        internal static void MarkDragging(
            ConstructionObject obj,
            Construction_STRUCTURE_RAFT oldRaft,
            string reason)
        {
            EnsureInitialized();

            if (!IsDiagnosticFurniture(obj))
                return;

            string key = GetObjectKey(obj);
            RaftAttachmentDiagnosticRecord record;

            if (!Records.TryGetValue(key, out record))
            {
                if (oldRaft == null)
                    return;

                MarkAttached(obj, oldRaft, "DRAG_DISCOVERY");
                Records.TryGetValue(key, out record);
            }

            if (record == null)
                return;

            record.Object = obj;

            if (oldRaft != null)
            {
                record.Raft = oldRaft;
                record.RaftReferenceId = GetReferenceId(oldRaft);
            }

            record.State = RaftAttachmentDiagnosticState.Dragging;
            record.Revision++;
            record.LastWatchdogSignature = null;

            Emit(
                "DRAGGING",
                "reason=" + reason + " " +
                BuildContext(record, obj, oldRaft)
            );

            CaptureSnapshot(record, obj);
        }

        internal static void MarkDetached(
            ConstructionObject obj,
            Construction_STRUCTURE_RAFT oldRaft,
            string reason)
        {
            EnsureInitialized();

            if (!IsDiagnosticFurniture(obj))
                return;

            string key = GetObjectKey(obj);
            RaftAttachmentDiagnosticRecord record;

            if (!Records.TryGetValue(key, out record))
                return;

            record.Object = obj;
            record.State = RaftAttachmentDiagnosticState.Detached;
            record.Revision++;
            record.LastWatchdogSignature = null;

            Emit(
                "DETACHED",
                "reason=" + reason + " " +
                BuildContext(record, obj, oldRaft)
            );

            CaptureSnapshot(record, obj);
        }

        private static void Evaluate(
            RaftAttachmentDiagnosticRecord record,
            string source)
        {
            if (record == null || record.Object == null)
                return;

            ConstructionObject obj = record.Object;
            Rigidbody rb = obj.GetComponent<Rigidbody>();
            Construction_STRUCTURE_RAFT parentRaft = FindParentRaft(obj.transform);
            string parentRaftReferenceId = GetReferenceId(parentRaft);
            string parentPath = BuildHierarchyPath(obj.transform);

            bool hasRigidbody = rb != null;
            bool isKinematic = rb != null && rb.isKinematic;
            bool useGravity = rb != null && rb.useGravity;

            if (record.SnapshotInitialized)
            {
                bool parentChanged =
                    !String.Equals(
                        record.LastParentPath,
                        parentPath,
                        StringComparison.Ordinal
                    ) ||
                    !String.Equals(
                        record.LastParentRaftReferenceId,
                        parentRaftReferenceId,
                        StringComparison.OrdinalIgnoreCase
                    );

                bool rigidbodyChanged =
                    record.LastHasRigidbody != hasRigidbody ||
                    (hasRigidbody &&
                        (record.LastIsKinematic != isKinematic ||
                         record.LastUseGravity != useGravity));

                if (parentChanged || rigidbodyChanged)
                {
                    Emit(
                        "TRANSITION",
                        "source=" + source +
                        " parentChanged=" + parentChanged +
                        " rigidbodyChanged=" + rigidbodyChanged +
                        " oldParentRaft=" + SafeText(record.LastParentRaftReferenceId) +
                        " newParentRaft=" + SafeText(parentRaftReferenceId) +
                        " oldKinematic=" + record.LastIsKinematic +
                        " newKinematic=" + isKinematic +
                        " oldGravity=" + record.LastUseGravity +
                        " newGravity=" + useGravity +
                        " " + BuildContext(record, obj, record.Raft)
                    );
                }
            }

            if (record.State == RaftAttachmentDiagnosticState.Attached)
            {
                bool parentMissing = parentRaft == null;
                bool wrongParent =
                    parentRaft != null &&
                    !String.IsNullOrEmpty(record.RaftReferenceId) &&
                    !String.Equals(
                        parentRaftReferenceId,
                        record.RaftReferenceId,
                        StringComparison.OrdinalIgnoreCase
                    );

                bool dynamicRigidbody = rb != null && !rb.isKinematic;
                bool gravityEnabled = rb != null && rb.useGravity;
                bool raftMissing = record.Raft == null;

                float positionError = 0.0f;
                float rotationError = 0.0f;
                bool poseError = false;

                if (record.Raft != null)
                {
                    Vector3 expectedPosition =
                        record.Raft.transform.TransformPoint(
                            new Vector3(
                                record.LocalX,
                                record.HeightOffset,
                                record.LocalZ
                            )
                        );

                    Quaternion expectedRotation =
                        record.Raft.transform.rotation *
                        Quaternion.Euler(0.0f, record.LocalYaw, 0.0f);

                    positionError =
                        Vector3.Distance(
                            expectedPosition,
                            obj.transform.position
                        );

                    rotationError =
                        Quaternion.Angle(
                            expectedRotation,
                            obj.transform.rotation
                        );

                    poseError =
                        positionError > PositionErrorThreshold ||
                        rotationError > RotationErrorThreshold;
                }

                bool problem =
                    parentMissing ||
                    wrongParent ||
                    dynamicRigidbody ||
                    gravityEnabled ||
                    raftMissing ||
                    poseError;

                string signature =
                    "parentMissing=" + parentMissing +
                    "|wrongParent=" + wrongParent +
                    "|dynamic=" + dynamicRigidbody +
                    "|gravity=" + gravityEnabled +
                    "|raftMissing=" + raftMissing +
                    "|poseError=" + poseError +
                    "|parentRef=" + SafeText(parentRaftReferenceId);

                if (problem)
                {
                    if (!String.Equals(
                            signature,
                            record.LastWatchdogSignature,
                            StringComparison.Ordinal))
                    {
                        record.LastWatchdogSignature = signature;

                        Emit(
                            "WATCHDOG",
                            "source=" + source +
                            " positionError=" + positionError.ToString("F3") +
                            " rotationError=" + rotationError.ToString("F2") +
                            " " + signature +
                            " " + BuildContext(record, obj, record.Raft)
                        );
                    }
                }
                else
                {
                    record.LastWatchdogSignature = null;
                }
            }

            CaptureSnapshot(record, obj);
        }

        private static void CaptureSurfacePose(
            RaftAttachmentDiagnosticRecord record,
            ConstructionObject obj,
            Construction_STRUCTURE_RAFT raft)
        {
            if (record == null || obj == null || raft == null)
                return;

            Vector3 localPosition =
                raft.transform.InverseTransformPoint(
                    obj.transform.position
                );

            Quaternion localRotation =
                Quaternion.Inverse(raft.transform.rotation) *
                obj.transform.rotation;

            record.LocalX = localPosition.x;
            record.LocalZ = localPosition.z;
            record.HeightOffset = localPosition.y;
            record.LocalYaw = NormalizeYaw(localRotation.eulerAngles.y);
        }

        private static void ResolveMissingRuntimeObjects(
            RaftAttachmentDiagnosticRecord record)
        {
            if (record == null)
                return;

            if (record.Object == null)
            {
                ConstructionObject rebound =
                    FindFurnitureByReferenceId(record.ObjectReferenceId);

                if (rebound != null)
                {
                    record.Object = rebound;
                    record.MissingObjectLogged = false;

                    Emit(
                        "REBIND_OBJECT",
                        "object=" + record.ObjectReferenceId +
                        " instance=" + rebound.GetInstanceID()
                    );
                }
                else if (!record.MissingObjectLogged)
                {
                    record.MissingObjectLogged = true;

                    Emit(
                        "OBJECT_UNLOADED",
                        "object=" + record.ObjectReferenceId +
                        " raft=" + SafeText(record.RaftReferenceId) +
                        " state=" + record.State
                    );
                }
            }

            if (record.Raft == null &&
                !String.IsNullOrEmpty(record.RaftReferenceId))
            {
                Construction_STRUCTURE_RAFT reboundRaft =
                    FindRaftByReferenceId(record.RaftReferenceId);

                if (reboundRaft != null)
                {
                    record.Raft = reboundRaft;
                    record.MissingRaftLogged = false;

                    Emit(
                        "REBIND_RAFT",
                        "raft=" + record.RaftReferenceId +
                        " instance=" + reboundRaft.GetInstanceID()
                    );
                }
                else if (!record.MissingRaftLogged)
                {
                    record.MissingRaftLogged = true;

                    Emit(
                        "RAFT_UNLOADED",
                        "object=" + record.ObjectReferenceId +
                        " raft=" + record.RaftReferenceId +
                        " state=" + record.State
                    );
                }
            }
        }

        private static ConstructionObject FindFurnitureByReferenceId(
            string referenceId)
        {
            if (String.IsNullOrEmpty(referenceId))
                return null;

            ConstructionObject[] objects =
                UnityEngine.Object.FindObjectsOfType<ConstructionObject>();

            for (int i = 0; i < objects.Length; i++)
            {
                ConstructionObject obj = objects[i];

                if (!IsDiagnosticFurniture(obj))
                    continue;

                if (String.Equals(
                        GetReferenceId(obj),
                        referenceId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return obj;
                }
            }

            return null;
        }

        private static Construction_STRUCTURE_RAFT FindRaftByReferenceId(
            string referenceId)
        {
            if (String.IsNullOrEmpty(referenceId))
                return null;

            Construction_STRUCTURE_RAFT[] rafts =
                UnityEngine.Object.FindObjectsOfType<Construction_STRUCTURE_RAFT>();

            for (int i = 0; i < rafts.Length; i++)
            {
                Construction_STRUCTURE_RAFT raft = rafts[i];
                if (raft == null)
                    continue;

                if (String.Equals(
                        GetReferenceId(raft),
                        referenceId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return raft;
                }
            }

            return null;
        }

        private static bool IsDiagnosticFurniture(ConstructionObject obj)
        {
            return RaftFurnitureRuntime.IsAllowedFurnitureObject(obj);
        }

        private static Construction_STRUCTURE_RAFT FindParentRaft(
            Transform transform)
        {
            if (transform == null)
                return null;

            Transform current = transform.parent;

            while (current != null)
            {
                Construction_STRUCTURE_RAFT raft =
                    current.GetComponent<Construction_STRUCTURE_RAFT>();

                if (raft != null)
                    return raft;

                current = current.parent;
            }

            return null;
        }

        private static string GetObjectKey(ConstructionObject obj)
        {
            string referenceId = GetReferenceId(obj);

            if (!String.IsNullOrEmpty(referenceId))
                return "REF:" + referenceId;

            return "INSTANCE:" + obj.GetInstanceID();
        }

        private static string GetReferenceId(object target)
        {
            if (target == null)
                return null;

            try
            {
                Type type = target.GetType();

                while (type != null)
                {
                    PropertyInfo property =
                        type.GetProperty(
                            "ReferenceId",
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly
                        );

                    if (property != null && property.GetIndexParameters().Length == 0)
                    {
                        object value = property.GetValue(target, null);
                        if (value != null)
                            return value.ToString();
                    }

                    FieldInfo field =
                        type.GetField(
                            "ReferenceId",
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly
                        );

                    if (field == null)
                    {
                        field =
                            type.GetField(
                                "_referenceId",
                                BindingFlags.Instance |
                                BindingFlags.Public |
                                BindingFlags.NonPublic |
                                BindingFlags.DeclaredOnly
                            );
                    }

                    if (field != null)
                    {
                        object value = field.GetValue(target);
                        if (value != null)
                            return value.ToString();
                    }

                    type = type.BaseType;
                }
            }
            catch
            {
            }

            UnityEngine.Object unityObject = target as UnityEngine.Object;
            if (unityObject != null)
            {
                return "INSTANCE:" + unityObject.GetInstanceID();
            }

            return null;
        }

        private static void CaptureSnapshot(
            RaftAttachmentDiagnosticRecord record,
            ConstructionObject obj)
        {
            if (record == null || obj == null)
                return;

            Rigidbody rb = obj.GetComponent<Rigidbody>();
            Construction_STRUCTURE_RAFT parentRaft = FindParentRaft(obj.transform);

            record.LastParentPath = BuildHierarchyPath(obj.transform);
            record.LastParentRaftReferenceId = GetReferenceId(parentRaft);
            record.LastHasRigidbody = rb != null;
            record.LastIsKinematic = rb != null && rb.isKinematic;
            record.LastUseGravity = rb != null && rb.useGravity;
            record.SnapshotInitialized = true;
        }

        private static string BuildContext(
            RaftAttachmentDiagnosticRecord record,
            ConstructionObject obj,
            Construction_STRUCTURE_RAFT raft)
        {
            if (record == null || obj == null)
                return "context=<unavailable>";

            Rigidbody rb = obj.GetComponent<Rigidbody>();
            Construction_STRUCTURE_RAFT parentRaft = FindParentRaft(obj.transform);
            Construction_STRUCTURE_RAFT belowRaft =
                RaftFurnitureRuntime.FindRaftBelow(obj.transform.position);

            string parentRaftRef = GetReferenceId(parentRaft);
            string belowRaftRef = GetReferenceId(belowRaft);

            return
                "object=" + SafeText(record.ObjectReferenceId) +
                " instance=" + obj.GetInstanceID() +
                " raft=" + SafeText(record.RaftReferenceId) +
                " raftInstance=" + (raft != null ? raft.GetInstanceID().ToString() : "<null>") +
                " state=" + record.State +
                " revision=" + record.Revision +
                " profile=" + record.PlacementProfileId +
                " localSurfacePose=(x=" + record.LocalX.ToString("F3") +
                    ",h=" + record.HeightOffset.ToString("F3") +
                    ",z=" + record.LocalZ.ToString("F3") +
                    ",yaw=" + record.LocalYaw.ToString("F2") + ")" +
                " worldPos=" + FormatVector(obj.transform.position) +
                " worldEuler=" + FormatVector(obj.transform.eulerAngles) +
                " parentRaft=" + SafeText(parentRaftRef) +
                " raftBelow=" + SafeText(belowRaftRef) +
                " rb=" + FormatRigidbody(rb) +
                " collisions=" + GetCollisionIgnoreSummary(obj, raft) +
                " hierarchy=" + BuildHierarchyPath(obj.transform);
        }

        private static string GetCollisionIgnoreSummary(
            ConstructionObject obj,
            Construction_STRUCTURE_RAFT raft)
        {
            if (obj == null || raft == null)
                return "<unavailable>";

            try
            {
                Collider[] objectColliders =
                    obj.GetComponentsInChildren<Collider>(true);

                Collider[] raftColliders =
                    raft.GetComponentsInChildren<Collider>(true);

                int pairs = 0;
                int ignored = 0;

                for (int i = 0; i < objectColliders.Length; i++)
                {
                    Collider a = objectColliders[i];
                    if (a == null)
                        continue;

                    for (int j = 0; j < raftColliders.Length; j++)
                    {
                        Collider b = raftColliders[j];
                        if (b == null)
                            continue;

                        pairs++;

                        if (Physics.GetIgnoreCollision(a, b))
                            ignored++;
                    }
                }

                return ignored + "/" + pairs + " ignored";
            }
            catch (Exception ex)
            {
                return "error:" + ex.Message;
            }
        }

        private static string BuildHierarchyPath(Transform transform)
        {
            if (transform == null)
                return "<null>";

            List<string> parts = new List<string>();
            Transform current = transform;
            int depth = 0;

            while (current != null && depth < 20)
            {
                parts.Add(current.name ?? "<unnamed>");
                current = current.parent;
                depth++;
            }

            parts.Reverse();
            return String.Join("/", parts.ToArray());
        }

        private static string FormatRigidbody(Rigidbody rb)
        {
            if (rb == null)
                return "<none>";

            return
                "kinematic=" + rb.isKinematic +
                ",gravity=" + rb.useGravity +
                ",constraints=" + rb.constraints +
                ",velocity=" + FormatVector(rb.velocity) +
                ",angularVelocity=" + FormatVector(rb.angularVelocity);
        }

        private static string FormatVector(Vector3 value)
        {
            return
                "(" +
                value.x.ToString("F3") + "," +
                value.y.ToString("F3") + "," +
                value.z.ToString("F3") +
                ")";
        }

        private static float NormalizeYaw(float yaw)
        {
            yaw = Mathf.Repeat(yaw, 360.0f);

            if (yaw > 180.0f)
                yaw -= 360.0f;

            return yaw;
        }

        private static string SafeText(string value)
        {
            return String.IsNullOrEmpty(value) ? "<none>" : value;
        }

        private static void EnsureInitialized()
        {
            if (!Initialized)
                Initialize();
        }

        private static void Emit(string kind, string message)
        {
            string line =
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                " [" + kind + "] " + message;

            if (Plugin.Log != null)
                Plugin.Log.LogInfo("[AttachmentDiag] " + line);

            EmitRaw(line);
        }

        private static void EmitRaw(string line)
        {
            if (String.IsNullOrEmpty(DiagnosticFile))
                return;

            try
            {
                File.AppendAllText(
                    DiagnosticFile,
                    line + Environment.NewLine
                );
            }
            catch (Exception ex)
            {
                if (Plugin.Log != null)
                    Plugin.Log.LogWarning(
                        "[AttachmentDiag] Diagnostic file write failed: " +
                        ex.Message
                    );
            }
        }
    }

    internal enum RaftAttachmentRuntimeState
    {
        Attached,
        Dragging,
        Detached
    }

    [Serializable]
    public sealed class RaftAttachmentPersistentRecord
    {
        public string ObjectReferenceId;
        public string RaftReferenceId;
        public string PlacementProfileId;
        public float LocalX;
        public float LocalZ;
        public float HeightOffset;
        public float LocalYaw;
    }

    [Serializable]
    public sealed class RaftAttachmentPersistentFile
    {
        public int Version = 1;
        public RaftAttachmentPersistentRecord[] Records =
            new RaftAttachmentPersistentRecord[0];
    }

    internal sealed class RaftAttachmentRuntimeRecord
    {
        internal string ObjectReferenceId;
        internal string RaftReferenceId;
        internal string PlacementProfileId;
        internal float LocalX;
        internal float LocalZ;
        internal float HeightOffset;
        internal float LocalYaw;
        internal RaftAttachmentRuntimeState State;
        internal ConstructionObject Object;
        internal Construction_STRUCTURE_RAFT Raft;
        internal bool LoadedFromDisk;
        internal bool RuntimeMaterialized;
    }

    /// <summary>
    /// v0.2.1 persistent attachment authority for furniture.
    ///
    /// ReferenceId -> RaftReferenceId + raft-local surface pose is the source
    /// of truth. Transform parenting remains useful for vanilla hierarchy/save
    /// compatibility, but it is no longer relied on to move the object.
    /// </summary>
    internal static class RaftAttachmentRegistry
    {
        private const string PlacementProfileId = "FURNITURE_FREE_DECK_FOLLOWER";

        private static readonly Dictionary<string, RaftAttachmentRuntimeRecord> Records =
            new Dictionary<string, RaftAttachmentRuntimeRecord>(StringComparer.OrdinalIgnoreCase);

        private static string PersistenceDirectory;
        private static string PersistenceFile;
        private static string RuntimeLogFile;
        private static bool Initialized;
        private static bool Dirty;

        internal static void Initialize()
        {
            if (Initialized)
                return;

            Initialized = true;

            PersistenceDirectory = Path.Combine(
                Paths.ConfigPath,
                "StrandedDeepRaftFurniture"
            );

            Directory.CreateDirectory(PersistenceDirectory);

            PersistenceFile = Path.Combine(
                PersistenceDirectory,
                "Attachments.json"
            );

            RuntimeLogFile = Path.Combine(
                PersistenceDirectory,
                "AttachmentRuntime.log"
            );

            LoadPersistentSnapshot();

            Emit(
                "INIT",
                "v0.2.14 persistent follower initialized. records=" +
                Records.Count +
                " file=" + PersistenceFile
            );
        }

        internal static void Shutdown()
        {
            if (!Initialized)
                return;

            Emit(
                "SESSION",
                "Shutdown without implicit persistence. dirty=" + Dirty +
                " records=" + Records.Count
            );

            Initialized = false;
        }

        internal static void InstallNativeSaveHook(Harmony harmony)
        {
            if (harmony == null)
                return;

            MethodBase saveGame = FindNativeSaveGameMethod();
            if (saveGame == null)
            {
                Emit(
                    "SAVE_HOOK_MISSING",
                    "Could not find SaveManager.SaveGame(Beam.IPlayer). " +
                    "Follower remains active but Attachments.json will not update."
                );
                return;
            }

            MethodInfo prefixMethod = AccessTools.Method(
                typeof(RaftAttachmentRegistry),
                "NativeSavePrefix"
            );

            harmony.Patch(
                saveGame,
                new HarmonyMethod(prefixMethod)
            );

            Emit(
                "SAVE_HOOK",
                "Patched " + saveGame.DeclaringType.FullName + "." + saveGame.Name
            );
        }

        private static void NativeSavePrefix()
        {
            CommitNativeSave();
        }

        private static MethodBase FindNativeSaveGameMethod()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            for (int a = 0; a < assemblies.Length; a++)
            {
                Type[] types;

                try
                {
                    types = assemblies[a].GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch
                {
                    continue;
                }

                if (types == null)
                    continue;

                for (int t = 0; t < types.Length; t++)
                {
                    Type type = types[t];
                    if (type == null ||
                        !String.Equals(type.Name, "SaveManager", StringComparison.Ordinal))
                        continue;

                    MethodInfo[] methods = type.GetMethods(
                        BindingFlags.Instance |
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic
                    );

                    for (int m = 0; m < methods.Length; m++)
                    {
                        MethodInfo method = methods[m];
                        if (method == null ||
                            !String.Equals(method.Name, "SaveGame", StringComparison.Ordinal))
                            continue;

                        ParameterInfo[] parameters = method.GetParameters();
                        if (parameters.Length == 1 &&
                            parameters[0].ParameterType != null &&
                            String.Equals(
                                parameters[0].ParameterType.FullName,
                                "Beam.IPlayer",
                                StringComparison.Ordinal))
                        {
                            return method;
                        }
                    }
                }
            }

            return null;
        }

        internal static void Tick()
        {
            EnsureInitialized();

            if (Records.Count == 0)
                return;

            List<RaftAttachmentRuntimeRecord> snapshot =
                new List<RaftAttachmentRuntimeRecord>(Records.Values);

            for (int i = 0; i < snapshot.Count; i++)
            {
                RaftAttachmentRuntimeRecord record = snapshot[i];
                if (record == null || record.State != RaftAttachmentRuntimeState.Attached)
                    continue;

                ResolveRuntimeObjects(record);

                if (record.Object != null && record.Raft != null)
                    MaterializeRuntime(record, "TICK");
            }
        }

        internal static void Observe(ConstructionObject obj)
        {
            EnsureInitialized();

            if (obj == null || !RaftFurnitureRuntime.IsAllowedFurnitureObject(obj))
                return;

            string objectReferenceId = GetReferenceId(obj);
            if (String.IsNullOrEmpty(objectReferenceId))
                return;

            RaftAttachmentRuntimeRecord record;
            if (!Records.TryGetValue(objectReferenceId, out record))
                return;

            if (record.Object != obj)
            {
                record.Object = obj;
                record.RuntimeMaterialized = false;

                Emit(
                    "REBIND_OBJECT",
                    "object=" + objectReferenceId +
                    " instance=" + obj.GetInstanceID()
                );
            }

            ResolveRuntimeObjects(record);

            if (record.State == RaftAttachmentRuntimeState.Attached &&
                record.Object != null &&
                record.Raft != null)
            {
                MaterializeRuntime(record, "OBSERVE");
            }
        }

        internal static void Attach(
            ConstructionObject obj,
            Construction_STRUCTURE_RAFT raft,
            string reason)
        {
            EnsureInitialized();

            if (obj == null ||
                raft == null ||
                !RaftFurnitureRuntime.IsAllowedFurnitureObject(obj))
                return;

            string objectReferenceId = GetReferenceId(obj);
            string raftReferenceId = GetReferenceId(raft);

            if (String.IsNullOrEmpty(objectReferenceId) ||
                String.IsNullOrEmpty(raftReferenceId))
                return;

            RaftAttachmentRuntimeRecord record;
            bool existed = Records.TryGetValue(objectReferenceId, out record);

            if (!existed)
            {
                record = new RaftAttachmentRuntimeRecord();
                record.ObjectReferenceId = objectReferenceId;
                record.PlacementProfileId = PlacementProfileId;
                Records[objectReferenceId] = record;
            }

            bool samePersistentAssociation =
                existed &&
                record.State == RaftAttachmentRuntimeState.Attached &&
                !String.IsNullOrEmpty(record.RaftReferenceId) &&
                String.Equals(
                    record.RaftReferenceId,
                    raftReferenceId,
                    StringComparison.OrdinalIgnoreCase
                );

            bool preservePose =
                samePersistentAssociation &&
                (record.LoadedFromDisk ||
                 String.Equals(reason, "HEURISTIC_RECOVERY", StringComparison.Ordinal));

            record.Object = obj;
            record.Raft = raft;
            record.RaftReferenceId = raftReferenceId;
            record.PlacementProfileId = PlacementProfileId;
            record.State = RaftAttachmentRuntimeState.Attached;

            if (!preservePose)
                CaptureSurfacePose(record, obj, raft);

            record.LoadedFromDisk = false;
            record.RuntimeMaterialized = false;
            Dirty = true;

            MaterializeRuntime(record, "ATTACH:" + reason);

            Emit(
                "ATTACHED",
                "reason=" + reason +
                " existing=" + existed +
                " preservePose=" + preservePose +
                " object=" + objectReferenceId +
                " raft=" + raftReferenceId +
                " pose=" + FormatPose(record)
            );
        }

        internal static void BeginDrag(
            ConstructionObject obj,
            Construction_STRUCTURE_RAFT oldRaft)
        {
            EnsureInitialized();

            if (obj == null)
                return;

            string objectReferenceId = GetReferenceId(obj);
            if (String.IsNullOrEmpty(objectReferenceId))
                return;

            RaftAttachmentRuntimeRecord record;
            if (!Records.TryGetValue(objectReferenceId, out record))
            {
                if (oldRaft == null)
                    return;

                Attach(obj, oldRaft, "DRAG_DISCOVERY");
                Records.TryGetValue(objectReferenceId, out record);
            }

            if (record == null)
                return;

            record.Object = obj;

            if (oldRaft != null)
            {
                record.Raft = oldRaft;
                record.RaftReferenceId = GetReferenceId(oldRaft);
            }

            record.State = RaftAttachmentRuntimeState.Dragging;
            record.RuntimeMaterialized = false;

            Emit(
                "DRAGGING",
                "object=" + objectReferenceId +
                " raft=" + SafeText(record.RaftReferenceId)
            );
        }

        internal static void Detach(
            ConstructionObject obj,
            Construction_STRUCTURE_RAFT oldRaft,
            string reason)
        {
            EnsureInitialized();

            if (obj == null)
                return;

            string objectReferenceId = GetReferenceId(obj);
            if (String.IsNullOrEmpty(objectReferenceId))
                return;

            RaftAttachmentRuntimeRecord record;
            if (!Records.TryGetValue(objectReferenceId, out record))
                return;

            record.Object = obj;
            record.State = RaftAttachmentRuntimeState.Detached;
            record.RuntimeMaterialized = false;
            Dirty = true;

            Emit(
                "DETACHED",
                "reason=" + reason +
                " object=" + objectReferenceId +
                " oldRaft=" + SafeText(
                    oldRaft != null ? GetReferenceId(oldRaft) : record.RaftReferenceId)
            );
        }

        internal static bool HasRecord(ConstructionObject obj)
        {
            if (obj == null)
                return false;

            EnsureInitialized();

            string objectReferenceId = GetReferenceId(obj);
            if (String.IsNullOrEmpty(objectReferenceId))
                return false;

            RaftAttachmentRuntimeRecord record;
            return Records.TryGetValue(objectReferenceId, out record) &&
                record != null;
        }

        internal static bool IsAttached(ConstructionObject obj)
        {
            if (obj == null)
                return false;

            EnsureInitialized();

            string objectReferenceId = GetReferenceId(obj);
            if (String.IsNullOrEmpty(objectReferenceId))
                return false;

            RaftAttachmentRuntimeRecord record;
            return Records.TryGetValue(objectReferenceId, out record) &&
                record != null &&
                record.State == RaftAttachmentRuntimeState.Attached;
        }

        internal static Construction_STRUCTURE_RAFT GetAttachedRaft(
            ConstructionObject obj)
        {
            if (obj == null)
                return null;

            EnsureInitialized();

            string objectReferenceId = GetReferenceId(obj);
            if (String.IsNullOrEmpty(objectReferenceId))
                return null;

            RaftAttachmentRuntimeRecord record;
            if (!Records.TryGetValue(objectReferenceId, out record) ||
                record == null ||
                record.State != RaftAttachmentRuntimeState.Attached)
                return null;

            ResolveRuntimeObjects(record);
            return record.Raft;
        }

        internal static void FollowAttachedFurniture()
        {
            if (!Initialized || Records.Count == 0)
                return;

            List<RaftAttachmentRuntimeRecord> snapshot =
                new List<RaftAttachmentRuntimeRecord>(Records.Values);

            for (int i = 0; i < snapshot.Count; i++)
            {
                RaftAttachmentRuntimeRecord record = snapshot[i];
                if (record == null ||
                    record.State != RaftAttachmentRuntimeState.Attached ||
                    record.Object == null ||
                    record.Raft == null)
                    continue;

                ApplyFollowerPose(record);
            }
        }

        internal static void MagnetizeDraggingFurniture()
        {
            if (!Initialized || Records.Count == 0)
                return;

            List<RaftAttachmentRuntimeRecord> snapshot =
                new List<RaftAttachmentRuntimeRecord>(Records.Values);

            for (int i = 0; i < snapshot.Count; i++)
            {
                RaftAttachmentRuntimeRecord record = snapshot[i];
                if (record == null ||
                    record.State != RaftAttachmentRuntimeState.Dragging ||
                    record.Object == null ||
                    record.Raft == null)
                {
                    continue;
                }

                ConstructionObject obj = record.Object;
                Rigidbody rb = obj.GetComponent<Rigidbody>();

                RaycastHit hit;
                bool snapped = RaftFurnitureRuntime.TrySnapTransformToRaftDeck(
                    obj.transform,
                    record.Raft,
                    obj.transform.position,
                    record.LocalYaw,
                    0.015f,
                    out hit
                );

                if (snapped)
                {
                    RaftFurnitureRuntime.IgnoreBuiltCollisionWithRaft(
                        obj.transform,
                        record.Raft,
                        true
                    );

                    if (rb != null)
                    {
                        // Keep vanilla drag alive (dynamic body), but eliminate
                        // gravity/rotation drift while the object is over the deck.
                        // Own-raft collisions are already ignored, so this cannot
                        // inject a buoyancy impulse into the raft.
                        rb.useGravity = false;
                        rb.angularVelocity = Vector3.zero;
                    }
                }
                else if (rb != null)
                {
                    // Off-deck furniture retains the old detach semantics.
                    rb.useGravity = true;
                }
            }
        }

        internal static void CommitNativeSave()
        {
            EnsureInitialized();

            try
            {
                WritePersistentSnapshotAtomic();
                Dirty = false;

                Emit(
                    "PERSIST_SAVE",
                    "native SaveGame snapshot committed. records=" +
                    CountPersistentRecords()
                );
            }
            catch (Exception ex)
            {
                Emit(
                    "PERSIST_SAVE_ERROR",
                    ex.ToString()
                );
            }
        }

        private static void MaterializeRuntime(
            RaftAttachmentRuntimeRecord record,
            string reason)
        {
            if (record == null || record.Object == null || record.Raft == null)
                return;

            RaftFurnitureRuntime.ForceFurniturePhysicsForRaft(record.Object);
            RaftFurnitureRuntime.IgnoreBuiltCollisionWithRaft(
                record.Object.transform,
                record.Raft,
                true
            );

            ApplyFollowerPose(record);

            if (!record.RuntimeMaterialized)
            {
                record.RuntimeMaterialized = true;

                Emit(
                    "MATERIALIZED",
                    "reason=" + reason +
                    " object=" + record.ObjectReferenceId +
                    " raft=" + record.RaftReferenceId +
                    " pose=" + FormatPose(record)
                );
            }
        }

        private static void ApplyFollowerPose(
            RaftAttachmentRuntimeRecord record)
        {
            if (record == null || record.Object == null || record.Raft == null)
                return;

            ConstructionObject obj = record.Object;
            Construction_STRUCTURE_RAFT raft = record.Raft;

            Vector3 worldPosition =
                raft.transform.TransformPoint(
                    new Vector3(
                        record.LocalX,
                        record.HeightOffset,
                        record.LocalZ
                    )
                );

            Quaternion worldRotation =
                raft.transform.rotation *
                Quaternion.Euler(0.0f, record.LocalYaw, 0.0f);

            Rigidbody rb = obj.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
                rb.useGravity = false;
                rb.constraints = RigidbodyConstraints.None;
                rb.position = worldPosition;
                rb.rotation = worldRotation;
            }

            obj.transform.position = worldPosition;
            obj.transform.rotation = worldRotation;
        }

        private static void CaptureSurfacePose(
            RaftAttachmentRuntimeRecord record,
            ConstructionObject obj,
            Construction_STRUCTURE_RAFT raft)
        {
            Vector3 localPosition =
                raft.transform.InverseTransformPoint(obj.transform.position);

            Quaternion localRotation =
                Quaternion.Inverse(raft.transform.rotation) *
                obj.transform.rotation;

            record.LocalX = localPosition.x;
            record.LocalZ = localPosition.z;
            record.HeightOffset = localPosition.y;
            record.LocalYaw = NormalizeYaw(localRotation.eulerAngles.y);
        }

        private static void ResolveRuntimeObjects(
            RaftAttachmentRuntimeRecord record)
        {
            if (record == null)
                return;

            if (record.Object == null &&
                !String.IsNullOrEmpty(record.ObjectReferenceId))
            {
                record.Object = FindFurnitureByReferenceId(
                    record.ObjectReferenceId
                );

                if (record.Object != null)
                {
                    record.RuntimeMaterialized = false;
                    Emit(
                        "REBIND_OBJECT",
                        "object=" + record.ObjectReferenceId +
                        " instance=" + record.Object.GetInstanceID()
                    );
                }
            }

            if (record.Raft == null &&
                !String.IsNullOrEmpty(record.RaftReferenceId))
            {
                record.Raft = FindRaftByReferenceId(
                    record.RaftReferenceId
                );

                if (record.Raft != null)
                {
                    record.RuntimeMaterialized = false;
                    Emit(
                        "REBIND_RAFT",
                        "raft=" + record.RaftReferenceId +
                        " instance=" + record.Raft.GetInstanceID()
                    );
                }
            }
        }

        private static ConstructionObject FindFurnitureByReferenceId(
            string referenceId)
        {
            if (String.IsNullOrEmpty(referenceId))
                return null;

            ConstructionObject[] objects =
                UnityEngine.Object.FindObjectsOfType<ConstructionObject>();

            for (int i = 0; i < objects.Length; i++)
            {
                ConstructionObject obj = objects[i];
                if (obj == null ||
                    !RaftFurnitureRuntime.IsAllowedFurnitureObject(obj))
                    continue;

                if (String.Equals(
                        GetReferenceId(obj),
                        referenceId,
                        StringComparison.OrdinalIgnoreCase))
                    return obj;
            }

            return null;
        }

        private static Construction_STRUCTURE_RAFT FindRaftByReferenceId(
            string referenceId)
        {
            if (String.IsNullOrEmpty(referenceId))
                return null;

            Construction_STRUCTURE_RAFT[] rafts =
                UnityEngine.Object.FindObjectsOfType<Construction_STRUCTURE_RAFT>();

            for (int i = 0; i < rafts.Length; i++)
            {
                Construction_STRUCTURE_RAFT raft = rafts[i];
                if (raft == null)
                    continue;

                if (String.Equals(
                        GetReferenceId(raft),
                        referenceId,
                        StringComparison.OrdinalIgnoreCase))
                    return raft;
            }

            return null;
        }

        private static void LoadPersistentSnapshot()
        {
            if (String.IsNullOrEmpty(PersistenceFile) ||
                !File.Exists(PersistenceFile))
                return;

            try
            {
                string json = File.ReadAllText(PersistenceFile);
                if (String.IsNullOrEmpty(json))
                    return;

                RaftAttachmentPersistentFile file =
                    DeserializePersistentFile(json);

                if (file == null || file.Records == null)
                    return;

                for (int i = 0; i < file.Records.Length; i++)
                {
                    RaftAttachmentPersistentRecord saved = file.Records[i];
                    if (saved == null ||
                        String.IsNullOrEmpty(saved.ObjectReferenceId) ||
                        String.IsNullOrEmpty(saved.RaftReferenceId))
                        continue;

                    RaftAttachmentRuntimeRecord runtime =
                        new RaftAttachmentRuntimeRecord();

                    runtime.ObjectReferenceId = saved.ObjectReferenceId;
                    runtime.RaftReferenceId = saved.RaftReferenceId;
                    runtime.PlacementProfileId =
                        String.IsNullOrEmpty(saved.PlacementProfileId)
                        ? PlacementProfileId
                        : saved.PlacementProfileId;
                    runtime.LocalX = saved.LocalX;
                    runtime.LocalZ = saved.LocalZ;
                    runtime.HeightOffset = saved.HeightOffset;
                    runtime.LocalYaw = saved.LocalYaw;
                    runtime.State = RaftAttachmentRuntimeState.Attached;
                    runtime.LoadedFromDisk = true;
                    runtime.RuntimeMaterialized = false;

                    Records[runtime.ObjectReferenceId] = runtime;
                }

                Emit(
                    "PERSIST_LOAD",
                    "loaded records=" + Records.Count +
                    " file=" + PersistenceFile
                );
            }
            catch (Exception ex)
            {
                Emit(
                    "PERSIST_LOAD_ERROR",
                    ex.ToString()
                );
            }
        }

        private static void WritePersistentSnapshotAtomic()
        {
            if (String.IsNullOrEmpty(PersistenceFile))
                return;

            RaftAttachmentPersistentFile file =
                new RaftAttachmentPersistentFile();

            List<RaftAttachmentPersistentRecord> persistentRecords =
                new List<RaftAttachmentPersistentRecord>();

            foreach (RaftAttachmentRuntimeRecord record in Records.Values)
            {
                if (record == null ||
                    record.State == RaftAttachmentRuntimeState.Detached ||
                    String.IsNullOrEmpty(record.ObjectReferenceId) ||
                    String.IsNullOrEmpty(record.RaftReferenceId))
                    continue;

                RaftAttachmentPersistentRecord saved =
                    new RaftAttachmentPersistentRecord();

                saved.ObjectReferenceId = record.ObjectReferenceId;
                saved.RaftReferenceId = record.RaftReferenceId;
                saved.PlacementProfileId = record.PlacementProfileId;
                saved.LocalX = record.LocalX;
                saved.LocalZ = record.LocalZ;
                saved.HeightOffset = record.HeightOffset;
                saved.LocalYaw = record.LocalYaw;

                persistentRecords.Add(saved);
            }

            file.Records = persistentRecords.ToArray();

            string json = SerializePersistentFile(file);

            // Do not trust serialization blindly. Parse our own payload back before
            // replacing the current sidecar. This keeps a known-good file intact
            // if persistence code ever regresses.
            RaftAttachmentPersistentFile roundTrip =
                DeserializePersistentFile(json);

            int expectedCount = file.Records == null ? 0 : file.Records.Length;
            int serializedCount =
                roundTrip == null || roundTrip.Records == null
                ? 0
                : roundTrip.Records.Length;

            if (serializedCount != expectedCount)
            {
                throw new InvalidDataException(
                    "Attachment JSON round-trip mismatch. expected=" +
                    expectedCount +
                    " serialized=" + serializedCount
                );
            }

            if (expectedCount > 0)
            {
                string firstObjectId = file.Records[0].ObjectReferenceId;
                if (String.IsNullOrEmpty(firstObjectId) ||
                    json.IndexOf(firstObjectId, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    throw new InvalidDataException(
                        "Attachment JSON validation failed: first ObjectReferenceId missing from payload."
                    );
                }
            }

            string temp = PersistenceFile + ".tmp";
            string backup = PersistenceFile + ".bak";

            File.WriteAllText(temp, json);

            if (File.Exists(PersistenceFile))
            {
                try
                {
                    File.Replace(temp, PersistenceFile, backup);
                }
                catch
                {
                    File.Copy(PersistenceFile, backup, true);
                    File.Delete(PersistenceFile);
                    File.Move(temp, PersistenceFile);
                }
            }
            else
            {
                File.Move(temp, PersistenceFile);
            }
        }

        private static string SerializePersistentFile(
            RaftAttachmentPersistentFile file)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("{");
            builder.Append("  \"Version\": ");
            builder.Append(file == null ? 1 : file.Version);
            builder.AppendLine(",");
            builder.AppendLine("  \"Records\": [");

            RaftAttachmentPersistentRecord[] records =
                file == null || file.Records == null
                ? new RaftAttachmentPersistentRecord[0]
                : file.Records;

            bool wroteRecord = false;

            for (int i = 0; i < records.Length; i++)
            {
                RaftAttachmentPersistentRecord record = records[i];
                if (record == null)
                    continue;

                if (wroteRecord)
                    builder.AppendLine(",");

                builder.AppendLine("    {");
                AppendJsonString(builder, "ObjectReferenceId", record.ObjectReferenceId, true);
                AppendJsonString(builder, "RaftReferenceId", record.RaftReferenceId, true);
                AppendJsonString(builder, "PlacementProfileId", record.PlacementProfileId, true);
                AppendJsonFloat(builder, "LocalX", record.LocalX, true);
                AppendJsonFloat(builder, "LocalZ", record.LocalZ, true);
                AppendJsonFloat(builder, "HeightOffset", record.HeightOffset, true);
                AppendJsonFloat(builder, "LocalYaw", record.LocalYaw, false);
                builder.Append("    }");
                wroteRecord = true;
            }

            if (wroteRecord)
                builder.AppendLine();

            builder.AppendLine("  ]");
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static RaftAttachmentPersistentFile DeserializePersistentFile(
            string json)
        {
            RaftAttachmentPersistentFile file =
                new RaftAttachmentPersistentFile();

            if (String.IsNullOrEmpty(json))
                return file;

            Match versionMatch = Regex.Match(
                json,
                "\\\"Version\\\"\\s*:\\s*(\\d+)",
                RegexOptions.CultureInvariant
            );

            int version;
            if (versionMatch.Success &&
                Int32.TryParse(
                    versionMatch.Groups[1].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out version))
            {
                file.Version = version;
            }

            List<RaftAttachmentPersistentRecord> records =
                new List<RaftAttachmentPersistentRecord>();

            MatchCollection objectMatches = Regex.Matches(
                json,
                "\\{[^{}]*\\\"ObjectReferenceId\\\"[^{}]*\\}",
                RegexOptions.CultureInvariant | RegexOptions.Singleline
            );

            for (int i = 0; i < objectMatches.Count; i++)
            {
                string block = objectMatches[i].Value;

                RaftAttachmentPersistentRecord record =
                    new RaftAttachmentPersistentRecord();

                record.ObjectReferenceId = ReadJsonString(block, "ObjectReferenceId");
                record.RaftReferenceId = ReadJsonString(block, "RaftReferenceId");
                record.PlacementProfileId = ReadJsonString(block, "PlacementProfileId");
                record.LocalX = ReadJsonFloat(block, "LocalX");
                record.LocalZ = ReadJsonFloat(block, "LocalZ");
                record.HeightOffset = ReadJsonFloat(block, "HeightOffset");
                record.LocalYaw = ReadJsonFloat(block, "LocalYaw");

                if (!String.IsNullOrEmpty(record.ObjectReferenceId) &&
                    !String.IsNullOrEmpty(record.RaftReferenceId))
                {
                    records.Add(record);
                }
            }

            file.Records = records.ToArray();
            return file;
        }

        private static void AppendJsonString(
            StringBuilder builder,
            string name,
            string value,
            bool comma)
        {
            builder.Append("      \"");
            builder.Append(name);
            builder.Append("\": \"");
            builder.Append(EscapeJsonString(value));
            builder.Append('\"');
            if (comma)
                builder.Append(',');
            builder.AppendLine();
        }

        private static void AppendJsonFloat(
            StringBuilder builder,
            string name,
            float value,
            bool comma)
        {
            builder.Append("      \"");
            builder.Append(name);
            builder.Append("\": ");
            builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
            if (comma)
                builder.Append(',');
            builder.AppendLine();
        }

        private static string EscapeJsonString(string value)
        {
            if (value == null)
                return String.Empty;

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private static string UnescapeJsonString(string value)
        {
            if (value == null)
                return String.Empty;

            return value
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
        }

        private static string ReadJsonString(string block, string name)
        {
            Match match = Regex.Match(
                block,
                "\\\"" + Regex.Escape(name) +
                "\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"\\\\])*)\\\"",
                RegexOptions.CultureInvariant | RegexOptions.Singleline
            );

            if (!match.Success)
                return String.Empty;

            return UnescapeJsonString(match.Groups[1].Value);
        }

        private static float ReadJsonFloat(string block, string name)
        {
            Match match = Regex.Match(
                block,
                "\\\"" + Regex.Escape(name) +
                "\\\"\\s*:\\s*([-+]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)(?:[eE][-+]?\\d+)?)",
                RegexOptions.CultureInvariant
            );

            float value;
            if (match.Success &&
                Single.TryParse(
                    match.Groups[1].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value))
            {
                return value;
            }

            return 0.0f;
        }

        private static int CountPersistentRecords()
        {
            int count = 0;

            foreach (RaftAttachmentRuntimeRecord record in Records.Values)
            {
                if (record != null &&
                    record.State != RaftAttachmentRuntimeState.Detached &&
                    !String.IsNullOrEmpty(record.ObjectReferenceId) &&
                    !String.IsNullOrEmpty(record.RaftReferenceId))
                    count++;
            }

            return count;
        }

        private static string GetReferenceId(object target)
        {
            if (target == null)
                return null;

            try
            {
                Type type = target.GetType();

                while (type != null)
                {
                    PropertyInfo property =
                        type.GetProperty(
                            "ReferenceId",
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly
                        );

                    if (property != null && property.GetIndexParameters().Length == 0)
                    {
                        object value = property.GetValue(target, null);
                        if (value != null)
                            return value.ToString();
                    }

                    FieldInfo field =
                        type.GetField(
                            "ReferenceId",
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly
                        );

                    if (field == null)
                    {
                        field =
                            type.GetField(
                                "_referenceId",
                                BindingFlags.Instance |
                                BindingFlags.Public |
                                BindingFlags.NonPublic |
                                BindingFlags.DeclaredOnly
                            );
                    }

                    if (field != null)
                    {
                        object value = field.GetValue(target);
                        if (value != null)
                            return value.ToString();
                    }

                    type = type.BaseType;
                }
            }
            catch
            {
            }

            return null;
        }

        private static float NormalizeYaw(float yaw)
        {
            yaw = Mathf.Repeat(yaw, 360.0f);

            if (yaw > 180.0f)
                yaw -= 360.0f;

            return yaw;
        }

        private static string FormatPose(RaftAttachmentRuntimeRecord record)
        {
            return
                "(x=" + record.LocalX.ToString("F3") +
                ",h=" + record.HeightOffset.ToString("F3") +
                ",z=" + record.LocalZ.ToString("F3") +
                ",yaw=" + record.LocalYaw.ToString("F2") + ")";
        }

        private static string SafeText(string value)
        {
            return String.IsNullOrEmpty(value) ? "<none>" : value;
        }

        private static void EnsureInitialized()
        {
            if (!Initialized)
                Initialize();
        }

        private static void Emit(string kind, string message)
        {
            string line =
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                " [" + kind + "] " + message;

            if (Plugin.Log != null)
                Plugin.Log.LogInfo("[RaftAttachment] " + line);

            if (String.IsNullOrEmpty(RuntimeLogFile))
                return;

            try
            {
                File.AppendAllText(
                    RuntimeLogFile,
                    line + Environment.NewLine
                );
            }
            catch
            {
            }
        }
    }

    internal enum RaftRepositionTargetKind
    {
        Furniture,
        TerrainConstruction,
        CampfireStructure,
        ContainerShelf
    }

    internal sealed class RaftRepositionCollisionPairState
    {
        internal Collider ObjectCollider;
        internal Collider RaftCollider;
        internal bool WasIgnored;
        internal bool KeepIgnoredAfterReposition;
    }

    internal sealed class RaftRepositionTarget
    {
        internal RaftRepositionTargetKind Kind;
        internal Transform Root;
        internal BaseObject BaseObject;
        internal ConstructionObject Furniture;
        internal Constructing Constructing;
        internal Construction_STRUCTURE SmallStructure;
        internal RaftSmallStructureFollower SmallStructureFollower;
        internal Construction_STRUCTURE_RAFT Raft;
        internal Camera Camera;
        internal int OwnerPlayerId = -1;
        internal int OwnerJoystickId = -1;
        internal Vector3 OriginalPosition;
        internal Quaternion OriginalRotation;
        internal float LocalYaw;
        internal bool HasValidDeckPoint;

        // Water Still and STRUCTURE_SMALL can deliver solver impulses to the
        // dynamic raft while their kinematic colliders are moved. During custom
        // reposition keep an exact temporary collision-pair snapshot so the
        // moving target is fully isolated from the owning raft hierarchy.
        internal bool RepositionPhysicsIsolated;
        internal List<RaftRepositionCollisionPairState> RepositionCollisionPairs;

        // v0.2.13: while Water Still / STRUCTURE_SMALL is actively moved,
        // temporarily disable its solid colliders. Pair isolation from v0.2.12
        // protects the dynamic raft; this additional layer prevents the moving
        // kinematic target from physically pushing either local player or other
        // world rigidbodies. Trigger colliders stay enabled so logical/cooking
        // state is not needlessly disturbed. Exact enabled states are restored.
        internal bool RepositionTargetCollidersIsolated;
        internal List<Collider> RepositionTargetColliders;
        internal List<bool> RepositionTargetColliderEnabled;
        internal float RepositionPivotToBottom;
        internal bool RepositionPivotToBottomValid;

        // Last valid raft-local pose is retained while the player's camera
        // temporarily leaves the deck (for example, if the player falls off
        // the raft during reposition). The object must never freeze in world
        // space while its owning raft keeps moving.
        internal Vector3 LastValidLocalPosition;
        internal bool HasLastValidLocalPosition;

        // Native Container Shelf is normally a child collider hierarchy of the
        // dynamic raft. Moving those child colliders every frame changes the
        // raft's live compound physics shape and can violently disturb buoyancy.
        // During reposition we isolate the whole shelf hierarchy temporarily and
        // restore its exact native hierarchy/state on confirm or cancel.
        internal Transform OriginalParent;
        internal int OriginalSiblingIndex;
        internal bool ShelfPhysicsIsolated;
        internal List<Collider> ShelfColliders;
        internal List<bool> ShelfColliderEnabled;
        internal List<Rigidbody> ShelfBodies;
        internal List<bool> ShelfBodyKinematic;
        internal List<bool> ShelfBodyGravity;
        internal List<RigidbodyConstraints> ShelfBodyConstraints;
        internal List<Vector3> ShelfBodyVelocity;
        internal List<Vector3> ShelfBodyAngularVelocity;

        // Captured while shelf colliders are still enabled. During isolated
        // reposition all shelf colliders are disabled, so generic collider-
        // bottom snapping cannot measure the shelf and would fall back to the
        // root pivot. Preserve the real pivot-to-bottom distance instead.
        internal float ShelfPivotToBottom;
        internal bool ShelfPivotToBottomValid;
    }

    /// <summary>
    /// v0.2.2 split-screen surface reposition controller.
    ///
    /// Furniture keeps the game's native drag lifecycle. Terrain constructions,
    /// STRUCTURE_SMALL and Container Shelf now use the same physical Drag Item
    /// control as ordinary objects: hold L2/LT to move, release to drop.
    /// L1/LB rotates the active target by 15 degrees per press.
    /// Input is read from the Rewired joystick assigned to the owning local player,
    /// so P1 and P2 never control each other's active reposition target.
    /// </summary>
    internal static class RaftRepositionController
    {
        private const int DragAxisIndex = 4;       // raw P1 L2 / P2 LT
        private const int RotateButtonIndex = 4;  // raw P1 L1 / P2 LB
        private const float DragThreshold = 0.55f;

        private sealed class LocalControllerState
        {
            internal int PlayerId;
            internal Rewired.Player Player;
            internal Joystick Joystick;
            internal Camera Camera;
            internal bool DragHeld;
            internal bool RotateHeld;
        }

        private sealed class SuppressedElementState
        {
            internal ActionElementMap Map;
            internal bool WasEnabled;
        }

        private static readonly LocalControllerState[] Controllers =
            new LocalControllerState[]
            {
                new LocalControllerState { PlayerId = 0 },
                new LocalControllerState { PlayerId = 1 }
            };

        private static readonly List<SuppressedElementState> SuppressedMaps =
            new List<SuppressedElementState>();

        private static RaftRepositionTarget Active;
        private static string LogFile;
        private static float NextControllerRefresh;

        internal static void UpdateInput()
        {
            EnsureLogFile();
            RefreshControllerAssignmentsIfNeeded();

            for (int i = 0; i < Controllers.Length; i++)
            {
                LocalControllerState state = Controllers[i];
                Joystick joystick = state.Joystick;

                bool dragNow = false;
                bool rotateNow = false;

                if (joystick != null)
                {
                    try
                    {
                        dragNow = joystick.GetAxis(DragAxisIndex) > DragThreshold;
                        rotateNow = joystick.GetButton(RotateButtonIndex);
                    }
                    catch
                    {
                        dragNow = false;
                        rotateNow = false;
                    }
                }

                bool dragPressed = dragNow && !state.DragHeld;
                bool dragReleased = !dragNow && state.DragHeld;
                bool rotatePressed = rotateNow && !state.RotateHeld;

                // No custom construction reposition active: a fresh Drag Item
                // press may start one for the object under this player's camera.
                // Furniture is deliberately ignored here and continues through
                // the game's native InteractiveObject.BeginDrag/EndDrag path.
                if (Active == null && dragPressed && RaftFurnitureSettings.AllowManualReposition)
                {
                    TryBeginFromPlayerCamera(state);
                }
                else if (Active != null && Active.OwnerPlayerId == state.PlayerId)
                {
                    if (rotatePressed)
                    {
                        Active.LocalYaw = NormalizeYaw(Active.LocalYaw + 15.0f);

                        Emit(
                            "ROTATE",
                            "player=P" + (state.PlayerId + 1) +
                            " target=" + (Active.Root != null ? Active.Root.name : "<missing>") +
                            " yaw=" + Active.LocalYaw.ToString("F2")
                        );
                    }

                    // Drag/drop UX: releasing L2/LT is the drop action.
                    if (dragReleased)
                    {
                        if (Active.HasValidDeckPoint)
                            Confirm();
                        else
                            CancelWithReason("DROP_CANCEL_INVALID");
                    }
                }

                state.DragHeld = dragNow;
                state.RotateHeld = rotateNow;
            }
        }

        internal static void CancelActiveFromExternal(string reason)
        {
            if (Active == null)
                return;

            CancelWithReason(
                String.IsNullOrEmpty(reason)
                    ? "EXTERNAL_CANCEL"
                    : reason);
        }

        internal static void LateTick()
        {
            // Furniture uses the game's drag input; this only changes its deck
            // geometry, not the control scheme.
            RaftAttachmentRegistry.MagnetizeDraggingFurniture();

            if (Active == null ||
                Active.Root == null ||
                Active.Raft == null ||
                Active.Camera == null)
            {
                return;
            }

            Ray ray = Active.Camera.ViewportPointToRay(
                new Vector3(0.5f, 0.5f, 0.0f));

            RaycastHit hit;
            if (!Physics.Raycast(
                    ray,
                    out hit,
                    8.0f,
                    RaftFurnitureRuntime.RaftFloorMask,
                    QueryTriggerInteraction.Ignore))
            {
                Active.HasValidDeckPoint = false;
                HoldLastValidRaftPose(Active);
                return;
            }

            Construction_STRUCTURE_RAFT hitRaft =
                FindRaftFromDeckHit(hit);

            if (hitRaft != Active.Raft)
            {
                Active.HasValidDeckPoint = false;
                HoldLastValidRaftPose(Active);
                return;
            }

            RaycastHit snappedHit;

            if (Active.Kind == RaftRepositionTargetKind.ContainerShelf)
            {
                Active.HasValidDeckPoint =
                    TrySnapContainerShelfToRaftDeck(
                        Active,
                        hit.point,
                        0.015f,
                        out snappedHit
                    );
            }
            else if (Active.RepositionTargetCollidersIsolated)
            {
                Active.HasValidDeckPoint =
                    TrySnapColliderIsolatedTargetToRaftDeck(
                        Active,
                        hit.point,
                        0.015f,
                        out snappedHit
                    );
            }
            else
            {
                Active.HasValidDeckPoint =
                    RaftFurnitureRuntime.TrySnapTransformToRaftDeck(
                        Active.Root,
                        Active.Raft,
                        hit.point,
                        Active.LocalYaw,
                        0.015f,
                        out snappedHit
                    );
            }

            if (!Active.HasValidDeckPoint)
            {
                HoldLastValidRaftPose(Active);
                return;
            }

            CaptureLastValidRaftPose(Active);
            ApplyRepositionSafety(Active);
        }

        private static void RefreshControllerAssignmentsIfNeeded()
        {
            if (Time.unscaledTime < NextControllerRefresh)
                return;

            NextControllerRefresh = Time.unscaledTime + 1.0f;

            Camera[] gameplayCameras = GetGameplayCamerasSorted();

            for (int i = 0; i < Controllers.Length; i++)
            {
                LocalControllerState state = Controllers[i];
                state.Player = null;
                state.Joystick = null;
                state.Camera = i < gameplayCameras.Length
                    ? gameplayCameras[i]
                    : null;

                try
                {
                    if (!ReInput.isReady)
                        continue;

                    Rewired.Player player = ReInput.players.GetPlayer(state.PlayerId);
                    state.Player = player;

                    if (player != null && player.controllers.Joysticks.Count > 0)
                        state.Joystick = player.controllers.Joysticks[0];
                }
                catch
                {
                    state.Player = null;
                    state.Joystick = null;
                }
            }
        }

        private static Camera[] GetGameplayCamerasSorted()
        {
            List<Camera> cameras = new List<Camera>();
            Camera[] all = Camera.allCameras;

            for (int i = 0; i < all.Length; i++)
            {
                Camera camera = all[i];
                if (camera == null || !camera.enabled || camera.targetTexture != null)
                    continue;

                string path = GetTransformPath(camera.transform);
                if (path.IndexOf("PlayerCamera", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (path.IndexOf("PlayerUI", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("GameUI", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                cameras.Add(camera);
            }

            cameras.Sort(
                delegate(Camera a, Camera b)
                {
                    int x = a.rect.x.CompareTo(b.rect.x);
                    if (x != 0)
                        return x;
                    return b.depth.CompareTo(a.depth);
                }
            );

            // Deduplicate cameras that render the same split-screen viewport.
            List<Camera> unique = new List<Camera>();
            for (int i = 0; i < cameras.Count; i++)
            {
                Camera candidate = cameras[i];
                bool duplicate = false;
                for (int u = 0; u < unique.Count; u++)
                {
                    if (Mathf.Abs(unique[u].rect.x - candidate.rect.x) < 0.001f &&
                        Mathf.Abs(unique[u].rect.width - candidate.rect.width) < 0.001f)
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                    unique.Add(candidate);
            }

            return unique.ToArray();
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
                return String.Empty;

            string path = transform.name;
            Transform current = transform.parent;
            int guard = 0;

            while (current != null && guard++ < 16)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }

        private static void TryBeginFromPlayerCamera(LocalControllerState state)
        {
            if (state == null || state.Camera == null || state.Joystick == null)
                return;

            Camera camera = state.Camera;
            Ray ray = camera.ViewportPointToRay(
                new Vector3(0.5f, 0.5f, 0.0f));

            RaycastHit[] hits = Physics.RaycastAll(
                ray,
                6.0f,
                ~0,
                QueryTriggerInteraction.Ignore
            );

            Array.Sort(
                hits,
                delegate(RaycastHit a, RaycastHit b)
                {
                    return a.distance.CompareTo(b.distance);
                }
            );

            RaftRepositionTarget best = null;
            for (int h = 0; h < hits.Length; h++)
            {
                RaftRepositionTarget candidate;
                if (!TryResolveTarget(hits[h], camera, out candidate))
                    continue;

                // Tables/chairs already use vanilla drag. Starting the custom
                // controller for them would double-handle the same L2/LT press.
                if (candidate.Kind == RaftRepositionTargetKind.Furniture)
                    return;

                best = candidate;
                break;
            }

            // Empty Container Shelf has large visual gaps and the exact center
            // ray can pass between its rails. If the precise ray found nothing,
            // use a very small sphere cast around the crosshair as an acquisition
            // aid. This only chooses the object; deck movement remains exact.
            if (best == null)
            {
                RaycastHit[] nearHits = Physics.SphereCastAll(
                    ray,
                    0.16f,
                    6.0f,
                    ~0,
                    QueryTriggerInteraction.Ignore
                );

                Array.Sort(
                    nearHits,
                    delegate(RaycastHit a, RaycastHit b)
                    {
                        return a.distance.CompareTo(b.distance);
                    }
                );

                for (int h = 0; h < nearHits.Length; h++)
                {
                    RaftRepositionTarget candidate;
                    if (!TryResolveTarget(nearHits[h], camera, out candidate))
                        continue;

                    // The fallback must not steal ordinary furniture drag merely
                    // because a chair/table happens to be close to the crosshair.
                    if (candidate.Kind == RaftRepositionTargetKind.Furniture)
                        continue;

                    best = candidate;

                    Emit(
                        "BEGIN_SPHERE_FALLBACK",
                        "player=P" + (state.PlayerId + 1) +
                        " target=" +
                        (candidate.Root != null ? candidate.Root.name : "<missing>") +
                        " radius=0.16"
                    );

                    break;
                }
            }

            if (best == null)
                return;

            Active = best;
            Active.OwnerPlayerId = state.PlayerId;
            Active.OwnerJoystickId = state.Joystick.id;

            Active.OriginalPosition = Active.Root.position;
            Active.OriginalRotation = Active.Root.rotation;
            Active.OriginalParent = Active.Root.parent;
            Active.OriginalSiblingIndex =
                Active.Root.parent != null
                    ? Active.Root.GetSiblingIndex()
                    : -1;

            Active.LocalYaw = RaftFurnitureRuntime.GetLocalYaw(
                Active.Root,
                Active.Raft
            );

            CaptureLastValidRaftPose(Active);

            // Isolate the two proven physics-sensitive families before any
            // Rigidbody state or transform changes happen in custom reposition.
            BeginRepositionPhysicsIsolation(Active);
            BeginRepositionTargetColliderIsolation(Active);

            if (Active.SmallStructureFollower != null)
                Active.SmallStructureFollower.SetExternalReposition(true, false);

            if (Active.Kind == RaftRepositionTargetKind.ContainerShelf)
                IsolateContainerShelfPhysics(Active);

            ApplyRepositionSafety(Active);
            SuppressOwnerBindings(state);

            Emit(
                "BEGIN",
                "player=P" + (state.PlayerId + 1) +
                " input=DRAG_L2_LT" +
                " kind=" + Active.Kind +
                " target=" + Active.Root.name +
                " raft=" + SafeReferenceId(Active.Raft) +
                " yaw=" + Active.LocalYaw.ToString("F2")
            );
        }

        private static void SuppressOwnerBindings(LocalControllerState state)
        {
            RestoreSuppressedBindings();

            if (state == null ||
                state.Player == null ||
                state.Joystick == null)
            {
                return;
            }

            try
            {
                int suppressed = 0;

                // This deliberately mirrors the already field-tested Split Map
                // suppression path: filter the owning joystick ControllerMap, then
                // inspect ButtonMaps by raw elementIndex. For our two controllers
                // raw button 4 is L1/LB. Do not use elementIdentifierId here.
                IEnumerable<ControllerMap> maps =
                    state.Player.controllers.maps.GetAllMaps<ControllerMap>();

                foreach (ControllerMap map in maps)
                {
                    if (map == null ||
                        map.controllerType != ControllerType.Joystick ||
                        map.controllerId != state.Joystick.id)
                    {
                        continue;
                    }

                    IList<ActionElementMap> buttonMaps = map.ButtonMaps;
                    if (buttonMaps == null)
                        continue;

                    for (int i = 0; i < buttonMaps.Count; i++)
                    {
                        ActionElementMap actionMap = buttonMaps[i];

                        if (actionMap == null ||
                            actionMap.elementIndex != RotateButtonIndex)
                        {
                            continue;
                        }

                        SuppressedElementState saved =
                            new SuppressedElementState();

                        saved.Map = actionMap;
                        saved.WasEnabled = actionMap.enabled;
                        SuppressedMaps.Add(saved);

                        if (actionMap.enabled)
                            actionMap.enabled = false;

                        suppressed++;

                        Emit(
                            "INPUT_SUPPRESS",
                            "player=P" + (state.PlayerId + 1) +
                            " joystick=" + state.Joystick.id +
                            " rawButton=" + actionMap.elementIndex +
                            " action=" + actionMap.actionId +
                            " name=" + actionMap.actionDescriptiveName
                        );
                    }
                }

                if (suppressed == 0)
                {
                    Emit(
                        "INPUT_SUPPRESS_WARNING",
                        "No L1/LB ActionElementMap found for player=P" +
                        (state.PlayerId + 1) +
                        " joystick=" + state.Joystick.id
                    );
                }
            }
            catch (Exception ex)
            {
                Emit("INPUT_SUPPRESS_WARNING", ex.Message);
            }
        }

        private static void RestoreSuppressedBindings()
        {
            for (int i = 0; i < SuppressedMaps.Count; i++)
            {
                SuppressedElementState saved = SuppressedMaps[i];
                if (saved == null || saved.Map == null)
                    continue;

                try
                {
                    saved.Map.enabled = saved.WasEnabled;
                }
                catch
                {
                }
            }

            SuppressedMaps.Clear();
        }

        private static bool TryResolveTarget(
            RaycastHit hit,
            Camera camera,
            out RaftRepositionTarget target)
        {
            target = null;
            if (hit.collider == null)
                return false;

            Transform hitTransform = hit.collider.transform;

            ConstructionObject furniture =
                hit.collider.GetComponentInParent<ConstructionObject>();

            if (furniture != null &&
                RaftFurnitureRuntime.IsAllowedFurnitureObject(furniture))
            {
                Construction_STRUCTURE_RAFT raft =
                    RaftAttachmentRegistry.GetAttachedRaft(furniture);

                if (raft == null)
                    raft = RaftFurnitureRuntime.FindParentRaftStructure(
                        furniture.transform
                    );

                if (raft == null)
                    raft = RaftFurnitureRuntime.FindRaftBelow(
                        furniture.transform.position
                    );

                if (raft != null)
                {
                    target = new RaftRepositionTarget();
                    target.Kind = RaftRepositionTargetKind.Furniture;
                    target.Root = furniture.transform;
                    target.BaseObject = furniture;
                    target.Furniture = furniture;
                    target.Raft = raft;
                    target.Camera = camera;
                    return true;
                }
            }

            Constructing constructing =
                hit.collider.GetComponentInParent<Constructing>();

            if (constructing != null)
            {
                Construction_STRUCTURE structure =
                    RaftFurnitureRuntime.GetStructure(constructing);

                if (structure != null &&
                    !(structure is Construction_STRUCTURE_RAFT))
                {
                    Construction_CAMPFIRE fire =
                        RaftFurnitureRuntime.FindCampfireInStructure(structure);

                    if (fire != null &&
                        RaftFurnitureSettings.AllowCampfireStructuresOnRaft)
                    {
                        Construction_STRUCTURE_RAFT raft =
                            RaftFurnitureRuntime.GetRaftForSmallStructure(
                                structure
                            );

                        if (raft != null)
                        {
                            target = new RaftRepositionTarget();
                            target.Kind = RaftRepositionTargetKind.CampfireStructure;
                            target.Root = structure.transform;
                            target.Constructing = fire;
                            target.SmallStructure = structure;
                            target.SmallStructureFollower =
                                structure.GetComponent<RaftSmallStructureFollower>();
                            target.Raft = raft;
                            target.Camera = camera;
                            return true;
                        }
                    }
                }

                if (RaftFurnitureRuntime.IsAllowedTerrainConstructing(constructing) &&
                    RaftFurnitureRuntime.IsRepositionEnabled(constructing) &&
                    !(constructing is Construction_CAMPFIRE))
                {
                    Construction_STRUCTURE_RAFT raft =
                        RaftFurnitureRuntime.FindParentRaftStructure(
                            constructing.transform
                        );

                    if (raft == null)
                        raft = RaftFurnitureRuntime.GetStructure(constructing)
                            as Construction_STRUCTURE_RAFT;

                    if (raft == null)
                        raft = RaftFurnitureRuntime.FindRaftBelow(
                            constructing.transform.position
                        );

                    if (raft != null)
                    {
                        target = new RaftRepositionTarget();
                        target.Kind = RaftRepositionTargetKind.TerrainConstruction;
                        target.Root = constructing.transform;
                        target.BaseObject = constructing;
                        target.Constructing = constructing;
                        target.Raft = raft;
                        target.Camera = camera;
                        return true;
                    }
                }
            }

            Transform shelfRoot;
            BaseObject shelfBase;
            if (RaftFurnitureSettings.AllowContainerShelfReposition &&
                TryFindContainerShelfRoot(
                    hitTransform,
                    out shelfRoot,
                    out shelfBase))
            {
                Construction_STRUCTURE_RAFT raft =
                    RaftFurnitureRuntime.FindParentRaftStructure(shelfRoot);

                if (raft == null)
                    raft = RaftFurnitureRuntime.FindRaftBelow(
                        shelfRoot.position
                    );

                if (raft != null)
                {
                    target = new RaftRepositionTarget();
                    target.Kind = RaftRepositionTargetKind.ContainerShelf;
                    target.Root = shelfRoot;
                    target.BaseObject = shelfBase;
                    target.Raft = raft;
                    target.Camera = camera;
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindContainerShelfRoot(
            Transform hit,
            out Transform root,
            out BaseObject baseObject)
        {
            root = null;
            baseObject = null;

            Transform current = hit;
            for (int depth = 0; current != null && depth < 12; depth++)
            {
                if (RaftFurnitureRuntime.IsContainerShelfTransform(current))
                {
                    BaseObject candidate = current.GetComponent<BaseObject>();

                    if (candidate != null &&
                        !(candidate is Construction_STRUCTURE_RAFT))
                    {
                        root = candidate.transform;
                        baseObject = candidate;
                        return true;
                    }

                    // Some native raft connector prefabs keep the connector on
                    // a child GameObject while the shelf root itself has no
                    // BaseObject component. In that case move the shelf-named
                    // transform itself and leave its socket children untouched.
                    root = current;
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        private static Construction_STRUCTURE_RAFT FindRaftFromDeckHit(
            RaycastHit hit)
        {
            if (hit.collider == null)
                return null;

            Construction_RAFT piece =
                hit.collider.GetComponent<Construction_RAFT>();

            if (piece == null)
                piece = hit.collider.GetComponentInParent<Construction_RAFT>();

            if (piece == null)
                return null;

            return RaftFurnitureRuntime.GetStructure(piece)
                as Construction_STRUCTURE_RAFT;
        }

        private static void Confirm()
        {
            if (Active == null)
                return;

            if (!Active.HasValidDeckPoint)
            {
                Emit("CONFIRM_REJECTED", "Current aim is not on the owning raft deck.");
                return;
            }

            RaftRepositionTarget finished = Active;
            Active = null;
            RestoreSuppressedBindings();

            if (finished.Kind == RaftRepositionTargetKind.Furniture &&
                finished.Furniture != null)
            {
                RaftFurnitureRuntime.AttachFurnitureObject(
                    finished.Furniture,
                    finished.Raft,
                    "SURFACE_REPOSITION"
                );
            }
            else if (finished.Kind == RaftRepositionTargetKind.TerrainConstruction &&
                     finished.Constructing != null)
            {
                RaftFurnitureRuntime.SetStructure(
                    finished.Constructing,
                    finished.Raft
                );

                if (finished.BaseObject != null)
                {
                    RaftFurnitureRuntime.EnsureBaseObjectParent(
                        finished.BaseObject,
                        finished.Raft.transform
                    );
                }

                ForceSafeTargetPhysics(finished);
            }
            else if (finished.Kind == RaftRepositionTargetKind.CampfireStructure)
            {
                // The load path already repairs STRUCTURE_SMALL to the native
                // raft deck height. Apply that same proven normalization at drop
                // time, before the follower captures its new raft-local pose, so
                // Smoker/Fire Pit no longer remain visibly suspended until reload.
                if (finished.SmallStructure != null && finished.Raft != null)
                {
                    Vector3 before = finished.SmallStructure.transform.position;

                    RaftFurnitureRuntime.RepairSmallStructurePoseOnRaft(
                        finished.SmallStructure,
                        finished.Raft
                    );

                    Physics.SyncTransforms();

                    float repairDelta = Vector3.Distance(
                        before,
                        finished.SmallStructure.transform.position
                    );

                    Emit(
                        "STRUCTURE_DROP_NORMALIZE",
                        "target=" + finished.SmallStructure.name +
                        " delta=" + repairDelta.ToString("F3")
                    );
                }

                if (finished.SmallStructureFollower != null)
                    finished.SmallStructureFollower.SetExternalReposition(false, true);

                ForceSafeTargetPhysics(finished);
            }
            else if (finished.Kind == RaftRepositionTargetKind.ContainerShelf)
            {
                RestoreContainerShelfPhysics(finished, false);
            }

            RaftFurnitureRuntime.IgnoreBuiltCollisionWithRaft(
                finished.Root,
                finished.Raft,
                true
            );

            EndRepositionPhysicsIsolation(
                finished,
                "CONFIRM"
            );

            EndRepositionTargetColliderIsolation(
                finished,
                "CONFIRM"
            );

            Emit(
                "CONFIRM",
                "kind=" + finished.Kind +
                " target=" + finished.Root.name +
                " raft=" + SafeReferenceId(finished.Raft) +
                " local=" + FormatLocalPose(finished.Root, finished.Raft)
            );
        }

        private static void Cancel()
        {
            CancelWithReason("CANCEL");
        }

        private static void CancelWithReason(string reason)
        {
            if (Active == null)
                return;

            RaftRepositionTarget cancelled = Active;
            Active = null;
            RestoreSuppressedBindings();

            if (cancelled.Root != null)
            {
                cancelled.Root.position = cancelled.OriginalPosition;
                cancelled.Root.rotation = cancelled.OriginalRotation;
                Physics.SyncTransforms();
            }

            if (cancelled.Kind == RaftRepositionTargetKind.ContainerShelf)
                RestoreContainerShelfPhysics(cancelled, true);

            if (cancelled.Kind == RaftRepositionTargetKind.Furniture &&
                cancelled.Furniture != null)
            {
                RaftFurnitureRuntime.AttachFurnitureObject(
                    cancelled.Furniture,
                    cancelled.Raft,
                    "SURFACE_REPOSITION_CANCEL"
                );
            }

            if (cancelled.SmallStructureFollower != null)
                cancelled.SmallStructureFollower.SetExternalReposition(false, true);

            if (cancelled.Kind != RaftRepositionTargetKind.ContainerShelf)
                ForceSafeTargetPhysics(cancelled);

            EndRepositionPhysicsIsolation(
                cancelled,
                "CANCEL"
            );

            EndRepositionTargetColliderIsolation(
                cancelled,
                "CANCEL"
            );

            Emit(
                reason,
                "kind=" + cancelled.Kind +
                " target=" +
                (cancelled.Root != null ? cancelled.Root.name : "<missing>")
            );
        }

        private static bool TrySnapContainerShelfToRaftDeck(
            RaftRepositionTarget target,
            Vector3 desiredWorldPoint,
            float clearance,
            out RaycastHit deckHit)
        {
            deckHit = new RaycastHit();

            if (target == null ||
                target.Root == null ||
                target.Raft == null ||
                !target.ShelfPivotToBottomValid)
            {
                return false;
            }

            Vector3 raftUp = target.Raft.transform.up.normalized;
            Vector3 origin = desiredWorldPoint + raftUp * 3.0f;

            if (!Physics.Raycast(
                    origin,
                    -raftUp,
                    out deckHit,
                    8.0f,
                    RaftFurnitureRuntime.RaftFloorMask,
                    QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            Construction_STRUCTURE_RAFT hitRaft =
                FindRaftFromDeckHit(deckHit);

            if (hitRaft != target.Raft)
                return false;

            target.Root.rotation =
                target.Raft.transform.rotation *
                Quaternion.Euler(0.0f, target.LocalYaw, 0.0f);

            // Shelf colliders are intentionally disabled while isolated from the
            // dynamic raft, so use the pre-isolation pivot-to-bottom measurement.
            // Yaw-only rotation in raft-local space does not change this distance.
            target.Root.position =
                deckHit.point +
                raftUp * (target.ShelfPivotToBottom + clearance);

            Physics.SyncTransforms();
            return true;
        }

        private static void CaptureLastValidRaftPose(
            RaftRepositionTarget target)
        {
            if (target == null ||
                target.Root == null ||
                target.Raft == null)
            {
                return;
            }

            target.LastValidLocalPosition =
                target.Raft.transform.InverseTransformPoint(
                    target.Root.position
                );

            target.HasLastValidLocalPosition = true;
        }

        private static void HoldLastValidRaftPose(
            RaftRepositionTarget target)
        {
            if (target == null ||
                target.Root == null ||
                target.Raft == null ||
                !target.HasLastValidLocalPosition)
            {
                return;
            }

            target.Root.position =
                target.Raft.transform.TransformPoint(
                    target.LastValidLocalPosition
                );

            target.Root.rotation =
                target.Raft.transform.rotation *
                Quaternion.Euler(0.0f, target.LocalYaw, 0.0f);

            Physics.SyncTransforms();
            ApplyRepositionSafety(target);
        }

        private static bool NeedsRepositionPhysicsIsolation(
            RaftRepositionTarget target)
        {
            if (target == null)
                return false;

            if (target.Kind == RaftRepositionTargetKind.CampfireStructure)
                return true;

            if (target.Kind == RaftRepositionTargetKind.TerrainConstruction &&
                target.Constructing is Beam.Constructing_STILL)
            {
                return true;
            }

            return false;
        }

        private static void BeginRepositionTargetColliderIsolation(
            RaftRepositionTarget target)
        {
            if (!NeedsRepositionPhysicsIsolation(target) ||
                target.Root == null ||
                target.Raft == null ||
                target.RepositionTargetCollidersIsolated)
            {
                return;
            }

            Vector3 raftUp = target.Raft.transform.up.normalized;
            float bottomProjection;

            target.RepositionPivotToBottomValid =
                RaftFurnitureRuntime.TryGetFurnitureBottomProjection(
                    target.Root,
                    raftUp,
                    out bottomProjection
                );

            if (!target.RepositionPivotToBottomValid)
            {
                Emit(
                    "TARGET_COLLIDER_DISABLE_SKIP",
                    "kind=" + target.Kind +
                    " target=" + target.Root.name +
                    " reason=pivot-to-bottom-unavailable"
                );
                return;
            }

            target.RepositionPivotToBottom =
                Vector3.Dot(target.Root.position, raftUp) -
                bottomProjection;

            Collider[] colliders =
                target.Root.GetComponentsInChildren<Collider>(true);

            target.RepositionTargetColliders =
                new List<Collider>();
            target.RepositionTargetColliderEnabled =
                new List<bool>();

            int disabled = 0;
            int triggersKept = 0;

            if (colliders != null)
            {
                for (int i = 0; i < colliders.Length; i++)
                {
                    Collider collider = colliders[i];
                    if (collider == null)
                        continue;

                    target.RepositionTargetColliders.Add(collider);
                    target.RepositionTargetColliderEnabled.Add(
                        collider.enabled
                    );

                    // Trigger volumes do not create the physical push we are
                    // preventing. Leave them alone so cooking/interaction logic
                    // is not needlessly churned while the structure is moved.
                    if (collider.isTrigger)
                    {
                        triggersKept++;
                        continue;
                    }

                    if (collider.enabled)
                    {
                        collider.enabled = false;
                        disabled++;
                    }
                }
            }

            target.RepositionTargetCollidersIsolated = true;
            Physics.SyncTransforms();

            Emit(
                "TARGET_COLLIDER_DISABLE_BEGIN",
                "kind=" + target.Kind +
                " target=" + target.Root.name +
                " colliders=" +
                    target.RepositionTargetColliders.Count +
                " disabledSolid=" + disabled +
                " triggersKept=" + triggersKept +
                " pivotToBottom=" +
                    target.RepositionPivotToBottom.ToString("F3")
            );
        }

        private static void MaintainRepositionTargetColliderIsolation(
            RaftRepositionTarget target)
        {
            if (target == null ||
                !target.RepositionTargetCollidersIsolated ||
                target.RepositionTargetColliders == null)
            {
                return;
            }

            int repaired = 0;

            for (int i = 0;
                 i < target.RepositionTargetColliders.Count;
                 i++)
            {
                Collider collider =
                    target.RepositionTargetColliders[i];

                if (collider == null || collider.isTrigger)
                    continue;

                bool wasEnabled =
                    target.RepositionTargetColliderEnabled != null &&
                    i < target.RepositionTargetColliderEnabled.Count
                        ? target.RepositionTargetColliderEnabled[i]
                        : true;

                if (!wasEnabled || !collider.enabled)
                    continue;

                collider.enabled = false;
                repaired++;
            }

            if (repaired > 0)
            {
                Physics.SyncTransforms();

                Emit(
                    "TARGET_COLLIDER_REASSERT",
                    "kind=" + target.Kind +
                    " target=" +
                        (target.Root != null
                            ? target.Root.name
                            : "<missing>") +
                    " repaired=" + repaired
                );
            }
        }

        private static void EndRepositionTargetColliderIsolation(
            RaftRepositionTarget target,
            string phase)
        {
            if (target == null ||
                !target.RepositionTargetCollidersIsolated ||
                target.RepositionTargetColliders == null)
            {
                return;
            }

            int restored = 0;
            int missing = 0;

            for (int i = 0;
                 i < target.RepositionTargetColliders.Count;
                 i++)
            {
                Collider collider =
                    target.RepositionTargetColliders[i];

                if (collider == null)
                {
                    missing++;
                    continue;
                }

                bool desired =
                    target.RepositionTargetColliderEnabled != null &&
                    i < target.RepositionTargetColliderEnabled.Count
                        ? target.RepositionTargetColliderEnabled[i]
                        : true;

                if (collider.enabled != desired)
                {
                    collider.enabled = desired;
                    restored++;
                }
            }

            int colliderCount =
                target.RepositionTargetColliders.Count;

            target.RepositionTargetColliders.Clear();
            target.RepositionTargetColliders = null;

            if (target.RepositionTargetColliderEnabled != null)
            {
                target.RepositionTargetColliderEnabled.Clear();
                target.RepositionTargetColliderEnabled = null;
            }

            target.RepositionTargetCollidersIsolated = false;
            target.RepositionPivotToBottomValid = false;

            Physics.SyncTransforms();

            Emit(
                "TARGET_COLLIDER_DISABLE_END",
                "phase=" + phase +
                " kind=" + target.Kind +
                " target=" +
                    (target.Root != null
                        ? target.Root.name
                        : "<missing>") +
                " colliders=" + colliderCount +
                " restored=" + restored +
                " missing=" + missing
            );
        }

        private static bool TrySnapColliderIsolatedTargetToRaftDeck(
            RaftRepositionTarget target,
            Vector3 desiredWorldPoint,
            float clearance,
            out RaycastHit deckHit)
        {
            deckHit = new RaycastHit();

            if (target == null ||
                target.Root == null ||
                target.Raft == null ||
                !target.RepositionPivotToBottomValid)
            {
                return false;
            }

            Vector3 raftUp = target.Raft.transform.up.normalized;
            Vector3 origin = desiredWorldPoint + raftUp * 3.0f;

            if (!Physics.Raycast(
                    origin,
                    -raftUp,
                    out deckHit,
                    8.0f,
                    RaftFurnitureRuntime.RaftFloorMask,
                    QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            Construction_STRUCTURE_RAFT hitRaft =
                FindRaftFromDeckHit(deckHit);

            if (hitRaft != target.Raft)
                return false;

            target.Root.rotation =
                target.Raft.transform.rotation *
                Quaternion.Euler(0.0f, target.LocalYaw, 0.0f);

            // The solid target colliders are disabled during v0.2.13 custom
            // reposition, so the generic collider-bottom solver cannot measure
            // them frame-by-frame. Yaw-only rotation around raft.up preserves
            // the captured pivot-to-bottom distance.
            target.Root.position =
                deckHit.point +
                raftUp *
                (target.RepositionPivotToBottom + clearance);

            Physics.SyncTransforms();
            return true;
        }

        private static void BeginRepositionPhysicsIsolation(
            RaftRepositionTarget target)
        {
            if (!NeedsRepositionPhysicsIsolation(target) ||
                target.Root == null ||
                target.Raft == null ||
                target.RepositionPhysicsIsolated)
            {
                return;
            }

            Collider[] objectColliders =
                target.Root.GetComponentsInChildren<Collider>(true);

            Collider[] raftColliders =
                target.Raft.GetComponentsInChildren<Collider>(true);

            target.RepositionCollisionPairs =
                new List<RaftRepositionCollisionPairState>();

            Rigidbody raftBody =
                target.Raft.GetComponent<Rigidbody>();

            if (raftBody == null)
                raftBody = target.Raft.GetComponentInParent<Rigidbody>();

            int changed = 0;
            int keepIgnored = 0;

            if (objectColliders != null && raftColliders != null)
            {
                for (int a = 0; a < objectColliders.Length; a++)
                {
                    Collider objectCollider = objectColliders[a];
                    if (objectCollider == null)
                        continue;

                    for (int b = 0; b < raftColliders.Length; b++)
                    {
                        Collider raftCollider = raftColliders[b];
                        if (raftCollider == null ||
                            objectCollider == raftCollider)
                        {
                            continue;
                        }

                        Transform raftColliderTransform =
                            raftCollider.transform;

                        // Water Still is itself a child of STRUCTURE_RAFT. Skip
                        // every collider that belongs to the moving target.
                        if (raftColliderTransform == target.Root ||
                            raftColliderTransform.IsChildOf(target.Root))
                        {
                            continue;
                        }

                        RaftRepositionCollisionPairState state =
                            new RaftRepositionCollisionPairState();

                        state.ObjectCollider = objectCollider;
                        state.RaftCollider = raftCollider;
                        state.WasIgnored = Physics.GetIgnoreCollision(
                            objectCollider,
                            raftCollider
                        );

                        // Colliders attached directly to the dynamic raft body
                        // must remain ignored after drop as part of the normal
                        // own-raft collision policy. Other child-collider pairs
                        // are temporary isolation only and are restored exactly.
                        state.KeepIgnoredAfterReposition =
                            raftBody != null &&
                            raftCollider.attachedRigidbody == raftBody;

                        if (state.KeepIgnoredAfterReposition)
                            keepIgnored++;

                        target.RepositionCollisionPairs.Add(state);

                        if (!state.WasIgnored)
                        {
                            Physics.IgnoreCollision(
                                objectCollider,
                                raftCollider,
                                true
                            );
                            changed++;
                        }
                    }
                }
            }

            target.RepositionPhysicsIsolated = true;
            Physics.SyncTransforms();

            Emit(
                "PHYSICS_ISOLATE_BEGIN",
                "kind=" + target.Kind +
                " target=" + target.Root.name +
                " objectColliders=" +
                    (objectColliders != null ? objectColliders.Length : 0) +
                " raftHierarchyColliders=" +
                    (raftColliders != null ? raftColliders.Length : 0) +
                " pairs=" + target.RepositionCollisionPairs.Count +
                " changed=" + changed +
                " keepIgnored=" + keepIgnored
            );
        }

        private static void MaintainRepositionPhysicsIsolation(
            RaftRepositionTarget target)
        {
            if (target == null ||
                !target.RepositionPhysicsIsolated ||
                target.RepositionCollisionPairs == null)
            {
                return;
            }

            int repaired = 0;

            for (int i = 0; i < target.RepositionCollisionPairs.Count; i++)
            {
                RaftRepositionCollisionPairState state =
                    target.RepositionCollisionPairs[i];

                if (state == null ||
                    state.ObjectCollider == null ||
                    state.RaftCollider == null)
                {
                    continue;
                }

                if (Physics.GetIgnoreCollision(
                        state.ObjectCollider,
                        state.RaftCollider))
                {
                    continue;
                }

                Physics.IgnoreCollision(
                    state.ObjectCollider,
                    state.RaftCollider,
                    true
                );

                repaired++;
            }

            if (repaired > 0)
            {
                Emit(
                    "PHYSICS_ISOLATE_REASSERT",
                    "kind=" + target.Kind +
                    " target=" +
                        (target.Root != null ? target.Root.name : "<missing>") +
                    " repaired=" + repaired
                );
            }
        }

        private static void EndRepositionPhysicsIsolation(
            RaftRepositionTarget target,
            string phase)
        {
            if (target == null ||
                !target.RepositionPhysicsIsolated ||
                target.RepositionCollisionPairs == null)
            {
                return;
            }

            // Final pose and Rigidbody state must be visible to Physics before
            // any temporary pair is restored.
            Physics.SyncTransforms();

            int restored = 0;
            int keptIgnored = 0;
            int missing = 0;

            for (int i = 0; i < target.RepositionCollisionPairs.Count; i++)
            {
                RaftRepositionCollisionPairState state =
                    target.RepositionCollisionPairs[i];

                if (state == null ||
                    state.ObjectCollider == null ||
                    state.RaftCollider == null)
                {
                    missing++;
                    continue;
                }

                bool desired =
                    state.KeepIgnoredAfterReposition
                        ? true
                        : state.WasIgnored;

                bool current = Physics.GetIgnoreCollision(
                    state.ObjectCollider,
                    state.RaftCollider
                );

                if (current != desired)
                {
                    Physics.IgnoreCollision(
                        state.ObjectCollider,
                        state.RaftCollider,
                        desired
                    );
                    restored++;
                }

                if (desired)
                    keptIgnored++;
            }

            int pairCount = target.RepositionCollisionPairs.Count;

            target.RepositionCollisionPairs.Clear();
            target.RepositionCollisionPairs = null;
            target.RepositionPhysicsIsolated = false;

            Emit(
                "PHYSICS_ISOLATE_END",
                "phase=" + phase +
                " kind=" + target.Kind +
                " target=" +
                    (target.Root != null ? target.Root.name : "<missing>") +
                " pairs=" + pairCount +
                " restored=" + restored +
                " keptIgnored=" + keptIgnored +
                " missing=" + missing
            );
        }

        private static void ApplyRepositionSafety(
            RaftRepositionTarget target)
        {
            if (target == null)
                return;

            // Keep the target's solid colliders disabled and the complete
            // temporary raft-pair isolation asserted before any per-frame
            // Rigidbody safety or transform movement continues.
            MaintainRepositionTargetColliderIsolation(target);
            MaintainRepositionPhysicsIsolation(target);

            ForceSafeTargetPhysics(target);

            if (target.Root != null && target.Raft != null)
            {
                RaftFurnitureRuntime.IgnoreBuiltCollisionWithRaft(
                    target.Root,
                    target.Raft,
                    true
                );
            }

            if (target.Kind == RaftRepositionTargetKind.CampfireStructure &&
                target.SmallStructure != null)
            {
                RaftFurnitureRuntime.SyncAttachedCookables(
                    target.SmallStructure
                );
            }
        }

        private static void IsolateContainerShelfPhysics(
            RaftRepositionTarget target)
        {
            if (target == null ||
                target.Root == null ||
                target.Raft == null ||
                target.ShelfPhysicsIsolated)
            {
                return;
            }

            target.ShelfColliders = new List<Collider>();
            target.ShelfColliderEnabled = new List<bool>();
            target.ShelfBodies = new List<Rigidbody>();
            target.ShelfBodyKinematic = new List<bool>();
            target.ShelfBodyGravity = new List<bool>();
            target.ShelfBodyConstraints =
                new List<RigidbodyConstraints>();
            target.ShelfBodyVelocity = new List<Vector3>();
            target.ShelfBodyAngularVelocity = new List<Vector3>();

            Vector3 raftUp = target.Raft.transform.up.normalized;
            float bottomProjection;
            target.ShelfPivotToBottomValid =
                RaftFurnitureRuntime.TryGetFurnitureBottomProjection(
                    target.Root,
                    raftUp,
                    out bottomProjection
                );

            if (target.ShelfPivotToBottomValid)
            {
                target.ShelfPivotToBottom =
                    Vector3.Dot(target.Root.position, raftUp) -
                    bottomProjection;
            }

            Rigidbody[] bodies =
                target.Root.GetComponentsInChildren<Rigidbody>(true);

            for (int i = 0; i < bodies.Length; i++)
            {
                Rigidbody body = bodies[i];
                if (body == null)
                    continue;

                target.ShelfBodies.Add(body);
                target.ShelfBodyKinematic.Add(body.isKinematic);
                target.ShelfBodyGravity.Add(body.useGravity);
                target.ShelfBodyConstraints.Add(body.constraints);
                target.ShelfBodyVelocity.Add(body.velocity);
                target.ShelfBodyAngularVelocity.Add(body.angularVelocity);

                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = true;
                body.useGravity = false;
                body.Sleep();
            }

            Collider[] colliders =
                target.Root.GetComponentsInChildren<Collider>(true);

            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null)
                    continue;

                target.ShelfColliders.Add(collider);
                target.ShelfColliderEnabled.Add(collider.enabled);
                collider.enabled = false;
            }

            // Native Container Shelf normally lives under the dynamic raft.
            // While its child collider hierarchy is moved every frame, Unity
            // treats those colliders as a changing compound shape of the raft.
            // Temporarily move the shelf hierarchy beside the raft instead.
            Transform temporaryParent = target.Raft.transform.parent;

            target.Root.SetParent(temporaryParent, true);
            target.ShelfPhysicsIsolated = true;

            Physics.SyncTransforms();

            Emit(
                "SHELF_ISOLATE",
                "target=" + target.Root.name +
                " bodies=" + target.ShelfBodies.Count +
                " colliders=" + target.ShelfColliders.Count +
                " pivotToBottom=" +
                (target.ShelfPivotToBottomValid
                    ? target.ShelfPivotToBottom.ToString("F3")
                    : "<invalid>")
            );
        }

        private static void RestoreContainerShelfPhysics(
            RaftRepositionTarget target,
            bool cancelled)
        {
            if (target == null ||
                target.Root == null ||
                !target.ShelfPhysicsIsolated)
            {
                return;
            }

            Transform parent = target.OriginalParent;
            if (parent == null && target.Raft != null)
                parent = target.Raft.transform;

            target.Root.SetParent(parent, true);

            if (target.OriginalSiblingIndex >= 0 &&
                target.Root.parent != null)
            {
                int maxIndex =
                    target.Root.parent.childCount - 1;

                int index = Mathf.Clamp(
                    target.OriginalSiblingIndex,
                    0,
                    maxIndex
                );

                target.Root.SetSiblingIndex(index);
            }

            if (target.ShelfColliders != null)
            {
                for (int i = 0;
                     i < target.ShelfColliders.Count;
                     i++)
                {
                    Collider collider =
                        target.ShelfColliders[i];

                    if (collider == null)
                        continue;

                    bool enabled =
                        i < target.ShelfColliderEnabled.Count
                            ? target.ShelfColliderEnabled[i]
                            : true;

                    collider.enabled = enabled;
                }
            }

            if (target.ShelfBodies != null)
            {
                for (int i = 0;
                     i < target.ShelfBodies.Count;
                     i++)
                {
                    Rigidbody body = target.ShelfBodies[i];
                    if (body == null)
                        continue;

                    body.constraints =
                        target.ShelfBodyConstraints[i];
                    body.isKinematic =
                        target.ShelfBodyKinematic[i];
                    body.useGravity =
                        target.ShelfBodyGravity[i];
                    body.velocity =
                        target.ShelfBodyVelocity[i];
                    body.angularVelocity =
                        target.ShelfBodyAngularVelocity[i];

                    if (body.isKinematic)
                        body.Sleep();
                }
            }

            target.ShelfPhysicsIsolated = false;
            Physics.SyncTransforms();

            Emit(
                "SHELF_RESTORE",
                "target=" + target.Root.name +
                " cancelled=" + cancelled
            );
        }

        private static void ForceSafeTargetPhysics(RaftRepositionTarget target)
        {
            if (target == null || target.Root == null)
                return;

            if (target.Kind == RaftRepositionTargetKind.CampfireStructure)
            {
                Rigidbody[] bodies =
                    target.Root.GetComponentsInChildren<Rigidbody>(true);

                for (int i = 0; i < bodies.Length; i++)
                    ForceSafeBody(bodies[i]);

                return;
            }

            // For furniture, stills, hooks and especially Container Shelf, only
            // touch the root Rigidbody. Child objects may be legitimate native
            // attachments (crates, torch, etc.) with their own lifecycle.
            ForceSafeBody(target.Root.GetComponent<Rigidbody>());
        }

        private static void ForceSafeBody(Rigidbody rb)
        {
            if (rb == null)
                return;

            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.constraints = RigidbodyConstraints.None;
            rb.Sleep();
        }

        private static float NormalizeYaw(float yaw)
        {
            yaw = Mathf.Repeat(yaw, 360.0f);
            if (yaw > 180.0f)
                yaw -= 360.0f;
            return yaw;
        }

        private static string FormatLocalPose(
            Transform root,
            Construction_STRUCTURE_RAFT raft)
        {
            if (root == null || raft == null)
                return "<none>";

            Vector3 local = raft.transform.InverseTransformPoint(root.position);
            float yaw = RaftFurnitureRuntime.GetLocalYaw(root, raft);

            return
                "(x=" + local.x.ToString("F3") +
                ",h=" + local.y.ToString("F3") +
                ",z=" + local.z.ToString("F3") +
                ",yaw=" + yaw.ToString("F2") + ")";
        }

        private static string SafeReferenceId(object target)
        {
            if (target == null)
                return "<none>";

            try
            {
                Type type = target.GetType();
                while (type != null)
                {
                    PropertyInfo property = type.GetProperty(
                        "ReferenceId",
                        BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly
                    );

                    if (property != null && property.GetIndexParameters().Length == 0)
                    {
                        object value = property.GetValue(target, null);
                        if (value != null)
                            return value.ToString();
                    }

                    type = type.BaseType;
                }
            }
            catch
            {
            }

            return "<unknown>";
        }

        private static void EnsureLogFile()
        {
            if (!String.IsNullOrEmpty(LogFile))
                return;

            string directory = Path.Combine(
                Paths.ConfigPath,
                "StrandedDeepRaftFurniture"
            );

            Directory.CreateDirectory(directory);
            LogFile = Path.Combine(directory, "RepositionRuntime.log");
        }

        private static void Emit(string kind, string message)
        {
            EnsureLogFile();

            string line =
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                " [" + kind + "] " + message;

            if (Plugin.Log != null)
                Plugin.Log.LogInfo("[RaftReposition] " + line);

            try
            {
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
            catch
            {
            }
        }
    }

    internal sealed class RaftSmallStructureFollower : MonoBehaviour
    {
        internal Construction_STRUCTURE_RAFT Raft { get; private set; }

        private Construction_STRUCTURE _structure;
        private Vector3 _localPosition;
        private Quaternion _localRotation;
        private bool _initialized;
        private bool _externalReposition;

        internal void Initialize(
            Construction_STRUCTURE structure,
            Construction_STRUCTURE_RAFT raft)
        {
            if (structure == null || raft == null)
                return;

            _structure = structure;

            bool sameRaft =
                _initialized &&
                Raft == raft;

            Raft = raft;

            if (!sameRaft)
            {
                RaftFurnitureRuntime.RepairSmallStructurePoseOnRaft(
                    structure,
                    raft
                );

                _localPosition =
                    raft.transform.InverseTransformPoint(
                        structure.transform.position
                    );

                _localRotation =
                    Quaternion.Inverse(raft.transform.rotation) *
                    structure.transform.rotation;
            }

            _initialized = true;

            RaftFurnitureRuntime.IgnoreBuiltCollisionWithRaft(
                structure.transform,
                raft,
                true
            );
        }

        internal void SetExternalReposition(
            bool active,
            bool captureCurrentPose)
        {
            if (captureCurrentPose &&
                _structure != null &&
                Raft != null)
            {
                _localPosition =
                    Raft.transform.InverseTransformPoint(
                        _structure.transform.position
                    );

                _localRotation =
                    Quaternion.Inverse(Raft.transform.rotation) *
                    _structure.transform.rotation;
            }

            _externalReposition = active;
        }

        private void LateUpdate()
        {
            if (!_initialized ||
                _structure == null ||
                Raft == null)
                return;

            if (!_externalReposition)
            {
                transform.position =
                    Raft.transform.TransformPoint(
                        _localPosition
                    );

                transform.rotation =
                    Raft.transform.rotation *
                    _localRotation;
            }

            RaftFurnitureRuntime.IgnoreBuiltCollisionWithRaft(
                transform,
                Raft,
                true
            );

            RaftFurnitureRuntime.SyncAttachedCookables(
                _structure
            );
        }
    }

    [HarmonyPatch(typeof(Constructing), "OwnerPlacing")]
    internal static class ConstructingOwnerPlacingPatch
    {
        private static bool Prefix(
            Constructing __instance,
            Transform ghost,
            BeamRay ray,
            float yRotation,
            ref bool __result)
        {
            bool result;

            if (RaftFurnitureRuntime.TryHandleConstructingRaftPlacement(
                    __instance,
                    ghost,
                    ray,
                    yRotation,
                    out result))
            {
                __result = result;
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(ConstructionObject), "OwnerPlacing")]
    internal static class ConstructionObjectOwnerPlacingPatch
    {
        private static bool Prefix(
            ConstructionObject __instance,
            Transform ghost,
            BeamRay ray,
            float yRotation,
            ref bool __result)
        {
            bool result;

            if (RaftFurnitureRuntime.TryHandleFurnitureRaftPlacement(
                    __instance,
                    ghost,
                    ray,
                    yRotation,
                    out result))
            {
                __result = result;
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(PlacementModifier_SnapToTerrain), "Modify")]
    internal static class SnapToTerrainPatch
    {
        private static void Prefix(
            PlacementModifier_SnapToTerrain __instance,
            ref RaftFurnitureRuntime.LayerMaskState __state)
        {
            RaftFurnitureRuntime.ExtendSnapToTerrainMask(
                __instance,
                ref __state
            );
        }

        private static void Postfix(
            PlacementModifier_SnapToTerrain __instance,
            RaftFurnitureRuntime.LayerMaskState __state)
        {
            RaftFurnitureRuntime.RestoreSnapToTerrainMask(
                __instance,
                __state
            );
        }
    }

    [HarmonyPatch(typeof(ConnectorCheck_CheckSnapped), "Check")]
    internal static class CheckSnappedPatch
    {
        private static void Prefix(
            ConnectorCheck_CheckSnapped __instance,
            ref RaftFurnitureRuntime.LayerMaskState __state)
        {
            RaftFurnitureRuntime.ExtendCheckSnappedMask(
                __instance,
                ref __state
            );
        }

        private static void Postfix(
            ConnectorCheck_CheckSnapped __instance,
            RaftFurnitureRuntime.LayerMaskState __state)
        {
            RaftFurnitureRuntime.RestoreCheckSnappedMask(
                __instance,
                __state
            );
        }
    }

    [HarmonyPatch(typeof(Constructing), "OwnerPlacing")]
    internal static class CampfireUpgradeOwnerPlacingPatch
    {
        private static void Prefix(
            Constructing __instance,
            Transform ghost)
        {
            // On an already-snapped hover, _snappedConnector still points
            // to the previous frame here. Ignore collisions before vanilla
            // CheckPlacement / ghost validation runs.
            RaftFurnitureRuntime.HandleUpgradeGhostOnRaft(
                __instance,
                ghost
            );
        }

        private static void Postfix(
            Constructing __instance,
            Transform ghost)
        {
            // First frame over the connector gets the new snapped connector
            // only during OwnerPlacing, so apply again after vanilla logic.
            RaftFurnitureRuntime.HandleUpgradeGhostOnRaft(
                __instance,
                ghost
            );
        }
    }

    [HarmonyPatch(typeof(Ghost), "get_Colliding")]
    internal static class RaftSmokerGhostCollidingPatch
    {
        private static void Postfix(
            Ghost __instance,
            ref bool __result)
        {
            if (!__result)
                return;

            if (RaftFurnitureRuntime.ShouldSuppressSmokerGhostCollision(
                    __instance))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(Constructing), "DoPlace")]
    internal static class ConstructingDoPlacePatch
    {
        private static void Prefix(Constructing __instance)
        {
            RaftFurnitureRuntime.PrepareConstructingForRaft(__instance);
        }

        private static void Postfix(Constructing __instance)
        {
            RaftFurnitureRuntime.FinalizeConstructingOnRaft(__instance);
        }
    }

    [HarmonyPatch(typeof(ConstructionObject), "Place")]
    internal static class ConstructionObjectPlacePatch
    {
        private static void Postfix(ConstructionObject __instance)
        {
            RaftFurnitureRuntime.HandleConstructionObjectPlaced(__instance);
        }
    }

    [HarmonyPatch(typeof(InteractiveObject), "BeginDrag")]
    internal static class InteractiveObjectBeginDragPatch
    {
        private static void Prefix(InteractiveObject __instance)
        {
            RaftFurnitureRuntime.HandleConstructionObjectBeginDrag(__instance);
        }
    }

    [HarmonyPatch(typeof(InteractiveObject), "EndDrag")]
    internal static class InteractiveObjectEndDragPatch
    {
        private static void Postfix(InteractiveObject __instance)
        {
            RaftFurnitureRuntime.HandleConstructionObjectEndDrag(__instance);
        }
    }

    [HarmonyPatch(typeof(Constructing_SMOKER), "Place")]
    internal static class SmokerPlacePatch
    {
        private static void Prefix(
            Constructing_SMOKER __instance,
            ref RaftFurnitureRuntime.SmokerOrderState __state)
        {
            RaftFurnitureRuntime.PrepareSmokerOrder(
                __instance,
                ref __state
            );
        }

        private static void Postfix(
            RaftFurnitureRuntime.SmokerOrderState __state)
        {
            RaftFurnitureRuntime.RestoreSmokerOrder(__state);
        }
    }


}
