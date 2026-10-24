using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Mono.Cecil;

namespace CoreGen;

/// <summary>
/// Serialized layouts of the scripts in a compiled assembly (the vanilla Assembly-CSharp, engine assemblies), generated
/// with AssetsTools' Cecil generator (Unity's rules: public or [SerializeField] fields, base class first). The
/// generator also lists multidimensional arrays, which Unity never serializes; those are removed here. The project's
/// own layouts come from its source instead (<see cref="Model"/>). Values are read with a check that the layout
/// consumed exactly the object's bytes, so a wrong layout cannot pass silently.
/// </summary>
public sealed class ScriptLayouts : IDisposable
{
	public readonly AssemblyDefinition Assembly;
	private readonly InMemoryResolver resolver = new();
	private readonly MonoCecilTempGenerator generator;
	private readonly UnityVersion version;
	private readonly Dictionary<string, AssetTypeTemplateField?> cache = new();

	public ScriptLayouts(string assemblyPath, string searchDir, string unityVersion)
	{
		resolver.AddSearchDirectory(searchDir);
		// Read from memory: the compiler keeps generators alive, and must not hold the game's files open.
		Assembly = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate, InMemory = true });
		generator = new MonoCecilTempGenerator(searchDir);
		version = new UnityVersion(unityVersion);
	}

	public void Dispose()
	{
		generator.Dispose();
		Assembly.Dispose();
		resolver.Dispose();
	}

	public TypeDefinition? FindType(string fullName) => Assembly.MainModule.GetType(fullName);

	/// <summary>The full MonoBehaviour layout (base fields + script fields) of a type in this assembly, or null if absent.</summary>
	public AssetTypeTemplateField? Layout(string fullName)
	{
		if (cache.TryGetValue(fullName, out AssetTypeTemplateField? cached))
		{
			return cached;
		}
		TypeDefinition? type = FindType(fullName);
		AssetTypeTemplateField? result = null;
		if (type != null)
		{
			result = MonoBehaviourBase();
			result.Children.AddRange(Fields(type));
		}
		cache[fullName] = result;
		return result;
	}

	/// <summary>The serialized fields of a type (with its base classes'), or null if this assembly has no such type.</summary>
	public List<AssetTypeTemplateField>? Fields(string ns, string nestedName)
	{
		TypeDefinition? type = Assembly.MainModule.GetType(ns.Length > 0 ? ns + "." + nestedName : nestedName);
		return type == null ? null : Fields(type);
	}

	private List<AssetTypeTemplateField> Fields(TypeDefinition type)
	{
		string ns = type.Namespace, name = type.Name;
		for (TypeDefinition t = type; t.DeclaringType != null; t = t.DeclaringType)
		{
			ns = t.DeclaringType.Namespace;
			name = t.DeclaringType.Name + "/" + name;
		}
		List<AssetTypeTemplateField> fields = generator.Read(Assembly, ns, name, version);
		RemoveMultidimensional(fields, type);
		return fields;
	}

	/// <summary>Reads one MonoBehaviour's data; throws if the layout does not consume exactly its bytes.</summary>
	public static AssetTypeValueField Read(AssetTypeTemplateField layout, GameObj obj, AssetsFileReader r)
	{
		AssetTypeValueField value = layout.MakeValue(r, obj.Offset);
		long read = r.Position - obj.Offset;
		if (read != obj.Info.ByteSize)
		{
			throw new CoreGenException($"{obj}: layout read {read} bytes, the object has {obj.Info.ByteSize}");
		}
		return value;
	}

	/// <summary>First structural difference between two layouts (types, arrays, alignment; names do not matter to the player), or null.</summary>
	public static string? Difference(AssetTypeTemplateField a, AssetTypeTemplateField b, string path = "")
	{
		if (a.Type != b.Type || a.IsArray != b.IsArray || a.IsAligned != b.IsAligned || a.ValueType != b.ValueType)
		{
			return $"{path}: vanilla {Describe(a)}, now {Describe(b)}";
		}
		int ac = a.Children?.Count ?? 0, bc = b.Children?.Count ?? 0;
		for (int i = 0; i < Math.Max(ac, bc); i++)
		{
			if (i >= ac)
			{
				return $"{path}/{b.Children![i].Name}: extra field {Describe(b.Children[i])}";
			}
			if (i >= bc)
			{
				return $"{path}/{a.Children![i].Name}: missing field {Describe(a.Children[i])}";
			}
			string? d = Difference(a.Children![i], b.Children![i], path + "/" + a.Children[i].Name);
			if (d != null)
			{
				return d;
			}
		}
		return null;
	}

	private static string Describe(AssetTypeTemplateField f) => $"{f.Type} {f.Name}{(f.IsAligned ? " (aligned)" : "")}";

	/// <summary>
	/// Whether <paramref name="now"/> is <paramref name="vanilla"/> plus fields vanilla does not have (by name), anywhere in
	/// a class or struct: the vanilla fields kept their order and types. The extra fields go into <paramref name="added"/>;
	/// with them not serialized, the layouts are the same. Renames (same position and type) need no help: names do not count.
	/// </summary>
	public static bool OnlyAdded(AssetTypeTemplateField vanilla, AssetTypeTemplateField now, List<AssetTypeTemplateField> added)
	{
		if (vanilla.Type != now.Type || vanilla.IsArray != now.IsArray || vanilla.IsAligned != now.IsAligned || vanilla.ValueType != now.ValueType)
		{
			return false;
		}
		List<AssetTypeTemplateField> a = vanilla.Children ?? new(), b = now.Children ?? new();
		List<AssetTypeTemplateField> found = new();
		if (a.Count == b.Count && Pairwise(a, b, found))
		{
			added.AddRange(found);
			return true;
		}
		if (vanilla.ValueType != AssetValueType.None || vanilla.IsArray || b.Count <= a.Count)
		{
			return false;
		}
		HashSet<string> names = new(a.Select(c => c.Name));
		List<AssetTypeTemplateField> kept = b.Where(c => names.Contains(c.Name)).ToList();
		found.Clear();
		if (kept.Count != a.Count || !kept.Select(c => c.Name).SequenceEqual(a.Select(c => c.Name)) || !Pairwise(a, kept, found))
		{
			return false;
		}
		added.AddRange(found);
		added.AddRange(b.Where(c => !names.Contains(c.Name)));
		return true;
	}

	private static bool Pairwise(List<AssetTypeTemplateField> a, List<AssetTypeTemplateField> b, List<AssetTypeTemplateField> added)
	{
		for (int i = 0; i < a.Count; i++)
		{
			if (!OnlyAdded(a[i], b[i], added))
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>A copy of a layout without the given nodes: what Unity reads once they are not serialized.</summary>
	public static AssetTypeTemplateField Without(AssetTypeTemplateField f, ICollection<AssetTypeTemplateField> drop)
	{
		if (f.Children == null || f.Children.Count == 0 || !Contains(f, drop))
		{
			return f;
		}
		AssetTypeTemplateField copy = new() { Name = f.Name, Type = f.Type, ValueType = f.ValueType, IsArray = f.IsArray, IsAligned = f.IsAligned, HasValue = f.HasValue };
		copy.Children = f.Children.Where(c => !drop.Contains(c)).Select(c => Without(c, drop)).ToList();
		return copy;
	}

	private static bool Contains(AssetTypeTemplateField f, ICollection<AssetTypeTemplateField> drop) => f.Children != null && f.Children.Any(c => drop.Contains(c) || Contains(c, drop));

	/// <summary>A layout as text, names included: equal text = equal layout and names.</summary>
	public static void Describe(AssetTypeTemplateField f, System.Text.StringBuilder into)
	{
		into.Append(f.Name).Append(':').Append(f.Type).Append(f.IsArray ? "[]" : "").Append(f.IsAligned ? "!" : "").Append((int)f.ValueType);
		if (f.Children is { Count: > 0 })
		{
			into.Append('{');
			foreach (AssetTypeTemplateField c in f.Children)
			{
				Describe(c, into);
				into.Append(',');
			}
			into.Append('}');
		}
	}

	/// <summary>Reads referenced assemblies whole and closes them, like the main one; each once.</summary>
	private sealed class InMemoryResolver : BaseAssemblyResolver
	{
		private readonly Dictionary<string, AssemblyDefinition> loaded = new();

		public override AssemblyDefinition Resolve(AssemblyNameReference name)
		{
			if (!loaded.TryGetValue(name.Name, out AssemblyDefinition? a))
			{
				loaded[name.Name] = a = Resolve(name, new ReaderParameters { AssemblyResolver = this, ReadingMode = ReadingMode.Immediate, InMemory = true });
			}
			return a;
		}

		protected override void Dispose(bool disposing)
		{
			foreach (AssemblyDefinition a in loaded.Values)
			{
				a.Dispose();
			}
			loaded.Clear();
			base.Dispose(disposing);
		}
	}

	public static AssetTypeTemplateField MonoBehaviourBase()
	{
		AssetTypeTemplateField PPtr(string name, string type) => Node(name, type, AssetValueType.None, false, new()
		{
			Node("m_FileID", "int", AssetValueType.Int32, false, new()),
			Node("m_PathID", "SInt64", AssetValueType.Int64, false, new()),
		});
		return Node("Base", "MonoBehaviour", AssetValueType.None, false, new()
		{
			PPtr("m_GameObject", "PPtr<GameObject>"),
			Node("m_Enabled", "UInt8", AssetValueType.UInt8, true, new()),
			PPtr("m_Script", "PPtr<MonoScript>"),
			CommonMonoTemplateHelper.String("m_Name"),
		});
	}

	private static AssetTypeTemplateField Node(string name, string type, AssetValueType valueType, bool aligned, List<AssetTypeTemplateField> children) => new()
	{
		Name = name,
		Type = type,
		ValueType = valueType,
		IsAligned = aligned,
		HasValue = valueType != AssetValueType.None,
		Children = children,
	};

	/// <summary>Drops T[,] fields (and recurses into [Serializable] class fields) by matching template names to Cecil fields.</summary>
	private static void RemoveMultidimensional(List<AssetTypeTemplateField> fields, TypeDefinition type)
	{
		Dictionary<string, FieldDefinition> byName = new();
		for (TypeDefinition? t = type; t != null; t = t.BaseType?.Resolve())
		{
			foreach (FieldDefinition f in t.Fields)
			{
				if (!byName.ContainsKey(f.Name))
				{
					byName[f.Name] = f;
				}
			}
			if (t.BaseType == null || t.BaseType.FullName is "UnityEngine.MonoBehaviour" or "UnityEngine.ScriptableObject" or "System.Object")
			{
				break;
			}
		}
		fields.RemoveAll(f => byName.TryGetValue(f.Name, out FieldDefinition? d) && d.FieldType is ArrayType { Rank: > 1 });
		foreach (AssetTypeTemplateField f in fields)
		{
			if (byName.TryGetValue(f.Name, out FieldDefinition? d) && !f.IsArray && f.Children?.Count > 0)
			{
				TypeDefinition? nested = d.FieldType.Resolve();
				if (nested != null && nested.IsSerializable && !nested.IsEnum && nested.Module.Assembly == type.Module.Assembly)
				{
					RemoveMultidimensional(f.Children, nested);
				}
			}
		}
	}
}
