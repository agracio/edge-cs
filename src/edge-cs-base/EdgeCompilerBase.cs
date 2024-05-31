using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
// ReSharper disable InconsistentNaming

// ReSharper disable once CheckNamespace
public class EdgeCompilerBase
{
    private static readonly Regex ReferenceRegex = new(@"^[\ \t]*(?:\/{2})?\#r[\ \t]+""([^""]+)""", RegexOptions.Multiline);
    private static readonly Regex UsingRegex = new(@"^[\ \t]*(using[\ \t]+[^\ \t]+[\ \t]*\;)", RegexOptions.Multiline);
    private static readonly bool CacheEnabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EDGE_CS_CACHE"));
    private static readonly ConcurrentDictionary<string, Func<object, Task<object>>> FuncCache = new();

    protected static readonly bool DebuggingEnabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EDGE_CS_DEBUG"));
    protected static readonly Dictionary<string, Dictionary<string, Assembly>> ReferencedAssemblies = new();
    protected static Func<Stream, Assembly> _assemblyLoader;
    private List<string> _references;

    protected static string Framework;

    private void CreateReferences(List<string> references)
    {
        _references = references;
    }

    protected void DebugMessage(string format, params object[] args)
    {
        if (DebuggingEnabled)
        {
            Console.WriteLine(format, args);
        }
    }

    protected Func<object, Task<object>> CompileFunc(IDictionary<string, object> parameters)
    {
        return CompileFunc(parameters, new Dictionary<string, string>());
    }

    protected Func<object, Task<object>> CompileFunc(IDictionary<string, object> parameters, IDictionary<string, string> compileAssemblies)
    {
        DebugMessage($"EdgeCompiler::CompileFunc ({Framework}) - Starting");

        DebugMessage($"EdgeCompiler::CompileFunc ({Framework}) - Parameters");
        foreach (var key in parameters.Keys)
        {
            DebugMessage($"EdgeCompiler::CompileFunc ({Framework}) - {0}: {1}", key, parameters[key]);
        }

        var source = (string) parameters["source"];

        var comparison = Framework == ".NET" ? StringComparison.InvariantCultureIgnoreCase : StringComparison.OrdinalIgnoreCase;

        // read source from file
        if (source.EndsWith(".cs", comparison) || source.EndsWith(".csx", comparison))
        {
            source = File.ReadAllText(source);
        }

        DebugMessage($"EdgeCompiler::CompileFunc ({Framework}) - Func cache size: {0}", FuncCache.Count);

        var originalSource = source;
        if (FuncCache.ContainsKey(originalSource))
        {
            DebugMessage($"EdgeCompiler::CompileFunc ({Framework}) - Serving func from cache");
            return FuncCache[originalSource];
        }

        DebugMessage($"EdgeCompiler::CompileFunc ({Framework}) - Func not found in cache, compiling");

        // add assembly references provided explicitly through parameters
        if (Framework == ".NET")
        {
            CreateReferences([]);
        }
        else
        {
            CreateReferences([
                "System.Runtime",
                "System.Threading.Tasks",
                //"System.Dynamic.Runtime",
                "Microsoft.CSharp"
            ]);
        }

        if (parameters.TryGetValue("references", out var providedReferences))
        {
            foreach (var reference in (object[])providedReferences)
            {
                _references.Add((string)reference);
            }
        }

        // add assembly references provided in code as [//]#r "assemblyname" lines
        var match = ReferenceRegex.Match(source);
        while (match.Success)
        {
            _references.Add(match.Groups[1].Value);
            source = source.Substring(0, match.Index) + source.Substring(match.Index + match.Length);
            match = ReferenceRegex.Match(source);
        }

        // try to compile source code as a class library
        Assembly assembly;
        string errorsClass;
        var compile = Framework == ".NET" ? 
            TryCompile(source, _references, out errorsClass, out assembly) : 
            TryCompile(source, _references, compileAssemblies, out errorsClass, out assembly);
        
        if (!compile)
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

            source = usings + @"
                using System;
                using System.Threading.Tasks;

                public class Startup 
                {
                    public async Task<object> Invoke(object ___input) 
                    {
                " + @"
                        Func<object, Task<object>> func = " + source + @";
                #line hidden
                        return await func(___input);
                    }
                }";

            DebugMessage($"EdgeCompiler::CompileFunc ({Framework}) - Trying to compile async lambda expression:{0}{1}", Environment.NewLine, source);

            string errorsLambda;
            compile = Framework == ".NET" ? 
                TryCompile(source, _references, out errorsLambda, out assembly) : 
                TryCompile(source, _references, compileAssemblies, out errorsLambda, out assembly);
            
            if (!compile)
            {
                throw new InvalidOperationException(
                    "Unable to compile C# code.\n----> Errors when compiling as a CLR library:\n"
                    + errorsClass
                    + "\n----> Errors when compiling as a CLR async lambda expression:\n"
                    + errorsLambda);
            }
        }

        if (Framework == ".NET")
        {
            // store referenced assemblies to help resolve them at runtime from AppDomain.AssemblyResolve
            ReferencedAssemblies[assembly.FullName] = new Dictionary<string, Assembly>();
            foreach (var reference in _references)
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
        }
        
        var typeName = (string)parameters["typeName"];
        var methodName = (string)parameters["methodName"];

        // Extract the entry point to a class method
        Type startupType = assembly.GetType(typeName, true, true);
        object instance = Activator.CreateInstance(startupType, false);
        MethodInfo invokeMethod = startupType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);

        if (invokeMethod == null)
        {
            throw new InvalidOperationException(
                $"Unable to access CLR method to wrap through reflection. Make sure it is a public instance method.\r\nType: {typeName}, Method: {methodName}, Assembly: {assembly.GetName().FullName}");
        }

        // Create a Func<object,Task<object>> delegate around the method invocation using reflection
        Func<object, Task<object>> result = input => (Task<object>) invokeMethod.Invoke(instance, new object[] { input });

        if (CacheEnabled)
        {
            FuncCache[originalSource] = result;
        }

        return result;
    }

    protected virtual bool TryCompile(string source, List<string> references, IDictionary<string, string> compileAssemblies, out string errors, out Assembly assembly)
    {
        errors = string.Empty;
        assembly = null;
        return false;
    }
    protected virtual bool TryCompile(string source, List<string> references, out string errors, out Assembly assembly)
    {
        errors = string.Empty;
        assembly = null;
        return false;
    }

}
