using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CoreGen;

/// <summary>
/// The step after compiling that a source generator cannot do: marking fields [NonSerialized]. The generator lists the
/// fields of kept scripts that the vanilla layout does not have ([assembly: CoreGen.NotInVanillaLayout(type, field)]);
/// this sets their NotSerialized flag in the compiled assembly, which is exactly what [NonSerialized] compiles to, so
/// Unity reads the scripts' data with the vanilla layout. Idempotent: an assembly already marked is left as is.
/// </summary>
public static class Weaver
{
	public const string ListAttribute = "CoreGen.NotInVanillaLayoutAttribute";

	/// <summary>Marks the listed fields; returns those that changed ("Type.field").</summary>
	public static List<string> Apply(string assemblyPath)
	{
		string pdb = Path.ChangeExtension(assemblyPath, ".pdb");
		bool symbols = File.Exists(pdb);
		List<string> changed = new();
		// Read whole into memory, so the file can be rewritten in place.
		using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(assemblyPath)), new ReaderParameters { ReadSymbols = symbols, SymbolStream = symbols ? new MemoryStream(File.ReadAllBytes(pdb)) : null, SymbolReaderProvider = symbols ? new PortablePdbReaderProvider() : null }))
		{
			foreach (CustomAttribute a in assembly.CustomAttributes.Where(a => a.AttributeType.FullName == ListAttribute))
			{
				TypeReference typeRef = (TypeReference)a.ConstructorArguments[0].Value;
				string name = (string)a.ConstructorArguments[1].Value;
				TypeDefinition type = assembly.MainModule.GetType(typeRef.FullName) ?? throw new CoreGenException($"{ListAttribute}: no type {typeRef.FullName}");
				FieldDefinition field = type.Fields.FirstOrDefault(f => f.Name == name) ?? throw new CoreGenException($"{ListAttribute}: {type.FullName} has no field {name}");
				if (!field.IsNotSerialized)
				{
					field.IsNotSerialized = true;
					changed.Add(type.FullName + "." + name);
				}
			}
			if (changed.Count > 0)
			{
				using FileStream dll = File.Create(assemblyPath);
				using FileStream? symbolFile = symbols ? File.Create(pdb) : null;
				assembly.Write(dll, new WriterParameters { WriteSymbols = symbols, SymbolStream = symbolFile, SymbolWriterProvider = symbols ? new PortablePdbWriterProvider() : null });
			}
		}
		return changed;
	}
}

/// <summary>MSBuild task for <see cref="Weaver"/> (run by CoreGen.targets after CoreCompile).</summary>
public sealed class WeaveNotInVanilla : ITask
{
	[Required]
	public string Assembly { get; set; } = "";

	public IBuildEngine BuildEngine { get; set; } = null!;

	public ITaskHost HostObject { get; set; } = null!;

	public bool Execute()
	{
		try
		{
			List<string> changed = Weaver.Apply(Assembly);
			if (changed.Count > 0)
			{
				BuildEngine.LogMessageEvent(new BuildMessageEventArgs($"CoreGen: compiled as [NonSerialized] (not in the vanilla layout): {string.Join(", ", changed)}", "", "CoreGen", MessageImportance.Normal));
			}
			return true;
		}
		catch (Exception e)
		{
			BuildEngine.LogErrorEvent(new BuildErrorEventArgs("CoreGen", "CG0098", Assembly, 0, 0, 0, 0, "CoreGen could not mark fields [NonSerialized]: " + e.Message, "", "CoreGen"));
			return false;
		}
	}
}
