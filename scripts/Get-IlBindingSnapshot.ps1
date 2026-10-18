<#
.SYNOPSIS
    Records what every method in a set of compiled assemblies binds to, so a
    second snapshot after a change can be compared against it.

.DESCRIPTION
    The first half of a "did my refactor change what anything binds to" check;
    Compare-IlBindingSnapshot.ps1 is the second half. Take a snapshot of a build
    before the change, another after it, and compare the two.

    WHY IL AND NOT SOURCE. A change that should be behaviour-neutral -- adding a
    global using, moving usings, renaming a namespace, adding an overload or an
    extension method somewhere else -- can make overload resolution pick a
    different target without any error or warning. Source still reads the same;
    only the compiled call site says where it now goes. Measured case: with
    'using A;' in one file, adding 'global using B;' elsewhere moved
    "x".Hi() from A.AExt::Hi(Object) to B.BExt::Hi(String), and the build was
    green.

    Two levels of detail, chosen with -Detail:

    CallTargets (default) -- for every method body, the ordered list of the
    targets of call / callvirt / newobj / ldftn / ldvirtftn, resolved to
    Type::Name(params):return. This is the cheap view and catches every method
    rebind, extension methods and constructors included.

    Full -- adds, per method, every token operand in order (field access,
    casts, isinst, box, typeof via ldtoken, newarr, initobj, constrained. ...),
    the local variable types, the catch types of exception regions, and a
    SHA-256 of the body with every metadata token replaced by the name it
    resolves to. Per assembly it adds every TypeRef and MemberRef row and every
    custom attribute (owner, constructor, value blob). Use it when the change
    could move a TYPE rather than a method: CallTargets cannot see typeof, a
    cast, a field, a catch clause or an attribute binding elsewhere, and the
    body hash catches what no operand shows, such as a const whose value
    changed because a different constant now binds.

    The body hash is NORMALISED rather than raw. A raw hash includes metadata
    row numbers, which move whenever any reference is added anywhere earlier in
    the table; a normalised one changes only when what the body says changes.

    Reads metadata with System.Reflection.Metadata, which ships inside
    PowerShell 7 -- nothing to install, and nothing is loaded for execution:
    the assemblies are parsed as files and their dependencies are never needed.

.PARAMETER Path
    One or more assembly files or folders. A folder contributes the files
    matching -Include (non-recursive unless -Recurse). Files without metadata
    (native DLLs, apphost .exe files) and reference assemblies are skipped and
    listed under 'skipped' in the result.

.PARAMETER Include
    File-name wildcards used when a -Path entry is a folder. Default *.dll and
    *.exe. Narrow it (for example 'MyApp*.dll') to keep NuGet and framework
    DLLs that sit beside the build out of the snapshot.

.PARAMETER Recurse
    Scan folders recursively. Paths under obj\, ref\ and refint\ directories are
    skipped, because those hold intermediate and reference copies of the same
    assemblies.

.PARAMETER Solution
    A .sln, .slnx or single project file. Builds it (unless -NoBuild) with the
    given -Configuration, then snapshots each project's own output assembly
    (MSBuild TargetPath), one per target framework for multi-targeted projects.
    Dependencies copied into bin are not included.

.PARAMETER Configuration
    Build configuration for -Solution. Default Debug, dotnet's own default.
    Before and after must use the same one: Debug and Release IL differ.

.PARAMETER NoBuild
    With -Solution: do not build; snapshot the outputs already on disk. A
    missing output fails the run rather than being skipped.

.PARAMETER BuildTimeoutSec
    With -Solution: kill the build after this many seconds. Default 1200.

.PARAMETER OutFile
    Where to write the snapshot (JSON). Required -- a snapshot of a real
    solution runs to megabytes and does not belong on stdout.

.PARAMETER Detail
    CallTargets (default) or Full. See the description. Compare only snapshots
    taken at the same detail.

.PARAMETER Text
    Formatted summary for a terminal. The default is a JSON envelope.

.PARAMETER Pretty
    Indent the JSON envelope (the snapshot file itself is always compact).

.EXAMPLE
    & "$env:JanetBase\scripts\Get-IlBindingSnapshot.ps1" -Solution .\App.slnx -Configuration Release -OutFile $env:TEMP\before.json -Text
    # ...make the change...
    & "$env:JanetBase\scripts\Get-IlBindingSnapshot.ps1" -Solution .\App.slnx -Configuration Release -OutFile $env:TEMP\after.json -Text
    & "$env:JanetBase\scripts\Compare-IlBindingSnapshot.ps1" -Before $env:TEMP\before.json -After $env:TEMP\after.json -Text

.EXAMPLE
    & "$env:JanetBase\scripts\Get-IlBindingSnapshot.ps1" -Path .\bin\Release\net10.0 -Include 'MyApp*.dll' -Detail Full -OutFile before.json
#>
[CmdletBinding(DefaultParameterSetName = 'Path')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Path')]
    [string[]]$Path,

    [Parameter(ParameterSetName = 'Path')]
    [string[]]$Include = @('*.dll', '*.exe'),

    [Parameter(ParameterSetName = 'Path')]
    [switch]$Recurse,

    [Parameter(Mandatory = $true, ParameterSetName = 'Solution')]
    [string]$Solution,

    [Parameter(ParameterSetName = 'Solution')]
    [string]$Configuration = 'Debug',

    [Parameter(ParameterSetName = 'Solution')]
    [switch]$NoBuild,

    [Parameter(ParameterSetName = 'Solution')]
    [int]$BuildTimeoutSec = 1200,

    [Parameter(Mandatory = $true)]
    [string]$OutFile,

    [ValidateSet('CallTargets', 'Full')]
    [string]$Detail = 'CallTargets',

    [switch]$Text,

    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\lib\Invoke-External.ps1"

$snapshotSource = @'
#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JanetIlBinding
{
    public sealed class MethodRecord
    {
        public string Id { get; set; } = "";
        public List<string> Calls { get; set; } = new List<string>();
        public List<string>? Operands { get; set; }
        public string? BodyHash { get; set; }
    }

    public sealed class AssemblyRecord
    {
        public string Name { get; set; } = "";
        public string File { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string Mvid { get; set; } = "";
        public int CallSites { get; set; }
        public List<string> AssemblyReferences { get; set; } = new List<string>();
        public List<string>? References { get; set; }
        public List<string>? Attributes { get; set; }
        public List<MethodRecord> Methods { get; set; } = new List<MethodRecord>();
    }

    public sealed class SnapshotDocument
    {
        public string Format { get; set; } = "il-binding-snapshot";
        public int Version { get; set; } = 1;
        public string Detail { get; set; } = "CallTargets";
        public string Created { get; set; } = "";
        public Dictionary<string, string> Source { get; set; } = new Dictionary<string, string>();
        public List<AssemblyRecord> Assemblies { get; set; } = new List<AssemblyRecord>();

        public void Save(string path)
        {
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            File.WriteAllText(path, JsonSerializer.Serialize(this, options), new UTF8Encoding(false));
        }
    }

    public static class Snapshotter
    {
        static readonly Dictionary<int, OpCode> OpCodesByValue = BuildOpCodeTable();

        static Dictionary<int, OpCode> BuildOpCodeTable()
        {
            Dictionary<int, OpCode> table = new Dictionary<int, OpCode>();
            foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.GetValue(null) is OpCode o) { table[(ushort)o.Value] = o; }
            }
            return table;
        }

        sealed class Names : ISignatureTypeProvider<string, object?>
        {
            public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', s.Rank - 1) + "]";
            public string GetByReferenceType(string e) => e + "&";
            public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr(" + string.Join(",", s.ParameterTypes) + "):" + s.ReturnType;
            public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
            public string GetGenericMethodParameter(object? c, int i) => "!!" + i;
            public string GetGenericTypeParameter(object? c, int i) => "!" + i;
            public string GetModifiedType(string m, string u, bool r) => u;
            public string GetPinnedType(string e) => e + " pinned";
            public string GetPointerType(string e) => e + "*";
            public string GetPrimitiveType(PrimitiveTypeCode t) => t.ToString();
            public string GetSZArrayType(string e) => e + "[]";
            public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => DefName(r, h);
            public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => RefName(r, h, false);
            public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) => r.GetTypeSpecification(h).DecodeSignature(this, c);
        }

        static string DefName(MetadataReader r, TypeDefinitionHandle h)
        {
            TypeDefinition t = r.GetTypeDefinition(h);
            TypeDefinitionHandle d = t.GetDeclaringType();
            string n = r.GetString(t.Name);
            return d.IsNil ? r.GetString(t.Namespace) + "." + n : DefName(r, d) + "/" + n;
        }

        // Scoped names carry the assembly a TypeRef resolves into, so a type that moves
        // between two assemblies under the same full name still shows as a change.
        static string RefName(MetadataReader r, TypeReferenceHandle h, bool scoped)
        {
            TypeReference t = r.GetTypeReference(h);
            string n = r.GetString(t.Name);
            if (t.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                return RefName(r, (TypeReferenceHandle)t.ResolutionScope, scoped) + "/" + n;
            }
            string full = r.GetString(t.Namespace) + "." + n;
            if (scoped && t.ResolutionScope.Kind == HandleKind.AssemblyReference)
            {
                AssemblyReference a = r.GetAssemblyReference((AssemblyReferenceHandle)t.ResolutionScope);
                return "[" + r.GetString(a.Name) + "]" + full;
            }
            return full;
        }

        static string TypeName(MetadataReader r, EntityHandle p, Names np) => p.Kind switch
        {
            HandleKind.TypeDefinition => DefName(r, (TypeDefinitionHandle)p),
            HandleKind.TypeReference => RefName(r, (TypeReferenceHandle)p, true),
            HandleKind.TypeSpecification => r.GetTypeSpecification((TypeSpecificationHandle)p).DecodeSignature(np, null),
            HandleKind.MethodDefinition => "method",
            HandleKind.ModuleReference => "module",
            _ => p.Kind.ToString()
        };

        static string Member(MetadataReader r, EntityHandle h, Names np)
        {
            switch (h.Kind)
            {
                case HandleKind.MethodDefinition:
                {
                    MethodDefinition m = r.GetMethodDefinition((MethodDefinitionHandle)h);
                    MethodSignature<string> s = m.DecodeSignature(np, null);
                    return DefName(r, m.GetDeclaringType()) + "::" + r.GetString(m.Name) + "(" + string.Join(",", s.ParameterTypes) + "):" + s.ReturnType;
                }
                case HandleKind.MemberReference:
                {
                    MemberReference m = r.GetMemberReference((MemberReferenceHandle)h);
                    string head = TypeName(r, m.Parent, np) + "::" + r.GetString(m.Name);
                    if (m.GetKind() == MemberReferenceKind.Field) { return head + ":" + m.DecodeFieldSignature(np, null); }
                    MethodSignature<string> s = m.DecodeMethodSignature(np, null);
                    return head + "(" + string.Join(",", s.ParameterTypes) + "):" + s.ReturnType;
                }
                case HandleKind.MethodSpecification:
                {
                    MethodSpecification ms = r.GetMethodSpecification((MethodSpecificationHandle)h);
                    return Member(r, ms.Method, np) + "<" + string.Join(",", ms.DecodeSignature(np, null)) + ">";
                }
                case HandleKind.FieldDefinition:
                {
                    FieldDefinition f = r.GetFieldDefinition((FieldDefinitionHandle)h);
                    return DefName(r, f.GetDeclaringType()) + "::" + r.GetString(f.Name) + ":" + f.DecodeSignature(np, null);
                }
                case HandleKind.TypeDefinition:
                case HandleKind.TypeReference:
                case HandleKind.TypeSpecification:
                    return TypeName(r, h, np);
                default:
                    return h.Kind.ToString();
            }
        }

        static bool IsCall(int op) => op == 0x28 || op == 0x6F || op == 0x73 || op == 0xFE06 || op == 0xFE07;

        static string Hex(byte[] bytes) => bytes.Length <= 64
            ? Convert.ToHexString(bytes)
            : "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).Substring(0, 16);

        /// <summary>Returns null when the file can be snapshotted, otherwise the reason it cannot.</summary>
        public static string? Probe(string path)
        {
            using FileStream fs = File.OpenRead(path);
            using PEReader pe = new PEReader(fs);
            if (!pe.HasMetadata) { return "no metadata (native image)"; }
            MetadataReader r = pe.GetMetadataReader();
            if (!r.IsAssembly) { return "module without an assembly manifest"; }
            foreach (CustomAttributeHandle ch in r.GetAssemblyDefinition().GetCustomAttributes())
            {
                EntityHandle ctor = r.GetCustomAttribute(ch).Constructor;
                if (ctor.Kind != HandleKind.MemberReference) { continue; }
                EntityHandle parent = r.GetMemberReference((MemberReferenceHandle)ctor).Parent;
                if (parent.Kind == HandleKind.TypeReference && r.GetString(r.GetTypeReference((TypeReferenceHandle)parent).Name) == "ReferenceAssemblyAttribute")
                {
                    return "reference assembly (bodies are stubs)";
                }
            }
            return null;
        }

        public static AssemblyRecord Read(string path, bool full)
        {
            Names np = new Names();
            byte[] fileBytes = File.ReadAllBytes(path);
            using PEReader pe = new PEReader(ImmutableArray.Create(fileBytes));
            MetadataReader r = pe.GetMetadataReader();
            AssemblyRecord record = new AssemblyRecord
            {
                Name = r.GetString(r.GetAssemblyDefinition().Name),
                File = path,
                Sha256 = Convert.ToHexString(SHA256.HashData(fileBytes)),
                Mvid = r.GetGuid(r.GetModuleDefinition().Mvid).ToString()
            };
            foreach (AssemblyReferenceHandle ah in r.AssemblyReferences)
            {
                AssemblyReference a = r.GetAssemblyReference(ah);
                record.AssemblyReferences.Add(r.GetString(a.Name) + ", " + a.Version);
            }
            record.AssemblyReferences.Sort(StringComparer.Ordinal);

            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (MethodDefinitionHandle mh in r.MethodDefinitions)
            {
                MethodDefinition md = r.GetMethodDefinition(mh);
                if (md.RelativeVirtualAddress == 0) { continue; }
                string id = Member(r, mh, np);
                // Overloads that differ only in generic arity render alike; keep both, in order.
                int count = seen.TryGetValue(id, out int prior) ? prior + 1 : 1;
                seen[id] = count;
                if (count > 1) { id += " #" + count; }
                MethodRecord m = ReadBody(r, pe.GetMethodBody(md.RelativeVirtualAddress), id, full, np);
                record.CallSites += m.Calls.Count;
                record.Methods.Add(m);
            }
            record.Methods.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));

            if (full)
            {
                record.References = ReadReferences(r, np);
                record.Attributes = ReadAttributes(r, np);
            }
            return record;
        }

        static MethodRecord ReadBody(MetadataReader r, MethodBodyBlock body, string id, bool full, Names np)
        {
            MethodRecord m = new MethodRecord { Id = id };
            List<string>? operands = full ? new List<string>() : null;
            StringBuilder canonical = new StringBuilder();
            if (full && !body.LocalSignature.IsNil)
            {
                ImmutableArray<string> locals = r.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(np, null);
                string line = "locals (" + string.Join(",", locals) + ")";
                operands!.Add(line);
                canonical.Append(line).Append('\n');
            }
            BlobReader il = body.GetILReader();
            while (il.RemainingBytes > 0)
            {
                int start = il.Offset;
                int code = il.ReadByte();
                if (code == 0xFE) { code = 0xFE00 | il.ReadByte(); }
                if (!OpCodesByValue.TryGetValue(code, out OpCode oc))
                {
                    throw new BadImageFormatException("unknown opcode 0x" + code.ToString("X") + " at IL offset " + start + " in " + id);
                }
                canonical.Append(oc.Name);
                switch (oc.OperandType)
                {
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineTok:
                    case OperandType.InlineType:
                    {
                        string target = Member(r, MetadataTokens.EntityHandle(il.ReadInt32()), np);
                        if (IsCall(code)) { m.Calls.Add(target); }
                        operands?.Add(oc.Name + " " + target);
                        canonical.Append(' ').Append(target);
                        break;
                    }
                    case OperandType.InlineSig:
                    {
                        StandaloneSignatureHandle sh = (StandaloneSignatureHandle)MetadataTokens.EntityHandle(il.ReadInt32());
                        MethodSignature<string> s = r.GetStandaloneSignature(sh).DecodeMethodSignature(np, null);
                        string sig = "(" + string.Join(",", s.ParameterTypes) + "):" + s.ReturnType;
                        operands?.Add(oc.Name + " " + sig);
                        canonical.Append(' ').Append(sig);
                        break;
                    }
                    case OperandType.InlineString:
                    {
                        string literal = r.GetUserString((UserStringHandle)MetadataTokens.Handle(il.ReadInt32()));
                        canonical.Append(" \"").Append(literal.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                        break;
                    }
                    case OperandType.InlineSwitch:
                    {
                        int n = il.ReadInt32();
                        canonical.Append(' ').Append(n);
                        for (int i = 0; i < n; i++) { canonical.Append(' ').Append(il.ReadInt32()); }
                        break;
                    }
                    default:
                    {
                        int size = OperandSize(oc.OperandType);
                        if (size > 0) { canonical.Append(' ').Append(Convert.ToHexString(il.ReadBytes(size))); }
                        break;
                    }
                }
                canonical.Append('\n');
            }
            if (full)
            {
                foreach (ExceptionRegion er in body.ExceptionRegions)
                {
                    string region = er.Kind.ToString().ToLowerInvariant() + " " + er.TryOffset + "+" + er.TryLength + " " + er.HandlerOffset + "+" + er.HandlerLength;
                    if (er.Kind == ExceptionRegionKind.Catch) { region += " " + Member(r, er.CatchType, np); }
                    operands!.Add(region);
                    canonical.Append(region).Append('\n');
                }
                m.Operands = operands;
                m.BodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
            }
            return m;
        }

        static int OperandSize(OperandType t) => t switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget => 1,
            OperandType.ShortInlineI => 1,
            OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI8 => 8,
            OperandType.InlineR => 8,
            _ => 4
        };

        static List<string> ReadReferences(MetadataReader r, Names np)
        {
            List<string> refs = new List<string>();
            foreach (TypeReferenceHandle h in r.TypeReferences) { refs.Add("typeref " + RefName(r, h, true)); }
            foreach (MemberReferenceHandle h in r.MemberReferences) { refs.Add("memberref " + Member(r, h, np)); }
            refs.Sort(StringComparer.Ordinal);
            return refs;
        }

        static List<string> ReadAttributes(MetadataReader r, Names np)
        {
            Dictionary<EntityHandle, string> owners = new Dictionary<EntityHandle, string>();
            foreach (TypeDefinitionHandle th in r.TypeDefinitions)
            {
                TypeDefinition t = r.GetTypeDefinition(th);
                string tn = DefName(r, th);
                foreach (PropertyDefinitionHandle ph in t.GetProperties()) { owners[ph] = "property " + tn + "::" + r.GetString(r.GetPropertyDefinition(ph).Name); }
                foreach (EventDefinitionHandle eh in t.GetEvents()) { owners[eh] = "event " + tn + "::" + r.GetString(r.GetEventDefinition(eh).Name); }
            }
            foreach (MethodDefinitionHandle mh in r.MethodDefinitions)
            {
                string mn = Member(r, mh, np);
                foreach (ParameterHandle ph in r.GetMethodDefinition(mh).GetParameters()) { owners[ph] = "param " + mn + " #" + r.GetParameter(ph).SequenceNumber; }
            }
            List<string> lines = new List<string>();
            foreach (CustomAttributeHandle ch in r.CustomAttributes)
            {
                CustomAttribute ca = r.GetCustomAttribute(ch);
                string owner = Owner(r, ca.Parent, owners, np);
                lines.Add(owner + " => " + Member(r, ca.Constructor, np) + " " + Hex(r.GetBlobBytes(ca.Value)));
            }
            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        static string Owner(MetadataReader r, EntityHandle p, Dictionary<EntityHandle, string> owners, Names np)
        {
            if (owners.TryGetValue(p, out string? known)) { return known; }
            switch (p.Kind)
            {
                case HandleKind.TypeDefinition: return "type " + DefName(r, (TypeDefinitionHandle)p);
                case HandleKind.MethodDefinition: return "method " + Member(r, p, np);
                case HandleKind.FieldDefinition: return "field " + Member(r, p, np);
                case HandleKind.AssemblyDefinition: return "assembly";
                case HandleKind.ModuleDefinition: return "module";
                case HandleKind.GenericParameter:
                {
                    GenericParameter g = r.GetGenericParameter((GenericParameterHandle)p);
                    string parent = g.Parent.Kind == HandleKind.TypeDefinition ? DefName(r, (TypeDefinitionHandle)g.Parent) : Member(r, g.Parent, np);
                    return "genericparam " + parent + " <" + r.GetString(g.Name) + ">";
                }
                default: return p.Kind.ToString();
            }
        }
    }
}
'@

Add-Type -TypeDefinition $snapshotSource -ReferencedAssemblies @(
    'System.Runtime', 'System.Collections', 'System.Collections.Immutable', 'System.Linq',
    'System.Reflection.Metadata', 'System.Reflection.Primitives', 'System.Security.Cryptography',
    'System.Text.Json', 'System.Text.Encodings.Web', 'System.Text.Encoding.Extensions', 'System.Memory',
    'System.Runtime.Extensions')

function Get-ProjectList {
    # ASSIGN THE RESULT: returns a comma-wrapped array of project paths.
    param([string]$SolutionPath)

    $extension = [System.IO.Path]::GetExtension($SolutionPath).ToLowerInvariant()
    if ($extension -notin @('.sln', '.slnx', '.slnf')) { return , @($SolutionPath) }

    $listed = Invoke-External -NoThrow -Quiet dotnet sln $SolutionPath list
    if (-not $listed.Success) {
        throw "dotnet sln list failed for ${SolutionPath}: $($listed.StdErr -join ' ') $($listed.StdOut -join ' ')"
    }
    $root = Split-Path $SolutionPath -Parent
    $projects = @($listed.StdOut | Where-Object { $_ -match '\.(cs|vb|fs)proj\s*$' } |
            ForEach-Object { [System.IO.Path]::GetFullPath((Join-Path $root $_.Trim())) })
    return , $projects
}

function Get-ProjectOutput {
    # ASSIGN THE RESULT: returns a comma-wrapped array of TargetPath values, one per
    # target framework.
    param([string]$ProjectPath, [string]$BuildConfiguration)

    # The -getProperty switches are quoted: bare, PowerShell binds 'name:value' tokens as
    # a parameter and its argument and passes them on as two words, which MSBuild rejects.
    $query = Invoke-External -NoThrow -Quiet dotnet msbuild $ProjectPath '-nologo' "-p:Configuration=$BuildConfiguration" '-getProperty:TargetPath' '-getProperty:TargetFrameworks'
    if (-not $query.Success) {
        throw "Could not evaluate ${ProjectPath}: $($query.StdOut -join ' ') $($query.StdErr -join ' ')"
    }
    $properties = ($query.StdOut -join "`n" | ConvertFrom-Json).Properties
    if (-not [string]::IsNullOrWhiteSpace($properties.TargetPath)) { return , @($properties.TargetPath) }

    $outputs = foreach ($framework in @($properties.TargetFrameworks -split ';' | Where-Object { $_ })) {
        $one = Invoke-External -NoThrow -Quiet dotnet msbuild $ProjectPath '-nologo' "-p:Configuration=$BuildConfiguration" "-p:TargetFramework=$framework" '-getProperty:TargetPath'
        if (-not $one.Success) { throw "Could not evaluate $ProjectPath for ${framework}: $($one.StdOut -join ' ')" }
        ($one.StdOut -join '').Trim()
    }
    return , @($outputs)
}

function Get-FolderAssembly {
    # ASSIGN THE RESULT: returns a comma-wrapped array of file paths.
    param([string]$Folder, [string[]]$Patterns, [bool]$Deep)

    $intermediate = '\\(obj|ref|refint)\\'
    $found = @(Get-ChildItem -LiteralPath $Folder -File -Recurse:$Deep |
            Where-Object {
                $name = $_.Name
                ($Patterns | Where-Object { $name -like $_ }) -and
                -not ($Deep -and $_.FullName.Substring($Folder.Length) -match $intermediate)
            } |
            ForEach-Object FullName | Sort-Object)
    return , $found
}

$result = [ordered]@{
    ok         = $false
    outFile    = $null
    detail     = $Detail
    assemblies = 0
    methods    = 0
    callSites  = 0
    skipped    = @()
    error      = $null
}

try {
    $document = [JanetIlBinding.SnapshotDocument]::new()
    $document.Detail = $Detail
    $document.Created = (Get-Date).ToString('o')
    $sdk = Invoke-External -NoThrow -Quiet dotnet --version
    if ($sdk.Success) { $document.Source['sdk'] = ($sdk.StdOut -join '').Trim() }

    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($PSCmdlet.ParameterSetName -eq 'Solution') {
        $solutionPath = [System.IO.Path]::GetFullPath($Solution)
        if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) { throw "Solution or project not found: $Solution" }
        $document.Source['solution'] = $solutionPath
        $document.Source['configuration'] = $Configuration

        if (-not $NoBuild) {
            $build = Invoke-External -NoThrow -Quiet -TimeoutSec $BuildTimeoutSec dotnet build $solutionPath '-c' $Configuration '-nologo'
            if (-not $build.Success) {
                $errors = @($build.StdOut | Where-Object { $_ -match ': error ' } | Select-Object -First 5)
                $why = if ($build.TimedOut) { "timed out after ${BuildTimeoutSec}s" } else { "exited $($build.ExitCode)" }
                throw "Build $why. $($errors -join ' | ')"
            }
        }
        foreach ($project in (Get-ProjectList -SolutionPath $solutionPath)) {
            foreach ($output in (Get-ProjectOutput -ProjectPath $project -BuildConfiguration $Configuration)) {
                if (-not (Test-Path -LiteralPath $output -PathType Leaf)) { throw "Build output missing: $output (from $project)" }
                $candidates.Add($output)
            }
        }
    }
    else {
        $document.Source['path'] = ($Path | ForEach-Object { [System.IO.Path]::GetFullPath($_) }) -join ';'
        foreach ($entry in $Path) {
            $full = [System.IO.Path]::GetFullPath($entry)
            if (Test-Path -LiteralPath $full -PathType Container) {
                foreach ($file in (Get-FolderAssembly -Folder $full -Patterns $Include -Deep $Recurse.IsPresent)) { $candidates.Add($file) }
            }
            elseif (Test-Path -LiteralPath $full -PathType Leaf) { $candidates.Add($full) }
            else { throw "Path not found: $entry" }
        }
    }

    $skipped = [System.Collections.Generic.List[object]]::new()
    $byName = @{}
    foreach ($file in $candidates) {
        $reason = [JanetIlBinding.Snapshotter]::Probe($file)
        if ($null -ne $reason) {
            $skipped.Add([ordered]@{ file = $file; reason = $reason })
            continue
        }
        $record = [JanetIlBinding.Snapshotter]::Read($file, $Detail -eq 'Full')
        if ($byName.ContainsKey($record.Name)) {
            $first = $byName[$record.Name]
            if ($first.Sha256 -eq $record.Sha256) {
                $skipped.Add([ordered]@{ file = $file; reason = "identical copy of $($first.File)" })
                continue
            }
            # Same assembly name, different content: usually one per target framework.
            # Key it by the folder it sits in so both survive and still pair up later.
            $record.Name = "$($record.Name) ($(Split-Path (Split-Path $file -Parent) -Leaf))"
            if ($byName.ContainsKey($record.Name)) { throw "Two different assemblies would both be keyed '$($record.Name)': $($byName[$record.Name].File) and $file" }
        }
        $byName[$record.Name] = $record
        $document.Assemblies.Add($record)
    }
    if ($document.Assemblies.Count -eq 0) {
        throw "No assemblies to snapshot ($($candidates.Count) candidate files, $($skipped.Count) skipped)."
    }

    $destination = [System.IO.Path]::GetFullPath($OutFile)
    $destinationDirectory = Split-Path $destination -Parent
    if (-not (Test-Path -LiteralPath $destinationDirectory)) { New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null }
    $document.Save($destination)

    $result.outFile = $destination
    $result.assemblies = $document.Assemblies.Count
    $result.methods = [int]($document.Assemblies | Measure-Object -Property { $_.Methods.Count } -Sum).Sum
    $result.callSites = [int]($document.Assemblies | Measure-Object -Property CallSites -Sum).Sum
    $result.skipped = $skipped.ToArray()
    $result.ok = $true
}
catch {
    $result.error = $_.Exception.Message
}

if (-not $Text) {
    if ($Pretty) { [pscustomobject]$result | ConvertTo-Json -Depth 4 }
    else { [pscustomobject]$result | ConvertTo-Json -Depth 4 -Compress }
    if ($result.ok) { exit 0 }
    exit 1
}

# -Text goes to the output stream, not the host, so a caller can capture it.
if (-not $result.ok) {
    Write-Output "snapshot failed: $($result.error)"
    exit 1
}
Write-Output "$($result.callSites) call sites in $($result.methods) methods from $($result.assemblies) assemblies ($Detail) -> $($result.outFile)"
foreach ($skip in $result.skipped) { Write-Output "  skipped $($skip.file): $($skip.reason)" }
exit 0
