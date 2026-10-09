using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Coverlet.Core.Instrumentation
{
    /// <summary>
    /// This static class will be injected on a module being instrumented in order to direct on module hits
    /// to a single location.
    /// </summary>
    /// <remarks>
    /// As this type is going to be customized for each instrumented module it doesn't follow typical practices
    /// regarding visibility of members, etc.
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class ModuleTrackerTemplate
    {
        public static string HitsFilePath;
        public static int[] HitsArray;
        public static bool SingleHit;
        public static bool FlushHitFile;
        private static readonly bool _enableLog = int.TryParse(Environment.GetEnvironmentVariable("COVERLET_ENABLETRACKERLOG"), out int result) ? result == 1 : false;

        static ModuleTrackerTemplate()
        {
            // At the end of the instrumentation of a module, the instrumenter needs to add code here
            // to initialize the static fields according to the values derived from the instrumentation of
            // the module.
        }

        public static int[] GetHitsRecord() => Interlocked.Exchange(ref HitsArray, new int[HitsArray.Length]);

        // A call to this method will be injected in the static constructor above for most cases. However, if the
        // current assembly is System.Private.CoreLib (or more specifically, defines System.AppDomain), a call directly
        // to UnloadModule will be injected in System.AppContext.OnProcessExit.
        public static void RegisterUnloadEvents()
        {
            AppDomain.CurrentDomain.ProcessExit += new EventHandler(UnloadModule);
            AppDomain.CurrentDomain.DomainUnload += new EventHandler(UnloadModule);

            // A logoff terminates interactive processes without ProcessExit, after sending them WM_ENDSESSION.
            if (Environment.UserInteractive)
            {
                ThreadPool.QueueUserWorkItem(new WaitCallback(RegisterSessionEnded));
            }
        }

        // From a pool thread SystemEvents starts its own message loop thread and raises SessionEnded on it.
        private static void RegisterSessionEnded(object state)
        {
            try
            {
                // Only desktop apps ship the type on .NET, and a failed load would reach the app's AssemblyResolve handlers.
                string platformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
                string systemEvents = platformAssemblies == null
                    ? "Microsoft.Win32.SystemEvents, System, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"
                    : platformAssemblies.Contains("Microsoft.Win32.SystemEvents.dll") ? "Microsoft.Win32.SystemEvents, Microsoft.Win32.SystemEvents" : null;
                if (systemEvents != null)
                {
                    EventInfo sessionEnded = Type.GetType(systemEvents, true).GetEvent("SessionEnded");
                    sessionEnded.AddEventHandler(null, Delegate.CreateDelegate(sessionEnded.EventHandlerType, new EventHandler(UnloadModule).Method));
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Failed to register for session end -> '{ex.Message}'");
            }
        }

        public static void RecordHitInCoreLibrary(int hitLocationIndex)
        {
            // Make sure to avoid recording if this is a call to RecordHit within the AppDomain setup code in an
            // instrumented build of System.Private.CoreLib.
            if (HitsArray is null)
                return;

            Interlocked.Increment(ref HitsArray[hitLocationIndex]);
        }

        public static void RecordHit(int hitLocationIndex)
        {
            Interlocked.Increment(ref HitsArray[hitLocationIndex]);
        }

        public static void RecordSingleHitInCoreLibrary(int hitLocationIndex)
        {
            // Make sure to avoid recording if this is a call to RecordHit within the AppDomain setup code in an
            // instrumented build of System.Private.CoreLib.
            if (HitsArray is null)
                return;

            ref int location = ref HitsArray[hitLocationIndex];
            if (location == 0)
                location = 1;
        }

        public static void RecordSingleHit(int hitLocationIndex)
        {
            ref int location = ref HitsArray[hitLocationIndex];
            if (location == 0)
                location = 1;
        }

        public static void UnloadModule(object sender, EventArgs e)
        {
            // The same module can be unloaded multiple times in the same process via different app domains.
            // Use a global mutex to ensure no concurrent access.
            using (var mutex = new Mutex(true, Path.GetFileNameWithoutExtension(HitsFilePath) + "_Mutex", out bool createdNew))
            {
                if (!createdNew)
                {
                    mutex.WaitOne();
                }

                if (FlushHitFile)
                {
                    try
                    {
                        // Claim the current hits array and reset it to prevent double-counting scenarios.
                        int[] hitsArray = Interlocked.Exchange(ref HitsArray, new int[HitsArray.Length]);

                        WriteLog($"Unload called for '{Assembly.GetExecutingAssembly().Location}' by '{sender ?? "null"}'");
                        WriteLog($"Flushing hit file '{HitsFilePath}'");

                        bool failedToCreateNewHitsFile = false;
                        try
                        {
                            using (var fs = new FileStream(HitsFilePath, FileMode.CreateNew))
                            using (var bw = new BinaryWriter(fs))
                            {
                                bw.Write(hitsArray.Length);
                                foreach (int hitCount in hitsArray)
                                {
                                    bw.Write(hitCount);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            WriteLog($"Failed to create new hits file '{HitsFilePath}' -> '{ex.Message}'");
                            failedToCreateNewHitsFile = true;
                        }

                        if (failedToCreateNewHitsFile)
                        {
                            // Update the number of hits by adding value on disk with the ones on memory.
                            // This path should be triggered only in the case of multiple AppDomain unloads.
                            // The mutex only spans one Windows session, so a process in another session may still hold the file.
                            FileStream hitsFile = null;
                            for (int attempt = 0; hitsFile == null; ++attempt)
                            {
                                try
                                {
                                    hitsFile = new FileStream(HitsFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                                }
                                catch (IOException)
                                {
                                    if (attempt == 250)
                                    {
                                        throw;
                                    }

                                    Thread.Sleep(20);
                                }
                            }

                            using (var fs = hitsFile)
                            using (var br = new BinaryReader(fs))
                            using (var bw = new BinaryWriter(fs))
                            {
                                int hitsLength = br.ReadInt32();
                                WriteLog($"Current hits found '{hitsLength}'");

                                if (hitsLength != hitsArray.Length)
                                {
                                    throw new InvalidOperationException($"{HitsFilePath} has {hitsLength} entries but on memory {nameof(HitsArray)} has {hitsArray.Length}");
                                }

                                for (int i = 0; i < hitsLength; ++i)
                                {
                                    int oldHitCount = br.ReadInt32();
                                    bw.Seek(-sizeof(int), SeekOrigin.Current);
                                    if (SingleHit)
                                    {
                                        bw.Write(hitsArray[i] + oldHitCount > 0 ? 1 : 0);
                                    }
                                    else
                                    {
                                        bw.Write(hitsArray[i] + oldHitCount);
                                    }
                                }
                            }
                        }

                        WriteHits(sender);

                        WriteLog($"Hit file '{HitsFilePath}' flushed, size {new FileInfo(HitsFilePath).Length}");
                        WriteLog("--------------------------------");
                    }
                    catch (Exception ex)
                    {
                        WriteLog(ex.ToString());
                        throw;
                    }
                }

                // On purpose this is not under a try-finally: it is better to have an exception if there was any error writing the hits file
                // this case is relevant when instrumenting corelib since multiple processes can be running against the same instrumented dll.
                mutex.ReleaseMutex();
            }
        }

        private static void WriteHits(object sender)
        {
            if (_enableLog)
            {
                Assembly currentAssembly = Assembly.GetExecutingAssembly();
                DirectoryInfo location = new DirectoryInfo(Path.Combine(Path.GetDirectoryName(currentAssembly.Location), "TrackersHitsLog"));
                location.Create();
                string logFile = Path.Combine(location.FullName, $"{Path.GetFileName(currentAssembly.Location)}_{DateTime.UtcNow.Ticks}_{Process.GetCurrentProcess().Id}.txt");
                using (var fs = new FileStream(HitsFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                using (var log = new FileStream(logFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var logWriter = new StreamWriter(log))
                using (var br = new BinaryReader(fs))
                {
                    int hitsLength = br.ReadInt32();
                    for (int i = 0; i < hitsLength; ++i)
                    {
                        logWriter.WriteLine($"{i},{br.ReadInt32()}");
                    }
                }

                File.AppendAllText(logFile, $"Hits flushed file path {HitsFilePath} location '{Assembly.GetExecutingAssembly().Location}' by '{sender ?? "null"}'");
            }
        }

        private static void WriteLog(string logText)
        {
            if (_enableLog)
            {
                // We don't set path as global var to keep benign possible errors inside try/catch
                // I'm not sure that location will be ok in every scenario
                string location = Assembly.GetExecutingAssembly().Location;
                File.AppendAllText(Path.Combine(Path.GetDirectoryName(location), Path.GetFileName(location) + "_tracker.txt"), $"[{DateTime.UtcNow} P:{Process.GetCurrentProcess().Id} T:{Thread.CurrentThread.ManagedThreadId}]{logText}{Environment.NewLine}");
            }
        }
    }
}
