using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using Squirrel.Json;

namespace Squirrel.Github
{
    internal class GithubClient
    {
        public readonly Uri RepoUrl;
        public readonly Uri ApiUrl;
        readonly string AccessToken;
        readonly IFileDownloader HttpClient;

        string AuthorizationHeader => string.IsNullOrEmpty(AccessToken) ? null : "Bearer " + AccessToken;

        public GithubClient(string repoUrl, IFileDownloader downloader, string accessToken = null) : 
            this(new Uri(repoUrl), downloader, accessToken)
        {

        }

        public GithubClient(Uri repoUrl, IFileDownloader downloader, string accessToken = null)
        {
            RepoUrl = repoUrl;
            ApiUrl = GetApiUrl();
            AccessToken = accessToken;
            HttpClient = downloader;

            if (RepoUrl.Segments.Length != 3) {
                throw new Exception("Repo URL must be to the root URL of the repo e.g. https://github.com/myuser/myrepo");
            }
        }

        Uri GetApiUrl()
        {
            Uri baseAddress;

            if (RepoUrl.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase)) {
                baseAddress = new Uri("https://api.github.com/");
            } else {
                // if it's not github.com, it's probably an Enterprise server
                // now the problem with Enterprise is that the API doesn't come prefixed
                // it comes suffixed so the API path of http://internal.github.server.local
                // API location is http://interal.github.server.local/api/v3
                baseAddress = new Uri(string.Format("{0}{1}{2}/api/v3/", RepoUrl.Scheme, Uri.SchemeDelimiter, RepoUrl.Host));
            }

            // above ^^ notice the end slashes for the baseAddress, explained here: http://stackoverflow.com/a/23438417/162694

            return baseAddress;
        }

        public async Task<IEnumerable<GithubRelease>> GetReleases(bool prerelease)
        {
            var releasesApiBuilder = new StringBuilder("repos")
                .Append(RepoUrl.AbsolutePath)
                .Append("/releases");

            var fullPath = new Uri(ApiUrl, releasesApiBuilder.ToString());
            var response = await HttpClient.DownloadString(fullPath.ToString(), AuthorizationHeader).ConfigureAwait(false);

            var releases = SimpleJson.DeserializeObject<List<GithubRelease>>(response);
            return releases.OrderByDescending(d => d.PublishedAt).Where(x => prerelease || !x.Prerelease);
        }

        public async Task<GithubRelease> GetLatestRelease(bool prerelease)
        {
            IEnumerable<GithubRelease> releases = await GetReleases(prerelease).ConfigureAwait(false);
            if (releases.Count() == 0) {
                throw new Exception("No releases found on github");
            }

            return releases.First();
        }

        public async Task<GithubReleaseAsset> GetLatestAsset(string assetName, bool prerelease)
        {
            GithubRelease latest = await GetLatestRelease(prerelease).ConfigureAwait(false);
            return GetAsset(latest, assetName);
        }

        public GithubReleaseAsset GetAsset(GithubRelease release, string assetName)
        {
            if (release.Assets == null || release.Assets.Count() == 0) {
                throw new ArgumentException("No assets in github release");
            }

            IEnumerable<GithubReleaseAsset> allReleasesFiles = release.Assets.Where(a => a.Name == assetName);
            if (allReleasesFiles == null || allReleasesFiles.Count() == 0) {
                throw new ArgumentException($"Could not find asset called {assetName} in github release");
            }

            return allReleasesFiles.First();
        }

        bool IsAcceptedMimiType(string mimeType)
        {
            string[] types = { "application/octet-stream", "application/json" };
            return types.Contains(mimeType);
        }

        void EnsureMimeType(GithubReleaseAsset asset)
        {
            if (false == IsAcceptedMimiType(asset.ContentType)) {
                throw new Exception($"Github returned a mime type ({asset.ContentType}) that we do not accept for asset {asset.Name}");
            }
        }

        public Task DownloadAsset(GithubRelease release, string assetName, string targetFile, Action<int> progress = null)
        {
            GithubReleaseAsset asset = GetAsset(release, assetName);
            EnsureMimeType(asset);
            return HttpClient.DownloadFile(asset.Url, targetFile, progress, AuthorizationHeader, asset.ContentType);
        }

        public Task<string> DownloadAsset(GithubRelease release, string assetName)
        {
            GithubReleaseAsset asset = GetAsset(release, assetName);
            EnsureMimeType(asset);
            return HttpClient.DownloadString(asset.Url, AuthorizationHeader, asset.ContentType);
        }
    }
}
