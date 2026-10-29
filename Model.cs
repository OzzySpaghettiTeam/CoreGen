using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CoreGen;

/// <summary>A source location that survives the compilation it came from (diagnostics are cached across edits).</summary>
public readonly record struct Loc(string Path, TextSpan Span, LinePositionSpan Lines)
{
    public static Loc? Of(ISymbol? symbol) =>
        symbol?.Locations.FirstOrDefault(l => l.IsInSource) is Location l ? Of(l) : null;

    public static Loc Of(Location l) =>
        new(l.SourceTree?.FilePath ?? "", l.SourceSpan, l.GetLineSpan().Span);

    public Location ToLocation() => Location.Create(Path, Span, Lines);
}

/// <summary>A class that takes over a vanilla script's data ([RebuiltScript]).</summary>
public sealed class RebuiltType
{
    public INamedTypeSymbol Symbol = null!;
    public string Vanilla = "";
    public Loc? Location;

    /// <summary>"vanillaField" -> "member.path" ("" drops the field).</summary>
    public Dictionary<string, string> FieldMap = new();

    /// <summary>Set when the class cannot be generated for (not partial, nested...); the reason is already reported.</summary>
    public bool Invalid;
}

/// <summary>
/// The project's side, read from its compilation: which scripts it keeps, rebuilds or drops, and their serialized
/// layouts. Layouts follow Unity's rules (what AssetsTools' Cecil generator does for compiled assemblies), from source
/// symbols; types from referenced assemblies (UnityEvent, UI base classes...) are read from the game's compiled copy
/// instead, since a compilation does not see their private fields.
/// </summary>
public sealed class Model
{
    public const string AttributeNamespace = "CoreGen";

    public readonly Compilation Compilation;
    public readonly List<RebuiltType> Rebuilt = new();

    /// <summary>[assembly: DroppedScript("Name")]: vanilla scripts whose data the project means to leave unused.</summary>
    public readonly Dictionary<string, Loc?> Dropped = new();
    public readonly List<Diagnostic> Diagnostics = new();
    private readonly Func<string, ScriptLayouts?> assemblies;
    private readonly UnityVersion version;
    private readonly int depthLimit;
    private readonly Dictionary<INamedTypeSymbol, AssetTypeTemplateField> layouts = new(
        SymbolEqualityComparer.Default
    );

    /// <summary>The source field each template node of a source type's layout was made from.</summary>
    private readonly Dictionary<AssetTypeTemplateField, IFieldSymbol> origins = new(
        new ReferenceComparer()
    );

    public Model(
        Compilation compilation,
        string unityVersion,
        Func<string, ScriptLayouts?> assemblies
    )
    {
        Compilation = compilation;
        this.assemblies = assemblies;
        version = new UnityVersion(unityVersion);
        depthLimit = CommonMonoTemplateHelper.GetSerializationLimit(version);
        ReadAttributes();
    }

    /// <summary>A top-level type of this project by its full name (vanilla scripts live in the global namespace).</summary>
    public INamedTypeSymbol? FindType(string fullName) =>
        Compilation.Assembly.GetTypeByMetadataName(fullName);

    public static string FullName(ITypeSymbol t) =>
        t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");

    // ---- Attributes ----------------------------------------------------------------------------------------------

    private void ReadAttributes()
    {
        foreach (
            AttributeData a in Compilation
                .Assembly.GetAttributes()
                .Where(a => IsOurs(a, "DroppedScriptAttribute"))
        )
        {
            if (a.ConstructorArguments.FirstOrDefault().Value is string name)
            {
                Dropped[name] = a.ApplicationSyntaxReference is { } r
                    ? Loc.Of(Location.Create(r.SyntaxTree, r.Span))
                    : null;
            }
        }
        foreach (INamedTypeSymbol t in AllTypes(Compilation.Assembly.GlobalNamespace))
        {
            AttributeData? a = t.GetAttributes()
                .FirstOrDefault(x => IsOurs(x, "RebuiltScriptAttribute"));
            if (a == null || a.ConstructorArguments.FirstOrDefault().Value is not string vanilla)
            {
                continue;
            }
            RebuiltType r = new()
            {
                Symbol = t,
                Vanilla = vanilla,
                Location = Loc.Of(t),
            };
            if (a.ConstructorArguments.Length > 1)
            {
                foreach (TypedConstant m in a.ConstructorArguments[1].Values)
                {
                    string s = m.Value as string ?? "";
                    int eq = s.IndexOf('=');
                    if (eq <= 0)
                    {
                        Report(Diagnostics.Add, Descriptors.BadFieldMap, r.Location, t.Name, s);
                        continue;
                    }
                    r.FieldMap[s.Substring(0, eq)] = s.Substring(eq + 1);
                }
            }
            Validate(r);
            Rebuilt.Add(r);
        }
    }

    private static bool IsOurs(AttributeData a, string name) =>
        a.AttributeClass
            is {
                Name: var n,
                ContainingNamespace:
                { Name: AttributeNamespace, ContainingNamespace.IsGlobalNamespace: true }
            }
        && n == name;

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceOrTypeSymbol ns)
    {
        foreach (ISymbol m in ns.GetMembers())
        {
            if (m is INamedTypeSymbol t)
            {
                yield return t;
                foreach (INamedTypeSymbol n in AllTypes(t))
                {
                    yield return n;
                }
            }
            else if (m is INamespaceSymbol child)
            {
                foreach (INamedTypeSymbol n in AllTypes(child))
                {
                    yield return n;
                }
            }
        }
    }

    /// <summary>The generated code adds members to the class, so it must be a top-level, non-generic, partial MonoBehaviour.</summary>
    private void Validate(RebuiltType r)
    {
        INamedTypeSymbol t = r.Symbol;
        string? problem = null;
        if (t.TypeKind != TypeKind.Class || !Inherits(t, "UnityEngine.MonoBehaviour"))
        {
            problem = "is not a MonoBehaviour";
        }
        else if (t.ContainingType != null)
        {
            problem = "is nested in another type";
        }
        else if (t.IsGenericType)
        {
            problem = "is generic";
        }
        else if (t.ContainingNamespace.IsGlobalNamespace)
        {
            problem =
                "is in the global namespace, where Unity would still bind the vanilla data to it by name";
        }
        else if (
            !t.DeclaringSyntaxReferences.Any(s =>
                s.GetSyntax() is ClassDeclarationSyntax c
                && c.Modifiers.Any(SyntaxKind.PartialKeyword)
            )
        )
        {
            problem = "is not partial (CoreGen adds its fill methods to it)";
        }
        if (problem != null)
        {
            r.Invalid = true;
            Report(
                Diagnostics.Add,
                Descriptors.BadRebuiltType,
                r.Location,
                t.ToDisplayString(),
                r.Vanilla,
                problem
            );
            return;
        }
        CheckFillCall(r);
    }

    /// <summary>The first callback Unity runs on the component (Awake, else OnEnable) must start with CoreFill().</summary>
    private void CheckFillCall(RebuiltType r)
    {
        IMethodSymbol? first =
            FindCallback(r.Symbol, "Awake") ?? FindCallback(r.Symbol, "OnEnable");
        if (first == null)
        {
            return; // nothing runs before the data is applied
        }
        bool ok = first
            .DeclaringSyntaxReferences.Select(s => s.GetSyntax())
            .OfType<MethodDeclarationSyntax>()
            .Any(m =>
                (
                    m.ExpressionBody?.Expression
                    ?? (
                        m.Body?.Statements.FirstOrDefault() as ExpressionStatementSyntax
                    )?.Expression
                )
                    is InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } call
                && call.Expression
                    is IdentifierNameSyntax { Identifier.Text: "CoreFill" }
                        or MemberAccessExpressionSyntax
                        {
                            Expression: ThisExpressionSyntax,
                            Name.Identifier.Text: "CoreFill"
                        }
            );
        if (!ok)
        {
            Report(
                Diagnostics.Add,
                Descriptors.MissingFill,
                Loc.Of(first) ?? r.Location,
                first.ContainingType.Name + "." + first.Name
            );
        }
        else if (!SymbolEqualityComparer.Default.Equals(first.ContainingType, r.Symbol))
        {
            Report(
                Diagnostics.Add,
                Descriptors.MissingFill,
                Loc.Of(first) ?? r.Location,
                first.ContainingType.Name
                    + "."
                    + first.Name
                    + " (in a base class, whose CoreFill does not fill "
                    + r.Symbol.Name
                    + ")"
            );
        }
    }

    private static IMethodSymbol? FindCallback(INamedTypeSymbol type, string name)
    {
        for (
            INamedTypeSymbol? t = type;
            t != null && t.Locations.Any(l => l.IsInSource);
            t = t.BaseType
        )
        {
            IMethodSymbol? m = t.GetMembers(name)
                .OfType<IMethodSymbol>()
                .FirstOrDefault(x => !x.IsStatic && x.Parameters.Length == 0);
            if (m != null)
            {
                return m;
            }
        }
        return null;
    }

    public static bool Inherits(ITypeSymbol type, string baseName)
    {
        for (ITypeSymbol? t = type.BaseType; t != null; t = t.BaseType)
        {
            if (FullName(t) == baseName)
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsUnityObject(ITypeSymbol t) =>
        t.TypeKind != TypeKind.Interface
        && (FullName(t) == "UnityEngine.Object" || Inherits(t, "UnityEngine.Object"));

    public static void Report(
        Action<Diagnostic> add,
        DiagnosticDescriptor d,
        Loc? at,
        params object[] args
    ) => add(Diagnostic.Create(d, at?.ToLocation() ?? Location.None, args));

    // ---- Layouts -------------------------------------------------------------------------------------------------

    /// <summary>The full MonoBehaviour layout (base fields + script fields) of a source type.</summary>
    public AssetTypeTemplateField Layout(INamedTypeSymbol type)
    {
        if (!layouts.TryGetValue(type, out AssetTypeTemplateField? result))
        {
            result = ScriptLayouts.MonoBehaviourBase();
            bool usingManagedReference = false;
            List<AssetTypeTemplateField> fields = new();
            TypeLoad(type, fields, depthLimit, true, ref usingManagedReference);
            if (usingManagedReference)
            {
                fields.Add(
                    CommonMonoTemplateHelper.ManagedReferencesRegistry("references", version)
                );
            }
            result.Children.AddRange(fields);
            layouts[type] = result;
        }
        return result;
    }

    /// <summary>The source field a node of a <see cref="Layout"/> was made from (null for base fields and other assemblies' types).</summary>
    public IFieldSymbol? Origin(AssetTypeTemplateField node) =>
        origins.TryGetValue(node, out IFieldSymbol? f) ? f : null;

    private sealed class ReferenceComparer : IEqualityComparer<AssetTypeTemplateField>
    {
        public bool Equals(AssetTypeTemplateField? a, AssetTypeTemplateField? b) =>
            ReferenceEquals(a, b);

        public int GetHashCode(AssetTypeTemplateField o) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
    }

    private bool FromSource(ITypeSymbol t) =>
        SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, Compilation.Assembly);

    /// <summary>Base class fields first, then the type's own (MonoCecilTempGenerator.RecursiveTypeLoad).</summary>
    private void TypeLoad(
        INamedTypeSymbol type,
        List<AssetTypeTemplateField> into,
        int depth,
        bool isRecursiveCall,
        ref bool usingManagedReference
    )
    {
        if (!isRecursiveCall)
        {
            depth--;
        }
        if (!FromSource(type))
        {
            into.AddRange(CompiledFields(type));
            return;
        }
        INamedTypeSymbol? baseType = type.BaseType;
        if (
            baseType != null
            && FullName(baseType)
                is not (
                    "object"
                    or "System.Object"
                    or "UnityEngine.Object"
                    or "UnityEngine.MonoBehaviour"
                    or "UnityEngine.ScriptableObject"
                    or "System.ValueType"
                )
        )
        {
            TypeLoad(baseType, into, depth, true, ref usingManagedReference);
        }
        foreach (IFieldSymbol f in AcceptableFields(type, depth))
        {
            AssetTypeTemplateField t = FieldTemplate(f, depth, ref usingManagedReference);
            origins[t] = f;
            into.Add(t);
        }
    }

    /// <summary>Fields of a type from another assembly, from the game's compiled copy of it (private fields included).</summary>
    private List<AssetTypeTemplateField> CompiledFields(INamedTypeSymbol type)
    {
        string nested = type.MetadataName;
        INamedTypeSymbol t = type;
        for (; t.ContainingType != null; t = t.ContainingType)
        {
            nested = t.ContainingType.MetadataName + "/" + nested;
        }
        string ns = t.ContainingNamespace.IsGlobalNamespace
            ? ""
            : t.ContainingNamespace.ToDisplayString();
        List<AssetTypeTemplateField>? fields = assemblies(type.ContainingAssembly.Name)
            ?.Fields(ns, nested);
        if (fields == null)
        {
            throw new CoreGenException(
                $"the layout of {FullName(type)} ({type.ContainingAssembly.Name}) is needed, but the game's Managed folder has no compiled copy of it"
            );
        }
        return fields;
    }

    private List<IFieldSymbol> AcceptableFields(INamedTypeSymbol parent, int depth)
    {
        List<IFieldSymbol> result = new();
        foreach (IFieldSymbol f in parent.GetMembers().OfType<IFieldSymbol>())
        {
            bool attributed = f.GetAttributes()
                .Any(a =>
                    a.AttributeClass != null
                    && FullName(a.AttributeClass)
                        is "UnityEngine.SerializeField"
                            or "UnityEngine.SerializeReference"
                );
            if (
                f.DeclaredAccessibility != Accessibility.Public && !attributed
                || f.IsStatic
                || f.IsConst
                || f.IsReadOnly
                || f.GetAttributes()
                    .Any(a =>
                        a.AttributeClass != null
                        && FullName(a.AttributeClass) == "System.NonSerializedAttribute"
                    )
            )
            {
                continue;
            }
            ITypeSymbol fieldType = f.Type;
            if (Element(fieldType) is { } element)
            {
                if (
                    depth < 0
                    || Element(element) != null
                    || fieldType is IArrayTypeSymbol { Rank: > 1 }
                )
                {
                    continue;
                }
                fieldType = element;
            }
            else if (
                SymbolEqualityComparer.Default.Equals(parent, fieldType) && !IsUnityObject(parent)
            )
            {
                continue;
            }
            if (fieldType.TypeKind != TypeKind.Error && IsValid(f, fieldType, depth))
            {
                result.Add(f);
            }
        }
        return result;
    }

    /// <summary>The element type of T[] or List&lt;T&gt;, else null.</summary>
    public static ITypeSymbol? Element(ITypeSymbol t) =>
        t switch
        {
            IArrayTypeSymbol a => a.ElementType,
            INamedTypeSymbol { IsGenericType: true } g
                when FullName(g.OriginalDefinition) == "System.Collections.Generic.List<T>" =>
                g.TypeArguments[0],
            _ => null,
        };

    private static bool IsPrimitive(ITypeSymbol t) =>
        t.SpecialType
            is SpecialType.System_Boolean
                or SpecialType.System_Char
                or SpecialType.System_SByte
                or SpecialType.System_Byte
                or SpecialType.System_Int16
                or SpecialType.System_UInt16
                or SpecialType.System_Int32
                or SpecialType.System_UInt32
                or SpecialType.System_Int64
                or SpecialType.System_UInt64
                or SpecialType.System_Single
                or SpecialType.System_Double
                or SpecialType.System_IntPtr
                or SpecialType.System_UIntPtr;

    /// <summary>Metadata-style full name ("System.Int32", "UnityEngine.Vector3"), as the template helpers expect.</summary>
    private static string MetadataFullName(ITypeSymbol t)
    {
        if (t.SpecialType != SpecialType.None)
        {
            return t.ContainingNamespace.ToDisplayString() + "." + t.MetadataName;
        }
        string name = t.MetadataName;
        for (INamedTypeSymbol? c = t.ContainingType; c != null; c = c.ContainingType)
        {
            name = c.MetadataName + "/" + name;
        }
        return t.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString() + "." + name
            : name;
    }

    private static bool HasSerializeReference(IFieldSymbol f) =>
        f.GetAttributes().Any(a => a.AttributeClass?.Name == "SerializeReference");

    private static bool IsSerializable(ITypeSymbol t) =>
        t is INamedTypeSymbol { IsSerializable: true };

    private bool IsValid(IFieldSymbol f, ITypeSymbol t, int depth)
    {
        if (IsPrimitive(t) || t.SpecialType == SpecialType.System_String)
        {
            return true;
        }
        if (t is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } u })
        {
            return u.SpecialType is not (SpecialType.System_Int64 or SpecialType.System_UInt64);
        }
        if (depth < 0)
        {
            return t.IsValueType
                && (
                    IsSerializable(t)
                    || CommonMonoTemplateHelper.IsSpecialUnityType(MetadataFullName(t))
                );
        }
        if (IsUnityObject(t) || CommonMonoTemplateHelper.IsSpecialUnityType(MetadataFullName(t)))
        {
            return true;
        }
        if (HasSerializeReference(f))
        {
            return !t.IsValueType && t is not INamedTypeSymbol { IsGenericType: true };
        }
        if (
            t.ContainingAssembly != null
            && CommonMonoTemplateHelper.IsAssemblyBlacklisted(t.ContainingAssembly.Name, version)
        )
        {
            return false;
        }
        return !t.IsAbstract && IsSerializable(t);
    }

    /// <summary>One field's template (MonoCecilTempGenerator.ReadTypes).</summary>
    private AssetTypeTemplateField FieldTemplate(
        IFieldSymbol f,
        int depth,
        ref bool usingManagedReference
    )
    {
        ITypeSymbol t = f.Type;
        bool isArray = false;
        if (Element(t) is { } element)
        {
            isArray = true;
            t = element;
        }
        AssetTypeTemplateField v = new() { Name = f.Name };
        bool primitive = false,
            isString = false,
            unityObject = false,
            managedReference = false;
        if (t is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } u })
        {
            primitive = true;
            v.Type = CommonMonoTemplateHelper.ConvertBaseToPrimitive(MetadataFullName(u));
        }
        else if (IsPrimitive(t))
        {
            primitive = true;
            v.Type = CommonMonoTemplateHelper.ConvertBaseToPrimitive(MetadataFullName(t));
        }
        else if (t.SpecialType == SpecialType.System_String)
        {
            isString = true;
            v.Type = "string";
        }
        else if (IsUnityObject(t))
        {
            unityObject = true;
            v.Type = "PPtr<$" + t.MetadataName + ">";
        }
        else if (HasSerializeReference(f))
        {
            managedReference = usingManagedReference = true;
            v.Type = "managedReference";
        }
        else
        {
            v.Type = t.MetadataName;
        }
        if (primitive)
        {
            v.Children = new List<AssetTypeTemplateField>();
        }
        else if (isString)
        {
            v.Children = CommonMonoTemplateHelper.String();
        }
        else if (CommonMonoTemplateHelper.IsSpecialUnityType(MetadataFullName(t)))
        {
            v.Children = SpecialUnity((INamedTypeSymbol)t, depth, ref usingManagedReference);
        }
        else if (unityObject)
        {
            v.Children = CommonMonoTemplateHelper.PPtr(version);
        }
        else if (managedReference)
        {
            v.Children = CommonMonoTemplateHelper.ManagedReference(version);
        }
        else if (IsSerializable(t))
        {
            v.Children = new List<AssetTypeTemplateField>();
            TypeLoad((INamedTypeSymbol)t, v.Children, depth, false, ref usingManagedReference);
        }
        v.ValueType = AssetTypeValueField.GetValueTypeByTypeName(v.Type);
        v.IsAligned = CommonMonoTemplateHelper.TypeAligns(v.ValueType);
        v.HasValue = v.ValueType != AssetValueType.None;
        if (isArray)
        {
            v =
                primitive || isString || unityObject
                    ? CommonMonoTemplateHelper.Vector(v)
                    : CommonMonoTemplateHelper.VectorWithType(v);
        }
        return v;
    }

    private List<AssetTypeTemplateField> SpecialUnity(
        INamedTypeSymbol t,
        int depth,
        ref bool usingManagedReference
    )
    {
        switch (t.Name)
        {
            case "Gradient":
                return CommonMonoTemplateHelper.Gradient(version);
            case "AnimationCurve":
                return CommonMonoTemplateHelper.AnimationCurve(version);
            case "LayerMask":
                return CommonMonoTemplateHelper.BitField();
            case "Bounds":
                return CommonMonoTemplateHelper.AABB();
            case "BoundsInt":
                return CommonMonoTemplateHelper.BoundsInt();
            case "Rect":
                return CommonMonoTemplateHelper.Rectf();
            case "RectOffset":
                return CommonMonoTemplateHelper.RectOffset();
            case "Color32":
                return CommonMonoTemplateHelper.RGBAi();
            case "GUIStyle":
                return CommonMonoTemplateHelper.GUIStyle(version);
            case "Vector2Int":
                return CommonMonoTemplateHelper.Vector2Int();
            case "Vector3Int":
                return CommonMonoTemplateHelper.Vector3Int();
            case "PropertyName":
                return CommonMonoTemplateHelper.PropertyName(version);
            case "SphericalHarmonicsL2":
                return CommonMonoTemplateHelper.SphericalHarmonicsL2(version);
            default:
                List<AssetTypeTemplateField> fields = new();
                TypeLoad(t, fields, depth, false, ref usingManagedReference);
                return fields;
        }
    }

    // ---- Members -------------------------------------------------------------------------------------------------

    /// <summary>An instance field of a type or its base classes.</summary>
    public static IFieldSymbol? FindField(ITypeSymbol type, string name)
    {
        for (ITypeSymbol? t = type; t != null; t = t.BaseType)
        {
            if (t.GetMembers(name).OfType<IFieldSymbol>().FirstOrDefault(f => !f.IsStatic) is { } f)
            {
                return f;
            }
        }
        return null;
    }

    /// <summary>Methods of a name on a type or its base classes.</summary>
    public static IEnumerable<IMethodSymbol> FindMethods(ITypeSymbol type, string name)
    {
        for (ITypeSymbol? t = type; t != null; t = t.BaseType)
        {
            foreach (IMethodSymbol m in t.GetMembers(name).OfType<IMethodSymbol>())
            {
                yield return m;
            }
        }
    }

    public bool Accessible(ISymbol member, INamedTypeSymbol from) =>
        Compilation.IsSymbolAccessibleWithin(member, from);

    // ---- Fingerprint ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Everything about the project's types that the generated code and the checks depend on: fields, methods, base
    /// types, attributes, the start of Awake/OnEnable and declaration lines. Edits that keep it equal (method bodies,
    /// mostly) do not rerun CoreGen.
    /// </summary>
    public static string Fingerprint(Compilation c, CancellationToken ct)
    {
        StringBuilder b = new();
        b.Append(c.AssemblyName).Append('\n');
        foreach (AttributeData a in c.Assembly.GetAttributes())
        {
            b.Append("[assembly:").Append(a).Append("]\n");
        }
        foreach (INamedTypeSymbol t in AllTypes(c.Assembly.GlobalNamespace))
        {
            ct.ThrowIfCancellationRequested();
            b.Append(t.ToDisplayString())
                .Append(':')
                .Append(t.TypeKind)
                .Append(':')
                .Append(t.BaseType?.ToDisplayString())
                .Append(t.IsSerializable ? ":s" : "")
                .Append(t.IsAbstract ? ":a" : "");
            foreach (Location l in t.Locations.Where(l => l.IsInSource))
            {
                FileLinePositionSpan span = l.GetLineSpan();
                b.Append('@')
                    .Append(span.Path)
                    .Append(':')
                    .Append(span.StartLinePosition.Line)
                    .Append(':')
                    .Append(l.SourceSpan.Start);
            }
            foreach (SyntaxReference r in t.DeclaringSyntaxReferences)
            {
                if (r.GetSyntax(ct) is TypeDeclarationSyntax d)
                {
                    b.Append(" mods:").Append(d.Modifiers.ToString());
                }
            }
            b.Append('\n');
            foreach (AttributeData a in t.GetAttributes())
            {
                b.Append(" [").Append(a).Append("]\n");
            }
            foreach (ISymbol m in t.GetMembers())
            {
                switch (m)
                {
                    case IFieldSymbol f:
                        b.Append(" f ")
                            .Append(f.DeclaredAccessibility)
                            .Append(f.IsStatic ? " static" : "")
                            .Append(f.IsReadOnly ? " readonly" : "")
                            .Append(f.IsConst ? " const" : "")
                            .Append(' ')
                            .Append(f.Type.ToDisplayString())
                            .Append(' ')
                            .Append(f.Name);
                        foreach (AttributeData a in f.GetAttributes())
                        {
                            b.Append(" [").Append(a).Append(']');
                        }
                        b.Append('\n');
                        break;
                    case IMethodSymbol meth:
                        b.Append(" m ")
                            .Append(meth.DeclaredAccessibility)
                            .Append(meth.IsStatic ? " static " : " ")
                            .Append(meth.ToDisplayString())
                            .Append('\n');
                        if (meth.Name is "Awake" or "OnEnable")
                        {
                            foreach (SyntaxReference r in meth.DeclaringSyntaxReferences)
                            {
                                if (r.GetSyntax(ct) is MethodDeclarationSyntax md)
                                {
                                    FileLinePositionSpan span = md.GetLocation().GetLineSpan();
                                    b.Append("  first ")
                                        .Append(span.StartLinePosition.Line)
                                        .Append(' ')
                                        .Append(
                                            md.ExpressionBody?.ToString()
                                                ?? md.Body?.Statements.FirstOrDefault()?.ToString()
                                        )
                                        .Append('\n');
                                }
                            }
                        }
                        break;
                }
            }
        }
        return b.ToString();
    }
}
