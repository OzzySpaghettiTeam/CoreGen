using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace CoreGen;

/// <summary>An object in one of the game's serialized files.</summary>
public readonly record struct ObjId(string File, long PathId)
{
    public override string ToString() => $"{File}:{PathId}";
}

/// <summary>A vanilla MonoScript: which class a MonoBehaviour's data belongs to, and its script execution order.</summary>
public sealed record ScriptInfo(string Assembly, string Namespace, string Class, int ExecutionOrder)
{
    public string FullName => Namespace.Length > 0 ? Namespace + "." + Class : Class;

    public bool IsGame => Assembly == "Assembly-CSharp";
}

/// <summary>One serialized object: what it is and where it sits in its hierarchy.</summary>
public sealed class GameObj
{
    public ObjId Id;
    public GameFile File = null!;
    public AssetFileInfo Info = null!;
    public long Offset;
    public int ClassId;

    /// <summary>For MonoBehaviours: the script whose data this is.</summary>
    public ScriptInfo? Script;
    public string Name = "";

    /// <summary>Audio mixer groups only: their AudioMixer, which tells apart groups that share a name ("Master").</summary>
    public ObjId? Mixer;

    /// <summary>Sprites only: m_Rect, which tells apart sprites that share a name.</summary>
    public (float x, float y, float w, float h)? SpriteRect;

    /// <summary>For components: their GameObject. For GameObjects: themselves.</summary>
    public ObjId? GameObject;
    public bool IsComponent;

    // GameObjects only
    public List<ObjId> Components = new();

    // Transforms only
    public ObjId? Father;
    public List<ObjId> Children = new();

    /// <summary>Assembly-CSharp scripts only: the data, read with the vanilla layout.</summary>
    public AssetTypeValueField? Data;

    public override string ToString() =>
        $"{Id} {(Script != null ? Script.FullName : "#" + ClassId)} '{Name}'";
}

/// <summary>A serialized file (scene, shared assets, resources).</summary>
public sealed class GameFile
{
    public string Name = "";

    /// <summary>Normalized names of the files PPtr file ids 1.. point to.</summary>
    public List<string> Externals = new();
    public readonly Dictionary<long, GameObj> Objects = new();

    /// <summary>Transforms without a father, in file order.</summary>
    public List<ObjId> Roots = new();

    /// <summary>The scene build index for levelN files, else -1.</summary>
    public int BuildIndex =>
        Name.StartsWith("level") && int.TryParse(Name.Substring(5), out int i) ? i : -1;

    public bool IsScene => BuildIndex >= 0;

    /// <summary>The file a PPtr's file id points to (0 = this file).</summary>
    public string? ResolveFile(int fileId) =>
        fileId == 0 ? Name
        : fileId - 1 < Externals.Count ? Externals[fileId - 1]
        : null;
}

/// <summary>
/// The game's data folder read through AssetsTools.NET. Only the few engine types needed for hierarchy, names and
/// resource paths are parsed (from their fixed 2022.3 layouts); MonoBehaviour data goes through <see cref="ScriptLayouts"/>.
/// The files stay open only while <see cref="GameData"/> reads them.
/// </summary>
public sealed class GameFiles : IDisposable
{
    public const int ClassAudioMixerGroup = 243,
        ClassSprite = 213,
        ClassGameObject = 1,
        ClassTransform = 4,
        ClassMonoBehaviour = 114,
        ClassMonoScript = 115,
        ClassResourceManager = 147,
        ClassRectTransform = 224;

    /// <summary>The engine layouts parsed by hand in <see cref="Index"/> are those of this Unity line.</summary>
    public const string SupportedUnity = "2022.3.";

    public readonly Dictionary<string, GameFile> Files = new();

    /// <summary>MonoScripts (which class a MonoBehaviour's data is), from whichever file holds them.</summary>
    public readonly Dictionary<ObjId, ScriptInfo> Scripts = new();

    /// <summary>Resources.Load paths (lowercase, as Unity stores them) by object.</summary>
    public readonly Dictionary<ObjId, List<string>> ResourcePaths = new();
    public readonly string UnityVersion;
    private readonly AssetsManager manager = new();
    private readonly Dictionary<string, AssetsFileInstance> instances = new();

    public static string NormalizeName(string path) =>
        Path.GetFileName(path.Replace('\\', '/')).ToLowerInvariant();

    public GameFiles(string dataDir)
    {
        if (!File.Exists(Path.Combine(dataDir, "globalgamemanagers")))
        {
            throw new CoreGenException(
                $"'{dataDir}' is not a Unity player data folder (no globalgamemanagers)"
            );
        }
        List<string> paths = new();
        foreach (string p in Directory.GetFiles(dataDir).OrderBy(p => p, StringComparer.Ordinal))
        {
            string n = Path.GetFileName(p);
            if (
                n == "globalgamemanagers"
                || n == "globalgamemanagers.assets"
                || n == "resources.assets"
                || Regex.IsMatch(n, @"^(level\d+|sharedassets\d+\.assets)$")
            )
            {
                paths.Add(p);
            }
        }
        foreach (
            string extra in new[]
            {
                "Resources/unity default resources",
                "Resources/unity_builtin_extra",
            }
        )
        {
            string p = Path.Combine(dataDir, extra);
            if (File.Exists(p))
            {
                paths.Add(p);
            }
        }
        foreach (string p in paths)
        {
            AssetsFileInstance inst = manager.LoadAssetsFile(p, loadDeps: false);
            GameFile f = new() { Name = NormalizeName(p) };
            foreach (AssetsFileExternal e in inst.file.Metadata.Externals)
            {
                f.Externals.Add(NormalizeName(e.PathName));
            }
            instances[f.Name] = inst;
            Files[f.Name] = f;
        }
        UnityVersion = instances["globalgamemanagers"].file.Metadata.UnityVersion;
        if (!UnityVersion.StartsWith(SupportedUnity))
        {
            throw new CoreGenException(
                $"the game is built with Unity {UnityVersion}; CoreGen reads the engine objects of Unity {SupportedUnity}x only"
            );
        }
        ReadScripts();
        foreach (GameFile f in Files.Values)
        {
            Index(f);
        }
        LinkComponents();
        ReadResourcePaths();
    }

    public AssetsFileReader Reader(GameFile f) => instances[f.Name].file.Reader;

    public void Dispose() => manager.UnloadAll(true);

    public GameObj? Get(ObjId id) =>
        Files.TryGetValue(id.File, out GameFile? f)
        && f.Objects.TryGetValue(id.PathId, out GameObj? o)
            ? o
            : null;

    public GameObj? Get(GameFile from, int fileId, long pathId)
    {
        if (pathId == 0)
        {
            return null;
        }
        string? file = from.ResolveFile(fileId);
        return file == null ? null : Get(new ObjId(file, pathId));
    }

    /// <summary>The object a PPtr value points to, or null.</summary>
    public GameObj? Get(GameFile from, AssetTypeValueField pptr) =>
        Get(from, pptr["m_FileID"].AsInt, pptr["m_PathID"].AsLong);

    private void ReadScripts()
    {
        foreach (KeyValuePair<string, AssetsFileInstance> f in instances)
        {
            foreach (AssetFileInfo info in f.Value.file.AssetInfos)
            {
                if (info.TypeId != ClassMonoScript)
                {
                    continue;
                }
                AssetsFileReader r = f.Value.file.Reader;
                r.Position = info.GetAbsoluteByteOffset(f.Value.file);
                r.ReadCountStringInt32();
                r.Align();
                int order = r.ReadInt32(); // m_ExecutionOrder
                r.ReadBytes(16); // m_PropertiesHash
                string cls = ReadString(r),
                    ns = ReadString(r),
                    asm = ReadString(r);
                Scripts[new ObjId(f.Key, info.PathId)] = new ScriptInfo(
                    asm.EndsWith(".dll") ? asm.Substring(0, asm.Length - 4) : asm,
                    ns,
                    cls,
                    order
                );
            }
        }
    }

    private static string ReadString(AssetsFileReader r)
    {
        string s = r.ReadCountStringInt32();
        r.Align();
        return s;
    }

    private static (int fileId, long pathId) ReadPPtr(AssetsFileReader r) =>
        (r.ReadInt32(), r.ReadInt64());

    private void Index(GameFile f)
    {
        AssetsFileInstance inst = instances[f.Name];
        AssetsFileReader r = inst.file.Reader;
        foreach (AssetFileInfo info in inst.file.AssetInfos)
        {
            GameObj o = new()
            {
                Id = new ObjId(f.Name, info.PathId),
                File = f,
                Info = info,
                ClassId = info.TypeId,
                Offset = info.GetAbsoluteByteOffset(inst.file),
            };
            f.Objects[info.PathId] = o;
            r.Position = o.Offset;
            switch (info.TypeId)
            {
                case ClassGameObject:
                {
                    int n = r.ReadInt32();
                    for (int i = 0; i < n; i++)
                    {
                        (int fid, long pid) = ReadPPtr(r);
                        o.Components.Add(new ObjId(f.ResolveFile(fid)!, pid));
                    }
                    r.ReadUInt32(); // m_Layer
                    o.Name = ReadString(r);
                    o.GameObject = o.Id;
                    break;
                }
                case ClassTransform:
                case ClassRectTransform:
                {
                    (int gf, long gp) = ReadPPtr(r);
                    o.GameObject = new ObjId(f.ResolveFile(gf)!, gp);
                    r.Position += 16 + 12 + 12; // rotation, position, scale
                    int n = r.ReadInt32();
                    for (int i = 0; i < n; i++)
                    {
                        (int cf, long cp) = ReadPPtr(r);
                        o.Children.Add(new ObjId(f.ResolveFile(cf)!, cp));
                    }
                    (int ff, long fp) = ReadPPtr(r);
                    o.Father = fp == 0 ? null : new ObjId(f.ResolveFile(ff)!, fp);
                    if (o.Father == null)
                    {
                        f.Roots.Add(o.Id);
                    }
                    break;
                }
                case ClassMonoBehaviour:
                {
                    ReadPPtr(r); // m_GameObject (linked from the GameObject side)
                    r.ReadByte();
                    r.Align();
                    (int sf, long sp) = ReadPPtr(r);
                    if (
                        f.ResolveFile(sf) is { } scriptFile
                        && Scripts.TryGetValue(new ObjId(scriptFile, sp), out ScriptInfo? s)
                    )
                    {
                        o.Script = s;
                    }
                    o.Name = ReadString(r);
                    break;
                }
                case ClassAudioMixerGroup:
                {
                    o.Name = ReadString(r);
                    (int mf, long mp) = ReadPPtr(r); // m_AudioMixer
                    o.Mixer = mp == 0 ? null : new ObjId(f.ResolveFile(mf)!, mp);
                    break;
                }
                case ClassSprite:
                    o.Name = ReadString(r);
                    o.SpriteRect = (r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    break;
                default:
                    o.Name = TryReadName(r, o.Offset + info.ByteSize);
                    break;
            }
        }
    }

    /// <summary>Components are exactly what GameObjects list; their GameObject comes from that list.</summary>
    private void LinkComponents()
    {
        foreach (GameFile f in Files.Values)
        {
            foreach (GameObj go in f.Objects.Values)
            {
                if (go.ClassId != ClassGameObject)
                {
                    continue;
                }
                foreach (ObjId c in go.Components)
                {
                    GameObj? comp = Get(c);
                    if (comp != null)
                    {
                        comp.GameObject = go.Id;
                        comp.IsComponent = true;
                    }
                }
            }
        }
    }

    /// <summary>m_Name of named assets (their first field); empty for objects that do not start with a sane string.</summary>
    private static string TryReadName(AssetsFileReader r, long end)
    {
        if (r.Position + 4 > end)
        {
            return "";
        }
        int len = r.ReadInt32();
        if (len < 0 || len > 512 || r.Position + len > end)
        {
            return "";
        }
        byte[] bytes = r.ReadBytes(len);
        foreach (byte b in bytes)
        {
            if (b < 32 && b != 9)
            {
                return "";
            }
        }
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private void ReadResourcePaths()
    {
        AssetsFileInstance inst = instances["globalgamemanagers"];
        GameFile f = Files["globalgamemanagers"];
        foreach (AssetFileInfo info in inst.file.AssetInfos)
        {
            if (info.TypeId != ClassResourceManager)
            {
                continue;
            }
            AssetsFileReader r = inst.file.Reader;
            r.Position = info.GetAbsoluteByteOffset(inst.file);
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                string path = ReadString(r);
                (int fid, long pid) = ReadPPtr(r);
                string? file = f.ResolveFile(fid);
                if (file != null)
                {
                    ObjId id = new(file, pid);
                    if (!ResourcePaths.TryGetValue(id, out List<string>? list))
                    {
                        ResourcePaths[id] = list = new List<string>();
                    }
                    list.Add(path);
                }
            }
        }
    }

    /// <summary>The C# type of an engine class id (Unity type names, with the few non-UnityEngine namespaces).</summary>
    public static string EngineTypeName(int classId)
    {
        string name = ((AssetClassID)classId).ToString();
        return name switch
        {
            "Tilemap" or "TilemapRenderer" or "TilemapCollider2D" => "UnityEngine.Tilemaps." + name,
            "AudioMixerGroup" => "UnityEngine.Audio.AudioMixerGroup",
            "AudioMixerController" => "UnityEngine.Audio.AudioMixer",
            "Grid" => "UnityEngine.Grid",
            "AnimatorController" => "UnityEngine.RuntimeAnimatorController",
            "SpriteAtlas" => "UnityEngine.U2D.SpriteAtlas",
            _ when int.TryParse(name, out _) => throw new CoreGenException(
                $"unknown engine class id {classId}"
            ),
            _ => "UnityEngine." + name,
        };
    }
}

public sealed class CoreGenException(string message) : Exception(message);
