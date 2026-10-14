using System.Net;
using System.Text;
using GameFerry.Core.Covers;
using GameFerry.Core.Library;
using GameFerry.Tests.Support;

namespace GameFerry.Tests;

public sealed class CoverTests : IDisposable
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly TempDirectory _temp = new();
    private readonly FakeGameTdb _server = new();

    public void Dispose() => _temp.Dispose();

    private CoverDownloader Downloader => new(new HttpClient(_server));

    private CoverDestination Destination(params CoverLayout[] layouts) => new(_temp.Path, layouts);

    private Task<CoverSummary> DownloadAsync(CoverDestination destination, bool overwrite = false, params string[] ids) =>
        Downloader.DownloadAsync(ids, destination, overwrite, progress: null, CancellationToken.None);

    private string[] FilesWritten() =>
        [.. Directory.GetFiles(_temp.Path, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_temp.Path, f)).Order(StringComparer.Ordinal)];

    private string ReadArt(string relativePath) =>
        Encoding.ASCII.GetString(File.ReadAllBytes(_temp.Combine(relativePath))[PngSignature.Length..]);

    [Theory]
    [InlineData("TSTE01", "US,EN")]
    [InlineData("TSTP01", "EN")]
    [InlineData("TSTJ01", "JA,EN")]
    [InlineData("TSTK01", "KO,EN")]
    [InlineData("TSTW01", "ZH,EN")]
    [InlineData("TSTD7K", "EN,DE")]
    [InlineData("TSTX01", "EN,US")]
    [InlineData("TSTV01", "EN,DK,FI,SE")]
    [InlineData("ABC", "EN")]
    public void Languages_follow_the_region_letter_of_the_game_id(string id, string expected)
    {
        Assert.Equal(expected, string.Join(',', GameTdbArt.Languages(id)));
    }

    [Fact]
    public void Candidates_try_the_best_kind_in_every_language_before_the_next_kind()
    {
        Assert.Equal(
            [
                "https://art.gametdb.com/wii/coverfullHQ/US/TSTE01.png",
                "https://art.gametdb.com/wii/coverfullHQ/EN/TSTE01.png",
                "https://art.gametdb.com/wii/coverfull/US/TSTE01.png",
                "https://art.gametdb.com/wii/coverfull/EN/TSTE01.png",
            ],
            GameTdbArt.Candidates(CoverType.Full, "TSTE01").Select(uri => uri.AbsoluteUri));
    }

    [Fact]
    public async Task WiiFlow_gets_full_covers_in_boxcovers_and_flat_covers_in_covers()
    {
        _server.AddAllKinds("US", "TSTE01");

        CoverSummary summary = await DownloadAsync(Destination(CoverLayout.WiiFlow), ids: "TSTE01");

        Assert.Equal([@"wiiflow\boxcovers\TSTE01.png", @"wiiflow\covers\TSTE01.png"], FilesWritten());
        Assert.Equal("coverfullHQ/US/TSTE01", ReadArt(@"wiiflow\boxcovers\TSTE01.png"));
        Assert.Equal("cover/US/TSTE01", ReadArt(@"wiiflow\covers\TSTE01.png"));
        Assert.Equal((2, 0, 0, 0), (summary.Downloaded, summary.Present, summary.NotFound, summary.Failed));
    }

    [Fact]
    public async Task Usb_loader_gx_gets_four_kinds_under_its_images_folder()
    {
        _server.AddAllKinds("US", "TSTE01");

        await DownloadAsync(Destination(CoverLayout.UsbLoaderGx), ids: "TSTE01");

        Assert.Equal(
            [
                @"apps\usbloader_gx\images\2D\TSTE01.png",
                @"apps\usbloader_gx\images\TSTE01.png",
                @"apps\usbloader_gx\images\disc\TSTE01.png",
                @"apps\usbloader_gx\images\full\TSTE01.png",
            ],
            FilesWritten());
        Assert.Equal("cover3D/US/TSTE01", ReadArt(@"apps\usbloader_gx\images\TSTE01.png"));
        Assert.Equal("cover/US/TSTE01", ReadArt(@"apps\usbloader_gx\images\2D\TSTE01.png"));
        Assert.Equal("disc/US/TSTE01", ReadArt(@"apps\usbloader_gx\images\disc\TSTE01.png"));
    }

    [Fact]
    public async Task Configurable_usb_loader_keeps_flat_covers_in_the_covers_folder_itself()
    {
        _server.AddAllKinds("US", "TSTE01");

        await DownloadAsync(Destination(CoverLayout.ConfigurableUsbLoader), ids: "TSTE01");

        Assert.Equal(
            [
                @"usb-loader\covers\3d\TSTE01.png",
                @"usb-loader\covers\TSTE01.png",
                @"usb-loader\covers\disc\TSTE01.png",
                @"usb-loader\covers\full\TSTE01.png",
            ],
            FilesWritten());
        Assert.Equal("cover/US/TSTE01", ReadArt(@"usb-loader\covers\TSTE01.png"));
    }

    [Fact]
    public async Task Falls_back_to_the_lower_quality_cover_the_custom_disc_and_the_games_own_language()
    {
        // A German release with only standard-quality full art and a fan-made disc label.
        _server.Add("coverfull", "DE", "TSTD7K");
        _server.Add("disccustom", "DE", "TSTD7K");
        _server.Add("cover", "DE", "TSTD7K");
        _server.Add("cover3D", "EN", "TSTD7K");

        CoverSummary summary = await DownloadAsync(Destination(CoverLayout.UsbLoaderGx), ids: "TSTD7K");

        Assert.Equal(4, summary.Downloaded);
        Assert.Equal("coverfull/DE/TSTD7K", ReadArt(@"apps\usbloader_gx\images\full\TSTD7K.png"));
        Assert.Equal("disccustom/DE/TSTD7K", ReadArt(@"apps\usbloader_gx\images\disc\TSTD7K.png"));
        Assert.Equal("cover/DE/TSTD7K", ReadArt(@"apps\usbloader_gx\images\2D\TSTD7K.png"));
        Assert.Equal("cover3D/EN/TSTD7K", ReadArt(@"apps\usbloader_gx\images\TSTD7K.png"));
    }

    [Fact]
    public async Task Covers_already_present_are_left_alone_unless_overwriting()
    {
        _server.AddAllKinds("US", "TSTE01");
        CoverDestination destination = Destination(CoverLayout.WiiFlow);
        await DownloadAsync(destination, ids: "TSTE01");
        File.WriteAllText(_temp.Combine(@"wiiflow\covers\TSTE01.png"), "my own cover");
        _server.Requests.Clear();

        CoverSummary kept = await DownloadAsync(destination, ids: "TSTE01");

        Assert.Equal((0, 2), (kept.Downloaded, kept.Present));
        Assert.Empty(_server.Requests);
        Assert.Equal("my own cover", File.ReadAllText(_temp.Combine(@"wiiflow\covers\TSTE01.png")));

        CoverSummary replaced = await DownloadAsync(destination, overwrite: true, ids: "TSTE01");

        Assert.Equal(2, replaced.Downloaded);
        Assert.Equal("cover/US/TSTE01", ReadArt(@"wiiflow\covers\TSTE01.png"));
    }

    [Fact]
    public async Task An_image_several_loaders_use_is_downloaded_once_and_saved_for_each()
    {
        _server.AddAllKinds("US", "TSTE01");

        CoverSummary summary = await DownloadAsync(Destination([.. CoverLayout.All]), ids: ["TSTE01", "tste01"]);

        // Four kinds of cover, however many loaders want each one.
        Assert.Equal(4, summary.Downloaded);
        Assert.Equal(4, _server.Requests.Count);
        Assert.Equal(10, FilesWritten().Length);
        Assert.Equal(ReadArt(@"wiiflow\covers\TSTE01.png"), ReadArt(@"usb-loader\covers\TSTE01.png"));
    }

    [Fact]
    public async Task Art_gametdb_does_not_have_is_counted_and_nothing_is_created_for_it()
    {
        _server.AddAllKinds("US", "TSTE01");

        CoverSummary summary = await DownloadAsync(Destination(CoverLayout.WiiFlow), ids: ["TSTE01", "ZZZE99"]);

        Assert.Equal((2, 2), (summary.Downloaded, summary.NotFound));
        Assert.Equal(["ZZZE99"], summary.GamesWithoutArt);
        Assert.Equal(2, FilesWritten().Length);
        Assert.Equal("2 downloaded, 2 not on GameTDB", summary.ToString());
    }

    [Fact]
    public async Task A_response_that_is_not_an_image_is_never_saved_as_a_cover()
    {
        _server.Add("cover", "US", "TSTE01", Encoding.ASCII.GetBytes("<html>blocked</html>"));

        CoverSummary summary = await DownloadAsync(Destination(CoverLayout.WiiFlow), ids: "TSTE01");

        Assert.Equal(0, summary.Downloaded);
        Assert.Empty(FilesWritten());
    }

    [Fact]
    public async Task Server_errors_are_reported_as_failures_rather_than_as_missing_art()
    {
        _server.FailWith = HttpStatusCode.ServiceUnavailable;

        CoverSummary summary = await DownloadAsync(Destination(CoverLayout.WiiFlow), ids: "TSTE01");

        Assert.Equal((0, 0, 2), (summary.Downloaded, summary.NotFound, summary.Failed));
        Assert.Empty(summary.GamesWithoutArt);
    }

    [Fact]
    public void The_destination_follows_the_setting_and_the_loaders_found_on_the_drive()
    {
        string card = _temp.Combine("card");
        Directory.CreateDirectory(Path.Combine(card, "wbfs"));
        TargetLibrary target = TargetLibrary.Open(card);

        // Nothing identifies a loader yet, so "auto" has nowhere to put covers and says so.
        Assert.Null(CoverDestination.Resolve(CoverLoaderChoice.Auto, null, target, out string? reason));
        Assert.Contains("No loader folder", reason);
        Assert.Null(CoverDestination.Resolve(CoverLoaderChoice.None, null, target, out reason));
        Assert.Contains("switched off", reason);

        // An explicit choice works on a bare card.
        CoverDestination? chosen = CoverDestination.Resolve(CoverLoaderChoice.UsbLoaderGx, null, target, out reason);
        Assert.Equal((card, "USB Loader GX"), (chosen!.Root, Assert.Single(chosen.Layouts).Name));
        Assert.Null(reason);

        Directory.CreateDirectory(Path.Combine(card, "wiiflow"));
        Directory.CreateDirectory(Path.Combine(card, "usb-loader"));
        CoverDestination? detected = CoverDestination.Resolve(CoverLoaderChoice.Auto, null, target, out _);
        Assert.Equal(["WiiFlow", "Configurable USB Loader"], detected!.Layouts.Select(l => l.Name));

        // The loader can live on a different drive from the games.
        string sdCard = _temp.Combine("sd");
        Directory.CreateDirectory(Path.Combine(sdCard, "apps", "usbloader_gx"));
        CoverDestination? elsewhere = CoverDestination.Resolve(CoverLoaderChoice.Auto, sdCard, target, out _);
        Assert.Equal((sdCard, "USB Loader GX"), (elsewhere!.Root, Assert.Single(elsewhere.Layouts).Name));

        Assert.Null(CoverDestination.Resolve(CoverLoaderChoice.All, _temp.Combine("unplugged"), target, out reason));
        Assert.Contains("not available", reason);
    }

    [Fact]
    public void Loader_folders_sit_beside_the_wbfs_folder_not_inside_it()
    {
        string card = _temp.Combine("card");
        string wbfs = Path.Combine(card, "wbfs");
        string plain = _temp.Combine("plain");
        Directory.CreateDirectory(wbfs);
        Directory.CreateDirectory(plain);

        Assert.Equal(card, TargetLibrary.Open(card).DeviceRoot);
        Assert.Equal(card, TargetLibrary.Open(wbfs).DeviceRoot);
        Assert.Equal(plain, TargetLibrary.Open(plain).DeviceRoot);
        Assert.Equal(@"C:\", TargetLibrary.Open(@"C:\").DeviceRoot);
    }

    private CoverCache Cache(TimeProvider? clock = null) => new(_temp.Combine("cache"), new HttpClient(_server), clock);

    private static async Task<CoverAvailability> AskAsync(CoverCache cache, string id, string? deviceRoot = null) =>
        (await cache.GetAsync(id, deviceRoot, CancellationToken.None)).Availability;

    [Fact]
    public async Task A_cover_for_the_game_lists_is_downloaded_once_and_then_kept()
    {
        _server.AddAllKinds("US", "TSTE01");

        CachedCover first = await Cache().GetAsync("TSTE01", null, CancellationToken.None);
        CachedCover again = await Cache().GetAsync("tste01", null, CancellationToken.None);

        Assert.Equal(new CachedCover(CoverAvailability.Cached, _temp.Combine("cache", "TSTE01.png")), first);
        Assert.Equal(first, again);
        Assert.Equal(["cover/US/TSTE01"], _server.Requests);
        Assert.Equal([@"cache\TSTE01.png"], FilesWritten());
        Assert.Equal("cover/US/TSTE01", ReadArt(@"cache\TSTE01.png"));
    }

    [Fact]
    public async Task A_cover_a_loader_already_has_on_the_drive_is_copied_instead_of_downloaded()
    {
        _server.AddAllKinds("US", "TSTE01");
        _server.AddAllKinds("US", "TSTE02");
        string card = _temp.Combine("card");
        Directory.CreateDirectory(Path.Combine(card, @"apps\usbloader_gx\images\2D"));
        Directory.CreateDirectory(Path.Combine(card, @"wiiflow\covers"));
        File.WriteAllBytes(Path.Combine(card, @"apps\usbloader_gx\images\2D\TSTE01.png"), [.. PngSignature, .. "my own cover"u8]);
        File.WriteAllText(Path.Combine(card, @"wiiflow\covers\TSTE02.png"), "not an image at all");

        Assert.Equal(CoverAvailability.Cached, await AskAsync(Cache(), "TSTE01", card));
        Assert.Empty(_server.Requests);
        Assert.Equal("my own cover", ReadArt(@"cache\TSTE01.png"));

        // What is on the drive is only used if it is a picture.
        Assert.Equal(CoverAvailability.Cached, await AskAsync(Cache(), "TSTE02", card));
        Assert.Equal("cover/US/TSTE02", ReadArt(@"cache\TSTE02.png"));
    }

    [Fact]
    public async Task A_game_gametdb_has_no_cover_for_is_not_asked_about_again_for_a_month()
    {
        FakeClock clock = new();

        Assert.Equal(CoverAvailability.None, await AskAsync(Cache(clock), "ZZZE99"));
        Assert.Equal(["cover/US/ZZZE99", "cover/EN/ZZZE99"], _server.Requests);
        Assert.Equal(CoverAvailability.None, await AskAsync(Cache(clock), "ZZZE99"));
        Assert.Equal(2, _server.Requests.Count);

        // GameTDB gains the cover in the meantime; it is found once the month is up.
        _server.Add("cover", "US", "ZZZE99");
        clock.Advance(TimeSpan.FromDays(29));
        Assert.Equal(CoverAvailability.None, await AskAsync(Cache(clock), "ZZZE99"));
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(CoverAvailability.Cached, await AskAsync(Cache(clock), "ZZZE99"));
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    public async Task When_gametdb_cannot_be_reached_it_is_left_alone_for_a_while(bool offline, HttpStatusCode? failWith)
    {
        _server.AddAllKinds("US", "TSTE01");
        _server.Offline = offline;
        _server.FailWith = failWith;
        FakeClock clock = new();
        CoverCache cache = Cache(clock);

        Assert.Equal(CoverAvailability.Unreachable, await AskAsync(cache, "TSTE01"));
        int asked = _server.Requests.Count;
        Assert.Equal(CoverAvailability.Unreachable, await AskAsync(cache, "TSTE02"));
        Assert.Equal(asked, _server.Requests.Count);

        // In particular nothing has been written down about the game having no cover.
        Assert.Empty(FilesWritten());

        _server.Offline = false;
        _server.FailWith = null;
        clock.Advance(CoverCache.RetryDelay);
        Assert.Equal(CoverAvailability.Cached, await AskAsync(cache, "TSTE01"));
    }

    [Theory]
    [InlineData("TSTE0")]
    [InlineData("TSTE012")]
    [InlineData(@"..\RMG")]
    [InlineData("RMG/01")]
    [InlineData("RMG?01")]
    public async Task Something_that_is_not_a_game_id_is_never_looked_up(string id)
    {
        CoverCache cache = Cache();

        Assert.Equal(CoverAvailability.None, await AskAsync(cache, id, _temp.Path));
        cache.Discard(id);

        Assert.Empty(_server.Requests);
        Assert.Empty(FilesWritten());
    }

    [Fact]
    public async Task A_cached_cover_that_cannot_be_shown_is_thrown_out_and_fetched_again()
    {
        _server.AddAllKinds("US", "TSTE01");
        CoverCache cache = Cache();
        await AskAsync(cache, "TSTE01");

        cache.Discard("tste01");

        Assert.Empty(FilesWritten());
        Assert.Equal(CoverAvailability.Cached, await AskAsync(cache, "TSTE01"));
        Assert.Equal(2, _server.Requests.Count);
    }

    /// <summary>A clock that only moves when it is told to.</summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Stands in for art.gametdb.com: serves what it has been given and 404s everything else.</summary>
    private sealed class FakeGameTdb : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _art = [];

        public List<string> Requests { get; } = [];

        public HttpStatusCode? FailWith { get; set; }

        /// <summary>Makes every request fail the way it does when there is no connection.</summary>
        public bool Offline { get; set; }

        /// <summary>Adds an image whose content names it, so tests can tell which one was saved where.</summary>
        public void Add(string kind, string language, string id, byte[]? content = null)
        {
            string key = $"{kind}/{language}/{id}";
            _art[key] = content ?? [.. PngSignature, .. Encoding.ASCII.GetBytes(key)];
        }

        public void AddAllKinds(string language, string id)
        {
            foreach (string kind in (string[])["cover", "cover3D", "disc", "coverfullHQ"])
                Add(kind, language, id);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string key = request.RequestUri!.AbsolutePath["/wii/".Length..^".png".Length];
            lock (Requests)
                Requests.Add(key);

            if (Offline)
                throw new HttpRequestException("No such host is known.");
            if (FailWith is { } status)
                return Task.FromResult(new HttpResponseMessage(status));

            return Task.FromResult(_art.TryGetValue(key, out byte[]? bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
