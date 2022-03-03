using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Squirrel.SimpleSplat;

namespace Squirrel
{
    public partial class UpdateManager
    {
        /// <inheritdoc/>
        public virtual async Task DownloadReleases(IEnumerable<ReleaseEntry> releasesToDownload, Action<int> progress = null)
        {
            await acquireUpdateLock().ConfigureAwait(false);

            double toIncrement = 100.0 / releasesToDownload.Count();
            ProgressContext progressContext = new(progress ?? (_ => { }), toIncrement);

            await DownloadReleases(_updateUrlOrPath, releasesToDownload, progressContext, _urlDownloader).ConfigureAwait(false);
        }

        /// <summary>
        /// Download all specified releases
        /// </summary>
        /// <param name="updateUrlOrPath">Source for updates</param>
        /// <param name="releasesToDownload">All of the releases to be downloaded</param>
        /// <param name="progress">Progress state</param>
        /// <param name="urlDownloader">Handler for pulling files</param>
        /// <returns></returns>
        protected virtual Task DownloadReleases(string updateUrlOrPath, IEnumerable<ReleaseEntry> releasesToDownload, ProgressContext progress, IFileDownloader urlDownloader)
        {
            return releasesToDownload.ForEachAsync(async release => {
                await DownloadRelease(updateUrlOrPath, release, progress, urlDownloader).ConfigureAwait(false);
            });
        }

        /// <summary>
        /// Download a specific release
        /// </summary>
        /// <param name="updateUrlOrPath">Source for updates</param>
        /// <param name="release">The release to download</param>
        /// <param name="progress">Progress state</param>
        /// <param name="urlDownloader">Handler for pulling files</param>
        /// <returns></returns>
        protected virtual async Task DownloadRelease(string updateUrlOrPath, ReleaseEntry release, ProgressContext progress, IFileDownloader urlDownloader)
        {
            if (Utility.IsHttpUrl(updateUrlOrPath)) {
                await DownloadReleaseRemotely(updateUrlOrPath, release, progress, urlDownloader).ConfigureAwait(false);
            } else {
                DownloadReleaseLocally(updateUrlOrPath, release, progress);
            }
        }

        async Task DownloadReleaseRemotely(string updateUrlOrPath, ReleaseEntry releaseEntry, ProgressContext progress, IFileDownloader urlDownloader)
        {
            var targetFile = Path.Combine(PackagesDirectory, releaseEntry.Filename);
            double component = 0;

            var baseUri = Utility.EnsureTrailingSlash(new Uri(updateUrlOrPath));

            var releaseEntryUrl = releaseEntry.BaseUrl + releaseEntry.Filename;
            if (!String.IsNullOrEmpty(releaseEntry.Query)) {
                releaseEntryUrl += releaseEntry.Query;
            }
            var sourceFileUrl = new Uri(baseUri, releaseEntryUrl).AbsoluteUri;
            File.Delete(targetFile);

            await urlDownloader.DownloadFile(sourceFileUrl, targetFile, p => {
                lock (progress) {
                    progress.Current -= component;
                    component = progress.IncreamentSize / 100.0 * p;
                    progress.Increament(component);
                }
            }).ConfigureAwait(false);

            checksumPackage(releaseEntry);
        }

        void DownloadReleaseLocally(string updateUrlOrPath, ReleaseEntry releaseToDownload, ProgressContext progress)
        {
            var targetFile = Path.Combine(PackagesDirectory, releaseToDownload.Filename);

            File.Copy(
                Path.Combine(updateUrlOrPath, releaseToDownload.Filename),
                targetFile,
                true);

            progress.Increament();
            checksumPackage(releaseToDownload);
        }

        void checksumPackage(ReleaseEntry downloadedRelease)
        {
            var targetPackage = new FileInfo(
                Path.Combine(AppDirectory, "packages", downloadedRelease.Filename));

            if (!targetPackage.Exists) {
                this.Log().Error("File {0} should exist but doesn't", targetPackage.FullName);

                throw new Exception("Checksummed file doesn't exist: " + targetPackage.FullName);
            }

            if (targetPackage.Length != downloadedRelease.Filesize) {
                this.Log().Error("File Length should be {0}, is {1}", downloadedRelease.Filesize, targetPackage.Length);
                targetPackage.Delete();

                throw new Exception("Checksummed file size doesn't match: " + targetPackage.FullName);
            }

            using (var file = targetPackage.OpenRead()) {
                var hash = Utility.CalculateStreamSHA1(file);

                if (!hash.Equals(downloadedRelease.SHA1, StringComparison.OrdinalIgnoreCase)) {
                    this.Log().Error("File SHA1 should be {0}, is {1}", downloadedRelease.SHA1, hash);
                    targetPackage.Delete();
                    throw new Exception("Checksum doesn't match: " + targetPackage.FullName);
                }
            }
        }
    }
}
