#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace NekoSerializer
{
#if ODIN_INSPECTOR
    using Sirenix.OdinInspector.Editor;
    using Sirenix.Utilities.Editor;
#endif

#if ODIN_INSPECTOR
    public partial class DataSerializationViewer : OdinEditorWindow
#else
    public partial class DataSerializationViewer : EditorWindow
#endif
    {
        // Unity 6+ UI Toolkit hosting flag (CreateGUI sets this).
        private bool _useUIToolkitHost;

        private Vector2 scrollPosition;
        private Vector2 jsonScrollPosition;
        private Dictionary<string, object> currentSaveData = new();
        private Dictionary<string, object> stagedSaveData = new();
        private readonly HashSet<string> _dirtyRootKeys = new();
        private readonly Dictionary<string, bool> foldoutStates = new();
        private readonly Dictionary<string, bool> dictionaryFoldoutStates = new();

        // Struct/class drawing is handled via reflection in a single composite drawer.

        // Used ONLY for deciding whether a JObject root should be rendered as a struct/class field group.
        // This does not participate in serialization/deserialization.
        private static readonly Dictionary<string, Type> s_viewerSerializableTypeByName = new(StringComparer.Ordinal);

        // Data View root ordering cache:
        // - LastSaveTime should always appear first.
        // - Everything else should follow the "raw" order coming from the save system.
        // - Cache the ordering so we don't allocate/sort every repaint.
        private readonly List<string> _dataViewRootKeyRawOrder = new();
        private readonly List<string> _dataViewRootKeyDisplayOrder = new();
        private bool _dataViewRootKeyOrderDirty = true;

        private readonly Dictionary<string, string> _newDictionaryKeyByPath = new();

        // Refresh model:
        // - Refresh once when the window opens/enables
        // - Refresh when the user presses the Refresh button
        // - Refresh when entering Play Mode (to populate data)

        private bool _warmupDataRefreshActive = false;
        private int _warmupDataRefreshTriesRemaining = 0;

        private bool _warmupJsonRefreshActive = false;
        private int _warmupJsonRefreshTriesRemaining = 0;

        private string _colorizedJsonCache;
        private string _colorizedJsonCacheSource;

        private const int CollectionItemsPerPage = 10;
        private readonly Dictionary<string, int> _collectionPageByPath = new();

        // Pagination
        private int currentPage = 0;
        private const int itemsPerPage = 10;

        private static bool DrawCompactPagerRight(ref int page, int totalPages)
        {
            if (totalPages <= 1)
                return false;

            bool changed = false;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(page <= 0))
                {
                    if (GUILayout.Button("◀", GUILayout.Width(22)))
                    {
                        page = Mathf.Max(0, page - 1);
                        changed = true;
                    }
                }

                int pageOneBased = page + 1;
                EditorGUI.BeginChangeCheck();
                pageOneBased = EditorGUILayout.IntField(pageOneBased, GUILayout.Width(38));
                if (EditorGUI.EndChangeCheck())
                {
                    pageOneBased = Mathf.Clamp(pageOneBased, 1, totalPages);
                    page = pageOneBased - 1;
                    changed = true;
                }

                GUILayout.Label($"/ {totalPages}", EditorStyles.miniLabel, GUILayout.Width(60));

                using (new EditorGUI.DisabledScope(page >= totalPages - 1))
                {
                    if (GUILayout.Button("▶", GUILayout.Width(22)))
                    {
                        page = Mathf.Min(totalPages - 1, page + 1);
                        changed = true;
                    }
                }
            }

            return changed;
        }

        // Tab system
        private int selectedTab = 0;
        private readonly string[] tabs = { "Data View", "JSON View" };
        private string rawJsonData = "";

#if ODIN_INSPECTOR
        private readonly Dictionary<string, PropertyTree> _odinTreeByPath = new();
        private readonly Dictionary<string, IOdinValueContainer> _odinContainerByPath = new();

        private struct OdinRootConversionCacheEntry
        {
            public object Source;
            public object Converted;
            public Func<object, object> ConvertBack;
        }

        private readonly Dictionary<string, OdinRootConversionCacheEntry> _odinRootConversionByKey = new();

        private void DisposeOdinCaches()
        {
            foreach (var kvp in _odinTreeByPath)
            {
                try
                {
                    kvp.Value?.Dispose();
                }
                catch
                {
                    // Ignore dispose errors; we're shutting down or rebuilding UI state.
                }
            }

            _odinTreeByPath.Clear();
            _odinContainerByPath.Clear();
            _odinRootConversionByKey.Clear();
        }
#endif

        [MenuItem("Tools/Neko Serializer/Data Serialization Viewer")]
        private static void OpenWindow()
        {
            GetWindow<DataSerializationViewer>("Data Serialization Viewer").Show();
        }

#if ODIN_INSPECTOR
        protected override void OnEnable()
        {
            base.OnEnable();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            SerializationService.EditorPlayModeSaved += OnGameplaySaved;
            if (Application.isPlaying)
            {
                if (selectedTab == 1)
                    RefreshJsonView();
                else
                    Refresh();
            }
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            SerializationService.EditorPlayModeSaved -= OnGameplaySaved;

            DisposeOdinCaches();
        }
#else
        private void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            SerializationService.EditorPlayModeSaved += OnGameplaySaved;
            if (Application.isPlaying)
            {
                if (selectedTab == 1)
                    RefreshJsonView();
                else
                    Refresh();
            }
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            SerializationService.EditorPlayModeSaved -= OnGameplaySaved;
        }
#endif

        private void OnGameplaySaved()
        {
            if (!Application.isPlaying)
                return;

            if (selectedTab == 1)
                RefreshJsonView();
            else
                Refresh();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                if (selectedTab == 1)
                    RefreshJsonView();
                else
                    Refresh();
            }
        }

#if ODIN_INSPECTOR
        protected override void OnImGUI()
        {
            if (_useUIToolkitHost)
                return;

            DrawIMGUIRoot();
        }
#else
        private void OnGUI()
        {
            if (_useUIToolkitHost)
                return;

            DrawIMGUIRoot();
        }
#endif

        private void DrawIMGUIRoot()
        {
            EditorGUILayout.BeginVertical();
            EditorGUILayout.Space();

            // Always show tabs, but check prerequisites per tab
            DrawTabs();

            if (!CheckViewPrerequisites())
            {
                EditorGUILayout.EndVertical();
                return;
            }

            DrawContent();
            DrawBottomButtons();

            EditorGUILayout.EndVertical();
        }


        private static bool IsSingleValue(object value)
        {
            if (value == null) return true;

            // Unwrap JValue
            if (value is JValue jv)
                value = jv.Value;

            if (value == null) return true;

            // Treat vector objects encoded as JObject as single values
            if (value is JObject jobj)
            {
                bool looksLikeVector2 = jobj.ContainsKey("x") && jobj.ContainsKey("y") && !jobj.ContainsKey("z");
                bool looksLikeVector3 = jobj.ContainsKey("x") && jobj.ContainsKey("y") && jobj.ContainsKey("z") && !jobj.ContainsKey("w");
                if (looksLikeVector2 || looksLikeVector3) return true;
                return false;
            }

            // Treat vector dictionaries {x,y[,z]} as single values.
            if (value is System.Collections.IDictionary dict)
            {
                if (LooksLikeVector3Dictionary(dict) || LooksLikeVector2Dictionary(dict))
                    return true;
                return false;
            }

            if (value is JArray) return false;
            if (value is System.Collections.IList) return false;
            if (value is Array) return false;

            var t = value.GetType();
            if (t.IsPrimitive) return true;
            if (t == typeof(string)) return true;
            if (t == typeof(decimal)) return true;
            if (t == typeof(DateTime)) return true;
            if (t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Vector4)) return true;
            if (t == typeof(Color) || t == typeof(Quaternion)) return true;

            // Anything else could expand into fields, so keep foldout.
            return false;
        }

        private void DisplayData(string rootKey, object obj)
        {
            if (string.IsNullOrWhiteSpace(rootKey))
                rootKey = "(Unknown Key)";

            bool changed = false;
            object updated = obj;

            // Root values are stored as object; draw an inspector-like editor and save on change.
            changed |= DrawEditableAny("", ref updated, rootKey);

            if (changed)
            {
                stagedSaveData[rootKey] = updated;
                MarkRootDirty(rootKey);
            }
        }

        private bool DrawEditableAny(string label, ref object value, string path)
        {
            // Handle null
            if (value == null)
            {
                if (!string.IsNullOrEmpty(label))
                    EditorGUILayout.LabelField(label, "null");
                else
                    EditorGUILayout.LabelField("null");
                return false;
            }

            // Handle JToken wrappers (common when loaded as object from JSON)
            if (value is JValue jValue)
            {
                object raw = jValue.Value;
                if (raw == null)
                {
                    if (!string.IsNullOrEmpty(label))
                        EditorGUILayout.LabelField(label, "null");
                    else
                        EditorGUILayout.LabelField("null");
                    return false;
                }

                object boxed = raw;
                bool changed = DrawEditableAny(label, ref boxed, path);
                if (changed)
                {
                    value = new JValue(boxed);
                }
                return changed;
            }

            if (value is JObject jObject)
            {
                // Special-case common Unity structs encoded as {x,y,z,...}
                if (IsVector3(jObject))
                {
                    var v = ParseVector3(jObject);
                    EditorGUI.BeginChangeCheck();
                    v = DrawVector3InlineField(string.IsNullOrEmpty(label) ? "Vector3" : label, v);
                    if (EditorGUI.EndChangeCheck())
                    {
                        value = new JObject
                        {
                            ["x"] = v.x,
                            ["y"] = v.y,
                            ["z"] = v.z,
                        };
                        return true;
                    }
                    return false;
                }

                if (IsVector2(jObject))
                {
                    var v = ParseVector2(jObject);
                    EditorGUI.BeginChangeCheck();
                    v = DrawVector2InlineField(string.IsNullOrEmpty(label) ? "Vector2" : label, v);
                    if (EditorGUI.EndChangeCheck())
                    {
                        value = new JObject
                        {
                            ["x"] = v.x,
                            ["y"] = v.y,
                        };
                        return true;
                    }
                    return false;
                }

                // Generic object (dictionary-like)
                // Root values already have an outer foldout; avoid nested foldouts by using an empty label.
                return DrawEditableJObjectAsComposite(label, jObject, path);
            }

            if (value is JArray jArray)
            {
                return DrawEditableJArray(label, jArray, path);
            }

            // Dictionaries
            if (value is System.Collections.IDictionary)
            {
                var dict = (System.Collections.IDictionary)value;

                // Many serializable structs/classes round-trip through storage as Dictionary<string, object>.
                // Treat that shape like an object (fields) rather than a "real" dictionary UI.
                if (LooksLikeObjectDictionary(dict))
                    return DrawEditableObjectDictionaryAsComposite(label, dict, path);

                // Special-case vector dictionaries {x,y[,z]} so they render as a single Vector field.
                if (LooksLikeVector3Dictionary(dict))
                {
                    var v = new Vector3(
                        GetFloatFromDictionary(dict, "x"),
                        GetFloatFromDictionary(dict, "y"),
                        GetFloatFromDictionary(dict, "z")
                    );

                    EditorGUI.BeginChangeCheck();
#if ODIN_INSPECTOR
                    v = SirenixEditorFields.Vector3Field(string.IsNullOrEmpty(label) ? "Vector3" : label, v);
#else
                    v = DrawVector3InlineField(string.IsNullOrEmpty(label) ? "Vector3" : label, v);
#endif
                    if (EditorGUI.EndChangeCheck())
                    {
                        dict["x"] = v.x;
                        dict["y"] = v.y;
                        dict["z"] = v.z;
                        return true;
                    }
                    return false;
                }

                if (LooksLikeVector2Dictionary(dict))
                {
                    var v = new Vector2(
                        GetFloatFromDictionary(dict, "x"),
                        GetFloatFromDictionary(dict, "y")
                    );

                    EditorGUI.BeginChangeCheck();
#if ODIN_INSPECTOR
                    v = SirenixEditorFields.Vector2Field(string.IsNullOrEmpty(label) ? "Vector2" : label, v);
#else
                    v = DrawVector2InlineField(string.IsNullOrEmpty(label) ? "Vector2" : label, v);
#endif
                    if (EditorGUI.EndChangeCheck())
                    {
                        dict["x"] = v.x;
                        dict["y"] = v.y;
                        return true;
                    }
                    return false;
                }

#if ODIN_INSPECTOR
                object boxed = value;
                bool changed = DrawOdinValue(string.IsNullOrEmpty(label) ? "Dictionary" : label, ref boxed, path);
                if (changed)
                    value = boxed;
                return changed;
#else
                return DrawEditableDictionary(string.IsNullOrEmpty(label) ? "Dictionary" : label, dict, path);
#endif
            }

            // Common primitives
            var t = value.GetType();

            if (t.IsEnum)
            {
                EditorGUI.BeginChangeCheck();
                var e = (Enum)value;
                e = EditorGUILayout.EnumPopup(string.IsNullOrEmpty(label) ? ObjectNames.NicifyVariableName(t.Name) : label, e);
                if (EditorGUI.EndChangeCheck())
                {
                    value = e;
                    return true;
                }
                return false;
            }

            if (t == typeof(decimal))
            {
                EditorGUI.BeginChangeCheck();
                decimal d = (decimal)value;
                double dd = EditorGUILayout.DoubleField(string.IsNullOrEmpty(label) ? "Decimal" : label, (double)d);
                if (EditorGUI.EndChangeCheck())
                {
                    try
                    {
                        value = (decimal)dd;
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }
                return false;
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(t))
            {
                EditorGUI.BeginChangeCheck();
                var o = (UnityEngine.Object)value;
                o = EditorGUILayout.ObjectField(string.IsNullOrEmpty(label) ? ObjectNames.NicifyVariableName(t.Name) : label, o, t, true);
                if (EditorGUI.EndChangeCheck())
                {
                    value = o;
                    return true;
                }
                return false;
            }

            if (t == typeof(string))
            {
                EditorGUI.BeginChangeCheck();
                string s = (string)value;
#if ODIN_INSPECTOR
                s = SirenixEditorFields.TextField(string.IsNullOrEmpty(label) ? "String" : label, s);
#else
                s = EditorGUILayout.TextField(string.IsNullOrEmpty(label) ? "String" : label, s);
#endif
                if (EditorGUI.EndChangeCheck()) { value = s; return true; }
                return false;
            }
            if (t == typeof(int))
            {
                EditorGUI.BeginChangeCheck();
                int v = (int)value;
#if ODIN_INSPECTOR
                v = SirenixEditorFields.IntField(string.IsNullOrEmpty(label) ? "Integer" : label, v);
#else
                v = EditorGUILayout.IntField(string.IsNullOrEmpty(label) ? "Integer" : label, v);
#endif
                if (EditorGUI.EndChangeCheck()) { value = v; return true; }
                return false;
            }
            if (t == typeof(long))
            {
                EditorGUI.BeginChangeCheck();
                long v = (long)value;
#if ODIN_INSPECTOR
                v = SirenixEditorFields.LongField(string.IsNullOrEmpty(label) ? "Long" : label, v);
#else
                v = EditorGUILayout.LongField(string.IsNullOrEmpty(label) ? "Long" : label, v);
#endif
                if (EditorGUI.EndChangeCheck()) { value = v; return true; }
                return false;
            }
            if (t == typeof(float))
            {
                EditorGUI.BeginChangeCheck();
                float v = (float)value;
#if ODIN_INSPECTOR
                v = SirenixEditorFields.FloatField(string.IsNullOrEmpty(label) ? "Float" : label, v);
#else
                v = EditorGUILayout.FloatField(string.IsNullOrEmpty(label) ? "Float" : label, v);
#endif
                if (EditorGUI.EndChangeCheck()) { value = v; return true; }
                return false;
            }
            if (t == typeof(double))
            {
                EditorGUI.BeginChangeCheck();
                double v = (double)value;
#if ODIN_INSPECTOR
                v = SirenixEditorFields.DoubleField(string.IsNullOrEmpty(label) ? "Double" : label, v);
#else
                v = EditorGUILayout.DoubleField(string.IsNullOrEmpty(label) ? "Double" : label, v);
#endif
                if (EditorGUI.EndChangeCheck()) { value = v; return true; }
                return false;
            }
            if (t == typeof(bool))
            {
                EditorGUI.BeginChangeCheck();
                bool v = (bool)value;
                v = EditorGUILayout.Toggle(string.IsNullOrEmpty(label) ? "Boolean" : label, v);
                if (EditorGUI.EndChangeCheck()) { value = v; return true; }
                return false;
            }

            // Common Unity structs
            if (t == typeof(Vector2))
            {
                EditorGUI.BeginChangeCheck();
                var v = (Vector2)value;
#if ODIN_INSPECTOR
                v = SirenixEditorFields.Vector2Field(string.IsNullOrEmpty(label) ? "Vector2" : label, v);
#else
                v = DrawVector2InlineField(string.IsNullOrEmpty(label) ? "Vector2" : label, v);
#endif
                if (EditorGUI.EndChangeCheck()) { value = v; return true; }
                return false;
            }
            if (t == typeof(Vector3))
            {
                EditorGUI.BeginChangeCheck();
                var v = (Vector3)value;
#if ODIN_INSPECTOR
                v = SirenixEditorFields.Vector3Field(string.IsNullOrEmpty(label) ? "Vector3" : label, v);
#else
                v = DrawVector3InlineField(string.IsNullOrEmpty(label) ? "Vector3" : label, v);
#endif
                if (EditorGUI.EndChangeCheck()) { value = v; return true; }
                return false;
            }
            if (t == typeof(Vector2Int))
            {
                EditorGUI.BeginChangeCheck();
                var v = (Vector2Int)value;
                v = DrawVector2IntInlineField(string.IsNullOrEmpty(label) ? "Vector2Int" : label, v);
                if (EditorGUI.EndChangeCheck()) { value = v; return true; }
                return false;
            }
            if (t == typeof(Vector3Int))
            {
                EditorGUI.BeginChangeCheck();
                var v = (Vector3Int)value;
                v = DrawVector3IntInlineField(string.IsNullOrEmpty(label) ? "Vector3Int" : label, v);
                if (EditorGUI.EndChangeCheck()) { value = v; return true; }
                return false;
            }
            if (t == typeof(Vector4))
            {
                EditorGUI.BeginChangeCheck();
                var v = (Vector4)value;
#if ODIN_INSPECTOR
                v = SirenixEditorFields.Vector4Field(string.IsNullOrEmpty(label) ? "Vector4" : label, v);
#else
                v = EditorGUILayout.Vector4Field(string.IsNullOrEmpty(label) ? "Vector4" : label, v);
#endif
                if (EditorGUI.EndChangeCheck()) { value = v; return true; }
                return false;
            }
            if (t == typeof(Quaternion))
            {
                // Unity doesn't have a direct QuaternionField, show as Vector4
                var q = (Quaternion)value;
                var v4 = new Vector4(q.x, q.y, q.z, q.w);
                EditorGUI.BeginChangeCheck();
#if ODIN_INSPECTOR
                v4 = SirenixEditorFields.Vector4Field(string.IsNullOrEmpty(label) ? "Quaternion" : label, v4);
#else
                v4 = EditorGUILayout.Vector4Field(string.IsNullOrEmpty(label) ? "Quaternion" : label, v4);
#endif
                if (EditorGUI.EndChangeCheck()) { value = new Quaternion(v4.x, v4.y, v4.z, v4.w); return true; }
                return false;
            }
            if (t == typeof(Color))
            {
                EditorGUI.BeginChangeCheck();
                var c = (Color)value;
#if ODIN_INSPECTOR
                c = SirenixEditorFields.ColorField(string.IsNullOrEmpty(label) ? "Color" : label, c);
#else
                c = EditorGUILayout.ColorField(string.IsNullOrEmpty(label) ? "Color" : label, c);
#endif
                if (EditorGUI.EndChangeCheck()) { value = c; return true; }
                return false;
            }

            // Arrays
            if (t.IsArray)
            {
                var arr = (Array)value;
                bool changed = DrawEditableArray(label, ref arr, t.GetElementType(), path);
                if (changed) value = arr;
                return changed;
            }

            // Lists
            if (value is System.Collections.IList list)
            {
                Type elementType = null;
                if (t.IsGenericType && t.GetGenericArguments().Length == 1)
                    elementType = t.GetGenericArguments()[0];
                return DrawEditableList(label, list, elementType, path);
            }

            // DateTime: editable grouped editor (year/month/day/hour/minute/second...)
            if (t == typeof(DateTime))
            {
                var dt = (DateTime)value;
                if (DrawEditableDateTimeBox(string.IsNullOrEmpty(label) ? "DateTime" : label, dt, out var updated))
                {
                    value = updated;
                    return true;
                }
                return false;
            }

            // Serializable objects: draw as a foldable boxed group of fields/collections.
            return DrawEditableCompositeObject(label, ref value, path);
        }

        private static bool DrawEditableDateTimeBox(string label, DateTime value, out DateTime updated)
        {
            updated = value;

#if ODIN_INSPECTOR
            SirenixEditorGUI.BeginBox();
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
#else
            EditorGUILayout.BeginVertical("helpBox");
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
#endif

            bool changed = DrawEditableDateTimeFields(value, out updated);

#if ODIN_INSPECTOR
            SirenixEditorGUI.EndBox();
#else
            EditorGUILayout.EndVertical();
#endif

            return changed;
        }

        private static bool DrawEditableDateTimeFields(DateTime value, out DateTime updated)
        {
            updated = value;

            int year = value.Year;
            int month = value.Month;
            int day = value.Day;
            int hour = value.Hour;
            int minute = value.Minute;
            int second = value.Second;
            int millisecond = value.Millisecond;

            EditorGUI.BeginChangeCheck();

            year = ClampIntField("Year", year, 1, 9999);
            month = ClampIntField("Month", month, 1, 12);

            int maxDay;
            try
            {
                maxDay = DateTime.DaysInMonth(year, month);
            }
            catch
            {
                maxDay = 28;
            }

            day = ClampIntField("Day", day, 1, maxDay);
            hour = ClampIntField("Hour", hour, 0, 23);
            minute = ClampIntField("Minute", minute, 0, 59);
            second = ClampIntField("Second", second, 0, 59);
            millisecond = ClampIntField("Millisecond", millisecond, 0, 999);

            bool changed = EditorGUI.EndChangeCheck();
            if (!changed)
                return false;

            try
            {
                updated = new DateTime(year, month, day, hour, minute, second, millisecond, value.Kind);
                return true;
            }
            catch
            {
                updated = value;
                return false;
            }
        }

        private static int ClampIntField(string label, int value, int min, int max)
        {
#if ODIN_INSPECTOR
            int next = SirenixEditorFields.IntField(label, value);
#else
            int next = EditorGUILayout.IntField(label, value);
#endif
            if (next < min) next = min;
            if (next > max) next = max;
            return next;
        }

        private void MarkRootDirty(string rootKey)
        {
            if (string.IsNullOrEmpty(rootKey))
                return;
            _dirtyRootKeys.Add(rootKey);

            // Ensure the Save button state updates immediately.
            Repaint();
        }

        private bool HasStagedChanges() => _dirtyRootKeys.Count > 0;

        private void CommitStagedChangesToStorage()
        {
            if (!Application.isPlaying)
                return;
            if (!HasStagedChanges())
                return;

            foreach (var key in _dirtyRootKeys)
            {
                if (!stagedSaveData.TryGetValue(key, out var value))
                    continue;
                SerializationService.SaveWithoutUpdatingLastSaveTime(key, value);
            }

            _dirtyRootKeys.Clear();

#if ODIN_INSPECTOR
            DisposeOdinCaches();
#endif
        }

        private void RefreshDiscardingStagedChangesIfConfirmed()
        {
            if (!Application.isPlaying)
                return;

            // Backwards-compatible shim. Refresh is now unconditional.
            Refresh();
        }

#if ODIN_INSPECTOR
        private static bool IsOdinNativeCollection(object value)
        {
            if (value == null) return false;
            if (value is JValue jv) value = jv.Value;
            if (value is JToken) return false;
            return value is System.Collections.IList || value is Array || value is System.Collections.IDictionary;
        }
#endif

        private bool DrawEditableCompositeObject(string label, ref object value, string path)
        {
            if (value == null)
                return false;

            // JSON tokens should stay on the JToken drawers.
            if (value is JToken)
                return false;

            var type = value.GetType();

            // These should have been handled by the primitive cases.
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime))
                return false;

            // Avoid reflection-drawing UnityEngine.Object internals.
            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
                return false;

            bool showHeader = !string.IsNullOrEmpty(label);
            string foldoutKey = $"{path}__obj";

            if (showHeader)
            {
                if (!dictionaryFoldoutStates.ContainsKey(foldoutKey))
                    dictionaryFoldoutStates[foldoutKey] = true;

                EditorGUILayout.BeginVertical("box");
                using (new EditorGUILayout.HorizontalScope())
                {
                    dictionaryFoldoutStates[foldoutKey] = EditorGUILayout.Foldout(dictionaryFoldoutStates[foldoutKey], label, true);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(ObjectNames.NicifyVariableName(type.Name), EditorStyles.miniLabel);
                }

                if (!dictionaryFoldoutStates[foldoutKey])
                {
                    EditorGUILayout.EndVertical();
                    return false;
                }

                EditorGUI.indentLevel++;
            }

            bool changed = false;
            object target = value;
            var fields = GetUnityLikeSerializableFields(type);
            var serializer = JsonSerializerUtils.GetSerializer();

            if (fields.Count == 0)
            {
                string typeName = GetReadableTypeName(value);
                string valueDisplay = GetReadableValueDisplay(value);
                EditorGUILayout.LabelField(typeName, valueDisplay);
            }
            else
            {
                for (int i = 0; i < fields.Count; i++)
                {
                    var field = fields[i];
                    if (field == null) continue;

                    string fieldLabel = ObjectNames.NicifyVariableName(field.Name);
                    string childPath = string.IsNullOrEmpty(path) ? field.Name : path + "." + field.Name;

                    object fieldValue;
                    try { fieldValue = field.GetValue(target); }
                    catch { continue; }

                    object boxed = fieldValue;
                    var expectedType = field.FieldType;
                    bool fieldChanged;

                    // If we know this field is a collection type, prefer the collection drawers.
                    // This ensures struct/class fields that are lists/arrays/dicts get the same UX as normal collections.
                    if (expectedType != null && expectedType.IsArray)
                    {
                        if (boxed is JToken tok)
                        {
                            try { boxed = tok.ToObject(expectedType, serializer); }
                            catch { /* ignore */ }
                        }

                        if (boxed is Array arr)
                        {
                            Type elementType = expectedType.GetElementType();
                            fieldChanged = DrawEditableArray(fieldLabel, ref arr, elementType, childPath);
                            if (fieldChanged)
                                boxed = arr;
                        }
                        else
                        {
                            fieldChanged = DrawEditableAny(fieldLabel, ref boxed, childPath);
                        }
                    }
                    else if (expectedType != null && typeof(System.Collections.IList).IsAssignableFrom(expectedType))
                    {
                        if (boxed is JToken tok)
                        {
                            try { boxed = tok.ToObject(expectedType, serializer); }
                            catch { /* ignore */ }
                        }

                        if (boxed is System.Collections.IList list)
                        {
                            Type elementType = null;
                            if (expectedType.IsGenericType && expectedType.GetGenericArguments().Length == 1)
                                elementType = expectedType.GetGenericArguments()[0];
                            fieldChanged = DrawEditableList(fieldLabel, list, elementType, childPath);
                        }
                        else
                        {
                            fieldChanged = DrawEditableAny(fieldLabel, ref boxed, childPath);
                        }
                    }
                    else if (expectedType != null && typeof(System.Collections.IDictionary).IsAssignableFrom(expectedType))
                    {
                        if (boxed is JToken tok)
                        {
                            try { boxed = tok.ToObject(expectedType, serializer); }
                            catch { /* ignore */ }
                        }

                        if (boxed is System.Collections.IDictionary dict)
                        {
#if ODIN_INSPECTOR
                            object boxedDict = dict;
                            fieldChanged = DrawOdinValue(fieldLabel, ref boxedDict, childPath);
                            if (fieldChanged)
                                boxed = boxedDict;
#else
                            fieldChanged = DrawEditableDictionary(fieldLabel, dict, childPath);
                            if (fieldChanged)
                                boxed = dict;
#endif
                        }
                        else
                        {
                            fieldChanged = DrawEditableAny(fieldLabel, ref boxed, childPath);
                        }
                    }
                    else
                    {
                        fieldChanged = DrawEditableAny(fieldLabel, ref boxed, childPath);
                    }

                    if (fieldChanged)
                    {
                        try
                        {
                            field.SetValue(target, CoerceToType(boxed, field.FieldType));
                            changed = true;
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }
            }

            if (changed)
                value = target;

            if (showHeader)
            {
                EditorGUI.indentLevel--;
                EditorGUILayout.EndVertical();
            }

            return changed;
        }

        private static List<FieldInfo> GetUnityLikeSerializableFields(Type type)
        {
            var result = new List<FieldInfo>();
            if (type == null)
                return result;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var fields = type.GetFields(flags);
            if (fields == null || fields.Length == 0)
                return result;

            for (int i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                if (field == null) continue;
                if (field.IsStatic) continue;
                if (Attribute.IsDefined(field, typeof(NonSerializedAttribute))) continue;
                if (Attribute.IsDefined(field, typeof(HideInInspector))) continue;

                bool isUnitySerialized = field.IsPublic || Attribute.IsDefined(field, typeof(SerializeField));
                if (!isUnitySerialized) continue;

                result.Add(field);
            }

            // Deterministic order.
            result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return result;
        }

        private static bool ShouldRenderJObjectRootAsStructFields(string rootKey, JObject obj)
        {
            if (string.IsNullOrEmpty(rootKey) || obj == null)
                return false;

            // Heuristic: if the root key matches a [Serializable] type name, and the JObject's properties
            // are a subset of that type's serializable fields, treat it as a struct/class.
            if (!TryResolveViewerSerializableTypeByName(rootKey, out var type))
                return false;

            var fields = GetUnityLikeSerializableFields(type);
            if (fields.Count == 0)
                return false;

            var fieldNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < fields.Count; i++)
                fieldNames.Add(fields[i].Name);

            int checkedCount = 0;
            foreach (var prop in obj.Properties())
            {
                // Ignore metadata-like properties.
                if (prop.Name != null && prop.Name.StartsWith("$", StringComparison.Ordinal))
                    continue;

                checkedCount++;
                if (!fieldNames.Contains(prop.Name))
                    return false;
            }

            return checkedCount > 0;
        }

        private static bool TryResolveViewerSerializableTypeByName(string typeName, out Type resolved)
        {
            resolved = null;
            if (string.IsNullOrEmpty(typeName))
                return false;

            if (s_viewerSerializableTypeByName.TryGetValue(typeName, out resolved))
                return resolved != null;

            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    var asm = assemblies[i];
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch { continue; }

                    for (int t = 0; t < types.Length; t++)
                    {
                        var candidate = types[t];
                        if (candidate == null) continue;
                        if (candidate.Name != typeName) continue;
                        if (candidate.IsAbstract) continue;
                        if (!(candidate.IsClass || candidate.IsValueType)) continue;
                        if (candidate.IsPrimitive) continue;
                        if (candidate == typeof(string)) continue;
                        if (!candidate.IsSerializable) continue;

                        // Only treat types with at least one Unity-like serializable field as "struct/class".
                        if (GetUnityLikeSerializableFields(candidate).Count == 0)
                            continue;

                        resolved = candidate;
                        s_viewerSerializableTypeByName[typeName] = resolved;
                        return true;
                    }
                }
            }
            catch
            {
                // ignore
            }

            s_viewerSerializableTypeByName[typeName] = null;
            return false;
        }

        private static bool TryResolveViewerSerializableTypeForPath(string path, out Type resolved)
        {
            resolved = null;
            if (string.IsNullOrEmpty(path))
                return false;

            // Walk a dot-separated path: RootKey.FieldA.FieldB
            var parts = path.Split('.');
            if (parts.Length == 0)
                return false;

            if (!TryResolveViewerSerializableTypeByName(parts[0], out var current))
                return false;

            for (int i = 1; i < parts.Length; i++)
            {
                if (current == null)
                    return false;

                var fields = GetUnityLikeSerializableFields(current);
                FieldInfo match = null;
                for (int f = 0; f < fields.Count; f++)
                {
                    var fi = fields[f];
                    if (fi != null && string.Equals(fi.Name, parts[i], StringComparison.Ordinal))
                    {
                        match = fi;
                        break;
                    }
                }

                if (match == null)
                    return false;

                current = match.FieldType;
            }

            resolved = current;
            return resolved != null;
        }

        private static Dictionary<string, Type> TryGetExpectedFieldTypesForPath(string path)
        {
            if (!TryResolveViewerSerializableTypeForPath(path, out var typeAtPath))
                return null;

            if (typeAtPath == null)
                return null;

            // Only map fields for composite types.
            if (typeAtPath.IsPrimitive || typeAtPath.IsEnum || typeAtPath == typeof(string) || typeAtPath == typeof(decimal) || typeAtPath == typeof(DateTime))
                return null;

            if (typeof(UnityEngine.Object).IsAssignableFrom(typeAtPath))
                return null;

            var fields = GetUnityLikeSerializableFields(typeAtPath);
            if (fields.Count == 0)
                return null;

            var map = new Dictionary<string, Type>(StringComparer.Ordinal);
            for (int i = 0; i < fields.Count; i++)
            {
                var fi = fields[i];
                if (fi == null) continue;
                map[fi.Name] = fi.FieldType;
            }

            return map;
        }

        private bool DrawEditableArray(string label, ref Array array, Type elementType, string path)
        {
            if (array == null)
            {
                if (!string.IsNullOrWhiteSpace(label))
                    EditorGUILayout.LabelField(label, "null");
                else
                    EditorGUILayout.LabelField("null");
                return false;
            }

#if ODIN_INSPECTOR
            object boxed = array;
            bool changed = DrawOdinValue(label, ref boxed, path);
            if (changed && boxed is Array newArr)
                array = newArr;
            return changed;
#else
            // Convert to a temp list for UI, then write back to array.
            var temp = new List<object>(array.Length);
            for (int i = 0; i < array.Length; i++) temp.Add(array.GetValue(i));

            bool changed = DrawPagedReorderableCollection(
                label,
                path,
                temp,
                elementType,
                canResize: true,
                canAddRemove: true,
                onResize: newSize =>
                {
                    newSize = Mathf.Max(0, newSize);
                    while (temp.Count < newSize) temp.Add(CreateDefaultValue(elementType));
                    while (temp.Count > newSize) temp.RemoveAt(temp.Count - 1);
                });

            if (changed)
            {
                var newArray = Array.CreateInstance(elementType ?? typeof(object), temp.Count);
                for (int i = 0; i < temp.Count; i++) newArray.SetValue(CoerceToType(temp[i], elementType), i);
                array = newArray;
            }

            return changed;
#endif
        }

        private bool DrawEditableList(string label, System.Collections.IList list, Type elementType, string path)
        {
            if (list == null)
            {
                if (!string.IsNullOrWhiteSpace(label))
                    EditorGUILayout.LabelField(label, "null");
                else
                    EditorGUILayout.LabelField("null");
                return false;
            }

#if ODIN_INSPECTOR
            object boxed = list;
            bool changed = DrawOdinValue(label, ref boxed, path);
            if (changed && boxed is System.Collections.IList newList && !ReferenceEquals(newList, list))
            {
                try
                {
                    list.Clear();
                    for (int i = 0; i < newList.Count; i++)
                        list.Add(newList[i]);
                }
                catch
                {
                    // If it's not mutable, ignore.
                }
            }
            return changed;
#else
            // Copy to a strongly-owned list so we can use paged UI reliably.
            var temp = new List<object>(list.Count);
            for (int i = 0; i < list.Count; i++) temp.Add(list[i]);

            bool changed = DrawPagedReorderableCollection(
                label,
                path,
                temp,
                elementType,
                canResize: true,
                canAddRemove: true,
                onResize: newSize =>
                {
                    newSize = Mathf.Max(0, newSize);
                    while (temp.Count < newSize) temp.Add(CreateDefaultValue(elementType));
                    while (temp.Count > newSize) temp.RemoveAt(temp.Count - 1);
                });

            if (changed)
            {
                // Write back (best effort) by clearing and re-adding.
                // For fixed-size lists, this may throw; we fall back to updating existing indices.
                try
                {
                    list.Clear();
                    for (int i = 0; i < temp.Count; i++)
                        list.Add(CoerceToType(temp[i], elementType));
                }
                catch
                {
                    int n = Math.Min(list.Count, temp.Count);
                    for (int i = 0; i < n; i++)
                        list[i] = CoerceToType(temp[i], elementType);
                }
            }

            return changed;
#endif
        }

#if !ODIN_INSPECTOR
        private static bool TryGetDictionaryKeyValueTypes(System.Collections.IDictionary dict, out Type keyType, out Type valueType)
        {
            keyType = null;
            valueType = null;

            if (dict == null)
                return false;

            var t = dict.GetType();
            if (t.IsGenericType)
            {
                var args = t.GetGenericArguments();
                if (args.Length == 2)
                {
                    keyType = args[0];
                    valueType = args[1];
                    return true;
                }
            }

            foreach (var it in t.GetInterfaces())
            {
                if (!it.IsGenericType)
                    continue;
                if (it.GetGenericTypeDefinition() != typeof(IDictionary<,>))
                    continue;
                var args = it.GetGenericArguments();
                keyType = args[0];
                valueType = args[1];
                return true;
            }

            return false;
        }

        private bool DrawEditableDictionary(string label, System.Collections.IDictionary dict, string path)
        {
            if (dict == null)
            {
                if (!string.IsNullOrWhiteSpace(label))
                    EditorGUILayout.LabelField(label, "null");
                else
                    EditorGUILayout.LabelField("null");
                return false;
            }

            string foldoutKey = $"{path}__dict";
            if (!dictionaryFoldoutStates.ContainsKey(foldoutKey))
                dictionaryFoldoutStates[foldoutKey] = true;

            bool addClicked;
            using (new EditorGUILayout.HorizontalScope())
            {
                dictionaryFoldoutStates[foldoutKey] = EditorGUILayout.Foldout(dictionaryFoldoutStates[foldoutKey], label, true);
                GUILayout.FlexibleSpace();
                GUILayout.Label($"{dict.Count} items", EditorStyles.miniLabel);
                addClicked = GUILayout.Button("+", GUILayout.Width(26));
            }

            if (!dictionaryFoldoutStates[foldoutKey])
                return false;

            TryGetDictionaryKeyValueTypes(dict, out var keyType, out var valueType);

            if (addClicked)
            {
                // Keep fallback simple and robust: only supports adding for string-key dictionaries.
                if (keyType == null || keyType == typeof(string) || keyType == typeof(object))
                {
                    if (!_newDictionaryKeyByPath.TryGetValue(foldoutKey, out var newKey) || string.IsNullOrWhiteSpace(newKey))
                        newKey = "New Key";

                    string unique = newKey;
                    int suffix = 1;
                    while (dict.Contains(unique))
                        unique = $"{newKey} {suffix++}";

                    dict[unique] = CreateDefaultValue(valueType) ?? string.Empty;
                    _newDictionaryKeyByPath[foldoutKey] = newKey;
                }
            }

            var keys = new List<object>(dict.Count);
            foreach (System.Collections.DictionaryEntry entry in dict)
                keys.Add(entry.Key);

            int total = keys.Count;
            int totalPages = Mathf.Max(1, Mathf.CeilToInt(total / (float)CollectionItemsPerPage));
            int page = GetCollectionPage(foldoutKey);
            if (page >= totalPages) page = totalPages - 1;
            if (page < 0) page = 0;
            _collectionPageByPath[foldoutKey] = page;

            if (totalPages > 1)
            {
                if (DrawCompactPagerRight(ref page, totalPages))
                    _collectionPageByPath[foldoutKey] = page;
            }

            page = Mathf.Clamp(GetCollectionPage(foldoutKey), 0, totalPages - 1);
            _collectionPageByPath[foldoutKey] = page;
            int start = page * CollectionItemsPerPage;
            int end = Mathf.Min(start + CollectionItemsPerPage, total);

            bool changed = false;
            object removeKey = null;

            EditorGUI.indentLevel++;
            for (int i = start; i < end; i++)
            {
                object key = keys[i];
                object val = dict[key];

                var rowRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight + 6f);
                rowRect.y += 2f;
                rowRect.height = EditorGUIUtility.singleLineHeight;

                var removeRect = new Rect(rowRect.xMax - 18f, rowRect.y, 18f, rowRect.height);
                var fieldRect = new Rect(rowRect.x, rowRect.y, rowRect.width - 22f, rowRect.height);

                const float keyW = 160f;
                var keyRect = new Rect(fieldRect.x, fieldRect.y, Mathf.Min(keyW, fieldRect.width * 0.45f), fieldRect.height);
                var valueRect = new Rect(keyRect.xMax + 6f, fieldRect.y, Mathf.Max(0f, fieldRect.width - keyRect.width - 6f), fieldRect.height);

                EditorGUI.LabelField(keyRect, key?.ToString() ?? "null", EditorStyles.miniLabel);

                object boxed = val;
                EditorGUI.BeginChangeCheck();
                DrawInlineElementField(valueRect, ref boxed, valueType);
                if (EditorGUI.EndChangeCheck())
                {
                    dict[key] = CoerceToType(boxed, valueType);
                    changed = true;
                }

                if (GUI.Button(removeRect, "x"))
                    removeKey = key;
            }
            EditorGUI.indentLevel--;

            if (removeKey != null)
            {
                dict.Remove(removeKey);
                changed = true;
            }

            return changed;
        }
#endif

        private bool DrawEditableJArray(string label, JArray array, string path)
        {
            if (array == null)
            {
                if (!string.IsNullOrWhiteSpace(label))
                    EditorGUILayout.LabelField(label, "null");
                else
                    EditorGUILayout.LabelField("null");
                return false;
            }

            var temp = new List<object>(array.Count);
            for (int i = 0; i < array.Count; i++) temp.Add(array[i]);

            bool changed = DrawPagedReorderableCollection(
                label,
                path,
                temp,
                elementType: null,
                canResize: true,
                canAddRemove: true,
                onResize: newSize =>
                {
                    newSize = Mathf.Max(0, newSize);
                    while (temp.Count < newSize) temp.Add(JValue.CreateNull());
                    while (temp.Count > newSize) temp.RemoveAt(temp.Count - 1);
                });

            if (changed)
            {
                array.Clear();
                for (int i = 0; i < temp.Count; i++)
                {
                    var token = temp[i] as JToken;
                    array.Add(token ?? JToken.FromObject(temp[i], JsonSerializerUtils.GetSerializer()));
                }
            }

            return changed;
        }

        private bool DrawPagedReorderableCollection(
            string label,
            string path,
            List<object> items,
            Type elementType,
            bool canResize,
            bool canAddRemove,
            Action<int> onResize)
        {
            bool changed = false;

            if (!string.IsNullOrWhiteSpace(label))
                EditorGUILayout.LabelField(label, EditorStyles.boldLabel);

            int total = items?.Count ?? 0;
            int totalPages = Mathf.Max(1, Mathf.CeilToInt(total / (float)CollectionItemsPerPage));
            int page = GetCollectionPage(path);
            if (page >= totalPages) page = totalPages - 1;
            if (page < 0) page = 0;
            _collectionPageByPath[path] = page;

            // For root collections, pagination is displayed in the foldout header row.
            // For nested collections (label not empty), keep pagination above the list.
            if (!string.IsNullOrWhiteSpace(label) && totalPages > 1)
            {
                if (DrawCompactPagerRight(ref page, totalPages))
                    _collectionPageByPath[path] = page;
            }

            total = items?.Count ?? 0;
            totalPages = Mathf.Max(1, Mathf.CeilToInt(total / (float)CollectionItemsPerPage));
            page = Mathf.Clamp(GetCollectionPage(path), 0, totalPages - 1);
            _collectionPageByPath[path] = page;
            int start = page * CollectionItemsPerPage;
            int end = Mathf.Min(start + CollectionItemsPerPage, total);

            int removeGlobalIndex = -1;
            for (int i = start; i < end; i++)
            {
                var rowRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight + 6f);
                rowRect.y += 2f;
                rowRect.height = EditorGUIUtility.singleLineHeight;

                var removeRect = new Rect(rowRect.xMax - 18f, rowRect.y, 18f, rowRect.height);
                var fieldRect = new Rect(rowRect.x, rowRect.y, rowRect.width - 22f, rowRect.height);

                object element = items[i];
                object boxed = element;

                EditorGUI.BeginChangeCheck();
                DrawInlineElementField(fieldRect, ref boxed, elementType);
                if (EditorGUI.EndChangeCheck())
                {
                    items[i] = CoerceToType(boxed, elementType);
                    changed = true;
                }

                if (canAddRemove && GUI.Button(removeRect, "x"))
                    removeGlobalIndex = i;
            }

            if (canAddRemove && removeGlobalIndex >= 0 && removeGlobalIndex < items.Count)
            {
                items.RemoveAt(removeGlobalIndex);
                changed = true;
            }

            // If we removed an item, ensure page is still valid.
            if (changed)
            {
                total = items?.Count ?? 0;
                totalPages = Mathf.Max(1, Mathf.CeilToInt(total / (float)CollectionItemsPerPage));
                if (_collectionPageByPath.TryGetValue(path, out var p) && p >= totalPages)
                    _collectionPageByPath[path] = totalPages - 1;
            }

            return changed;
        }

        private static Vector3 DrawVector3InlineField(string label, Vector3 value)
        {
            var rect = EditorGUILayout.GetControlRect(true);
            rect = EditorGUI.PrefixLabel(rect, new GUIContent(label));
            DrawInlineVector3(rect, ref value);
            return value;
        }

        private static Vector2 DrawVector2InlineField(string label, Vector2 value)
        {
            var rect = EditorGUILayout.GetControlRect(true);
            rect = EditorGUI.PrefixLabel(rect, new GUIContent(label));
            DrawInlineVector2(rect, ref value);
            return value;
        }

        private static Vector3Int DrawVector3IntInlineField(string label, Vector3Int value)
        {
            var rect = EditorGUILayout.GetControlRect(true);
            rect = EditorGUI.PrefixLabel(rect, new GUIContent(label));
            DrawInlineVector3Int(rect, ref value);
            return value;
        }

        private static Vector2Int DrawVector2IntInlineField(string label, Vector2Int value)
        {
            var rect = EditorGUILayout.GetControlRect(true);
            rect = EditorGUI.PrefixLabel(rect, new GUIContent(label));
            DrawInlineVector2Int(rect, ref value);
            return value;
        }

        private int GetCollectionPage(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            return _collectionPageByPath.TryGetValue(path, out var p) ? p : 0;
        }

        private static bool TryGetCollectionCount(object value, out int count)
        {
            count = 0;
            if (value == null) return false;
            if (value is JValue jv) value = jv.Value;
            if (value == null) return false;

            // Don't treat vector-shaped dictionaries as collections (they render as Vector fields).
            if (value is System.Collections.IDictionary dict)
            {
                if (LooksLikeVector3Dictionary(dict) || LooksLikeVector2Dictionary(dict))
                    return false;
            }

            if (value is JArray ja) { count = ja.Count; return true; }
            if (value is System.Collections.ICollection coll) { count = coll.Count; return true; }
            if (value is Array arr) { count = arr.Length; return true; }
            return false;
        }

        private static bool LooksLikeVector2Dictionary(System.Collections.IDictionary dict)
        {
            if (dict == null) return false;
            if (dict.Count != 2) return false;
            return dict.Contains("x") && dict.Contains("y");

        }

        private static bool LooksLikeObjectDictionary(System.Collections.IDictionary dict)
        {
            if (dict == null)
                return false;

            // Must have string keys.
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                if (entry.Key is not string)
                    return false;
            }

            var t = dict.GetType();
            if (t.IsGenericType)
            {
                var args = t.GetGenericArguments();
                if (args.Length == 2)
                {
                    var keyType = args[0];
                    var valueType = args[1];
                    if (keyType != typeof(string) && keyType != typeof(object))
                        return false;
                    if (valueType != typeof(object))
                        return false;
                }
            }

            return true;
        }

        private bool DrawEditableObjectDictionaryAsComposite(string label, System.Collections.IDictionary dict, string path)
        {
            if (dict == null)
            {
                if (!string.IsNullOrEmpty(label))
                    EditorGUILayout.LabelField(label, "null");
                else
                    EditorGUILayout.LabelField("null");
                return false;
            }

            bool showHeader = !string.IsNullOrEmpty(label);
            string foldoutKey = $"{path}__objdictbox";

            if (showHeader)
            {
                if (!dictionaryFoldoutStates.ContainsKey(foldoutKey))
                    dictionaryFoldoutStates[foldoutKey] = true;

                EditorGUILayout.BeginVertical("box");
                dictionaryFoldoutStates[foldoutKey] = EditorGUILayout.Foldout(dictionaryFoldoutStates[foldoutKey], label, true);

                if (!dictionaryFoldoutStates[foldoutKey])
                {
                    EditorGUILayout.EndVertical();
                    return false;
                }

                EditorGUI.indentLevel++;
            }

            bool changed = false;
            var serializer = JsonSerializerUtils.GetSerializer();
            var expectedTypes = TryGetExpectedFieldTypesForPath(path);
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                string key = entry.Key as string;
                if (string.IsNullOrEmpty(key))
                    continue;

                string propLabel = ObjectNames.NicifyVariableName(key);
                string childPath = string.IsNullOrEmpty(path) ? key : path + "." + key;

                object boxed = entry.Value;

                bool entryChanged;
                if (expectedTypes != null && expectedTypes.TryGetValue(key, out var expectedType) && expectedType != null)
                {
                    if (expectedType.IsArray)
                    {
                        if (boxed is JToken tok)
                        {
                            try { boxed = tok.ToObject(expectedType, serializer); }
                            catch { /* ignore */ }
                        }

                        if (boxed is Array arr)
                        {
                            var elementType = expectedType.GetElementType();
                            entryChanged = DrawEditableArray(propLabel, ref arr, elementType, childPath);
                            if (entryChanged) boxed = arr;
                        }
                        else
                        {
                            entryChanged = DrawEditableAny(propLabel, ref boxed, childPath);
                        }
                    }
                    else if (typeof(System.Collections.IList).IsAssignableFrom(expectedType))
                    {
                        if (boxed is JToken tok)
                        {
                            try { boxed = tok.ToObject(expectedType, serializer); }
                            catch { /* ignore */ }
                        }

                        if (boxed is System.Collections.IList list)
                        {
                            Type elementType = null;
                            if (expectedType.IsGenericType && expectedType.GetGenericArguments().Length == 1)
                                elementType = expectedType.GetGenericArguments()[0];
                            entryChanged = DrawEditableList(propLabel, list, elementType, childPath);
                        }
                        else
                        {
                            entryChanged = DrawEditableAny(propLabel, ref boxed, childPath);
                        }
                    }
                    else if (typeof(System.Collections.IDictionary).IsAssignableFrom(expectedType))
                    {
                        if (boxed is JToken tok)
                        {
                            try { boxed = tok.ToObject(expectedType, serializer); }
                            catch { /* ignore */ }
                        }

                        if (boxed is System.Collections.IDictionary subDict)
                        {
#if ODIN_INSPECTOR
                            object boxedDict = subDict;
                            entryChanged = DrawOdinValue(propLabel, ref boxedDict, childPath);
                            if (entryChanged) boxed = boxedDict;
#else
                            entryChanged = DrawEditableDictionary(propLabel, subDict, childPath);
                            if (entryChanged) boxed = subDict;
#endif
                        }
                        else
                        {
                            entryChanged = DrawEditableAny(propLabel, ref boxed, childPath);
                        }
                    }
                    else
                    {
                        entryChanged = DrawEditableAny(propLabel, ref boxed, childPath);
                    }
                }
                else
                {
                    entryChanged = DrawEditableAny(propLabel, ref boxed, childPath);
                }

                if (entryChanged)
                {
                    dict[key] = boxed;
                    changed = true;
                }
            }

            if (showHeader)
            {
                EditorGUI.indentLevel--;
                EditorGUILayout.EndVertical();
            }

            return changed;
        }

        private static bool LooksLikeVector3Dictionary(System.Collections.IDictionary dict)
        {
            if (dict == null) return false;
            if (dict.Count != 3) return false;
            return dict.Contains("x") && dict.Contains("y") && dict.Contains("z");
        }

        private static float GetFloatFromDictionary(System.Collections.IDictionary dict, string key)
        {
            if (dict == null || key == null) return 0f;
            if (!dict.Contains(key)) return 0f;

            object raw = dict[key];
            if (raw is JValue jv)
                raw = jv.Value;

            if (raw == null) return 0f;
            if (raw is float f) return f;
            if (raw is double d) return (float)d;
            if (raw is int i) return i;
            if (raw is long l) return l;

            try
            {
                return Convert.ToSingle(raw);
            }
            catch
            {
                return 0f;
            }
        }

        private static bool TryAddCollectionItem(ref object collection, string path)
        {
            if (collection == null) return false;
            if (collection is JValue jv) collection = jv.Value;
            if (collection == null) return false;

            if (collection is JArray ja)
            {
                ja.Add(JValue.CreateNull());
                return true;
            }

            var t = collection.GetType();
            if (t.IsArray)
            {
                var arr = (Array)collection;
                var elementType = t.GetElementType();
                int oldSize = arr.Length;
                var newArray = Array.CreateInstance(elementType ?? typeof(object), oldSize + 1);
                for (int i = 0; i < oldSize; i++) newArray.SetValue(arr.GetValue(i), i);
                newArray.SetValue(CreateDefaultValue(elementType), oldSize);
                collection = newArray;
                return true;
            }

            if (collection is System.Collections.IList list)
            {
                Type elementType = null;
                if (t.IsGenericType && t.GetGenericArguments().Length == 1)
                    elementType = t.GetGenericArguments()[0];
                list.Add(CreateDefaultValue(elementType));
                return true;
            }

            return false;
        }

        private static void DrawInlineElementField(Rect rect, ref object value, Type elementType)
        {
            // Prefer runtime type when available.
            var t = value?.GetType() ?? elementType;
            if (t == null)
            {
                EditorGUI.LabelField(rect, "null");
                return;
            }

            // Unwrap JValue for editing primitives.
            if (value is JValue jv)
            {
                object raw = jv.Value;
                DrawInlineElementField(rect, ref raw, elementType);
                value = new JValue(raw);
                return;
            }

            // Vector encoded as JObject
            if (value is JObject jobj)
            {
                bool looksV2 = jobj.ContainsKey("x") && jobj.ContainsKey("y") && !jobj.ContainsKey("z");
                bool looksV3 = jobj.ContainsKey("x") && jobj.ContainsKey("y") && jobj.ContainsKey("z") && !jobj.ContainsKey("w");
                if (looksV3)
                {
                    var v = new Vector3(
                        jobj["x"]?.ToObject<float>(JsonSerializerUtils.GetSerializer()) ?? 0f,
                        jobj["y"]?.ToObject<float>(JsonSerializerUtils.GetSerializer()) ?? 0f,
                        jobj["z"]?.ToObject<float>(JsonSerializerUtils.GetSerializer()) ?? 0f);
                    DrawInlineVector3(rect, ref v);
                    value = new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
                    return;
                }
                if (looksV2)
                {
                    var v = new Vector2(
                        jobj["x"]?.ToObject<float>(JsonSerializerUtils.GetSerializer()) ?? 0f,
                        jobj["y"]?.ToObject<float>(JsonSerializerUtils.GetSerializer()) ?? 0f);
                    DrawInlineVector2(rect, ref v);
                    value = new JObject { ["x"] = v.x, ["y"] = v.y };
                    return;
                }

                // Fallback: show summary
                EditorGUI.LabelField(rect, jobj.ToString(Formatting.None));
                return;
            }

            if (t == typeof(string))
            {
#if ODIN_INSPECTOR
                value = SirenixEditorFields.TextField(rect, GUIContent.none, (string)value);
#else
                value = EditorGUI.TextField(rect, (string)value);
#endif
                return;
            }
            if (t == typeof(int))
            {
#if ODIN_INSPECTOR
                value = SirenixEditorFields.IntField(rect, GUIContent.none, (int)value);
#else
                value = EditorGUI.IntField(rect, (int)value);
#endif
                return;
            }
            if (t == typeof(long))
            {
#if ODIN_INSPECTOR
                value = SirenixEditorFields.LongField(rect, GUIContent.none, (long)value);
#else
                value = EditorGUI.LongField(rect, (long)value);
#endif
                return;
            }
            if (t == typeof(float))
            {
#if ODIN_INSPECTOR
                value = SirenixEditorFields.FloatField(rect, GUIContent.none, (float)value);
#else
                value = EditorGUI.FloatField(rect, (float)value);
#endif
                return;
            }
            if (t == typeof(double))
            {
#if ODIN_INSPECTOR
                value = SirenixEditorFields.DoubleField(rect, GUIContent.none, (double)value);
#else
                value = EditorGUI.DoubleField(rect, (double)value);
#endif
                return;
            }
            if (t == typeof(bool))
            {
                value = EditorGUI.Toggle(rect, (bool)value);
                return;
            }

            if (t == typeof(Vector3))
            {
                var v = (Vector3)value;
                DrawInlineVector3(rect, ref v);
                value = v;
                return;
            }
            if (t == typeof(Vector2))
            {
                var v = (Vector2)value;
                DrawInlineVector2(rect, ref v);
                value = v;
                return;
            }
            if (t == typeof(Vector3Int))
            {
                var v = (Vector3Int)value;
                DrawInlineVector3Int(rect, ref v);
                value = v;
                return;
            }
            if (t == typeof(Vector2Int))
            {
                var v = (Vector2Int)value;
                DrawInlineVector2Int(rect, ref v);
                value = v;
                return;
            }

            // Fallback
            EditorGUI.LabelField(rect, value?.ToString() ?? "null");
        }

        private static void DrawInlineVector3(Rect rect, ref Vector3 v)
        {
            const float spacing = 6f;
            float w = (rect.width - spacing * 2f) / 3f;
            var r0 = new Rect(rect.x, rect.y, w, rect.height);
            var r1 = new Rect(rect.x + w + spacing, rect.y, w, rect.height);
            var r2 = new Rect(rect.x + (w + spacing) * 2f, rect.y, w, rect.height);

            v.x = DrawAxisFloat(r0, "X", v.x);
            v.y = DrawAxisFloat(r1, "Y", v.y);
            v.z = DrawAxisFloat(r2, "Z", v.z);
        }

        private static void DrawInlineVector2(Rect rect, ref Vector2 v)
        {
            const float spacing = 6f;
            float w = (rect.width - spacing) / 2f;
            var r0 = new Rect(rect.x, rect.y, w, rect.height);
            var r1 = new Rect(rect.x + w + spacing, rect.y, w, rect.height);

            v.x = DrawAxisFloat(r0, "X", v.x);
            v.y = DrawAxisFloat(r1, "Y", v.y);
        }

        private static void DrawInlineVector3Int(Rect rect, ref Vector3Int v)
        {
            const float spacing = 6f;
            float w = (rect.width - spacing * 2f) / 3f;
            var r0 = new Rect(rect.x, rect.y, w, rect.height);
            var r1 = new Rect(rect.x + w + spacing, rect.y, w, rect.height);
            var r2 = new Rect(rect.x + (w + spacing) * 2f, rect.y, w, rect.height);

            v.x = DrawAxisInt(r0, "X", v.x);
            v.y = DrawAxisInt(r1, "Y", v.y);
            v.z = DrawAxisInt(r2, "Z", v.z);
        }

        private static void DrawInlineVector2Int(Rect rect, ref Vector2Int v)
        {
            const float spacing = 6f;
            float w = (rect.width - spacing) / 2f;
            var r0 = new Rect(rect.x, rect.y, w, rect.height);
            var r1 = new Rect(rect.x + w + spacing, rect.y, w, rect.height);

            v.x = DrawAxisInt(r0, "X", v.x);
            v.y = DrawAxisInt(r1, "Y", v.y);
        }

        private static float DrawAxisFloat(Rect rect, string axis, float value)
        {
            const float axisW = 12f;
            const float gap = 2f;
            var axisRect = new Rect(rect.x, rect.y, axisW, rect.height);
            var fieldRect = new Rect(rect.x + axisW + gap, rect.y, Mathf.Max(0f, rect.width - axisW - gap), rect.height);

            var axisStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleLeft };
            GUI.Label(axisRect, axis, axisStyle);

#if ODIN_INSPECTOR
            return SirenixEditorFields.FloatField(fieldRect, GUIContent.none, value);
#else
            return EditorGUI.FloatField(fieldRect, value);
#endif
        }

        private static int DrawAxisInt(Rect rect, string axis, int value)
        {
            const float axisW = 12f;
            const float gap = 2f;
            var axisRect = new Rect(rect.x, rect.y, axisW, rect.height);
            var fieldRect = new Rect(rect.x + axisW + gap, rect.y, Mathf.Max(0f, rect.width - axisW - gap), rect.height);

            var axisStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleLeft };
            GUI.Label(axisRect, axis, axisStyle);

#if ODIN_INSPECTOR
            return SirenixEditorFields.IntField(fieldRect, GUIContent.none, value);
#else
            return EditorGUI.IntField(fieldRect, value);
#endif
        }

        private bool DrawEditableJObjectAsComposite(string label, JObject obj, string path)
        {
            if (obj == null)
            {
                if (!string.IsNullOrEmpty(label))
                    EditorGUILayout.LabelField(label, "null");
                else
                    EditorGUILayout.LabelField("null");
                return false;
            }

            bool showHeader = !string.IsNullOrEmpty(label);
            string foldoutKey = $"{path}__jobjbox";

            if (showHeader)
            {
                if (!dictionaryFoldoutStates.ContainsKey(foldoutKey))
                    dictionaryFoldoutStates[foldoutKey] = true;

                EditorGUILayout.BeginVertical("box");
                dictionaryFoldoutStates[foldoutKey] = EditorGUILayout.Foldout(dictionaryFoldoutStates[foldoutKey], label, true);

                if (!dictionaryFoldoutStates[foldoutKey])
                {
                    EditorGUILayout.EndVertical();
                    return false;
                }

                EditorGUI.indentLevel++;
            }

            bool changed = false;
            var serializer = JsonSerializerUtils.GetSerializer();
            var expectedTypes = TryGetExpectedFieldTypesForPath(path);
            foreach (var prop in obj.Properties())
            {
                string propLabel = ObjectNames.NicifyVariableName(prop.Name);
                string childPath = string.IsNullOrEmpty(path) ? prop.Name : path + "." + prop.Name;

                object boxed = prop.Value;
                bool propChanged;

                if (expectedTypes != null && expectedTypes.TryGetValue(prop.Name, out var expectedType) && expectedType != null)
                {
                    if (expectedType.IsArray)
                    {
                        if (prop.Value is JToken tok)
                        {
                            try { boxed = tok.ToObject(expectedType, serializer); }
                            catch { boxed = prop.Value; }
                        }

                        if (boxed is Array arr)
                        {
                            var elementType = expectedType.GetElementType();
                            propChanged = DrawEditableArray(propLabel, ref arr, elementType, childPath);
                            if (propChanged) boxed = arr;
                        }
                        else
                        {
                            propChanged = DrawEditableAny(propLabel, ref boxed, childPath);
                        }
                    }
                    else if (typeof(System.Collections.IList).IsAssignableFrom(expectedType))
                    {
                        if (prop.Value is JToken tok)
                        {
                            try { boxed = tok.ToObject(expectedType, serializer); }
                            catch { boxed = prop.Value; }
                        }

                        if (boxed is System.Collections.IList list)
                        {
                            Type elementType = null;
                            if (expectedType.IsGenericType && expectedType.GetGenericArguments().Length == 1)
                                elementType = expectedType.GetGenericArguments()[0];
                            propChanged = DrawEditableList(propLabel, list, elementType, childPath);
                        }
                        else
                        {
                            propChanged = DrawEditableAny(propLabel, ref boxed, childPath);
                        }
                    }
                    else if (typeof(System.Collections.IDictionary).IsAssignableFrom(expectedType))
                    {
                        if (prop.Value is JToken tok)
                        {
                            try { boxed = tok.ToObject(expectedType, serializer); }
                            catch { boxed = prop.Value; }
                        }

                        if (boxed is System.Collections.IDictionary dict)
                        {
#if ODIN_INSPECTOR
                            object boxedDict = dict;
                            propChanged = DrawOdinValue(propLabel, ref boxedDict, childPath);
                            if (propChanged) boxed = boxedDict;
#else
                            propChanged = DrawEditableDictionary(propLabel, dict, childPath);
                            if (propChanged) boxed = dict;
#endif
                        }
                        else
                        {
                            propChanged = DrawEditableAny(propLabel, ref boxed, childPath);
                        }
                    }
                    else
                    {
                        propChanged = DrawEditableAny(propLabel, ref boxed, childPath);
                    }
                }
                else
                {
                    propChanged = DrawEditableAny(propLabel, ref boxed, childPath);
                }

                if (propChanged)
                {
                    prop.Value = boxed as JToken ?? JToken.FromObject(boxed, serializer);
                    changed = true;
                }
            }

            if (showHeader)
            {
                EditorGUI.indentLevel--;
                EditorGUILayout.EndVertical();
            }

            return changed;
        }

        private static object CreateDefaultValue(Type type)
        {
            if (type == null) return null;
            if (type == typeof(string)) return string.Empty;
            if (type.IsValueType)
            {
                try { return Activator.CreateInstance(type); }
                catch { return null; }
            }
            return null;
        }

        private static object CoerceToType(object value, Type targetType)
        {
            if (targetType == null) return value;
            if (value == null) return null;
            if (targetType.IsInstanceOfType(value)) return value;

            try
            {
                if (value is JValue jv) value = jv.Value;
                if (value == null) return null;
                // Handle numeric conversions (common when data comes from JSON)
                if (targetType == typeof(int)) return Convert.ToInt32(value);
                if (targetType == typeof(long)) return Convert.ToInt64(value);
                if (targetType == typeof(float)) return Convert.ToSingle(value);
                if (targetType == typeof(double)) return Convert.ToDouble(value);
                if (targetType == typeof(bool)) return Convert.ToBoolean(value);
                if (targetType == typeof(string)) return Convert.ToString(value);
            }
            catch
            {
                // ignore
            }
            return value;
        }

        private string GetReadableTypeName(object obj)
        {
            if (obj == null) return "Null";

            System.Type type = obj.GetType();

            // Handle common Unity types
            if (type == typeof(Vector2)) return "Vector2";
            if (type == typeof(Vector3)) return "Vector3";
            if (type == typeof(Vector4)) return "Vector4";
            if (type == typeof(Quaternion)) return "Quaternion";
            if (type == typeof(Color)) return "Color";
            if (type == typeof(Color32)) return "Color32";

            // Handle primitive types
            if (type == typeof(int)) return "Integer";
            if (type == typeof(float)) return "Float";
            if (type == typeof(double)) return "Double";
            if (type == typeof(bool)) return "Boolean";
            if (type == typeof(string)) return "String";
            if (type == typeof(System.DateTime)) return "DateTime";

            // Handle collections
            if (type.IsArray) return $"{GetElementTypeName(type.GetElementType())} Array";
            if (type.IsGenericType)
            {
                var genericDef = type.GetGenericTypeDefinition();
                if (genericDef == typeof(List<>))
                    return $"{GetElementTypeName(type.GetGenericArguments()[0])} List";
                if (genericDef == typeof(Dictionary<,>))
                {
                    var args = type.GetGenericArguments();
                    return $"Dictionary<{GetElementTypeName(args[0])}, {GetElementTypeName(args[1])}>";
                }
            }

            // Check if it's a Newtonsoft.Json.Linq object (from deserialized JSON)
            if (type.Namespace == "Newtonsoft.Json.Linq")
            {
                if (type.Name == "JObject") return "Object";
                if (type.Name == "JArray") return "Array";
                if (type.Name == "JValue") return "Value";
            }

            // For custom classes, use the class name
            return ObjectNames.NicifyVariableName(type.Name);
        }

        private string GetElementTypeName(System.Type elementType)
        {
            if (elementType == null) return "Unknown";
            if (elementType == typeof(int)) return "int";
            if (elementType == typeof(float)) return "float";
            if (elementType == typeof(string)) return "string";
            if (elementType == typeof(bool)) return "bool";
            return ObjectNames.NicifyVariableName(elementType.Name);
        }

        private string GetReadableValueDisplay(object obj)
        {
            if (obj == null) return "null";

            System.Type type = obj.GetType();

            // Handle Unity types with special formatting
            if (type == typeof(Vector2))
            {
                var v = (Vector2)obj;
                return $"({v.x:F3}, {v.y:F3})";
            }
            if (type == typeof(Vector3))
            {
                var v = (Vector3)obj;
                return $"({v.x:F3}, {v.y:F3}, {v.z:F3})";
            }
            if (type == typeof(Vector4))
            {
                var v = (Vector4)obj;
                return $"({v.x:F3}, {v.y:F3}, {v.z:F3}, {v.w:F3})";
            }
            if (type == typeof(Quaternion))
            {
                var q = (Quaternion)obj;
                return $"({q.x:F3}, {q.y:F3}, {q.z:F3}, {q.w:F3})";
            }
            if (type == typeof(Color))
            {
                var c = (Color)obj;
                return $"RGBA({c.r:F3}, {c.g:F3}, {c.b:F3}, {c.a:F3})";
            }

            // Handle collections with count info
            if (obj is System.Collections.ICollection collection)
                return $"[{collection.Count} items]";

            // For everything else, use ToString but limit length
            string str = obj.ToString();
            return str.Length > 100 ? str.Substring(0, 97) + "..." : str;
        }

        private bool IsVector2(object value)
        {
            if (value is Vector2) return true;
            if (value is Newtonsoft.Json.Linq.JObject jobj)
                return jobj.ContainsKey("x") && jobj.ContainsKey("y") && !jobj.ContainsKey("z");
            return false;
        }

        private Vector2 ParseVector2(object value)
        {
            if (value is Vector2 vector2) return vector2;
            if (value is Newtonsoft.Json.Linq.JObject jobj)
            {
                float x = jobj["x"]?.ToObject<float>(JsonSerializerUtils.GetSerializer()) ?? 0f;
                float y = jobj["y"]?.ToObject<float>(JsonSerializerUtils.GetSerializer()) ?? 0f;
                return new Vector2(x, y);
            }
            return Vector2.zero;
        }

        private bool IsVector3(object value)
        {
            if (value is Vector3) return true;
            if (value is Newtonsoft.Json.Linq.JObject jobj)
                return jobj.ContainsKey("x") && jobj.ContainsKey("y") && jobj.ContainsKey("z") && !jobj.ContainsKey("w");
            return false;
        }

        private Vector3 ParseVector3(object value)
        {
            if (value is Vector3 vector3) return vector3;
            if (value is Newtonsoft.Json.Linq.JObject jobj)
            {
                float x = jobj["x"]?.ToObject<float>(JsonSerializerUtils.GetSerializer()) ?? 0f;
                float y = jobj["y"]?.ToObject<float>(JsonSerializerUtils.GetSerializer()) ?? 0f;
                float z = jobj["z"]?.ToObject<float>(JsonSerializerUtils.GetSerializer()) ?? 0f;
                return new Vector3(x, y, z);
            }
            return Vector3.zero;
        }

        private bool IsDictionary(object value)
        {
            return value is System.Collections.IDictionary ||
                   value is Newtonsoft.Json.Linq.JObject;
        }

        private bool IsArray(object value)
        {
            if (value is System.Array) return true;
            if (value is System.Collections.IList) return true;
            if (value is Newtonsoft.Json.Linq.JArray) return true;
            return false;
        }

        private bool IsEmptyDictionary(object value)
        {
            try
            {
                if (value is Newtonsoft.Json.Linq.JObject jobj)
                {
                    return jobj.Count == 0;
                }
                else if (value is System.Collections.IDictionary dict)
                {
                    return dict.Count == 0;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private void DisplayDictionary(object value)
        {
            EditorGUI.indentLevel++;
            try
            {
                if (value is Newtonsoft.Json.Linq.JObject jobj)
                {
                    foreach (var kvp in jobj)
                    {
                        EditorGUILayout.LabelField(kvp.Key + ":", kvp.Value?.ToString() ?? "null");
                    }
                }
                else if (value is System.Collections.IDictionary dict)
                {
                    foreach (System.Collections.DictionaryEntry entry in dict)
                    {
                        EditorGUILayout.LabelField((entry.Key?.ToString() ?? "null") + ":", entry.Value?.ToString() ?? "null");
                    }
                }
            }
            catch (System.Exception e)
            {
                EditorGUILayout.LabelField("Error displaying dictionary:", e.Message);
            }
            EditorGUI.indentLevel--;
        }

        private void DisplayArray(object value)
        {
            EditorGUI.indentLevel++;
            try
            {
                if (value is Newtonsoft.Json.Linq.JArray jarray)
                {
                    for (int i = 0; i < jarray.Count; i++)
                    {
                        EditorGUILayout.LabelField($"Element {i}", jarray[i]?.ToString() ?? "null");
                    }
                }
                else if (value is System.Collections.IList list)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        EditorGUILayout.LabelField($"Element {i}", list[i]?.ToString() ?? "null");
                    }
                }
                else if (value is System.Array array)
                {
                    for (int i = 0; i < array.Length; i++)
                    {
                        EditorGUILayout.LabelField($"Element {i}", array.GetValue(i)?.ToString() ?? "null");
                    }
                }
            }
            catch (System.Exception e)
            {
                EditorGUILayout.LabelField("Error displaying array: " + e.Message);
            }
            EditorGUI.indentLevel--;
        }

        public void Refresh()
        {
            if (!Application.isPlaying)
                return;

            // Refresh is unconditional: always reload from storage and discard staged edits.
            _dirtyRootKeys.Clear();

            currentSaveData = GetDirectStorageData();
            UpdateRawJsonData();

            // Save systems can warm an editor cache shortly after entering play mode.
            // If we refresh too early, we can briefly see empty data.
            // Do a small bounded retry so the UI shows "Loading..." instead of flashing empty.
            if (currentSaveData.Count == 0)
            {
                if (!_warmupDataRefreshActive)
                {
                    _warmupDataRefreshActive = true;
                    _warmupDataRefreshTriesRemaining = 2;
                }

                if (_warmupDataRefreshTriesRemaining > 0)
                {
                    _warmupDataRefreshTriesRemaining--;
                    EditorApplication.delayCall += () =>
                    {
                        if (this == null) return;
                        if (!Application.isPlaying) return;
                        Refresh();
                    };
                }
                else
                {
                    _warmupDataRefreshActive = false;
                    _warmupDataRefreshTriesRemaining = 0;
                }
            }
            else
            {
                _warmupDataRefreshActive = false;
                _warmupDataRefreshTriesRemaining = 0;
            }

            stagedSaveData = new Dictionary<string, object>(currentSaveData);

            DataViewCaptureRootKeyOrderFromCurrentSaveData();

            PruneUiStateCachesForCurrentRoots();

#if ODIN_INSPECTOR
            DisposeOdinCaches();
#endif
            Repaint();
        }

        private void PruneUiStateCachesForCurrentRoots()
        {
            if (stagedSaveData == null || stagedSaveData.Count == 0)
            {
                foldoutStates.Clear();
                dictionaryFoldoutStates.Clear();
                _collectionPageByPath.Clear();
                _newDictionaryKeyByPath.Clear();
                return;
            }

            var roots = new HashSet<string>(stagedSaveData.Keys, StringComparer.Ordinal);

            PruneByRoot(foldoutStates, roots);
            PruneByRoot(dictionaryFoldoutStates, roots);
            PruneByRoot(_collectionPageByPath, roots);
            PruneByRoot(_newDictionaryKeyByPath, roots);
        }

        private void DataViewCaptureRootKeyOrderFromCurrentSaveData()
        {
            _dataViewRootKeyRawOrder.Clear();

            // currentSaveData is the closest thing we have to "raw" order
            // without re-reading/rehydrating from disk.
            foreach (var kvp in currentSaveData)
                _dataViewRootKeyRawOrder.Add(kvp.Key);

            _dataViewRootKeyOrderDirty = true;
        }

        private IReadOnlyList<string> GetDataViewRootKeysForDisplay()
        {
            if (!_dataViewRootKeyOrderDirty)
                return _dataViewRootKeyDisplayOrder;

            _dataViewRootKeyDisplayOrder.Clear();

            // Find the actual key casing for LastSaveTime, if present.
            string lastSaveTimeKey = null;
            foreach (var key in stagedSaveData.Keys)
            {
                if (string.Equals(key, "LastSaveTime", StringComparison.OrdinalIgnoreCase))
                {
                    lastSaveTimeKey = key;
                    break;
                }
            }

            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(lastSaveTimeKey))
            {
                _dataViewRootKeyDisplayOrder.Add(lastSaveTimeKey);
                added.Add(lastSaveTimeKey);
            }

            // First, follow the captured raw order.
            for (int i = 0; i < _dataViewRootKeyRawOrder.Count; i++)
            {
                string key = _dataViewRootKeyRawOrder[i];
                if (added.Contains(key))
                    continue;

                // Only include if it still exists in stagedSaveData.
                if (stagedSaveData.ContainsKey(key))
                {
                    _dataViewRootKeyDisplayOrder.Add(key);
                    added.Add(key);
                }
                else
                {
                    // Fallback for casing changes: try to match by ignore-case.
                    foreach (var stagedKey in stagedSaveData.Keys)
                    {
                        if (added.Contains(stagedKey))
                            continue;

                        if (string.Equals(stagedKey, key, StringComparison.OrdinalIgnoreCase))
                        {
                            _dataViewRootKeyDisplayOrder.Add(stagedKey);
                            added.Add(stagedKey);
                            break;
                        }
                    }
                }
            }

            // Finally, append any new roots that weren't in the captured order.
            // This also preserves the runtime insertion order for newly created keys.
            foreach (var key in stagedSaveData.Keys)
            {
                if (added.Contains(key))
                    continue;

                _dataViewRootKeyDisplayOrder.Add(key);
                added.Add(key);
            }

            _dataViewRootKeyOrderDirty = false;
            return _dataViewRootKeyDisplayOrder;
        }

        private static void PruneByRoot<T>(Dictionary<string, T> dict, HashSet<string> roots)
        {
            if (dict == null || dict.Count == 0)
                return;

            var toRemove = new List<string>();
            foreach (var key in dict.Keys)
            {
                string root = GetRootKeyFromPathKey(key);
                if (!roots.Contains(root))
                    toRemove.Add(key);
            }

            for (int i = 0; i < toRemove.Count; i++)
                dict.Remove(toRemove[i]);
        }

        private static string GetRootKeyFromPathKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                return string.Empty;

            int dot = key.IndexOf('.');
            int sep = key.IndexOf("__", StringComparison.Ordinal);

            int cut;
            if (dot < 0 && sep < 0)
                cut = -1;
            else if (dot < 0)
                cut = sep;
            else if (sep < 0)
                cut = dot;
            else
                cut = Mathf.Min(dot, sep);

            return cut <= 0 ? key : key.Substring(0, cut);
        }

        private void RefreshJsonView()
        {
            if (!Application.isPlaying)
                return;

            LoadDataDirectlyFromStorage();

            // Mirror the same warm-up behavior for the JSON tab.
            if (rawJsonData == "{}" || string.IsNullOrEmpty(rawJsonData))
            {
                if (!_warmupJsonRefreshActive)
                {
                    _warmupJsonRefreshActive = true;
                    _warmupJsonRefreshTriesRemaining = 2;
                }

                if (_warmupJsonRefreshTriesRemaining > 0)
                {
                    _warmupJsonRefreshTriesRemaining--;
                    EditorApplication.delayCall += () =>
                    {
                        if (this == null) return;
                        if (!Application.isPlaying) return;
                        RefreshJsonView();
                    };
                }
                else
                {
                    _warmupJsonRefreshActive = false;
                }
            }
            else
            {
                _warmupJsonRefreshActive = false;
                _warmupJsonRefreshTriesRemaining = 0;
            }

            Repaint();
        }

        private void LoadDataDirectlyFromStorage()
        {
            try
            {
                var directData = GetDirectStorageData();
                if (directData.Count > 0)
                {
                    var settings = JsonSerializerUtils.GetSettings();
                    rawJsonData = JsonConvert.SerializeObject(directData, settings);
                }
                else
                {
                    rawJsonData = "{}";
                }
            }
            catch (System.Exception e)
            {
                rawJsonData = $"Error loading data directly from storage:\n{e.Message}";
            }
        }

        private Dictionary<string, object> GetDirectStorageData()
        {
            // In play mode, SerializationService warms and maintains an editor cache.
            // Use it for listing/inspection (especially for PlayerPrefs where keys can't be enumerated).
            return SerializationService.GetAllSaveDataCopy();
        }

        private void UpdateRawJsonData()
        {
            try
            {
                var settings = JsonSerializerUtils.GetSettings();
                rawJsonData = JsonConvert.SerializeObject(currentSaveData, settings);
            }
            catch (System.Exception e)
            {
                rawJsonData = $"Error serializing save data to JSON:\n{e.Message}";
            }
        }

        public void DeleteAll()
        {
            if (!Application.isPlaying)
                return;

            const string title = "Delete All";
            const string message = "Delete all save data and restart the game?";
            const string ok = "Delete & Restart";
            const string cancel = "Cancel";

            if (EditorUtility.DisplayDialog(title, message, ok, cancel))
            {
                // Delete all keys tracked by the library (from SerializationService editor cache).
                var keys = new List<string>(SerializationService.GetAllSaveData().Keys);
                foreach (var key in keys)
                {
                    SerializationService.DeleteData(key);
                }

                RestartGame();
            }
        }

        private void RestartGame()
        {
            // Store flag in a project asset to survive domain reload (no machine-wide EditorPrefs).
            try
            {
                var state = SerializerProjectEditorState.GetOrCreate();
                state.DataViewerShouldEnterPlayMode = true;
                state.SaveIfDirty();
            }
            catch
            {
                // Best-effort only.
            }

            // Use EditorApplication.delayCall to ensure the current frame completes before restarting
            EditorApplication.delayCall += () =>
            {
                // First, exit play mode
                EditorApplication.isPlaying = false;

                // Wait for play mode to fully exit, then reload domain
                EditorApplication.delayCall += () =>
                {
                    // Force domain reload
                    EditorUtility.RequestScriptReload();
                };
            };
        }

        // Static constructor to handle post-domain-reload logic
        static DataSerializationViewer()
        {
            // Check if we should enter play mode after domain reload
            EditorApplication.delayCall += CheckAndEnterPlayMode;
        }

        private static void CheckAndEnterPlayMode()
        {
            bool shouldEnter = false;
            try
            {
                var state = SerializerProjectEditorState.GetOrCreate();
                shouldEnter = state.DataViewerShouldEnterPlayMode;
                if (shouldEnter)
                {
                    state.DataViewerShouldEnterPlayMode = false;
                    state.SaveIfDirty();
                }
            }
            catch
            {
                shouldEnter = false;
            }

            if (shouldEnter)
            {

                // Wait a bit more to ensure everything is fully loaded
                EditorApplication.delayCall += () =>
                {
                    EditorApplication.isPlaying = true;
                };
            }
        }
    }
}

#endif