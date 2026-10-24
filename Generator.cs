using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CoreGen;

/// <summary>
/// The game player has no type trees: Unity reads each component's bytes by the order and types of its class's
/// serialized fields as vanilla declared them. For every Assembly-CSharp script the game's assets hold data for, the
/// project must keep the vanilla layout, rebuild the script ([RebuiltScript]) or declare it dropped
/// ([assembly: DroppedScript]); anything else is a compile error, where the IDE shows it. For rebuilt scripts it
/// generates CoreGen.CoreData (what to recreate per scene and Resources prefab) and fill methods in each rebuilt class.
/// The runtime (Runtime/*.cs: Fixer, Hierarchy, Assets, Links, the attributes) is added to the project too; call <c>CoreGen.Fixer.Install()</c> before the
/// first scene. Configured by MSBuild properties (see CoreGen.targets).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class Generator : IIncrementalGenerator
{
	/// <summary>Runtime/*.cs, embedded by CoreGen.csproj.</summary>
	private const string RuntimePrefix = "CoreGen.Runtime.";

	/// <summary>One run at a time: the game data cache and layout caches are shared.</summary>
	private static readonly object gate = new();

	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		context.RegisterPostInitializationOutput(c =>
		{
			foreach (string name in typeof(Generator).Assembly.GetManifestResourceNames().Where(n => n.StartsWith(RuntimePrefix)).OrderBy(n => n, StringComparer.Ordinal))
			{
				c.AddSource("CoreGen." + name.Substring(RuntimePrefix.Length, name.Length - RuntimePrefix.Length - 3) + ".g.cs", Resource(name));
			}
		});
		IncrementalValueProvider<Config> config = context.AnalyzerConfigOptionsProvider.Select((o, _) => Config.From(o.GlobalOptions));
		IncrementalValueProvider<string> fingerprint = context.CompilationProvider.Select((c, ct) => Model.Fingerprint(c, ct));
		// The compilation rides along but does not count: runs repeat only when the fingerprint or config change.
		IncrementalValueProvider<((string, Compilation), Config)> input = fingerprint.Combine(context.CompilationProvider).Combine(config).WithComparer(new InputComparer());
		context.RegisterSourceOutput(input, (spc, x) => Execute(spc, x.Item1.Item2, x.Item2));
	}

	private static void Execute(SourceProductionContext spc, Compilation compilation, Config config)
	{
		lock (gate)
		{
			List<Diagnostic> diagnostics = new();
			string code;
			try
			{
				code = Run(compilation, config, diagnostics);
			}
			catch (Exception e) when (e is not OperationCanceledException)
			{
				diagnostics.Add(Diagnostic.Create(Descriptors.Internal, Location.None, e.ToString().Replace("\n", " | ")));
				code = new Emitter(null, new Model(compilation, Config.DefaultUnity, _ => null)).Emit();
			}
			spc.AddSource("CoreData.g.cs", code);
			foreach (Diagnostic d in diagnostics)
			{
				spc.ReportDiagnostic(d);
			}
		}
	}

	/// <summary>Checks the project against the game's data and returns the generated source.</summary>
	public static string Run(Compilation compilation, Config config, List<Diagnostic> diagnostics)
	{
		Model model;
		Planner? planner = null;
		if (config.DataDir.Length == 0)
		{
			model = new Model(compilation, Config.DefaultUnity, _ => null); // not configured: the runtime and CoreFill() still compile
		}
		else
		{
			GameData data;
			try
			{
				data = GameData.Get(config.DataDir, config.Vanilla, config.Managed);
			}
			catch (CoreGenException e)
			{
				diagnostics.Add(Diagnostic.Create(Descriptors.Config, Location.None, e.Message));
				model = new Model(compilation, Config.DefaultUnity, _ => null);
				diagnostics.AddRange(model.Diagnostics);
				return new Emitter(null, model).Emit();
			}
			model = new Model(compilation, data.UnityVersion, data.Assembly);
			foreach (string e in data.Errors)
			{
				diagnostics.Add(Diagnostic.Create(Descriptors.VanillaBroken, Location.None, e));
			}
			if (data.Errors.Count == 0)
			{
				planner = new Planner(data, model);
				planner.Plan();
				diagnostics.AddRange(planner.Diagnostics);
			}
		}
		diagnostics.AddRange(model.Diagnostics);
		Emitter emitter = new(planner, model);
		string code = emitter.Emit();
		diagnostics.AddRange(emitter.Diagnostics);
		return code;
	}

	private static string Resource(string name)
	{
		using Stream s = typeof(Generator).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("missing resource " + name);
		using StreamReader r = new(s);
		return r.ReadToEnd();
	}

	private sealed class InputComparer : IEqualityComparer<((string, Compilation), Config)>
	{
		public bool Equals(((string, Compilation), Config) a, ((string, Compilation), Config) b) => a.Item1.Item1 == b.Item1.Item1 && a.Item2.Equals(b.Item2);

		public int GetHashCode(((string, Compilation), Config) x) => x.Item1.Item1.GetHashCode() ^ x.Item2.GetHashCode();
	}
}

/// <summary>The MSBuild properties CoreGen.targets passes in.</summary>
public readonly record struct Config(string DataDir, string Vanilla, string Managed)
{
	public const string DefaultUnity = "2022.3.0f1";

	public static Config From(AnalyzerConfigOptions o)
	{
		string Get(string name) => o.TryGetValue("build_property." + name, out string? v) ? v.Trim() : "";
		string data = Get("CoreGenGameData");
		string managed = Get("CoreGenManaged");
		return new Config(data, Get("CoreGenVanilla"), managed.Length > 0 || data.Length == 0 ? managed : Path.Combine(data, "Managed"));
	}
}

public static class Descriptors
{
	private const string Category = "CoreGen";

	private static DiagnosticDescriptor Error(string id, string title, string message) => new(id, title, message, Category, DiagnosticSeverity.Error, true);

	private static DiagnosticDescriptor Warning(string id, string title, string message) => new(id, title, message, Category, DiagnosticSeverity.Warning, true);

	public static readonly DiagnosticDescriptor LayoutChanged = Error("CG0001", "Serialized layout differs from vanilla",
		"{0} ({1} in the game's assets) keeps its name but not the vanilla serialized layout, so the player would misread its data: {2}. Fields may be added freely, but vanilla ones must keep their order and types: restore them, or move the class into a namespace and mark it [RebuiltScript(\"{0}\")].");

	public static readonly DiagnosticDescriptor Undeclared = Error("CG0002", "Vanilla script removed without a decision",
		"The vanilla script {0} ({1} in the game's assets) is gone and nothing rebuilds it, so its components would silently disappear. Add [RebuiltScript(\"{0}\")] to its replacement, or [assembly: CoreGen.DroppedScript(\"{0}\")] if that is intended.");

	public static readonly DiagnosticDescriptor StillBound = Error("CG0003", "Vanilla class still bound",
		"The global class {0} still exists, so Unity binds the vanilla data to it, but {1}");

	public static readonly DiagnosticDescriptor BadRebuiltType = Error("CG0004", "Invalid [RebuiltScript] class",
		"{0} cannot rebuild {1}: it {2}");

	public static readonly DiagnosticDescriptor MissingFill = Error("CG0005", "Rebuilt component not filled first",
		"{0} must start with CoreFill(); the vanilla data is applied there, before the rest of it runs");

	public static readonly DiagnosticDescriptor UnknownScript = Error("CG0006", "No such vanilla script",
		"{0}: the game's assets hold no data for a script named {1}");

	public static readonly DiagnosticDescriptor MissingMethod = Error("CG0007", "UnityEvent calls a missing method",
		"The game's assets call {0}.{1} {2} from a UnityEvent (on {3}), but there is no such method; Unity finds it by name, so the call would silently do nothing");

	public static readonly DiagnosticDescriptor BadFieldMap = Error("CG0008", "Invalid field map",
		"{0}: field map \"{1}\" is not \"vanillaField=member.path\"");

	public static readonly DiagnosticDescriptor Generation = Error("CG0009", "Cannot rebuild a component",
		"Cannot rebuild {0}: {1}");

	public static readonly DiagnosticDescriptor Unreachable = Generation;

	public static readonly DiagnosticDescriptor Config = Error("CG0010", "Cannot read the game",
		"CoreGen cannot read the game: {0}");

	public static readonly DiagnosticDescriptor VanillaBroken = Error("CG0011", "Vanilla inputs do not fit",
		"{0}");

	public static readonly DiagnosticDescriptor DroppedReference = Warning("CG0012", "Reference to a dropped script",
		"{0} {1} points at {2}, whose script {3} is dropped; it will be null");

	public static readonly DiagnosticDescriptor Unreadable = Warning("CG0013", "Unreadable scripts",
		"Could not read these scripts' data, so references they hold to rebuilt components are not restored: {0}");

	public static readonly DiagnosticDescriptor NotInVanilla = new("CG0014", "Field compiled as [NonSerialized]",
		"{0} is not in the vanilla layout of {1}, so CoreGen compiles it as [NonSerialized]: Unity neither loads it from the assets nor copies it on Instantiate (a copy gets its initial value)", Category, DiagnosticSeverity.Info, true);

	public static readonly DiagnosticDescriptor ExecutionOrder = Warning("CG0015", "Execution order differs from vanilla",
		"{0} declares [DefaultExecutionOrder({1})], but vanilla {2} ran at execution order {3}; remove the attribute to keep the vanilla order (CoreGen generates it)");

	public static readonly DiagnosticDescriptor EarlyAccess = Warning("CG0016", "Rebuilt component used in Awake/OnEnable",
		"{0} uses {1} of {2}, which CoreGen rebuilds only after the scene's Awake/OnEnable: here it is not there yet (null, or a static it sets in its Awake). Move this to Start.");

	public static readonly DiagnosticDescriptor Internal = Error("CG0099", "CoreGen failed",
		"CoreGen failed: {0}");
}
