﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Squirrel;
using Squirrel.Tests.TestHelpers;
using Xunit;
using System.Net;
using Squirrel.NuGet;
using System.Net.Http;

namespace Squirrel.Tests
{
    public class UpdateManagerTests
    {
        public class CreateUninstallerRegKeyTests
        {
            [Fact]
            public async Task CallingMethodTwiceShouldUpdateInstaller()
            {
                string remotePkgPath;
                string path;

                using (Utility.WithTempDirectory(out path)) {
                    using (Utility.WithTempDirectory(out remotePkgPath))
                    using (var mgr = new UpdateManager(remotePkgPath, "theApp", path)) {
                        IntegrationTestHelper.CreateFakeInstalledApp("1.0.0.1", remotePkgPath);
                        await mgr.FullInstall();
                    }

                    using (var mgr = new UpdateManager("http://lol", "theApp", path)) {
                        await mgr.CreateUninstallerRegistryEntry();
                        var regKey = await mgr.CreateUninstallerRegistryEntry();

                        Assert.False(String.IsNullOrWhiteSpace((string) regKey.GetValue("DisplayName")));

                        mgr.RemoveUninstallerRegistryEntry();
                    }

                    // NB: Squirrel-Aware first-run might still be running, slow
                    // our roll before blowing away the temp path
                    Thread.Sleep(1000);
                }

                var key = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default)
                    .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");

                using (key) {
                    Assert.False(key.GetSubKeyNames().Contains("theApp"));
                }
            }
        }

        public class UpdateLocalReleasesTests
        {
            [Fact]
            public async Task UpdateLocalReleasesSmokeTest()
            {
                string tempDir;
                using (Utility.WithTempDirectory(out tempDir)) {
                    var appDir = Path.Combine(tempDir, "theApp");
                    var packageDir = Directory.CreateDirectory(Path.Combine(appDir, "packages"));

                    new[] {
                        "Squirrel.Core.1.0.0.0-full.nupkg",
                        "Squirrel.Core.1.1.0.0-delta.nupkg",
                        "Squirrel.Core.1.1.0.0-full.nupkg",
                    }.ForEach(x => File.Copy(IntegrationTestHelper.GetPath("fixtures", x), Path.Combine(tempDir, "theApp", "packages", x)));

                    var fixture = new UpdateManager.ApplyReleasesImpl(appDir);

                    await fixture.updateLocalReleasesFile();

                    var releasePath = Path.Combine(packageDir.FullName, "RELEASES");
                    File.Exists(releasePath).ShouldBeTrue();

                    var entries = ReleaseEntry.ParseReleaseFile(File.ReadAllText(releasePath, Encoding.UTF8));
                    entries.Count().ShouldEqual(3);
                }
            }

            [Fact]
            public async Task InitialInstallSmokeTest()
            {
                string tempDir;
                using (Utility.WithTempDirectory(out tempDir)) {
                    var remotePackageDir = Directory.CreateDirectory(Path.Combine(tempDir, "remotePackages"));
                    var localAppDir = Path.Combine(tempDir, "theApp");

                    new[] {
                        "Squirrel.Core.1.0.0.0-full.nupkg",
                    }.ForEach(x => File.Copy(IntegrationTestHelper.GetPath("fixtures", x), Path.Combine(remotePackageDir.FullName, x)));

                    using (var fixture = new UpdateManager(remotePackageDir.FullName, "theApp", tempDir)) {
                        await fixture.FullInstall();
                    }

                    var releasePath = Path.Combine(localAppDir, "packages", "RELEASES");
                    File.Exists(releasePath).ShouldBeTrue();

                    var entries = ReleaseEntry.ParseReleaseFile(File.ReadAllText(releasePath, Encoding.UTF8));
                    entries.Count().ShouldEqual(1);

                    new[] {
                        "ReactiveUI.dll",
                        "NSync.Core.dll",
                    }.ForEach(x => File.Exists(Path.Combine(localAppDir, "app-1.0.0.0", x)).ShouldBeTrue());
                }
            }

            [Fact]
            public async Task SpecialCharactersInitialInstallTest()
            {
                string tempDir;
                using (Utility.WithTempDirectory(out tempDir)) {
                    var remotePackageDir = Directory.CreateDirectory(Path.Combine(tempDir, "remotePackages"));
                    var localAppDir = Path.Combine(tempDir, "theApp");

                    new[] {
                        "SpecialCharacters-0.1.0-full.nupkg",
                    }.ForEach(x => File.Copy(IntegrationTestHelper.GetPath("fixtures", x), Path.Combine(remotePackageDir.FullName, x)));

                    using (var fixture = new UpdateManager(remotePackageDir.FullName, "theApp", tempDir)) {
                        await fixture.FullInstall();
                    }

                    var releasePath = Path.Combine(localAppDir, "packages", "RELEASES");
                    File.Exists(releasePath).ShouldBeTrue();

                    var entries = ReleaseEntry.ParseReleaseFile(File.ReadAllText(releasePath, Encoding.UTF8));
                    entries.Count().ShouldEqual(1);

                    new[] {
                        "file space name.txt"
                    }.ForEach(x => File.Exists(Path.Combine(localAppDir, "app-0.1.0", x)).ShouldBeTrue());
                }
            }

            [Fact]
            public async Task WhenBothFilesAreInSyncNoUpdatesAreApplied()
            {
                string tempDir;
                using (Utility.WithTempDirectory(out tempDir)) {
                    var appDir = Path.Combine(tempDir, "theApp");
                    var localPackages = Path.Combine(appDir, "packages");
                    var remotePackages = Path.Combine(tempDir, "releases");
                    Directory.CreateDirectory(localPackages);
                    Directory.CreateDirectory(remotePackages);

                    new[] {
                        "Squirrel.Core.1.0.0.0-full.nupkg",
                        "Squirrel.Core.1.1.0.0-delta.nupkg",
                        "Squirrel.Core.1.1.0.0-full.nupkg",
                    }.ForEach(x => {
                        var path = IntegrationTestHelper.GetPath("fixtures", x);
                        File.Copy(path, Path.Combine(localPackages, x));
                        File.Copy(path, Path.Combine(remotePackages, x));
                    });

                    var fixture = new UpdateManager.ApplyReleasesImpl(appDir);

                    // sync both release files
                    await fixture.updateLocalReleasesFile();
                    ReleaseEntry.BuildReleasesFile(remotePackages);

                    // check for an update
                    UpdateInfo updateInfo;
                    using (var mgr = new UpdateManager(remotePackages, "theApp", tempDir, new FakeDownloader())) {
                        updateInfo = await mgr.CheckForUpdate();
                    }

                    Assert.NotNull(updateInfo);
                    Assert.Empty(updateInfo.ReleasesToApply);
                }
            }

            [Fact]
            public async Task WhenRemoteReleasesDoNotHaveDeltasNoUpdatesAreApplied()
            {
                string tempDir;
                using (Utility.WithTempDirectory(out tempDir)) {
                    var appDir = Path.Combine(tempDir, "theApp");
                    var localPackages = Path.Combine(appDir, "packages");
                    var remotePackages = Path.Combine(tempDir, "releases");
                    Directory.CreateDirectory(localPackages);
                    Directory.CreateDirectory(remotePackages);

                    new[] {
                        "Squirrel.Core.1.0.0.0-full.nupkg",
                        "Squirrel.Core.1.1.0.0-delta.nupkg",
                        "Squirrel.Core.1.1.0.0-full.nupkg",
                    }.ForEach(x => {
                        var path = IntegrationTestHelper.GetPath("fixtures", x);
                        File.Copy(path, Path.Combine(localPackages, x));
                    });

                    new[] {
                        "Squirrel.Core.1.0.0.0-full.nupkg",
                        "Squirrel.Core.1.1.0.0-full.nupkg",
                    }.ForEach(x => {
                        var path = IntegrationTestHelper.GetPath("fixtures", x);
                        File.Copy(path, Path.Combine(remotePackages, x));
                    });

                    var fixture = new UpdateManager.ApplyReleasesImpl(appDir);

                    // sync both release files
                    await fixture.updateLocalReleasesFile();
                    ReleaseEntry.BuildReleasesFile(remotePackages);

                    UpdateInfo updateInfo;
                    using (var mgr = new UpdateManager(remotePackages, "theApp", tempDir, new FakeDownloader())) {
                        updateInfo = await mgr.CheckForUpdate();
                    }

                    Assert.NotNull(updateInfo);
                    Assert.Empty(updateInfo.ReleasesToApply);
                }
            }

            [Fact]
            public async Task WhenTwoRemoteUpdatesAreAvailableChoosesDeltaVersion()
            {
                string tempDir;
                using (Utility.WithTempDirectory(out tempDir)) {
                    var appDir = Path.Combine(tempDir, "theApp");
                    var localPackages = Path.Combine(appDir, "packages");
                    var remotePackages = Path.Combine(tempDir, "releases");
                    Directory.CreateDirectory(localPackages);
                    Directory.CreateDirectory(remotePackages);

                    new[] { "Squirrel.Core.1.0.0.0-full.nupkg", }.ForEach(x => {
                        var path = IntegrationTestHelper.GetPath("fixtures", x);
                        File.Copy(path, Path.Combine(localPackages, x));
                    });

                    new[] {
                        "Squirrel.Core.1.0.0.0-full.nupkg",
                        "Squirrel.Core.1.1.0.0-delta.nupkg",
                        "Squirrel.Core.1.1.0.0-full.nupkg",
                    }.ForEach(x => {
                        var path = IntegrationTestHelper.GetPath("fixtures", x);
                        File.Copy(path, Path.Combine(remotePackages, x));
                    });

                    var fixture = new UpdateManager.ApplyReleasesImpl(appDir);

                    // sync both release files
                    await fixture.updateLocalReleasesFile();
                    ReleaseEntry.BuildReleasesFile(remotePackages);

                    using (var mgr = new UpdateManager(remotePackages, "theApp", tempDir, new FakeDownloader())) {
                        UpdateInfo updateInfo;
                        updateInfo = await mgr.CheckForUpdate();
                        Assert.True(updateInfo.ReleasesToApply.First().IsDelta);

                        updateInfo = await mgr.CheckForUpdate(ignoreDeltaUpdates: true);
                        Assert.False(updateInfo.ReleasesToApply.First().IsDelta);
                    }
                }
            }

            [Fact]
            public async Task WhenFolderDoesNotExistThrowHelpfulError()
            {
                string tempDir;
                using (Utility.WithTempDirectory(out tempDir)) {
                    var directory = Path.Combine(tempDir, "missing-folder");
                    var fixture = new UpdateManager(directory, "MyAppName");

                    using (fixture) {
                        await Assert.ThrowsAsync<Exception>(() => fixture.CheckForUpdate());
                    }
                }
            }

            [Fact]
            public async Task WhenReleasesFileDoesntExistThrowACustomError()
            {
                string tempDir;
                using (Utility.WithTempDirectory(out tempDir)) {
                    var fixture = new UpdateManager(tempDir, "MyAppName");

                    using (fixture) {
                        await Assert.ThrowsAsync<Exception>(() => fixture.CheckForUpdate());
                    }
                }
            }

            [Fact]
            public async Task WhenReleasesFileIsBlankThrowAnException()
            {
                string tempDir;
                using (Utility.WithTempDirectory(out tempDir)) {
                    var fixture = new UpdateManager(tempDir, "MyAppName");
                    File.WriteAllText(Path.Combine(tempDir, "RELEASES"), "");

                    using (fixture) {
                        await Assert.ThrowsAsync(typeof(Exception), () => fixture.CheckForUpdate());
                    }
                }
            }

            [Fact]
            public async Task WhenUrlResultsInWebExceptionWeShouldThrow()
            {
                // This should result in a WebException (which gets caught) unless you can actually access http://lol
                using (var fixture = new UpdateManager("http://lol", "theApp")) {
                    await Assert.ThrowsAsync(typeof(HttpRequestException), () => fixture.CheckForUpdate());
                }
            }

            [Fact]
            public void IsInstalledHandlesInvalidDirectoryStructure()
            {
                using (Utility.WithTempDirectory(out var tempDir)) {
                    Directory.CreateDirectory(Path.Combine(tempDir, "theApp"));
                    Directory.CreateDirectory(Path.Combine(tempDir, "theApp", "app-1.0.1"));
                    Directory.CreateDirectory(Path.Combine(tempDir, "theApp", "wrongDir"));
                    File.WriteAllText(Path.Combine(tempDir, "theApp", "Update.exe"), "1");
                    using (var fixture = new UpdateManager("http://lol", "theApp", tempDir)) {
                        Assert.Null(fixture.CurrentlyInstalledVersion(Path.Combine(tempDir, "app.exe")));
                        Assert.Null(fixture.CurrentlyInstalledVersion(Path.Combine(tempDir, "theApp", "app.exe")));
                        Assert.Null(fixture.CurrentlyInstalledVersion(Path.Combine(tempDir, "theApp", "wrongDir", "app.exe")));
                        Assert.Equal(new SemanticVersion(1, 0, 1, 9), fixture.CurrentlyInstalledVersion(Path.Combine(tempDir, "theApp", "app-1.0.1.9", "app.exe")));
                    }
                }
            }

            [Fact]
            public void CurrentlyInstalledVersionDoesNotThrow()
            {
                using var fixture = new UpdateManager();
                Assert.Null(fixture.CurrentlyInstalledVersion());
                Assert.False(fixture.IsInstalledApp);
            }

            [Fact]
            public void SetModelIdDoesNotThrow()
            {
                using (var fixture = new UpdateManager())
                    fixture.SetProcessAppUserModelId();

                using (var fixture2 = new UpdateManager("", "TestASD"))
                    fixture2.SetProcessAppUserModelId();
            }

            [Theory]
            [InlineData(0, 0, 25, 0)]
            [InlineData(12, 0, 25, 3)]
            [InlineData(55, 0, 25, 13)]
            [InlineData(100, 0, 25, 25)]
            [InlineData(0, 25, 50, 25)]
            [InlineData(12, 25, 50, 28)]
            [InlineData(55, 25, 50, 38)]
            [InlineData(100, 25, 50, 50)]
            public void CalculatesPercentageCorrectly(int percentageOfCurrentStep, int stepStartPercentage, int stepEndPercentage, int expectedPercentage)
            {
                var percentage = UpdateManager.CalculateProgress(percentageOfCurrentStep, stepStartPercentage, stepEndPercentage);

                Assert.Equal(expectedPercentage, percentage);
            }

            [Fact]
            public void CalculatesPercentageCorrectlyForUpdateExe()
            {
                // Note: this mimicks the update.exe progress reporting of multiple steps

                var progress = new List<int>();

                // 3 % (3 stages), check for updates
                foreach (var step in new[] { 0, 33, 66, 100 }) {
                    progress.Add(UpdateManager.CalculateProgress(step, 0, 3));

                    Assert.InRange(progress.Last(), 0, 3);
                }

                Assert.Equal(3, progress.Last());

                // 3 - 30 %, download releases
                for (var step = 0; step <= 100; step++) {
                    progress.Add(UpdateManager.CalculateProgress(step, 3, 30));

                    Assert.InRange(progress.Last(), 3, 30);
                }

                Assert.Equal(30, progress.Last());

                // 30 - 100 %, apply releases
                for (var step = 0; step <= 100; step++) {
                    progress.Add(UpdateManager.CalculateProgress(step, 30, 100));

                    Assert.InRange(progress.Last(), 30, 100);
                }

                Assert.Equal(100, progress.Last());
            }
        }
    }
}
