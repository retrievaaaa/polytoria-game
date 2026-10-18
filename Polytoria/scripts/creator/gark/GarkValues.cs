// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Attributes;
using Polytoria.Datamodel;
using Polytoria.Datamodel.Data;
using Polytoria.Datamodel.Interfaces;
using Polytoria.Datamodel.Resources;
using Polytoria.Scripting.Datatypes;
using Polytoria.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Instance = Polytoria.Datamodel.Instance;

namespace Polytoria.Creator.Gark;

/// <summary>
/// JSON form of every editable datamodel value: vectors and colors, ranges and series, UI scales,
/// instance references and asset resources. Shared by the MCP tools and the construction VM.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Polytoria.csproj roots the entire datamodel assembly for runtime reflection.")]
[UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "Polytoria.csproj roots the entire datamodel assembly for runtime reflection.")]
[UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Polytoria.csproj roots the entire datamodel assembly for runtime reflection.")]
[UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Polytoria.csproj roots the entire datamodel assembly for runtime reflection.")]
[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Polytoria.csproj roots the entire datamodel assembly for runtime reflection.")]
[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Arrays of datamodel property element types; the Creator runs on the JIT runtime.")]
internal static class GarkValues
{
	/// <summary>Resolves instance references and owns newly created asset resources.</summary>
	internal sealed record Context(World World, Func<string, Instance> Resolve);

	private static readonly ConcurrentDictionary<Type, PropertyInfo[]> EditableCache = new();

	/// <summary>Editable properties the tools can read and write. Script source and linked files have dedicated tools.</summary>
	internal static PropertyInfo[] Editable(Type type) => EditableCache.GetOrAdd(type, static t => t
		.GetProperties(BindingFlags.Public | BindingFlags.Instance)
		.Where(p => p.GetIndexParameters().Length == 0 && p.IsDefined(typeof(EditableAttribute), true)
			&& p.GetMethod?.IsPublic == true && p.SetMethod?.IsPublic == true
			&& p.Name is not ("LinkedScript" or "LinkedModel" or "Source") && Supported(p.PropertyType))
		.GroupBy(p => p.Name).Select(g => g.First()).OrderBy(p => p.Name, StringComparer.Ordinal).ToArray());

	internal static bool Supported(Type type)
	{
		type = Nullable.GetUnderlyingType(type) ?? type;
		if (type.IsArray) return type.GetArrayRank() == 1 && Supported(type.GetElementType()!);
		return type.IsEnum || type == typeof(string) || type == typeof(bool) || type == typeof(float) || type == typeof(double)
			|| type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(Vector3) || type == typeof(Vector2)
			|| type == typeof(Color) || type == typeof(Quaternion) || type == typeof(NumberRange) || type == typeof(NumberSeries)
			|| type == typeof(ColorSeries) || type == typeof(Variant)
			|| typeof(Instance).IsAssignableFrom(type) || typeof(BaseAsset).IsAssignableFrom(type) || IsDataStruct(type);
	}

	private static bool IsDataStruct(Type type) => type.IsValueType && typeof(IData).IsAssignableFrom(type) && DataProperties(type).Length > 0;
	private static PropertyInfo[] DataProperties(Type type) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
		.Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod?.IsPublic == true && p.SetMethod?.IsPublic == true
			&& p.PropertyType != type && Supported(p.PropertyType)).ToArray();

	internal static string PathOf(Instance item)
	{
		var names = new List<string>();
		for (Instance? current = item; current != null && current is not World; current = current.Parent) names.Add(current.Name);
		names.Reverse();
		return "/" + string.Join("/", names);
	}

	internal static JsonObject Reference(Instance item) => new() { ["id"] = item.ObjectID, ["path"] = PathOf(item), ["class_name"] = item.ClassName };

	internal static JsonNode? Encode(object? value) => value switch
	{
		null => null,
		string s => JsonValue.Create(s),
		bool b => JsonValue.Create(b),
		int n => JsonValue.Create(n), uint n => JsonValue.Create(n), long n => JsonValue.Create(n),
		float n => float.IsFinite(n) ? JsonValue.Create(n) : JsonValue.Create(n.ToString(CultureInfo.InvariantCulture)),
		double n => double.IsFinite(n) ? JsonValue.Create(n) : JsonValue.Create(n.ToString(CultureInfo.InvariantCulture)),
		Vector3 v => new JsonArray(v.X, v.Y, v.Z),
		Vector2 v => new JsonArray(v.X, v.Y),
		Quaternion q => new JsonArray(q.X, q.Y, q.Z, q.W),
		Color c => JsonValue.Create("#" + c.ToHtml(c.A < 1)),
		Enum e => JsonValue.Create(e.ToString()),
		NumberRange r => new JsonArray(r.Min, r.Max),
		NumberSeries s => new JsonArray((s.Points ?? []).Select(p => (JsonNode)new JsonArray(p.Offset, p.Value)).ToArray()),
		ColorSeries s => new JsonArray((s.Points ?? []).Select(p => (JsonNode)new JsonArray(p.Offset, "#" + p.Color.ToHtml(p.Color.A < 1))).ToArray()),
		Variant v => EncodeVariant(v),
		Instance i => i.IsDeleted ? null : Reference(i),
		FileLinkAsset link => new JsonObject { ["class_name"] = link.ClassName, ["path"] = link.LinkedPath },
		BaseAsset asset => EncodeAsset(asset),
		Array array => new JsonArray(array.Cast<object?>().Select(Encode).ToArray()),
		IData data when data.GetType().IsValueType => EncodeData(data),
		_ => JsonValue.Create(value.ToString())
	};

	private static JsonNode? EncodeVariant(Variant value) => value.VariantType switch
	{
		Variant.Type.Nil => null,
		Variant.Type.Bool => JsonValue.Create(value.AsBool()),
		Variant.Type.Int => JsonValue.Create(value.AsInt64()),
		Variant.Type.Float => JsonValue.Create(value.AsDouble()),
		Variant.Type.String => JsonValue.Create(value.AsString()),
		Variant.Type.Vector2 => Encode(value.AsVector2()),
		Variant.Type.Vector3 => Encode(value.AsVector3()),
		Variant.Type.Quaternion => Encode(value.AsQuaternion()),
		Variant.Type.Color => Encode(value.AsColor()),
		_ => JsonValue.Create(value.ToString())
	};

	private static JsonObject EncodeAsset(BaseAsset asset)
	{
		var properties = new JsonObject();
		foreach (var property in Editable(asset.GetType())) properties[property.Name] = Encode(property.GetValue(asset));
		return new JsonObject { ["class_name"] = asset.ClassName, ["properties"] = properties };
	}

	private static JsonObject EncodeData(object data)
	{
		var result = new JsonObject();
		foreach (var property in DataProperties(data.GetType())) result[property.Name] = Encode(property.GetValue(data));
		return result;
	}

	/// <summary>Converts JSON into a property value. <paramref name="current"/> lets partial struct objects keep unspecified fields.</summary>
	internal static object? Decode(JsonNode? node, Type type, Context context, object? current = null)
	{
		type = Nullable.GetUnderlyingType(type) ?? type;
		if (node == null)
		{
			if (typeof(Instance).IsAssignableFrom(type) || typeof(BaseAsset).IsAssignableFrom(type)) return null;
			if (type == typeof(Variant)) return PTVariant.ToGodot(null);
			throw new ArgumentException("null is only allowed for instance and asset references.");
		}
		if (type == typeof(string)) return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
		if (type == typeof(bool)) return Bool(node);
		if (type == typeof(float)) return (float)Number(node);
		if (type == typeof(double)) return Number(node);
		if (type == typeof(int)) return checked((int)Integer(node));
		if (type == typeof(uint)) return checked((uint)Integer(node));
		if (type == typeof(long)) return Integer(node);
		if (type == typeof(Vector3)) return Vector3Value(node);
		if (type == typeof(Vector2)) { var n = Numbers(node, 2, "[x,y]", "x", "y"); return new Vector2(n[0], n[1]); }
		if (type == typeof(Quaternion)) { var n = Numbers(node, 4, "[x,y,z,w]", "x", "y", "z", "w"); return new Quaternion(n[0], n[1], n[2], n[3]).Normalized(); }
		if (type == typeof(Color)) return ColorValue(node);
		if (type.IsEnum) return EnumValue(node, type);
		if (type == typeof(NumberRange)) { var n = Numbers(node, 2, "[min,max]", "min", "max"); return new NumberRange(Math.Min(n[0], n[1]), Math.Max(n[0], n[1])); }
		if (type == typeof(NumberSeries)) return NumberSeriesValue(node);
		if (type == typeof(ColorSeries)) return ColorSeriesValue(node);
		if (type == typeof(Variant)) return VariantValue(node);
		if (typeof(Instance).IsAssignableFrom(type)) return InstanceValue(node, type, context);
		if (typeof(BaseAsset).IsAssignableFrom(type)) return AssetValue(node, type, context, current as BaseAsset);
		if (type.IsArray)
		{
			var element = type.GetElementType()!;
			var items = node as JsonArray ?? throw new ArgumentException("Expected a JSON array.");
			var array = Array.CreateInstance(element, items.Count);
			for (int i = 0; i < items.Count; i++) array.SetValue(Decode(items[i], element, context), i);
			return array;
		}
		if (IsDataStruct(type)) return DataValue(node, type, context, current);
		throw new ArgumentException("Unsupported value type " + type.Name);
	}

	private static bool Bool(JsonNode node) => node.GetValueKind() switch
	{
		JsonValueKind.True => true,
		JsonValueKind.False => false,
		JsonValueKind.String when bool.TryParse(node.GetValue<string>(), out bool b) => b,
		_ => throw new ArgumentException("Expected true or false.")
	};

	internal static double Number(JsonNode node)
	{
		double value = node.GetValueKind() switch
		{
			// Parsed JSON converts directly; values built in code (int, float...) go through their JSON text.
			JsonValueKind.Number => node.AsValue().TryGetValue(out double direct) ? direct : double.Parse(node.ToJsonString(), CultureInfo.InvariantCulture),
			JsonValueKind.String when double.TryParse(node.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
			_ => throw new ArgumentException("Expected a number.")
		};
		if (!double.IsFinite(value) || Math.Abs(value) > 1e9) throw new ArgumentException("Numbers must be finite and within ±1e9.");
		return value;
	}

	private static long Integer(JsonNode node)
	{
		double value = Number(node);
		if (value != Math.Truncate(value)) throw new ArgumentException("Expected a whole number.");
		return (long)value;
	}

	/// <summary>Accepts [x,y,z] or {x,y,z}/{X,Y,Z}.</summary>
	internal static Vector3 Vector3Value(JsonNode node)
	{
		var n = Numbers(node, 3, "[x,y,z]", "x", "y", "z");
		return new Vector3(n[0], n[1], n[2]);
	}

	private static float[] Numbers(JsonNode node, int count, string format, params string[] names)
	{
		if (node is JsonArray array && array.Count == count) return array.Select(n => (float)Number(n ?? throw new ArgumentException($"Expected {format}, got null inside the array."))).ToArray();
		if (node is JsonObject obj)
		{
			var values = new float[count];
			for (int i = 0; i < count; i++)
			{
				var entry = obj.FirstOrDefault(p => string.Equals(p.Key, names[i], StringComparison.OrdinalIgnoreCase)).Value
					?? throw new ArgumentException($"Expected {format} (missing '{names[i]}').");
				values[i] = (float)Number(entry);
			}
			return values;
		}
		throw new ArgumentException($"Expected {format}, got {Shape(node)}.");
	}

	/// <summary>Describes a JSON value for error messages, so agents can see what they sent.</summary>
	internal static string Shape(JsonNode? node) => node switch
	{
		null => "null",
		JsonArray array => $"an array of {array.Count} value{(array.Count == 1 ? "" : "s")}",
		JsonObject obj => obj.Count == 0 ? "an empty object" : $"an object with keys {string.Join(", ", obj.Select(p => p.Key).Take(6))}",
		_ => node.GetValueKind() switch
		{
			JsonValueKind.String => "a string",
			JsonValueKind.Number => "a number",
			JsonValueKind.True or JsonValueKind.False => "a boolean",
			_ => "an unsupported value"
		}
	};

	private static Color ColorValue(JsonNode node)
	{
		if (node.GetValueKind() == JsonValueKind.String)
		{
			string text = node.GetValue<string>().Trim();
			if (Color.HtmlIsValid(text)) return Color.FromHtml(text);
			var named = Color.FromString(text, new Color(0, 0, 0, -1));
			if (named.A >= 0) return named;
		}
		else if (node is JsonArray { Count: 3 or 4 } array)
		{
			float[] c = array.Select(n => (float)Number(n!)).ToArray();
			float scale = c.Take(3).Any(v => v > 1) ? 255f : 1f;
			return new Color(c[0] / scale, c[1] / scale, c[2] / scale, c.Length == 4 ? (c[3] > 1 ? c[3] / 255f : c[3]) : 1);
		}
		throw new ArgumentException("Expected a hex color such as \"#3399ff\" (or \"#3399ff80\" with alpha).");
	}

	private static object EnumValue(JsonNode node, Type type)
	{
		if (node.GetValueKind() == JsonValueKind.String)
		{
			string text = node.GetValue<string>();
			string? name = Enum.GetNames(type).FirstOrDefault(n => n == text) ?? Enum.GetNames(type).FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
			if (name != null) return Enum.Parse(type, name);
		}
		else if (node.GetValueKind() == JsonValueKind.Number)
		{
			object value = Enum.ToObject(type, Integer(node));
			if (Enum.IsDefined(type, value)) return value;
		}
		throw new ArgumentException("Expected one of " + string.Join(", ", Enum.GetNames(type)) + ".");
	}

	private static IEnumerable<(float Time, JsonNode Value)> SeriesPoints(JsonNode node, string valueName)
	{
		if (node is not JsonArray array || array.Count == 0) throw new ArgumentException($"Expected [[time,{valueName}],...] with times from 0 to 1.");
		foreach (var point in array)
		{
			if (point is JsonArray { Count: 2 } pair) yield return ((float)Number(pair[0]!), pair[1]!);
			else if (point is JsonObject obj)
			{
				var time = obj["time"] ?? obj["offset"] ?? obj["Time"] ?? obj["Offset"] ?? throw new ArgumentException("Series points need a time.");
				var value = obj[valueName] ?? obj[char.ToUpperInvariant(valueName[0]) + valueName[1..]] ?? throw new ArgumentException($"Series points need a {valueName}.");
				yield return ((float)Number(time), value);
			}
			else throw new ArgumentException($"Expected [[time,{valueName}],...].");
		}
	}

	private static NumberSeries NumberSeriesValue(JsonNode node)
	{
		var series = new NumberSeries();
		series.Clear();
		foreach (var (time, value) in SeriesPoints(node, "value").OrderBy(p => p.Time)) series.AddPoint(Math.Clamp(time, 0, 1), (float)Number(value));
		return series;
	}

	private static ColorSeries ColorSeriesValue(JsonNode node)
	{
		var series = new ColorSeries();
		series.Clear();
		foreach (var (time, value) in SeriesPoints(node, "color").OrderBy(p => p.Time)) series.AddPoint(Math.Clamp(time, 0, 1), ColorValue(value));
		return series;
	}

	private static Variant VariantValue(JsonNode node) => node.GetValueKind() switch
	{
		JsonValueKind.String => PTVariant.ToGodot(node.GetValue<string>()),
		JsonValueKind.True => PTVariant.ToGodot(true),
		JsonValueKind.False => PTVariant.ToGodot(false),
		JsonValueKind.Number => NumberVariant(Number(node)),
		JsonValueKind.Array when node.AsArray().Count == 2 => PTVariant.ToGodot(Decode(node, typeof(Vector2), null!)),
		JsonValueKind.Array when node.AsArray().Count == 3 => PTVariant.ToGodot(Vector3Value(node)),
		_ => throw new ArgumentException("Variant values may be strings, numbers, booleans, [x,y], [x,y,z] or null.")
	};

	private static Variant NumberVariant(double value) => value == Math.Truncate(value) && Math.Abs(value) < int.MaxValue
		? PTVariant.ToGodot((int)value) : PTVariant.ToGodot((float)value);

	private static Instance InstanceValue(JsonNode node, Type type, Context context)
	{
		string id = node switch
		{
			JsonObject obj => (obj["id"] ?? obj["path"])?.GetValue<string>() ?? throw new ArgumentException("Reference objects need an id."),
			_ when node.GetValueKind() == JsonValueKind.String => node.GetValue<string>(),
			_ => throw new ArgumentException("Expected an instance ID or path string, or null.")
		};
		var item = context.Resolve(id);
		if (!type.IsInstanceOfType(item)) throw new ArgumentException($"{item.Name} is a {item.ClassName}; this property needs a {type.Name}.");
		return item;
	}

	/// <summary>Instantiable asset classes that can be assigned to a property of this type.</summary>
	internal static Type[] AssetClasses(Type type) => BaseAsset.GetAllDerivedTypesOf(type)
		.Append(type).Distinct().Where(t => !t.IsAbstract && t.IsDefined(typeof(InstantiableAttribute), false)).OrderBy(t => t.Name).ToArray();

	private static BaseAsset? AssetValue(JsonNode node, Type type, Context context, BaseAsset? current)
	{
		if (node.GetValueKind() == JsonValueKind.Number)
		{
			// Shorthand for an uploaded Polytoria asset ID.
			(string ClassName, string Property)? shorthand = type switch
			{
				_ when type.IsAssignableFrom(typeof(PTImageAsset)) => ("PTImageAsset", "ImageID"),
				_ when type.IsAssignableFrom(typeof(PTMeshAsset)) => ("PTMeshAsset", "AssetID"),
				_ when type.IsAssignableFrom(typeof(PTAudioAsset)) => ("PTAudioAsset", "AudioID"),
				_ => null
			};
			if (shorthand == null) throw new ArgumentException($"{type.Name} has no numeric asset-ID shorthand; use {{\"class_name\":...,\"properties\":{{...}}}}.");
			node = new JsonObject { ["class_name"] = shorthand.Value.ClassName, ["properties"] = new JsonObject { [shorthand.Value.Property] = node.DeepClone() } };
		}
		if (node.GetValueKind() == JsonValueKind.String)
		{
			string text = node.GetValue<string>();
			if (type.IsAssignableFrom(typeof(FileLinkAsset))) return FileLink(text, context);
			node = new JsonObject { ["class_name"] = text };
		}
		if (node is not JsonObject obj) throw new ArgumentException("Expected an asset object {\"class_name\":...,\"properties\":{...}}, an asset ID number, or null.");
		var classes = AssetClasses(type);
		string? className = obj["class_name"]?.GetValue<string>() ?? obj["ClassName"]?.GetValue<string>()
			?? (current != null && classes.Contains(current.GetType()) ? current.ClassName : classes.Length == 1 ? classes[0].Name : null);
		if (className == null) throw new ArgumentException($"Choose a class_name for {type.Name}: {string.Join(", ", classes.Select(c => c.Name))}.");
		var assetType = classes.FirstOrDefault(c => c.Name == className)
			?? throw new ArgumentException($"{className} cannot be assigned to {type.Name}. Use one of: {string.Join(", ", classes.Select(c => c.Name))}.");
		if (assetType == typeof(FileLinkAsset)) return FileLink(obj["path"]?.GetValue<string>() ?? throw new ArgumentException("File links need a project-relative path."), context);
		var asset = Globals.LoadNetworkedObject(assetType.Name, context.World) as BaseAsset ?? throw new ArgumentException("Could not create " + assetType.Name);
		try
		{
			var properties = Editable(assetType).ToDictionary(p => p.Name, StringComparer.Ordinal);
			// Keep unspecified fields when editing the current asset of the same class.
			if (current != null && current.GetType() == assetType)
				foreach (var property in properties.Values) property.SetValue(asset, property.GetValue(current));
			var values = obj["properties"] as JsonObject ?? new JsonObject(obj.Where(p => p.Key is not ("class_name" or "ClassName")).Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
			foreach (var (key, value) in values)
			{
				if (!properties.TryGetValue(key, out var property))
					throw new ArgumentException($"{assetType.Name} has no editable property {key}. Properties: {string.Join(", ", properties.Keys)}.");
				property.SetValue(asset, Decode(value, property.PropertyType, context, property.GetValue(asset)));
			}
			return asset;
		}
		catch { asset.Delete(); throw; }
	}

	private static FileLinkAsset FileLink(string path, Context context)
	{
		string relative = path.Replace('\\', '/').TrimStart('/');
		if (relative == "" || relative.Split('/').Any(p => p is "" or "." or "..") || relative.Contains(':'))
			throw new ArgumentException("File links need a project-relative path inside the project.");
		if (context.World.LinkedSession is not { } session || !System.IO.File.Exists(session.GlobalizePath(relative)))
			throw new ArgumentException("No project file at " + relative);
		return context.World.Assets.GetFileLinkByPath(relative);
	}

	private static object DataValue(JsonNode node, Type type, Context context, object? current)
	{
		var properties = DataProperties(type);
		object result = current != null && current.GetType() == type ? current : Activator.CreateInstance(type)!;
		if (node is JsonArray array && array.Count == properties.Length)
		{
			for (int i = 0; i < properties.Length; i++) properties[i].SetValue(result, Decode(array[i], properties[i].PropertyType, context));
			return result;
		}
		if (node is not JsonObject obj) throw new ArgumentException($"Expected an object with {string.Join(", ", properties.Select(p => p.Name))}.");
		foreach (var (key, value) in obj)
		{
			var property = properties.FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
				?? throw new ArgumentException($"{type.Name} has no field {key}. Fields: {string.Join(", ", properties.Select(p => p.Name))}.");
			property.SetValue(result, Decode(value, property.PropertyType, context, property.GetValue(result)));
		}
		return result;
	}

	/// <summary>Short description of the accepted JSON for a property type, used by class_schema and errors.</summary>
	internal static string Format(Type type)
	{
		type = Nullable.GetUnderlyingType(type) ?? type;
		if (type == typeof(Vector3)) return "[x,y,z]";
		if (type == typeof(Vector2)) return "[x,y]";
		if (type == typeof(Quaternion)) return "[x,y,z,w]";
		if (type == typeof(Color)) return "\"#RRGGBB\" or \"#RRGGBBAA\"";
		if (type == typeof(NumberRange)) return "[min,max]";
		if (type == typeof(NumberSeries)) return "[[time,value],...], time 0..1";
		if (type == typeof(ColorSeries)) return "[[time,\"#RRGGBB\"],...], time 0..1";
		if (type == typeof(Variant)) return "string, number, boolean, [x,y], [x,y,z] or null";
		if (type == typeof(string)) return "string";
		if (type == typeof(bool)) return "boolean";
		if (type == typeof(int) || type == typeof(uint) || type == typeof(long)) return "integer";
		if (type == typeof(float) || type == typeof(double)) return "number";
		if (type.IsEnum) return "enum string";
		if (typeof(Instance).IsAssignableFrom(type)) return $"{type.Name} instance ID or path, or null";
		if (typeof(BaseAsset).IsAssignableFrom(type)) return "{\"class_name\":...,\"properties\":{...}} or null" + (type.IsAssignableFrom(typeof(PTImageAsset)) || type.IsAssignableFrom(typeof(PTMeshAsset)) || type.IsAssignableFrom(typeof(PTAudioAsset)) ? "; a number is shorthand for a Polytoria asset ID" : "");
		if (type.IsArray) return "[" + Format(type.GetElementType()!) + ", ...]";
		if (IsDataStruct(type)) return "{" + string.Join(",", DataProperties(type).Select(p => $"\"{p.Name}\":{Format(p.PropertyType)}")) + "}";
		return type.Name;
	}

	/// <summary>Schema entry for one property: type, JSON format and the choices that are valid for it.</summary>
	internal static JsonObject Describe(PropertyInfo property)
	{
		var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
		var info = new JsonObject { ["type"] = type.Name, ["format"] = Format(type) };
		if (type.IsEnum) info["values"] = new JsonArray(Enum.GetNames(type).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
		if (typeof(BaseAsset).IsAssignableFrom(type))
		{
			var classes = new JsonObject();
			foreach (var asset in AssetClasses(type))
			{
				var fields = new JsonObject();
				foreach (var field in Editable(asset)) fields[field.Name] = field.PropertyType.IsEnum
					? string.Join("|", Enum.GetNames(field.PropertyType)) : Format(field.PropertyType);
				classes[asset.Name] = fields;
			}
			info["asset_classes"] = classes;
		}
		return info;
	}
}
