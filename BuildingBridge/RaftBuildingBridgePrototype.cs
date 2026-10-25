using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BamEx.StrandedDeep.RaftBuildingBridgePrototype
{
    [BepInPlugin(
        "bamex.strandeddeep.raftbuildingbridgeprototype",
        "Stranded Deep Raft Building Bridge Prototype",
        "0.1.21"
    )]
    public sealed class RaftBuildingBridgePrototypePlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static string RuntimeLogPath;
        internal static RaftBuildingBridgePrototypePlugin Instance;

        private Harmony _harmony;
        private Type _ghostType;
        private Type _crafterType;
        private Type _roofType;
        private Type _constructingType;
        private float _nextAutoRecoveryScanTime = -999f;
        private float _autoRecoveryFastUntil = -999f;
        private int _autoRecoveryScanCount;
        private bool _recoveredFromSave;
        private readonly HashSet<int> _autoPreparedFoundationIds =
            new HashSet<int>();

        private readonly List<Component> _crafters =
            new List<Component>();
        private float _lastCrafterRefreshTime = -999f;
        private float _lastGhostOverrideLogTime = -999f;
        private float _lastGhostConditionLogTime = -999f;
        private float _lastRoofDiagnosticLogTime = -999f;
        private float _nextIsolationReconcileTime = -999f;
        private bool _raftHierarchyInventoryLogged;

        private readonly HashSet<int> _roofModifierDumpedSources =
            new HashSet<int>();

        private sealed class PhysicsIsolationPair
        {
            public Collider Building;
            public Collider Raft;
            public string Key;
        }

        private readonly List<PhysicsIsolationPair> _physicalIsolationPairs =
            new List<PhysicsIsolationPair>();

        private readonly HashSet<string> _physicalIsolationPairKeys =
            new HashSet<string>();

        private Component _targetRaftStructure;

        private Type _foundationType;
        private Type _raftType;
        private Type _connectorType;
        private Type _miniGuidType;
        private Type _multiplayerMngType;

        private GameObject _proxyRoot;
        private Component _proxyFoundation;
        private Component _proxyStructure;
        private readonly List<Component> _proxyFoundations =
            new List<Component>();
        private Component _targetRaftFloor;
        private Vector3 _structureLocalPosition;
        private Quaternion _structureLocalRotation;
        private bool _deployed;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            string dir = Path.Combine(
                Paths.ConfigPath,
                "StrandedDeepRaftBuildingBridgePrototype"
            );

            Directory.CreateDirectory(dir);
            RuntimeLogPath = Path.Combine(dir, "BridgeRuntime.log");

            WriteRaw("");
            WriteRaw("============================================================");
            WriteRaw("RAFT BUILDING BRIDGE PROTOTYPE v0.1.21 START");
            WriteRaw("UTC=" + DateTime.UtcNow.ToString("o"));
            WriteRaw("F9=create ONE shared building structure with hidden WOOD_FOUNDATION bridge on every floor of P1 target raft");
            WriteRaw("F10=cleanup only for incomplete undeployed proxy; blocked for any deployed/recovered bridge");
            WriteRaw("v0.1.7: NO OwnerPlacing patch; Ghost.get_Colliding + post-Place physical raft isolation");
            WriteRaw("v0.1.21: catalog-verified BUILDING member recognition on cold-load (all materials)");
            WriteRaw("============================================================");

            _foundationType = FindType("Beam.Crafting.Construction_FOUNDATION");
            _raftType = FindType("Beam.Crafting.Construction_RAFT");
            _connectorType = FindType("Beam.Crafting.Connector");
            _miniGuidType = FindType("Funlabs.MiniGuid");
            _multiplayerMngType = FindTypeBySimpleName("MultiplayerMng");
            _ghostType = FindType("Beam.Crafting.Ghost");
            _roofType = FindType("Beam.Crafting.Constructing_ROOF");
            _constructingType = FindType("Beam.Crafting.Constructing");
            _autoRecoveryFastUntil = Time.unscaledTime + 30f;
            _crafterType =
                FindType("Beam.Crafting.Crafter") ??
                FindTypeBySimpleName("Crafter");

            Write(
                "INIT",
                "foundationType=" + TypeName(_foundationType) +
                " | raftType=" + TypeName(_raftType) +
                " | connectorType=" + TypeName(_connectorType) +
                " | miniGuidType=" + TypeName(_miniGuidType) +
                " | multiplayerMngType=" + TypeName(_multiplayerMngType) +
                " | ghostType=" + TypeName(_ghostType) +
                " | roofType=" + TypeName(_roofType) +
                " | constructingType=" + TypeName(_constructingType) +
                " | crafterType=" + TypeName(_crafterType)
            );

            InstallRuntimePatches();
            RefreshCrafters("AWAKE");

            Logger.LogWarning(
                "Raft Building Bridge Prototype v0.1.21 loaded. " +
                "F9 creates a new bridge when none exists; saved bridges auto-recover after cold load. " +
                "OwnerPlacing is untouched. F10 is blocked for deployed/recovered bridges."
            );
        }

        private void Update()
        {
            // A loaded zone can disappear/reappear while the plugin instance
            // survives. If the bound Unity objects are gone, clear only our
            // runtime references and allow the saved bridge to be discovered
            // again. Never destroy native save objects here.
            if (_deployed &&
                (_proxyFoundation == null ||
                 _proxyStructure == null ||
                 _targetRaftFloor == null ||
                 _targetRaftStructure == null))
            {
                Write(
                    "AUTO_RECOVER_BINDING_LOST",
                    "deployed=" + _deployed +
                    " | recoveredFromSave=" +
                        _recoveredFromSave
                );

                ClearRuntimeBinding(
                    "BINDING_LOST"
                );

                _autoRecoveryFastUntil =
                    Time.unscaledTime + 15f;
            }

            if (!_deployed &&
                Time.unscaledTime >=
                    _nextAutoRecoveryScanTime)
            {
                float interval =
                    Time.unscaledTime <
                        _autoRecoveryFastUntil
                    ? 0.10f
                    : 1.00f;

                _nextAutoRecoveryScanTime =
                    Time.unscaledTime +
                    interval;

                try
                {
                    TryAutoRecoverSavedBridge();
                }
                catch (Exception ex)
                {
                    Write(
                        "AUTO_RECOVER_ERROR",
                        ex.GetType().FullName +
                        ": " + ex.Message +
                        Environment.NewLine +
                        ex.StackTrace
                    );
                }
            }

            if (Input.GetKeyDown(KeyCode.F9))
            {
                if (_deployed)
                {
                    Write(
                        "CONTROL",
                        "F9 ignored: bridge already deployed/recovered"
                    );
                    return;
                }

                try
                {
                    DeployNativeProxy();
                }
                catch (Exception ex)
                {
                    Write(
                        "ERROR",
                        "DeployNativeProxy failed: " +
                        ex.GetType().FullName + ": " + ex.Message +
                        Environment.NewLine + ex.StackTrace
                    );
                }
            }

            if (Input.GetKeyDown(KeyCode.F10))
            {
                if (_deployed)
                {
                    Write(
                        "CONTROL",
                        "F10 blocked: deployed/recovered bridge is save-backed and must not be destroyed"
                    );
                    return;
                }

                DestroyProxy(
                    "EMERGENCY_F10_UNDEPLOYED_ONLY"
                );
            }
        }

        private void LateUpdate()
        {
            if (!_deployed || _proxyFoundation == null)
                return;

            // The hidden vanilla Construction_STRUCTURE is the bridge root.
            // Follow the selected raft floor without physically parenting the
            // structure into the raft hierarchy.
            if (_proxyStructure != null &&
                _targetRaftFloor != null)
            {
                Transform raftTransform =
                    _targetRaftFloor.transform;

                Transform structureTransform =
                    _proxyStructure.transform;

                structureTransform.position =
                    raftTransform.TransformPoint(
                        _structureLocalPosition
                    );

                structureTransform.rotation =
                    raftTransform.rotation *
                    _structureLocalRotation;
            }

            // Construction stages can enable/replace physical colliders
            // AFTER Place(). Maintain own-raft collision isolation as a
            // persistent invariant rather than a one-shot post-place action.
            if (Time.unscaledTime >= _nextIsolationReconcileTime)
            {
                _nextIsolationReconcileTime =
                    Time.unscaledTime + 0.25f;

                MaintainBuiltStructureRaftIsolation(
                    "PERIODIC"
                );
            }

            // Periodic state breadcrumbs without per-frame spam.
            if (Time.frameCount % 300 == 0)
            {
                Write(
                    "PROXY_STATE_PERIODIC",
                    "foundation=" +
                    DescribeConstructing(_proxyFoundation) +
                    " | foundationState=" +
                    DumpImportantState(_proxyFoundation) +
                    " | hiddenFoundationCount=" +
                    _proxyFoundations.Count +
                    " | structure=" +
                    DescribeConstructing(_proxyStructure)
                );
            }
        }

        private void OnDestroy()
        {
            RestoreBuiltStructureRaftIsolation(
                "PLUGIN_DESTROY"
            );

            try
            {
                if (_harmony != null)
                    _harmony.UnpatchSelf();
            }
            catch
            {
            }

            // v0.1.16 persistence rule:
            // plugin shutdown must never delete the native vanilla
            // Construction_STRUCTURE/foundations that are now part of the
            // game's own save. Only release plugin-side references.
            ClearRuntimeBinding(
                "PLUGIN_DESTROY"
            );

            if (System.Object.ReferenceEquals(
                    Instance,
                    this))
            {
                Instance = null;
            }
        }

        // Atlas Batch 08 catalog, 2026-10-25:
        // - 51 active BUILDING recipes (81 is the bridge's separate anchor)
        // - 10 known legacy DRIFTWOOD building prefabs for old saves.
        // This is a strict prefabId + exact component type allowlist.  Do not
        // replace it with an ID interval: some prefabs in those intervals
        // are crafting materials, containers or aggregate STRUCTURE objects.
        // A recognized member is only a RECOVERY fingerprint; this does not
        // enable additional placement connectors on the raft.
        private static bool IsRecognizedBuildingMember(
            Component member,
            int prefabId)
        {
            if (member == null || prefabId == 81)
                return false;

            string typeName = member.GetType().FullName;

            switch (typeName)
            {
                case "Beam.Crafting.Constructing_ROOF":
                    switch (prefabId)
                    {
                        case 239:
                        case 240:
                        case 241:
                        case 242:
                        case 243:
                        case 244:
                        case 245:
                        case 246:
                            return true;
                    }
                    break;
                case "Beam.Crafting.Construction_ARCH":
                    switch (prefabId)
                    {
                        case 112:
                        case 114:
                        case 116:
                        case 118:
                        case 231:
                            return true;
                    }
                    break;
                case "Beam.Crafting.Construction_DOOR":
                    switch (prefabId)
                    {
                        case 91:
                        case 113:
                        case 115:
                        case 117:
                        case 119:
                        case 291:
                            return true;
                    }
                    break;
                case "Beam.Crafting.Construction_FLOOR":
                    switch (prefabId)
                    {
                        case 75:
                        case 79:
                        case 82:
                        case 88:
                        case 101:
                        case 104:
                        case 107:
                        case 109:
                        case 232:
                        case 237:
                            return true;
                    }
                    break;
                case "Beam.Crafting.Construction_FOUNDATION":
                    switch (prefabId)
                    {
                        case 76:
                        case 78:
                        case 87:
                        case 102:
                        case 105:
                        case 108:
                        case 110:
                        case 233:
                        case 238:
                            return true;
                    }
                    break;
                case "Beam.Crafting.Construction_PANEL":
                    switch (prefabId)
                    {
                        case 77:
                        case 80:
                        case 83:
                        case 86:
                        case 89:
                        case 93:
                        case 120:
                        case 121:
                        case 122:
                        case 123:
                        case 124:
                        case 125:
                        case 126:
                        case 127:
                        case 234:
                        case 235:
                        case 236:
                            return true;
                    }
                    break;
                case "Beam.Crafting.Construction_STEPS":
                    switch (prefabId)
                    {
                        case 84:
                        case 100:
                        case 103:
                        case 106:
                        case 292:
                            return true;
                    }
                    break;
            }

            return false;
        }

        private void TryAutoRecoverSavedBridge()
        {
            if (_foundationType == null ||
                _raftType == null ||
                _constructingType == null)
            {
                return;
            }

            _autoRecoveryScanCount++;

            UnityEngine.Object[] loaded =
                UnityEngine.Object.FindObjectsOfType(
                    _constructingType
                );

            Dictionary<int, List<Component>>
                membersByStructure =
                    new Dictionary<int, List<Component>>();

            Dictionary<int, Component>
                structures =
                    new Dictionary<int, Component>();

            for (int i = 0;
                 i < loaded.Length;
                 i++)
            {
                Component construction =
                    loaded[i] as Component;

                if (construction == null)
                    continue;

                Component structure =
                    GetMember(
                        construction,
                        "Structure",
                        "_structure",
                        "structure"
                    ) as Component;

                if (structure == null)
                    continue;

                int key =
                    structure.GetInstanceID();

                List<Component> members;

                if (!membersByStructure.TryGetValue(
                        key,
                        out members))
                {
                    members =
                        new List<Component>();

                    membersByStructure.Add(
                        key,
                        members
                    );

                    structures.Add(
                        key,
                        structure
                    );
                }

                members.Add(
                    construction
                );
            }

            // Enumerate loaded rafts once and group floors by their native
            // Construction_STRUCTURE_RAFT.
            UnityEngine.Object[] raftObjects =
                UnityEngine.Object.FindObjectsOfType(
                    _raftType
                );

            Dictionary<int, List<Component>>
                raftFloorsByStructure =
                    new Dictionary<int, List<Component>>();

            Dictionary<int, Component>
                raftStructures =
                    new Dictionary<int, Component>();

            for (int i = 0;
                 i < raftObjects.Length;
                 i++)
            {
                Component raft =
                    raftObjects[i] as Component;

                if (raft == null ||
                    !IsRaftFloor(
                        GetPrefabId(raft),
                        GetUnityName(raft)))
                {
                    continue;
                }

                Component raftStructure =
                    GetMember(
                        raft,
                        "Structure",
                        "_structure",
                        "structure"
                    ) as Component;

                if (raftStructure == null)
                    continue;

                int key =
                    raftStructure.GetInstanceID();

                List<Component> floors;

                if (!raftFloorsByStructure.TryGetValue(
                        key,
                        out floors))
                {
                    floors =
                        new List<Component>();

                    raftFloorsByStructure.Add(
                        key,
                        floors
                    );

                    raftStructures.Add(
                        key,
                        raftStructure
                    );
                }

                floors.Add(
                    raft
                );
            }

            List<Component> bestFoundations = null;
            Component bestStructure = null;
            Component bestRaftStructure = null;
            int bestRaftFloorCount = 0;
            float bestRootDistance = Single.MaxValue;
            int candidateGroups = 0;

            foreach (
                KeyValuePair<int, List<Component>>
                    pair in membersByStructure)
            {
                Component structure =
                    structures[pair.Key];

                if (structure == null ||
                    GetPrefabId(structure) != 97)
                {
                    continue;
                }

                List<Component> foundations =
                    new List<Component>();

                // v0.1.21: a structure is a saved bridge candidate only
                // when it has the native hidden WOOD_FOUNDATION anchors plus
                // at least one genuine construction member.
                // The old broad ID ranges contained craft items and missed
                // every BRICK definition, PLANK WINDOW, etc.
                int buildingMembers = 0;
                StringBuilder buildingPrefabIds =
                    new StringBuilder();
                StringBuilder ignoredPrefabIds =
                    new StringBuilder();

                for (int i = 0;
                     i < pair.Value.Count;
                     i++)
                {
                    Component member =
                        pair.Value[i];

                    if (member == null)
                        continue;

                    int prefabId =
                        GetPrefabId(member);

                    // Bridge-owned native foundation is an anchor,
                    // not evidence of a user-built structure.
                    if (prefabId == 81 &&
                        _foundationType
                            .IsInstanceOfType(member))
                    {
                        foundations.Add(
                            member
                        );
                        continue;
                    }

                    if (IsRecognizedBuildingMember(
                            member,
                            prefabId))
                    {
                        buildingMembers++;

                        if (buildingPrefabIds.Length < 220)
                        {
                            if (buildingPrefabIds.Length > 0)
                                buildingPrefabIds.Append(',');
                            buildingPrefabIds.Append(prefabId);
                        }
                    }
                    else if (ignoredPrefabIds.Length < 220)
                    {
                        if (ignoredPrefabIds.Length > 0)
                            ignoredPrefabIds.Append(',');
                        ignoredPrefabIds.Append(prefabId);
                    }
                }

                if (foundations.Count < 4 ||
                    buildingMembers < 1)
                {
                    if (foundations.Count >= 4 &&
                        (_autoRecoveryScanCount <= 5 ||
                         _autoRecoveryScanCount % 20 == 0))
                    {
                        Write(
                            "AUTO_RECOVER_GROUP_REJECT",
                            "reason=NO_RECOGNIZED_BUILDING" +
                            " | structure=" +
                                DescribeConstructing(structure) +
                            " | foundations=" +
                                foundations.Count +
                            " | ignoredPrefabIds=" +
                                ignoredPrefabIds.ToString()
                        );
                    }
                    continue;
                }

                candidateGroups++;

                Transform structureSaveContainer =
                    FindAncestorByName(
                        structure.transform,
                        "SaveContainer"
                    );

                Component nearestRaftStructure = null;
                int nearestRaftFloorCount = 0;
                float nearestRootDistance =
                    Single.MaxValue;

                foreach (
                    KeyValuePair<int, List<Component>>
                        raftPair in raftFloorsByStructure)
                {
                    Component raftStructure =
                        raftStructures[raftPair.Key];

                    if (raftStructure == null)
                        continue;

                    Transform raftSaveContainer =
                        FindAncestorByName(
                            raftStructure.transform,
                            "SaveContainer"
                        );

                    // The bridge and its owning raft must be loaded from the
                    // same zone SaveContainer.
                    if (structureSaveContainer != null &&
                        raftSaveContainer != null &&
                        !System.Object.ReferenceEquals(
                            structureSaveContainer,
                            raftSaveContainer))
                    {
                        continue;
                    }

                    int raftFloorCount =
                        raftPair.Value.Count;

                    // A bridge contains one hidden foundation per raft floor
                    // at the moment it was created. The raft may later have
                    // been expanded, so allow >=, never fewer.
                    if (raftFloorCount <
                        foundations.Count)
                    {
                        continue;
                    }

                    float rootDistance =
                        Vector3.Distance(
                            structure.transform.position,
                            raftStructure.transform.position
                        );

                    if (_autoRecoveryScanCount <= 5 ||
                        _autoRecoveryScanCount % 20 == 0)
                    {
                        Write(
                            "AUTO_RECOVER_ROOT_PAIR",
                            "structure=" +
                                DescribeConstructing(
                                    structure) +
                            " | foundations=" +
                                foundations.Count +
                            " | buildingMembers=" +
                                buildingMembers +
                            " | buildingPrefabIds=" +
                                buildingPrefabIds.ToString() +
                            " | raftStructure=" +
                                DescribeConstructing(
                                    raftStructure) +
                            " | raftFloors=" +
                                raftFloorCount +
                            " | sameSaveContainer=" +
                                ((structureSaveContainer == null ||
                                  raftSaveContainer == null)
                                    ? "<unknown>"
                                    : System.Object.ReferenceEquals(
                                        structureSaveContainer,
                                        raftSaveContainer
                                      ).ToString()) +
                            " | rootDistance=" +
                                rootDistance
                                    .ToString("0.000")
                        );
                    }

                    if (rootDistance <
                        nearestRootDistance)
                    {
                        nearestRootDistance =
                            rootDistance;

                        nearestRaftStructure =
                            raftStructure;

                        nearestRaftFloorCount =
                            raftFloorCount;
                    }
                }

                // v0.1.18 authority:
                // composition fingerprint + same SaveContainer + a nearby
                // raft STRUCTURE root. The save probe showed our bridge and
                // raft roots only a few metres apart, while floor transforms
                // are not stable comparison points after native cold load.
                const float maxRootDistance =
                    25.0f;

                bool matched =
                    nearestRaftStructure != null &&
                    nearestRootDistance <=
                        maxRootDistance;

                Write(
                    "AUTO_RECOVER_CANDIDATE",
                    "structure=" +
                        DescribeConstructing(
                            structure) +
                    " | members=" +
                        pair.Value.Count +
                    " | foundations=" +
                        foundations.Count +
                    " | buildingMembers=" +
                        buildingMembers +
                    " | buildingPrefabIds=" +
                        buildingPrefabIds.ToString() +
                    " | matched=" +
                        matched +
                    " | raftStructure=" +
                        DescribeConstructing(
                            nearestRaftStructure) +
                    " | raftFloors=" +
                        nearestRaftFloorCount +
                    " | rootDistance=" +
                        nearestRootDistance
                            .ToString("0.000")
                );

                if (!matched)
                    continue;

                if (nearestRootDistance <
                    bestRootDistance)
                {
                    bestFoundations =
                        foundations;

                    bestStructure =
                        structure;

                    bestRaftStructure =
                        nearestRaftStructure;

                    bestRaftFloorCount =
                        nearestRaftFloorCount;

                    bestRootDistance =
                        nearestRootDistance;
                }
            }

            if (bestFoundations == null ||
                bestStructure == null ||
                bestRaftStructure == null)
            {
                if (_autoRecoveryScanCount <= 5 ||
                    _autoRecoveryScanCount % 20 == 0)
                {
                    Write(
                        "AUTO_RECOVER_SCAN",
                        "loadedConstructing=" +
                            loaded.Length +
                        " | structures=" +
                            membersByStructure.Count +
                        " | raftStructures=" +
                            raftFloorsByStructure.Count +
                        " | candidateGroups=" +
                            candidateGroups +
                        " | result=NO_ROOT_MATCH"
                    );
                }

                return;
            }

            // Native cold load preserves the bridge Structure and visible
            // building pieces, but secondary hidden foundations are restored
            // with incorrect transforms. Those foundations own the WALL /
            // HALF_WALL / ROOF target connectors, so the visible building can
            // follow the raft while new pieces no longer snap.
            //
            // Reconstruct the ORIGINAL bridge grid from native ordering:
            // Structure.Constructions was populated primary-foundation first,
            // then secondary foundations in FindFloorsForSelectedRaft order.
            List<Component> orderedFoundations =
                GetOrderedRecoveredFoundations(
                    bestStructure
                );

            if (orderedFoundations.Count !=
                bestFoundations.Count)
            {
                Write(
                    "AUTO_RECOVER_GRID_FAIL",
                    "reason=FOUNDATION_ORDER_COUNT_MISMATCH" +
                    " | grouped=" +
                        bestFoundations.Count +
                    " | structureOrdered=" +
                        orderedFoundations.Count
                );
                return;
            }

            Component primaryFoundation =
                orderedFoundations[0];

            Component primaryRaftFloor =
                FindNearestFloorForRaftStructure(
                    primaryFoundation,
                    bestRaftStructure
                );

            if (primaryRaftFloor == null)
            {
                Write(
                    "AUTO_RECOVER_GRID_FAIL",
                    "reason=PRIMARY_RAFT_FLOOR_NOT_FOUND" +
                    " | primary=" +
                        DescribeConstructing(
                            primaryFoundation)
                );
                return;
            }

            float primaryDistance =
                Vector3.Distance(
                    primaryFoundation.transform.position,
                    primaryRaftFloor.transform.position
                );

            // The persistence probe proved exactly one foundation remains
            // geometrically aligned after cold load: the primary created via
            // native CheckStructure. Require it to be close before using the
            // saved Construction ordering as authority.
            if (primaryDistance > 2.0f)
            {
                Write(
                    "AUTO_RECOVER_GRID_FAIL",
                    "reason=PRIMARY_NOT_ALIGNED" +
                    " | distance=" +
                        primaryDistance
                            .ToString("0.000") +
                    " | primary=" +
                        DescribeConstructing(
                            primaryFoundation) +
                    " | floor=" +
                        DescribeConstructing(
                            primaryRaftFloor)
                );
                return;
            }

            List<Component> orderedRaftFloors =
                FindFloorsForSelectedRaft(
                    primaryRaftFloor,
                    bestRaftStructure
                );

            if (orderedRaftFloors.Count <
                orderedFoundations.Count)
            {
                Write(
                    "AUTO_RECOVER_GRID_FAIL",
                    "reason=RAFT_FLOOR_COUNT_TOO_SMALL" +
                    " | foundations=" +
                        orderedFoundations.Count +
                    " | raftFloors=" +
                        orderedRaftFloors.Count
                );
                return;
            }

            for (int i = 0;
                 i < orderedFoundations.Count;
                 i++)
            {
                Component foundation =
                    orderedFoundations[i];

                Component floor =
                    orderedRaftFloors[i];

                Vector3 beforePosition =
                    foundation.transform.position;

                Quaternion beforeRotation =
                    foundation.transform.rotation;

                foundation.transform.position =
                    floor.transform.position;

                foundation.transform.rotation =
                    floor.transform.rotation;

                Write(
                    "AUTO_RECOVER_GRID_REPAIR",
                    "index=" + i +
                    " | foundation=" +
                        DescribeConstructing(
                            foundation) +
                    " | floor=" +
                        DescribeConstructing(
                            floor) +
                    " | beforePos=" +
                        FormatVector(
                            beforePosition) +
                    " | afterPos=" +
                        FormatVector(
                            foundation.transform.position) +
                    " | moved=" +
                        Vector3.Distance(
                            beforePosition,
                            foundation.transform.position
                        ).ToString("0.000") +
                    " | beforeRotY=" +
                        beforeRotation.eulerAngles.y
                            .ToString("0.0") +
                    " | afterRotY=" +
                        foundation.transform.rotation
                            .eulerAngles.y
                            .ToString("0.0")
                );
            }

            Physics.SyncTransforms();

            bestFoundations =
                orderedFoundations;

            // Now that all connector owners are back on their raft-floor
            // transforms, neutralize their render/solid physics while keeping
            // the bridge target trigger colliders active.
            for (int i = 0;
                 i < bestFoundations.Count;
                 i++)
            {
                Component foundation =
                    bestFoundations[i];

                PrepareHiddenFoundation(
                    foundation,
                    i
                );

                _autoPreparedFoundationIds.Add(
                    foundation.GetInstanceID()
                );

                Write(
                    "AUTO_RECOVER_PREPARE",
                    "foundation=" +
                        DescribeConstructing(
                            foundation) +
                    " | runtimeName=" +
                        foundation.gameObject.name +
                    " | path=" +
                        GetTransformPath(
                            foundation.transform)
                );
            }

            int reboundMembers =
                ReparentRecoveredStructureMembers(
                    bestStructure
                );

            _proxyStructure =
                bestStructure;

            _proxyFoundations.Clear();

            for (int i = 0;
                 i < bestFoundations.Count;
                 i++)
            {
                _proxyFoundations.Add(
                    bestFoundations[i]
                );
            }

            _proxyFoundation =
                bestFoundations[0];

            _proxyRoot =
                _proxyFoundation.gameObject;

            // For recovered bridges, anchor directly to the native raft
            // STRUCTURE root rather than a Construction_RAFT floor. The
            // structure root is the stable rigidbody authority across save/load.
            _targetRaftFloor =
                bestRaftStructure;

            _targetRaftStructure =
                bestRaftStructure;

            _structureLocalPosition =
                bestRaftStructure.transform
                    .InverseTransformPoint(
                        bestStructure.transform.position
                    );

            _structureLocalRotation =
                Quaternion.Inverse(
                    bestRaftStructure.transform.rotation
                ) *
                bestStructure.transform.rotation;

            _raftHierarchyInventoryLogged = false;
            _nextIsolationReconcileTime = -999f;
            _deployed = true;
            _recoveredFromSave = true;

            MaintainBuiltStructureRaftIsolation(
                "AUTO_RECOVER"
            );

            RefreshCrafters(
                "AUTO_RECOVER_DONE"
            );

            Write(
                "AUTO_RECOVER_DONE",
                "structure=" +
                    DescribeConstructing(
                        bestStructure) +
                " | hiddenFoundations=" +
                    bestFoundations.Count +
                " | reboundMembers=" +
                    reboundMembers +
                " | raftStructure=" +
                    DescribeConstructing(
                        bestRaftStructure) +
                " | raftFloors=" +
                    bestRaftFloorCount +
                " | snapGridRepaired=" +
                    bestFoundations.Count +
                " | rootDistance=" +
                    bestRootDistance
                        .ToString("0.000") +
                " | localPos=" +
                    FormatVector(
                        _structureLocalPosition) +
                " | localRotY=" +
                    _structureLocalRotation
                        .eulerAngles.y
                        .ToString("0.0")
            );

            Logger.LogWarning(
                "Raft Building Bridge v0.1.21: saved bridge auto-recovered with snap-grid reconstruction."
            );
        }

        private List<Component> GetOrderedRecoveredFoundations(
            Component structure)
        {
            List<Component> result =
                new List<Component>();

            if (structure == null ||
                _foundationType == null)
            {
                return result;
            }

            object raw =
                GetMember(
                    structure,
                    "Constructions",
                    "_constructions",
                    "constructions"
                );

            System.Collections.IEnumerable enumerable =
                raw as System.Collections.IEnumerable;

            if (enumerable == null)
            {
                Write(
                    "AUTO_RECOVER_FOUNDATION_ORDER",
                    "authority=<missing Structure.Constructions>"
                );
                return result;
            }

            int constructionIndex = 0;

            foreach (object item in enumerable)
            {
                Component component =
                    item as Component;

                if (component == null &&
                    item is GameObject)
                {
                    component =
                        ((GameObject)item)
                            .GetComponent(
                                _foundationType
                            );
                }

                if (component != null &&
                    GetPrefabId(component) == 81 &&
                    _foundationType
                        .IsInstanceOfType(component))
                {
                    result.Add(
                        component
                    );

                    Write(
                        "AUTO_RECOVER_FOUNDATION_ORDER",
                        "constructionIndex=" +
                            constructionIndex +
                        " | foundationIndex=" +
                            (result.Count - 1) +
                        " | " +
                            DescribeConstructing(
                                component) +
                        " | pos=" +
                            FormatVector(
                                component.transform.position)
                    );
                }

                constructionIndex++;
            }

            return result;
        }

        private Component FindNearestFloorForRaftStructure(
            Component source,
            Component raftStructure)
        {
            if (source == null ||
                raftStructure == null ||
                _raftType == null)
            {
                return null;
            }

            UnityEngine.Object[] raftObjects =
                UnityEngine.Object.FindObjectsOfType(
                    _raftType
                );

            Component nearest = null;
            float nearestSq =
                Single.MaxValue;

            for (int i = 0;
                 i < raftObjects.Length;
                 i++)
            {
                Component raft =
                    raftObjects[i] as Component;

                if (raft == null ||
                    !IsRaftFloor(
                        GetPrefabId(raft),
                        GetUnityName(raft)))
                {
                    continue;
                }

                object owner =
                    GetMember(
                        raft,
                        "Structure",
                        "_structure",
                        "structure"
                    );

                if (owner == null ||
                    !System.Object.ReferenceEquals(
                        owner,
                        raftStructure))
                {
                    continue;
                }

                float sq =
                    Vector3.SqrMagnitude(
                        source.transform.position -
                        raft.transform.position
                    );

                if (sq < nearestSq)
                {
                    nearestSq = sq;
                    nearest = raft;
                }
            }

            Write(
                "AUTO_RECOVER_PRIMARY_FLOOR",
                "primary=" +
                    DescribeConstructing(source) +
                " | floor=" +
                    DescribeConstructing(nearest) +
                " | distance=" +
                    (nearest == null
                        ? "<none>"
                        : Mathf.Sqrt(nearestSq)
                            .ToString("0.000"))
            );

            return nearest;
        }

        private Component FindPrimaryRecoveredFoundation(
            List<Component> foundations)
        {
            if (foundations == null)
                return null;

            for (int i = 0;
                 i < foundations.Count;
                 i++)
            {
                Component foundation =
                    foundations[i];

                if (foundation != null &&
                    foundation.gameObject.name ==
                        "~RaftBridgeNativeFoundation_81")
                {
                    return foundation;
                }
            }

            return foundations.Count > 0
                ? foundations[0]
                : null;
        }

        private int ReparentRecoveredStructureMembers(
            Component structure)
        {
            if (structure == null ||
                _constructingType == null)
            {
                return 0;
            }

            UnityEngine.Object[] constructions =
                UnityEngine.Object.FindObjectsOfType(
                    _constructingType
                );

            int rebound = 0;

            for (int i = 0;
                 i < constructions.Length;
                 i++)
            {
                Component construction =
                    constructions[i] as Component;

                if (construction == null ||
                    System.Object.ReferenceEquals(
                        construction,
                        structure))
                {
                    continue;
                }

                object ownerStructure =
                    GetMember(
                        construction,
                        "Structure",
                        "_structure",
                        "structure"
                    );

                if (ownerStructure == null ||
                    !System.Object.ReferenceEquals(
                        ownerStructure,
                        structure
                    ))
                {
                    continue;
                }

                Transform t =
                    construction.transform;

                if (t == structure.transform ||
                    t.IsChildOf(
                        structure.transform))
                {
                    continue;
                }

                t.SetParent(
                    structure.transform,
                    true
                );

                rebound++;
            }

            Write(
                "AUTO_RECOVER_MEMBERS",
                "structure=" +
                    DescribeConstructing(
                        structure) +
                " | rebound=" +
                    rebound
            );

            return rebound;
        }

        private bool TryFindOwningRaftForRecoveredBridge(
            List<Component> foundations,
            out Component anchorFloor,
            out Component raftStructure,
            out int matchedFoundations,
            out float aggregateDistance)
        {
            anchorFloor = null;
            raftStructure = null;
            matchedFoundations = 0;
            aggregateDistance = Single.MaxValue;

            if (foundations == null ||
                foundations.Count == 0 ||
                _raftType == null)
            {
                return false;
            }

            UnityEngine.Object[] raftObjects =
                UnityEngine.Object.FindObjectsOfType(
                    _raftType
                );

            Dictionary<int, List<Component>>
                floorsByStructure =
                    new Dictionary<int, List<Component>>();

            Dictionary<int, Component>
                structures =
                    new Dictionary<int, Component>();

            for (int i = 0;
                 i < raftObjects.Length;
                 i++)
            {
                Component raft =
                    raftObjects[i] as Component;

                if (raft == null ||
                    !IsRaftFloor(
                        GetPrefabId(raft),
                        GetUnityName(raft)))
                {
                    continue;
                }

                Component structure =
                    GetMember(
                        raft,
                        "Structure",
                        "_structure",
                        "structure"
                    ) as Component;

                if (structure == null)
                    continue;

                int key =
                    structure.GetInstanceID();

                List<Component> floors;

                if (!floorsByStructure.TryGetValue(
                        key,
                        out floors))
                {
                    floors =
                        new List<Component>();

                    floorsByStructure.Add(
                        key,
                        floors
                    );

                    structures.Add(
                        key,
                        structure
                    );
                }

                floors.Add(
                    raft
                );
            }

            if (floorsByStructure.Count == 0)
                return false;

            int requiredMatches =
                Math.Max(
                    1,
                    (foundations.Count * 8 + 9) / 10
                );

            const float matchRadius =
                1.50f;

            float matchRadiusSq =
                matchRadius *
                matchRadius;

            int bestMatched = -1;
            float bestAggregate =
                Single.MaxValue;

            List<Component> bestFloors =
                null;

            Component bestStructure =
                null;

            foreach (
                KeyValuePair<int, List<Component>>
                    pair in floorsByStructure)
            {
                List<Component> floors =
                    pair.Value;

                int matched = 0;
                float aggregate = 0f;

                for (int i = 0;
                     i < foundations.Count;
                     i++)
                {
                    Component foundation =
                        foundations[i];

                    if (foundation == null)
                        continue;

                    float nearest =
                        Single.MaxValue;

                    for (int j = 0;
                         j < floors.Count;
                         j++)
                    {
                        float sq =
                            Vector3.SqrMagnitude(
                                foundation.transform
                                    .position -
                                floors[j].transform
                                    .position
                            );

                        if (sq < nearest)
                            nearest = sq;
                    }

                    if (nearest <=
                        matchRadiusSq)
                    {
                        matched++;
                    }

                    aggregate +=
                        Mathf.Sqrt(
                            Math.Min(
                                nearest,
                                10000f
                            )
                        );
                }

                if (matched > bestMatched ||
                    (matched == bestMatched &&
                     aggregate <
                        bestAggregate))
                {
                    bestMatched =
                        matched;

                    bestAggregate =
                        aggregate;

                    bestFloors =
                        floors;

                    bestStructure =
                        structures[pair.Key];
                }
            }

            matchedFoundations =
                Math.Max(
                    0,
                    bestMatched
                );

            aggregateDistance =
                bestAggregate;

            if (bestFloors == null ||
                bestStructure == null ||
                bestMatched <
                    requiredMatches)
            {
                return false;
            }

            Component nearestFloor = null;
            float nearestPrimary =
                Single.MaxValue;

            // Runtime foundation names are not stable across native load.
            // Pick the closest foundation<->raft-floor pair in the already
            // validated geometric group.
            for (int f = 0;
                 f < foundations.Count;
                 f++)
            {
                Component foundation =
                    foundations[f];

                if (foundation == null)
                    continue;

                for (int i = 0;
                     i < bestFloors.Count;
                     i++)
                {
                    float sq =
                        Vector3.SqrMagnitude(
                            foundation.transform.position -
                            bestFloors[i].transform.position
                        );

                    if (sq < nearestPrimary)
                    {
                        nearestPrimary = sq;
                        nearestFloor =
                            bestFloors[i];
                    }
                }
            }

            if (nearestFloor == null ||
                nearestPrimary >
                    matchRadiusSq)
            {
                return false;
            }

            anchorFloor =
                nearestFloor;

            raftStructure =
                bestStructure;

            Write(
                "AUTO_RECOVER_MATCH",
                "candidateFoundations=" +
                    foundations.Count +
                " | requiredMatches=" +
                    requiredMatches +
                " | matchedFoundations=" +
                    bestMatched +
                " | aggregateDistance=" +
                    bestAggregate
                        .ToString("0.000") +
                " | anchorDistance=" +
                    Mathf.Sqrt(
                        nearestPrimary)
                        .ToString("0.000") +
                " | raftStructure=" +
                    DescribeConstructing(
                        bestStructure) +
                " | anchorFloor=" +
                    DescribeConstructing(
                        nearestFloor)
            );

            return true;
        }

        private void ClearRuntimeBinding(
            string reason)
        {
            RestoreBuiltStructureRaftIsolation(
                "CLEAR:" + reason
            );

            Write(
                "RUNTIME_BINDING_CLEAR",
                "reason=" + reason +
                " | recoveredFromSave=" +
                    _recoveredFromSave
            );

            _proxyRoot = null;
            _proxyFoundation = null;
            _proxyStructure = null;
            _proxyFoundations.Clear();
            _targetRaftFloor = null;
            _targetRaftStructure = null;
            _deployed = false;
            _recoveredFromSave = false;
            _raftHierarchyInventoryLogged = false;
            _autoPreparedFoundationIds.Clear();
            _physicalIsolationPairs.Clear();
            _physicalIsolationPairKeys.Clear();
        }

        private void DeployNativeProxy()
        {
            if (_foundationType == null ||
                _raftType == null ||
                _connectorType == null ||
                _miniGuidType == null ||
                _multiplayerMngType == null)
            {
                Write("DEPLOY_FAIL", "Required runtime types are missing.");
                return;
            }

            Component raftFloor = FindTargetRaftFloor();
            if (raftFloor == null)
            {
                Write("DEPLOY_FAIL", "No loaded raft floor found.");
                return;
            }

            _targetRaftFloor = raftFloor;
            _targetRaftStructure =
                GetMember(
                    raftFloor,
                    "Structure",
                    "_structure",
                    "structure"
                ) as Component;

            if (_targetRaftStructure == null)
            {
                Write(
                    "DEPLOY_FAIL",
                    "Selected raft floor has no Construction_STRUCTURE_RAFT."
                );
                return;
            }

            Write(
                "RAFT_STRUCTURE_SELECTED",
                "floor=" + DescribeConstructing(raftFloor) +
                " | raftStructure=" +
                    DescribeConstructing(_targetRaftStructure)
            );

            List<Component> raftFloors =
                FindFloorsForSelectedRaft(
                    raftFloor,
                    _targetRaftStructure
                );

            if (raftFloors.Count == 0)
            {
                Write(
                    "DEPLOY_FAIL",
                    "No raft floors belonging to selected raft were found."
                );
                return;
            }

            Write(
                "RAFT_FLOOR_SET",
                "count=" + raftFloors.Count
            );

            for (int i = 0; i < raftFloors.Count; i++)
            {
                Write(
                    "RAFT_FLOOR_MEMBER",
                    "index=" + i +
                    " | " +
                    DescribeConstructing(raftFloors[i]) +
                    " | pos=" +
                    FormatVector(
                        raftFloors[i].transform.position)
                );
            }

            MethodInfo nativeInstantiate =
                FindNativeInstantiateMethod();

            if (nativeInstantiate == null)
            {
                Write(
                    "DEPLOY_FAIL",
                    "Matching MultiplayerMng.Instantiate<T>(UInt32,Vector3,Quaternion,MiniGuid,Transform) not found."
                );

                DumpInstantiateCandidates();
                return;
            }

            MethodInfo closed =
                nativeInstantiate.MakeGenericMethod(
                    _foundationType
                );

            // Create the primary bridge on the exact floor selected by P1.
            Component primary =
                CreateNativeFoundationBridge(
                    closed,
                    raftFloor,
                    "~RaftBridgeNativeFoundation_81"
                );

            if (primary == null)
            {
                Write(
                    "DEPLOY_FAIL",
                    "Primary hidden foundation creation failed."
                );
                return;
            }

            _proxyFoundation = primary;
            _proxyRoot = primary.gameObject;
            _proxyFoundations.Add(primary);

            MethodInfo checkStructure =
                FindInstanceMethod(
                    _foundationType,
                    "CheckStructure"
                );

            if (checkStructure == null)
            {
                Write(
                    "DEPLOY_FAIL",
                    "Construction_FOUNDATION.CheckStructure method not found."
                );

                UnityEngine.Object.Destroy(
                    primary.gameObject
                );
                _proxyFoundations.Clear();
                return;
            }

            Write(
                "CHECK_STRUCTURE_BEFORE",
                "foundation=" +
                DescribeConstructing(primary) +
                " | state=" +
                DumpImportantState(primary)
            );

            checkStructure.Invoke(
                primary,
                new object[0]
            );

            _proxyStructure =
                GetMember(
                    primary,
                    "Structure",
                    "_structure",
                    "structure"
                ) as Component;

            Write(
                "CHECK_STRUCTURE_AFTER",
                "foundation=" +
                DescribeConstructing(primary) +
                " | structure=" +
                DescribeConstructing(_proxyStructure) +
                " | state=" +
                DumpImportantState(primary)
            );

            if (_proxyStructure == null)
            {
                Write(
                    "DEPLOY_FAIL",
                    "Primary CheckStructure completed but Structure is still null."
                );

                UnityEngine.Object.Destroy(
                    primary.gameObject
                );
                _proxyFoundations.Clear();
                return;
            }

            ApplyDontSaveRecursive(
                _proxyStructure.gameObject
            );

            _structureLocalPosition =
                raftFloor.transform.InverseTransformPoint(
                    _proxyStructure.transform.position
                );

            _structureLocalRotation =
                Quaternion.Inverse(
                    raftFloor.transform.rotation
                ) *
                _proxyStructure.transform.rotation;

            Write(
                "STRUCTURE_BRIDGE_READY",
                "structure=" +
                DescribeConstructing(_proxyStructure) +
                " | localPos=" +
                FormatVector(_structureLocalPosition) +
                " | localRotY=" +
                _structureLocalRotation.eulerAngles.y.ToString("0.0")
            );

            // Prepare primary connector/collider presentation now that
            // CheckStructure has reparented it under the vanilla structure.
            PrepareHiddenFoundation(primary, 0);

            MethodInfo addConstruction =
                FindAddConstructionMethod(
                    _proxyStructure.GetType()
                );

            int secondaryCreated = 0;
            int secondaryAttached = 0;

            for (int i = 0; i < raftFloors.Count; i++)
            {
                Component floor = raftFloors[i];

                if (System.Object.ReferenceEquals(
                        floor,
                        raftFloor))
                {
                    continue;
                }

                Component secondary =
                    CreateNativeFoundationBridge(
                        closed,
                        floor,
                        "~RaftBridgeNativeFoundation_81_" +
                        (i + 1)
                    );

                if (secondary == null)
                {
                    Write(
                        "SECONDARY_FAIL",
                        "index=" + i +
                        " | floor=" +
                        DescribeConstructing(floor) +
                        " | stage=CREATE"
                    );
                    continue;
                }

                secondaryCreated++;

                // Critical multi-floor bridge step:
                // all hidden foundations share ONE vanilla building Structure.
                bool structureSet =
                    SetMember(
                        secondary,
                        _proxyStructure,
                        "_structure",
                        "Structure",
                        "structure"
                    );

                // Match vanilla CheckStructure parenting while keeping world pose.
                secondary.transform.SetParent(
                    _proxyStructure.transform,
                    true
                );

                bool addInvoked = false;

                if (addConstruction != null)
                {
                    try
                    {
                        addConstruction.Invoke(
                            _proxyStructure,
                            new object[] { secondary }
                        );
                        addInvoked = true;
                    }
                    catch (Exception ex)
                    {
                        Write(
                            "SECONDARY_ADD_ERROR",
                            "index=" + i +
                            " | " +
                            ex.GetType().Name +
                            ": " + ex.Message
                        );
                    }
                }

                object actualStructure =
                    GetMember(
                        secondary,
                        "Structure",
                        "_structure",
                        "structure"
                    );

                bool attached =
                    actualStructure != null &&
                    System.Object.ReferenceEquals(
                        actualStructure,
                        _proxyStructure
                    );

                Write(
                    "SECONDARY_ATTACHED",
                    "index=" + i +
                    " | foundation=" +
                        DescribeConstructing(secondary) +
                    " | floor=" +
                        DescribeConstructing(floor) +
                    " | structureSet=" +
                        structureSet +
                    " | addConstructionInvoked=" +
                        addInvoked +
                    " | attached=" +
                        attached +
                    " | worldPos=" +
                        FormatVector(
                            secondary.transform.position)
                );

                if (!attached)
                {
                    UnityEngine.Object.Destroy(
                        secondary.gameObject
                    );
                    continue;
                }

                PrepareHiddenFoundation(
                    secondary,
                    i + 1
                );

                _proxyFoundations.Add(
                    secondary
                );

                secondaryAttached++;
            }

            _deployed = true;
            _recoveredFromSave = false;

            int totalBridgeTargets = 0;

            for (int i = 0;
                 i < _proxyFoundations.Count;
                 i++)
            {
                totalBridgeTargets +=
                    FindBridgeTargetConnectors(
                        _proxyFoundations[i]
                    ).Count;
            }

            Write(
                "DEPLOY_DONE",
                "selectedFloor=" +
                    DescribeConstructing(raftFloor) +
                " | raftFloorCount=" +
                    raftFloors.Count +
                " | hiddenFoundations=" +
                    _proxyFoundations.Count +
                " | secondaryCreated=" +
                    secondaryCreated +
                " | secondaryAttached=" +
                    secondaryAttached +
                " | sharedStructure=" +
                    DescribeConstructing(
                        _proxyStructure) +
                " | totalBridgeTargets=" +
                    totalBridgeTargets
            );

            RefreshCrafters("DEPLOY_DONE");

            Logger.LogWarning(
                "Bridge Prototype v0.1.11: multi-floor native bridge created. " +
                "WALL, HALF_WALL and ROOF target connectors are active. " +
                "One shared Construction_STRUCTURE spans the selected raft floors. " +
                "DO NOT SAVE."
            );
        }

        private Component CreateNativeFoundationBridge(
            MethodInfo closedInstantiate,
            Component raftFloor,
            string rootName)
        {
            object miniGuid =
                Activator.CreateInstance(
                    _miniGuidType
                );

            Write(
                "NATIVE_METHOD",
                ReflectionMethodSignature(
                    closedInstantiate) +
                " | floor=" +
                    DescribeConstructing(
                        raftFloor) +
                " | miniGuid=" +
                    Safe(miniGuid)
            );

            object created =
                closedInstantiate.Invoke(
                    null,
                    new object[]
                    {
                        (UInt32)81,
                        raftFloor.transform.position,
                        raftFloor.transform.rotation,
                        miniGuid,
                        raftFloor.transform
                    }
                );

            Component foundation =
                created as Component;

            if (foundation == null &&
                created is GameObject)
            {
                foundation =
                    ((GameObject)created)
                        .GetComponent(
                            _foundationType
                        );
            }

            if (foundation == null)
            {
                Write(
                    "NATIVE_CREATE_FAIL",
                    "floor=" +
                    DescribeConstructing(raftFloor) +
                    " | returned=" +
                    (created == null
                        ? "<null>"
                        : created.GetType().FullName)
                );
                return null;
            }

            GameObject root =
                foundation.gameObject;

            root.name = rootName;

            ApplyDontSaveRecursive(root);

            root.transform.position =
                raftFloor.transform.position;

            root.transform.rotation =
                raftFloor.transform.rotation;

            root.transform.SetParent(
                raftFloor.transform,
                true
            );

            Behaviour behaviour =
                foundation as Behaviour;

            Write(
                "NATIVE_CREATED_PREP",
                "root=" +
                    root.name +
                " | floor=" +
                    DescribeConstructing(
                        raftFloor) +
                " | behaviourEnabled=" +
                    (behaviour == null
                        ? "<not-behaviour>"
                        : behaviour.enabled.ToString()) +
                " | activeInHierarchy=" +
                    root.activeInHierarchy +
                " | owner=" +
                    DescribeConstructing(
                        foundation) +
                " | state=" +
                    DumpImportantState(
                        foundation)
            );

            return foundation;
        }

        private void PrepareHiddenFoundation(
            Component foundation,
            int index)
        {
            if (foundation == null)
                return;

            GameObject root =
                foundation.gameObject;

            Renderer[] renderers =
                root.GetComponentsInChildren<Renderer>(
                    true
                );

            for (int i = 0;
                 i < renderers.Length;
                 i++)
            {
                renderers[i].enabled = false;
            }

            List<Component> bridgeTargets =
                FindBridgeTargetConnectors(
                    foundation
                );

            HashSet<int> keepColliderObjects =
                new HashSet<int>();

            int wallCount = 0;
            int halfWallCount = 0;
            int roofCount = 0;

            for (int i = 0;
                 i < bridgeTargets.Count;
                 i++)
            {
                Component connector =
                    bridgeTargets[i];

                keepColliderObjects.Add(
                    connector.gameObject
                        .GetInstanceID()
                );

                string connectorType =
                    Safe(
                        GetMember(
                            connector,
                            "Type",
                            "_type",
                            "ConnectorType"
                        )
                    );

                if (connectorType == "WALL")
                    wallCount++;
                else if (connectorType == "HALF_WALL")
                    halfWallCount++;
                else if (connectorType == "ROOF")
                    roofCount++;

                // Preserve the native connector type exactly.
                // We only ensure runtime validity and collider visibility.
                SetMember(
                    connector,
                    true,
                    "IsValid",
                    "_isValid",
                    "isValid"
                );

                Collider connectorCollider =
                    connector.GetComponent<Collider>();

                if (connectorCollider != null)
                {
                    connectorCollider.enabled =
                        true;

                    connectorCollider.isTrigger =
                        true;
                }
            }

            Collider[] allColliders =
                root.GetComponentsInChildren<Collider>(
                    true
                );

            int kept = 0;
            int disabled = 0;

            for (int i = 0;
                 i < allColliders.Length;
                 i++)
            {
                Collider col =
                    allColliders[i];

                if (keepColliderObjects.Contains(
                        col.gameObject
                            .GetInstanceID()))
                {
                    col.enabled = true;
                    col.isTrigger = true;
                    kept++;
                }
                else
                {
                    col.enabled = false;
                    disabled++;
                }
            }

            Rigidbody[] bodies =
                root.GetComponentsInChildren<Rigidbody>(
                    true
                );

            for (int i = 0;
                 i < bodies.Length;
                 i++)
            {
                bodies[i].isKinematic = true;
                bodies[i].useGravity = false;
                bodies[i].velocity = Vector3.zero;
                bodies[i].angularVelocity =
                    Vector3.zero;
            }

            Write(
                "HIDDEN_FOUNDATION_READY",
                "index=" + index +
                " | foundation=" +
                    DescribeConstructing(
                        foundation) +
                " | structure=" +
                    DescribeConstructing(
                        GetMember(
                            foundation,
                            "Structure",
                            "_structure",
                            "structure")) +
                " | wallTargets=" +
                    wallCount +
                " | halfWallTargets=" +
                    halfWallCount +
                " | roofTargets=" +
                    roofCount +
                " | collidersKept=" +
                    kept +
                " | collidersDisabled=" +
                    disabled +
                " | pos=" +
                    FormatVector(
                        foundation.transform.position)
            );

            for (int i = 0;
                 i < bridgeTargets.Count;
                 i++)
            {
                Write(
                    "PROXY_CONNECTOR",
                    "foundationIndex=" +
                        index +
                    " | " +
                    DescribeConnector(
                        bridgeTargets[i])
                );
            }
        }

        private List<Component> FindBridgeTargetConnectors(
            Component construction)
        {
            List<Component> result =
                new List<Component>();

            if (construction == null ||
                _connectorType == null)
            {
                return result;
            }

            Component[] connectors =
                construction.GetComponentsInChildren(
                    _connectorType,
                    true
                );

            for (int i = 0;
                 i < connectors.Length;
                 i++)
            {
                Component connector =
                    connectors[i];

                if (connector == null)
                    continue;

                string type =
                    Safe(
                        GetMember(
                            connector,
                            "Type",
                            "_type",
                            "ConnectorType"
                        )
                    );

                string name =
                    connector.gameObject.name ?? "";

                bool allowed =
                    type == "WALL" ||
                    type == "HALF_WALL" ||
                    type == "ROOF";

                if (!allowed)
                    continue;

                // Keep only the native target connector families confirmed
                // by the v0.1.1 runtime inventory.
                if (type == "WALL" &&
                    !name.StartsWith(
                        "Connector_Wall_",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (type == "HALF_WALL" &&
                    !name.StartsWith(
                        "Connector_Wall_Half_",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (type == "ROOF" &&
                    !String.Equals(
                        name,
                        "Connector_Roof",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(connector);
            }

            return result;
        }

        private List<Component> FindFloorsForSelectedRaft(
            Component selectedFloor,
            Component raftStructure)
        {
            List<Component> result =
                new List<Component>();

            UnityEngine.Object[] raftObjects =
                UnityEngine.Object
                    .FindObjectsOfType(
                        _raftType
                    );

            for (int i = 0;
                 i < raftObjects.Length;
                 i++)
            {
                Component raft =
                    raftObjects[i] as Component;

                if (raft == null)
                    continue;

                if (!IsRaftFloor(
                        GetPrefabId(raft),
                        GetUnityName(raft)))
                {
                    continue;
                }

                object structure =
                    GetMember(
                        raft,
                        "Structure",
                        "_structure",
                        "structure"
                    );

                if (structure == null ||
                    !System.Object.ReferenceEquals(
                        structure,
                        raftStructure
                    ))
                {
                    continue;
                }

                result.Add(raft);
            }

            result.Sort(
                delegate(
                    Component a,
                    Component b)
                {
                    float da =
                        Vector3.SqrMagnitude(
                            a.transform.position -
                            selectedFloor.transform.position
                        );

                    float db =
                        Vector3.SqrMagnitude(
                            b.transform.position -
                            selectedFloor.transform.position
                        );

                    return da.CompareTo(db);
                }
            );

            return result;
        }

        private MethodInfo FindAddConstructionMethod(
            Type structureType)
        {
            Type current =
                structureType;

            while (current != null)
            {
                MethodInfo[] methods =
                    current.GetMethods(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly
                    );

                for (int i = 0;
                     i < methods.Length;
                     i++)
                {
                    MethodInfo method =
                        methods[i];

                    if (method.Name !=
                        "AddConstruction")
                    {
                        continue;
                    }

                    ParameterInfo[] parameters =
                        method.GetParameters();

                    if (parameters.Length != 1)
                        continue;

                    if (parameters[0]
                        .ParameterType
                        .IsAssignableFrom(
                            _foundationType))
                    {
                        Write(
                            "ADD_CONSTRUCTION_METHOD",
                            ReflectionMethodSignature(
                                method)
                        );

                        return method;
                    }
                }

                current =
                    current.BaseType;
            }

            Write(
                "ADD_CONSTRUCTION_METHOD",
                "<not found>; prototype will rely on foundation._structure + shared parenting"
            );

            return null;
        }

        private Component FindTargetRaftFloor()
        {
            UnityEngine.Object[] raftObjects =
                UnityEngine.Object.FindObjectsOfType(_raftType);

            if (raftObjects == null || raftObjects.Length == 0)
                return null;

            Camera p1 = FindP1GameplayCamera();

            if (p1 != null)
            {
                Write(
                    "P1_CAMERA",
                    "path=" + GetHierarchyPath(p1.transform) +
                    " | pos=" + FormatVector(p1.transform.position) +
                    " | rect=" +
                        p1.rect.x.ToString("0.###") + "," +
                        p1.rect.y.ToString("0.###") + "," +
                        p1.rect.width.ToString("0.###") + "," +
                        p1.rect.height.ToString("0.###")
                );

                // First choice: the raft floor directly under P1's crosshair.
                Ray ray = p1.ViewportPointToRay(
                    new Vector3(0.5f, 0.5f, 0f)
                );

                RaycastHit[] hits = Physics.RaycastAll(
                    ray,
                    8.0f,
                    ~0,
                    QueryTriggerInteraction.Collide
                );

                Array.Sort(
                    hits,
                    delegate(RaycastHit a, RaycastHit b)
                    {
                        return a.distance.CompareTo(b.distance);
                    }
                );

                for (int i = 0; i < hits.Length; i++)
                {
                    Transform t = hits[i].transform;

                    while (t != null)
                    {
                        Component raft =
                            t.GetComponent(_raftType);

                        if (raft != null &&
                            IsRaftFloor(
                                GetPrefabId(raft),
                                GetUnityName(raft)))
                        {
                            Write(
                                "RAFT_SELECTED",
                                "reason=P1_CROSSHAIR" +
                                " | hitDistance=" +
                                    hits[i].distance.ToString("0.000") +
                                " | cameraPos=" +
                                    FormatVector(p1.transform.position) +
                                " | raft=" +
                                    DescribeConstructing(raft) +
                                " | pos=" +
                                    FormatVector(raft.transform.position)
                            );

                            return raft;
                        }

                        t = t.parent;
                    }
                }

                // Fallback: nearest raft floor to the actual P1 gameplay camera.
                Component nearest = null;
                float nearestDistance = Single.MaxValue;

                for (int i = 0; i < raftObjects.Length; i++)
                {
                    Component raft =
                        raftObjects[i] as Component;

                    if (raft == null ||
                        !IsRaftFloor(
                            GetPrefabId(raft),
                            GetUnityName(raft)))
                        continue;

                    float distance =
                        Vector3.SqrMagnitude(
                            raft.transform.position -
                            p1.transform.position
                        );

                    if (nearest == null ||
                        distance < nearestDistance)
                    {
                        nearest = raft;
                        nearestDistance = distance;
                    }
                }

                Write(
                    "RAFT_SELECTED",
                    "reason=P1_NEAREST_FALLBACK" +
                    " | cameraPos=" +
                        FormatVector(p1.transform.position) +
                    " | raft=" +
                        DescribeConstructing(nearest) +
                    " | pos=" +
                        (nearest == null
                            ? "<null>"
                            : FormatVector(nearest.transform.position))
                );

                return nearest;
            }

            // Last-resort fallback. We log it explicitly because this path
            // should not be used in the normal local split-screen test.
            Component first = null;

            for (int i = 0; i < raftObjects.Length; i++)
            {
                Component raft =
                    raftObjects[i] as Component;

                if (raft != null &&
                    IsRaftFloor(
                        GetPrefabId(raft),
                        GetUnityName(raft)))
                {
                    first = raft;
                    break;
                }
            }

            Write(
                "RAFT_SELECTED",
                "reason=NO_GAMEPLAY_CAMERA_FALLBACK" +
                " | raft=" + DescribeConstructing(first) +
                " | pos=" +
                    (first == null
                        ? "<null>"
                        : FormatVector(first.transform.position))
            );

            return first;
        }

        private Camera FindP1GameplayCamera()
        {
            Camera[] cameras = Camera.allCameras;

            List<Camera> gameplay =
                new List<Camera>();

            for (int i = 0; i < cameras.Length; i++)
            {
                Camera c = cameras[i];

                if (c == null ||
                    !c.enabled ||
                    !c.gameObject.activeInHierarchy)
                    continue;

                string path =
                    GetHierarchyPath(c.transform);

                if (path.IndexOf(
                        "PlayerCamera",
                        StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (path.IndexOf(
                        "PlayerUI",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf(
                        "GameUI",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                gameplay.Add(c);
            }

            gameplay.Sort(
                delegate(Camera a, Camera b)
                {
                    int x =
                        a.rect.x.CompareTo(b.rect.x);

                    if (x != 0)
                        return x;

                    return b.depth.CompareTo(a.depth);
                }
            );

            for (int i = 0; i < gameplay.Count; i++)
            {
                Write(
                    "GAMEPLAY_CAMERA_CANDIDATE",
                    "index=" + i +
                    " | path=" +
                        GetHierarchyPath(
                            gameplay[i].transform) +
                    " | pos=" +
                        FormatVector(
                            gameplay[i].transform.position) +
                    " | rect=" +
                        gameplay[i].rect.x.ToString("0.###") + "," +
                        gameplay[i].rect.y.ToString("0.###") + "," +
                        gameplay[i].rect.width.ToString("0.###") + "," +
                        gameplay[i].rect.height.ToString("0.###") +
                    " | depth=" +
                        gameplay[i].depth.ToString("0.###")
                );
            }

            if (gameplay.Count == 0)
                return null;

            return gameplay[0];
        }

        private static string GetHierarchyPath(
            Transform transform)
        {
            if (transform == null)
                return "<null>";

            List<string> parts =
                new List<string>();

            Transform t = transform;

            while (t != null)
            {
                parts.Add(t.name);
                t = t.parent;
            }

            parts.Reverse();

            return String.Join("/", parts.ToArray());
        }

        private void InstallRuntimePatches()
        {
            if (_ghostType == null)
            {
                Write(
                    "GHOST_PATCH_FAIL",
                    "Beam.Crafting.Ghost type not found."
                );
                return;
            }

            PropertyInfo collidingProperty =
                _ghostType.GetProperty(
                    "Colliding",
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                );

            MethodInfo getter =
                collidingProperty == null
                    ? null
                    : collidingProperty.GetGetMethod(true);

            if (getter == null)
            {
                Write(
                    "GHOST_PATCH_FAIL",
                    "Ghost.Colliding getter not found."
                );
                return;
            }

            _harmony = new Harmony(
                "bamex.strandeddeep.raftbuildingbridgeprototype"
            );

            _harmony.Patch(
                getter,
                postfix: new HarmonyMethod(
                    typeof(RaftBuildingBridgePrototypePlugin)
                        .GetMethod(
                            "GhostCollidingPostfix",
                            BindingFlags.Static |
                            BindingFlags.NonPublic
                        )
                )
            );

            Write(
                "GHOST_PATCH_OK",
                ReflectionMethodSignature(getter)
            );

            Type constructingType =
                FindType("Beam.Crafting.Constructing");

            MethodInfo placeMethod =
                constructingType == null
                    ? null
                    : constructingType.GetMethod(
                        "Place",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic
                    );

            if (placeMethod == null)
            {
                Write(
                    "PLACE_PATCH_FAIL",
                    "Constructing.Place not found."
                );
            }
            else
            {
                _harmony.Patch(
                    placeMethod,
                    postfix: new HarmonyMethod(
                        typeof(RaftBuildingBridgePrototypePlugin)
                            .GetMethod(
                                "ConstructingPlacePostfix",
                                BindingFlags.Static |
                                BindingFlags.NonPublic
                            )
                    )
                );

                Write(
                    "PLACE_PATCH_OK",
                    ReflectionMethodSignature(placeMethod)
                );
            }

            InstallRoofProximityPatch();
        }

        private void InstallRoofProximityPatch()
        {
            if (_roofType == null)
            {
                Write(
                    "ROOF_PROXIMITY_PATCH_FAIL",
                    "Beam.Crafting.Constructing_ROOF type not found."
                );
                return;
            }

            MethodInfo method = null;

            MethodInfo[] methods =
                _roofType.GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                );

            for (int i = 0;
                 i < methods.Length;
                 i++)
            {
                MethodInfo candidate =
                    methods[i];

                if (candidate.Name !=
                    "CheckProximity")
                {
                    continue;
                }

                if (candidate.ReturnType !=
                    typeof(bool))
                {
                    continue;
                }

                ParameterInfo[] parameters =
                    candidate.GetParameters();

                if (parameters.Length != 1 ||
                    parameters[0].ParameterType !=
                        typeof(Transform))
                {
                    continue;
                }

                method = candidate;
                break;
            }

            if (method == null)
            {
                Write(
                    "ROOF_PROXIMITY_PATCH_FAIL",
                    "CheckProximity(Transform) bool method not found on Constructing_ROOF/base hierarchy."
                );
                return;
            }

            _harmony.Patch(
                method,
                postfix: new HarmonyMethod(
                    typeof(RaftBuildingBridgePrototypePlugin)
                        .GetMethod(
                            "RoofCheckProximityPostfix",
                            BindingFlags.Static |
                            BindingFlags.NonPublic
                        )
                )
            );

            Write(
                "ROOF_PROXIMITY_PATCH_OK",
                ReflectionMethodSignature(method) +
                " | declaringType=" +
                TypeName(method.DeclaringType)
            );
        }

        private static void RoofCheckProximityPostfix(
            object __instance,
            ref bool __result)
        {
            if (!__result)
                return;

            RaftBuildingBridgePrototypePlugin plugin =
                Instance;

            if (plugin == null ||
                !plugin._deployed ||
                __instance == null)
            {
                return;
            }

            try
            {
                if (!plugin.ShouldSuppressRoofProximity(
                        __instance))
                {
                    return;
                }

                __result = false;

                Write(
                    "ROOF_PROXIMITY_SUPPRESSED",
                    "source=" +
                        DescribeConstructing(
                            __instance) +
                    " | snappedConnector=" +
                        GetUnityName(
                            GetMember(
                                __instance,
                                "SnappedConnector",
                                "_snappedConnector",
                                "snappedConnector"
                            )
                        )
                );
            }
            catch (Exception ex)
            {
                Write(
                    "ROOF_PROXIMITY_PATCH_ERROR",
                    ex.GetType().Name +
                    ": " + ex.Message
                );
            }
        }

        private bool IsRecoveredBridgeFoundationOwner(
            object targetOwner)
        {
            if (targetOwner == null ||
                _proxyStructure == null)
            {
                return false;
            }

            Component targetComponent =
                targetOwner as Component;

            if (targetComponent == null)
                return false;

            if (_foundationType != null &&
                !_foundationType.IsInstanceOfType(
                    targetComponent))
            {
                return false;
            }

            if (GetPrefabId(targetComponent) != 81)
                return false;

            object targetStructure =
                GetMember(
                    targetOwner,
                    "Structure",
                    "_structure",
                    "structure"
                );

            if (targetStructure == null ||
                !System.Object.ReferenceEquals(
                    targetStructure,
                    _proxyStructure
                ))
            {
                return false;
            }

            for (int i = 0;
                 i < _proxyFoundations.Count;
                 i++)
            {
                if (System.Object.ReferenceEquals(
                        _proxyFoundations[i],
                        targetComponent))
                {
                    return true;
                }
            }

            return false;
        }

        private bool ShouldSuppressRoofProximity(
            object constructing)
        {
            if (constructing == null ||
                _roofType == null ||
                !_roofType.IsInstanceOfType(
                    constructing))
            {
                return false;
            }

            bool isSnapped =
                ToBool(
                    GetMember(
                        constructing,
                        "IsSnapped",
                        "_snapped",
                        "snapped"
                    )
                );

            if (!isSnapped)
                return false;

            object snappedConnector =
                GetMember(
                    constructing,
                    "SnappedConnector",
                    "_snappedConnector",
                    "snappedConnector"
                );

            if (snappedConnector == null)
                return false;

            string connectorType =
                Safe(
                    GetMember(
                        snappedConnector,
                        "Type",
                        "_type",
                        "ConnectorType"
                    )
                );

            if (connectorType != "ROOF")
                return false;

            object targetOwner =
                GetMember(
                    snappedConnector,
                    "Constructing",
                    "_constructing",
                    "constructing"
                );

            if (targetOwner == null)
                return false;

            if (!IsRecoveredBridgeFoundationOwner(
                    targetOwner))
            {
                return false;
            }

            return true;
        }

        private static void ConstructingPlacePostfix(
            object __instance)
        {
            RaftBuildingBridgePrototypePlugin plugin =
                Instance;

            if (plugin == null ||
                !plugin._deployed ||
                __instance == null)
            {
                return;
            }

            try
            {
                object structure =
                    GetMember(
                        __instance,
                        "Structure",
                        "_structure",
                        "structure"
                    );

                if (structure == null ||
                    plugin._proxyStructure == null ||
                    !System.Object.ReferenceEquals(
                        structure,
                        plugin._proxyStructure
                    ))
                {
                    return;
                }

                plugin.MaintainBuiltStructureRaftIsolation(
                    "PLACE:" +
                    DescribeConstructing(__instance)
                );
            }
            catch (Exception ex)
            {
                Write(
                    "POST_PLACE_ISOLATION_ERROR",
                    ex.GetType().Name +
                    ": " + ex.Message
                );
            }
        }

        private static void GhostCollidingPostfix(
            object __instance,
            ref bool __result)
        {
            RaftBuildingBridgePrototypePlugin plugin =
                Instance;

            if (plugin == null ||
                !plugin._deployed)
                return;

            try
            {
                plugin.ObserveRoofPlacement(
                    __instance,
                    __result
                );

                if (!__result)
                    return;

                if (plugin.ShouldSuppressGhostCollision(
                        __instance))
                {
                    __result = false;

                    if (Time.unscaledTime -
                        plugin._lastGhostOverrideLogTime >
                        0.5f)
                    {
                        plugin._lastGhostOverrideLogTime =
                            Time.unscaledTime;

                        Write(
                            "GHOST_COLLIDING_SUPPRESSED",
                            "bridge-snapped construction via recovered foundation membership | ghost=" +
                            GetUnityName(__instance)
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                Write(
                    "GHOST_PATCH_ERROR",
                    ex.GetType().Name +
                    ": " + ex.Message
                );
            }
        }

        private bool ShouldSuppressGhostCollision(
            object ghost)
        {
            if (ghost == null)
                return false;

            if (_crafterType == null)
                return false;

            if (_crafters.Count == 0 ||
                Time.unscaledTime -
                    _lastCrafterRefreshTime >
                    2.0f)
            {
                RefreshCrafters("GHOST_GETTER_REFRESH");
            }

            for (int i = 0;
                 i < _crafters.Count;
                 i++)
            {
                Component crafter =
                    _crafters[i];

                if (crafter == null)
                    continue;

                object crafterGhost =
                    GetMember(
                        crafter,
                        "_ghost",
                        "Ghost",
                        "ghost"
                    );

                if (!SameGhost(
                        crafterGhost,
                        ghost))
                {
                    continue;
                }

                object placingCraftable =
                    GetMember(
                        crafter,
                        "_placingCraftable",
                        "PlacingCraftable",
                        "placingCraftable"
                    );

                if (placingCraftable == null)
                    return false;

                bool isSnapped =
                    ToBool(
                        GetMember(
                            placingCraftable,
                            "IsSnapped",
                            "_snapped",
                            "snapped"
                        )
                    );

                if (!isSnapped)
                    return false;

                object snappedConnector =
                    GetMember(
                        placingCraftable,
                        "SnappedConnector",
                        "_snappedConnector",
                        "snappedConnector"
                    );

                if (snappedConnector == null)
                    return false;

                string snappedConnectorType =
                    Safe(
                        GetMember(
                            snappedConnector,
                            "Type",
                            "_type",
                            "ConnectorType"
                        )
                    );

                if (snappedConnectorType != "WALL" &&
                    snappedConnectorType != "HALF_WALL" &&
                    snappedConnectorType != "ROOF")
                {
                    return false;
                }

                object targetOwner =
                    GetMember(
                        snappedConnector,
                        "Constructing",
                        "_constructing",
                        "constructing"
                    );

                if (targetOwner == null)
                    return false;

                if (!IsRecoveredBridgeFoundationOwner(
                        targetOwner))
                {
                    return false;
                }

                object targetStructure =
                    GetMember(
                        targetOwner,
                        "Structure",
                        "_structure",
                        "structure"
                    );

                if (targetStructure == null)
                    return false;

                if (Time.unscaledTime -
                    _lastGhostConditionLogTime >
                    0.5f)
                {
                    _lastGhostConditionLogTime =
                        Time.unscaledTime;

                    Write(
                        "GHOST_OVERRIDE_CONDITION",
                        "crafter=" +
                            GetUnityName(crafter) +
                        " | source=" +
                            DescribeConstructing(
                                placingCraftable) +
                        " | snappedConnector=" +
                            GetUnityName(
                                snappedConnector) +
                        " | snappedConnectorType=" +
                            snappedConnectorType +
                        " | target=" +
                            DescribeConstructing(
                                targetOwner) +
                        " | targetStructure=" +
                            DescribeConstructing(
                                targetStructure)
                    );
                }

                return true;
            }

            return false;
        }

        private void ObserveRoofPlacement(
            object ghost,
            bool originalGhostColliding)
        {
            if (ghost == null ||
                _crafterType == null)
            {
                return;
            }

            if (_crafters.Count == 0 ||
                Time.unscaledTime -
                    _lastCrafterRefreshTime >
                    2.0f)
            {
                RefreshCrafters(
                    "ROOF_DIAG_REFRESH"
                );
            }

            for (int i = 0;
                 i < _crafters.Count;
                 i++)
            {
                Component crafter =
                    _crafters[i];

                if (crafter == null)
                    continue;

                object crafterGhost =
                    GetMember(
                        crafter,
                        "_ghost",
                        "Ghost",
                        "ghost"
                    );

                if (!SameGhost(
                        crafterGhost,
                        ghost))
                {
                    continue;
                }

                object placingCraftable =
                    GetMember(
                        crafter,
                        "_placingCraftable",
                        "PlacingCraftable",
                        "placingCraftable"
                    );

                if (placingCraftable == null)
                    return;

                bool isSnapped =
                    ToBool(
                        GetMember(
                            placingCraftable,
                            "IsSnapped",
                            "_snapped",
                            "snapped"
                        )
                    );

                if (!isSnapped)
                    return;

                object snappedConnector =
                    GetMember(
                        placingCraftable,
                        "SnappedConnector",
                        "_snappedConnector",
                        "snappedConnector"
                    );

                if (snappedConnector == null)
                    return;

                string snappedConnectorType =
                    Safe(
                        GetMember(
                            snappedConnector,
                            "Type",
                            "_type",
                            "ConnectorType"
                        )
                    );

                if (snappedConnectorType != "ROOF")
                    return;

                object targetOwner =
                    GetMember(
                        snappedConnector,
                        "Constructing",
                        "_constructing",
                        "constructing"
                    );

                if (!IsRecoveredBridgeFoundationOwner(
                        targetOwner))
                {
                    return;
                }

                if (Time.unscaledTime -
                    _lastRoofDiagnosticLogTime >
                    0.5f)
                {
                    _lastRoofDiagnosticLogTime =
                        Time.unscaledTime;

                    Write(
                        "ROOF_DIAG_STATE",
                        "source=" +
                            DescribeConstructing(
                                placingCraftable) +
                        " | isSnapped=" +
                            isSnapped +
                        " | snappedConnector=" +
                            GetUnityName(
                                snappedConnector) +
                        " | originalGhostColliding=" +
                            originalGhostColliding +
                        " | target=" +
                            DescribeConstructing(
                                targetOwner) +
                        " | sourceState=" +
                            DumpImportantState(
                                placingCraftable)
                    );
                }

                Component sourceComponent =
                    placingCraftable as Component;

                if (sourceComponent != null)
                {
                    int sourceId =
                        sourceComponent
                            .GetInstanceID();

                    if (!_roofModifierDumpedSources
                        .Contains(sourceId))
                    {
                        _roofModifierDumpedSources
                            .Add(sourceId);

                        DumpPlacementModifiers(
                            placingCraftable,
                            sourceId
                        );
                    }
                }

                return;
            }
        }

        private void DumpPlacementModifiers(
            object placingCraftable,
            int sourceId)
        {
            object modifiers =
                GetMember(
                    placingCraftable,
                    "_placementModifiers",
                    "PlacementModifiers",
                    "placementModifiers"
                );

            System.Collections.IEnumerable enumerable =
                modifiers as
                    System.Collections.IEnumerable;

            if (enumerable == null)
            {
                Write(
                    "ROOF_MODIFIERS",
                    "sourceId=" +
                        sourceId +
                    " | modifiers=<not enumerable> | raw=" +
                        Safe(modifiers)
                );
                return;
            }

            int index = 0;

            foreach (object modifier in enumerable)
            {
                if (modifier == null)
                {
                    Write(
                        "ROOF_MODIFIER",
                        "sourceId=" +
                            sourceId +
                        " | index=" +
                            index +
                        " | <null>"
                    );

                    index++;
                    continue;
                }

                Write(
                    "ROOF_MODIFIER",
                    "sourceId=" +
                        sourceId +
                    " | index=" +
                        index +
                    " | type=" +
                        modifier.GetType().FullName +
                    " | members=" +
                        DumpSimpleMembers(
                            modifier)
                );

                index++;
            }

            Write(
                "ROOF_MODIFIERS",
                "sourceId=" +
                    sourceId +
                " | count=" +
                    index
            );
        }

        private static string DumpSimpleMembers(
            object obj)
        {
            if (obj == null)
                return "<null>";

            List<string> parts =
                new List<string>();

            Type type =
                obj.GetType();

            BindingFlags flags =
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic;

            FieldInfo[] fields =
                type.GetFields(flags);

            for (int i = 0;
                 i < fields.Length &&
                 parts.Count < 20;
                 i++)
            {
                FieldInfo field =
                    fields[i];

                try
                {
                    object value =
                        field.GetValue(obj);

                    if (IsSimpleDiagnosticValue(
                            value))
                    {
                        parts.Add(
                            field.Name +
                            "=" +
                            Safe(value)
                        );
                    }
                }
                catch
                {
                }
            }

            PropertyInfo[] properties =
                type.GetProperties(flags);

            for (int i = 0;
                 i < properties.Length &&
                 parts.Count < 30;
                 i++)
            {
                PropertyInfo property =
                    properties[i];

                if (property.GetIndexParameters()
                    .Length != 0)
                {
                    continue;
                }

                try
                {
                    object value =
                        property.GetValue(
                            obj,
                            null
                        );

                    if (IsSimpleDiagnosticValue(
                            value))
                    {
                        parts.Add(
                            property.Name +
                            "=" +
                            Safe(value)
                        );
                    }
                }
                catch
                {
                }
            }

            if (parts.Count == 0)
                return "<no simple members>";

            return String.Join(
                ";",
                parts.ToArray()
            );
        }

        private static bool IsSimpleDiagnosticValue(
            object value)
        {
            if (value == null)
                return true;

            Type type =
                value.GetType();

            if (type.IsPrimitive ||
                type.IsEnum ||
                value is string ||
                value is decimal)
            {
                return true;
            }

            UnityEngine.Object unityObject =
                value as UnityEngine.Object;

            if (unityObject != null)
                return true;

            return false;
        }

        private void RefreshCrafters(
            string reason)
        {
            _crafters.Clear();
            _lastCrafterRefreshTime =
                Time.unscaledTime;

            if (_crafterType == null)
            {
                Write(
                    "CRAFTER_REFRESH",
                    "reason=" + reason +
                    " | crafterType=<null>"
                );
                return;
            }

            UnityEngine.Object[] found =
                UnityEngine.Object.FindObjectsOfType(
                    _crafterType
                );

            if (found != null)
            {
                for (int i = 0;
                     i < found.Length;
                     i++)
                {
                    Component component =
                        found[i] as Component;

                    if (component != null)
                        _crafters.Add(component);
                }
            }

            Write(
                "CRAFTER_REFRESH",
                "reason=" + reason +
                " | count=" + _crafters.Count
            );
        }

        private bool SameGhost(
            object a,
            object b)
        {
            if (System.Object.ReferenceEquals(a, b))
                return true;

            Component aComponent =
                NormalizeGhostComponent(a);

            Component bComponent =
                NormalizeGhostComponent(b);

            return
                aComponent != null &&
                bComponent != null &&
                System.Object.ReferenceEquals(
                    aComponent,
                    bComponent
                );
        }

        private Component NormalizeGhostComponent(
            object value)
        {
            if (value == null ||
                _ghostType == null)
            {
                return null;
            }

            Component component =
                value as Component;

            if (component != null)
            {
                if (_ghostType.IsAssignableFrom(
                        component.GetType()))
                {
                    return component;
                }

                return component.GetComponent(
                    _ghostType
                );
            }

            GameObject gameObject =
                value as GameObject;

            if (gameObject != null)
            {
                return gameObject.GetComponent(
                    _ghostType
                );
            }

            return null;
        }

        private static bool ToBool(
            object value)
        {
            if (value == null)
                return false;

            if (value is bool)
                return (bool)value;

            bool parsed;

            return Boolean.TryParse(
                value.ToString(),
                out parsed
            ) && parsed;
        }

        private void MaintainBuiltStructureRaftIsolation(
            string reason)
        {
            if (_proxyStructure == null)
                return;

            Component raftStructure =
                _targetRaftStructure;

            if (raftStructure == null &&
                _targetRaftFloor != null)
            {
                raftStructure =
                    GetMember(
                        _targetRaftFloor,
                        "Structure",
                        "_structure",
                        "structure"
                    ) as Component;

                _targetRaftStructure =
                    raftStructure;
            }

            if (raftStructure == null)
                return;

            // Remove only dead bookkeeping entries. Never restore a live pair
            // during a normal refresh: construction-level collider changes can
            // happen after Place(), and even a brief re-enable is unnecessary.
            for (int i =
                    _physicalIsolationPairs.Count - 1;
                 i >= 0;
                 i--)
            {
                PhysicsIsolationPair existing =
                    _physicalIsolationPairs[i];

                if (existing == null ||
                    existing.Building == null ||
                    existing.Raft == null)
                {
                    if (existing != null &&
                        !String.IsNullOrEmpty(
                            existing.Key))
                    {
                        _physicalIsolationPairKeys
                            .Remove(
                                existing.Key
                            );
                    }

                    _physicalIsolationPairs
                        .RemoveAt(i);
                }
            }

            Collider[] buildingCandidates =
                _proxyStructure
                    .GetComponentsInChildren<Collider>(
                        true
                    );

            Collider[] raftCandidates =
                raftStructure
                    .GetComponentsInChildren<Collider>(
                        true
                    );

            List<Collider> buildingPhysical =
                new List<Collider>();

            for (int i = 0;
                 i < buildingCandidates.Length;
                 i++)
            {
                Collider c =
                    buildingCandidates[i];

                if (c == null ||
                    !c.enabled ||
                    c.isTrigger)
                {
                    continue;
                }

                buildingPhysical.Add(c);
            }

            List<Collider> raftPhysical =
                new List<Collider>();

            for (int i = 0;
                 i < raftCandidates.Length;
                 i++)
            {
                Collider c =
                    raftCandidates[i];

                if (c == null ||
                    !c.enabled ||
                    c.isTrigger)
                {
                    continue;
                }

                // v0.1.14:
                // The owning Construction_STRUCTURE_RAFT hierarchy is the
                // authority. Do NOT restrict physical isolation to deck/base
                // layer 31 only. Vehicle components (motor/rudder/anchor/etc.)
                // can own solid colliders on other layers and still share the
                // raft Rigidbody / physical hierarchy.
                //
                // This mirrors the field-proven Raft Furniture policy:
                // target building structure <-> ALL solid colliders in the
                // owning raft hierarchy.
                raftPhysical.Add(c);
            }

            if (!_raftHierarchyInventoryLogged)
            {
                _raftHierarchyInventoryLogged = true;

                Write(
                    "RAFT_HIERARCHY_PHYSICS",
                    "raftStructure=" +
                        DescribeConstructing(
                            raftStructure) +
                    " | candidateColliders=" +
                        raftCandidates.Length +
                    " | solidEnabled=" +
                        raftPhysical.Count
                );

                for (int i = 0;
                     i < raftPhysical.Count;
                     i++)
                {
                    Collider c =
                        raftPhysical[i];

                    Rigidbody body =
                        c.attachedRigidbody;

                    Write(
                        "RAFT_HIERARCHY_COLLIDER",
                        "index=" + i +
                        " | name=" +
                            c.gameObject.name +
                        " | type=" +
                            c.GetType().Name +
                        " | layer=" +
                            c.gameObject.layer +
                        " | path=" +
                            GetTransformPath(
                                c.transform) +
                        " | attachedBody=" +
                            (body == null
                                ? "<null>"
                                : body.gameObject.name) +
                        " | bodyKinematic=" +
                            (body == null
                                ? "<null>"
                                : body.isKinematic.ToString())
                    );
                }
            }

            int addedPairs = 0;

            for (int i = 0;
                 i < buildingPhysical.Count;
                 i++)
            {
                Collider building =
                    buildingPhysical[i];

                for (int j = 0;
                     j < raftPhysical.Count;
                     j++)
                {
                    Collider raft =
                        raftPhysical[j];

                    if (building == null ||
                        raft == null ||
                        building == raft)
                    {
                        continue;
                    }

                    string key =
                        building.GetInstanceID()
                            .ToString() +
                        ":" +
                        raft.GetInstanceID()
                            .ToString();

                    if (_physicalIsolationPairKeys
                        .Contains(key))
                    {
                        continue;
                    }

                    Physics.IgnoreCollision(
                        building,
                        raft,
                        true
                    );

                    _physicalIsolationPairKeys
                        .Add(key);

                    _physicalIsolationPairs.Add(
                        new PhysicsIsolationPair
                        {
                            Building =
                                building,
                            Raft =
                                raft,
                            Key =
                                key
                        }
                    );

                    addedPairs++;
                }
            }

            if (addedPairs > 0 ||
                reason.StartsWith(
                    "PLACE:",
                    StringComparison.Ordinal))
            {
                Write(
                    "ISOLATION_RECONCILE",
                    "reason=" +
                        reason +
                    " | buildingPhysical=" +
                        buildingPhysical.Count +
                    " | raftPhysical=" +
                        raftPhysical.Count +
                    " | addedPairs=" +
                        addedPairs +
                    " | activePairs=" +
                        _physicalIsolationPairs.Count
                );
            }
        }

        private static Transform FindAncestorByName(
            Transform start,
            string targetName)
        {
            Transform current =
                start;

            while (current != null)
            {
                if (String.Equals(
                        current.name,
                        targetName,
                        StringComparison.Ordinal))
                {
                    return current;
                }

                current =
                    current.parent;
            }

            return null;
        }

        private static string GetTransformPath(
            Transform transform)
        {
            if (transform == null)
                return "<null>";

            List<string> parts =
                new List<string>();

            Transform current =
                transform;

            while (current != null &&
                   parts.Count < 24)
            {
                parts.Add(
                    current.name
                );

                current =
                    current.parent;
            }

            parts.Reverse();

            return String.Join(
                "/",
                parts.ToArray()
            );
        }

        private Component FindRaftOwner(
            Transform start)
        {
            if (_raftType == null ||
                start == null)
            {
                return null;
            }

            Transform current = start;

            while (current != null)
            {
                Component owner =
                    current.GetComponent(
                        _raftType
                    );

                if (owner != null)
                    return owner;

                if (_targetRaftStructure != null &&
                    current ==
                    _targetRaftStructure.transform)
                {
                    break;
                }

                current = current.parent;
            }

            return null;
        }

        private void RestoreBuiltStructureRaftIsolation(
            string reason)
        {
            if (_physicalIsolationPairs.Count == 0)
                return;

            int restored = 0;

            for (int i = 0;
                 i < _physicalIsolationPairs.Count;
                 i++)
            {
                PhysicsIsolationPair pair =
                    _physicalIsolationPairs[i];

                if (pair == null ||
                    pair.Building == null ||
                    pair.Raft == null)
                {
                    continue;
                }

                try
                {
                    Physics.IgnoreCollision(
                        pair.Building,
                        pair.Raft,
                        false
                    );

                    restored++;
                }
                catch
                {
                }
            }

            _physicalIsolationPairs.Clear();
            _physicalIsolationPairKeys.Clear();

            Write(
                "POST_PLACE_ISOLATION_RESTORED",
                "reason=" + reason +
                " | restoredPairs=" +
                    restored
            );
        }

        private static MethodInfo FindInstanceMethod(
            Type type,
            string name)
        {
            Type current = type;

            while (current != null)
            {
                MethodInfo method =
                    current.GetMethod(
                        name,
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly,
                        null,
                        Type.EmptyTypes,
                        null
                    );

                if (method != null)
                    return method;

                current = current.BaseType;
            }

            return null;
        }

        private MethodInfo FindNativeInstantiateMethod()
        {
            MethodInfo[] methods =
                _multiplayerMngType.GetMethods(
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                );

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];

                if (method.Name != "Instantiate" ||
                    !method.IsGenericMethodDefinition)
                    continue;

                ParameterInfo[] p = method.GetParameters();

                if (p.Length != 5)
                    continue;

                if (p[0].ParameterType != typeof(UInt32))
                    continue;

                if (p[1].ParameterType != typeof(Vector3))
                    continue;

                if (p[2].ParameterType != typeof(Quaternion))
                    continue;

                if (p[3].ParameterType != _miniGuidType)
                    continue;

                if (p[4].ParameterType != typeof(Transform))
                    continue;

                return method;
            }

            return null;
        }

        private void DumpInstantiateCandidates()
        {
            MethodInfo[] methods =
                _multiplayerMngType.GetMethods(
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic
                );

            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name == "Instantiate")
                {
                    Write(
                        "INSTANTIATE_CANDIDATE",
                        ReflectionMethodSignature(methods[i])
                    );
                }
            }
        }

        private void DestroyProxy(string reason)
        {
            RestoreBuiltStructureRaftIsolation(
                "DESTROY_PROXY_" + reason
            );

            if (_proxyRoot == null &&
                _proxyStructure == null)
                return;

            Write(
                "PROXY_DESTROY",
                "reason=" + reason +
                " | owner=" +
                    DescribeConstructing(_proxyFoundation) +
                " | structure=" +
                    DescribeConstructing(_proxyStructure) +
                " | state=" +
                    DumpImportantState(_proxyFoundation)
            );

            if (_proxyStructure != null)
            {
                UnityEngine.Object.Destroy(
                    _proxyStructure.gameObject
                );
            }
            else if (_proxyRoot != null)
            {
                UnityEngine.Object.Destroy(
                    _proxyRoot
                );
            }

            _proxyRoot = null;
            _proxyFoundation = null;
            _proxyStructure = null;
            _proxyFoundations.Clear();
            _targetRaftFloor = null;
            _targetRaftStructure = null;
            _targetRaftFloor = null;
            _raftHierarchyInventoryLogged = false;
            _recoveredFromSave = false;
            _autoPreparedFoundationIds.Clear();
            _deployed = false;

            if (reason == "EMERGENCY_F10")
            {
                Logger.LogWarning(
                    "Native bridge structure destroyed. " +
                    "Use F10 only before placing a wall."
                );
            }
        }

        private static string DumpImportantState(object obj)
        {
            if (obj == null)
                return "<null>";

            List<string> rows = new List<string>();

            Type type = obj.GetType();

            while (type != null)
            {
                FieldInfo[] fields =
                    type.GetFields(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly
                    );

                for (int i = 0; i < fields.Length; i++)
                {
                    string n = fields[i].Name;

                    if (!ImportantName(n))
                        continue;

                    try
                    {
                        rows.Add(
                            type.Name + "." + n + "=" +
                            SafeCompact(fields[i].GetValue(obj))
                        );
                    }
                    catch
                    {
                    }
                }

                PropertyInfo[] properties =
                    type.GetProperties(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly
                    );

                for (int i = 0; i < properties.Length; i++)
                {
                    PropertyInfo p = properties[i];

                    if (p.GetIndexParameters().Length != 0 ||
                        !p.CanRead ||
                        !ImportantName(p.Name))
                        continue;

                    try
                    {
                        rows.Add(
                            type.Name + "." + p.Name + "=" +
                            SafeCompact(p.GetValue(obj, null))
                        );
                    }
                    catch
                    {
                    }
                }

                type = type.BaseType;
            }

            rows.Sort(StringComparer.Ordinal);

            if (rows.Count == 0)
                return "<no-matching-state>";

            return String.Join(" ; ", rows.ToArray());
        }

        private static bool ImportantName(string name)
        {
            if (String.IsNullOrEmpty(name))
                return false;

            string n = name.ToLowerInvariant();

            return
                n.Contains("place") ||
                n.Contains("structure") ||
                n.Contains("reference") ||
                n.Contains("guid") ||
                n.Contains("save") ||
                n.Contains("initial") ||
                n.Contains("built") ||
                n.Contains("zone") ||
                n.Contains("snapped") ||
                n.Contains("destroy") ||
                n.Contains("valid") ||
                n.Contains("active") ||
                n.Contains("construct");
        }

        private static void ApplyDontSaveRecursive(
            GameObject root)
        {
            if (root == null)
                return;

            Transform[] transforms =
                root.GetComponentsInChildren<Transform>(true);

            for (int i = 0; i < transforms.Length; i++)
            {
                GameObject go = transforms[i].gameObject;

                go.hideFlags =
                    HideFlags.DontSave |
                    HideFlags.HideInHierarchy;

                Component[] components =
                    go.GetComponents<Component>();

                for (int j = 0; j < components.Length; j++)
                {
                    if (components[j] != null)
                        components[j].hideFlags = HideFlags.DontSave;
                }
            }
        }

        private static bool IsRaftFloor(
            int prefabId,
            string name)
        {
            if (prefabId >= 137 && prefabId <= 141)
                return true;

            return !String.IsNullOrEmpty(name) &&
                name.IndexOf(
                    "RAFT_FLOOR",
                    StringComparison.OrdinalIgnoreCase
                ) >= 0;
        }

        private static int GetPrefabId(object obj)
        {
            object value = GetMember(
                obj,
                "PrefabId",
                "PrefabID",
                "_prefabId",
                "_prefabID"
            );

            int result;

            if (value != null &&
                Int32.TryParse(value.ToString(), out result))
                return result;

            return -1;
        }

        private static Type FindType(string fullName)
        {
            Assembly[] assemblies =
                AppDomain.CurrentDomain.GetAssemblies();

            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    Type t = assemblies[i].GetType(fullName, false);

                    if (t != null)
                        return t;
                }
                catch
                {
                }
            }

            return null;
        }

        private static Type FindTypeBySimpleName(string simpleName)
        {
            Assembly[] assemblies =
                AppDomain.CurrentDomain.GetAssemblies();

            for (int i = 0; i < assemblies.Length; i++)
            {
                Type[] types;

                try
                {
                    types = assemblies[i].GetTypes();
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

                for (int j = 0; j < types.Length; j++)
                {
                    Type t = types[j];

                    if (t != null &&
                        t.Name == simpleName)
                        return t;
                }
            }

            return null;
        }

        private static object GetMember(
            object obj,
            params string[] names)
        {
            if (obj == null || names == null)
                return null;

            Type original = obj.GetType();

            for (int i = 0; i < names.Length; i++)
            {
                Type type = original;

                while (type != null)
                {
                    try
                    {
                        PropertyInfo property =
                            type.GetProperty(
                                names[i],
                                BindingFlags.Instance |
                                BindingFlags.Public |
                                BindingFlags.NonPublic |
                                BindingFlags.DeclaredOnly
                            );

                        if (property != null &&
                            property.GetIndexParameters().Length == 0)
                            return property.GetValue(obj, null);
                    }
                    catch
                    {
                    }

                    try
                    {
                        FieldInfo field =
                            type.GetField(
                                names[i],
                                BindingFlags.Instance |
                                BindingFlags.Public |
                                BindingFlags.NonPublic |
                                BindingFlags.DeclaredOnly
                            );

                        if (field != null)
                            return field.GetValue(obj);
                    }
                    catch
                    {
                    }

                    type = type.BaseType;
                }
            }

            return null;
        }

        private static bool SetMember(
            object obj,
            object value,
            params string[] names)
        {
            if (obj == null || names == null)
                return false;

            Type original = obj.GetType();

            for (int i = 0; i < names.Length; i++)
            {
                Type type = original;

                while (type != null)
                {
                    try
                    {
                        PropertyInfo property =
                            type.GetProperty(
                                names[i],
                                BindingFlags.Instance |
                                BindingFlags.Public |
                                BindingFlags.NonPublic |
                                BindingFlags.DeclaredOnly
                            );

                        if (property != null &&
                            property.GetIndexParameters().Length == 0 &&
                            property.GetSetMethod(true) != null &&
                            CanAssign(property.PropertyType, value))
                        {
                            property.SetValue(obj, value, null);
                            return true;
                        }
                    }
                    catch
                    {
                    }

                    try
                    {
                        FieldInfo field =
                            type.GetField(
                                names[i],
                                BindingFlags.Instance |
                                BindingFlags.Public |
                                BindingFlags.NonPublic |
                                BindingFlags.DeclaredOnly
                            );

                        if (field != null &&
                            !field.IsInitOnly &&
                            CanAssign(field.FieldType, value))
                        {
                            field.SetValue(obj, value);
                            return true;
                        }
                    }
                    catch
                    {
                    }

                    type = type.BaseType;
                }
            }

            return false;
        }

        private static bool SetEnumMember(
            object obj,
            string enumName,
            params string[] names)
        {
            if (obj == null || names == null)
                return false;

            Type original = obj.GetType();

            for (int i = 0; i < names.Length; i++)
            {
                Type type = original;

                while (type != null)
                {
                    try
                    {
                        PropertyInfo property =
                            type.GetProperty(
                                names[i],
                                BindingFlags.Instance |
                                BindingFlags.Public |
                                BindingFlags.NonPublic |
                                BindingFlags.DeclaredOnly
                            );

                        if (property != null &&
                            property.PropertyType.IsEnum &&
                            property.GetSetMethod(true) != null)
                        {
                            object enumValue =
                                Enum.Parse(
                                    property.PropertyType,
                                    enumName,
                                    true
                                );

                            property.SetValue(obj, enumValue, null);
                            return true;
                        }
                    }
                    catch
                    {
                    }

                    try
                    {
                        FieldInfo field =
                            type.GetField(
                                names[i],
                                BindingFlags.Instance |
                                BindingFlags.Public |
                                BindingFlags.NonPublic |
                                BindingFlags.DeclaredOnly
                            );

                        if (field != null &&
                            field.FieldType.IsEnum &&
                            !field.IsInitOnly)
                        {
                            object enumValue =
                                Enum.Parse(
                                    field.FieldType,
                                    enumName,
                                    true
                                );

                            field.SetValue(obj, enumValue);
                            return true;
                        }
                    }
                    catch
                    {
                    }

                    type = type.BaseType;
                }
            }

            return false;
        }

        private static bool CanAssign(
            Type targetType,
            object value)
        {
            if (targetType == null)
                return false;

            if (value == null)
                return !targetType.IsValueType;

            return targetType.IsAssignableFrom(
                value.GetType()
            );
        }

        private static string DescribeConstructing(object obj)
        {
            if (obj == null)
                return "<null>";

            object reference = GetMember(
                obj,
                "ReferenceId",
                "ReferenceID",
                "_referenceId",
                "_referenceID"
            );

            return obj.GetType().FullName +
                "{name=" + GetUnityName(obj) +
                ",prefabId=" + GetPrefabId(obj) +
                ",referenceId=" + SafeCompact(reference) +
                "}";
        }

        private static string DescribeConnector(object connector)
        {
            if (connector == null)
                return "<null>";

            object type = GetMember(
                connector,
                "Type",
                "_type",
                "ConnectorType"
            );

            object valid = GetMember(
                connector,
                "IsValid",
                "_isValid",
                "isValid"
            );

            object owner = GetMember(
                connector,
                "Constructing",
                "_constructing",
                "constructing"
            );

            Component component = connector as Component;

            string colliderText = "<none>";
            string position = "<none>";
            string layer = "<none>";

            if (component != null)
            {
                position = FormatVector(component.transform.position);
                layer = component.gameObject.layer.ToString();

                Collider collider =
                    component.GetComponent<Collider>();

                if (collider != null)
                {
                    colliderText =
                        collider.GetType().Name +
                        "(enabled=" + collider.enabled +
                        ",trigger=" + collider.isTrigger + ")";
                }
            }

            return connector.GetType().FullName +
                "{name=" + GetUnityName(connector) +
                ",type=" + SafeCompact(type) +
                ",isValid=" + SafeCompact(valid) +
                ",owner=" + DescribeConstructing(owner) +
                ",layer=" + layer +
                ",collider=" + colliderText +
                ",pos=" + position +
                "}";
        }

        private static string GetUnityName(object obj)
        {
            if (obj == null)
                return "<null>";

            UnityEngine.Object unityObject =
                obj as UnityEngine.Object;

            if (unityObject != null &&
                !String.IsNullOrEmpty(unityObject.name))
                return unityObject.name;

            Component component = obj as Component;

            if (component != null &&
                component.gameObject != null)
                return component.gameObject.name;

            return obj.GetType().Name;
        }

        private static string SafeCompact(object value)
        {
            if (value == null)
                return "<null>";

            try
            {
                string s = value.ToString();

                if (s == null)
                    return "<null-string>";

                s = s.Replace("\r", " ").Replace("\n", " ");

                if (s.Length > 120)
                    s = s.Substring(0, 120) + "...";

                return s;
            }
            catch
            {
                return "<ToString failed>";
            }
        }

        private static string Safe(object value)
        {
            return SafeCompact(value);
        }

        private static string TypeName(Type type)
        {
            return type == null ? "<null>" : type.FullName;
        }

        private static string FormatVector(Vector3 value)
        {
            return "(" +
                value.x.ToString("0.000") + "," +
                value.y.ToString("0.000") + "," +
                value.z.ToString("0.000") + ")";
        }

        private static string ReflectionMethodSignature(MethodInfo method)
        {
            if (method == null)
                return "<null>";

            ParameterInfo[] p = method.GetParameters();

            StringBuilder sb = new StringBuilder();
            sb.Append(method.DeclaringType.FullName);
            sb.Append("::");
            sb.Append(method.Name);

            if (method.IsGenericMethod)
            {
                sb.Append("<");
                Type[] ga = method.GetGenericArguments();

                for (int i = 0; i < ga.Length; i++)
                {
                    if (i > 0)
                        sb.Append(",");

                    sb.Append(ga[i].FullName ?? ga[i].Name);
                }

                sb.Append(">");
            }

            sb.Append("(");

            for (int i = 0; i < p.Length; i++)
            {
                if (i > 0)
                    sb.Append(",");

                sb.Append(p[i].ParameterType.FullName);
            }

            sb.Append(")");

            return sb.ToString();
        }

        internal static void Write(
            string category,
            string message)
        {
            string line =
                DateTime.Now.ToString("HH:mm:ss.fff") +
                " | f=" + Time.frameCount +
                " | " + category +
                " | " + message;

            WriteRaw(line);

            if (Log != null)
            {
                Log.LogInfo(
                    "[RAFT_BRIDGE_PROTO] " +
                    category +
                    " | " +
                    message
                );
            }
        }

        internal static void WriteRaw(string line)
        {
            try
            {
                File.AppendAllText(
                    RuntimeLogPath,
                    line + Environment.NewLine,
                    Encoding.UTF8
                );
            }
            catch
            {
            }
        }
    }
}
