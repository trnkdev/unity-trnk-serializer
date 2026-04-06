# Neko Serializer

A save/load serialization system for Unity, backed by [Newtonsoft.Json](https://www.newtonsoft.com/json). Supports `PlayerPrefs` and JSON-file storage, encryption, async operations, and a fluent data-migration API.

## Installation

1. Install NekoLib via Unity Package Manager:

```
https://github.com/boobosua/unity-nekolib.git
```

2. Then add NekoSerializer (Newtonsoft.Json must be available — it ships with Unity 2021+):

```
https://github.com/boobosua/unity-neko-serializer.git
```

## Configuration

Create the settings asset once: **Assets → Create → Neko Framework → Serialize → Serializer Settings**, name it `SerializerSettings`, and place it inside a `Resources` folder. If the asset is missing, defaults are used automatically.

| Property | Default | Description |
|---|---|---|
| `StorageOption` | `PlayerPrefs` | `PlayerPrefs` or `JsonFile` |
| `SaveDirectory` | `"SaveData"` | Folder name for JSON files (relative to `Application.persistentDataPath`) |
| `UseEncryption` | `false` | Encrypt serialized strings before writing |
| `EncryptionKey` | `"DefaultEncryptionKey"` | Key used when encryption is enabled |
| `PrettyPrintJson` | `true` | Indented vs compact JSON output |

## Save / Load Usage

All public API is on the static `NSR` class in the `NekoSerializer` namespace.

```csharp
using NekoSerializer;
```

### Save & load

```csharp
// Save — immediately writes to the configured storage backend
NSR.Save("playerName", "Neko");
NSR.Save("score", 9001);
NSR.Save("position", transform.position); // Unity types are handled automatically

// Load — returns defaultValue when the key does not exist
string name = NSR.Load<string>("playerName", "Unknown");
int score = NSR.Load<int>("score", 0);
Vector3 pos = NSR.Load<Vector3>("position");
```

### Async variants

```csharp
await NSR.SaveAsync("highScore", 9999);
int hs = await NSR.LoadAsync<int>("highScore", 0);
```

### Key checks & deletion

```csharp
if (NSR.Exists("highScore"))
    NSR.Delete("highScore");
```

### Last save time

```csharp
DateTime utc   = NSR.LastSaveTimeUtc;
DateTime local = NSR.LastSaveTimeLocal;
```

### Direct JSON serialization

Use these when you need raw JSON strings (e.g. for networking or clipboard):

```csharp
string json = NSR.Serialize(myData);
MyData data = NSR.Deserialize<MyData>(json);
```

## Data Migration (Pack / Unpack)

`Pack` bundles multiple saved keys into a single portable string; `Unpack` restores them. Useful for cloud sync or profile transfer.

```csharp
// Bundle several keys into one string
string snapshot = NSR.Pack("playerName", "score", "position");

// Restore — existing keys are overwritten by default
NSR.Unpack(snapshot);

// Preserve existing values, only write missing keys
NSR.Unpack(snapshot, overwriteExisting: false);
```

## Supported Unity Types

The following Unity value types are serialized / deserialized automatically:

`Vector2` · `Vector2Int` · `Vector3` · `Vector3Int` · `Vector4` · `Quaternion` · `Color` · `Rect` · `Bounds` · `Transform` (position / rotation / scale snapshot)

## API Reference

```csharp
using NekoSerializer;

NSR.Save<T>(string key, T data)
NSR.SaveAsync<T>(string key, T data)               // Task

NSR.Load<T>(string key, T defaultValue = default)
NSR.LoadAsync<T>(string key, T defaultValue = default) // Task<T>

NSR.Exists(string key)                             // bool
NSR.Delete(string key)

NSR.Pack(params string[] keys)                     // string
NSR.Unpack(string packedData, bool overwriteExisting = true)

NSR.Serialize(object obj)                          // string (JSON)
NSR.Deserialize<T>(string json)                    // T

NSR.LastSaveTimeUtc   // DateTime
NSR.LastSaveTimeLocal // DateTime
```

## Requirements

- Unity 2021+ (Newtonsoft.Json shipped by default)
- NekoLib
