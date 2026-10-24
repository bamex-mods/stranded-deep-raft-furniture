using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Beam;
using Beam.Crafting;

namespace BamEx.StrandedDeep.RaftFurniture
{
    /// <summary>
    /// Parallel integration path for Item Framework items that declare the
    /// RaftSurface capability. It does not use or mutate the legacy
    /// RaftAttachmentRegistry used by existing Raft Furniture object families.
    /// Framework-item raft association is intentionally runtime-derived from
    /// the native restored world pose; no second attachment sidecar is used.
    /// </summary>
    internal static class FrameworkRaftItemBridge
    {
        private const string ConfigSection = "Framework Item API";
        private const string LogFileName = "ItemApiRuntime.log";

        private static readonly Dictionary<int, FrameworkRaftItemAgent> Agents =
            new Dictionary<int, FrameworkRaftItemAgent>();

        private static ConfigEntry<bool> _enabled;
        private static string _logFile;
        private static bool _initialized;
        private static bool _frameworkBound;

        internal static bool Enabled
        {
            get { return _enabled != null && _enabled.Value; }
        }

        internal static void Initialize(
            Harmony harmony,
            ConfigFile config)
        {
            if (_initialized)
                return;

            _initialized = true;

            _logFile = Path.Combine(
                Paths.ConfigPath,
                "StrandedDeepRaftFurniture",
                LogFileName);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logFile));
                File.WriteAllText(
                    _logFile,
                    "Raft Furniture Framework Item API v1\r\n" +
                    "Framework item attachment: runtime-derived; native save owns item pose\r\n");
            }
            catch
            {
            }

            if (config == null)
            {
                Emit("BRIDGE_DISABLED", "ConfigFile unavailable.");
                return;
            }

            _enabled = config.Bind(
                ConfigSection,
                "EnableFrameworkRaftItems",
                true,
                "Allow Item Framework items with the RaftSurface capability to " +
                "attach to raft decks. Default ON. Requires restart after changing.");

            if (!_enabled.Value)
            {
                Emit(
                    "BRIDGE_DISABLED",
                    "EnableFrameworkRaftItems=false");
                return;
            }

            if (harmony == null)
            {
                Emit("BRIDGE_DISABLED", "Harmony instance unavailable.");
                return;
            }

            string bindReport;
            if (!ItemFrameworkClient.TryBind(out bindReport))
            {
                Emit("BRIDGE_DISABLED", bindReport);
                return;
            }

            Type markerType = ItemFrameworkClient.MarkerType;
            MethodInfo start = AccessTools.Method(markerType, "Start");
            MethodInfo destroy = AccessTools.Method(markerType, "OnDestroy");
            MethodInfo startPostfix = AccessTools.Method(
                typeof(FrameworkRaftItemBridge),
                "MarkerStartPostfix");
            MethodInfo destroyPrefix = AccessTools.Method(
                typeof(FrameworkRaftItemBridge),
                "MarkerDestroyPrefix");

            if (start == null || destroy == null ||
                startPostfix == null || destroyPrefix == null)
            {
                Emit(
                    "BRIDGE_DISABLED",
                    "Framework marker lifecycle methods not found.");
                return;
            }

            try
            {
                harmony.Patch(
                    start,
                    postfix: new HarmonyMethod(startPostfix));
                harmony.Patch(
                    destroy,
                    prefix: new HarmonyMethod(destroyPrefix));
            }
            catch (Exception ex)
            {
                Emit(
                    "BRIDGE_DISABLED",
                    "Harmony patch failed: " + ex);
                return;
            }

            _frameworkBound = true;
            Emit("BRIDGE_ENABLED", bindReport);
        }

        internal static void Shutdown()
        {
            if (!_initialized)
                return;

            List<FrameworkRaftItemAgent> snapshot =
                new List<FrameworkRaftItemAgent>(Agents.Values);

            for (int i = 0; i < snapshot.Count; i++)
            {
                FrameworkRaftItemAgent agent = snapshot[i];
                if (agent != null)
                    agent.ReleaseForBridgeShutdown();
            }

            Agents.Clear();
            _frameworkBound = false;
            _initialized = false;
        }

        private static void MarkerStartPostfix(object __instance)
        {
            if (!_frameworkBound || !Enabled || __instance == null)
                return;

            ItemFrameworkClient.Descriptor descriptor;
            string error;

            if (!ItemFrameworkClient.TryReadDescriptor(
                    __instance,
                    out descriptor,
                    out error))
            {
                Emit("INSTANCE_REJECT", error);
                return;
            }

            if (!descriptor.HasRaftSurface)
            {
                Emit(
                    "INSTANCE_IGNORE",
                    "itemId=" + descriptor.ItemId +
                    " prefabId=" + descriptor.PrefabId +
                    " reason=NO_RAFT_SURFACE");
                return;
            }

            if (String.IsNullOrEmpty(descriptor.ReferenceId))
            {
                Emit(
                    "INSTANCE_REJECT",
                    "itemId=" + descriptor.ItemId +
                    " prefabId=" + descriptor.PrefabId +
                    " reason=REFERENCE_ID_UNAVAILABLE");
                return;
            }

            InteractiveObject item = descriptor.InteractiveObject;
            if (item == null)
                return;

            int instanceId = item.gameObject.GetInstanceID();

            FrameworkRaftItemAgent agent =
                item.gameObject.GetComponent<FrameworkRaftItemAgent>();

            if (agent == null)
                agent = item.gameObject.AddComponent<FrameworkRaftItemAgent>();

            agent.Configure(
                descriptor.ItemId,
                descriptor.PrefabId,
                descriptor.ReferenceId,
                item);

            Agents[instanceId] = agent;

            Emit(
                "INSTANCE_AVAILABLE",
                "itemId=" + descriptor.ItemId +
                " prefabId=" + descriptor.PrefabId +
                " reference=" + descriptor.ReferenceId +
                " instance=" + instanceId);
        }

        private static void MarkerDestroyPrefix(object __instance)
        {
            if (__instance == null)
                return;

            ItemFrameworkClient.Descriptor descriptor;
            string error;
            if (!ItemFrameworkClient.TryReadDescriptor(
                    __instance,
                    out descriptor,
                    out error) ||
                descriptor == null ||
                descriptor.InteractiveObject == null)
            {
                return;
            }

            int instanceId =
                descriptor.InteractiveObject.gameObject.GetInstanceID();

            FrameworkRaftItemAgent agent;
            if (Agents.TryGetValue(instanceId, out agent) && agent != null)
            {
                // Framework API contract explicitly states OnDestroy can also be
                // zone unload. Runtime physics is released only; Framework-item
                // raft association is reconstructed from native world pose later.
                agent.ReleaseForNativeDestroyOrUnload();
            }

            Agents.Remove(instanceId);

            Emit(
                "INSTANCE_DESTROY_OR_UNLOAD",
                "itemId=" + descriptor.ItemId +
                " reference=" + Safe(descriptor.ReferenceId) +
                " instance=" + instanceId);
        }

        internal static void NotifyAgentEvent(
            string kind,
            FrameworkRaftItemAgent agent,
            string detail)
        {
            if (agent == null)
                return;

            Emit(
                kind,
                "itemId=" + Safe(agent.ItemId) +
                " reference=" + Safe(agent.ReferenceId) +
                " " + Safe(detail));
        }

        private static void Emit(string kind, string detail)
        {
            string line =
                DateTime.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss.fff",
                    CultureInfo.InvariantCulture) +
                " | " + kind + " | " + Safe(detail);

            try
            {
                if (!String.IsNullOrEmpty(_logFile))
                    File.AppendAllText(_logFile, line + Environment.NewLine);
            }
            catch
            {
            }

            if (Plugin.Log != null)
                Plugin.Log.LogInfo("[RaftItemApi] " + kind + " " + Safe(detail));
        }

        private static string Safe(string value)
        {
            return value ?? String.Empty;
        }
    }

    internal sealed class FrameworkRaftItemAgent : MonoBehaviour
    {
        private sealed class CollisionPairState
        {
            internal Collider ItemCollider;
            internal Collider RaftCollider;
            internal bool WasIgnored;
        }

        private string _itemId;
        private uint _prefabId;
        private string _referenceId;
        private InteractiveObject _item;
        private Rigidbody _body;
        private Collider[] _itemColliders;

        private Construction_STRUCTURE_RAFT _raft;
        private Vector3 _localPosition;
        private Quaternion _localRotation;
        private bool _attached;
        private bool _carriedFromRaft;
        private bool _configured;
        private float _nextWorldEvaluation;

        // Native cold restore can temporarily leave a real WORLD item
        // kinematic even though it is already spatially restored on the deck.
        // During this short per-instance grace window we allow a geometry-only
        // raft rebind. This is intentionally bounded and never scans the world.
        private float _coldRestoreProbeUntil;
        private bool _coldRestoreProbeLogged;

        private bool _cachedBodyState;
        private bool _oldIsKinematic;
        private bool _oldUseGravity;
        private RigidbodyConstraints _oldConstraints;

        private readonly List<CollisionPairState> _collisionPairs =
            new List<CollisionPairState>();

        internal string ItemId { get { return _itemId; } }
        internal string ReferenceId { get { return _referenceId; } }

        internal void Configure(
            string itemId,
            uint prefabId,
            string referenceId,
            InteractiveObject item)
        {
            _itemId = itemId;
            _prefabId = prefabId;
            _referenceId = referenceId;
            _item = item;
            _body = item == null ? null : item.GetComponent<Rigidbody>();
            _itemColliders = item == null
                ? new Collider[0]
                : item.GetComponentsInChildren<Collider>(true);
            _configured = item != null;
            _nextWorldEvaluation = Time.unscaledTime + 0.25f;
            _coldRestoreProbeUntil = Time.unscaledTime + 12.0f;
            _coldRestoreProbeLogged = false;

            FrameworkRaftItemBridge.NotifyAgentEvent(
                "AGENT_READY",
                this,
                "prefabId=" + _prefabId);
        }

        private void LateUpdate()
        {
            if (!_configured || _item == null)
                return;

            if (IsHeldByPlayer())
            {
                if (_attached)
                    BeginCarryFromRaft("HELD_DETECTED");
                return;
            }

            if (_attached)
            {
                ApplyAttachedPose();
                return;
            }

            if (!gameObject.activeInHierarchy ||
                Time.unscaledTime < _nextWorldEvaluation)
                return;

            _nextWorldEvaluation = Time.unscaledTime + 0.20f;

            bool dynamicWorldCandidate = IsWorldDynamicCandidate();
            bool coldRestoreCandidate =
                !dynamicWorldCandidate && IsColdRestoreWorldCandidate();

            if (!dynamicWorldCandidate && !coldRestoreCandidate)
                return;

            Construction_STRUCTURE_RAFT raft;
            RaycastHit deckHit;
            if (TryFindCloseRaftDeck(out raft, out deckHit))
            {
                if (coldRestoreCandidate && !_coldRestoreProbeLogged)
                {
                    _coldRestoreProbeLogged = true;
                    FrameworkRaftItemBridge.NotifyAgentEvent(
                        "COLD_LOAD_REBIND",
                        this,
                        "bodyKinematic=" + (_body != null && _body.isKinematic));
                }

                AttachToRaft(
                    raft,
                    deckHit,
                    coldRestoreCandidate
                        ? "COLD_LOAD_ON_DECK"
                        : "WORLD_ON_DECK");
            }
            else if (_carriedFromRaft)
            {
                FinishOffRaftDrop("WORLD_OFF_DECK");
            }
        }

        private void OnTransformParentChanged()
        {
            if (!_configured || !_attached)
                return;

            if (IsHeldByPlayer())
                BeginCarryFromRaft("PARENT_TO_HELD");
        }

        private void OnDisable()
        {
            if (!_configured)
                return;

            // Inventory/storage and zone streaming may disable a live native host.
            // Framework-item raft association is runtime-derived, so a disabled
            // instance releases only runtime raft physics. Re-enable evaluates WORLD.
            if (_attached || _carriedFromRaft)
                ReleaseRuntimePhysics(false, "HOST_DISABLED");
        }

        private void OnEnable()
        {
            if (_configured)
                _nextWorldEvaluation = Time.unscaledTime + 0.25f;
        }

        internal void ReleaseForNativeDestroyOrUnload()
        {
            ReleaseRuntimePhysics(false, "NATIVE_DESTROY_OR_UNLOAD");
        }

        internal void ReleaseForBridgeShutdown()
        {
            ReleaseRuntimePhysics(true, "BRIDGE_SHUTDOWN");
        }

        private bool IsHeldByPlayer()
        {
            if (_item == null)
                return false;

            Transform current = _item.transform.parent;
            while (current != null)
            {
                string name = current.name;
                if (String.Equals(
                        name,
                        "HoldingPosition",
                        StringComparison.Ordinal) ||
                    String.Equals(
                        name,
                        "Items_Holding_Position",
                        StringComparison.Ordinal))
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        private bool IsWorldDynamicCandidate()
        {
            if (_body == null)
                return false;

            // The synthetic Framework host is field-proven as dynamic in WORLD.
            // Inventory/held paths are kinematic or inactive. We deliberately do
            // not infer semantic ownership from transform parent alone.
            return !_body.isKinematic;
        }

        private bool IsColdRestoreWorldCandidate()
        {
            if (_body == null || !_body.isKinematic)
                return false;

            if (Time.unscaledTime > _coldRestoreProbeUntil)
                return false;

            if (!gameObject.activeInHierarchy || IsHeldByPlayer())
                return false;

            // Do not broaden this into a generic kinematic-item policy. The deck
            // raycast + tight bottom separation remains the final eligibility
            // check, and this exception expires shortly after InstanceAvailable.
            return true;
        }

        private bool TryFindCloseRaftDeck(
            out Construction_STRUCTURE_RAFT raft,
            out RaycastHit hit)
        {
            raft = null;
            hit = new RaycastHit();

            if (_body != null &&
                (_body.velocity.sqrMagnitude > 0.75f ||
                 _body.angularVelocity.sqrMagnitude > 16.0f))
                return false;

            float bottomY = transform.position.y;
            bool hasBounds = false;

            if (_itemColliders != null)
            {
                for (int i = 0; i < _itemColliders.Length; i++)
                {
                    Collider collider = _itemColliders[i];
                    if (collider == null || !collider.enabled || collider.isTrigger)
                        continue;

                    float y = collider.bounds.min.y;
                    if (!hasBounds || y < bottomY)
                    {
                        bottomY = y;
                        hasBounds = true;
                    }
                }
            }

            Vector3 origin = transform.position + Vector3.up * 0.35f;

            if (!Physics.Raycast(
                    origin,
                    Vector3.down,
                    out hit,
                    0.85f,
                    RaftFurnitureRuntime.RaftFloorMask,
                    QueryTriggerInteraction.Ignore))
                return false;

            if (hit.collider == null ||
                hit.collider.gameObject.layer != RaftFurnitureRuntime.RaftFloorLayer)
                return false;

            float separation = Mathf.Abs(bottomY - hit.point.y);
            if (separation > 0.22f)
                return false;

            Construction_RAFT piece =
                hit.collider.GetComponent<Construction_RAFT>();

            if (piece == null)
                piece = hit.collider.GetComponentInParent<Construction_RAFT>();

            if (piece == null)
                return false;

            raft =
                RaftFurnitureRuntime.GetStructure(piece)
                as Construction_STRUCTURE_RAFT;

            return raft != null;
        }

        private void AttachToRaft(
            Construction_STRUCTURE_RAFT raft,
            RaycastHit deckHit,
            string reason)
        {
            if (raft == null || _item == null)
                return;

            if (_attached && _raft == raft)
                return;

            if (_attached)
                ReleaseRuntimePhysics(true, "REATTACH_DIFFERENT_RAFT");

            if (_carriedFromRaft)
            {
                // The old own-raft collision state is only restored now, after
                // the object is safely back in WORLD and no longer in the pickup
                // transition. This mirrors the proven furniture safety rule.
                RestoreCollisionPairs();
                _carriedFromRaft = false;
            }

            _raft = raft;

            Vector3 local =
                raft.transform.InverseTransformPoint(transform.position);

            Quaternion localRotation =
                Quaternion.Inverse(raft.transform.rotation) * transform.rotation;

            _localPosition = local;
            _localRotation = localRotation;

            CacheBodyState();
            CaptureAndIgnoreOwnRaftCollisions(raft);

            if (_body != null)
            {
                _body.velocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
                _body.isKinematic = true;
                _body.useGravity = false;
                _body.constraints = RigidbodyConstraints.None;
            }

            _attached = true;
            ApplyAttachedPose();

            FrameworkRaftItemBridge.NotifyAgentEvent(
                "RAFT_ATTACH",
                this,
                "reason=" + reason +
                " raft=" + SafeReferenceId(raft) +
                " local=" + FormatVector(_localPosition) +
                " localRot=" + FormatQuaternion(_localRotation));
        }

        private void BeginCarryFromRaft(string reason)
        {
            if (!_attached)
                return;

            Construction_STRUCTURE_RAFT oldRaft = _raft;

            _attached = false;
            _raft = null;
            _carriedFromRaft = true;

            // Do not restore old own-raft collision here. The item may still be
            // touching the deck during pickup and an early restore can inject a
            // violent impulse into the dynamic raft. Native pickup owns Rigidbody
            // state from this point onward.

            FrameworkRaftItemBridge.NotifyAgentEvent(
                "RAFT_CARRY_BEGIN",
                this,
                "reason=" + reason +
                " oldRaft=" + SafeReferenceId(oldRaft));
        }

        private void FinishOffRaftDrop(string reason)
        {
            RestoreCollisionPairs();
            _carriedFromRaft = false;
            _raft = null;
            _cachedBodyState = false;

            FrameworkRaftItemBridge.NotifyAgentEvent(
                "RAFT_DETACH",
                this,
                "reason=" + reason);
        }

        private void ApplyAttachedPose()
        {
            if (!_attached || _raft == null || _item == null)
                return;

            Vector3 worldPosition =
                _raft.transform.TransformPoint(_localPosition);

            Quaternion worldRotation =
                _raft.transform.rotation * _localRotation;

            if (_body != null)
            {
                _body.velocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
                _body.isKinematic = true;
                _body.useGravity = false;
                _body.constraints = RigidbodyConstraints.None;
                _body.position = worldPosition;
                _body.rotation = worldRotation;
            }

            transform.position = worldPosition;
            transform.rotation = worldRotation;
        }

        private void CacheBodyState()
        {
            if (_body == null || _cachedBodyState)
                return;

            _oldIsKinematic = _body.isKinematic;
            _oldUseGravity = _body.useGravity;
            _oldConstraints = _body.constraints;
            _cachedBodyState = true;
        }

        private void RestoreBodyStateIfOwned()
        {
            if (_body == null || !_cachedBodyState)
                return;

            _body.isKinematic = _oldIsKinematic;
            _body.useGravity = _oldUseGravity;
            _body.constraints = _oldConstraints;
            _cachedBodyState = false;
        }

        private void CaptureAndIgnoreOwnRaftCollisions(
            Construction_STRUCTURE_RAFT raft)
        {
            RestoreCollisionPairs();

            if (raft == null || _itemColliders == null)
                return;

            IList<Constructing> constructions = raft.Constructions;
            if (constructions == null)
                return;

            for (int i = 0; i < constructions.Count; i++)
            {
                Construction_RAFT piece = constructions[i] as Construction_RAFT;
                if (piece == null)
                    continue;

                Collider[] raftColliders =
                    piece.GetComponentsInChildren<Collider>(true);

                for (int a = 0; a < _itemColliders.Length; a++)
                {
                    Collider itemCollider = _itemColliders[a];
                    if (itemCollider == null)
                        continue;

                    for (int b = 0; b < raftColliders.Length; b++)
                    {
                        Collider raftCollider = raftColliders[b];
                        if (raftCollider == null || raftCollider == itemCollider)
                            continue;

                        bool wasIgnored =
                            Physics.GetIgnoreCollision(itemCollider, raftCollider);

                        CollisionPairState state = new CollisionPairState();
                        state.ItemCollider = itemCollider;
                        state.RaftCollider = raftCollider;
                        state.WasIgnored = wasIgnored;
                        _collisionPairs.Add(state);

                        if (!wasIgnored)
                            Physics.IgnoreCollision(itemCollider, raftCollider, true);
                    }
                }
            }
        }

        private void RestoreCollisionPairs()
        {
            for (int i = 0; i < _collisionPairs.Count; i++)
            {
                CollisionPairState state = _collisionPairs[i];
                if (state == null ||
                    state.ItemCollider == null ||
                    state.RaftCollider == null)
                    continue;

                try
                {
                    bool current = Physics.GetIgnoreCollision(
                        state.ItemCollider,
                        state.RaftCollider);

                    if (current != state.WasIgnored)
                    {
                        Physics.IgnoreCollision(
                            state.ItemCollider,
                            state.RaftCollider,
                            state.WasIgnored);
                    }
                }
                catch
                {
                }
            }

            _collisionPairs.Clear();
        }

        private void ReleaseRuntimePhysics(
            bool restoreBody,
            string reason)
        {
            bool wasAttached = _attached;
            Construction_STRUCTURE_RAFT oldRaft = _raft;

            _attached = false;
            _raft = null;
            _carriedFromRaft = false;

            RestoreCollisionPairs();

            if (restoreBody)
                RestoreBodyStateIfOwned();
            else
                _cachedBodyState = false;

            if (wasAttached)
            {
                FrameworkRaftItemBridge.NotifyAgentEvent(
                    "RAFT_RUNTIME_RELEASE",
                    this,
                    "reason=" + reason +
                    " oldRaft=" + SafeReferenceId(oldRaft));
            }
        }

        private static string FormatVector(Vector3 value)
        {
            return value.x.ToString("F3", CultureInfo.InvariantCulture) + "," +
                   value.y.ToString("F3", CultureInfo.InvariantCulture) + "," +
                   value.z.ToString("F3", CultureInfo.InvariantCulture);
        }

        private static string FormatQuaternion(Quaternion value)
        {
            return value.x.ToString("F4", CultureInfo.InvariantCulture) + "," +
                   value.y.ToString("F4", CultureInfo.InvariantCulture) + "," +
                   value.z.ToString("F4", CultureInfo.InvariantCulture) + "," +
                   value.w.ToString("F4", CultureInfo.InvariantCulture);
        }

        private static string SafeReferenceId(object target)
        {
            if (target == null)
                return String.Empty;

            try
            {
                Type type = target.GetType();
                while (type != null)
                {
                    PropertyInfo property = type.GetProperty(
                        "ReferenceId",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);

                    if (property != null && property.GetIndexParameters().Length == 0)
                    {
                        object value = property.GetValue(target, null);
                        if (value != null)
                            return value.ToString();
                    }

                    FieldInfo field = type.GetField(
                        "ReferenceId",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);

                    if (field == null)
                    {
                        field = type.GetField(
                            "_referenceId",
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly);
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

            return String.Empty;
        }
    }
}
