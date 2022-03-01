using System;
using System.Runtime.Serialization;

namespace Squirrel.Github
{
    [DataContract]
    internal class GithubRelease
    {
        [DataMember(Name = "prerelease")]
        public bool Prerelease { get; set; }

        [DataMember(Name = "published_at")]
        public DateTime PublishedAt { get; set; }

        [DataMember(Name = "html_url")]
        public string HtmlUrl { get; set; }

        public string DownloadUrl => HtmlUrl.Replace("/tag/", "/download/");

        [DataMember(Name = "assets")]
        public GithubReleaseAsset[] Assets;
    }

    [DataContract]
    internal class GithubReleaseAsset
    {
        [DataMember(Name = "url")]
        public string Url { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "content_type")]
        public string ContentType { get; set; }
    }
}
