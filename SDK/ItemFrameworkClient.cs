using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Beam;

namespace BamEx.StrandedDeep.RaftFurniture
{
    /// <summary>
    /// Optional, reflection-only bridge to Stranded Deep Item Framework API v1.
    /// Raft Furniture keeps no compile-time reference to the Framework assembly.
    /// </summary>
    internal static class ItemFrameworkClient
    {
        internal const string PluginGuid = "com.bamex.strandeddeep.itemframework";
        internal const int RequiredApiVersion = 1;
        internal const int RaftSurfaceCapabilityBit = 1;

        private const string ApiTypeName =
            "BamEx.StrandedDeep.ItemFramework.ItemFrameworkApi";
        private const string MarkerTypeName =
            "BamEx.StrandedDeep.ItemFramework.FrameworkItemInstance";

        private static Type _apiType;
        private static Type _markerType;
        private static PropertyInfo _apiVersionProperty;
        private static PropertyInfo _itemIdProperty;
        private static PropertyInfo _prefabIdProperty;
        private static PropertyInfo _capabilitiesProperty;
        private static PropertyInfo _interactiveObjectProperty;
        private static MethodInfo _tryGetNativeReferenceIdMethod;

        internal sealed class Descriptor
        {
            internal string ItemId;
            internal uint PrefabId;
            internal int Capabilities;
            internal InteractiveObject InteractiveObject;
            internal string ReferenceId;

            internal bool HasRaftSurface
            {
                get
                {
                    return (Capabilities & RaftSurfaceCapabilityBit) != 0;
                }
            }
        }

        internal static bool TryBind(out string report)
        {
            report = null;

            _apiType = FindLoadedType(ApiTypeName);
            _markerType = FindLoadedType(MarkerTypeName);

            if (_apiType == null || _markerType == null)
            {
                report = "Item Framework not installed/loaded; bridge disabled.";
                ClearCachedReflection();
                return false;
            }

            _apiVersionProperty =
                _apiType.GetProperty(
                    "ApiVersion",
                    BindingFlags.Public | BindingFlags.Static);

            if (_apiVersionProperty == null)
            {
                report = "Item Framework ApiVersion not found; bridge disabled.";
                ClearCachedReflection();
                return false;
            }

            int apiVersion;
            try
            {
                apiVersion = Convert.ToInt32(
                    _apiVersionProperty.GetValue(null, null));
            }
            catch (Exception ex)
            {
                report = "Could not read Item Framework ApiVersion: " + ex.Message;
                ClearCachedReflection();
                return false;
            }

            if (apiVersion < RequiredApiVersion)
            {
                report =
                    "Item Framework API too old. required=" +
                    RequiredApiVersion + " actual=" + apiVersion;
                ClearCachedReflection();
                return false;
            }

            _itemIdProperty = GetPublicInstanceProperty(_markerType, "ItemId");
            _prefabIdProperty = GetPublicInstanceProperty(_markerType, "PrefabId");
            _capabilitiesProperty =
                GetPublicInstanceProperty(_markerType, "Capabilities");
            _interactiveObjectProperty =
                GetPublicInstanceProperty(_markerType, "InteractiveObject");
            _tryGetNativeReferenceIdMethod =
                _markerType.GetMethod(
                    "TryGetNativeReferenceId",
                    BindingFlags.Public | BindingFlags.Instance);

            if (_itemIdProperty == null ||
                _prefabIdProperty == null ||
                _capabilitiesProperty == null ||
                _interactiveObjectProperty == null ||
                _tryGetNativeReferenceIdMethod == null)
            {
                report =
                    "Item Framework marker API incomplete; bridge disabled.";
                ClearCachedReflection();
                return false;
            }

            report =
                "Item Framework API bound. api=" + apiVersion +
                " marker=" + _markerType.FullName;
            return true;
        }

        internal static Type MarkerType
        {
            get { return _markerType; }
        }

        internal static bool TryReadDescriptor(
            object marker,
            out Descriptor descriptor,
            out string error)
        {
            descriptor = null;
            error = null;

            if (marker == null || _markerType == null || !_markerType.IsInstanceOfType(marker))
            {
                error = "Invalid FrameworkItemInstance marker.";
                return false;
            }

            try
            {
                Descriptor result = new Descriptor();

                object itemId = _itemIdProperty.GetValue(marker, null);
                object prefabId = _prefabIdProperty.GetValue(marker, null);
                object capabilities = _capabilitiesProperty.GetValue(marker, null);
                object interactive =
                    _interactiveObjectProperty.GetValue(marker, null);

                result.ItemId = itemId == null ? null : itemId.ToString();
                result.PrefabId = prefabId == null
                    ? 0u
                    : Convert.ToUInt32(prefabId);
                result.Capabilities = capabilities == null
                    ? 0
                    : Convert.ToInt32(capabilities);
                result.InteractiveObject = interactive as InteractiveObject;

                object[] referenceArgs = new object[] { null };
                object referenceResult =
                    _tryGetNativeReferenceIdMethod.Invoke(marker, referenceArgs);

                if (referenceResult is bool && (bool)referenceResult)
                    result.ReferenceId = referenceArgs[0] as string;

                if (String.IsNullOrEmpty(result.ItemId) ||
                    result.PrefabId == 0u ||
                    result.InteractiveObject == null)
                {
                    error = "Framework marker returned incomplete identity.";
                    return false;
                }

                descriptor = result;
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                error = "Framework marker read failed: " + inner.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = "Framework marker read failed: " + ex.Message;
                return false;
            }
        }

        private static PropertyInfo GetPublicInstanceProperty(
            Type type,
            string name)
        {
            if (type == null)
                return null;

            return type.GetProperty(
                name,
                BindingFlags.Public | BindingFlags.Instance);
        }

        private static Type FindLoadedType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];
                if (assembly == null)
                    continue;

                try
                {
                    Type type = assembly.GetType(fullName, false);
                    if (type != null)
                        return type;
                }
                catch
                {
                }
            }

            return null;
        }

        private static void ClearCachedReflection()
        {
            _apiType = null;
            _markerType = null;
            _apiVersionProperty = null;
            _itemIdProperty = null;
            _prefabIdProperty = null;
            _capabilitiesProperty = null;
            _interactiveObjectProperty = null;
            _tryGetNativeReferenceIdMethod = null;
        }
    }
}
