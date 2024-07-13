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
using System.Linq;
using System.Text;
using TSLib;

namespace TS3AudioBot.Rights;

internal class ParseContext
{
	public List<RightsDecl> Declarations { get; }
	public RightsGroup[] Groups { get; private set; }
	public RightsRule[] Rules { get; private set; }
	public List<string> Errors { get; }
	public List<string> Warnings { get; }
	public ISet<string> RegisteredRights { get; }

	public RightsRule RootRule { get; }
	public bool NeedsAvailableGroups { get; set; } = false;
	public bool NeedsAvailableChanGroups { get; set; } = false;
	public HashSet<TsPermission> NeedsPermOverview { get; set; } = [];

	public ParseContext(ISet<string> registeredRights)
	{
		Declarations = [];
		RootRule = new RightsRule();
		Errors = [];
		Warnings = [];
		RegisteredRights = registeredRights;
		Groups = [];
		Rules = [];
	}

	public void SplitDeclarations()
	{
		Groups = Declarations.OfType<RightsGroup>().ToArray();
		Rules = Declarations.OfType<RightsRule>().ToArray();
	}

	public (bool hasErrors, string info) AsResult()
	{
		var strb = new StringBuilder();
		foreach (var warn in Warnings)
			strb.Append("WRN: ").AppendLine(warn);
		if (Errors.Count == 0)
		{
			strb.Append(string.Join("\n", Rules.Select(x => x.ToString())));
			return (true, strb.ToString());
		}
		else
		{
			foreach (var err in Errors)
				strb.Append("ERR: ").AppendLine(err);
			return (false, strb.ToString());
		}
	}
}
