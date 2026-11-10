using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MetaDataIAPlugin
{
    /// <summary>
    /// Uses the separately installed "LaunchBox Metadata Provider" add-on (Jeshibu) as a
    /// metadata and media source. That add-on owns the local LaunchBox Games Database copy,
    /// its platform-aware game matching and its image lookup; this service only asks it for
    /// fields in background mode (no dialogs) and hands the results to Metadata AI.
    /// </summary>
    internal sealed class LaunchBoxPluginService
    {
        public static readonly Guid LaunchBoxPluginId = Guid.Parse("3b1908f2-de02-48c9-9633-10d978903652");

        private static readonly ILogger Logger = LogManager.GetLogger();
        private static readonly SemaphoreSlim QueryLock = new SemaphoreSlim(1, 1);
        private static readonly Dictionary<string, LookupEntry> Cache = new Dictionary<string, LookupEntry>();
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
        private const int MaxCachedGames = 12;

        private readonly IPlayniteAPI playniteApi;
        private readonly MetaDataIASettings settings;

        public LaunchBoxPluginService(IPlayniteAPI playniteApi, MetaDataIASettings settings)
        {
            this.playniteApi = playniteApi ?? TryGetGlobalApi();
            this.settings = settings;
        }

        /// <summary>
        /// True when the LaunchBox Metadata Provider add-on is installed and loaded.
        /// </summary>
        public static bool IsInstalled(IPlayniteAPI api = null)
        {
            return FindPlugin(api ?? TryGetGlobalApi()) != null;
        }

        public async Task<OfficialStoreMetadata> GetContextAsync(Game game, CancellationToken cancelToken)
        {
            if (!CanQuery(game))
            {
                return null;
            }

            GameMetadata text = null;
            await QueryLock.WaitAsync(cancelToken).ConfigureAwait(false);
            try
            {
                var entry = await GetEntryAsync(game, cancelToken).ConfigureAwait(false);
                if (entry == null || !entry.Matched)
                {
                    return null;
                }

                if (entry.Text == null)
                {
                    var provider = entry.Provider;
                    entry.Text = await Task.Run(() => ReadTextFields(provider), cancelToken).ConfigureAwait(false);
                }

                text = entry.Text;
            }
            finally
            {
                QueryLock.Release();
            }

            var context = new PlayniteIntegrationService(playniteApi, settings).ToContext(text, game, MetaDataIASettings.SourceLaunchBox);
            if (context == null)
            {
                return null;
            }

            var pluginIsEnglish = ((settings == null ? null : settings.Language) ?? "en").Trim().StartsWith("en", StringComparison.OrdinalIgnoreCase);
            context.ListsMatchPluginLanguage = pluginIsEnglish;
            context.IsExactMatch = true;
            return context;
        }

        public async Task<OriginIntegrationMedia> GetMediaAsync(Game game, MediaKind kind, CancellationToken cancelToken)
        {
            if (!CanQuery(game) || kind == MediaKind.Logo)
            {
                return null;
            }

            MetadataFile file = null;
            await QueryLock.WaitAsync(cancelToken).ConfigureAwait(false);
            try
            {
                var entry = await GetEntryAsync(game, cancelToken).ConfigureAwait(false);
                if (entry == null || !entry.Matched)
                {
                    return null;
                }

                if (!entry.Media.TryGetValue(kind, out file))
                {
                    var provider = entry.Provider;
                    file = await Task.Run(() => ReadImage(provider, kind), cancelToken).ConfigureAwait(false);
                    entry.Media[kind] = file;
                }
            }
            finally
            {
                QueryLock.Release();
            }

            if (file == null || !file.HasContent)
            {
                return null;
            }

            var result = new OriginIntegrationResult
            {
                IntegrationId = LaunchBoxPluginId,
                IntegrationName = MetaDataIASettings.SourceLaunchBox,
                Metadata = new GameMetadata
                {
                    CoverImage = kind == MediaKind.Cover ? file : null,
                    Icon = kind == MediaKind.Icon ? file : null,
                    BackgroundImage = kind == MediaKind.Background ? file : null
                }
            };

            return await new PlayniteIntegrationService(playniteApi, settings).GetMediaAsync(result, kind, cancelToken).ConfigureAwait(false);
        }

        private bool CanQuery(Game game)
        {
            return game != null && !string.IsNullOrWhiteSpace(game.Name) && playniteApi != null;
        }

        // Must be called while holding QueryLock.
        private async Task<LookupEntry> GetEntryAsync(Game game, CancellationToken cancelToken)
        {
            var key = BuildKey(game);
            LookupEntry entry;
            if (Cache.TryGetValue(key, out entry))
            {
                if (DateTime.UtcNow - entry.Created < CacheLifetime)
                {
                    return entry;
                }

                Cache.Remove(key);
                entry.Dispose();
            }

            if (Cache.Count >= MaxCachedGames)
            {
                foreach (var stale in Cache.Values)
                {
                    stale.Dispose();
                }

                Cache.Clear();
            }

            var plugin = FindPlugin(playniteApi);
            if (plugin == null)
            {
                // Not cached: the add-on may be installed later in the session.
                return null;
            }

            cancelToken.ThrowIfCancellationRequested();
            entry = await Task.Run(() => CreateEntry(plugin, game), cancelToken).ConfigureAwait(false);
            Cache[key] = entry;
            return entry;
        }

        private static LookupEntry CreateEntry(MetadataPlugin plugin, Game game)
        {
            var entry = new LookupEntry();
            try
            {
                // Background mode: the add-on matches by exact (normalised) title plus platform
                // overlap and never opens its search or image picker dialogs.
                var provider = plugin.GetMetadataProvider(new MetadataRequestOptions(game, true));
                if (provider == null)
                {
                    // The add-on returns no provider when its local database has not been
                    // downloaded yet or needs a format update. It shows its own notification.
                    return entry;
                }

                var name = Safe(() => provider.GetName(new GetMetadataFieldArgs()), "name", game);
                if (string.IsNullOrWhiteSpace(name))
                {
                    provider.Dispose();
                    return entry;
                }

                entry.Provider = provider;
                entry.Matched = true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "LaunchBox Metadata Provider failed while matching " + game.Name + ".");
            }

            return entry;
        }

        private static GameMetadata ReadTextFields(OnDemandMetadataProvider provider)
        {
            var args = new GetMetadataFieldArgs();
            return new GameMetadata
            {
                Name = Safe(() => provider.GetName(args), "name", null),
                Description = Safe(() => provider.GetDescription(args), "description", null),
                Genres = ToSet(Safe(() => provider.GetGenres(args), "genres", null)),
                Developers = ToSet(Safe(() => provider.GetDevelopers(args), "developers", null)),
                Publishers = ToSet(Safe(() => provider.GetPublishers(args), "publishers", null)),
                AgeRatings = ToSet(Safe(() => provider.GetAgeRatings(args), "age ratings", null)),
                ReleaseDate = Safe(() => provider.GetReleaseDate(args), "release date", null),
                Links = (Safe(() => provider.GetLinks(args), "links", null) ?? Enumerable.Empty<Link>())
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Url))
                    .ToList()
            };
        }

        private static MetadataFile ReadImage(OnDemandMetadataProvider provider, MediaKind kind)
        {
            var args = new GetMetadataFieldArgs();
            switch (kind)
            {
                case MediaKind.Cover:
                    return Safe(() => provider.GetCoverImage(args), "cover", null);
                case MediaKind.Icon:
                    return Safe(() => provider.GetIcon(args), "icon", null);
                case MediaKind.Background:
                    return Safe(() => provider.GetBackgroundImage(args), "background", null);
                default:
                    return null;
            }
        }

        private static T Safe<T>(Func<T> read, string field, Game game)
        {
            try
            {
                return read();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "LaunchBox Metadata Provider failed to return " + field +
                                (game == null ? "." : " for " + game.Name + "."));
                return default(T);
            }
        }

        private static HashSet<MetadataProperty> ToSet(IEnumerable<MetadataProperty> values)
        {
            var list = (values ?? Enumerable.Empty<MetadataProperty>()).Where(x => x != null).ToList();
            return list.Count == 0 ? null : new HashSet<MetadataProperty>(list);
        }

        private static string BuildKey(Game game)
        {
            var platformIds = game.PlatformIds == null
                ? string.Empty
                : string.Join(",", game.PlatformIds.OrderBy(x => x).Select(x => x.ToString("N")));
            return game.Id.ToString("N") + "|" + (game.Name ?? string.Empty).Trim() + "|" + platformIds;
        }

        private static MetadataPlugin FindPlugin(IPlayniteAPI api)
        {
            try
            {
                return api == null || api.Addons == null || api.Addons.Plugins == null
                    ? null
                    : api.Addons.Plugins.OfType<MetadataPlugin>().FirstOrDefault(x => x.Id == LaunchBoxPluginId);
            }
            catch
            {
                return null;
            }
        }

        private static IPlayniteAPI TryGetGlobalApi()
        {
            try
            {
                return API.Instance;
            }
            catch
            {
                return null;
            }
        }

        private sealed class LookupEntry : IDisposable
        {
            public LookupEntry()
            {
                Created = DateTime.UtcNow;
                Media = new Dictionary<MediaKind, MetadataFile>();
            }

            public DateTime Created { get; private set; }
            public bool Matched { get; set; }
            public OnDemandMetadataProvider Provider { get; set; }
            public GameMetadata Text { get; set; }
            public Dictionary<MediaKind, MetadataFile> Media { get; private set; }

            public void Dispose()
            {
                try
                {
                    if (Provider != null)
                    {
                        Provider.Dispose();
                    }
                }
                catch
                {
                }

                Provider = null;
            }
        }
    }
}
