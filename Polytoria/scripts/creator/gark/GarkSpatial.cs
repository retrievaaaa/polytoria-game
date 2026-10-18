// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Datamodel;
using Polytoria.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Polytoria.Creator.Gark;

internal sealed partial class GarkTools
{
	private async Task PhysicsReady(CancellationToken token)
	{
		// Deferred collider changes need a physics tick before querying the space.
		var tree = Globals.Singleton.GetTree();
		await Globals.Singleton.ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
		await Globals.Singleton.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
		CheckContext(token);
	}
	private Aabb Bounds(Instance item)
	{
		if (item is Dynamic dynamic) return dynamic.GetBounds();
		var geometry = item.GetDescendants().OfType<Dynamic>().Where(Visible).ToArray();
		if (geometry.Length == 0) throw new ArgumentException(item.Name + " has no spatial bounds.");
		return geometry.Select(d => d.GetBounds()).Aggregate((a, b) => a.Merge(b));
	}
	private Aabb? TryBounds(Instance item)
	{
		try { return Bounds(item); }
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
	}
	private static JsonObject BoundsJson(Aabb bounds) => new() {
		["min"] = GarkValues.Encode(bounds.Position), ["max"] = GarkValues.Encode(bounds.End),
		["center"] = GarkValues.Encode(bounds.GetCenter()), ["size"] = GarkValues.Encode(bounds.Size)
	};
	private JsonObject GetBounds(JsonObject a)
	{
		var bounds = IdList(a).Select(Resolve).Select(i => (Item: i, Box: Bounds(i))).ToArray();
		return new() { ["items"] = new JsonArray(bounds.Select(b => (JsonNode)new JsonObject { ["id"] = b.Item.ObjectID, ["bounds"] = BoundsJson(b.Box) }).ToArray()),
			["combined"] = BoundsJson(bounds.Select(b => b.Box).Aggregate((b, next) => b.Merge(next))) };
	}
	private JsonObject MeasureDistance(JsonObject a)
	{
		var from = Bounds(Resolve(Text(a, "from")));
		var to = Bounds(Resolve(Text(a, "to")));
		Vector3 gap = (from.Position - to.End).Max(to.Position - from.End).Max(Vector3.Zero);
		return new() { ["center_distance"] = from.GetCenter().DistanceTo(to.GetCenter()), ["bounds_gap"] = gap.Length(),
			["center_delta"] = GarkValues.Encode(to.GetCenter() - from.GetCenter()), ["gap_by_axis"] = GarkValues.Encode(gap) };
	}
	private Instance[] Ignored(JsonObject a) => Strings(a, "ignore_ids").Select(Resolve).ToArray();
	private static bool IsIgnored(Instance item, Instance[] ignore) => ignore.Any(i => item == i || item.IsDescendantOf(i));
	private JsonObject OverlapBox(JsonObject a)
	{
		Vector3 center = GarkValues.Vector3Value(a["center"]!), size = GarkValues.Vector3Value(a["size"]!);
		if (size.X <= 0 || size.Y <= 0 || size.Z <= 0) throw new ArgumentException("Box size must be positive.");
		Aabb box = new(center - size / 2, size);
		var ignore = Ignored(a);
		var matches = new List<JsonNode>();
		foreach (var item in world!.Environment.GetDescendants().OfType<Dynamic>())
		{
			if (!Visible(item) || item.IsHidden || IsIgnored(item, ignore)) continue;
			Aabb bounds = item.GetSelfBound();
			if (!bounds.HasVolume() || !box.Intersects(bounds)) continue;
			var data = Summary(item); data["bounds"] = BoundsJson(bounds); matches.Add(data);
		}
		int offset = Int(a, "offset", 0, 0, int.MaxValue), limit = Int(a, "limit", 100, 1, 5000);
		return new() { ["test"] = "world_axis_aligned_bounds", ["total"] = matches.Count,
			["items"] = new JsonArray(matches.Skip(offset).Take(limit).ToArray()), ["has_more"] = offset + limit < matches.Count };
	}
	private sealed record SpatialHit(Instance Item, Vector3 Position, Vector3 Normal, float Distance);
	private SpatialHit? Cast(Vector3 origin, Vector3 direction, float distance, Instance[] ignore, CancellationToken token)
	{
		if (direction.IsZeroApprox()) throw new ArgumentException("Ray direction cannot be zero.");
		if (distance <= 0) throw new ArgumentException("max_distance must be positive.");
		using var query = PhysicsRayQueryParameters3D.Create(origin, origin + direction.Normalized() * distance);
		query.CollideWithAreas = true;
		query.CollideWithBodies = true;
		query.HitFromInside = true;
		var exclude = new Godot.Collections.Array<Rid>();
		while (true)
		{
			token.ThrowIfCancellationRequested();
			query.Exclude = exclude;
			var hit = world!.World3D.DirectSpaceState.IntersectRay(query);
			if (hit.Count == 0) return null;
			Node collider = (Node)hit["collider"];
			Instance? item = Physical.GetPhysicalFromCollider(collider)
				?? (collider is CollisionObject3D body ? Physical.GetPhysicalFromBodyShape(body) : null)
				?? Instance.GetNetObjFromProxy(collider) as Instance;
			if (item != null && Visible(item) && item is Dynamic { IsHidden: false } && !IsIgnored(item, ignore))
			{
				Vector3 point = (Vector3)hit["position"];
				return new(item, point, (Vector3)hit["normal"], origin.DistanceTo(point));
			}
			exclude.Add((Rid)hit["rid"]); // Includes editor helper colliders and ignored descendants.
		}
	}
	private JsonObject Raycast(JsonObject a, CancellationToken token)
	{
		var hit = Cast(GarkValues.Vector3Value(a["origin"]!), GarkValues.Vector3Value(a["direction"]!), Float(a, "max_distance") ?? 1000, Ignored(a), token);
		return hit == null ? new() { ["hit"] = false } : new() { ["hit"] = true, ["instance"] = Summary(hit.Item),
			["position"] = GarkValues.Encode(hit.Position), ["normal"] = GarkValues.Encode(hit.Normal), ["distance"] = hit.Distance };
	}
	private Dynamic[] SpatialTargets(JsonObject a) => Targets(a).Select(i => i as Dynamic
		?? throw new ArgumentException(i.Name + " is not a movable spatial object; select a Model or Dynamic instance.")).ToArray();
	private JsonArray MoveTogether(IEnumerable<(Dynamic Item, Vector3 Delta)> moves, string title)
	{
		using var edit = new SceneEdit(this);
		var result = new JsonArray();
		foreach (var (item, delta) in moves)
		{
			Vector3 before = item.Position, after = before + delta;
			edit.Change(() => item.Position = after, () => item.Position = before);
			result.Add((JsonNode)new JsonObject { ["id"] = item.ObjectID, ["position"] = GarkValues.Encode(after), ["delta"] = GarkValues.Encode(delta) });
		}
		edit.Finish(title);
		return result;
	}
	private JsonArray PlaceOnGround(JsonObject a, CancellationToken token)
	{
		var items = SpatialTargets(a);
		float clearance = Float(a, "clearance") ?? 0;
		float distance = Float(a, "max_distance") ?? 1000;
		var moves = new List<(Dynamic, Vector3)>();
		foreach (var item in items)
		{
			Aabb bounds = Bounds(item);
			Vector3 origin = bounds.GetCenter(); origin.Y = bounds.End.Y + 0.01f;
			var hit = Cast(origin, Vector3.Down, distance, items, token)
				?? throw new InvalidOperationException("No ground found below " + item.Name + ". Nothing moved.");
			moves.Add((item, Vector3.Up * (hit.Position.Y + clearance - bounds.Position.Y)));
		}
		return MoveTogether(moves, "Gark: place on ground");
	}
	private JsonArray AlignInstances(JsonObject a)
	{
		var items = SpatialTargets(a);
		int axis = Text(a, "axis") switch { "x" => 0, "y" => 1, "z" => 2, _ => throw new ArgumentException("axis must be x, y or z.") };
		float Edge(Aabb b) => Text(a, "edge", "center") switch { "min" => b.Position[axis], "center" => b.GetCenter()[axis], "max" => b.End[axis], _ => throw new ArgumentException("edge must be min, center or max.") };
		if (a["coordinate"] != null && a["reference"] != null) throw new ArgumentException("Supply reference or coordinate, not both.");
		var reference = a["reference"] != null ? Resolve(Text(a, "reference")) : null;
		if (reference != null && items.Any(i => reference == i || reference.IsDescendantOf(i)))
			throw new ArgumentException("The reference cannot be one of the objects being moved or inside one.");
		float coordinate = Float(a, "coordinate") ?? Edge(reference != null ? Bounds(reference) : items.Select(i => Bounds(i)).Aggregate((b, next) => b.Merge(next)));
		var moves = items.Select(item => { var delta = Vector3.Zero; delta[axis] = coordinate - Edge(Bounds(item)); return (item, delta); }).ToArray();
		return MoveTogether(moves, "Gark: align objects");
	}
}
