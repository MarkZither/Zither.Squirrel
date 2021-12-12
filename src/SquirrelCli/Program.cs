using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mono.Options;
using Squirrel;
using Squirrel.Json;
using Squirrel.Lib;
using Squirrel.NuGet;
using Squirrel.SimpleSplat;
using SquirrelCli.Sources;

namespace SquirrelCli
{
    class Program : IEnableLogger
    {
        public static int Main(string[] args)
        {
            var exeName = Path.GetFileName(AssemblyRuntimeInfo.EntryExePath);
            var commands = new CommandSet {
                "",
#pragma warning disable CS0436 // Type conflicts with imported type
                $"Squirrel ({ThisAssembly.AssemblyInformationalVersion}) command line tool for creating and deploying Squirrel releases",
#pragma warning restore CS0436 // Type conflicts with imported type
                $"Usage: {exeName} [verb] [--option:value]",
                "",
                "Package Authoring:",
                { "releasify", "Take an existing nuget package and turn it into a Squirrel release", new ReleasifyOptions(), Releasify },
                { "pack", "Creates a nuget package from a folder and releasifies it in a single step", new PackOptions(), Pack },

                "Package Deployment / Syncing:",
                { "b2-down", "Download recent releases from BackBlaze B2", new SyncBackblazeOptions(), o => new BackblazeRepository(o).DownloadRecentPackages().Wait() },
                { "b2-up", "Upload releases to BackBlaze B2", new SyncBackblazeOptions(), o => new BackblazeRepository(o).UploadMissingPackages().Wait() },
                { "http-down", "Download recent releases from an HTTP source", new SyncHttpOptions(), o => new SimpleWebRepository(o).DownloadRecentPackages().Wait() },
                //{ "http-up", "sync", new SyncHttpOptions(), o => new SimpleWebRepository(o).UploadMissingPackages().Wait() },
                { "github-down", "Download recent releases from GitHub", new SyncGithubOptions(), o => new GitHubRepository(o).DownloadRecentPackages().Wait() },
                //{ "github-up", "sync", new SyncGithubOptions(), o => new GitHubRepository(o).UploadMissingPackages().Wait() },
                //"",
                //"Examples:",
                //$"    {exeName} pack ",
                //$"        ",
            };

            var logger = new ConsoleLogger();

            try {
                // check for help/verbose argument
                bool help = false;
                bool verbose = false;
                new OptionSet() {
                    { "h|?|help", _ => help = true },
                    { "v|verbose", _ => verbose = true },
                }.Parse(args);

                if (verbose) {
                    logger.Level = LogLevel.Debug;
                }

                if (help) {
                    commands.WriteHelp();
                    return -1;
                } else {
                    // parse cli and run command
                    SquirrelLocator.CurrentMutable.Register(() => logger, typeof(Squirrel.SimpleSplat.ILogger));
                    commands.Execute(args);
                }
                return 0;
            } catch (Exception ex) {
                Console.WriteLine();
                logger.Write(ex.ToString(), LogLevel.Error);
                Console.WriteLine();
                commands.WriteHelp();
                return -1;
            }
        }

        static IFullLogger Log => SquirrelLocator.Current.GetService<ILogManager>().GetLogger(typeof(Program));

        static void Pack(PackOptions options)
        {
            using (Utility.WithTempDirectory(out var tmpDir)) {
                string nuspec = $@"
<?xml version=""1.0"" encoding=""utf-8""?>
<package>
  <metadata>
    <id>{options.packName}</id>
    <title>{options.packName}</title>
    <description>{options.packName}</description>
    <authors>{options.packAuthors}</authors>
    <version>{options.packVersion}</version>
  </metadata>
  <files>
    <file src=""**"" target=""lib\native\"" exclude=""{(options.includePdb ? "" : "*.pdb;")}*.nupkg;*.vshost.*""/>
  </files>
</package>
".Trim();
                var nuspecPath = Path.Combine(tmpDir, options.packName + ".nuspec");
                File.WriteAllText(nuspecPath, nuspec);

                HelperExe.NugetPack(nuspecPath, options.packDirectory, tmpDir).Wait();

                var nupkgPath = Directory.EnumerateFiles(tmpDir).Where(f => f.EndsWith(".nupkg")).FirstOrDefault();
                if (nupkgPath == null)
                    throw new Exception($"Failed to generate nupkg, unspecified error");

                options.package = nupkgPath;
                Releasify(options);
            }
        }

        static void Releasify(ReleasifyOptions options)
        {
            var targetDir = options.releaseDir ?? Path.Combine(".", "Releases");
            if (!Directory.Exists(targetDir)) {
                Directory.CreateDirectory(targetDir);
            }

            var frameworkVersion = options.framework;
            var signingOpts = options.signParams;
            var package = options.package;
            var baseUrl = options.baseUrl;
            var generateDeltas = !options.noDelta;
            var backgroundGif = options.splashImage;
            var setupIcon = options.setupIcon;

            if (!package.EndsWith(".nupkg", StringComparison.InvariantCultureIgnoreCase))
                throw new ArgumentException("package must be packed with nuget and end in '.nupkg'");

            // validate that the provided "frameworkVersion" is supported by Setup.exe
            if (!String.IsNullOrWhiteSpace(frameworkVersion)) {
                HelperExe.ValidateFrameworkVersion(frameworkVersion).Wait();
            }

            using var ud = Utility.WithTempDirectory(out var tempDir);

            // update icon for Update.exe if requested
            var updatePath = Path.Combine(tempDir, "Update.exe");
            if (options.updateIcon != null) {
                SingleFileBundle.UpdateSingleFileIcon(HelperExe.UpdatePath, updatePath, options.updateIcon).Wait();
            } else {
                File.Copy(HelperExe.UpdatePath, updatePath, true);
            }

            // Sign Update.exe so that virus scanners don't think we're pulling one over on them
            HelperExe.SignPEFile(updatePath, signingOpts).Wait();

            // copy input package to target output directory
            var di = new DirectoryInfo(targetDir);
            File.Copy(package, Path.Combine(di.FullName, Path.GetFileName(package)), true);

            var allNuGetFiles = di.EnumerateFiles()
                .Where(x => x.Name.EndsWith(".nupkg", StringComparison.InvariantCultureIgnoreCase));

            var toProcess = allNuGetFiles.Where(x => !x.Name.Contains("-delta") && !x.Name.Contains("-full"));
            var processed = new List<string>();

            var releaseFilePath = Path.Combine(di.FullName, "RELEASES");
            var previousReleases = new List<ReleaseEntry>();
            if (File.Exists(releaseFilePath)) {
                previousReleases.AddRange(ReleaseEntry.ParseReleaseFile(File.ReadAllText(releaseFilePath, Encoding.UTF8)));
            }

            foreach (var file in toProcess) {
                Log.Info("Creating release package: " + file.FullName);

                var rp = new ReleasePackage(file.FullName);
                rp.CreateReleasePackage(Path.Combine(di.FullName, rp.SuggestedReleaseFileName), contentsPostProcessHook: (pkgPath, frameworkName) => {

                    // create stub executable for all exe's in this package (except Squirrel!)
                    new DirectoryInfo(pkgPath).GetAllFilesRecursively()
                        .Where(x => x.Name.EndsWith(".exe", StringComparison.InvariantCultureIgnoreCase))
                        .Where(x => !x.Name.Contains("squirrel.exe", StringComparison.InvariantCultureIgnoreCase))
                        .Where(x => Utility.IsFileTopLevelInPackage(x.FullName, pkgPath))
                        .Where(x => Utility.ExecutableUsesWin32Subsystem(x.FullName))
                        .ForEachAsync(x => createExecutableStubForExe(x.FullName))
                        .Wait();

                    // sign all exe's in this package
                    new DirectoryInfo(pkgPath).GetAllFilesRecursively()
                        .Where(x => Utility.FileIsLikelyPEImage(x.Name))
                        .ForEachAsync(x => HelperExe.SignPEFile(x.FullName, signingOpts))
                        .Wait();

                    // copy Update.exe into package, so it can also be updated in both full/delta packages
                    File.Copy(updatePath, Path.Combine(pkgPath, "lib", frameworkName, "Squirrel.exe"), true);
                });

                processed.Add(rp.ReleasePackageFile);

                var prev = ReleaseEntry.GetPreviousRelease(previousReleases, rp, targetDir);
                if (prev != null && generateDeltas) {
                    var deltaBuilder = new DeltaPackageBuilder();
                    var dp = deltaBuilder.CreateDeltaPackage(prev, rp,
                        Path.Combine(di.FullName, rp.SuggestedReleaseFileName.Replace("full", "delta")));
                    processed.Insert(0, dp.InputPackageFile);
                }
            }

            foreach (var file in toProcess) {
                File.Delete(file.FullName);
            }

            var newReleaseEntries = processed
                .Select(packageFilename => ReleaseEntry.GenerateFromFile(packageFilename, baseUrl))
                .ToList();
            var distinctPreviousReleases = previousReleases
                .Where(x => !newReleaseEntries.Select(e => e.Version).Contains(x.Version));
            var releaseEntries = distinctPreviousReleases.Concat(newReleaseEntries).ToList();

            ReleaseEntry.WriteReleaseFile(releaseEntries, releaseFilePath);

            var targetSetupExe = Path.Combine(di.FullName, "Setup.exe");
            var newestFullRelease = Squirrel.EnumerableExtensions.MaxBy(releaseEntries, x => x.Version).Where(x => !x.IsDelta).First();

            File.Copy(HelperExe.SetupPath, targetSetupExe, true);
            var zipPath = createSetupEmbeddedZip(Path.Combine(di.FullName, newestFullRelease.Filename), updatePath);

            try {
                HelperExe.BundleZipIntoTargetSetupExe(targetSetupExe, zipPath, frameworkVersion, backgroundGif).Wait();
            } catch (Exception ex) {
                Log.ErrorException("Failed to update Setup.exe with new Zip file", ex);
                throw;
            } finally {
                File.Delete(zipPath);
            }

            Utility.Retry(() =>
                HelperExe.SetPEVersionBlockFromPackageInfo(targetSetupExe, new ZipPackage(package), setupIcon).Wait());

            HelperExe.SignPEFile(targetSetupExe, signingOpts).Wait();

            //if (generateMsi) {
            //    createMsiPackage(targetSetupExe, new ZipPackage(package), packageAs64Bit).Wait();

            //    if (signingOpts != null) {
            //        signPEFile(targetSetupExe.Replace(".exe", ".msi"), signingOpts).Wait();
            //    }
            //}

            Log.Info("Done");
        }

        static string createSetupEmbeddedZip(string fullPackage, string updatePath)
        {
            string tempPath;

            Log.Info("Start building embedded zip file for Setup.exe");
            using (Utility.WithTempDirectory(out tempPath, null)) {

                // copy package and Update.exe into temporary directory
                var tmpPackagePath = Path.Combine(tempPath, Path.GetFileName(fullPackage));
                Log.ErrorIfThrows(() => {
                    File.Copy(updatePath, Path.Combine(tempPath, "Update.exe"));
                    File.Copy(fullPackage, tmpPackagePath);
                }, "Failed to write package files to temp dir: " + tempPath);

                // remove Squirrel.exe from the setup package to save space in the installer
                Log.Info("Optimizing setup package for space savings");
                using (var stream = File.Open(tmpPackagePath, FileMode.Open, FileAccess.ReadWrite))
                using (var package = System.IO.Packaging.Package.Open(stream, FileMode.Open, FileAccess.ReadWrite)) {
                    var parts = package.GetParts();
                    var toDelete = parts.FirstOrDefault(p => p.Uri.ToString().EndsWith("Squirrel.exe", StringComparison.InvariantCultureIgnoreCase));
                    if (toDelete != null) {
                        package.DeletePart(toDelete.Uri);
                    }
                }

                // generate RELEASES file with only this current release
                var releases = new[] { ReleaseEntry.GenerateFromFile(tmpPackagePath) };
                ReleaseEntry.WriteReleaseFile(releases, Path.Combine(tempPath, "RELEASES"));

                // create zip bundle from temp directory
                Log.Info("Compressing Setup.exe bundle");
                var target = Path.GetTempFileName();
                File.Delete(target);
                Log.ErrorIfThrows(() =>
                    ZipFile.CreateFromDirectory(tempPath, target, CompressionLevel.Optimal, false),
                    "Failed to create Zip file from directory: " + tempPath);

                return target;
            }
        }

        static async Task createExecutableStubForExe(string exeToCopy)
        {
            var target = Path.Combine(
                Path.GetDirectoryName(exeToCopy),
                Path.GetFileNameWithoutExtension(exeToCopy) + "_ExecutionStub.exe");

            await Utility.CopyToAsync(HelperExe.StubExecutablePath, target);
            await HelperExe.CopyResourcesToTargetStubExe(exeToCopy, target);
        }
    }

    class ConsoleLogger : Squirrel.SimpleSplat.ILogger
    {
        readonly object gate = 42;
        public LogLevel Level { get; set; } = LogLevel.Info;
        public void Write(string message, LogLevel logLevel)
        {
            if (logLevel < Level) {
                return;
            }

            lock (gate) {
                string lvl = logLevel.ToString().Substring(0, 4).ToUpper();
                if (logLevel == LogLevel.Error || logLevel == LogLevel.Fatal) {
                    Utility.ConsoleWriteWithColor($"[{lvl}] {message}\r\n", ConsoleColor.Red);
                    Console.WriteLine();
                } else if (logLevel == LogLevel.Warn) {
                    Utility.ConsoleWriteWithColor($"[{lvl}] {message}\r\n", ConsoleColor.Yellow);
                    Console.WriteLine();
                } else {
                    Console.WriteLine($"[{lvl}] {message}");
                }
            }
        }
    }
}
