using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Squirrel.Json;
using Squirrel.SimpleSplat;

#nullable enable

namespace Squirrel.Sources
{
    /// <summary> Describes a Gitea release, including attached assets. </summary>
    [DataContract]
    public class GiteaRelease
    {
        /// <summary>
        /// Public constructor for repository creating before upload
        /// </summary>
       /* [System.Text.Json.Serialization.JsonConstructor]
        public GiteaRelease(string Tag)
        {
            this.Tag = Tag;
        }

        /// <summary>
        /// Private constructor for deserialization
        /// </summary>
        [System.Text.Json.Serialization.JsonConstructor]
        private GiteaRelease()
        {

        }
       */
        /// <summary> 
        /// The integer ID of this release. Sequential but not continuous.
        /// Deleted assets will return null if called by ID.
        /// Deleted assets ID's will not be reused.
        /// Drafts and prereleases have an associated ID. 
        /// </summary>
        [DataMember(Name = "id")]
        public Int64? Id { get; set; }

        /// <summary> The name of this release. </summary>
        [DataMember(Name = "name")]
        public string? Name { get; set; }

        /// <summary> The tag associates with this release. </summary>
        [DataMember(Name = "tag_name")]
        public string? Tag { get; set; }

        /// <summary> The body of the release. </summary>
        [DataMember(Name = "body")]
        public string? Body { get; set; }

        /// <summary> True if this release is a draft. </summary>
        [DataMember(Name = "draft")]
        public bool? Draft { get; set; }

        /// <summary> True if this release is a prerelease. </summary>
        [DataMember(Name = "prerelease")]
        public bool Prerelease { get; set; }

        /// <summary> The date which this release was published publically. </summary>
        [DataMember(Name = "published_at")]
        public DateTime? PublishedAt { get; set; }

        /// <summary> A list of assets (files) uploaded to this release. </summary>
        [DataMember(Name = "assets")]
        public GiteaReleaseAsset[]? Assets { get; set; }
    }

    /// <summary> Describes a asset (file) uploaded to a Gitea release. </summary>
    [DataContract]
    public class GiteaReleaseAsset
    {
        /// <summary> The id of this release asset. </summary>
        [DataMember(Name = "id")]
        public Int64? Id { get; set; }

        /// <summary> The size of this release asset in bytes. </summary>
        [DataMember(Name = "uuid")]
        public string? Uuid { get; set; }

        /// <summary> The (file) name of this release asset. </summary>
        [DataMember(Name = "name")]
        public string? Name { get; set; }

        // Requests to this URL will use API
        // quota and return JSON unless the 'Accept' header is "application/octet-stream". VVVVV

        /// <summary> 
        /// The asset URL for this release asset.
        /// </summary>
        [DataMember(Name = "browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        /// <summary> The date which this asset was created. </summary>
        [DataMember(Name = "created_at")]
        public DateTime? CreatedAt { get; set; }

        /// <summary> The size of this release asset in bytes. </summary>
        [DataMember(Name = "size")]
        public Int64? Size { get; set; }
    }

    /// <summary>
    /// Retrieves available releases from a Gitea repository. This class only
    /// downloads assets from the very latest Gitea release.
    /// </summary>
    public class GiteaSource : IUpdateSource
    {
        internal readonly static IFullLogger Log = SquirrelLocator.Current.GetService<ILogManager>().GetLogger(typeof(GiteaSource));

        /// <summary> 
        /// The URL of the Gitea repository
        /// (e.g. https://MyGiteaHost.com/repoOwner/repoName)
        /// </summary>
        public virtual Uri RepoUri { get; }

        /// <summary> 
        /// An organization (recommended) or a user.
        /// </summary>
        public virtual string? RepoOwner { get; set; }

        /// <summary> 
        /// Name of the repository
        /// </summary>
        public virtual string? RepoName { get; set; }

        /// <summary>  
        /// If true, the latest pre-release will be downloaded. If false, the latest 
        /// stable release will be downloaded.
        /// </summary>
        public virtual bool Prerelease { get; }

        /// <summary> 
        /// The file downloader used to perform HTTP requests. Default to HttpFileDownloader
        /// </summary>
        public virtual IFileDownloader Downloader { get; }

        /// <summary>  
        /// The Gitea release which this class should download assets from when 
        /// executing <see cref="DownloadReleaseEntry"/>. This property can be set
        /// explicitly, otherwise it will also be set automatically when executing
        /// <see cref="GetReleaseFeed(Guid?, ReleaseEntry)"/>.
        /// </summary>
        public virtual GiteaRelease? Release { get; set; }

        /// <summary>
        /// The Gitea access token to use with the request to download releases. 
        /// Must include the word "token " with a space as a prefix. (eg "token 9e876ac72703f34ed60de66574420f6a3033f440")
        /// Can also use "Bearer 9e876ac72703f34ed60de66574420f6a3033f440"
        /// </summary>
        protected virtual string? AccessToken { get; }

        //TODO add support for more Auth types https://docs.gitea.com/next/development/api-usage

        /// <summary> The Bearer token used in the request. </summary>
        protected virtual string? Authorization => string.IsNullOrWhiteSpace(AccessToken) ? null : AccessToken;

        /// <inheritdoc cref="GiteaSource" />
        /// <param name="repoUrl">
        /// The URL of the Gitea repository to download releases from 
        /// (e.g. https://Gitea.com/myuser/myrepo)
        /// </param>
        /// <param name="accessToken">
        /// The Gitea access token to use with the request to download releases. 
        /// If left empty, the Gitea api cannot be accessed
        /// </param>
        /// <param name="prerelease">
        /// If true, the latest pre-release will be downloaded. If false, the latest 
        /// stable release will be downloaded.
        /// </param>
        /// <param name="downloader">
        /// The file downloader used to perform HTTP requests. 
        /// </param>
        public GiteaSource(string repoUrl, string accessToken, bool prerelease, IFileDownloader downloader = null)
        {
            RepoUri = new Uri(repoUrl);
            AccessToken = accessToken;
            Prerelease = prerelease;
            Downloader = downloader ?? Utility.CreateDefaultDownloader();

            GenerateSourceInfo();
        }

        /// <inheritdoc />
        public virtual async Task<ReleaseEntry[]> GetReleaseFeed(Guid? stagingId = null, ReleaseEntry latestLocalRelease = null)
        {
            var releases = await GetReleases(Prerelease).ConfigureAwait(false); //ok

            if (releases == null || releases.Count() == 0) //sub optimal but fine i guess. error checking update
                throw new Exception($"No Gitea releases found at '{RepoUri}'.");

            // CS: we 'cache' the release here, so subsequent calls to DownloadReleaseEntry
            // will download assets from the same release in which we returned ReleaseEntry's
            // from. A better architecture would be to return an array of "GiteaReleaseEntry"
            // containing a reference to the GiteaReleaseAsset instead.
            Release = releases.First();

            // https://docs.Gitea.com/en/rest/reference/releases#get-a-release-asset
            var assetUrl = GetAssetUrlFromName(Release, "RELEASES");
            var releaseBytes = await Downloader.DownloadBytes(assetUrl, Authorization, "application/octet-stream").ConfigureAwait(false); //test this method
            var txt = Utility.RemoveByteOrderMarkerIfPresent(releaseBytes);
            return ReleaseEntry.ParseReleaseFileAndApplyStaging(txt, stagingId).ToArray();
        }

        /// <inheritdoc />
        public virtual Task DownloadReleaseEntry(ReleaseEntry releaseEntry, string localFile, Action<int> progress)
        {
            if (Release == null) {
                throw new InvalidOperationException("No Gitea Release specified. Call GetReleaseFeed or set " +
                    "GiteaSource.Release before calling this function.");
            }

            // this might be a browser url or an api url (depending on whether we have a AccessToken or not)
            // https://docs.Gitea.com/en/rest/reference/releases#get-a-release-asset
            var assetUrl = GetAssetUrlFromName(Release, releaseEntry.Filename);
            return Downloader.DownloadFile(assetUrl, localFile, progress, Authorization, "application/octet-stream"); //TODO test this line
        }

        /// <summary>
        /// Retrieves a list of <see cref="GiteaRelease"/> from the current repository.
        /// </summary>
        public virtual async Task<GiteaRelease[]> GetReleases(bool includePrereleases)
        {
            GenerateSourceInfo();

            string releasesUrl = $"{RepoUri.GetLeftPart(System.UriPartial.Authority)}/api/v1/repos/{RepoOwner}/{RepoName}/releases";

            this.Log().Info("RELEASES URL: " + releasesUrl);

            //TODO validate token length?
            //TODO validate more inputs / better implement nullable
            //TODO downloader is going to have the 401 authentication issue sometimes.

            var response = await Downloader.DownloadString(releasesUrl, Authorization, "application/json").ConfigureAwait(false);

            var releases = SimpleJson.DeserializeObject<List<GiteaRelease>>(response);

            return releases.OrderByDescending(d => d.PublishedAt).Where(x => includePrereleases || !x.Prerelease).ToArray();
        }

        /// <summary>
        /// TODO refactor into 2 helper methods that are called by constructor.
        /// </summary>
        private void GenerateSourceInfo()
        {
            /* http://localhost:3000/repoOwner/repoName/ */

            var repoParts = RepoUri.AbsolutePath.Trim('/').Split('/');

            if (repoParts.Length != 2)
                throw new Exception($"Invalid Gitea URL, '{RepoUri.AbsolutePath}' should be in the format 'owner/repo'");

            RepoOwner = repoParts[0];
            RepoName = repoParts[1];
        }

        /// <summary>
        /// Given a <see cref="GiteaRelease"/> and an asset filename (eg. 'RELEASES' or 'whatever.nupkg') this 
        /// function will return the <see cref="GiteaReleaseAsset.BrowserDownloadUrl"/>
        /// Logs error if no AccessToken
        /// </summary>
        /// <param name="release">Parent release of asset list</param>
        /// <param name="assetName">Name of asset to retrieve url for</param>
        /// <returns></returns>
        /// <exception cref="ArgumentException">Throws if no assets or no matching asset name</exception>
        protected virtual string GetAssetUrlFromName(GiteaRelease release, string assetName)
        {
            if (release.Assets == null || release.Assets.Count() == 0) {
                throw new ArgumentException($"No assets found in Gitea Release '{release.Name}'.");
            }

            IEnumerable<GiteaReleaseAsset> allReleasesFiles = release.Assets.Where(a => a.Name.Equals(assetName, StringComparison.InvariantCultureIgnoreCase));

            if (allReleasesFiles == null || allReleasesFiles.Count() == 0) {
                throw new ArgumentException($"Could not find asset called '{assetName}' in Gitea Release '{release.Name}'.");
            }
            
            if(allReleasesFiles.Count() > 1) 
            {
                Log.Error("Multiple release assets with same name");
            }

            var asset = allReleasesFiles.First();

            if (String.IsNullOrWhiteSpace(AccessToken))
                Log.Error("No Gitea access token provided. Will not be able to download files");

            return asset.BrowserDownloadUrl;
        }
    }
}

