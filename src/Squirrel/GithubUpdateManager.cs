using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using Squirrel.Github;
using Squirrel.Json;

namespace Squirrel
{
    /// <summary>
    /// An implementation of UpdateManager which supports checking updates and 
    /// downloading releases directly from GitHub releases
    /// </summary>
#if NET5_0_OR_GREATER
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
    public class GithubUpdateManager : UpdateManager
    {
        private readonly string _repoUrl;
        private readonly string _accessToken;
        private readonly bool _prerelease;

        /// <inheritdoc cref="UpdateManager(string, string, string, IFileDownloader)"/>
        /// <param name="repoUrl">
        /// The URL of the GitHub repository to download releases from 
        /// (e.g. https://github.com/myuser/myrepo)
        /// </param>
        /// <param name="applicationIdOverride">
        /// The Id of your application should correspond with the 
        /// appdata directory name, and the Id used with Squirrel releasify/pack.
        /// If left null/empty, will attempt to determine the current application Id  
        /// from the installed app location.
        /// </param>
        /// <param name="urlDownloader">
        /// A custom file downloader, for using non-standard package sources or adding 
        /// proxy configurations. 
        /// </param>
        /// <param name="localAppDataDirectoryOverride">
        /// Provide a custom location for the system LocalAppData, it will be used 
        /// instead of <see cref="Environment.SpecialFolder.LocalApplicationData"/>.
        /// </param>
        /// <param name="prerelease">
        /// If true, the latest pre-release will be downloaded. If false, the latest 
        /// stable release will be downloaded.
        /// </param>
        /// <param name="accessToken">
        /// The GitHub access token to use with the request to download releases. 
        /// If left empty, the GitHub rate limit for unauthenticated requests allows 
        /// for up to 60 requests per hour, limited by IP address.
        /// </param>
        public GithubUpdateManager(
            string repoUrl,
            bool prerelease = false,
            string accessToken = null,
            string applicationIdOverride = null,
            string localAppDataDirectoryOverride = null,
            IFileDownloader urlDownloader = null)
            : base(null, applicationIdOverride, localAppDataDirectoryOverride, urlDownloader)
        {
            _updateUrlOrPath = _repoUrl = repoUrl;
            _accessToken = accessToken;
            _prerelease = prerelease;
        }



        /// <inheritdoc />
        protected override async Task<string> ReadReleasesFile(string updateUrlOrPath, ReleaseEntry latestLocalRelease, IFileDownloader urlDownloader)
        {
            GithubClient client = new GithubClient(updateUrlOrPath, urlDownloader, _accessToken);

            GithubRelease latest = await client.GetLatestRelease(_prerelease).ConfigureAwait(false);
            if (latest.Assets == null || latest.Assets.Count() == 0) {
                throw new Exception("No assets on latest github release");
            }

            return await client.DownloadAsset(latest, ReleasesFileName).ConfigureAwait(false);
        }

        /// <inheritdoc />
        protected override async Task DownloadRelease(string updateUrlOrPath, ReleaseEntry release, ProgressContext progress, IFileDownloader urlDownloader)
        {
            GithubClient client = new GithubClient(updateUrlOrPath, urlDownloader, _accessToken);

            GithubRelease latest = await client.GetLatestRelease(_prerelease).ConfigureAwait(false);
            if (latest.Assets == null || latest.Assets.Count() == 0) {
                throw new Exception("No assets on latest github release");
            }

            // TODO: Handle progress
            await client.DownloadAsset(latest, release.Filename);
        }
    }
}
