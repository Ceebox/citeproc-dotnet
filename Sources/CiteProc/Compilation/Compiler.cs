using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace CiteProc.Compilation;

internal class Compiler : Snippet
{
    public const string CONTEXT_NAME = "c";
    public const string PARAMETER_NAME = "p";

    private readonly List<string> mUsings = new();
    private readonly List<Namespace> mNamespaces = new();
    private readonly Dictionary<string, string> mMacroMappings = new();
    private readonly Dictionary<string, Element> mSpecialElements = new();

    public Compiler() : base(null, null)
    {
        ParameterIndex = -1;
    }

    public int ParameterIndex { get; set; }

    public void AppendUsing(string @namespace) => mUsings.Add(@namespace);

    public Namespace AppendNamespace(string name)
    {
        Namespace result = new Namespace(this, name);
        mNamespaces.Add(result);
        return result;
    }

    public void RegisterMacros(IEnumerable<string> macros)
    {
        foreach (string macro in macros)
        {
            string name = string.Format("GenerateMacro{0:00}", mMacroMappings.Count);
            mMacroMappings.Add(macro, name);
        }
    }

    public string GetMacro(string name)
    {
        if (!mMacroMappings.ContainsKey(name))
        {
            throw new ArgumentOutOfRangeException(name, $"Macro '{name}' is not defined.");
        }
        return mMacroMappings[name];
    }

    public void SetSpecialElement<T>(T element, string name = "") where T : Element
    {
        string key = $"{typeof(T).FullName}::{name}";
        mSpecialElements.Add(key, element);
    }

    public T? GetSpecialElement<T>(string name = "") where T : Element
    {
        string key = $"{typeof(T).FullName}::{name}";
        return mSpecialElements.ContainsKey(key) ? (T)mSpecialElements[key] : null;
    }

    public static string GetLiteral(object value)
    {
        if (value == null)
        {
            return "null";
        }

        if (value is string s)
        {
            return $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t")}\"";
        }

        if (value is bool b)
        {
            return b ? "true" : "false";
        }

        if (value is int i)
        {
            return i.ToString();
        }

        if (value.GetType().IsEnum)
        {
            return $"{value.GetType().Name}.{value}";
        }

        throw new NotSupportedException();
    }

    public override void Render(CodeWriter writer)
    {
        foreach (string u in mUsings)
        {
            writer.AppendIndent();
            writer.Append($"using {u};\n");
        }

        writer.Append(Environment.NewLine);

        foreach (Namespace ns in mNamespaces)
        {
            ns.Render(writer);
        }
    }

    public Assembly Compile()
    {
        var sourceCode = ToString();
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);
        var assemblyName = $"CiteProc.Dynamic_{Guid.NewGuid():N}";

        Type[] referenceTypes = [typeof(string), typeof(Enumerable), typeof(Processor), typeof(object)];

        IEnumerable<MetadataReference> references = referenceTypes
            .Select(t => t.Assembly.Location)
            .Distinct()
            .Select(path => MetadataReference.CreateFromFile(path))
            .Concat(
            [
                MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll"))
            ]);

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Release));

        using MemoryStream ms = new MemoryStream();
        EmitResult result = compilation.Emit(ms);

        if (!result.Success)
        {
            IEnumerable<Diagnostic> failures = result.Diagnostics.Where(d => d.IsWarningAsError || d.Severity == DiagnosticSeverity.Error);
            string errorMsg = string.Join(Environment.NewLine, failures.Select(d => $"{d.Id}: {d.GetMessage()}"));
            throw new InvalidOperationException($"Compilation failed: {errorMsg}");
        }

        ms.Seek(0, SeekOrigin.Begin);
        return AssemblyLoadContext.Default.LoadFromStream(ms);
    }
}