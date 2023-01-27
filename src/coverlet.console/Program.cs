using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

using ConsoleTables;
using Coverlet.Console.Logging;
using Coverlet.Core;
using Coverlet.Core.Abstractions;
using Coverlet.Core.Enums;
using Coverlet.Core.Helpers;
using Coverlet.Core.Reporters;
using Coverlet.Core.Symbols;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;

namespace Coverlet.Console
{
    class Program
    {
        static int Main(string[] args)
        {
            IServiceCollection serviceCollection = new ServiceCollection();
            serviceCollection.AddTransient<IRetryHelper, RetryHelper>();
            serviceCollection.AddTransient<IProcessExitHandler, ProcessExitHandler>();
            serviceCollection.AddTransient<IFileSystem, FileSystem>();
            serviceCollection.AddTransient<ILogger, ConsoleLogger>();
            // We need to keep singleton/static semantics
            serviceCollection.AddSingleton<IInstrumentationHelper, InstrumentationHelper>();
            serviceCollection.AddSingleton<ISourceRootTranslator, SourceRootTranslator>(provider => new SourceRootTranslator(provider.GetRequiredService<ILogger>(), provider.GetRequiredService<IFileSystem>()));
            serviceCollection.AddSingleton<ICecilSymbolHelper, CecilSymbolHelper>();

            ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();

            var logger = (ConsoleLogger)serviceProvider.GetService<ILogger>();
            var fileSystem = serviceProvider.GetService<IFileSystem>();

            var app = new CommandLineApplication();
            app.Name = "coverlet";
            app.FullName = "Cross platform .NET Core code coverage tool";
            app.HelpOption("-h|--help");
            app.VersionOption("-v|--version", GetAssemblyVersion());
            int exitCode = (int)CommandExitCodes.Success;

            CommandArgument moduleOrAppDirectory = app.Argument("<ASSEMBLY|DIRECTORY>", "Path to the test assembly or application directory.");
            CommandOption target = app.Option("-t|--target", "Path to the test runner application.", CommandOptionType.SingleValue);
            CommandOption targs = app.Option("-a|--targetargs", "Arguments to be passed to the test runner.", CommandOptionType.SingleValue);
            CommandOption output = app.Option("-o|--output", "Output of the generated coverage report", CommandOptionType.SingleValue);
            CommandOption<LogLevel> verbosity = app.Option<LogLevel>("-v|--verbosity", "Sets the verbosity level of the command. Allowed values are quiet, minimal, normal, detailed.", CommandOptionType.SingleValue);
            CommandOption formats = app.Option("-f|--format", "Format of the generated coverage report.", CommandOptionType.MultipleValue);
            CommandOption threshold = app.Option("--threshold", "Exits with error if the coverage % is below value.", CommandOptionType.SingleValue);
            CommandOption thresholdTypes = app.Option("--threshold-type", "Coverage type to apply the threshold to.", CommandOptionType.MultipleValue);
            CommandOption thresholdStat = app.Option("--threshold-stat", "Coverage statistic used to enforce the threshold value.", CommandOptionType.SingleValue);
            CommandOption excludeFilters = app.Option("--exclude", "Filter expressions to exclude specific modules and types.", CommandOptionType.MultipleValue);
            CommandOption includeFilters = app.Option("--include", "Filter expressions to include only specific modules and types.", CommandOptionType.MultipleValue);
            CommandOption excludedSourceFiles = app.Option("--exclude-by-file", "Glob patterns specifying source files to exclude.", CommandOptionType.MultipleValue);
            CommandOption includeDirectories = app.Option("--include-directory", "Include directories containing additional assemblies to be instrumented.", CommandOptionType.MultipleValue);
            CommandOption excludeAttributes = app.Option("--exclude-by-attribute", "Attributes to exclude from code coverage.", CommandOptionType.MultipleValue);
            CommandOption includeTestAssembly = app.Option("--include-test-assembly", "Specifies whether to report code coverage of the test assembly.", CommandOptionType.NoValue);
            CommandOption singleHit = app.Option("--single-hit", "Specifies whether to limit code coverage hit reporting to a single hit for each location", CommandOptionType.NoValue);
            CommandOption skipAutoProp = app.Option("--skipautoprops", "Neither track nor record auto-implemented properties.", CommandOptionType.NoValue);
            CommandOption mergeWith = app.Option("--merge-with", "Path to existing coverage result to merge.", CommandOptionType.SingleValue);
            CommandOption hitsFolderPath = app.Option("--hits-path", "Path to store the hits files. Defaults to the user temp folder.", CommandOptionType.SingleValue);
            CommandOption exposeGetHitsMethodOnly = app.Option("--expose-get-hits-method-only", "When set to false, it will not store the hits files on AppDomain.DomainUnload()", CommandOptionType.NoValue);
            CommandOption useSourceLink = app.Option("--use-source-link", "Specifies whether to use SourceLink URIs in place of file system paths.", CommandOptionType.NoValue);
            CommandOption doesNotReturnAttributes = app.Option("--does-not-return-attribute", "Attributes that mark methods that do not return.", CommandOptionType.MultipleValue);
            CommandOption additionalModulePaths = app.Option("--module-path", "Specifies an additional module path for assembly resolution.", CommandOptionType.MultipleValue);
            CommandOption instrumentOnly = app.Option("--instrument-only", "Permanently instruments the assemblies, skipping execution. To be used with --skip-instrumentation or --collect-only when consecutive out of process executions are required.", CommandOptionType.NoValue);
            CommandOption collectOnly = app.Option("--collect-only", "Collects the coverage result from an out-of-process execution of instrumented assemblies.", CommandOptionType.NoValue);
            CommandOption skipInstrumentation = app.Option("--skip-instrumentation", "Runs the process directly, skipping the instrumentation process. Assumes that the assemblies have previously been instrumented.", CommandOptionType.NoValue);

            app.OnExecute(() =>
            {
                if (string.IsNullOrEmpty(moduleOrAppDirectory.Value) || string.IsNullOrWhiteSpace(moduleOrAppDirectory.Value))
                    throw new CommandParsingException(app, "No test assembly or application directory specified.");

                if (!target.HasValue())
                    throw new CommandParsingException(app, "Target must be specified.");

                if (verbosity.HasValue())
                {
                    // Adjust log level based on user input.
                    logger.Level = verbosity.ParsedValue;
                }

                CoverageParameters parameters = new CoverageParameters
                {
                    IncludeFilters = includeFilters.Values.ToArray(),
                    IncludeDirectories = includeDirectories.Values.ToArray(),
                    ExcludeFilters = excludeFilters.Values.ToArray(),
                    ExcludedSourceFiles = excludedSourceFiles.Values.ToArray(),
                    ExcludeAttributes = excludeAttributes.Values.ToArray(),
                    IncludeTestAssembly = includeTestAssembly.HasValue(),
                    SingleHit = singleHit.HasValue(),
                    MergeWith = mergeWith.Value(),
                    UseSourceLink = useSourceLink.HasValue(),
                    SkipAutoProps = skipAutoProp.HasValue(),
                    AdditionalModulePaths = additionalModulePaths.Values.ToArray(),
                    DoesNotReturnAttributes = doesNotReturnAttributes.Values.ToArray(),
                    HitsFolderPath = hitsFolderPath.Value(),
                    ExposeGetHitsMethodOnly = exposeGetHitsMethodOnly.HasValue()
                };

                Coverage coverage = null;
                CoveragePrepareResult coveragePrepareResult = null;

                if (skipInstrumentation.HasValue() || collectOnly.HasValue())
                {
                    coveragePrepareResult = LoadCoverageDetailsFromFile(moduleOrAppDirectory.Value, fileSystem);
                    coverage = new Coverage(coveragePrepareResult,
                                                 logger,
                                                 serviceProvider.GetRequiredService<IInstrumentationHelper>(),
                                                 fileSystem,
                                                 serviceProvider.GetRequiredService<ISourceRootTranslator>());
                }
                else
                {
                    coverage = new Coverage(moduleOrAppDirectory.Value,
                                                 parameters,
                                                 logger,
                                                 serviceProvider.GetRequiredService<IInstrumentationHelper>(),
                                                 fileSystem,
                                                 serviceProvider.GetRequiredService<ISourceRootTranslator>(),
                                                 serviceProvider.GetRequiredService<ICecilSymbolHelper>());
                    coveragePrepareResult = coverage.PrepareModules();
                }


                if (instrumentOnly.HasValue())
                {
                    using (Stream instrumentedStateFile = fileSystem.NewFileStream(GetCoverageDetailsFileName(moduleOrAppDirectory.Value), FileMode.OpenOrCreate, FileAccess.Write))
                    {
                        using (Stream serializedState = CoveragePrepareResult.Serialize(coveragePrepareResult))
                        {
                            serializedState.CopyTo(instrumentedStateFile);
                        }
                    }

                    IInstrumentationHelper instrumentationHelper = serviceProvider.GetService<IInstrumentationHelper>();
                    instrumentationHelper.ShouldRestoreOriginalModulesOnExit = false;

                    return 0;
                }

                Process process = null;

                if (collectOnly.HasValue())
                {
                    IInstrumentationHelper instrumentationHelper = serviceProvider.GetService<IInstrumentationHelper>();
                    instrumentationHelper.ShouldRestoreOriginalModulesOnExit = false;
                }
                else
                {
                    process = new Process();
                    process.StartInfo.FileName = target.Value();
                    process.StartInfo.Arguments = targs.HasValue() ? targs.Value() : string.Empty;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.OutputDataReceived += (sender, eventArgs) =>
                    {
                        if (!string.IsNullOrEmpty(eventArgs.Data))
                            logger.LogInformation(eventArgs.Data, important: true);
                    };

                    process.ErrorDataReceived += (sender, eventArgs) =>
                    {
                        if (!string.IsNullOrEmpty(eventArgs.Data))
                            logger.LogError(eventArgs.Data);
                    };

                    process.Start();

                    process.BeginErrorReadLine();
                    process.BeginOutputReadLine();

                    process.WaitForExit();
                }

                var dOutput = output.HasValue() ? output.Value() : Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar.ToString();
                var dThreshold = threshold.HasValue() ? double.Parse(threshold.Value()) : 0;
                var dThresholdTypes = thresholdTypes.HasValue() ? thresholdTypes.Values : new List<string>(new string[] { "line", "branch", "method" });
#if NET6_0_OR_GREATER
                var dThresholdStat = thresholdStat.HasValue() ? Enum.Parse<ThresholdStatistic>(thresholdStat.Value(), true) : Enum.Parse<ThresholdStatistic>("minimum", true);
#else
                var dThresholdStat = thresholdStat.HasValue() ? (ThresholdStatistic)Enum.Parse(typeof(ThresholdStatistic), thresholdStat.Value(), true) : (ThresholdStatistic)Enum.Parse(typeof(ThresholdStatistic), "minimum", true);
#endif

                logger.LogInformation("\nCalculating coverage result...");

                var restoreOriginalAssemblies = !skipInstrumentation.HasValue() && !collectOnly.HasValue();

                var result = coverage.GetCoverageResult(restoreOriginalAssemblies);
                var directory = Path.GetDirectoryName(dOutput);
                if (directory == string.Empty)
                {
                    directory = Directory.GetCurrentDirectory();
                }
                else if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                foreach (var format in (formats.HasValue() ? formats.Values : new List<string>(new string[] { "json" })))
                {
                    var reporter = new ReporterFactory(format).CreateReporter();
                    if (reporter == null)
                    {
                        throw new Exception($"Specified output format '{format}' is not supported");
                    }

                    if (reporter.OutputType == ReporterOutputType.Console)
                    {
                        // Output to console
                        logger.LogInformation("  Outputting results to console", important: true);
                        logger.LogInformation(reporter.Report(result), important: true);
                    }
                    else
                    {
                        // Output to file
                        var filename = Path.GetFileName(dOutput);
                        filename = (filename == string.Empty) ? $"coverage.{reporter.Extension}" : filename;
                        filename = Path.HasExtension(filename) ? filename : $"{filename}.{reporter.Extension}";

                        var report = Path.Combine(directory, filename);
                        logger.LogInformation($"  Generating report '{report}'", important: true);
                        fileSystem.WriteAllText(report, reporter.Report(result));
                    }
                }

                var thresholdTypeFlags = ThresholdTypeFlags.None;

                foreach (var thresholdType in dThresholdTypes)
                {
                    if (thresholdType.Equals("line", StringComparison.OrdinalIgnoreCase))
                    {
                        thresholdTypeFlags |= ThresholdTypeFlags.Line;
                    }
                    else if (thresholdType.Equals("branch", StringComparison.OrdinalIgnoreCase))
                    {
                        thresholdTypeFlags |= ThresholdTypeFlags.Branch;
                    }
                    else if (thresholdType.Equals("method", StringComparison.OrdinalIgnoreCase))
                    {
                        thresholdTypeFlags |= ThresholdTypeFlags.Method;
                    }
                }

                var coverageTable = new ConsoleTable("Module", "Line", "Branch", "Method");
                var summary = new CoverageSummary();
                int numModules = result.Modules.Count;

                var linePercentCalculation = summary.CalculateLineCoverage(result.Modules);
                var branchPercentCalculation = summary.CalculateBranchCoverage(result.Modules);
                var methodPercentCalculation = summary.CalculateMethodCoverage(result.Modules);

                var totalLinePercent = linePercentCalculation.Percent;
                var totalBranchPercent = branchPercentCalculation.Percent;
                var totalMethodPercent = methodPercentCalculation.Percent;

                var averageLinePercent = linePercentCalculation.AverageModulePercent;
                var averageBranchPercent = branchPercentCalculation.AverageModulePercent;
                var averageMethodPercent = methodPercentCalculation.AverageModulePercent;

                foreach (var _module in result.Modules)
                {
                    var linePercent = summary.CalculateLineCoverage(_module.Value).Percent;
                    var branchPercent = summary.CalculateBranchCoverage(_module.Value).Percent;
                    var methodPercent = summary.CalculateMethodCoverage(_module.Value).Percent;

                    coverageTable.AddRow(Path.GetFileNameWithoutExtension(_module.Key), $"{linePercent}%", $"{branchPercent}%", $"{methodPercent}%");
                }

                logger.LogInformation(coverageTable.ToStringAlternative());

                coverageTable.Columns.Clear();
                coverageTable.Rows.Clear();

                coverageTable.AddColumn(new[] { "", "Line", "Branch", "Method" });
                coverageTable.AddRow("Total", $"{totalLinePercent}%", $"{totalBranchPercent}%", $"{totalMethodPercent}%");
                coverageTable.AddRow("Average", $"{averageLinePercent}%", $"{averageBranchPercent}%", $"{averageMethodPercent}%");

                logger.LogInformation(coverageTable.ToStringAlternative());
                if (process?.ExitCode > 0)
                {
                    exitCode += (int)CommandExitCodes.TestFailed;
                }
                thresholdTypeFlags = result.GetThresholdTypesBelowThreshold(summary, dThreshold, thresholdTypeFlags, dThresholdStat);
                if (thresholdTypeFlags != ThresholdTypeFlags.None)
                {
                    exitCode += (int)CommandExitCodes.CoverageBelowThreshold;
                    var exceptionMessageBuilder = new StringBuilder();
                    if ((thresholdTypeFlags & ThresholdTypeFlags.Line) != ThresholdTypeFlags.None)
                    {
                        exceptionMessageBuilder.AppendLine($"The {dThresholdStat.ToString().ToLower()} line coverage is below the specified {dThreshold}");
                    }

                    if ((thresholdTypeFlags & ThresholdTypeFlags.Branch) != ThresholdTypeFlags.None)
                    {
                        exceptionMessageBuilder.AppendLine($"The {dThresholdStat.ToString().ToLower()} branch coverage is below the specified {dThreshold}");
                    }

                    if ((thresholdTypeFlags & ThresholdTypeFlags.Method) != ThresholdTypeFlags.None)
                    {
                        exceptionMessageBuilder.AppendLine($"The {dThresholdStat.ToString().ToLower()} method coverage is below the specified {dThreshold}");
                    }

                    throw new Exception(exceptionMessageBuilder.ToString());
                }

                return exitCode;
            });

            try
            {
                return app.Execute(args);
            }
            catch (CommandParsingException ex)
            {
                logger.LogError(ex.Message);
                app.ShowHelp();
                return (int)CommandExitCodes.CommandParsingException;
            }
            catch (Win32Exception we) when (we.Source == "System.Diagnostics.Process")
            {
                logger.LogError($"Start process '{target.Value()}' failed with '{we.Message}'");
                return exitCode > 0 ? exitCode : (int)CommandExitCodes.Exception;
            }
            catch (Exception ex)
            {
                logger.LogError(ex.Message);
                return exitCode > 0 ? exitCode : (int)CommandExitCodes.Exception;
            }
        }

        private static CoveragePrepareResult LoadCoverageDetailsFromFile(string moduleOrAppDirectory, IFileSystem fileSystem)
        {
            var coverageFile = GetCoverageDetailsFileName(Path.GetDirectoryName(moduleOrAppDirectory));

            if (fileSystem.Exists(coverageFile))
            {
                using var fileStream = new FileStream(coverageFile, FileMode.Open);
                return CoveragePrepareResult.Deserialize(fileStream);
            }

            throw new FileNotFoundException("Missing instrumentation file, please make sure to instrument the modules first.");
        }

        private static string GetCoverageDetailsFileName(string folderPath)
        {
            return Path.Combine(folderPath, "__instrumentation.xml");
        }

        static string GetAssemblyVersion() => typeof(Program).Assembly.GetName().Version.ToString();
    }
}
