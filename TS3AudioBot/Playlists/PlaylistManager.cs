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
using TS3AudioBot.Config;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.Playlists.Shuffle;
using TS3AudioBot.Web.Model;
using TSLib.Helper;

namespace TS3AudioBot.Playlists;

public sealed class PlaylistManager
{
	private readonly PlaylistIO playlistPool;
	private const string mixName = ".mix";
	private readonly Playlist mixList = new() { Title = "Now Playing" };
	private readonly object listLock = new();
	public IReadOnlyPlaylist CurrentList => mixList;

	private IShuffleAlgorithm shuffle;

	private readonly IShuffleAlgorithm NormalOrder = new NormalOrder();
	private readonly IShuffleAlgorithm RandomOrder = new LinearFeedbackShiftRegister();

	public int Index { get => shuffle.Index; set => shuffle.Index = value; }

	public PlaylistItem? Current => MoveIndex(null, true);

	private bool random;
	public bool Random
	{
		get => random;
		set
		{
			random = value;
			var index = shuffle.Index;
			if (random)
				shuffle = RandomOrder;
			else
				shuffle = NormalOrder;
			shuffle.Index = index;
		}
	}

	public int Seed { get => shuffle.Seed; set => shuffle.Seed = value; }

	/// <summary>Loop mode for the current playlist.</summary>
	public LoopMode Loop { get; set; } = LoopMode.Off;

	public PlaylistManager(ConfPlaylists _, PlaylistIO playlistPool)
	{
		this.playlistPool = playlistPool;
		shuffle = NormalOrder;
	}

	public PlaylistItem? Next(bool manually = true) => MoveIndex(forward: true, manually);

	public PlaylistItem? Previous(bool manually = true) => MoveIndex(forward: false, manually);

	internal PlaylistItem? MoveIndex(bool? forward, bool manually)
	{
		lock (listLock)
		{
			if (mixList.Items.Count == 0)
				return null;

			if (shuffle.Length != mixList.Items.Count)
				shuffle.Length = mixList.Items.Count;
			if (shuffle.Index < 0 || shuffle.Index >= mixList.Items.Count)
				shuffle.Index = 0;

			// When next/prev was requested manually (via command) we ignore the loop one
			// mode and instead move the index.
			if ((Loop == LoopMode.One && !manually) || forward is null)
				return mixList[shuffle.Index];

			bool listEnded;
			if (forward == true)
				listEnded = shuffle.Next();
			else
				listEnded = shuffle.Prev();

			// Get a new seed when one play-through ended.
			if (listEnded && Random)
				SetRandomSeed();

			// If a next/prev request goes over the bounds of the list while loop mode is off
			// but was requested manually we act as if the list was looped.
			// This will give a more intuitive behaviour when the list is shuffeled (and also if not)
			// as the end might not be clear or visible.
			if (Loop == LoopMode.Off && listEnded && !manually)
				return null;

			return mixList[shuffle.Index];
		}
	}

	public void Queue(PlaylistItem item)
		=> ModifyPlaylist(mixName, mix => mix.Add(item).UnwrapThrow());

	public void Queue(IEnumerable<PlaylistItem> items)
		=> ModifyPlaylist(mixName, mix => mix.AddRange(items).UnwrapThrow());

	public void Clear()
		=> ModifyPlaylist(mixName, mix => mix.Clear());

	private void SetRandomSeed()
	{
		shuffle.Seed = Tools.Random.Next();
	}

	public R<IReadOnlyPlaylist, LocalStr> LoadPlaylist(string listId)
	{
		return LoadPlaylist(listId, false);
	}

	/// <summary>
	/// 加载播放列表
	/// </summary>
	/// <param name="listId">播放列表ID</param>
	/// <param name="forceReload">是否强制重新从文件加载（忽略缓存）</param>
	public R<IReadOnlyPlaylist, LocalStr> LoadPlaylist(string listId, bool forceReload)
	{
		R<Playlist, LocalStr> res;
		if (listId.StartsWith('.'))
		{
			res = GetSpecialPlaylist(listId);
		}
		else
		{
			// 如果需要强制重新加载，先清除缓存
			if (forceReload)
			{
				playlistPool.ForceReload(listId);
			}
			// 跳过文件名验证，因为文件可能已经存在（可能包含中文字符）
			// 文件系统本身会处理不安全的文件名
			res = playlistPool.Read(listId);
		}

		if (!res.Ok)
			return res.Error;
		return res.Value;
	}

	public E<LocalStr> CreatePlaylist(string listId, string? title = null)
	{
		if (!Util.IsSafeFileName(listId).GetOk(out var error))
			return error;
		if (playlistPool.Exists(listId))
			return new LocalStr("Already exists");
		return playlistPool.Write(listId, new Playlist().SetTitle(title ?? listId));
	}

	public bool ExistsPlaylist(string listId)
	{
		if (GetSpecialPlaylist(listId))
			return true;
		// 跳过文件名验证，因为文件可能已经存在（可能包含中文字符）
		// 文件系统本身会处理不安全的文件名
		return playlistPool.Exists(listId);
	}

	public E<LocalStr> ModifyPlaylist(string listId, Action<Playlist> action)
	{
		var res = GetSpecialPlaylist(listId);
		if (res.GetOk(out var plist))
		{
			lock (listLock)
			{
				action(plist);
			}
			return R.Ok;
		}
		else
		{
			// 跳过文件名验证，因为文件可能已经存在（可能包含中文字符）
			// 文件系统本身会处理不安全的文件名
			if (!playlistPool.Read(listId).Get(out plist, out var error))
				return error;
			lock (listLock)
			{
				action(plist);
			}
			return playlistPool.Write(listId, plist);
		}
	}

	public E<LocalStr> DeletePlaylist(string listId)
	{
		// 跳过文件名验证，因为文件可能已经存在（可能包含中文字符）
		// 文件系统本身会处理不安全的文件名
		return playlistPool.Delete(listId);
	}

	public R<PlaylistInfo[], LocalStr> GetAvailablePlaylists(string? pattern = null) => playlistPool.ListPlaylists(pattern);

	private R<Playlist, LocalStr> GetSpecialPlaylist(string listId)
	{
		return listId switch
		{
			mixName => mixList,
			_ => new LocalStr(strings.error_playlist_special_not_found),
		};
	}
}
