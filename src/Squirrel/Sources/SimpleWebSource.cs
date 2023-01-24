﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Squirrel.SimpleSplat;

namespace Squirrel.Sources
{
    /// <summary>
    /// Retrieves updates from a static file host or other web server. 
    /// Will perform a request for '{baseUri}/RELEASES' to locate the available packages,
    /// and provides query parameters to specify the name of the requested package.
    /// </summary>
    public class SimpleWebSource : IUpdateSource
    {
        /// <summary> The URL of the server hosting packages to update to. </summary>
        public virtual Uri BaseUri { get; }

        /// <summary> The <see cref="IFileDownloader"/> to be used for performing http requests. </summary>
        public virtual IFileDownloader Downloader { get; }

        /// <inheritdoc cref="SimpleWebSource" />
        public SimpleWebSource(string baseUrl, IFileDownloader downloader = null)
            : this(new Uri(baseUrl), downloader)
        { }

        /// <inheritdoc cref="SimpleWebSource" />
        public SimpleWebSource(Uri baseUri, IFileDownloader downloader = null)
        {
            BaseUri = baseUri;
            Downloader = downloader ?? Utility.CreateDefaultDownloader();
        }

        /// <inheritdoc />
        public virtual async Task<ReleaseEntry[]> GetReleaseFeed(Guid? stagingId = null, ReleaseEntry latestLocalRelease = null)
        {
            var uri = Utility.AppendPathToUri(BaseUri, "RELEASES");

            var args = new Dictionary<string, string>();

            if (SquirrelRuntimeInfo.SystemArch != RuntimeCpu.Unknown) {
                args.Add("arch", SquirrelRuntimeInfo.SystemArch.ToString());
            }

            if (SquirrelRuntimeInfo.SystemOs != RuntimeOs.Unknown) {
                args.Add("os", SquirrelRuntimeInfo.SystemOs.GetOsShortName());
                args.Add("rid", SquirrelRuntimeInfo.SystemRid);
            }

            if (latestLocalRelease != null) {
                args.Add("id", latestLocalRelease.PackageName);
                args.Add("localVersion", latestLocalRelease.Version.ToString());
            }

            var uriAndQuery = Utility.AddQueryParamsToUri(uri, args);

            this.Log().Info($"Downloading RELEASES from '{uriAndQuery}'.");

            var bytes = await Downloader.DownloadBytes(uriAndQuery.ToString()).ConfigureAwait(false);
            var txt = Utility.RemoveByteOrderMarkerIfPresent(bytes);
            return ReleaseEntry.ParseReleaseFileAndApplyStaging(txt, stagingId).ToArray();
        }

        /// <inheritdoc />
        public virtual Task DownloadReleaseEntry(ReleaseEntry releaseEntry, string localFile, Action<int> progress)
        {
            if (releaseEntry == null) throw new ArgumentNullException(nameof(releaseEntry));
            if (localFile == null) throw new ArgumentNullException(nameof(localFile));


            var releaseUri = releaseEntry.BaseUrl == null
                ? releaseEntry.Filename
                : Utility.AppendPathToUri(new Uri(releaseEntry.BaseUrl), releaseEntry.Filename).ToString();

            if (!String.IsNullOrEmpty(releaseEntry.Query)) {
                releaseUri += releaseEntry.Query;
            }

            // releaseUri can be a relative url (eg. "MyPackage.nupkg") or it can be an 
            // absolute url (eg. "https://example.com/MyPackage.nupkg"). In the former case
            var sourceBaseUri = Utility.EnsureTrailingSlash(BaseUri);
            var source = Utility.AppendPathToUri(sourceBaseUri, releaseUri).ToString();

            this.Log().Info($"Downloading '{releaseEntry.Filename}' from '{source}'.");
            return Downloader.DownloadFile(source, localFile, progress);
        }
    }
}
