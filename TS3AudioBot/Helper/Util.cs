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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Localization;
using TSLib.Messages;

namespace TS3AudioBot.Helper;

public static partial class Util
{
	public const RegexOptions DefaultRegexConfig = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.ECMAScript;

	[GeneratedRegex(@"^[\w-_]+$", RegexOptions.IgnoreCase | RegexOptions.ECMAScript)]
	private static partial Regex SafeFileNameReg();

	private static readonly string[] byteSuffix = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

	public static string FormatBytesHumanReadable(long bytes)
	{
		if (bytes == 0)
			return "0B";
		var order = (int)Math.Log(Math.Abs(bytes), 1024);
		return (bytes >> (10 * order)) + byteSuffix[order];
	}

	public static string FromSeed(int seed)
	{
		var seedstr = new char[7];
		uint plainseed = unchecked((uint)seed);
		for (int i = 0; i < 7; i++)
		{
			if (plainseed > 0)
			{
				plainseed--;
				var remainder = plainseed % 26;
				seedstr[i] = (char)(remainder + 'a');
				plainseed = (plainseed - remainder) / 26;
			}
			else
			{
				seedstr[i] = '\0';
			}
		}
		return new string(seedstr).TrimEnd('\0');
	}

	public static int ToSeed(string seed)
	{
		long finalValue = 0;

		for (int i = 0; i < seed.Length; i++)
		{
			long powVal = (seed[i] - 'a' + 1) * Pow(26, i % 7);
			finalValue += powVal;
			finalValue %= ((long)uint.MaxValue + 1);
		}
		var uval = (uint)finalValue;
		return unchecked((int)uval);
	}

	private static long Pow(long b, int pow)
	{
		long ret = 1;
		while (pow != 0)
		{
			if ((pow & 1) == 1)
				ret *= b;
			b *= b;
			pow >>= 1;
		}
		return ret;
	}

	public static Stream? GetEmbeddedFile(string name)
	{
		var assembly = Assembly.GetExecutingAssembly();
		return assembly.GetManifestResourceStream(name);
	}

	public static E<LocalStr> IsSafeFileName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return new LocalStr(strings.error_playlist_name_invalid_empty); // TODO change to more generic error
		if (name.Length > 64)
			return new LocalStr(strings.error_playlist_name_invalid_too_long);
		if (!SafeFileNameReg().IsMatch(name))
			return new LocalStr(strings.error_playlist_name_invalid_character);
		return R.Ok;
	}

	public static IEnumerable<TResult> SelectOk<TSource, TResult, TErr>(this IEnumerable<TSource> source, Func<TSource, R<TResult, TErr>> selector) where TSource : notnull where TResult : notnull where TErr : notnull
		=> source.Select(selector).Where(x => x.Ok).Select(x => x.Value);
	public static IEnumerable<TResult> SelectNotNull<TSource, TResult>(this IEnumerable<TSource> source, Func<TSource, TResult?> selector) where TResult : class
		=> source.Select(selector).Where(x => x != null)!;

	public static bool HasExitedSafe(this Process process)
	{
		try { return process.HasExited; }
		catch { return true; }
	}

	public static V GetOrNew<K, V>(this IDictionary<K, V> dict, K key) where K : notnull where V : new()
	{
		if (!dict.TryGetValue(key, out var val))
		{
			val = new V();
			dict[key] = val;
		}
		return val;
	}

	public static async Task CatchToLog(this Task t, NLog.Logger logger, NLog.LogLevel? level = null)
	{
		try
		{
			await t;
		}
		catch (AudioBotException ex)
		{
			logger.Log(level ?? NLog.LogLevel.Warn, ex, ex.Message);
		}
	}

	public static async Task<T?> Try<T>(this Task<T> t) where T : class
	{
		try { return await t; }
		catch { return null; }
	}

	public static T? Try<T>(Func<T> t) where T : class
	{
		try { return t(); }
		catch { return null; }
	}

	public static void UnwrapThrow(this E<LocalStr> r)
	{
		if (!r.Ok)
			throw new CommandException(r.Error.Str, CommandExceptionReason.CommandError);
	}

	public static T UnwrapThrow<T>(this R<T, LocalStr> r) where T : notnull
	{
		if (r.Get(out var value, out var error))
			return value;
		else
			throw new CommandException(error.Str, CommandExceptionReason.CommandError);
	}

	public static bool UnwrapToLog(this E<LocalStr> r, NLog.Logger logger, NLog.LogLevel? level = null)
	{
		if (!r.Ok)
			logger.Log(level ?? NLog.LogLevel.Warn, r.Error.Str);
		return r.Ok;
	}

	public static bool UnwrapToLog(this E<CommandError> r, NLog.Logger logger, NLog.LogLevel? level = null)
	{
		if (!r.Ok)
			logger.Log(level ?? NLog.LogLevel.Warn, r.Error.ErrorFormat());
		return r.Ok;
	}
}
