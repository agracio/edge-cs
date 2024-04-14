using System;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CSharp;

// ReSharper disable once CheckNamespace
public class EdgeCompiler
{
    private static readonly Regex ReferenceRegex = new Regex(@"^[\ \t]*(?:\/{2})?\#r[\ \t]+""([^""]+)""", RegexOptions.Multiline);
    private static readonly Regex UsingRegex = new Regex(@"^[\ \t]*(using[\ \t]+[^\ \t]+[\ \t]*\;)", RegexOptions.Multiline);
    private static readonly bool DebuggingEnabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EDGE_CS_DEBUG"));
    private static readonly bool CacheEnabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EDGE_CS_CACHE"));
    private static readonly ConcurrentDictionary<string, Func<object, Task<object>>> FuncCache = new ConcurrentDictionary<string, Func<object, Task<object>>>();
    private static readonly Dictionary<string, Dictionary<string, Assembly>> ReferencedAssemblies = new Dictionary<string, Dictionary<string, Assembly>>();
    private static Func<Stream, Assembly> _assemblyLoader;
    
    static EdgeCompiler()
    {
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

    public void DebugMessage(string format, params object[] args)
    {
        if (DebuggingEnabled)
        {
            Console.WriteLine(format, args);
        }
    }
    public Func<object, Task<object>> CompileFunc(IDictionary<string, object> parameters)
    {
        DebugMessage("EdgeCompiler::CompileFunc (.NET) - Starting");
        
        string source = (string)parameters["source"];
        string lineDirective = string.Empty;
        string fileName = null;
        int lineNumber = 1;

        // read source from file
        if (source.EndsWith(".cs", StringComparison.InvariantCultureIgnoreCase)
            || source.EndsWith(".csx", StringComparison.InvariantCultureIgnoreCase))
        {
            // retain fileName for debugging purposes
            if (DebuggingEnabled)
            {
                fileName = source;
            }

            source = File.ReadAllText(source);
        }

        DebugMessage("EdgeCompiler::CompileFunc (.NET) - Func cache size: {0}", FuncCache.Count);

        var originalSource = source;
        if (FuncCache.ContainsKey(originalSource))
        {
            DebugMessage("EdgeCompiler::CompileFunc (.NET) - Serving func from cache");

            return FuncCache[originalSource];
        }
        DebugMessage("EdgeCompiler::CompileFunc (.NET) - Func not found in cache, compiling");

        // add assembly references provided explicitly through parameters
        List<string> references = new List<string>();
        object v;
        if (parameters.TryGetValue("references", out v))
        {
            foreach (object reference in (object[])v)
            {
                references.Add((string)reference);
            }
        }

        // add assembly references provided in code as [//]#r "assemblyname" lines
        Match match = ReferenceRegex.Match(source);
        while (match.Success)
        {
            references.Add(match.Groups[1].Value);
            source = source.Substring(0, match.Index) + source.Substring(match.Index + match.Length);
            match = ReferenceRegex.Match(source);
        }

        if (DebuggingEnabled)
        {
            object jsFileName;
            if (parameters.TryGetValue("jsFileName", out jsFileName))
            {
                fileName = (string)jsFileName;
                lineNumber = (int)parameters["jsLineNumber"];
            }
            
            if (!string.IsNullOrEmpty(fileName)) 
            {
                lineDirective = string.Format("#line {0} \"{1}\"\n", lineNumber, fileName);
            }
        }

        // try to compile source code as a class library
        Assembly assembly;
        string errorsClass;
        if (!this.TryCompile(lineDirective + source, references, out errorsClass, out assembly))
        {
            // try to compile source code as an async lambda expression

            // extract using statements first
            string usings = "";
            match = UsingRegex.Match(source);
            while (match.Success)
            {
                usings += match.Groups[1].Value;
                source = source.Substring(0, match.Index) + source.Substring(match.Index + match.Length);
                match = UsingRegex.Match(source);
            }

            string errorsLambda;
            source = usings + @"
                using System;
                using System.Threading.Tasks;

                public class Startup 
                {
                    public async Task<object> Invoke(object ___input) 
                    {
                " + lineDirective + @"
                        Func<object, Task<object>> func = " + source + @";
                #line hidden
                        return await func(___input);
                    }
                }";

            DebugMessage("EdgeCompiler::CompileFunc (.NET) - Trying to compile async lambda expression:{0}{1}", Environment.NewLine, source);

            if (!TryCompile(source, references, out errorsLambda, out assembly))
            {
                throw new InvalidOperationException(
                    "Unable to compile C# code.\n----> Errors when compiling as a CLR library:\n"
                    + errorsClass
                    + "\n----> Errors when compiling as a CLR async lambda expression:\n"
                    + errorsLambda);
            }
        }

        // store referenced assemblies to help resolve them at runtime from AppDomain.AssemblyResolve
        ReferencedAssemblies[assembly.FullName] = new Dictionary<string, Assembly>();
        foreach (var reference in references)
        {
            try
            {
                var referencedAssembly = Assembly.UnsafeLoadFrom(reference);
                ReferencedAssemblies[assembly.FullName][referencedAssembly.FullName] = referencedAssembly;
            }
            catch
            {
                // empty - best effort
            }
        }

        // extract the entry point to a class method
        Type startupType = assembly.GetType((string)parameters["typeName"], true, true);
        object instance = Activator.CreateInstance(startupType, false);
        MethodInfo invokeMethod = startupType.GetMethod((string)parameters["methodName"], BindingFlags.Instance | BindingFlags.Public);
        if (invokeMethod == null)
        {
            throw new InvalidOperationException("Unable to access CLR method to wrap through reflection. Make sure it is a public instance method.");
        }

        // create a Func<object,Task<object>> delegate around the method invocation using reflection
        Func<object,Task<object>> result = (input) => 
        {
            return (Task<object>)invokeMethod.Invoke(instance, new object[] { input });
        };

        if (CacheEnabled)
        {
            FuncCache[originalSource] = result;
        }

        return result;
    }

    bool TryCompile(string source, List<string> references, out string errors, out Assembly assembly)
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