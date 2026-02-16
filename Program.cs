using HtmlAgilityPack;
using ShellProgressBar;
using System.Buffers;

if (args.Length > 2) return Exit("Too many arguments given.");
if (args is not [string albumLink, ..]) return Exit("Must provide an album URL.");
if (args is not [_, string outputDir]) outputDir = Environment.CurrentDirectory;

// XPath selectors
const string SongLinks = "//table[@id = 'songlist']//td[@class = 'playlistDownloadSong']/a";
const string MP3Link = "//span[@class = 'songDownloadLink']/text()[contains(., 'Click here to download as MP3')]/../..";

// Progress bar styling
const char ProgressCharacter = '─';
ConsoleColor[] colors =
[
    ConsoleColor.DarkGreen,
    ConsoleColor.DarkCyan,
    ConsoleColor.DarkRed,
    ConsoleColor.DarkMagenta,
    ConsoleColor.DarkYellow,
    ConsoleColor.Blue,
    ConsoleColor.Green,
    ConsoleColor.Cyan,
    ConsoleColor.Red,
    ConsoleColor.Magenta,
    ConsoleColor.Yellow
];

// Set up output directory
Uri albumUri = new(albumLink);
string albumName = Path.GetFileName(albumUri.LocalPath);
outputDir = Path.Combine(outputDir, albumName);
Directory.CreateDirectory(outputDir);

// Show progress bar
using ProgressBar pbar = new(10000, $"Downloading {albumName}", new ProgressBarOptions
{
    ProgressCharacter = ProgressCharacter
});
using HttpClient client = new();
List<Uri> songUris;

// Load album HTML
HtmlDocument albumDoc = new();
albumDoc.LoadHtml(await client.GetStringAsync(albumUri));

// Get songs in album
songUris = [.. from songLink in albumDoc.DocumentNode.SelectNodes(SongLinks)
               select new Uri($"https://downloads.khinsider.com{songLink.GetAttributeValue("href", "")}")];
pbar.MaxTicks = songUris.Count;
pbar.Message = $"Downloading {albumName} (0/{songUris.Count})";

// Download each song
await Task.WhenAll(songUris.Select(async (songUri, i) =>
{
    // Get file link from song page
    HtmlDocument songDoc = new();
    songDoc.LoadHtml(await client.GetStringAsync(songUri));
    Uri fileUri = new(songDoc.DocumentNode.SelectSingleNode(MP3Link).GetAttributeValue("href", ""));

    // Show download bar
    string fileName = Path.GetFileName(fileUri.LocalPath);
    using ChildProgressBar downloadBar = pbar.Spawn(10000, $"Downloading {fileName}", new ProgressBarOptions
    {
        ProgressCharacter = ProgressCharacter,
        ForegroundColor = colors[i % colors.Length],
        CollapseWhenFinished = true
    });

    // Get song file size
    using HttpResponseMessage response = await client.GetAsync(fileUri, HttpCompletionOption.ResponseHeadersRead);
    long? length = response.Content.Headers.ContentLength;
    IProgress<float>? progress = length.HasValue ? downloadBar.AsProgress<float>() : null;
    string fileSize = length.HasValue ? FormatFileSize(length.Value) : "?";

    // Set up file streams
    using Stream inStream = await response.Content.ReadAsStreamAsync();
    await using FileStream outStream = new(Path.Combine(outputDir, fileName),
                                           FileMode.Create,
                                           FileAccess.Write,
                                           FileShare.Read,
                                           4096,
                                           FileOptions.Asynchronous);
    using IMemoryOwner<byte> memory = MemoryPool<byte>.Shared.Rent(81920);
    Memory<byte> buffer = memory.Memory;
    long totalRead = 0;

    // Download file
    while (await inStream.ReadAsync(buffer) is int bytesRead and not 0)
    {
        totalRead += bytesRead;
        progress?.Report((float)totalRead / length!.Value);
        downloadBar.Message = $"Downloading {fileName} ({FormatFileSize(totalRead)}/{fileSize})";
        await outStream.WriteAsync(buffer[..bytesRead]);
    }

    // Update album progress
    pbar.Tick($"Downloading {albumName} ({pbar.CurrentTick + 1}/{songUris.Count})");
}));

return 0;

// Prints the specified error message and returns a non-zero exit code
static int Exit(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

// Formats the specified number as a file size string
static string FormatFileSize(long i) => i switch
{
    >= 1L << 60 => $"{(i >> 50) / 1024.0:0.##} EB",
    >= 1L << 50 => $"{(i >> 40) / 1024.0:0.##} PB",
    >= 1L << 40 => $"{(i >> 30) / 1024.0:0.##} TB",
    >= 1L << 30 => $"{(i >> 20) / 1024.0:0.##} GB",
    >= 1L << 20 => $"{(i >> 10) / 1024.0:0.##} MB",
    >= 1L << 10 => $"{i / 1024.0:0.##} KB",
    _ => $"{i} B"
};
