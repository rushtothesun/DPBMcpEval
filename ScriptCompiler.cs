using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DreamPoeBot.Loki.Bot;
using DreamPoeBot.Loki.Common;
using log4net;

namespace McpEval
{
    /// <summary>
    /// Compiles and executes C# scripts at runtime using DreamPoeBot's RoslynCodeCompiler.
    /// Adapted from DevTab's Gui.xaml.cs Dev_Execute() method.
    /// </summary>
    public class ScriptCompiler
    {
        private static readonly ILog Log = Logger.GetLoggerInstanceForType();

        /// <summary>
        /// Result of a script compilation and execution attempt.
        /// </summary>
        public class ScriptResult
        {
            public bool Success { get; set; }
            public object Result { get; set; }
            public string Error { get; set; }
            public bool IsCompilationError { get; set; }
        }

        /// <summary>
        /// Compiles and executes a C# script.
        /// </summary>
        /// <param name="code">The C# source code to compile.</param>
        /// <param name="className">The class name containing the Execute method. Defaults to "Script".</param>
        /// <param name="userAssemblies">Optional additional assembly references.</param>
        /// <returns>A ScriptResult with the outcome.</returns>
        public ScriptResult CompileAndExecute(string code, string className = "Script", IEnumerable<string> userAssemblies = null)
        {
            try
            {
                using (var cs = RoslynCodeCompiler.CreateLatestCSharpProvider())
                {
                    var options = new CompilerParameters
                    {
                        GenerateExecutable = false,
                        GenerateInMemory = false,
                    };

                    var extraAssemblies = userAssemblies?.Select(s => s.ToLowerInvariant()).ToList()
                                         ?? new List<string>();

                    // Reference all currently loaded assemblies in the AppDomain.
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            if (asm.IsDynamic)
                                continue;
                            if (string.IsNullOrEmpty(asm.Location))
                                continue;
                            if (!options.ReferencedAssemblies.Contains(asm.Location))
                            {
                                options.ReferencedAssemblies.Add(asm.Location);
                                extraAssemblies.Remove(
                                    System.IO.Path.GetFileName(asm.Location).ToLowerInvariant());
                            }
                        }
                        catch (Exception)
                        {
                            // Skip assemblies that can't be referenced (e.g., IronPython dynamic assemblies).
                        }
                    }

                    // Add any remaining user-specified assemblies.
                    foreach (var assembly in extraAssemblies)
                    {
                        if (!options.ReferencedAssemblies.Contains(assembly))
                        {
                            options.ReferencedAssemblies.Add(assembly);
                        }
                    }

                    // Compile the code.
                    var res = cs.CompileAssemblyFromSource(options, code);

                    // Handle compilation errors.
                    if (res.Errors.Count > 0)
                    {
                        var sb = new StringBuilder();
                        foreach (CompilerError err in res.Errors)
                        {
                            sb.AppendFormat("Line {0}, Error {1}: {2}", err.Line, err.ErrorNumber, err.ErrorText);
                            sb.AppendLine();
                        }

                        return new ScriptResult
                        {
                            Success = false,
                            Error = sb.ToString(),
                            IsCompilationError = true
                        };
                    }

                    // Find the class and invoke Execute().
                    var type = res.CompiledAssembly.GetType(className);
                    if (type == null)
                    {
                        return new ScriptResult
                        {
                            Success = false,
                            Error = $"Class '{className}' not found in compiled assembly.",
                            IsCompilationError = false
                        };
                    }

                    var executeMethod = type.GetMethod("Execute");
                    if (executeMethod == null)
                    {
                        return new ScriptResult
                        {
                            Success = false,
                            Error = $"Method 'Execute' not found on class '{className}'.",
                            IsCompilationError = false
                        };
                    }

                    var obj = Activator.CreateInstance(type);
                    var output = executeMethod.Invoke(obj, new object[] { });

                    return new ScriptResult
                    {
                        Success = true,
                        Result = output
                    };
                }
            }
            catch (Exception ex)
            {
                Log.Error("[McpEval] Script execution error:", ex);
                return new ScriptResult
                {
                    Success = false,
                    Error = ex.ToString(),
                    IsCompilationError = false
                };
            }
        }
    }
}
