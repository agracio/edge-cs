using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CSharp;

// ReSharper disable once CheckNamespace
public class EdgeCompiler: EdgeCompilerBase
{
    static EdgeCompiler()
    {
        Framework = ".NET";
        AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
    }

    static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
    {
        Assembly result = null;
        if (ReferencedAssemblies.TryGetValue(args.RequestingAssembly.FullName, out var requesting))
        {
            requesting.TryGetValue(args.Name, out result);
        }

        return result;
    }

    public static void SetAssemblyLoader(Func<Stream, Assembly> assemblyLoader)
    {
        _assemblyLoader = assemblyLoader;
    }
    
    public new Func<object, Task<object>> CompileFunc(IDictionary<string, object> parameters)
    {
        return base.CompileFunc(parameters);
    }
    
    protected override bool TryCompile(string source, List<string> references, out string errors, out Assembly assembly)
    {
        bool result = false;
        assembly = null;
        errors = null;

        Dictionary<string, string> options = new Dictionary<string, string> { { "CompilerVersion", "v4.0" } };
        CSharpCodeProvider csc = new CSharpCodeProvider(options);
        CompilerParameters parameters = new CompilerParameters();
        parameters.GenerateInMemory = true;
        parameters.IncludeDebugInformation = DebuggingEnabled;
        parameters.ReferencedAssemblies.AddRange(references.ToArray());
        parameters.ReferencedAssemblies.Add("System.dll");
        parameters.ReferencedAssemblies.Add("System.Core.dll");
        parameters.ReferencedAssemblies.Add("Microsoft.CSharp.dll");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EDGE_CS_TEMP_DIR")))
        {
            parameters.TempFiles = new TempFileCollection(Environment.GetEnvironmentVariable("EDGE_CS_TEMP_DIR"));
        }
        CompilerResults results = csc.CompileAssemblyFromSource(parameters, source);
        if (results.Errors.HasErrors)
        {
            foreach (CompilerError error in results.Errors)
            {
                if (errors == null)
                {
                    errors = error.ToString();
                }
                else
                {
                    errors += "\n" + error.ToString();
                }
            }
        }
        else
        {
            assembly = results.CompiledAssembly;
            result = true;
        }

        return result;
    }

}