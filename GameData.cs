using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetsTools.NET;

namespace CoreGen;

/// <summary>A step from a value to one of its children: a field (index into the parent's children) or an array element.</summary>
public readonly record struct PathSeg(int Child, int Element)
{
    public static PathSeg Field(int i) => new(i, -1);

    public static PathSeg Item(int i) => new(-1, i);
}

/// <summary>A vanilla Assembly-CSharp script the assets hold data for.</summary>
public sealed class VanillaScript
{
    public ScriptInfo Info = null!;
    public AssetTypeTemplateField Layout = null!;
    public List<GameObj> Instances = new();
}

/// <summary>A reference a MonoBehaviour's data holds to a component of an Assembly-CSharp script.</summary>
public sealed class RefInfo
{
    public GameObj Owner = null!;
    public List<PathSeg> Path = null!;
    public GameObj Target = null!;
}

/// <summary>A UnityEvent persistent call (e.g. a button's OnClick) whose target is an Assembly-CSharp script.</summary>
public sealed class CallInfo
{
    public GameObj Owner = null!;

    /// <summary>Path to the UnityEvent field in the owner's data.</summary>
    public List<PathSeg> EventPath = null!;
    public GameObj Target = null!;
    public string Method = "";

    /// <summary>PersistentListenerMode: 0 event-defined, 1 void, 2 object, 3 int, 4 float, 5 string, 6 bool.</summary>
    public int Mode;
    public GameObj? ObjectArgument;
    public int IntArgument;
    public float FloatArgument;
    public string StringArgument = "";
    public bool BoolArgument;
}

/// <summary>
/// Everything CoreGen needs from the game's files, read once and kept while those files do not change: the object
/// index, the data of every Assembly-CSharp script instance (vanilla layout), and the references and UnityEvent
/// calls other components hold to them. The files are closed after reading.
/// </summary>
public sealed class GameData
{
    public GameFiles Files = null!;
    public string UnityVersion = "";
    public readonly Dictionary<ScriptInfo, VanillaScript> Scripts = new();

    /// <summary>Problems with the vanilla inputs themselves; nothing can be checked while there are any.</summary>
    public readonly List<string> Errors = new();
    public readonly List<RefInfo> Refs = new();
    public readonly List<CallInfo> Calls = new();

    /// <summary>Engine scripts on GameObjects whose data could not be read (their references are not scanned).</summary>
    public readonly SortedSet<string> Unreadable = new(StringComparer.Ordinal);

    /// <summary>Engine script layouts by script, for naming paths in their data.</summary>
    public readonly Dictionary<ScriptInfo, AssetTypeTemplateField> EngineLayouts = new();
    public readonly Dictionary<ObjId, ObjId> TransformOf = new();

    /// <summary>Layouts of the other assemblies in Managed (engine, UI, TextMeshPro...), loaded on demand and kept.</summary>
    private readonly Dictionary<string, ScriptLayouts?> engine = new();
    private string managedDir = "";

    private static readonly object gate = new();
    private static string? cachedKey;
    private static GameData? cached;

    /// <summary>The data for these inputs, reusing the last load while the files are unchanged.</summary>
    public static GameData Get(string dataDir, string vanillaDll, string managedDir)
    {
        string key = Key(dataDir, vanillaDll, managedDir);
        lock (gate)
        {
            if (cached == null || cachedKey != key)
            {
                cached = null;
                cached = Load(dataDir, vanillaDll, managedDir);
                cachedKey = key;
            }
            return cached;
        }
    }

    private static string Key(string dataDir, string vanillaDll, string managedDir)
    {
        StringBuilder k = new();
        k.Append(dataDir).Append('|').Append(vanillaDll).Append('|').Append(managedDir);
        foreach (
            string p in Directory
                .GetFiles(dataDir)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Append(vanillaDll)
        )
        {
            FileInfo fi = new(p);
            if (fi.Exists)
            {
                k.Append('|')
                    .Append(fi.Name)
                    .Append(':')
                    .Append(fi.Length)
                    .Append(':')
                    .Append(fi.LastWriteTimeUtc.Ticks);
            }
        }
        return k.ToString();
    }

    private static GameData Load(string dataDir, string vanillaDll, string managedDir)
    {
        if (!File.Exists(vanillaDll))
        {
            throw new CoreGenException($"vanilla assembly '{vanillaDll}' does not exist");
        }
        GameData d = new() { managedDir = managedDir };
        using GameFiles files = new(dataDir);
        d.Files = files;
        d.UnityVersion = files.UnityVersion;
        using ScriptLayouts vanilla = new(vanillaDll, managedDir, files.UnityVersion);
        if (vanilla.FindType("CoreGen.Fixer") != null)
        {
            throw new CoreGenException(
                $"'{vanillaDll}' was built with CoreGen, so it is not the vanilla Assembly-CSharp. Restore the game's original (e.g. verify the files in Steam) or point VanillaAssembly at a copy of it"
            );
        }
        d.Index();
        d.ReadVanilla(vanilla);
        d.Scan();
        return d;
    }

    private void Index()
    {
        foreach (GameFile f in Files.Files.Values)
        {
            foreach (GameObj o in f.Objects.Values)
            {
                if (o.ClassId == GameFiles.ClassGameObject)
                {
                    foreach (ObjId c in o.Components)
                    {
                        if (
                            Files.Get(c) is
                            { ClassId: GameFiles.ClassTransform or GameFiles.ClassRectTransform }
                        )
                        {
                            TransformOf[o.Id] = c;
                        }
                    }
                }
                if (o.Script is { IsGame: true })
                {
                    if (!Scripts.TryGetValue(o.Script, out VanillaScript? s))
                    {
                        Scripts[o.Script] = s = new VanillaScript { Info = o.Script };
                    }
                    s.Instances.Add(o);
                }
            }
        }
    }

    /// <summary>Reads every instance with its vanilla layout, which must consume exactly its bytes (self-check).</summary>
    private void ReadVanilla(ScriptLayouts vanilla)
    {
        foreach (VanillaScript s in Scripts.Values.ToList())
        {
            AssetTypeTemplateField? layout = vanilla.Layout(s.Info.FullName);
            if (layout == null)
            {
                Errors.Add(
                    $"{s.Info.FullName}: not in the vanilla assembly (is it really the vanilla Assembly-CSharp?)"
                );
                Scripts.Remove(s.Info);
                continue;
            }
            s.Layout = layout;
            foreach (GameObj o in s.Instances)
            {
                try
                {
                    o.Data = ScriptLayouts.Read(layout, o, Files.Reader(o.File));
                }
                catch (Exception e)
                {
                    Errors.Add(
                        $"vanilla layout of {s.Info.FullName} does not fit its data: {e.Message}"
                    );
                    break;
                }
            }
        }
    }

    /// <summary>Finds every reference and UnityEvent call that MonoBehaviours hold to Assembly-CSharp components.</summary>
    private void Scan()
    {
        foreach (GameFile f in Files.Files.Values)
        {
            foreach (GameObj o in f.Objects.Values)
            {
                if (o.ClassId != GameFiles.ClassMonoBehaviour || o.Script == null)
                {
                    continue;
                }
                AssetTypeValueField? data = o.Data;
                if (data == null && !o.Script.IsGame)
                {
                    AssetTypeTemplateField? layout = EngineLayout(o.Script);
                    try
                    {
                        data =
                            layout == null
                                ? null
                                : ScriptLayouts.Read(layout, o, Files.Reader(o.File));
                    }
                    catch (Exception)
                    {
                        data = null;
                    }
                    if (data == null && o.IsComponent)
                    {
                        Unreadable.Add(o.Script.FullName); // assets (ScriptableObjects) cannot point at scene components anyway
                    }
                }
                if (data != null)
                {
                    Walk(data, new List<PathSeg>(), o, null);
                }
            }
        }
    }

    /// <summary>The compiled layouts of an assembly in Managed (other than Assembly-CSharp), or null if there is none.</summary>
    public ScriptLayouts? Assembly(string name)
    {
        lock (engine)
        {
            if (!engine.TryGetValue(name, out ScriptLayouts? layouts))
            {
                string path = Path.Combine(managedDir, name + ".dll");
                engine[name] = layouts =
                    name != "Assembly-CSharp" && File.Exists(path)
                        ? new ScriptLayouts(path, managedDir, UnityVersion)
                        : null;
            }
            return layouts;
        }
    }

    private AssetTypeTemplateField? EngineLayout(ScriptInfo s)
    {
        if (EngineLayouts.TryGetValue(s, out AssetTypeTemplateField? known))
        {
            return known;
        }
        AssetTypeTemplateField? layout = Assembly(s.Assembly)?.Layout(s.FullName);
        if (layout != null)
        {
            EngineLayouts[s] = layout;
        }
        return layout;
    }

    private void Walk(
        AssetTypeValueField field,
        List<PathSeg> path,
        GameObj owner,
        List<PathSeg>? eventPath
    )
    {
        AssetTypeTemplateField t = field.TemplateField;
        if (t.Type == "PersistentCall" && t.Children.Any(c => c.Name == "m_Target")) // not the List<PersistentCall> around it, which has the same type name
        {
            AddCall(field, owner, eventPath!);
            return; // its target and argument are the call's, not plain references
        }
        if (t.Type.StartsWith("PPtr<"))
        {
            if (Files.Get(owner.File, field) is { Script.IsGame: true } target)
            {
                Refs.Add(
                    new RefInfo
                    {
                        Owner = owner,
                        Path = new List<PathSeg>(path),
                        Target = target,
                    }
                );
            }
            return;
        }
        if (field.Children == null)
        {
            return;
        }
        for (int i = 0; i < field.Children.Count; i++)
        {
            AssetTypeValueField child = field.Children[i];
            if (path.Count == 0 && child.TemplateField.Name is "m_GameObject" or "m_Script")
            {
                continue;
            }
            path.Add(t.IsArray ? PathSeg.Item(i) : PathSeg.Field(i));
            Walk(
                child,
                path,
                owner,
                child.TemplateField.Name == "m_PersistentCalls"
                    ? path.Take(path.Count - 1).ToList()
                    : eventPath
            );
            path.RemoveAt(path.Count - 1);
        }
    }

    private void AddCall(AssetTypeValueField call, GameObj owner, List<PathSeg> eventPath)
    {
        if (Files.Get(owner.File, call["m_Target"]) is not { Script.IsGame: true } target)
        {
            return;
        }
        AssetTypeValueField args = call["m_Arguments"];
        Calls.Add(
            new CallInfo
            {
                Owner = owner,
                EventPath = eventPath,
                Target = target,
                Method = call["m_MethodName"].AsString,
                Mode = call["m_Mode"].AsInt,
                ObjectArgument = Files.Get(owner.File, args["m_ObjectArgument"]),
                IntArgument = args["m_IntArgument"].AsInt,
                FloatArgument = args["m_FloatArgument"].AsFloat,
                StringArgument = args["m_StringArgument"].AsString,
                BoolArgument = args["m_BoolArgument"].AsBool,
            }
        );
    }

    /// <summary>A data path as member names ("a.b[2].c"), named by the given layout (vanilla, engine or the project's).</summary>
    public static string PathString(AssetTypeTemplateField layout, List<PathSeg> path)
    {
        StringBuilder s = new();
        AssetTypeTemplateField t = layout;
        foreach (PathSeg seg in path)
        {
            if (seg.Element >= 0)
            {
                t = t.Children[1]; // Array: size, data
                s.Append('[').Append(seg.Element).Append(']');
                continue;
            }
            t = t.Children[seg.Child];
            if (!t.IsArray)
            {
                s.Append(s.Length > 0 ? "." : "").Append(t.Name);
            }
        }
        return s.ToString();
    }

    /// <summary>The root GameObject of the hierarchy an object (GameObject or component) is in.</summary>
    public GameObj? RootOf(GameObj o)
    {
        GameObj? go = o.GameObject is ObjId g ? Files.Get(g) : null;
        while (
            go != null
            && TransformOf.TryGetValue(go.Id, out ObjId tid)
            && Files.Get(tid) is { Father: ObjId father }
        )
        {
            go = Files.Get(father) is { GameObject: ObjId pg } ? Files.Get(pg) : null;
        }
        return go;
    }
}
