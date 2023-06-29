﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Squirrel.NuGet;
using Squirrel.SimpleSplat;
using System.Threading;
using Squirrel.Shell;
using Microsoft.Win32;

namespace Squirrel
{
    public partial class UpdateManager
    {
        internal class ApplyReleasesImpl : IEnableLogger
        {
            readonly string rootAppDirectory;

            public ApplyReleasesImpl(string rootAppDirectory)
            {
                this.rootAppDirectory = rootAppDirectory;
            }

            public async Task<string> ApplyReleases(UpdateInfo updateInfo, bool silentInstall, bool attemptingFullInstall, bool preferPackageNameForShortcut, Action<int> progress = null)
            {
                progress = progress ?? (_ => { });

                progress(0);

                // Progress range: 00 -> 40
                var release = await createFullPackagesFromDeltas(updateInfo.ReleasesToApply, updateInfo.CurrentlyInstalledVersion, new ApplyReleasesProgress(updateInfo.ReleasesToApply.Count, x => progress(CalculateProgress(x, 0, 40)))).ConfigureAwait(false);

                progress(40);

                if (release == null) {
                    if (attemptingFullInstall) {
                        this.Log().Info("No release to install, running the app");
                        await invokePostInstall(updateInfo.CurrentlyInstalledVersion.Version, false, true, silentInstall, preferPackageNameForShortcut).ConfigureAwait(false);
                    }

                    progress(100);
                    return getDirectoryForRelease(updateInfo.CurrentlyInstalledVersion.Version).FullName;
                }

                // Progress range: 40 -> 80
                var ret = await this.ErrorIfThrows(() => installPackageToAppDir(updateInfo, release, x => progress(CalculateProgress(x, 40, 80))),
                    "Failed to install package to app dir").ConfigureAwait(false);

                progress(80);

                var currentReleases = await this.ErrorIfThrows(() => updateLocalReleasesFile(),
                    "Failed to update local releases file").ConfigureAwait(false);

                progress(85);

                var newVersion = currentReleases.MaxBy(x => x.Version).First().Version;
                executeSelfUpdate(newVersion);

                progress(90);

                await this.ErrorIfThrows(() => invokePostInstall(newVersion, attemptingFullInstall, false, silentInstall, preferPackageNameForShortcut),
                    "Failed to invoke post-install").ConfigureAwait(false);

                progress(95);

                this.Log().Info("Starting fixPinnedExecutables");

                this.ErrorIfThrows(() => fixPinnedExecutables(updateInfo.FutureReleaseEntry.Version));

                progress(97);

                unshimOurselves();

                progress(98);

                try {
                    var currentVersion = updateInfo.CurrentlyInstalledVersion != null ?
                        updateInfo.CurrentlyInstalledVersion.Version : null;

                    await cleanDeadVersions(currentVersion, newVersion).ConfigureAwait(false);
                } catch (Exception ex) {
                    this.Log().WarnException("Failed to clean dead versions, continuing anyways", ex);
                }

                progress(100);

                return ret;
            }

            public async Task FullUninstall(bool preferPackageNameForShortcut)
            {
                var releases = getReleases();
                if (!releases.Any())
                    return;

                var (currentRelease, currentVersion) = releases.OrderByDescending(x => x.Version).FirstOrDefault();

                this.Log().Info("Starting full uninstall");
                if (currentRelease.Exists) {
                    try {
                        var squirrelAwareApps = SquirrelAwareExecutableDetector.GetAllSquirrelAwareApps(currentRelease.FullName);

                        if (isAppFolderDead(currentRelease.FullName)) throw new Exception("App folder is dead, but we're trying to uninstall it?");

                        var allApps = currentRelease.EnumerateFiles()
                            .Where(x => x.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            .Where(x => !x.Name.StartsWith("squirrel.", StringComparison.OrdinalIgnoreCase) && !x.Name.StartsWith("update.", StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        if (squirrelAwareApps.Count > 0) {
                            await squirrelAwareApps.ForEachAsync(async exe => {
                                using (var cts = new CancellationTokenSource()) {
                                    cts.CancelAfter(10 * 1000);

                                    try {
                                        await Utility.InvokeProcessAsync(exe, new string[] { "--squirrel-uninstall", currentVersion.ToString() }, cts.Token).ConfigureAwait(false);
                                    } catch (Exception ex) {
                                        this.Log().ErrorException("Failed to run cleanup hook, continuing: " + exe, ex);
                                    }
                                }
                            }, 1 /*at a time*/).ConfigureAwait(false);
                        } else {
                            allApps.ForEach(x => RemoveShortcutsForExecutable(x.Name, ShortcutLocation.StartMenu | ShortcutLocation.Desktop, preferPackageNameForShortcut));
                        }
                    } catch (Exception ex) {
                        this.Log().WarnException("Failed to run pre-uninstall hooks, uninstalling anyways", ex);
                    }
                }

                try {
                    this.ErrorIfThrows(() => fixPinnedExecutables(new SemanticVersion(255, 255, 255, 255), true));
                } catch { }

                this.ErrorIfThrows(() => Utility.DeleteFileOrDirectoryHardOrGiveUp(rootAppDirectory),
                    "Failed to delete app directory: " + rootAppDirectory);

                // NB: We drop this file here so that --checkInstall will ignore 
                // this folder - if we don't do this, users who "accidentally" run as 
                // administrator will find the app reinstalling itself on every
                // reboot
                if (!Directory.Exists(rootAppDirectory)) {
                    Directory.CreateDirectory(rootAppDirectory);
                }

                File.WriteAllText(Path.Combine(rootAppDirectory, ".dead"), " ");
            }

            public Dictionary<ShortcutLocation, ShellLink> GetShortcutsForExecutable(string exeName, ShortcutLocation locations, string programArguments, bool preferPackageName)
            {
                this.Log().Info("About to create shortcuts for {0}, rootAppDir {1}", exeName, rootAppDirectory);

                var releases = Utility.LoadLocalReleases(Utility.LocalReleaseFileForAppDir(rootAppDirectory));
                var thisRelease = Utility.FindCurrentVersion(releases);

                var zf = new ZipPackage(Path.Combine(
                    Utility.PackageDirectoryForAppDir(rootAppDirectory),
                    thisRelease.Filename));

                var exePath = Path.Combine(Utility.AppDirForRelease(rootAppDirectory, thisRelease), exeName);
                var fileVerInfo = FileVersionInfo.GetVersionInfo(exePath);

                var ret = new Dictionary<ShortcutLocation, ShellLink>();
                foreach (var f in (ShortcutLocation[]) Enum.GetValues(typeof(ShortcutLocation))) {
                    if (!locations.HasFlag(f)) continue;

                    var file = linkTargetForVersionInfo(f, zf, fileVerInfo, preferPackageName);
                    var appUserModelId = Utility.GetAppUserModelId(zf.Id, exeName);
                    var toastActivatorCLSDID = Utility.CreateGuidFromHash(appUserModelId).ToString();

                    this.Log().Info("Creating shortcut for {0} => {1}", exeName, file);
                    this.Log().Info("appUserModelId: {0} | toastActivatorCLSID: {1}", appUserModelId, toastActivatorCLSDID);

                    var target = Path.Combine(rootAppDirectory, exeName);
                    var sl = new ShellLink {
                        ShortCutFile = file,
                        Target = target,
                        IconPath = target,
                        IconIndex = 0,
                        WorkingDirectory = Path.GetDirectoryName(exePath),
                        Description = zf.ProductDescription,
                    };

                    if (!String.IsNullOrWhiteSpace(programArguments)) {
                        sl.Arguments += String.Format(" -a \"{0}\"", programArguments);
                    }

                    sl.SetAppUserModelId(appUserModelId);
                    sl.SetToastActivatorCLSID(toastActivatorCLSDID);

                    ret.Add(f, sl);
                }

                return ret;
            }

            public void CreateShortcutsForExecutable(string exeName, ShortcutLocation locations, bool updateOnly, string programArguments, string icon, bool preferPackageName)
            {
                this.Log().Info("About to create shortcuts for {0}, rootAppDir {1}", exeName, rootAppDirectory);

                var releases = Utility.LoadLocalReleases(Utility.LocalReleaseFileForAppDir(rootAppDirectory));
                var thisRelease = Utility.FindCurrentVersion(releases);

                var zf = new ZipPackage(Path.Combine(
                    Utility.PackageDirectoryForAppDir(rootAppDirectory),
                    thisRelease.Filename));

                var exePath = Path.Combine(Utility.AppDirForRelease(rootAppDirectory, thisRelease), exeName);
                var fileVerInfo = FileVersionInfo.GetVersionInfo(exePath);

                foreach (var f in (ShortcutLocation[]) Enum.GetValues(typeof(ShortcutLocation))) {
                    if (!locations.HasFlag(f)) continue;

                    var file = linkTargetForVersionInfo(f, zf, fileVerInfo, preferPackageName);
                    var fileExists = File.Exists(file);

                    // NB: If we've already installed the app, but the shortcut
                    // is no longer there, we have to assume that the user didn't
                    // want it there and explicitly deleted it, so we shouldn't
                    // annoy them by recreating it.
                    if (!fileExists && updateOnly) {
                        this.Log().Warn("Wanted to update shortcut {0} but it appears user deleted it", file);
                        continue;
                    }

                    this.Log().Info("Creating shortcut for {0} => {1}", exeName, file);

                    ShellLink sl;
                    this.ErrorIfThrows(() => Utility.Retry(() => {
                        File.Delete(file);

                        var target = Path.Combine(rootAppDirectory, exeName);
                        sl = new ShellLink {
                            Target = target,
                            IconPath = icon ?? target,
                            IconIndex = 0,
                            WorkingDirectory = Path.GetDirectoryName(exePath),
                            Description = zf.ProductDescription,
                        };

                        if (!String.IsNullOrWhiteSpace(programArguments)) {
                            sl.Arguments += String.Format(" -a \"{0}\"", programArguments);
                        }

                        var appUserModelId = Utility.GetAppUserModelId(zf.Id, exeName);
                        var toastActivatorCLSID = Utility.CreateGuidFromHash(appUserModelId).ToString();

                        sl.SetAppUserModelId(appUserModelId);
                        sl.SetToastActivatorCLSID(toastActivatorCLSID);

                        this.Log().Info("About to save shortcut: {0} (target {1}, workingDir {2}, args {3}, toastActivatorCSLID {4})", file, sl.Target, sl.WorkingDirectory, sl.Arguments, toastActivatorCLSID);
                        if (ModeDetector.InUnitTestRunner() == false) sl.Save(file);
                    }, 4), "Can't write shortcut: " + file);
                }

                fixPinnedExecutables(zf.Version);
            }

            public void RemoveShortcutsForExecutable(string exeName, ShortcutLocation locations, bool preferPackageName)
            {
                var releases = Utility.LoadLocalReleases(Utility.LocalReleaseFileForAppDir(rootAppDirectory));
                var thisRelease = Utility.FindCurrentVersion(releases);

                var zf = new ZipPackage(Path.Combine(
                    Utility.PackageDirectoryForAppDir(rootAppDirectory),
                    thisRelease.Filename));

                var fileVerInfo = FileVersionInfo.GetVersionInfo(
                    Path.Combine(Utility.AppDirForRelease(rootAppDirectory, thisRelease), exeName));

                foreach (var f in (ShortcutLocation[]) Enum.GetValues(typeof(ShortcutLocation))) {
                    if (!locations.HasFlag(f)) continue;

                    var file = linkTargetForVersionInfo(f, zf, fileVerInfo, preferPackageName);

                    this.Log().Info("Removing shortcut for {0} => {1}", exeName, file);

                    this.ErrorIfThrows(() => {
                        if (File.Exists(file)) File.Delete(file);
                    }, "Couldn't delete shortcut: " + file);
                }

                fixPinnedExecutables(zf.Version);
            }

            Task<string> installPackageToAppDir(UpdateInfo updateInfo, ReleaseEntry release, Action<int> progressCallback)
            {
                return Task.Run(async () => {
                    var target = getDirectoryForRelease(release.Version);

                    // NB: This might happen if we got killed partially through applying the release
                    if (target.Exists) {
                        this.Log().Warn("Found partially applied release folder, killing it: " + target.FullName);
                        Utility.DeleteFileOrDirectoryHardOrGiveUp(target.FullName);
                    }

                    target.Create();

                    this.Log().Info("Writing files to app directory: {0}", target.FullName);
                    await ReleasePackage.ExtractZipForInstall(
                        Path.Combine(updateInfo.PackageDirectory, release.Filename),
                        target.FullName,
                        rootAppDirectory,
                        progressCallback).ConfigureAwait(false);

                    return target.FullName;
                });
            }

            async Task<ReleaseEntry> createFullPackagesFromDeltas(IEnumerable<ReleaseEntry> releasesToApply, ReleaseEntry currentVersion, ApplyReleasesProgress progress)
            {
                Contract.Requires(releasesToApply != null);

                progress = progress ?? new ApplyReleasesProgress(releasesToApply.Count(), x => { });

                // If there are no remote releases at all, bail
                if (!releasesToApply.Any()) {
                    return null;
                }

                // If there are no deltas in our list, we're already done
                if (releasesToApply.All(x => !x.IsDelta)) {
                    return releasesToApply.MaxBy(x => x.Version).FirstOrDefault();
                }

                if (!releasesToApply.All(x => x.IsDelta)) {
                    throw new Exception("Cannot apply combinations of delta and full packages");
                }

                // Progress calculation is "complex" here. We need to known how many releases, and then give each release a similar amount of
                // progress. For example, when applying 5 releases:
                //
                // release 1: 00 => 20
                // release 2: 20 => 40
                // release 3: 40 => 60
                // release 4: 60 => 80
                // release 5: 80 => 100
                // 

                // Smash together our base full package and the nearest delta
                var ret = await Task.Run(() => {
                    var basePkg = new ReleasePackage(Path.Combine(rootAppDirectory, "packages", currentVersion.Filename));
                    var deltaPkg = new ReleasePackage(Path.Combine(rootAppDirectory, "packages", releasesToApply.First().Filename));

                    var deltaBuilder = new DeltaPackageBuilder(Directory.GetParent(rootAppDirectory).FullName);

                    return deltaBuilder.ApplyDeltaPackage(basePkg, deltaPkg,
                        Regex.Replace(deltaPkg.InputPackageFile, @"-delta.nupkg$", ".nupkg", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                        x => progress.ReportReleaseProgress(x));
                }).ConfigureAwait(false);

                progress.FinishRelease();

                if (releasesToApply.Count() == 1) {
                    return ReleaseEntry.GenerateFromFile(ret.InputPackageFile);
                }

                var fi = new FileInfo(ret.InputPackageFile);
                var entry = ReleaseEntry.GenerateFromFile(fi.OpenRead(), fi.Name);

                // Recursively combine the rest of them
                return await createFullPackagesFromDeltas(releasesToApply.Skip(1), entry, progress).ConfigureAwait(false);
            }

            void executeSelfUpdate(SemanticVersion currentVersion)
            {
                var targetDir = getDirectoryForRelease(currentVersion);
                var newSquirrel = Path.Combine(targetDir.FullName, "Squirrel.exe");
                if (!File.Exists(newSquirrel)) {
                    return;
                }

                // If we're running in the context of Update.exe, we can't 
                // update ourselves. Instead, ask the new Update.exe to do it
                // once we exit
                var ourLocation = SquirrelRuntimeInfo.EntryExePath;
                if (ourLocation != null && Path.GetFileName(ourLocation).Equals("update.exe", StringComparison.OrdinalIgnoreCase)) {
                    var appName = targetDir.Parent.Name;

                    Process.Start(newSquirrel, "--updateSelf=" + ourLocation);
                    return;
                }

                // If we're *not* Update.exe, this is easy, it's just a file copy
                Utility.Retry(() =>
                    File.Copy(newSquirrel, Path.Combine(targetDir.Parent.FullName, "Update.exe"), true));
            }

            async Task invokePostInstall(SemanticVersion currentVersion, bool isInitialInstall, bool firstRunOnly, bool silentInstall, bool preferPackageNameForShortcut)
            {
                var targetDir = getDirectoryForRelease(currentVersion);
                var command = isInitialInstall ? "--squirrel-install" : "--squirrel-updated";
                var args = new string[] { command, currentVersion.ToString() };

                var squirrelApps = SquirrelAwareExecutableDetector.GetAllSquirrelAwareApps(targetDir.FullName);

                this.Log().Info("Squirrel Enabled Apps: [{0}]", String.Join(",", squirrelApps));

                // For each app, run the install command in-order and wait
                if (!firstRunOnly) await squirrelApps.ForEachAsync(async exe => {
                    using (var cts = new CancellationTokenSource()) {
                        cts.CancelAfter(30 * 1000);

                        try {
                            await Utility.InvokeProcessAsync(exe, args, cts.Token, Path.GetDirectoryName(exe)).ConfigureAwait(false);
                        } catch (Exception ex) {
                            this.Log().ErrorException("Couldn't run Squirrel hook, continuing: " + exe, ex);
                        }
                    }
                }, 1 /* at a time */).ConfigureAwait(false);

                // If this is the first run, we run the apps with first-run and 
                // *don't* wait for them, since they're probably the main EXE
                if (squirrelApps.Count == 0) {
                    this.Log().Warn("No apps are marked as Squirrel-aware! Going to run them all");

                    squirrelApps = targetDir.EnumerateFiles()
                        .Where(x => x.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        .Where(x => !x.Name.StartsWith("squirrel.", StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.FullName)
                        .ToList();

                    // Create shortcuts for apps automatically if they didn't
                    // create any Squirrel-aware apps
                    squirrelApps.ForEach(x => CreateShortcutsForExecutable(Path.GetFileName(x), ShortcutLocation.Desktop | ShortcutLocation.StartMenu, isInitialInstall == false, null, null, preferPackageNameForShortcut));
                }

                if (!isInitialInstall || silentInstall) return;

                var firstRunParam = isInitialInstall ? "--squirrel-firstrun" : "";
                squirrelApps
                    .Select(exe => new ProcessStartInfo(exe, firstRunParam) { WorkingDirectory = Path.GetDirectoryName(exe) })
                    .ForEach(info => Process.Start(info));
            }

            void fixPinnedExecutables(SemanticVersion newCurrentVersion, bool removeAll = false)
            {
                if (Environment.OSVersion.Version < new Version(6, 1)) {
                    this.Log().Warn("fixPinnedExecutables: Found OS Version '{0}', exiting...", Environment.OSVersion.VersionString);
                    return;
                }

                var newCurrentFolder = "app-" + newCurrentVersion;
                var newAppPath = Path.Combine(rootAppDirectory, newCurrentFolder);

                var taskbarPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft\\Internet Explorer\\Quick Launch\\User Pinned\\TaskBar");

                if (!Directory.Exists(taskbarPath)) {
                    this.Log().Info("fixPinnedExecutables: PinnedExecutables directory doesn't exitsts, skiping...");
                    return;
                }

                var resolveLink = new Func<FileInfo, ShellLink>(file => {
                    try {
                        this.Log().Debug("Examining Pin: " + file);
                        return new ShellLink(file.FullName);
                    } catch (Exception ex) {
                        var message = String.Format("File '{0}' could not be converted into a valid ShellLink", file.FullName);
                        this.Log().WarnException(message, ex);
                        return null;
                    }
                });

                var shellLinks = (new DirectoryInfo(taskbarPath)).GetFiles("*.lnk").Select(resolveLink).ToArray();

                foreach (var shortcut in shellLinks) {
                    try {
                        if (shortcut == null) continue;
                        if (String.IsNullOrWhiteSpace(shortcut.Target)) continue;
                        if (!Utility.IsFileInDirectory(shortcut.Target, rootAppDirectory)) continue;

                        if (removeAll) {
                            Utility.DeleteFileOrDirectoryHard(shortcut.ShortCutFile);
                        } else {
                            updateLink(shortcut, newAppPath);
                        }

                    } catch (Exception ex) {
                        var message = String.Format("fixPinnedExecutables: shortcut failed: {0}", shortcut.Target);
                        this.Log().ErrorException(message, ex);
                    }
                }
            }

            void updateLink(ShellLink shortcut, string newAppPath)
            {
                this.Log().Info("Processing shortcut '{0}'", shortcut.ShortCutFile);

                var target = Environment.ExpandEnvironmentVariables(shortcut.Target);
                var targetIsUpdateDotExe = target.EndsWith("update.exe", StringComparison.OrdinalIgnoreCase);

                this.Log().Info("Old shortcut target: '{0}'", target);

                // NB: In 1.5.0 we accidentally fixed the target of pinned shortcuts but left the arguments,
                // so if we find a shortcut with --processStart in the args, we're gonna stomp it even though
                // what we _should_ do is stomp it only if the target is Update.exe
                if (shortcut.Arguments.Contains("--processStart")) {
                    shortcut.Arguments = "";
                }

                if (!targetIsUpdateDotExe) {
                    target = Path.Combine(rootAppDirectory, Path.GetFileName(shortcut.Target));
                } else {
                    target = Path.Combine(rootAppDirectory, Path.GetFileName(shortcut.IconPath));
                }

                this.Log().Info("New shortcut target: '{0}'", target);

                shortcut.WorkingDirectory = newAppPath;
                shortcut.Target = target;

                this.Log().Info("Old iconPath is: '{0}'", shortcut.IconPath);
                shortcut.IconPath = target;
                shortcut.IconIndex = 0;

                this.ErrorIfThrows(() => Utility.Retry(() => shortcut.Save(), 2), "Couldn't write shortcut " + shortcut.ShortCutFile);
                this.Log().Info("Finished shortcut successfully");
            }

            internal void unshimOurselves()
            {
                new[] { RegistryView.Registry32, RegistryView.Registry64 }.ForEach(view => {
                    var baseKey = default(RegistryKey);
                    var regKey = default(RegistryKey);

                    try {
                        baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                        regKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers");

                        if (regKey == null) return;

                        var toDelete = regKey.GetValueNames()
                            .Where(x => Utility.IsFileInDirectory(x, rootAppDirectory))
                            .ToList();

                        toDelete.ForEach(x =>
                            this.Log().LogIfThrows(LogLevel.Warn, "Failed to delete key: " + x,
                                () => regKey.DeleteValue(x)));
                    } catch (Exception e) {
                        this.Log().WarnException("Couldn't rewrite shim RegKey, most likely no apps are shimmed", e);
                    } finally {
                        if (regKey != null) regKey.Dispose();
                        if (baseKey != null) baseKey.Dispose();
                    }
                });
            }

            // NB: Once we uninstall the old version of the app, we try to schedule
            // it to be deleted at next reboot. Unfortunately, depending on whether
            // the user has admin permissions, this can fail. So as a failsafe,
            // before we try to apply any update, we assume previous versions in the
            // directory are "dead" (i.e. already uninstalled, but not deleted), and
            // we blow them away. This is to make sure that we don't attempt to run
            // an uninstaller on an already-uninstalled version.
            async Task cleanDeadVersions(SemanticVersion currentVersion, SemanticVersion newVersion, bool forceUninstall = false)
            {
                if (newVersion == null) return;

                var di = new DirectoryInfo(rootAppDirectory);
                if (!di.Exists) return;

                this.Log().Info("cleanDeadVersions: checking for version {0}", newVersion);

                string currentVersionFolder = null;
                if (currentVersion != null) {
                    currentVersionFolder = getDirectoryForRelease(currentVersion).Name;
                    this.Log().Info("cleanDeadVersions: exclude current version folder {0}", currentVersionFolder);
                }

                string newVersionFolder = null;
                if (newVersion != null) {
                    newVersionFolder = getDirectoryForRelease(newVersion).Name;
                    this.Log().Info("cleanDeadVersions: exclude new version folder {0}", newVersionFolder);
                }

                // NB: If we try to access a directory that has already been 
                // scheduled for deletion by MoveFileEx it throws what seems like
                // NT's only error code, ERROR_ACCESS_DENIED. Squelch errors that
                // come from here.
                var toCleanup = di.GetDirectories()
                    .Where(x => x.Name.ToLowerInvariant().Contains("app-"))
                    .Where(x => x.Name != newVersionFolder && x.Name != currentVersionFolder)
                    .Where(x => !isAppFolderDead(x.FullName));

                if (forceUninstall == false) {
                    await toCleanup.ForEachAsync(async x => {
                        var squirrelApps = SquirrelAwareExecutableDetector.GetAllSquirrelAwareApps(x.FullName);
                        var args = new string[] { "--squirrel-obsolete", x.Name.Replace("app-", "") };

                        if (squirrelApps.Count > 0) {
                            // For each app, run the install command in-order and wait
                            await squirrelApps.ForEachAsync(async exe => {
                                using (var cts = new CancellationTokenSource()) {
                                    cts.CancelAfter(10 * 1000);

                                    try {
                                        await Utility.InvokeProcessAsync(exe, args, cts.Token).ConfigureAwait(false);
                                    } catch (Exception ex) {
                                        this.Log().ErrorException("Coudln't run Squirrel hook, continuing: " + exe, ex);
                                    }
                                }
                            }, 1 /* at a time */).ConfigureAwait(false);
                        }
                    }).ConfigureAwait(false);
                }

                // Include dead folders in folders to :fire:
                toCleanup = di.GetDirectories()
                    .Where(x => x.Name.ToLowerInvariant().Contains("app-"))
                    .Where(x => x.Name != newVersionFolder && x.Name != currentVersionFolder);

                // Get the current process list in an attempt to not burn 
                // directories which have running processes
                var runningProcesses = Utility.EnumerateProcesses();

                // Finally, clean up the app-X.Y.Z directories
                await toCleanup.ForEachAsync(x => {
                    try {
                        if (runningProcesses.All(p => p.Item1 == null || !p.Item1.StartsWith(x.FullName, StringComparison.OrdinalIgnoreCase))) {
                            Utility.DeleteFileOrDirectoryHardOrGiveUp(x.FullName);
                        }

                        if (Directory.Exists(x.FullName)) {
                            // NB: If we cannot clean up a directory, we need to make 
                            // sure that anyone finding it later won't attempt to run
                            // Squirrel events on it. We'll mark it with a .dead file
                            markAppFolderAsDead(x.FullName);
                        }
                    } catch (UnauthorizedAccessException ex) {
                        this.Log().WarnException("Couldn't delete directory: " + x.FullName, ex);

                        // NB: Same deal as above
                        markAppFolderAsDead(x.FullName);
                    }
                }).ConfigureAwait(false);

                // Clean up the packages directory too
                var releasesFile = Utility.LocalReleaseFileForAppDir(rootAppDirectory);
                var entries = ReleaseEntry.ParseReleaseFile(File.ReadAllText(releasesFile, Encoding.UTF8));
                var pkgDir = Utility.PackageDirectoryForAppDir(rootAppDirectory);
                var releaseEntry = default(ReleaseEntry);

                foreach (var entry in entries) {
                    if (entry.Version == newVersion) {
                        releaseEntry = ReleaseEntry.GenerateFromFile(Path.Combine(pkgDir, entry.Filename));
                        continue;
                    }

                    File.Delete(Path.Combine(pkgDir, entry.Filename));
                }

                ReleaseEntry.WriteReleaseFile(new[] { releaseEntry }, releasesFile);
            }

            static void markAppFolderAsDead(string appFolderPath)
            {
                File.WriteAllText(Path.Combine(appFolderPath, ".dead"), "");
            }

            static bool isAppFolderDead(string appFolderPath)
            {
                return File.Exists(Path.Combine(appFolderPath, ".dead"));
            }

            internal async Task<List<ReleaseEntry>> updateLocalReleasesFile()
            {
                return await Task.Run(() => ReleaseEntry.BuildReleasesFile(Utility.PackageDirectoryForAppDir(rootAppDirectory))).ConfigureAwait(false);
            }

            IEnumerable<(DirectoryInfo Directory, SemanticVersion Version)> getReleases()
            {
                var rootDirectory = new DirectoryInfo(rootAppDirectory);

                if (!rootDirectory.Exists) return Enumerable.Empty<(DirectoryInfo Directory, SemanticVersion Version)>();

                return rootDirectory.GetDirectories()
                    .Where(x => x.Name.StartsWith("app-", StringComparison.InvariantCultureIgnoreCase))
                    .Select(x => (x, new SemanticVersion(x.Name.Substring(4))));
            }

            DirectoryInfo getDirectoryForRelease(SemanticVersion releaseVersion)
            {
                return new DirectoryInfo(Path.Combine(rootAppDirectory, "app-" + releaseVersion));
            }

            string linkTargetForVersionInfo(ShortcutLocation location, IPackage package, FileVersionInfo versionInfo, bool preferPackageName = false)
            {
                var possibleProductNames = new[] {
                    !preferPackageName ? versionInfo.ProductName : null, //put assembly product name first if package name is not preferred
                    package.ProductName,
                    preferPackageName ? versionInfo.ProductName : null, //put assembly product name after package name if it is
                    versionInfo.FileDescription,
                    Path.GetFileNameWithoutExtension(versionInfo.FileName)
                };

                var possibleCompanyNames = new[] {
                    versionInfo.CompanyName,
                    package.ProductCompany,
                };

                var prodName = possibleCompanyNames.First(x => !String.IsNullOrWhiteSpace(x));
                var pkgName = possibleProductNames.First(x => !String.IsNullOrWhiteSpace(x));

                return getLinkTarget(location, pkgName, prodName);
            }

            string getLinkTarget(ShortcutLocation location, string title, string applicationName, bool createDirectoryIfNecessary = true)
            {
                var dir = default(string);

                switch (location) {
                case ShortcutLocation.Desktop:
                    dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    break;
                case ShortcutLocation.StartMenu:
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", applicationName);
                    break;
                case ShortcutLocation.StartMenuRoot:
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
                    break;
                case ShortcutLocation.Startup:
                    dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                    break;
                case ShortcutLocation.AppRoot:
                    dir = rootAppDirectory;
                    break;
                }

                if (createDirectoryIfNecessary && !Directory.Exists(dir)) {
                    Directory.CreateDirectory(dir);
                }

                return Path.Combine(dir, title + ".lnk");
            }
        }
    }
}
