﻿using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MoreLinq;

namespace MediaBrowser.Providers.Music
{
    public class LastfmAlbumProvider : LastfmBaseProvider
    {
        private static readonly Task<string> BlankId = Task.FromResult("");

        private readonly IProviderManager _providerManager;

        /// <summary>
        /// The name of the local json meta file for this item type
        /// </summary>
        protected string LocalMetaFileName { get; set; }

        public LastfmAlbumProvider(IJsonSerializer jsonSerializer, IHttpClient httpClient, ILogManager logManager, IServerConfigurationManager configurationManager, IProviderManager providerManager)
            : base(jsonSerializer, httpClient, logManager, configurationManager)
        {
            _providerManager = providerManager;
            LocalMetaFileName = LastfmHelper.LocalAlbumMetaFileName;
        }

        protected override Task<string> FindId(BaseItem item, CancellationToken cancellationToken)
        {
            // We don't fetch by id
            return BlankId;
        }

        /// <summary>
        /// Needses the refresh internal.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="providerInfo">The provider info.</param>
        /// <returns><c>true</c> if XXXX, <c>false</c> otherwise</returns>
        protected override bool NeedsRefreshInternal(BaseItem item, BaseProviderInfo providerInfo)
        {
            // If song metadata has changed and we don't have an mbid, refresh
            if (string.IsNullOrEmpty(item.GetProviderId(MetadataProviders.Musicbrainz)) &&
                GetComparisonData(item as MusicAlbum) != providerInfo.Data)
            {
                return true;
            }

            return base.NeedsRefreshInternal(item, providerInfo);
        }

        protected override async Task FetchLastfmData(BaseItem item, string id, CancellationToken cancellationToken)
        {
            var result = await GetAlbumResult(item, cancellationToken).ConfigureAwait(false);

            if (result != null && result.album != null)
            {
                LastfmHelper.ProcessAlbumData(item, result.album);
                //And save locally if indicated
                if (ConfigurationManager.Configuration.SaveLocalMeta)
                {
                    var ms = new MemoryStream();
                    JsonSerializer.SerializeToStream(result.album, ms);

                    cancellationToken.ThrowIfCancellationRequested();

                    await _providerManager.SaveToLibraryFilesystem(item, Path.Combine(item.MetaLocation, LocalMetaFileName), ms, cancellationToken).ConfigureAwait(false);
                    
                }
            }

            BaseProviderInfo data;
            if (!item.ProviderData.TryGetValue(Id, out data))
            {
                data = new BaseProviderInfo();
                item.ProviderData[Id] = data;
            }

            data.Data = GetComparisonData(item as MusicAlbum);
        }

        private async Task<LastfmGetAlbumResult> GetAlbumResult(BaseItem item, CancellationToken cancellationToken)
        {
            var folder = (Folder)item;

            // Get each song, distinct by the combination of AlbumArtist and Album
            var songs = folder.RecursiveChildren.OfType<Audio>().DistinctBy(i => (i.AlbumArtist ?? string.Empty) + (i.Album ?? string.Empty), StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var song in songs.Where(song => !string.IsNullOrEmpty(song.Album) && !string.IsNullOrEmpty(song.AlbumArtist)))
            {
                var result = await GetAlbumResult(song.AlbumArtist, song.Album, cancellationToken).ConfigureAwait(false);

                if (result != null && result.album != null)
                {
                    return result;
                }
            }

            // Try the folder name
            return await GetAlbumResult(item.Parent.Name, item.Name, cancellationToken);
        }

        private async Task<LastfmGetAlbumResult> GetAlbumResult(string artist, string album, CancellationToken cancellationToken)
        {
            // Get albu info using artist and album name
            var url = RootUrl + string.Format("method=album.getInfo&artist={0}&album={1}&api_key={2}&format=json", UrlEncode(artist), UrlEncode(album), ApiKey);

            using (var json = await HttpClient.Get(new HttpRequestOptions
            {
                Url = url,
                ResourcePool = LastfmResourcePool,
                CancellationToken = cancellationToken,
                EnableHttpCompression = false

            }).ConfigureAwait(false))
            {
                return JsonSerializer.DeserializeFromStream<LastfmGetAlbumResult>(json);
            }
        }
        
        protected override Task FetchData(BaseItem item, CancellationToken cancellationToken)
        {
            return FetchLastfmData(item, string.Empty, cancellationToken);
        }

        public override bool Supports(BaseItem item)
        {
            return item is MusicAlbum;
        }

        /// <summary>
        /// Gets the data.
        /// </summary>
        /// <param name="album">The album.</param>
        /// <returns>Guid.</returns>
        private Guid GetComparisonData(MusicAlbum album)
        {
            var songs = album.RecursiveChildren.OfType<Audio>().ToList();

            var albumArtists = songs.Select(i => i.AlbumArtist)
                .Where(i => !string.IsNullOrEmpty(i))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var albumNames = songs.Select(i => i.AlbumArtist)
                .Where(i => !string.IsNullOrEmpty(i))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            albumArtists.AddRange(albumNames);

            return string.Join(string.Empty, albumArtists.OrderBy(i => i).ToArray()).GetMD5();
        }
    }
}
