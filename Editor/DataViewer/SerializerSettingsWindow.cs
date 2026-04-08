#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace NekoSerializer
{
    public class SerializerSettingsWindow : EditorWindow
    {
        private SerializerSettings _settings;
        private SerializedObject _serializedSettings;
        private Vector2 _scrollPosition;

        private SerializedProperty _storageOptionProp;
        private SerializedProperty _saveDirectoryProp;
        private SerializedProperty _useEncryptionProp;
        private SerializedProperty _encryptionKeyProp;
        private SerializedProperty _prettyPrintJsonProp;

        [MenuItem("Tools/Neko Serializer/Settings")]
        public static void ShowWindow()
        {
            var window = GetWindow<SerializerSettingsWindow>("Serializer Settings");
            window.minSize = new Vector2(400, 500);
            window.Show();
        }

        private void OnEnable()
        {
            LoadSettings();
        }

        private void LoadSettings()
        {
            _settings = Resources.Load<SerializerSettings>("SerializerSettings");

            if (_settings != null)
            {
                _serializedSettings = new SerializedObject(_settings);

                _storageOptionProp = _serializedSettings.FindProperty("<StorageOption>k__BackingField");
                _saveDirectoryProp = _serializedSettings.FindProperty("<SaveDirectory>k__BackingField");
                _useEncryptionProp = _serializedSettings.FindProperty("<UseEncryption>k__BackingField");
                _encryptionKeyProp = _serializedSettings.FindProperty("<EncryptionKey>k__BackingField");
                _prettyPrintJsonProp = _serializedSettings.FindProperty("<PrettyPrintJson>k__BackingField");
            }
        }

        private void CreateDefaultSettings()
        {
            _settings = CreateInstance<SerializerSettings>();

            // Create the directory structure for library use
            string pluginPath = "Assets/Plugins";
            string NekoSerializerPath = "Assets/Plugins/NekoSerializer";
            string resourcesPath = "Assets/Plugins/NekoSerializer/Resources";

            if (!AssetDatabase.IsValidFolder(pluginPath))
                AssetDatabase.CreateFolder("Assets", "Plugins");

            if (!AssetDatabase.IsValidFolder(NekoSerializerPath))
                AssetDatabase.CreateFolder("Assets/Plugins", "NekoSerializer");

            if (!AssetDatabase.IsValidFolder(resourcesPath))
                AssetDatabase.CreateFolder("Assets/Plugins/NekoSerializer", "Resources");

            // Save the settings asset
            string assetPath = "Assets/Plugins/NekoSerializer/Resources/SerializerSettings.asset";
            AssetDatabase.CreateAsset(_settings, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[SaveLoadSettings] Created default settings at: {assetPath}");
        }

        private void ReloadScene()
        {
            if (Application.isPlaying)
            {
                // Removed runtime controls from this window.
            }
        }

        private void OnGUI()
        {
            if (_settings == null || _serializedSettings == null)
            {
                EditorGUILayout.HelpBox("SerializerSettings not found. Create settings for this project.", MessageType.Warning);
                EditorGUILayout.Space();

                if (GUILayout.Button("Create New Settings", GUILayout.Height(30)))
                {
                    CreateDefaultSettings();
                    LoadSettings();
                }
                return;
            }

            var scrollStarted = false;
            try
            {
                _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
                scrollStarted = true;

                EditorGUILayout.Space(5);

                // Settings UI
                _serializedSettings.Update();

                DrawSettingsSection();

                if (_serializedSettings.ApplyModifiedProperties())
                {
                    EditorUtility.SetDirty(_settings);
                    AssetDatabase.SaveAssets();

                    // No runtime service refresh needed (direct-to-storage model)
                }

                // Intentionally no utility/runtime buttons in this window.
            }
            finally
            {
                if (scrollStarted)
                    EditorGUILayout.EndScrollView();
            }
        }

        private void DrawSettingsSection()
        {
            // EditorGUILayout.LabelField("Serializer Settings", EditorStyles.boldLabel);

            if (_storageOptionProp == null || _saveDirectoryProp == null || _useEncryptionProp == null || _encryptionKeyProp == null || _prettyPrintJsonProp == null)
            {
                EditorGUILayout.HelpBox(
                    "SerializerSettings fields could not be found. This window is out of sync with SerializerSettings.\n" +
                    "Try reimporting scripts or regenerate the SerializerSettings asset.",
                    MessageType.Error);
                return;
            }

            // Save Location
            EditorGUILayout.PropertyField(_storageOptionProp, new GUIContent("Storage Option"));

            var saveLocation = (StorageOption)_storageOptionProp.enumValueIndex;

            if (saveLocation == StorageOption.JsonFile)
            {
                EditorGUILayout.PropertyField(_saveDirectoryProp, new GUIContent("Save Directory"));
            }

            // EditorGUILayout.Space();
            // EditorGUILayout.LabelField("Security", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_useEncryptionProp, new GUIContent("Use Encryption"));

            if (_useEncryptionProp.boolValue)
            {
                EditorGUILayout.PropertyField(_encryptionKeyProp, new GUIContent("Encryption Key"));
                EditorGUILayout.HelpBox("Keep your encryption key secure! Consider using environment variables in production.", MessageType.Info);
            }

            // EditorGUILayout.Space();
            // EditorGUILayout.LabelField("Formatting", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_prettyPrintJsonProp, new GUIContent("Pretty Print JSON"));
        }

        // Utility/runtime controls intentionally removed.
    }
}
#endif