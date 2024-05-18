using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

// ReSharper disable once CheckNamespace
public class EdgeCompiler: EdgeCompilerBase
{
    static EdgeCompiler()
    {
        Framework = "CLR";
    }
    
    public static void SetAssemblyLoader(Func<Stream, Assembly> assemblyLoader)
    {
        _assemblyLoader = assemblyLoader;
    }

    public new Func<object, Task<object>> CompileFunc(IDictionary<string, object> parameters, IDictionary<string, string> compileAssemblies)
    {
        return base.CompileFunc(parameters, compileAssemblies);
    }
    
    protected override bool TryCompile(string source, List<string> references, IDictionary<string, string> compileAssemblies, out string errors, out Assembly assembly)
    {
        assembly = null;
        errors = null;

        string projectDirectory = Environment.GetEnvironmentVariable("EDGE_APP_ROOT") ?? Directory.GetCurrentDirectory();

        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);
        List<MetadataReference> metadataReferences = new List<MetadataReference>();

        DebugMessage("EdgeCompiler::TryCompile (CLR) - Resolving {0} references", references.Count);

        // Search the NuGet package repository for each reference
        foreach (string reference in references)
        {
            DebugMessage("EdgeCompiler::TryCompile (CLR) - Searching for {0}", reference);

            // If the reference looks like a filename, try to load it directly; if we fail and the reference name does not contain a path separator (like
            // System.Data.dll), we fall back to stripping off the extension and treating the reference like a NuGet package
            if (reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                if (reference.Contains(Path.DirectorySeparatorChar.ToString()))
                {
                    metadataReferences.Add(MetadataReference.CreateFromFile(Path.IsPathRooted(reference)
                        ? reference
                        : Path.Combine(projectDirectory, reference)));
                    continue;
                }

                if (File.Exists(Path.Combine(projectDirectory, reference)))
                {
                    metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(projectDirectory, reference)));
                    continue;
                }
            }

            string referenceName = reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? reference.Substring(0, reference.Length - 4)
                : reference;

            if (!compileAssemblies.ContainsKey(referenceName))
            {
                throw new Exception($"Unable to resolve reference to {referenceName}.");
            }

            DebugMessage("EdgeCompiler::TryCompile (CLR) - Reference to {0} resolved to {1}", referenceName, compileAssemblies[referenceName]);
            metadataReferences.Add(MetadataReference.CreateFromFile(compileAssemblies[referenceName]));
            metadataReferences.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
            metadataReferences.Add(MetadataReference.CreateFromFile(typeof(System.Runtime.CompilerServices.DynamicAttribute).Assembly.Location));
        }

        CSharpCompilationOptions compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: DebuggingEnabled
            ? OptimizationLevel.Debug
            : OptimizationLevel.Release);

        DebugMessage("EdgeCompiler::TryCompile (CLR) - Starting compilation");

        CSharpCompilation compilation = CSharpCompilation.Create(Guid.NewGuid() + ".dll", new SyntaxTree[]
        {
            syntaxTree
        }, metadataReferences, compilationOptions);

        using (MemoryStream memoryStream = new MemoryStream())
        {
            EmitResult compilationResults = compilation.Emit(memoryStream);

            if (!compilationResults.Success)
            {
                IEnumerable<Diagnostic> failures =
                    compilationResults.Diagnostics.Where(
                        diagnostic =>
                            diagnostic.IsWarningAsError || diagnostic.Severity == DiagnosticSeverity.Error || diagnostic.Severity == DiagnosticSeverity.Warning);

                foreach (Diagnostic diagnostic in failures)
                {
                    if (errors == null)
                    {
                        errors = String.Format("{0}: {1}", diagnostic.Id, diagnostic.GetMessage());
                    }

                    else
                    {
                        errors += String.Format("\n{0}: {1}", diagnostic.Id, diagnostic.GetMessage());
                    }
                }

                DebugMessage("EdgeCompiler::TryCompile (CLR) - Compilation failed with the following errors: {0}{1}", Environment.NewLine, errors);
                return false;
            }

            memoryStream.Seek(0, SeekOrigin.Begin);
            assembly = _assemblyLoader(memoryStream);

            DebugMessage("EdgeCompiler::TryCompile (CLR) - Compilation completed successfully");
            return true;
        }
    }
}