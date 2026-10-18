// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Datamodel;
using Polytoria.Datamodel.Resources;
using Polytoria.Formats;
using Polytoria.Schemas.API;
using Polytoria.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using File = System.IO.File;
using HttpClient = System.Net.Http.HttpClient;
using Script = Polytoria.Datamodel.Script;

namespace Polytoria.Creator.Gark;

/// <summary>The public Toolbox endpoints; never receives AI-provider credentials.</summary>
internal sealed class GarkAssetLibrary(HttpClient? client = null)
{
	internal static GarkAssetLibrary Shared { get; } = new();
	private readonly HttpClient _client = Configure(client ?? new());
	private readonly Dictionary<(string Type, uint Id), APILibraryItem> _results = [];
	private static HttpClient Configure(HttpClient client)
	{
		client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"Polytoria Client {Globals.AppVersion}");
		client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
		return client;
	}
	internal static string AssetType(string type) => type is "model" or "mesh" or "image" or "audio" ? type : throw new ArgumentException("Use model, mesh, image or audio.");
	internal async Task<APILibraryResponse> Search(string type, string query, int page, CancellationToken token)
	{
		string apiType = AssetType(type) == "image" ? "decal" : type;
		using var response = await _client.GetAsync(Globals.MainEndpoint.TrimEnd('/') + $"/api/library?page={page}&search={Uri.EscapeDataString(query)}&type={apiType}", token);
		response.EnsureSuccessStatusCode();
		var result = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(token), APIGenerationContext.Default.APILibraryResponse);
		foreach (var item in result.Data ?? []) _results[(type, item.ID)] = item;
		return result;
	}
	internal APILibraryItem? Find(string type, uint id) => _results.TryGetValue((AssetType(type), id), out var item) ? item : null;
	internal Task<byte[]> Model(uint id, CancellationToken token) => _client.GetByteArrayAsync(Globals.ApiEndpoint.TrimEnd('/') + "/v1/models/get-model?id=" + id, token);
	internal Task<byte[]> Thumbnail(string url, CancellationToken token)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort
			|| !(uri.Host == "polytoria.com" || uri.Host.EndsWith(".polytoria.com", StringComparison.OrdinalIgnoreCase)))
			throw new ArgumentException("No supported Polytoria thumbnail URL is available for this asset.");
		return _client.GetByteArrayAsync(uri, token);
	}
}

internal sealed partial class GarkTools
{
	internal GarkAssetLibrary AssetLibrary { get; init; } = GarkAssetLibrary.Shared;
	private static JsonObject AssetJson(APILibraryItem item, string type) => new() {
		["id"] = item.ID, ["type"] = type, ["name"] = item.Name,
		["creator"] = item.CreatorName, ["creator_id"] = item.CreatorID, ["thumbnail_url"] = item.ThumbnailUrl
	};
	private (string Type, uint Id, APILibraryItem? Item) Asset(JsonObject args)
	{
		string type = GarkAssetLibrary.AssetType(Text(args, "type"));
		double raw = GarkValues.Number(args["id"]!);
		if (raw < 1 || raw > uint.MaxValue || raw != Math.Truncate(raw)) throw new ArgumentException("id must be a positive asset ID.");
		uint id = (uint)raw;
		return (type, id, AssetLibrary.Find(type, id));
	}
	private async Task<JsonObject> SearchAssets(JsonObject args, CancellationToken token)
	{
		string type = GarkAssetLibrary.AssetType(Text(args, "type"));
		int page = args["page"]?.GetValue<int>() ?? 1;
		if (page < 1) throw new ArgumentException("page starts at 1.");
		var response = await AssetLibrary.Search(type, Text(args, "query"), page, token);
		CheckContext(token);
		return new() { ["items"] = new JsonArray((response.Data ?? []).Select(i => (JsonNode)AssetJson(i, type)).ToArray()),
			["page"] = page, ["last_page"] = response.Meta.LastPage, ["total"] = response.Meta.Total,
			["has_more"] = page < response.Meta.LastPage };
	}
	private async Task<GarkToolResult> InspectAsset(JsonObject args, CancellationToken token)
	{
		var (type, id, found) = Asset(args);
		if (found is not { } item) throw new ArgumentException($"Asset {id} is not in your recent search_assets results for type {type}; search for it first so its metadata and thumbnail are known.");
		var result = AssetJson(item, type);
		if (!Flag(args, "preview", true)) return new(result);
		try
		{
			byte[] bytes = await AssetLibrary.Thumbnail(item.ThumbnailUrl, token);
			CheckContext(token);
			using var image = new Image();
			if (image.LoadPngFromBuffer(bytes) != Error.Ok && image.LoadJpgFromBuffer(bytes) != Error.Ok && image.LoadWebpFromBuffer(bytes) != Error.Ok)
				throw new InvalidDataException("The Toolbox thumbnail could not be decoded.");
			if (Math.Max(image.GetWidth(), image.GetHeight()) > 1024)
			{
				float scale = 1024f / Math.Max(image.GetWidth(), image.GetHeight());
				image.Resize(Math.Max(1, (int)(image.GetWidth() * scale)), Math.Max(1, (int)(image.GetHeight() * scale)));
			}
			return new(result, image.SavePngToBuffer());
		}
		catch (Exception ex) when (ex is HttpRequestException or ArgumentException or InvalidDataException)
		{
			result["preview_error"] = ex.Message;
			return new(result);
		}
	}
	private async Task<JsonObject> ImportAsset(JsonObject args, CancellationToken token)
	{
		var (type, assetId, asset) = Asset(args);
		string assetName = asset?.Name is { Length: > 0 } known ? known : $"{char.ToUpperInvariant(type[0])}{type[1..]} {assetId}";
		var parent = Resolve(Text(args, "parent", "/Environment"));
		byte[]? model = type == "model" ? await AssetLibrary.Model(assetId, token) : null;
		CheckContext(token);
		if (parent.IsDeleted || !Visible(parent)) throw new InvalidOperationException("The target parent is no longer available.");
		using var edit = new SceneEdit(this);
		Instance? item = null;
		Instance? staging = null;
		string? importFolder = null;
		try
		{
			if (model != null)
			{
				importFolder = Globals.ToolboxFolderName + "/Gark-" + assetId + "-" + Guid.NewGuid().ToString("N");
				ValidateImportFolder(importFolder);
				staging = world!.New<Folder>();
				staging.Parent = world.TemporaryContainer;
				if (DatamodelLoader.DetermineFileTypeFromBytes(model) == PolyFileTypeEnum.Packed)
				{
					var data = PackedFormat.ReadModelData(model) ?? throw new InvalidDataException("The Toolbox returned an invalid model.");
					data.ModelName = "model";
					ValidatePackedModel(data.RootData);
					RemapImportedObjectIds(data.RootData);
					// The existing loader writes linked resources; validate their destinations before it runs.
					if (data.FileContents?.TryGetValue("index.json", out var index) == true)
					{
						var paths = JsonNode.Parse(index)!.AsObject();
						foreach (var path in paths) ValidateRelativeAssetPath(path.Value!.GetValue<string>());
					}
					item = PackedFormat.LoadPackedModelData(world!, data, staging, importFolder);
				}
				else
				{
					var legacy = XmlFormat.ParseContent(Encoding.UTF8.GetString(model).TrimStart('\uFEFF'));
					ValidateLegacyModel(legacy);
					item = await XmlFormat.LoadModelItem(world!, legacy, staging);
					if (item != null)
					{
						int index = 0;
						foreach (var script in new[] { item }.Concat(item.GetDescendants()).OfType<Script>())
						{
							string suffix = script is ServerScript ? ".server.luau" : script is ClientScript ? ".client.luau" : ".module.luau";
							string path = importFolder + "/script-" + ++index + suffix;
							world!.IO.WriteBytesToPath(path, Encoding.UTF8.GetBytes(script.Source));
							script.LinkedScript = world.Assets.GetFileLinkByPath(path);
						}
					}
				}
				if (item == null) throw new InvalidDataException("The Toolbox model contains no importable objects.");
			}
			else item = Globals.LoadInstance<Instance>(type switch { "mesh" => "Mesh", "image" => "Image3D", "audio" => "Sound", _ => throw new ArgumentException("Unsupported asset.") }, world)!;
			edit.Track(item);
			CheckContext(token);
			edit.Change(() => item.Parent = parent, () => { world!.CreatorContext.Selections.Deselect(item); item.Parent = world.TemporaryContainer; });
			item.Name = Text(args, "name", assetName);
			if (item is Datamodel.Mesh mesh && type == "mesh")
			{
				var reference = world!.New<PTMeshAsset>(); reference.AssetID = assetId; mesh.Asset = reference;
			}
			if (item is Image3D image && type == "image")
			{
				var reference = world!.New<PTImageAsset>(); reference.ImageID = assetId; image.Image = reference;
			}
			if (item is Sound sound && type == "audio")
			{
				var reference = world!.New<PTAudioAsset>(); reference.AudioID = assetId; sound.Audio = reference;
			}
			foreach (var change in ConvertProperties(item, args["properties"] as JsonObject ?? new())) change.Property.SetValue(item, change.Value);
			if (importFolder != null)
			{
				string file = session!.GlobalizePath(importFolder + "/asset.model");
				Directory.CreateDirectory(Path.GetDirectoryName(file)!);
				PolyFormat.SaveModelToFile(item, file);
				session.RescanFolder();
			}
			CheckContext(token);
			edit.Finish("Gark: import " + item.Name);
			var result = Summary(item);
			result["asset"] = asset is { } metadata ? AssetJson(metadata, type) : new JsonObject { ["id"] = assetId, ["type"] = type };
			result["import_folder"] = importFolder;
			result["script_count"] = new[] { item }.Concat(item.GetDescendants()).OfType<Script>().Count();
			return result;
		}
		catch
		{
			// Only the unique directory created by this call is eligible for cleanup.
			if (importFolder != null)
			{
				string path = ValidateImportFolder(importFolder);
				if (Directory.Exists(path)) Directory.Delete(path, true);
				session!.RescanFolder();
			}
			throw;
		}
		finally { if (staging != null && !staging.IsDeleted) staging.Delete(); }
	}
	private static void ValidateLegacyModel(XmlFormat.GameItem item)
	{
		// These legacy classes reuse active world services instead of creating a new object.
		if (item.Class == null || !Classes.Contains(item.Class) || item.Class == typeof(Camera)
			|| item.Class == typeof(SunLight) || item.Class == typeof(Inventory))
			throw new InvalidDataException("This legacy model contains an unsupported or world-service class: " + item.Class?.Name);
		foreach (var child in item.Children) ValidateLegacyModel(child);
	}
	private static void ValidatePackedModel(PolyFormat.PolyRootData data)
	{
		if (data.FileType != PolyFormat.PolyFileType.Model || data.Objects is not { Length: 1 })
			throw new InvalidDataException("The Toolbox file must contain one model root.");
		void Visit(PolyFormat.PolyObject item)
		{
			string name = PolyFormat.ConvertClassName(item.ClassName);
			if (!Classes.Any(t => t.Name == name)) throw new InvalidDataException("Model contains an unsupported scene class: " + name);
			foreach (var child in item.Children) Visit(child);
		}
		Visit(data.Objects[0]);
	}
	private static void RemapImportedObjectIds(PolyFormat.PolyRootData data)
	{
		// A library model can be inserted repeatedly. Its file's object IDs must not
		// replace existing entries in the world's lookup table. This transforms only
		// imported data, using the existing ID/reference fields and serialization.
		var objects = new List<PolyFormat.PolyObject>();
		void Visit(PolyFormat.PolyObject item) { objects.Add(item); foreach (var child in item.Children) Visit(child); }
		foreach (var item in data.Objects.Concat(data.NonInstanceObjects)) Visit(item);
		var ids = new Dictionary<string, string>();
		foreach (var item in objects)
			if (string.IsNullOrEmpty(item.ID) || !ids.TryAdd(item.ID, Guid.NewGuid().ToString()))
				throw new InvalidDataException("The model contains missing or duplicate object IDs.");
		var types = typeof(NetworkedObject).Assembly.GetTypes().Where(t => typeof(NetworkedObject).IsAssignableFrom(t)).ToArray();
		foreach (var item in objects)
		{
			item.ID = ids[item.ID];
			var type = types.FirstOrDefault(t => t.Name == PolyFormat.ConvertClassName(item.ClassName));
			foreach (var key in item.Properties.Keys.ToArray())
			{
				var propertyType = type?.GetProperty(key)?.PropertyType;
				if (propertyType == null || !typeof(NetworkedObject).IsAssignableFrom(propertyType) || typeof(FileLinkAsset).IsAssignableFrom(propertyType)) continue;
				if (PolyFormat.DeserializePropValue(item.Properties[key], typeof(string)) is string reference && ids.TryGetValue(reference, out var replacement))
					item.Properties[key] = replacement;
			}
		}
	}
	private string ValidateImportFolder(string relative)
	{
		ValidateRelativeAssetPath(relative);
		string root = Path.GetFullPath(session!.ProjectFolderPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		string target = Path.GetFullPath(Path.Combine(root, relative));
		if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Import destination must stay in the project.");
		for (string? at = target; at != null && at.StartsWith(root, StringComparison.OrdinalIgnoreCase); at = Path.GetDirectoryName(at))
			if (Path.Exists(at) && (File.GetAttributes(at) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Import cannot traverse symbolic links.");
		return target;
	}
	private static void ValidateRelativeAssetPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':')
			|| path.Replace('\\', '/').Split('/').Any(p => p is "" or "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
			throw new InvalidDataException("Model contains an invalid linked-asset path.");
	}
}
