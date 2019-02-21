﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

using StLogger = Microsoft.Build.Logging.StructuredLogger;

namespace MessagePack.CodeGenerator
{
    // Utility and Extension methods for Roslyn
    internal static class RoslynExtensions
    {
        static (string fname, string args) GetBuildCommandLine(string csprojPath, string tempPath, bool useDotNet)
        {
            string fname = "dotnet";
            const string tasks = "ResolveReferences";
            // from Buildalyzer implementation
            // https://github.com/daveaglick/Buildalyzer/blob/b42d2e3ba1b3673a8133fb41e72b507b01bce1d6/src/Buildalyzer/Environment/BuildEnvironment.cs#L86-L96
            Dictionary<string, string> properties = new Dictionary<string, string>()
                {
                    {"ProviderCommandLineArgs", "true"},
                    {"GenerateResourceMSBuildArchitecture", "CurrentArchitecture"},
                    {"DesignTimeBuild", "true"},
                    {"BuildProjectReferences","false"},
                    {"SkipCompilerExecution","true"},
                    {"DisableRarCache", "true"},
                    {"AutoGenerateBindingRedirects", "false"},
                    {"CopyBuildOutputToOutputDirectory", "false"},
                    {"CopyOutputSymbolsToOutputDirectory", "false"},
                    {"SkipCopyBuildProduct", "true"},
                    {"AddModules", "false"},
                    {"UseCommonOutputDirectory", "true"},
                    {"GeneratePackageOnBuild", "false"},
                    {"RunPostBuildEvent", "false"},
                    {"SolutionDir", (new FileInfo(csprojPath).Directory.FullName) + "/"}
                };
            var propargs = string.Join(" ", properties.Select(kv => $"/p:{kv.Key}=\"{kv.Value}\""));
            // how to determine whether command should be executed('dotnet msbuild' or 'msbuild')?
            if (useDotNet)
            {
                fname = "dotnet";
                return (fname, $"msbuild \"{csprojPath}\" /t:{tasks} {propargs} /bl:\"{Path.Combine(tempPath, "build.binlog")}\" /v:n");
            }
            else
            {
                fname = "msbuild";
                return (fname, $"\"{csprojPath}\" /t:{tasks} {propargs} /bl:\"{Path.Combine(tempPath, "build.binlog")}\" /v:n");
            }
        }
        static async Task<bool> TryExecute(string csprojPath, string tempPath, bool useDotNet)
        {
            // executing build command with output binary log
            var (fname, args) = GetBuildCommandLine(csprojPath, tempPath, useDotNet);
            try
            {
                using (var stdout = new MemoryStream())
                using (var stderr = new MemoryStream())
                {
                    var exitCode = await ProcessUtil.ExecuteProcessAsync(fname, args, stdout, stderr, null).ConfigureAwait(false);
                    if (exitCode == 0)
                    {
                        return true;
                    }
                    else
                    {
                        // write process output to stdout and stderr when error.
                        using (var stdout2 = new MemoryStream(stdout.ToArray()))
                        using (var stderr2 = new MemoryStream(stderr.ToArray()))
                        using (var consoleStdout = Console.OpenStandardOutput())
                        using (var consoleStderr = Console.OpenStandardError())
                        {
                            await stdout2.CopyToAsync(consoleStdout).ConfigureAwait(false);
                            await stderr2.CopyToAsync(consoleStderr).ConfigureAwait(false);
                        }
                        return false;
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"exception occured(fname={fname}, args={args}):{e}");
                return false;
            }
        }
        static async Task<StLogger.Build> GetBuildResult(string csprojPath, params string[] preprocessorSymbols)
        {
            var tempPath = Path.Combine(new FileInfo(csprojPath).Directory.FullName, "__buildtemp");
            try
            {
                if (!await TryExecute(csprojPath, tempPath, true).ConfigureAwait(false))
                {
                    Console.WriteLine("execute `dotnet msbuild` failed, retry with `msbuild`");
                    if (!await TryExecute(csprojPath, tempPath, false).ConfigureAwait(false))
                    {
                        throw new Exception("failed to build project");
                    }
                }
                var reader = new StLogger.BinLogReader();
                var stlogger = new StLogger.StructuredLogger();
                // prevent output temporary file
                StLogger.StructuredLogger.SaveLogToDisk = false;
                // never output, but if not set, throw exception when initializing
                stlogger.Parameters = "tmp.buildlog";
                stlogger.Initialize(reader);
                reader.Replay(Path.Combine(tempPath, "build.binlog"));
                stlogger.Shutdown();
                return stlogger.Construction.Build;
            }
            finally
            {
                if (Directory.Exists(tempPath))
                {
                    Directory.Delete(tempPath, true);
                }
            }
        }
        // static async Task<AnalyzerResult[]> GetAnalyzerResults(AnalyzerManager analyzerManager, string csprojPath, params string[] preprocessorSymbols)
        // {
        //     var tempPath = Path.Combine(new FileInfo(csprojPath).Directory.FullName, "__buildtemp");
        //     try
        //     {
        //         if (!await TryExecute(csprojPath, tempPath, true).ConfigureAwait(false))
        //         {
        //             Console.WriteLine("execute `dotnet msbuild` failed, retry with `msbuild`");
        //             if (!await TryExecute(csprojPath, tempPath, false).ConfigureAwait(false))
        //             {
        //                 throw new Exception("failed to build project");
        //             }
        //         }

        //         // get results of analysis from binarylog
        //         return analyzerManager.Analyze(Path.Combine(tempPath, "build.binlog")).ToArray();
        //     }
        //     finally
        //     {
        //         if (Directory.Exists(tempPath))
        //         {
        //             Directory.Delete(tempPath, true);
        //         }
        //     }
        // }
        public static Workspace GetWorkspaceFromBuild(this StLogger.Build build, params string[] preprocessorSymbols)
        {
            var csproj = build.Children.OfType<StLogger.Project>().FirstOrDefault();
            if (csproj == null)
            {
                throw new InvalidOperationException("cannot find cs project build");
            }
            StLogger.Item[] compileItems = Array.Empty<StLogger.Item>();
            var properties = new Dictionary<string, StLogger.Property>();
            foreach (var folder in csproj.Children.OfType<StLogger.Folder>())
            {
                if (folder.Name == "Items")
                {
                    var compileFolder = folder.Children.OfType<StLogger.Folder>().FirstOrDefault(x => x.Name == "Compile");
                    if (compileFolder == null)
                    {
                        throw new InvalidOperationException("failed to get compililation documents");
                    }
                    compileItems = compileFolder.Children.OfType<StLogger.Item>().ToArray();
                }
                else if (folder.Name == "Properties")
                {
                    properties = folder.Children.OfType<StLogger.Property>().ToDictionary(x => x.Name);
                }
            }
            var assemblies = Array.Empty<StLogger.Item>();
            foreach (var target in csproj.Children.OfType<StLogger.Target>())
            {
                if (target.Name == "ResolveReferences")
                {
                    var folder = target.Children.OfType<StLogger.Folder>().Where(x => x.Name == "TargetOutputs").FirstOrDefault();
                    if (folder == null)
                    {
                        throw new InvalidOperationException("cannot find result of resolving assembly");
                    }
                    assemblies = folder.Children.OfType<StLogger.Item>().ToArray();
                }
            }
            var ws = new AdhocWorkspace();
            var roslynProject = ws.AddProject(Path.GetFileNameWithoutExtension(csproj.ProjectFile), Microsoft.CodeAnalysis.LanguageNames.CSharp);
            var projectDir = properties["ProjectDir"].Value;
            var pguid = properties.ContainsKey("ProjectGuid") ? Guid.Parse("ProjectGuid") : Guid.NewGuid();
            var projectGuid = ProjectId.CreateFromSerialized(pguid);
            Console.WriteLine($"projectDir = {projectDir}");
            foreach (var compile in compileItems)
            {
                var filePath = compile.Text;
                var absFilePath = Path.Combine(projectDir, filePath);
                Console.WriteLine($"compitem = {filePath}");
                roslynProject = roslynProject.AddDocument(filePath, File.ReadAllText(absFilePath)).Project;
            }
            foreach (var asm in assemblies)
            {
                roslynProject = roslynProject.AddMetadataReference(MetadataReference.CreateFromFile(asm.Text));
            }
            var compopt = roslynProject.CompilationOptions as CSharpCompilationOptions;
            compopt = roslynProject.CompilationOptions as CSharpCompilationOptions;
            compopt = compopt ?? new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
            OutputKind kind;
            switch (properties["OutputType"].Value)
            {
                case "Exe":
                    kind = OutputKind.ConsoleApplication;
                    break;
                case "Library":
                    kind = OutputKind.DynamicallyLinkedLibrary;
                    break;
                default:
                    kind = OutputKind.DynamicallyLinkedLibrary;
                    break;
            }
            roslynProject = roslynProject.WithCompilationOptions(compopt.WithOutputKind(kind).WithAllowUnsafe(true));
            var parseopt = roslynProject.ParseOptions as CSharpParseOptions;
            roslynProject = roslynProject.WithParseOptions(parseopt.WithPreprocessorSymbols(preprocessorSymbols));
            if(!ws.TryApplyChanges(roslynProject.Solution))
            {
                throw new InvalidOperationException("failed to apply solution changes to workspace");
            }
            return ws;
        }
        public static async Task<Compilation> GetCompilationFromProject(string csprojPath, params string[] preprocessorSymbols)
        {
            // var analyzerOptions = new AnalyzerManagerOptions();
            // analyzerOptions.LogWriter = Console.Out;

            // var manager = new AnalyzerManager();
            // var projectAnalyzer = manager.GetProject(csprojPath); // addproj
            // projectAnalyzer.AddBuildLogger(new Microsoft.Build.Logging.ConsoleLogger(Microsoft.Build.Framework.LoggerVerbosity.Minimal));

            var build = await GetBuildResult(csprojPath, preprocessorSymbols).ConfigureAwait(false);

            // var workspace = await manager.GetWorkspaceWithPreventBuildEventAsync().ConfigureAwait(false);
            var workspace = GetWorkspaceFromBuild(build, preprocessorSymbols);


            workspace.WorkspaceFailed += WorkSpaceFailed;
            var project = workspace.CurrentSolution.Projects.First();
            foreach (var doc in project.Documents)
            {
                Console.WriteLine($"doc2 = {doc.Name}");
            }
            project = project
                .WithParseOptions((project.ParseOptions as CSharpParseOptions).WithPreprocessorSymbols(preprocessorSymbols))
                .WithCompilationOptions((project.CompilationOptions as CSharpCompilationOptions).WithAllowUnsafe(true));

            var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
            return compilation;
        }

        private static void WorkSpaceFailed(object sender, WorkspaceDiagnosticEventArgs e)
        {
            Console.WriteLine(e);
        }

        // WIP function for getting Roslyn's workspace from csproj
        // public static async Task<AdhocWorkspace> GetWorkspaceWithPreventBuildEventAsync(this AnalyzerManager manager)
        // {
        //     var projPath = manager.Projects.First().Value.ProjectFile.Path;
        //     var ws = new AdhocWorkspace();
        //     foreach (var result in await GetAnalyzerResults(manager, projPath))
        //     {
        //         // getting only successful build
        //         if (result.Succeeded)
        //         {
        //             result.AddToWorkspace(ws);
        //         }
        //     }
        //     return ws;
        // }

        // public static AdhocWorkspace GetWorkspace(this AnalyzerManager manager, EnvironmentOptions envOptions)
        // {
        //     // Run builds in parallel
        //     List<AnalyzerResult> results = manager.Projects.Values
        //         .AsParallel()
        //         .Select(p => p.Build(envOptions).FirstOrDefault()) // with envoption
        //         .Where(x => x != null)
        //         .ToList();

        //     // Add each result to a new workspace
        //     AdhocWorkspace workspace = new AdhocWorkspace();
        //     foreach (AnalyzerResult result in results)
        //     {
        //         result.AddToWorkspace(workspace);
        //     }
        //     return workspace;
        // }

        public static IEnumerable<INamedTypeSymbol> GetNamedTypeSymbols(this Compilation compilation)
        {
            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semModel = compilation.GetSemanticModel(syntaxTree);

                foreach (var item in syntaxTree.GetRoot()
                    .DescendantNodes()
                    .Select(x => semModel.GetDeclaredSymbol(x))
                    .Where(x => x != null))
                {
                    var namedType = item as INamedTypeSymbol;
                    if (namedType != null)
                    {
                        yield return namedType;
                    }
                }
            }
        }

        public static IEnumerable<INamedTypeSymbol> EnumerateBaseType(this ITypeSymbol symbol)
        {
            var t = symbol.BaseType;
            while (t != null)
            {
                yield return t;
                t = t.BaseType;
            }
        }

        public static AttributeData FindAttribute(this IEnumerable<AttributeData> attributeDataList, string typeName)
        {
            return attributeDataList
                .Where(x => x.AttributeClass.ToDisplayString() == typeName)
                .FirstOrDefault();
        }

        public static AttributeData FindAttributeShortName(this IEnumerable<AttributeData> attributeDataList,
            string typeName)
        {
            return attributeDataList
                .Where(x => x.AttributeClass.Name == typeName)
                .FirstOrDefault();
        }

        public static AttributeData FindAttributeIncludeBasePropertyShortName(this IPropertySymbol property,
            string typeName)
        {
            do
            {
                var data = FindAttributeShortName(property.GetAttributes(), typeName);
                if (data != null) return data;
                property = property.OverriddenProperty;
            } while (property != null);

            return null;
        }

        public static AttributeSyntax FindAttribute(this BaseTypeDeclarationSyntax typeDeclaration, SemanticModel model,
            string typeName)
        {
            return typeDeclaration.AttributeLists
                .SelectMany(x => x.Attributes)
                .Where(x => model.GetTypeInfo(x).Type?.ToDisplayString() == typeName)
                .FirstOrDefault();
        }

        public static INamedTypeSymbol FindBaseTargetType(this ITypeSymbol symbol, string typeName)
        {
            return symbol.EnumerateBaseType()
                .Where(x => x.OriginalDefinition?.ToDisplayString() == typeName)
                .FirstOrDefault();
        }

        public static object GetSingleNamedArgumentValue(this AttributeData attribute, string key)
        {
            foreach (var item in attribute.NamedArguments)
            {
                if (item.Key == key)
                {
                    return item.Value.Value;
                }
            }

            return null;
        }

        public static bool IsNullable(this INamedTypeSymbol symbol)
        {
            if (symbol.IsGenericType)
            {
                if (symbol.ConstructUnboundGenericType().ToDisplayString() == "T?")
                {
                    return true;
                }
            }
            return false;
        }

        public static IEnumerable<ISymbol> GetAllMembers(this ITypeSymbol symbol)
        {
            var t = symbol;
            while (t != null)
            {
                foreach (var item in t.GetMembers())
                {
                    yield return item;
                }
                t = t.BaseType;
            }
        }

        public static IEnumerable<ISymbol> GetAllInterfaceMembers(this ITypeSymbol symbol)
        {
            return symbol.GetMembers()
                .Concat(symbol.AllInterfaces.SelectMany(x => x.GetMembers()));
        }
    }
}