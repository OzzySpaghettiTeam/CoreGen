using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CoreGen.Tests;

/// <summary>The repository, the game install (from vars.targets) and in-memory compilations of the project's sources.</summary>
public static class Workspace
{
	public static readonly string Root = FindRoot();
	public static readonly string GamePath = Regex.Match(File.ReadAllText(Path.Combine(Root, "vars.targets")), "<BaseGamePath>(.*?)</BaseGamePath>").Groups[1].Value;
	public static readonly string DataDir = Path.Combine(GamePath, "CasualtiesUnknown_Data");
	public static readonly string Managed = Path.Combine(DataDir, "Managed");

	/// <summary>The vanilla Assembly-CSharp: the pinned copy the build makes, else the game's (while it is still vanilla).</summary>
	public static readonly string Vanilla = File.Exists(Path.Combine(Root, "OpenScav/obj/coregen/Assembly-CSharp.dll")) ? Path.Combine(Root, "OpenScav/obj/coregen/Assembly-CSharp.dll") : Path.Combine(Managed, "Assembly-CSharp.dll");

	public static readonly Config Config = new(DataDir, Vanilla, Managed);

	private static readonly CSharpParseOptions parse = new(LanguageVersion.Latest);

	private static readonly ImmutableArray<MetadataReference> references = LoadReferences();

	private static string FindRoot()
	{
		foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
		{
			for (string? d = start; d != null; d = Path.GetDirectoryName(d))
			{
				if (File.Exists(Path.Combine(d, "vars.targets")))
				{
					return d;
				}
			}
		}
		throw new InvalidOperationException("vars.targets not found above " + AppContext.BaseDirectory);
	}

	/// <summary>The game's Managed assemblies, as OpenScav.csproj references them, plus netstandard 2.1.</summary>
	private static ImmutableArray<MetadataReference> LoadReferences()
	{
		string netstandard = Directory.GetDirectories(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "../../../packs/NETStandard.Library.Ref")).OrderBy(d => d).Last();
		IEnumerable<string> refs = Directory.GetFiles(Path.Combine(netstandard, "ref/netstandard2.1"), "*.dll");
		refs = refs.Concat(Directory.GetFiles(Managed, "*.dll").Where(p => Path.GetFileName(p) is var n && n != "Assembly-CSharp.dll" && n != "mscorlib.dll" && n != "netstandard.dll" && !n.StartsWith("System") && !n.StartsWith("Microsoft") && !n.StartsWith("Mono")));
		return refs.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToImmutableArray();
	}

	/// <summary>The C# sources of a project folder (not bin/obj), keyed by path relative to it.</summary>
	public static Dictionary<string, string> Sources(string project, params string[] exclude)
	{
		string dir = Path.Combine(Root, project);
		return Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
			.Where(p => !p.Contains("/bin/") && !p.Contains("/obj/") && !exclude.Contains(Path.GetRelativePath(dir, p)))
			.OrderBy(p => p, StringComparer.Ordinal)
			.ToDictionary(p => Path.GetRelativePath(dir, p), File.ReadAllText);
	}

	public static CSharpCompilation Compile(Dictionary<string, string> sources) => CSharpCompilation.Create("Assembly-CSharp",
		sources.Select(s => CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(s.Value, System.Text.Encoding.UTF8), parse, path: "/src/" + s.Key)),
		references,
		new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Disable));

	public sealed class Result
	{
		public required GeneratorDriver Driver;
		public required Compilation Input;
		public required Compilation Output;
		/// <summary>The generator's own diagnostics.</summary>
		public required ImmutableArray<Diagnostic> Generator;
		public required string Generated;

		/// <summary>Compile errors the generated code adds (the decompile has a few of its own, which do not count).</summary>
		public IEnumerable<Diagnostic> NewCompileErrors
		{
			get
			{
				HashSet<string> before = Input.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToHashSet();
				return Output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && !before.Contains(d.ToString()));
			}
		}

		public IEnumerable<Diagnostic> Ids(string id) => Generator.Where(d => d.Id == id);
	}

	/// <summary>Runs the generator as the compiler does (driver, config from MSBuild properties).</summary>
	public static Result Run(Compilation compilation, Config? config = null, GeneratorDriver? driver = null)
	{
		Config c = config ?? Config;
		driver ??= CSharpGeneratorDriver.Create(new[] { new Generator().AsSourceGenerator() }, parseOptions: parse,
			optionsProvider: new Options(new Dictionary<string, string> { ["build_property.CoreGenGameData"] = c.DataDir, ["build_property.CoreGenVanilla"] = c.Vanilla, ["build_property.CoreGenManaged"] = c.Managed }),
			driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
		driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out ImmutableArray<Diagnostic> diagnostics);
		GeneratorRunResult run = driver.GetRunResult().Results[0];
		if (run.Exception != null)
		{
			throw new Exception("generator threw", run.Exception);
		}
		string generated = run.GeneratedSources.First(s => s.HintName == "CoreData.g.cs").SourceText.ToString();
		return new Result { Driver = driver, Input = compilation, Output = output, Generator = diagnostics, Generated = generated };
	}

	/// <summary>Moves a vanilla class into a namespace (so Unity no longer binds its data) and adjusts it with <paramref name="edit"/>.</summary>
	public static void MoveToNamespace(Dictionary<string, string> sources, string file, string ns, Func<ClassDeclarationSyntax, ClassDeclarationSyntax>? edit = null)
	{
		CompilationUnitSyntax root = CSharpSyntaxTree.ParseText(sources[file], parse).GetCompilationUnitRoot();
		if (edit != null)
		{
			root = root.ReplaceNodes(root.Members.OfType<ClassDeclarationSyntax>(), (_, c) => edit(c));
		}
		NamespaceDeclarationSyntax n = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(ns)).WithMembers(root.Members);
		sources[file] = root.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(n)).NormalizeWhitespace().ToFullString();
		sources["GlobalUsings_" + ns + ".cs"] = $"global using {ns};";
	}

	/// <summary>The edit a rebuilt class needs: partial, [RebuiltScript], CoreFill() first in Awake/OnEnable, and a layout change.</summary>
	public static ClassDeclarationSyntax Rebuild(ClassDeclarationSyntax c, bool fill = true, bool partial = true)
	{
		string name = c.Identifier.Text;
		c = c.ReplaceNodes(c.Members.OfType<MethodDeclarationSyntax>().Where(m => fill && m.Identifier.Text is "Awake" && m.Body != null), (_, m) => m.WithBody(m.Body!.WithStatements(m.Body.Statements.Insert(0, SyntaxFactory.ParseStatement("CoreFill();")))));
		if (fill && !c.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == "Awake"))
		{
			c = c.ReplaceNodes(c.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text is "OnEnable" && m.Body != null), (_, m) => m.WithBody(m.Body!.WithStatements(m.Body.Statements.Insert(0, SyntaxFactory.ParseStatement("CoreFill();")))));
		}
		c = c.WithMembers(c.Members.Insert(0, SyntaxFactory.ParseMemberDeclaration("public int coreGenTestAddedField;")!));
		if (partial)
		{
			c = c.AddModifiers(SyntaxFactory.Token(SyntaxKind.PartialKeyword));
		}
		return c.AddAttributeLists(SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Attribute(SyntaxFactory.ParseName("CoreGen.RebuiltScript"), SyntaxFactory.ParseAttributeArgumentList($"(\"{name}\")")))));
	}

	private sealed class Options(Dictionary<string, string> values) : AnalyzerConfigOptionsProvider
	{
		private readonly Global global = new(values);

		public override AnalyzerConfigOptions GlobalOptions => global;

		public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Global.Empty;

		public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Global.Empty;

		private sealed class Global(Dictionary<string, string> values) : AnalyzerConfigOptions
		{
			public static readonly Global Empty = new(new Dictionary<string, string>());

			public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
		}
	}
}
