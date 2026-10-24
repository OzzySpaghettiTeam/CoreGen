using System;
using System.Collections.Generic;
using System.Linq;
using AssetsTools.NET;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CoreGen;

public enum ScriptFate
{
	/// <summary>The project keeps the class with the vanilla layout; Unity loads it normally.</summary>
	Kept,
	/// <summary>The class is gone from the global namespace and a [RebuiltScript] type recreates it.</summary>
	Rebuilt,
	/// <summary>The class is gone and nothing replaces it ([assembly: DroppedScript]); its data is skipped.</summary>
	Dropped,
	/// <summary>Something is wrong (reported); nothing is generated for it.</summary>
	Broken,
}

public sealed class ScriptPlan
{
	public VanillaScript Vanilla = null!;
	public ScriptFate Fate;
	/// <summary>The type Unity binds the data to (Kept) or the one rebuilding it (Rebuilt).</summary>
	public INamedTypeSymbol? Type;
	public RebuiltType? Rebuilt;
	/// <summary>Kept scripts with fields vanilla does not have: the layout Unity reads once those are not serialized.</summary>
	public AssetTypeTemplateField? EffectiveLayout;
}

/// <summary>A component the generated code creates, with the vanilla data it is filled from.</summary>
public sealed class RebuiltInstance
{
	public GameObj Obj = null!;
	public ScriptPlan Script = null!;
	public Scope Scope = null!;
	/// <summary>Identifier part unique to this instance (file and path id).</summary>
	public string Id => Emitter.Identifier(Obj.File.Name) + "_" + (Obj.Id.PathId < 0 ? "m" + (-Obj.Id.PathId) : Obj.Id.PathId.ToString());
}

/// <summary>Where generated code runs: one scene, or one prefab loaded from Resources.</summary>
public sealed class Scope
{
	public GameFile File = null!;
	public GameObj? PrefabRoot;
	public string? ResourcePath;
	public List<RebuiltInstance> Instances = new();
	/// <summary>References loaded components held to rebuilt ones.</summary>
	public List<RefInfo> Incoming = new();
	/// <summary>UnityEvent calls loaded components held to rebuilt ones.</summary>
	public List<CallInfo> IncomingCalls = new();
	public string Method => PrefabRoot == null ? $"Scene{File.BuildIndex}" : "Prefab_" + Emitter.Identifier(ResourcePath!);
}

/// <summary>
/// Decides what happens to every Assembly-CSharp script the assets hold data for, and where the generated code must
/// recreate components and restore references. Problems become diagnostics; the plan covers what is still sound.
/// </summary>
public sealed class Planner
{
	public readonly GameData Data;
	public readonly Model Model;
	public readonly List<Diagnostic> Diagnostics = new();
	public readonly Dictionary<ScriptInfo, ScriptPlan> Scripts = new();
	public readonly Dictionary<ObjId, RebuiltInstance> Rebuilt = new();
	public readonly List<Scope> Scopes = new();
	/// <summary>Fields of kept scripts (or their [Serializable] types) not in the vanilla layout; the build makes them [NonSerialized].</summary>
	public readonly HashSet<IFieldSymbol> NotInVanilla = new(SymbolEqualityComparer.Default);
	/// <summary>Rebuilt classes whose vanilla script has an execution order the class does not declare itself.</summary>
	public readonly Dictionary<INamedTypeSymbol, int> ExecutionOrders = new(SymbolEqualityComparer.Default);
	private readonly HashSet<string> warned = new();

	public Planner(GameData data, Model model)
	{
		Data = data;
		Model = model;
	}

	public GameFiles Files => Data.Files;

	private void Error(DiagnosticDescriptor d, Loc? at, params object[] args) => Model.Report(Diagnostics.Add, d, at, args);

	public void Plan()
	{
		Dictionary<string, RebuiltType> claims = new();
		foreach (RebuiltType r in Model.Rebuilt)
		{
			if (claims.TryGetValue(r.Vanilla, out RebuiltType? other))
			{
				Error(Descriptors.BadRebuiltType, r.Location, r.Symbol.ToDisplayString(), r.Vanilla, "rebuilds the same script as " + other.Symbol.ToDisplayString());
				continue;
			}
			claims[r.Vanilla] = r;
		}
		foreach (VanillaScript s in Data.Scripts.Values.OrderBy(s => s.Info.FullName, StringComparer.Ordinal))
		{
			Scripts[s.Info] = Classify(s, claims);
		}
		foreach (RebuiltType r in claims.Values.Where(r => !Data.Scripts.Keys.Any(k => k.FullName == r.Vanilla)))
		{
			Error(Descriptors.UnknownScript, r.Location, "[RebuiltScript(\"" + r.Vanilla + "\")]", r.Vanilla);
		}
		foreach (KeyValuePair<string, Loc?> d in Model.Dropped.Where(d => !Data.Scripts.Keys.Any(k => k.FullName == d.Key)))
		{
			Error(Descriptors.UnknownScript, d.Value, "[assembly: DroppedScript(\"" + d.Key + "\")]", d.Key);
		}
		foreach (ScriptPlan plan in Scripts.Values.Where(p => p.Fate == ScriptFate.Rebuilt))
		{
			foreach (GameObj o in plan.Vanilla.Instances)
			{
				AddRebuilt(plan, o);
			}
		}
		CheckCalls();
		AssignIncoming();
		CheckEarlyAccess();
	}

	private ScriptPlan Classify(VanillaScript s, Dictionary<string, RebuiltType> claims)
	{
		string name = s.Info.FullName;
		ScriptPlan plan = new() { Vanilla = s, Type = Model.FindType(name) };
		claims.TryGetValue(name, out RebuiltType? claim);
		bool dropped = Model.Dropped.TryGetValue(name, out Loc? dropLoc);
		if (plan.Type != null)
		{
			if (claim != null || dropped)
			{
				Error(Descriptors.StillBound, claim?.Location ?? dropLoc, name, claim != null ? claim.Symbol.ToDisplayString() + " rebuilds it" : "it is declared dropped");
				plan.Fate = ScriptFate.Broken;
				return plan;
			}
			AssetTypeTemplateField layout = Model.Layout(plan.Type);
			string? difference = ScriptLayouts.Difference(s.Layout, layout);
			plan.Fate = ScriptFate.Kept;
			if (difference == null)
			{
				return plan;
			}
			List<AssetTypeTemplateField> added = new();
			List<IFieldSymbol?> fields = new();
			if (ScriptLayouts.OnlyAdded(s.Layout, layout, added) && (fields = added.Select(Model.Origin).ToList()).All(f => f != null))
			{
				plan.EffectiveLayout = ScriptLayouts.Without(layout, new HashSet<AssetTypeTemplateField>(added));
				foreach (IFieldSymbol f in fields!)
				{
					if (NotInVanilla.Add(f))
					{
						Error(Descriptors.NotInVanilla, Loc.Of(f), f.ContainingType.Name + "." + f.Name, name);
					}
				}
				return plan;
			}
			Error(Descriptors.LayoutChanged, Loc.Of(plan.Type), name, s.Instances.Count, difference);
			plan.Fate = ScriptFate.Broken;
			return plan;
		}
		if (claim != null && dropped)
		{
			Error(Descriptors.BadRebuiltType, claim.Location, claim.Symbol.ToDisplayString(), name, "rebuilds a script that is also declared dropped");
		}
		if (claim != null)
		{
			plan.Type = claim.Symbol;
			plan.Rebuilt = claim;
			plan.Fate = claim.Invalid ? ScriptFate.Broken : ScriptFate.Rebuilt;
			if (plan.Fate == ScriptFate.Rebuilt)
			{
				CheckExecutionOrder(plan);
			}
			return plan;
		}
		plan.Fate = ScriptFate.Dropped;
		if (!dropped)
		{
			Error(Descriptors.Undeclared, null, name, s.Instances.Count);
		}
		return plan;
	}

	/// <summary>
	/// Unity orders Awake/Update/... by the MonoScript's execution order; a rebuilt class has no vanilla MonoScript, so
	/// the order comes from [DefaultExecutionOrder] (which the player honors for it), generated unless declared.
	/// </summary>
	private void CheckExecutionOrder(ScriptPlan plan)
	{
		int vanilla = plan.Vanilla.Info.ExecutionOrder;
		AttributeData? declared = plan.Type!.GetAttributes().FirstOrDefault(a => a.AttributeClass is { } c && Model.FullName(c) == "UnityEngine.DefaultExecutionOrder");
		if (declared == null)
		{
			if (vanilla != 0)
			{
				ExecutionOrders[plan.Type] = vanilla;
			}
		}
		else if (declared.ConstructorArguments.FirstOrDefault().Value is int order && order != vanilla)
		{
			Error(Descriptors.ExecutionOrder, plan.Rebuilt!.Location, plan.Type.Name, order, plan.Vanilla.Info.FullName, vanilla);
		}
	}

	private void AddRebuilt(ScriptPlan plan, GameObj o)
	{
		Scope? scope = ScopeOf(o);
		if (scope == null)
		{
			return;
		}
		RebuiltInstance inst = new() { Obj = o, Script = plan, Scope = scope };
		scope.Instances.Add(inst);
		Rebuilt[o.Id] = inst;
	}

	/// <summary>The scene, or the Resources prefab, an object belongs to; null (with an error) if generated code cannot reach it.</summary>
	public Scope? ScopeOf(GameObj o)
	{
		if (o.File.IsScene)
		{
			return GetScope(o.File, null, null);
		}
		GameObj? root = Data.RootOf(o);
		if (root == null)
		{
			Error(Descriptors.Unreachable, null, o.ToString(), "it is not in a GameObject hierarchy");
			return null;
		}
		if (!Files.ResourcePaths.TryGetValue(root.Id, out List<string>? paths))
		{
			Error(Descriptors.Unreachable, null, o.ToString(), $"it is on prefab '{root.Name}' in {root.File.Name}, which is not under Resources, so it cannot be loaded and fixed by path");
			return null;
		}
		return GetScope(o.File, root, paths[0]);
	}

	private Scope GetScope(GameFile file, GameObj? root, string? resourcePath)
	{
		Scope? s = Scopes.FirstOrDefault(x => x.File == file && x.PrefabRoot?.Id == root?.Id);
		if (s == null)
		{
			Scopes.Add(s = new Scope { File = file, PrefabRoot = root, ResourcePath = resourcePath });
		}
		return s;
	}

	/// <summary>
	/// Rebuilt components appear in sceneLoaded, after the scene's Awake/OnEnable. Code a kept script runs from those
	/// callbacks (followed into the project's methods and property getters, not into lambdas, which run later) must not
	/// use a component rebuilt in the same scene, nor statics such a component sets: they are not there yet.
	/// </summary>
	private void CheckEarlyAccess()
	{
		Dictionary<GameFile, HashSet<INamedTypeSymbol>> rebuiltIn = new();
		foreach (RebuiltInstance r in Rebuilt.Values.Where(r => r.Obj.File.IsScene))
		{
			if (!rebuiltIn.TryGetValue(r.Obj.File, out HashSet<INamedTypeSymbol>? set))
			{
				rebuiltIn[r.Obj.File] = set = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
			}
			set.Add(r.Script.Type!);
		}
		HashSet<string> reported = new();
		foreach (ScriptPlan plan in Scripts.Values.Where(p => p.Fate == ScriptFate.Kept))
		{
			HashSet<INamedTypeSymbol> rebuilt = new(plan.Vanilla.Instances.Where(i => rebuiltIn.ContainsKey(i.File)).SelectMany(i => rebuiltIn[i.File]), SymbolEqualityComparer.Default);
			if (rebuilt.Count == 0)
			{
				continue;
			}
			foreach (string callback in new[] { "Awake", "OnEnable" })
			{
				for (INamedTypeSymbol? t = plan.Type; t != null && t.Locations.Any(l => l.IsInSource); t = t.BaseType)
				{
					if (t.GetMembers(callback).OfType<IMethodSymbol>().FirstOrDefault(m => !m.IsStatic && m.Parameters.Length == 0) is { } m)
					{
						WalkEarly(m, $"{plan.Type!.Name}.{callback}", 0, new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default), rebuilt, reported);
						break;
					}
				}
			}
		}
	}

	private void WalkEarly(IMethodSymbol method, string chain, int depth, HashSet<IMethodSymbol> visited, HashSet<INamedTypeSymbol> rebuilt, HashSet<string> reported)
	{
		if (depth > 4 || !visited.Add(method))
		{
			return;
		}
		bool IsRebuilt(ITypeSymbol? t)
		{
			for (; t != null; t = t.BaseType)
			{
				if (t is INamedTypeSymbol n && rebuilt.Contains(n))
				{
					return true;
				}
			}
			return false;
		}
		foreach (SyntaxReference r in method.DeclaringSyntaxReferences)
		{
			SyntaxNode body = r.GetSyntax();
			SemanticModel sm = Model.Compilation.GetSemanticModel(body.SyntaxTree);
			foreach (SimpleNameSyntax n in body.DescendantNodes(d => d is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)).OfType<SimpleNameSyntax>())
			{
				ISymbol? symbol = sm.GetSymbolInfo(n).Symbol;
				if (symbol == null || n.Ancestors().OfType<InvocationExpressionSyntax>().Any(i => i.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" }))
				{
					continue;
				}
				string? member = null;
				ITypeSymbol? owner = null;
				if (symbol is IMethodSymbol { IsGenericMethod: true } generic && generic.TypeArguments.FirstOrDefault(IsRebuilt) is { } arg)
				{
					member = generic.Name + "<" + arg.Name + ">";
					owner = arg;
				}
				else if (symbol is IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol { IsStatic: false } && IsRebuilt(symbol.ContainingType))
				{
					member = symbol.Name;
					owner = symbol.ContainingType;
				}
				if (member != null)
				{
					if (reported.Add(chain + "|" + owner!.Name)) // the first use per call chain and class is enough
					{
						Model.Report(Diagnostics.Add, Descriptors.EarlyAccess, Loc.Of(n.GetLocation()), chain, member, owner.Name);
					}
					continue;
				}
				IMethodSymbol? callee = symbol switch
				{
					IMethodSymbol m => m,
					IPropertySymbol p => p.GetMethod,
					_ => null,
				};
				if (callee != null && SymbolEqualityComparer.Default.Equals(callee.ContainingAssembly, Model.Compilation.Assembly) && !IsRebuilt(callee.ContainingType) && !callee.DeclaringSyntaxReferences.IsEmpty)
				{
					WalkEarly(callee.OriginalDefinition, chain + " → " + (symbol is IPropertySymbol ? symbol.Name : callee.Name), depth + 1, visited, rebuilt, reported);
				}
			}
		}
	}

	/// <summary>The project type whose data an object is: the kept or rebuilding type for Assembly-CSharp scripts.</summary>
	public ScriptPlan? PlanOf(GameObj o) => o.Script is { IsGame: true } s && Scripts.TryGetValue(s, out ScriptPlan? p) ? p : null;

	/// <summary>Does the component load (and so keep its own data and calls)? Engine scripts and kept game scripts do.</summary>
	private bool Loads(GameObj o) => o.Script is { IsGame: false } || PlanOf(o)?.Fate == ScriptFate.Kept;

	/// <summary>Every UnityEvent call into a kept or rebuilt script must find its method, as Unity looks it up by name.</summary>
	private void CheckCalls()
	{
		foreach (CallInfo call in Data.Calls)
		{
			ScriptPlan? target = PlanOf(call.Target);
			if (target == null || target.Type == null || target.Fate is not (ScriptFate.Kept or ScriptFate.Rebuilt))
			{
				continue;
			}
			if (FindCallMethod(target.Type, call) == null)
			{
				string key = target.Type.ToDisplayString() + "." + call.Method + "/" + call.Mode;
				if (warned.Add(key))
				{
					Error(Descriptors.MissingMethod, Loc.Of(target.Type), target.Type.ToDisplayString(), call.Method, ModeDescription(call.Mode), call.Owner.ToString());
				}
			}
		}
	}

	public static string ModeDescription(int mode) => mode switch
	{
		0 => "taking the event's arguments",
		1 => "with no parameters",
		2 => "taking one UnityEngine.Object",
		3 => "taking one int",
		4 => "taking one float",
		5 => "taking one string",
		6 => "taking one bool",
		_ => "of listener mode " + mode,
	};

	/// <summary>The method a persistent call binds to (Unity: by name and argument kind, any accessibility, base classes too).</summary>
	public static IMethodSymbol? FindCallMethod(ITypeSymbol type, CallInfo call) => Model.FindMethods(type, call.Method).FirstOrDefault(m => !m.IsGenericMethod && call.Mode switch
	{
		0 => m.Parameters.Length <= 4,
		1 => m.Parameters.Length == 0,
		2 => m.Parameters.Length == 1 && Model.IsUnityObject(m.Parameters[0].Type),
		3 => m.Parameters.Length == 1 && m.Parameters[0].Type.SpecialType == SpecialType.System_Int32,
		4 => m.Parameters.Length == 1 && m.Parameters[0].Type.SpecialType == SpecialType.System_Single,
		5 => m.Parameters.Length == 1 && m.Parameters[0].Type.SpecialType == SpecialType.System_String,
		6 => m.Parameters.Length == 1 && m.Parameters[0].Type.SpecialType == SpecialType.System_Boolean,
		_ => false,
	});

	/// <summary>References and calls that loaded components hold to rebuilt (restored) or dropped (warned) components.</summary>
	private void AssignIncoming()
	{
		foreach (RefInfo r in Data.Refs)
		{
			if (!Loads(r.Owner) || PlanOf(r.Target) is not { } p)
			{
				continue;
			}
			if (p.Fate == ScriptFate.Dropped)
			{
				Warn(Descriptors.DroppedReference, r.Owner.ToString(), OwnerPath(r.Owner, r.Path), r.Target.ToString(), p.Vanilla.Info.FullName);
			}
			else if (p.Fate == ScriptFate.Rebuilt && Rebuilt.ContainsKey(r.Target.Id) && ScopeOf(r.Owner) is { } scope)
			{
				scope.Incoming.Add(r);
			}
		}
		foreach (CallInfo c in Data.Calls)
		{
			if (Loads(c.Owner) && PlanOf(c.Target) is { Fate: ScriptFate.Rebuilt } && Rebuilt.ContainsKey(c.Target.Id) && ScopeOf(c.Owner) is { } scope)
			{
				scope.IncomingCalls.Add(c);
			}
		}
		if (Data.Unreadable.Count > 0 && Rebuilt.Count > 0)
		{
			Warn(Descriptors.Unreadable, string.Join(", ", Data.Unreadable));
		}
	}

	private void Warn(DiagnosticDescriptor d, params object[] args)
	{
		if (warned.Add(d.Id + string.Join("|", args)))
		{
			Error(d, null, args);
		}
	}

	/// <summary>A path in a loaded component's data, named by the layout Unity reads it with (the project's for kept scripts).</summary>
	public string OwnerPath(GameObj owner, List<PathSeg> path)
	{
		ScriptPlan? plan = PlanOf(owner);
		AssetTypeTemplateField layout = owner.Script!.IsGame ? plan!.EffectiveLayout ?? Model.Layout(plan.Type!) : Data.EngineLayouts[owner.Script];
		return GameData.PathString(layout, path);
	}

	/// <summary>Path segments from the scope root to a GameObject: names, "#k" for the k-th sibling of the same name.</summary>
	public List<string> PathOf(GameObj go, Scope scope)
	{
		List<string> segments = new();
		GameObj? cur = go;
		while (cur != null)
		{
			GameObj t = Files.Get(Data.TransformOf[cur.Id])!;
			GameObj? father = t.Father is ObjId f ? Files.Get(Files.Get(f)!.GameObject!.Value) : null;
			if (father == null && scope.PrefabRoot != null)
			{
				break; // prefab paths are relative to the root
			}
			List<ObjId> siblings = t.Father is ObjId ff ? Files.Get(ff)!.Children : cur.File.Roots;
			int k = 0, same = 0;
			foreach (ObjId s in siblings)
			{
				GameObj? sgo = Files.Get(s) is { GameObject: ObjId sg } ? Files.Get(sg) : null;
				if (sgo == null || sgo.Name != cur.Name)
				{
					continue;
				}
				if (sgo.Id == cur.Id)
				{
					k = same;
				}
				same++;
			}
			if (father == null && same > 1)
			{
				throw new CoreGenException($"scene root '{cur.Name}' in {cur.File.Name} is not unique; root order is not stable at runtime");
			}
			if (cur.Name.Contains("/") || cur.Name.Contains("#"))
			{
				throw new CoreGenException($"GameObject name '{cur.Name}' contains '/' or '#'");
			}
			segments.Insert(0, k == 0 ? cur.Name : $"{cur.Name}#{k}");
			cur = father;
		}
		return segments;
	}
}
