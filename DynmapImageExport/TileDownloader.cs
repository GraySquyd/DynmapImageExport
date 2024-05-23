using DynmapImageExport.Models;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace DynmapImageExport
{
	internal class TileDownloader : IDisposable
	{
		private readonly HttpClient Client = new();
		private readonly ImageMap Files = new();
		private readonly SemaphoreSlim Semaphore;
		private readonly TileMap Tiles;
		private readonly string Title;

		public TileDownloader(TileMap tiles) : this(tiles, 4) { }

		public TileDownloader(TileMap tiles, int threads)
		{
			Tiles = tiles;
			Title = SanitizeFileName(Tiles.Source.Title);
			Semaphore = new(threads);
			ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
		}

		public bool UseCache { get; set; } = true;

		public async Task<ImageMap> Download(IProgress<int> IP = default)
		{
			Trace.WriteLine($"Download started: {Tiles.Count} tiles");

			Files.Clear();
			var Tasks = Tiles.Select(async (KV) =>
			{
				var (DXY, Tile) = KV;
				var sanitizedTilePath = SanitizeFileName(Tile.TilePath);
				if (await TryDownloadTile(Tile, sanitizedTilePath) is string path) { Files[DXY] = path; }
				IP.Report(1);
			});
			await Task.WhenAll(Tasks);

			Trace.WriteLine($"Download done: {Files.Count} tiles");
			return Files;
		}

		private async Task<string> TryDownloadTile(Tile tile, string sanitizedTilePath)
		{
			var LocalFile = new FileInfo(Path.Combine("tiles", Title, sanitizedTilePath));
			if (UseCache && LocalFile.Exists)
			{
				Trace.WriteLine($"Cached tile: {sanitizedTilePath} ");
				return LocalFile.FullName;
			}
			try
			{
				await Semaphore.WaitAsync();
				Trace.WriteLine($"Downloading tile: {tile.TileURI} ");
				using var Response = await Client.GetAsync(tile.TileURI, HttpCompletionOption.ResponseHeadersRead);
				if (!Response.IsSuccessStatusCode)
				{
					Trace.WriteLine($"Tile: {tile} - {Response.StatusCode}({Response.ReasonPhrase})");
					return null;
				}

				Directory.CreateDirectory(LocalFile.DirectoryName);
				using var FS = new FileStream(LocalFile.FullName, FileMode.Create);
				using var RS = await Response.Content.ReadAsStreamAsync();
				await RS.CopyToAsync(FS);

				return LocalFile.FullName;
			}
			catch (HttpRequestException e)
			{
				Trace.WriteLine($"Tile: {tile} - {e.Message}");
			}
			finally
			{
				Semaphore.Release();
			}
			return null;
		}

		private static string SanitizeFileName(string fileName)
		{
			// more witchcraft, sanatizing w/ regex
			var sanitized = Regex.Replace(fileName, @"[<>:""/\\|?*\x00-\x1F§]", "");
			return sanitized.Trim().Trim('.');
		}

		#region IDispose

		public void Dispose()
		{
			((IDisposable)Client).Dispose();
		}

		#endregion IDispose
	}
}
