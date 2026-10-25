using System.Diagnostics;
using System.Text;
using AssetsTools.NET;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace CoreGen.Tests;

public sealed class GeneratorTests(ITestOutputHelper output)
{
	private static Dictionary<string, string> Vanilla() => Workspace.Sources("GameDecomp");

	/// <summary>OpenScav's BepInEx loaders, built separately against BepInEx.</summary>
	private static readonly string[] Loader = { "Loader/Plugin.cs", "Loader/Preloader.cs" };

	private static string Describe(AssetTypeTemplateField f)
	{
		StringBuilder b = new();
		ScriptLayouts.Describe(f, b);
		return b.ToString();
	}

	/// <summary>
	/// Layouts read from source (Roslyn) must equal what Unity's rules give for the compiled code (Cecil): the vanilla
	/// decompile against the game's own Assembly-CSharp, OpenScav against itself compiled.
	/// </summary>
	[Theory]
	[InlineData("GameDecomp")]
	[InlineData("OpenScav")]
	public void SourceLayoutsMatchCompiledLayouts(string project)
	{
		// With the generator's output: the project uses its attributes and runtime (the generated members are not serialized).
		Compilation compilation = Workspace.Run(Workspace.Compile(Workspace.Sources(project, Loader))).Output;
		string dll = Workspace.Vanilla;
		if (project != "GameDecomp")
		{
			dll = Path.Combine(Path.GetTempPath(), $"coregen-{project}-{Guid.NewGuid():N}.dll");
			using FileStream fs = File.Create(dll);
			Microsoft.CodeAnalysis.Emit.EmitResult emit = compilation.Emit(fs);
			Assert.True(emit.Success, string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(10)));
		}
		try
		{
			GameData data = GameData.Get(Workspace.DataDir, Workspace.Vanilla, Workspace.Managed);
			Model model = new(compilation, data.UnityVersion, data.Assembly);
			using ScriptLayouts compiled = new(dll, Workspace.Managed, data.UnityVersion);
			List<string> mismatches = new();
			int checkedTypes = 0;
			foreach (Mono.Cecil.TypeDefinition t in compiled.Assembly.MainModule.GetTypes())
			{
				if (t.HasGenericParameters || t.IsInterface || !IsUnityObject(t))
				{
					continue;
				}
				INamedTypeSymbol? symbol = compilation.GetTypeByMetadataName(t.FullName.Replace('/', '+'));
				Assert.True(symbol != null, t.FullName + " is not in the source");
				string expected = Describe(compiled.Layout(t.FullName)!), actual = Describe(model.Layout(symbol!));
				checkedTypes++;
				if (expected != actual)
				{
					mismatches.Add($"{t.FullName}\n  compiled: {expected}\n  source:   {actual}");
				}
			}
			output.WriteLine($"{checkedTypes} types compared");
			Assert.True(checkedTypes > 100);
			Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(5)));
		}
		finally
		{
			if (dll != Workspace.Vanilla)
			{
				File.Delete(dll);
			}
		}
	}

	private static bool IsUnityObject(Mono.Cecil.TypeDefinition t)
	{
		for (Mono.Cecil.TypeReference? b = t.BaseType; b != null; b = b.Resolve()?.BaseType)
		{
			if (b.FullName == "UnityEngine.Object")
			{
				return true;
			}
		}
		return false;
	}

	[Fact]
	public void VanillaSourceIsClean()
	{
		Stopwatch sw = Stopwatch.StartNew();
		Workspace.Result r = Workspace.Run(Workspace.Compile(Vanilla()));
		output.WriteLine($"generator + compile: {sw.ElapsedMilliseconds} ms");
		Assert.Empty(r.Generator);
		Assert.Empty(r.NewCompileErrors);
		Assert.Contains("public static void FixPrefabs()", r.Generated);
		Assert.DoesNotContain("Fixer.Rebuild", r.Generated);
	}

	/// <summary>The scripts rebuilt in the earlier in-game experiment and those OpenScav rebuilds: the generated code must compile and restore everything.</summary>
	[Fact]
	public void RebuiltScriptsGenerateCompilingCode()
	{
		Dictionary<string, string> s = Vanilla();
		foreach (string file in new[] { "Vomiter.cs", "MenuExperimentBlink.cs", "WoundView.cs", "ConsoleScript.cs", "Body.cs", "PlayerCamera.cs", "WorldGeneration.cs", "TraderScript.cs" })
		{
			Workspace.MoveToNamespace(s, file, "Rebuilt", c => Workspace.Rebuild(c));
		}
		Workspace.Result r = Workspace.Run(Workspace.Compile(s));
		File.WriteAllText(Path.Combine(Path.GetTempPath(), "coregen-rebuilt.g.cs"), r.Generated);
		Assert.Empty(r.Generator.Where(d => d.Severity == DiagnosticSeverity.Error));
		Assert.Empty(r.NewCompileErrors.Take(10));
		string g = r.Generated;
		Assert.Contains("Fixer.Rebuild<global::Rebuilt.Vomiter>(s.Go(\"Experiment\", \"Body\")", g);
		Assert.Contains("Prefab_special_console()", g);
		Assert.Contains("c.vomitTime = 0f;", g); // private [SerializeField], assigned directly
		Assert.Contains("__CoreCall_TakeANap_", g); // the nap button's OnClick, typed
		Assert.Contains("Links.Set(s.Get<global::WoundViewLimb>(", g);
		Assert.Contains("private void CoreFill() => global::CoreGen.Fixer.Fill(this);", g);
		Assert.Contains("c.soundMixerGroup = global::CoreGen.Assets.MixerGroup(\"SoundMixer\", \"Master\");", g);
	}

	[Fact]
	public void ChangedLayoutIsAnError()
	{
		Dictionary<string, string> s = Vanilla();
		s["Limb.cs"] = s["Limb.cs"].Replace("\tpublic float baseMass;", "\t[System.NonSerialized]\n\tpublic float baseMass;");
		Workspace.Result r = Workspace.Run(Workspace.Compile(s));
		Diagnostic d = Assert.Single(r.Generator);
		Assert.Equal("CG0001", d.Id);
		Assert.Contains("Limb", d.GetMessage());
		Assert.EndsWith("Limb.cs", d.Location.GetLineSpan().Path);
	}

	/// <summary>Fields only added to a kept script are listed, hinted (CG0014), and marked [NonSerialized] after compiling.</summary>
	[Fact]
	public void AddedFieldsAreCompiledNonSerialized()
	{
		Dictionary<string, string> s = Vanilla();
		s["Limb.cs"] = s["Limb.cs"].Replace("\tpublic float baseMass;", "\tpublic float baseMass;\n\tpublic bool addedInTheMiddle;").Replace("public class Limb : MonoBehaviour\n{", "public class Limb : MonoBehaviour\n{\n\tpublic int addedFirst;");
		Workspace.Result r = Workspace.Run(Workspace.Compile(s));
		Assert.Equal(new[] { "CG0014", "CG0014" }, r.Generator.Select(d => d.Id));
		Assert.All(r.Generator, d => Assert.Equal(DiagnosticSeverity.Info, d.Severity));
		Assert.Contains("[assembly: global::CoreGen.NotInVanillaLayout(typeof(global::Limb), \"addedInTheMiddle\")]", r.Generated);
		Assert.Contains("[assembly: global::CoreGen.NotInVanillaLayout(typeof(global::Limb), \"addedFirst\")]", r.Generated);
		Assert.Empty(r.NewCompileErrors);
	}

	/// <summary>The whole path on OpenScav: generate, compile, weave; the compiled layouts of its scripts with added fields are vanilla again.</summary>
	[Fact]
	public void WovenOpenScavHasVanillaLayouts()
	{
		Workspace.Result r = Workspace.Run(Workspace.Compile(Workspace.Sources("OpenScav", Loader)));
		string[] added = r.Generator.Where(d => d.Id == "CG0014").Select(d => d.GetMessage().Split(' ')[0]).ToArray();
		output.WriteLine("compiled as [NonSerialized]: " + string.Join(", ", added));
		Assert.NotEmpty(added);
		string dll = Path.Combine(Path.GetTempPath(), $"coregen-woven-{Guid.NewGuid():N}.dll");
		try
		{
			using (FileStream fs = File.Create(dll))
			using (FileStream pdb = File.Create(Path.ChangeExtension(dll, ".pdb")))
			{
				Microsoft.CodeAnalysis.Emit.EmitResult emit = r.Output.Emit(fs, pdb, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.PortablePdb));
				Assert.True(emit.Success, string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(10)));
			}
			List<string> changed = Weaver.Apply(dll);
			Assert.Equal(added.Length, changed.Count);
			Assert.Empty(Weaver.Apply(dll)); // idempotent

			GameData data = GameData.Get(Workspace.DataDir, Workspace.Vanilla, Workspace.Managed);
			using ScriptLayouts woven = new(dll, Workspace.Managed, data.UnityVersion);
			foreach (string script in added.Select(a => a.Split('.')[0]).Distinct())
			{
				VanillaScript v = data.Scripts.Values.First(x => x.Info.FullName == script);
				Assert.Null(ScriptLayouts.Difference(v.Layout, woven.Layout(script)!));
			}
		}
		finally
		{
			File.Delete(dll);
			File.Delete(Path.ChangeExtension(dll, ".pdb"));
		}
	}

	[Fact]
	public void RemovedScriptMustBeDeclared()
	{
		Dictionary<string, string> s = Vanilla();
		Workspace.MoveToNamespace(s, "Vomiter.cs", "Gone");
		Assert.Equal("CG0002", Assert.Single(Workspace.Run(Workspace.Compile(s)).Generator).Id);

		s["Dropped.cs"] = "[assembly: CoreGen.DroppedScript(\"Vomiter\")]";
		Assert.DoesNotContain(Workspace.Run(Workspace.Compile(s)).Generator, d => d.Severity == DiagnosticSeverity.Error);
	}

	[Fact]
	public void RebuiltClassMustBePartialAndFillFirst()
	{
		Dictionary<string, string> s = Vanilla();
		Workspace.MoveToNamespace(s, "Vomiter.cs", "Rebuilt", c => Workspace.Rebuild(c, partial: false));
		Workspace.MoveToNamespace(s, "WoundView.cs", "Rebuilt", c => Workspace.Rebuild(c, fill: false));
		Workspace.Result r = Workspace.Run(Workspace.Compile(s));
		Assert.Contains(r.Generator, d => d.Id == "CG0004" && d.GetMessage().Contains("Vomiter") && d.GetMessage().Contains("partial"));
		Assert.Contains(r.Generator, d => d.Id == "CG0005" && d.GetMessage().Contains("WoundView.Awake"));
	}

	[Fact]
	public void RenamedEventMethodIsAnError()
	{
		Dictionary<string, string> s = Vanilla();
		s["WoundView.cs"] = s["WoundView.cs"].Replace("public void TakeANap()", "public void Nap()");
		Workspace.Result r = Workspace.Run(Workspace.Compile(s));
		Diagnostic d = Assert.Single(r.Generator);
		Assert.Equal("CG0007", d.Id);
		Assert.Contains("WoundView.TakeANap", d.GetMessage());
	}

	/// <summary>Edits inside method bodies keep the fingerprint, so the IDE does not rerun the generator on every keystroke.</summary>
	[Fact]
	public void MethodBodyEditsAreCached()
	{
		Dictionary<string, string> s = Vanilla();
		Workspace.MoveToNamespace(s, "Vomiter.cs", "Rebuilt", c => Workspace.Rebuild(c));
		CSharpCompilation first = Workspace.Compile(s);
		Workspace.Result r1 = Workspace.Run(first);

		SyntaxTree tree = first.SyntaxTrees.First(t => t.FilePath.EndsWith("/Item.cs"));
		MethodDeclarationSyntax method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First(m => m.Body is { Statements.Count: > 0 });
		SyntaxNode edited = tree.GetRoot().ReplaceNode(method, method.WithBody(method.Body!.AddStatements(SyntaxFactory.ParseStatement("int unusedEdit = 1;"))));
		Stopwatch sw = Stopwatch.StartNew();
		Workspace.Result r2 = Workspace.Run(first.ReplaceSyntaxTree(tree, tree.WithRootAndOptions(edited, tree.Options)), driver: r1.Driver);
		output.WriteLine($"rerun after a body edit: {sw.ElapsedMilliseconds} ms");

		GeneratorRunResult run = r2.Driver.GetRunResult().Results[0];
		IncrementalStepRunReason reason = run.TrackedOutputSteps.SelectMany(p => p.Value).SelectMany(step => step.Outputs).Select(o => o.Reason).Single();
		Assert.Equal(IncrementalStepRunReason.Cached, reason);
		Assert.Equal(r1.Generated, r2.Generated);
	}

	/// <summary>The bug found in game: RadiationLine.Awake → Deactivate read WorldGeneration.world before the rebuild.</summary>
	[Fact]
	public void AwakeUsingRebuiltComponentIsWarned()
	{
		Dictionary<string, string> s = Workspace.Sources("OpenScav", Loader);
		string file = "World/Hazards/RadiationLine.cs";
		s[file] = s[file].Replace("\t\tline = this;\n\t}", "\t\tline = this;\n\t\tDeactivate();\n\t}");
		Workspace.Result r = Workspace.Run(Workspace.Compile(s));
		Diagnostic d = Assert.Single(r.Generator, x => x.Id == "CG0016" && x.Location.GetLineSpan().Path.EndsWith("RadiationLine.cs"));
		Assert.Contains("RadiationLine.Awake → Deactivate", d.GetMessage());
		Assert.Contains("world of WorldGeneration", d.GetMessage());
		Assert.Contains("[global::UnityEngine.DefaultExecutionOrder(-1)]\n\tpartial class WorldGeneration", r.Generated);
	}

	/// <summary>Where OpenScav stands: prints what CoreGen reports for it (no assertion on the count, it is work in progress).</summary>
	[Fact]
	public void OpenScavReport()
	{
		Workspace.Result r = Workspace.Run(Workspace.Compile(Workspace.Sources("OpenScav", Loader)));
		foreach (Diagnostic d in r.Generator.OrderBy(d => d.Id))
		{
			output.WriteLine($"{d.Id} {d.GetMessage()}");
		}
		Assert.DoesNotContain(r.Generator, d => d.Id is "CG0099" or "CG0010" or "CG0011");
	}
}
