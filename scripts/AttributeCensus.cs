#:package Microsoft.CodeAnalysis.CSharp@5.6.0
#:property JsonSerializerIsReflectionEnabledByDefault=true

// Attribute census for the RazorGraph attribute-emission work (2026-08-17).
//
// Syntax-only: parses every .cs file, never compiles. That makes it fast enough
// to run over OrchardCore, and exact on the two things the design turns on --
// what an attribute is attached to, and what shape its arguments are. It is
// deliberately NOT exact on where an attribute TYPE is declared: with no
// semantic model, in-solution is approximated by matching against the
// *Attribute types declared inside the same corpus. That approximation is
// reported as such rather than smoothed over.

using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var corpora = new (string Name, string Root)[]
{
    // Scoped to src\ only: build-solution compiles the six projects in the .slnx,
    // and tests\fixtures\ is a sample solution that is deliberately not one of them.
    ("RazorGraphTool-src", @"D:\Repos\RazorGraphTool\src"),
};

var report = new List<object>();

foreach (var (name, root) in corpora)
{
    if (!Directory.Exists(root)) { Console.Error.WriteLine($"skip {name}: {root} missing"); continue; }

    var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)
                 && !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    var targetKinds = new Dictionary<string, int>(StringComparer.Ordinal);
    var attrNames = new Dictionary<string, int>(StringComparer.Ordinal);
    var argShapes = new Dictionary<string, int>(StringComparer.Ordinal);
    var paramNames = new Dictionary<string, int>(StringComparer.Ordinal);
    var typeofTargets = new Dictionary<string, int>(StringComparer.Ordinal);
    var declaredInCorpus = new HashSet<string>(StringComparer.Ordinal);

    int usages = 0, generatedFileUsages = 0;
    int withNoArgs = 0, withPositional = 0, withNamed = 0;
    int listsWithMultiple = 0, attributeLists = 0;
    int filesWithAny = 0, sourceFileCount = 0;

    foreach (var file in files)
    {
        string text;
        try { text = File.ReadAllText(file); } catch { continue; }
        sourceFileCount++;

        var tree = CSharpSyntaxTree.ParseText(text);
        var rootNode = tree.GetRoot();

        // Types the corpus declares itself, so "would this attribute resolve
        // inside the graph" has an answer that is at least corpus-local.
        foreach (var decl in rootNode.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            if (decl.Identifier.ValueText.EndsWith("Attribute", StringComparison.Ordinal))
                declaredInCorpus.Add(decl.Identifier.ValueText);

        var lists = rootNode.DescendantNodes().OfType<AttributeListSyntax>().ToArray();
        if (lists.Length == 0) continue;
        filesWithAny++;

        bool generated = file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                      || file.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
                      || file.Contains(@"\Generated\", StringComparison.OrdinalIgnoreCase);

        foreach (var list in lists)
        {
            attributeLists++;
            if (list.Attributes.Count > 1) listsWithMultiple++;

            foreach (var attr in list.Attributes)
            {
                usages++;
                if (generated) generatedFileUsages++;

                Bump(targetKinds, TargetKind(list));
                var simple = SimpleName(attr);
                Bump(attrNames, simple);
                if (TargetKind(list).StartsWith("parameter", StringComparison.Ordinal))
                    Bump(paramNames, simple);

                var attrArgs = attr.ArgumentList?.Arguments;
                if (attrArgs is null || attrArgs.Value.Count == 0) { withNoArgs++; continue; }

                bool anyPositional = false, anyNamed = false;
                foreach (var arg in attrArgs.Value)
                {
                    bool named = arg.NameEquals != null || arg.NameColon != null;
                    if (named) anyNamed = true; else anyPositional = true;

                    var shape = Shape(arg.Expression);
                    Bump(argShapes, (named ? "named/" : "positional/") + shape);

                    if (arg.Expression is TypeOfExpressionSyntax t)
                        Bump(typeofTargets, t.Type.ToString());
                }
                if (anyPositional) withPositional++;
                if (anyNamed) withNamed++;
            }
        }
    }

    int inCorpus = 0, notInCorpus = 0;
    foreach (var (n, c) in attrNames)
    {
        var full = n.EndsWith("Attribute", StringComparison.Ordinal) ? n : n + "Attribute";
        if (declaredInCorpus.Contains(full)) inCorpus += c; else notInCorpus += c;
    }

    report.Add(new
    {
        corpus = name,
        files = sourceFileCount,
        filesWithAttributes = filesWithAny,
        usages,
        attributeLists,
        listsWithMultipleAttributes = listsWithMultiple,
        distinctAttributeTypes = attrNames.Count,
        usagesInGeneratedFiles = generatedFileUsages,
        declaredInCorpus = declaredInCorpus.Count,
        usagesOfCorpusDeclaredAttributes = inCorpus,
        usagesOfExternalAttributes = notInCorpus,
        withNoArguments = withNoArgs,
        withPositionalArguments = withPositional,
        withNamedArguments = withNamed,
        targetKinds = Top(targetKinds, 40),
        topAttributes = Top(attrNames, 30),
        parameterAttributes = Top(paramNames, 25),
        argumentShapes = Top(argShapes, 40),
        topTypeofArguments = Top(typeofTargets, 15),
    });

    Console.Error.WriteLine($"{name}: {usages} usages over {sourceFileCount} files");
}

Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

static void Bump(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out var v) ? v + 1 : 1;

static Dictionary<string, int> Top(Dictionary<string, int> d, int n) =>
    d.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
     .Take(n).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

static string SimpleName(AttributeSyntax a)
{
    var n = a.Name switch
    {
        QualifiedNameSyntax q => q.Right.ToString(),
        GenericNameSyntax g => g.Identifier.ValueText + "<>",
        _ => a.Name.ToString()
    };
    return n;
}

// What the attribute is attached to. An explicit target specifier wins for
// assembly/module/return, because those say something the parent node cannot.
static string TargetKind(AttributeListSyntax list)
{
    var specifier = list.Target?.Identifier.ValueText;
    if (specifier is "assembly" or "module") return specifier;

    var owner = list.Parent;
    var kind = owner switch
    {
        ClassDeclarationSyntax => "class",
        StructDeclarationSyntax => "struct",
        RecordDeclarationSyntax r =>
            r.ClassOrStructKeyword.ValueText == "struct" ? "recordStruct" : "record",
        InterfaceDeclarationSyntax => "interface",
        EnumDeclarationSyntax => "enum",
        EnumMemberDeclarationSyntax => "enumMember",
        DelegateDeclarationSyntax => "delegate",
        MethodDeclarationSyntax => "method",
        ConstructorDeclarationSyntax => "constructor",
        DestructorDeclarationSyntax => "destructor",
        OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax => "operator",
        PropertyDeclarationSyntax => "property",
        IndexerDeclarationSyntax => "indexer",
        EventDeclarationSyntax or EventFieldDeclarationSyntax => "event",
        FieldDeclarationSyntax => "field",
        AccessorDeclarationSyntax acc => "accessor:" + acc.Keyword.ValueText,
        // Primary-constructor parameters are called out separately: they are the
        // ones the injection heuristic currently misreads as injected services.
        ParameterSyntax p => p.Parent?.Parent is TypeDeclarationSyntax
            ? "parameter:primaryCtor"
            : "parameter",
        TypeParameterSyntax => "typeParameter",
        LocalFunctionStatementSyntax => "localFunction",
        LocalDeclarationStatementSyntax => "local",
        LambdaExpressionSyntax => "lambda",
        _ => "other:" + (owner?.GetType().Name ?? "null")
    };

    return specifier == "return" ? kind + ":return" : kind;
}

// The argument's syntactic shape. This is the question that decides
// serialization: a string literal is a value, but typeof(Foo) is a reference to
// a type the graph may already hold as a node.
static string Shape(ExpressionSyntax e) => e switch
{
    LiteralExpressionSyntax l => "literal:" + l.Kind() switch
    {
        var k when k == SyntaxKind.StringLiteralExpression => "string",
        var k when k == SyntaxKind.NumericLiteralExpression => "number",
        var k when k == SyntaxKind.TrueLiteralExpression
                || k == SyntaxKind.FalseLiteralExpression => "bool",
        var k when k == SyntaxKind.NullLiteralExpression => "null",
        var k => k.ToString()
    },
    TypeOfExpressionSyntax => "typeof",
    InvocationExpressionSyntax i when i.Expression.ToString() == "nameof" => "nameof",
    InvocationExpressionSyntax => "invocation",
    MemberAccessExpressionSyntax => "memberAccess",
    IdentifierNameSyntax => "identifier",
    BinaryExpressionSyntax => "binary",
    PrefixUnaryExpressionSyntax => "unary",
    ArrayCreationExpressionSyntax or ImplicitArrayCreationExpressionSyntax => "array",
    CollectionExpressionSyntax => "collectionExpression",
    InterpolatedStringExpressionSyntax => "interpolatedString",
    ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax => "objectCreation",
    ConditionalExpressionSyntax => "conditional",
    ParenthesizedExpressionSyntax => "parenthesized",
    CastExpressionSyntax => "cast",
    _ => "other:" + e.GetType().Name
};
