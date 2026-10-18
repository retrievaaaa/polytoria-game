// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Polytoria.Scripting.Luau;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Polytoria.Creator.Gark;

/// <summary>A private construction VM. Never registers gameplay objects or shares gameplay globals.</summary>
internal static partial class GarkLuau
{
	// Only the stable userdata/interrupt prefix of lua_Callbacks is accessed.
	// https://github.com/luau-lang/luau/blob/master/VM/include/lua.h
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void Interrupt(IntPtr state, int gc);
	[DllImport("Luau.VM", CallingConvention = CallingConvention.Cdecl)]
	private static extern IntPtr lua_callbacks(IntPtr state);
	[DllImport("Luau.VM", CallingConvention = CallingConvention.Cdecl)]
	private static extern int lua_break(IntPtr state);
	[DllImport("Luau.VM", CallingConvention = CallingConvention.Cdecl)]
	private static extern int lua_isyieldable(IntPtr state);
	[DllImport("Luau.VM", CallingConvention = CallingConvention.Cdecl)]
	private static extern int lua_checkstack(IntPtr state, int extra);

	private const string Prelude = """
		local call = __gark
		__gark = nil
		-- Native callback boundaries cannot yield. Keep custom comparisons in Luau
		-- so even a non-terminating comparator remains interruptible by Stop.
		local nativeSort = table.sort
		table.sort = function(t, less)
			if less == nil then return nativeSort(t) end
			for i=2,#t do
				local value, j = t[i], i-1
				while j > 0 and less(value, t[j]) do t[j+1]=t[j]; j-=1 end
				t[j+1]=value
			end
		end
		local nativeGsub = string.gsub
		string.gsub = function(s, pattern, replacement, count)
			assert(type(replacement) ~= 'function', 'Construction string.gsub accepts string/table replacements; use Luau loops for callbacks.')
			return nativeGsub(s, pattern, replacement, count)
		end
		local function invoke(op, args)
			local ok, value = call(op, args)
			if not ok then error(value, 3) end
			return value
		end
		scene = table.freeze({
			create = function(class_name, properties, parent) return invoke('create', {class_name=class_name, properties=properties or {}, parent=parent}) end,
			set = function(id, properties) return invoke('set', {id=id, properties=properties}) end,
			reparent = function(id, parent) return invoke('reparent', {id=id, parent=parent}) end,
			delete = function(id) return invoke('delete', {id=id}) end,
			get = function(id) return invoke('get', {id=id}) end,
			children = function(id, include_properties)
				if type(include_properties) == 'table' and include_properties.include_properties ~= nil then include_properties = include_properties.include_properties end
				return invoke('children', {id=id, include_properties=include_properties})
			end,
			descendants = function(id, class_name, include_properties)
				if type(class_name) == 'table' then class_name, include_properties = class_name.class_name, class_name.include_properties end
				return invoke('descendants', {id=id, class_name=class_name, include_properties=include_properties})
			end,
			find = function(path) return invoke('find', {path=path}) end,
			selection = function() return invoke('selection', {}) end,
			bounds = function(id) return invoke('bounds', {id=id}) end,
			raycast = function(args) return invoke('raycast', args) end,
			overlap = function(args) return invoke('overlap', args) end,
		})
		print = function(...)
			local args = table.pack(...)
			for i=1,args.n do args[i] = tostring(args[i]) end
			invoke('print', {text=table.concat(args, '\t', 1, args.n)})
		end
		""";

	internal static async Task<(JsonNode? Result, string Output)> RunAsync(string source,
		Func<string, JsonObject, JsonNode?> bridge, Action checkContext, CancellationToken token)
	{
		using var state = new LuaState();
		state.OpenLibs();
		// Keep standard computation libraries; no host IO, nested VMs or environment access.
		foreach (string name in new[] { "io", "os", "package", "debug", "require", "load", "loadfile", "loadstring", "dofile", "getfenv", "setfenv", "newproxy", "coroutine", "getmetatable", "setmetatable", "pcall", "xpcall" })
		{
			state.PushNil(); state.SetGlobal(name);
		}
		var output = new StringBuilder();
		LuaFunction host = ptr => {
			// Do not longjmp or let managed exceptions cross the native callback boundary.
			var callbackState = new LuaState(ptr);
			int top = callbackState.GetTop();
			try
			{
				checkContext();
				string op = callbackState.ToString(1) ?? throw new ArgumentException("Missing scene operation.");
				var args = Read(callbackState, 2, []) as JsonObject ?? throw new ArgumentException("Expected an argument table.");
				JsonNode? value;
				if (op == "print") { output.AppendLine(args["text"]!.GetValue<string>()); value = null; }
				else value = bridge(op, args);
				callbackState.PushBoolean(true); Push(callbackState, value); return 2;
			}
			catch (Exception ex)
			{
				callbackState.SetTop(top);
				callbackState.PushBoolean(false); callbackState.PushString(ex.GetBaseException().Message); return 2;
			}
		};
		// Root this delegate for the private VM's lifetime, avoiding shared gameplay callback registries.
		NativeBindings.lua_pushcclosurek(state.State, host, "scene", 0, null);
		state.SetGlobal("__gark");
		var slice = Stopwatch.StartNew();
		Interrupt interrupt = (ptr, gc) => {
			if (gc < 0 && (token.IsCancellationRequested || slice.ElapsedMilliseconds >= 8) && lua_isyieldable(ptr) != 0)
				lua_break(ptr);
		};
		IntPtr callbacks = lua_callbacks(state.State);
		Marshal.WriteIntPtr(callbacks, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(interrupt));
		try
		{
			state.Load("Gark construction", LuaState.Compile(Prelude + "\nreturn (function()\n" + source + "\nend)()"));
			while (true)
			{
				checkContext();
				slice.Restart();
				LuaStatus status = state.Resume();
				checkContext();
				if (status == LuaStatus.OK) return (state.GetTop() == 0 ? null : Read(state, -1, []), output.ToString());
				if (status != LuaStatus.Break) throw new InvalidOperationException(state.ToString(-1) ?? "Construction failed: " + status);
				await Task.Delay(1, token); // Yield, not a deadline: runs until completion or user Stop.
			}
		}
		finally
		{
			Marshal.WriteIntPtr(callbacks, IntPtr.Size, IntPtr.Zero);
			GC.KeepAlive(interrupt); GC.KeepAlive(host);
		}
	}

	private static JsonNode? Read(LuaState state, int index, HashSet<IntPtr> seen)
	{
		if (lua_checkstack(state.State, 4) == 0) throw new ArgumentException("Scene data exceeds the VM's stack capacity.");
		switch (state.Type(index))
		{
			case LuaType.Nil: return null;
			case LuaType.String: return JsonValue.Create(state.ToString(index));
			case LuaType.Boolean: return JsonValue.Create(state.ToBoolean(index));
			case LuaType.Integer: case LuaType.Number:
				double value = state.ToNumber(index);
				if (!double.IsFinite(value)) throw new ArgumentException("Scene data must contain finite numbers.");
				return JsonNode.Parse(value.ToString("R", CultureInfo.InvariantCulture));
			case LuaType.Table:
				if (seen.Count >= 64 || !seen.Add(state.ToPointer(index))) throw new ArgumentException("Scene data cannot contain cyclic or excessively nested tables.");
				int at = state.AbsIndex(index);
				var values = new SortedDictionary<int, JsonNode?>();
				var obj = new JsonObject();
				state.PushNil();
				while (state.Next(at))
				{
					if (state.Type(-2) == LuaType.String) obj.Add(state.ToString(-2)!, Read(state, -1, seen));
					else if (state.Type(-2) is LuaType.Number or LuaType.Integer)
					{
						double key = state.ToNumber(-2);
						if (key < 1 || key > int.MaxValue || key != Math.Truncate(key)) throw new ArgumentException("Array keys must be positive integers.");
						values.Add((int)key, Read(state, -1, seen));
					}
					else throw new ArgumentException("Scene table keys must be strings or array indices.");
					state.Pop(1);
				}
				seen.Remove(state.ToPointer(at));
				if (values.Count == 0) return obj;
				if (obj.Count > 0 || !values.ContainsKey(values.Count)) throw new ArgumentException("Use an object table or a contiguous array, not a mixed/sparse table.");
				var array = new JsonArray();
				foreach (var entry in values) array.Add(entry.Value);
				return array;
			default: throw new ArgumentException("Return scene data as strings, numbers, booleans and plain tables.");
		}
	}

	private static void Push(LuaState state, JsonNode? node)
	{
		if (lua_checkstack(state.State, 4) == 0) throw new ArgumentException("Scene data exceeds the VM's stack capacity.");
		if (node == null) { state.PushNil(); return; }
		if (node is JsonObject obj)
		{
			state.NewTable();
			foreach (var (key, value) in obj) { state.PushString(key); Push(state, value); state.RawSet(-3); }
		}
		else if (node is JsonArray array)
		{
			state.NewTable();
			for (int i = 0; i < array.Count; i++) { Push(state, array[i]); state.RawSetInteger(-2, i + 1); }
		}
		else switch (node.GetValueKind())
		{
			case JsonValueKind.String: state.PushString(node.GetValue<string>()); break;
			case JsonValueKind.True: state.PushBoolean(true); break;
			case JsonValueKind.False: state.PushBoolean(false); break;
			case JsonValueKind.Number: state.PushNumber(double.Parse(node.ToJsonString(), CultureInfo.InvariantCulture)); break;
			default: state.PushNil(); break;
		}
	}
}
