// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Polytoria.Providers.Datastore;

/// <summary>In-memory datastore for local tests. Applies the live provider's value and quota rules so scripts fail locally the way they would online.</summary>
public class LocalDatastoreProvider : IDatastoreProvider
{
	// Shared by every datastore in the server, like the live provider's counters.
	private static int _readRequestsThisMinute = 0, _writeRequestsThisMinute = 0, _currentMinute = -1;
	private readonly Dictionary<string, object?> _data = [];
	private Datamodel.Data.Datastore? _ds;

	public void Connect(string key, Datamodel.Data.Datastore ds)
	{
		_ds = ds;
	}

	private int PlayersCount => _ds?.DatastoreService?.Root?.Players?.PlayersCount ?? 0;

	private static void ResetQuotaEachMinute()
	{
		if (_currentMinute != DateTime.Now.Minute)
		{
			_currentMinute = DateTime.Now.Minute;
			_readRequestsThisMinute = 0;
			_writeRequestsThisMinute = 0;
		}
	}

	public async Task<object?> ReadData(string key)
	{
		ResetQuotaEachMinute();
		if (_readRequestsThisMinute >= PTDatastoreProvider.MaxReadRequestsPerMinute + (PTDatastoreProvider.ReadRequestsPerPlayerModifier * PlayersCount))
		{
			throw new PTDatastoreProvider.PTDatastoreQuotaException("Read quota exceeded");
		}
		_readRequestsThisMinute++;

		if (_data.TryGetValue(key, out object? val))
		{
			return val;
		}
		else
		{
			return null;
		}
	}

	public async Task WriteData(string key, object? value)
	{
		if (value != null && !PTDatastoreProvider.CheckSupportedType(value))
		{
			throw new InvalidOperationException("Invalid value type");
		}
		ResetQuotaEachMinute();
		if (_writeRequestsThisMinute >= PTDatastoreProvider.MaxWriteRequestsPerMinute + (PTDatastoreProvider.WriteRequestsPerPlayerModifier * PlayersCount))
		{
			throw new PTDatastoreProvider.PTDatastoreQuotaException("Write quota exceeded");
		}
		_writeRequestsThisMinute++;

		// Live values round-trip through JSON: every number comes back as a double.
		_data[key] = value switch
		{
			int i => (double)i,
			float f => (double)f,
			_ => value
		};
	}

	public void Dispose()
	{
		_data.Clear();
		GC.SuppressFinalize(this);
	}
}
