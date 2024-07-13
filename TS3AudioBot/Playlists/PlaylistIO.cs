// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using TS3AudioBot.Algorithm;
using TS3AudioBot.Config;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Web.Model;
using TSLib.Helper;

namespace TS3AudioBot.Playlists;

public sealed class PlaylistIO : IDisposable
{
	private readonly ConfBot confBot;
	private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
	private readonly Dictionary<string, PlaylistMeta> playlistInfo = [];
	private readonly LruCache<string, Playlist> playlistCache = new(16);
	private readonly HashSet<string> dirtyList = [];
	private readonly ReaderWriterLockSlim rwLock = new();
	private bool reloadFolderCache = true;
	private const int FileVersion = 3;
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	public PlaylistIO(ConfBot confBot)
	{
		this.confBot = confBot;
	}

	private FileInfo? NameToFile(string listId)
	{
		var localDir = confBot.LocalConfigDir;
		if (localDir is null)
			return null;
		return new FileInfo(Path.Combine(localDir, BotPaths.Playlists, listId));
	}

	public R<Playlist, LocalStr> Read(string listId) => ReadInternal(listId);

	private R<Playlist, LocalStr> ReadInternal(string listId)
	{
		bool hasReadLock = false;
		bool hasWriteLock = false;

		try
		{
			if (!hasReadLock && !hasWriteLock)
			{
				rwLock.EnterReadLock();
				hasReadLock = true;
			}

			if (playlistCache.TryGetValue(listId, out var playlist))
			{
				return playlist;
			}

			if (!hasWriteLock)
			{
				rwLock.ExitReadLock();
				hasReadLock = false;

				rwLock.EnterWriteLock();
				hasWriteLock = true;
			}

			if (ReadFromFile(listId).Get(out var plist, out var error))
			{
				playlistCache.Set(listId, plist);
				return plist;
			}
			else
			{
				return error;
			}
		}
		finally
		{
			if (hasReadLock)
				rwLock.ExitReadLock();
			if (hasWriteLock)
				rwLock.ExitWriteLock();
		}
	}

	private R<Playlist, LocalStr> ReadFromFile(string listId, bool headOnly = false)
	{
		var fi = NameToFile(listId);
		if (fi is null || !fi.Exists)
			return new LocalStr(strings.error_playlist_not_found);

		using var sr = new StreamReader(fi.Open(FileMode.Open, FileAccess.Read, FileShare.Read), Tools.Utf8Encoder);
		var metaRes = ReadHeadStream(sr);
		if (!metaRes.Get(out var meta, out var error))
			return error;

		playlistInfo[listId] = meta;

		var plist = new Playlist
		{
			Title = meta.Title
		};

		if (headOnly)
			return plist;

		// read content
		string? line;
		while ((line = sr.ReadLine()) != null)
		{
			var kvp = line.Split([':'], 2);
			if (kvp.Length < 2) continue;

			string key = kvp[0];
			string value = kvp[1];

			switch (key)
			{
			// Legacy entry
			case "rs":
				{
					var rskvp = value.Split([':'], 2);
					if (kvp.Length < 2)
					{
						Log.Warn("Erroneous playlist split count: {0}", line);
						continue;
					}
					string content = rskvp[1];

					var rsSplit = content.Split([','], 3);
					if (rsSplit.Length < 3)
						goto default;
					if (!string.IsNullOrWhiteSpace(rsSplit[0]))
						plist.Add(new PlaylistItem(new AudioResource(Uri.UnescapeDataString(rsSplit[1]), Uri.UnescapeDataString(rsSplit[2]), rsSplit[0])));
					else
						goto default;
					break;
				}

			case "rsj":
				var res = JsonSerializer.Deserialize<AudioResource>(value, JsonOptions);
				if (res is null)
				{
					Log.Warn("Invalid rsj element: {0}", line);
					break;
				}
				plist.Add(new PlaylistItem(res));
				break;

			case "id":
			case "ln":
				Log.Warn("Deprecated playlist data block: {0}", line);
				break;

			default:
				Log.Warn("Erroneous playlist data block: {0}", line);
				break;
			}
		}

		meta.Count = plist.Items.Count;
		return plist;
	}

	private static R<PlaylistMeta, LocalStr> ReadHeadStream(StreamReader sr)
	{
		string? line;
		int version = -1;

		// read header
		while ((line = sr.ReadLine()) != null)
		{
			if (string.IsNullOrEmpty(line))
				break;

			var kvp = line.Split([':'], 2);
			if (kvp.Length < 2) continue;

			string key = kvp[0];
			string value = kvp[1];

			switch (key)
			{
			case "version":
				version = int.Parse(value);
				if (version > FileVersion)
					return new LocalStr("The file version is too new and can't be read."); // LOC: TODO
				break;
			case "meta":
				var meta = JsonSerializer.Deserialize<PlaylistMeta>(value, JsonOptions);
				if (meta is null)
				{
					Log.Warn("Invalid meta element: {0}", line);
					break;
				}
				meta.Version = version;
				return meta;
			}
		}

		return new PlaylistMeta { Title = "", Count = 0, Version = version };
	}

	public E<LocalStr> Write(string listId, IReadOnlyPlaylist list)
	{
		try
		{
			rwLock.EnterWriteLock();

			var result = WriteToFile(listId, list);
			dirtyList.Remove(listId);
			return result;
		}
		finally
		{
			rwLock.ExitWriteLock();
		}
	}

	private E<LocalStr> WriteToFile(string listId, IReadOnlyPlaylist plist)
	{
		var fi = NameToFile(listId);
		if (fi is null)
			return new LocalStr(strings.error_playlist_no_store_directory);
		var dir = fi.Directory ?? throw new Exception("Could not get playlist folder");
		if (!dir.Exists)
			dir.Create();

		using (var sw = new StreamWriter(fi.Open(FileMode.Create, FileAccess.Write, FileShare.Read), Tools.Utf8Encoder))
		{
			var meta = playlistInfo.GetOrNew(listId);
			meta.Title = plist.Title;
			meta.Count = plist.Items.Count;
			meta.Version = FileVersion;

			sw.WriteLine("version:" + FileVersion);
			sw.Write("meta:");
			sw.Write(JsonSerializer.Serialize(meta, JsonOptions));
			sw.WriteLine();

			sw.WriteLine();

			foreach (var pli in plist.Items)
			{
				sw.Write("rsj:");
				sw.Write(JsonSerializer.Serialize(pli.AudioResource, JsonOptions));
				sw.WriteLine();
			}
		}
		return R.Ok;
	}

	public E<LocalStr> Delete(string listId)
	{
		try
		{
			rwLock.EnterWriteLock();
			return DeleteInternal(listId);
		}
		finally
		{
			rwLock.ExitWriteLock();
		}
	}

	private E<LocalStr> DeleteInternal(string listId)
	{
		var fi = NameToFile(listId);
		bool cached = playlistInfo.ContainsKey(listId);

		if (!cached && (fi is null || !fi.Exists))
			return new LocalStr(strings.error_playlist_not_found);

		playlistCache.Remove(listId);
		playlistInfo.Remove(listId);
		dirtyList.Remove(listId);

		try
		{
			fi?.Delete();
			return R.Ok;
		}
		catch (IOException) { return new LocalStr(strings.error_io_in_use); }
		catch (System.Security.SecurityException) { return new LocalStr(strings.error_io_missing_permission); }
	}

	public R<PlaylistInfo[], LocalStr> ListPlaylists(string? pattern)
	{
		if (confBot.LocalConfigDir is null)
			return new LocalStr("Temporary bots cannot have playlists"); // TODO do this for all other methods too

		bool hasWriteLock = false;
		try
		{
			if (reloadFolderCache)
			{
				rwLock.EnterWriteLock();
				hasWriteLock = true;

				var di = new DirectoryInfo(Path.Combine(confBot.LocalConfigDir, BotPaths.Playlists));
				if (!di.Exists)
					return Array.Empty<PlaylistInfo>();

				IEnumerable<FileInfo> fileEnu;
				if (string.IsNullOrEmpty(pattern))
					fileEnu = di.EnumerateFiles();
				else
					fileEnu = di.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly); // TODO exceptions

				playlistInfo.Clear();
				foreach (var fi in fileEnu)
				{
					ReadFromFile(fi.Name, true);
				}

				reloadFolderCache = false;
			}
			else
			{
				rwLock.EnterReadLock();
				hasWriteLock = false;
			}

			return playlistInfo.Select(kvp => new PlaylistInfo(kvp.Key, kvp.Value.Title)
			{
				SongCount = kvp.Value.Count
			}).ToArray();
		}
		finally
		{
			if (hasWriteLock)
				rwLock.ExitWriteLock();
			else
				rwLock.ExitReadLock();
		}
	}

	public void ReloadFolderCache() => reloadFolderCache = true;

	public bool Exists(string listId)
	{
		try
		{
			rwLock.EnterWriteLock();
			return ExistsInternal(listId);
		}
		finally
		{
			rwLock.ExitWriteLock();
		}
	}

	public bool ExistsInternal(string listId)
	{
		if (playlistInfo.ContainsKey(listId))
			return true;
		var fi = NameToFile(listId);
		return fi is null || fi.Exists;
	}

	public void Flush()
	{
		try
		{
			rwLock.EnterWriteLock();

			foreach (var name in dirtyList)
			{
				if (playlistCache.TryGetValue(name, out var plist))
					WriteToFile(name, plist);
			}

			dirtyList.Clear();
		}
		finally
		{
			rwLock.ExitWriteLock();
		}
	}

	public void Dispose()
	{
		Flush();

		rwLock.Dispose();
	}
}

public class PlaylistMeta
{
	[JsonPropertyName("count")]
	public int Count { get; set; }
	[JsonPropertyName("title")]
	public string Title { get; set; } = string.Empty;
	[JsonIgnore]
	public int Version { get; set; }
}
