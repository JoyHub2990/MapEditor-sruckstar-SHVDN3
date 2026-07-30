using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.Native;

namespace MapEditor
{
	/// <summary>
	/// The maps the player marked for autoloading, spawned once when the script starts.
	///
	/// They are deliberately kept out of <see cref="PropStreamer"/>: an autoloaded map is scenery the player
	/// wants standing while they build something else, not the map they are editing. Staying out of the
	/// streamer's lists is what keeps them out of the entity menu, out of whatever gets saved, and — the
	/// reason this class exists — standing through "New Map". <see cref="Unload"/> and <see cref="UnloadAll"/>
	/// are the only way out.
	/// </summary>
	public static class AutoloadedMaps
	{
		/// <summary>
		/// Maps dropped in here autoload no matter what they say, from before the flag moved into the map file.
		/// </summary>
		private const string LegacyFolder = "scripts\\AutoloadMaps";

		/// <summary>
		/// One prop, vehicle or ped of an autoloaded map, and the entity standing for it at the moment — which
		/// is nothing at all while the player is far enough away that <see cref="SmartStreaming"/> has taken it
		/// out of the world.
		///
		/// It is always put back from the map object it was read out of the file as, never from a fresh reading
		/// of the entity that was there. An autoloaded map is scenery nobody is editing and nobody is going to
		/// save, so there is nothing to be gained by letting a hundred trips past it walk it away from what its
		/// file says it is.
		/// </summary>
		private sealed class LoadedEntity
		{
			public LoadedEntity(MapObject o)
			{
				Object = o;
			}

			public readonly MapObject Object;

			/// <summary>Null while it is not in the world, which is not the same as <see cref="Gone"/>.</summary>
			public Entity Entity;

			/// <summary>
			/// Set once the entity turns out to have been deleted by somebody else. These are spawned
			/// persistent, so the game cannot have done it, and streaming it back in would be arguing with
			/// whoever did.
			/// </summary>
			public bool Gone;
		}

		/// <summary>
		/// What one autoloaded map put into the world. Kept per map rather than in one shared pile so that a
		/// single map can be taken back out while the others stay standing.
		/// </summary>
		private sealed class LoadedMap
		{
			public LoadedMap(string name)
			{
				Name = name;
			}

			public string Name { get; }

			public readonly List<LoadedEntity> Entities = new List<LoadedEntity>();
			public readonly List<int> Pickups = new List<int>();
			public readonly List<Marker> Markers = new List<Marker>();
			public readonly List<MapObject> RemovedObjects = new List<MapObject>();
		}

		private static readonly List<LoadedMap> Maps = new List<LoadedMap>();

		private static bool _justTeleported;

		public static int MapCount => Maps.Count;

		/// <summary>
		/// Everything the loaded maps are made of, whether or not it happens to be in the world right now: a
		/// count that fell as the player drove away would be answering a question nobody asked.
		/// </summary>
		public static int EntityCount => Maps.Sum(m => m.Entities.Count + m.Pickups.Count);

		public static bool Any => Maps.Count > 0;

		/// <summary>The loaded maps by name, in the order <see cref="Unload"/> indexes them.</summary>
		public static IEnumerable<string> Names => Maps.Select(m => m.Name);

		public static void LoadAll()
		{
			foreach (var path in FindMaps())
			{
				try
				{
					Load(path);
				}
				catch (Exception e)
				{
					Compat.Notify("~r~~h~Map Editor~h~~w~~n~" + Translation.Translate("Map failed to load, see error below."));
					Compat.Notify(e.Message);
					File.AppendAllText("scripts\\MapEditor.log",
						DateTime.Now + " AUTOLOAD FAILED (" + path + "):\r\n" + e + "\r\n");
				}
			}

			if (Maps.Count > 0)
				Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("Autoloaded maps:") + " ~h~" +
				              string.Join(", ", Names.ToArray()) + "~h~.");
		}

		/// <summary>
		/// Every map that asked to be loaded: the ones in the user's folder that carry the flag, plus everything
		/// in the legacy folder.
		/// </summary>
		private static IEnumerable<string> FindMaps()
		{
			var found = new List<string>();

			if (Directory.Exists(UserMaps.Folder))
			{
				foreach (var file in Directory.GetFiles(UserMaps.Folder, "*.xml"))
				{
					if (WantsAutoload(file))
						found.Add(file);
				}
			}

			if (Directory.Exists(LegacyFolder))
			{
				found.AddRange(Directory.GetFiles(LegacyFolder, "*.xml"));
				found.AddRange(Directory.GetFiles(LegacyFolder, "*.ini"));
			}

			return found;
		}

		/// <summary>
		/// Reads only the flag. The folder holds every map the player ever saved, most of which are not meant to
		/// spawn, and a Menyoo or otherwise foreign .xml in there is simply not one of ours.
		/// </summary>
		private static bool WantsAutoload(string path)
		{
			try
			{
				var map = new MapSerializer().Deserialize(path, MapSerializer.Format.NormalXml);
				return map?.Metadata != null && map.Metadata.Autoload;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static void Load(string path)
		{
			var format = path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
				? MapSerializer.Format.SimpleTrainer
				: MapSerializer.Format.NormalXml;

			var map = new MapSerializer().Deserialize(path, format);
			if (map == null) return;

			var loaded = new LoadedMap(map.Metadata != null && !string.IsNullOrWhiteSpace(map.Metadata.Name)
				? map.Metadata.Name
				: Path.GetFileNameWithoutExtension(path));

			// Listed before anything is spawned, so a map that throws halfway through still owns what it managed
			// to put into the world and can be unloaded again.
			Maps.Add(loaded);

			foreach (var o in map.Objects)
			{
				if (o == null) continue;

				// A pickup is named by a pickup hash, not a model hash, and the game keeps it as a pickup
				// rather than as an entity. It has a range of its own and is left to it.
				if (o.Type == ObjectTypes.Pickup)
				{
					var pickup = Function.Call<int>(Hash.CREATE_PICKUP_ROTATE, o.Hash, o.Position.X, o.Position.Y,
						o.Position.Z, 0f, 0f, o.Rotation.Z, 515, o.Amount, 0, false, 0);
					if (pickup != 0) loaded.Pickups.Add(pickup);
					continue;
				}

				var entry = new LoadedEntity(o);
				loaded.Entities.Add(entry);

				// Only the part of the map the player is anywhere near goes into the world now. The rest is
				// put in by <see cref="StreamEntities"/> as they reach it, which is what makes a map far
				// larger than the world will hold cost nothing until it is walked into.
				if (SmartStreaming.HasReturned(o.Position))
					entry.Entity = Spawn(o);
			}

			foreach (var o in map.RemoveFromWorld)
			{
				if (o != null) loaded.RemovedObjects.Add(o);
			}

			foreach (var marker in map.Markers)
			{
				if (marker != null) loaded.Markers.Add(marker);
			}
		}

		/// <summary>
		/// Puts one object of an autoloaded map into the world, and hands back what it put there.
		///
		/// <paramref name="streaming"/> says nobody is waiting for it: it is scenery filling in behind a player
		/// who is walking towards it, so a model that is not in memory yet is asked for and the whole thing is
		/// left for a later frame rather than blocking on it with "Loading Model" on the screen.
		/// </summary>
		private static Entity Spawn(MapObject o, bool streaming = false)
		{
			var model = streaming ? SmartStreaming.RequestModel(o.Hash) : ObjectPreview.LoadObject(o.Hash);
			if (model == null) return null;

			Entity spawned = null;

			switch (o.Type)
			{
				case ObjectTypes.Prop:
				{
					// A door is spawned static so the game does not drop it, then unfrozen so it can still swing.
					var prop = World.CreatePropNoOffset(model, o.Position, o.Rotation, o.Dynamic && !o.Door);
					if (prop == null) break;

					if (o.Quaternion != null && !IsUnset(o.Quaternion))
						Quaternion.SetEntityQuaternion(prop, o.Quaternion);

					prop.PositionNoOffset = o.Position;
					prop.IsPositionFrozen = !o.Dynamic && !o.Door;
					spawned = prop;
					break;
				}
				case ObjectTypes.Vehicle:
				{
					var vehicle = World.CreateVehicle(model, o.Position, o.Rotation.Z);
					if (vehicle == null) break;

					vehicle.Mods.PrimaryColor = (VehicleColor) o.PrimaryColor;
					vehicle.Mods.SecondaryColor = (VehicleColor) o.SecondaryColor;
					if (o.Livery >= 0)
					{
						Function.Call(Hash.SET_VEHICLE_MOD_KIT, vehicle.Handle, 0);
						vehicle.Mods.Livery = o.Livery;
					}
					vehicle.IsSirenActive = o.SirensActive;
					vehicle.IsPositionFrozen = !o.Dynamic;
					spawned = vehicle;
					break;
				}
				case ObjectTypes.Ped:
				{
					// Peds stand on their feet where props hang from their centre, and the editor stores the centre.
					var ped = World.CreatePed(model, o.Position - new Vector3(0f, 0f, 1f), o.Rotation.Z);
					if (ped == null) break;

					ped.IsPositionFrozen = !o.Dynamic;
					PedComponents.Apply(ped, o.Drawables, o.Textures);

					if (o.Weapon.HasValue && o.Weapon.Value != WeaponHash.Unarmed)
						ped.Weapons.Give(o.Weapon.Value, 999, true, true);

					if (!string.IsNullOrEmpty(o.Relationship) && o.Relationship != "Companion")
						ObjectDatabase.SetPedRelationshipGroup(ped, o.Relationship);

					StartScenario(ped, o.Action);
					spawned = ped;
					break;
				}
			}

			// Persistence is what stops the game from streaming the map out again the moment the player walks
			// away. Which is exactly what smart streaming then does instead, deliberately and reversibly, on
			// its own terms — the game's streamer would take a map out and never put it back.
			if (spawned != null) spawned.IsPersistent = true;

			model.MarkAsNoLongerNeeded();
			return spawned;
		}

		/// <summary>A quaternion that was never written to the map file, as opposed to a real rotation.</summary>
		private static bool IsUnset(Quaternion q)
		{
			return q.X == 0 && q.Y == 0 && q.Z == 0 && q.W == 0;
		}

		private static void StartScenario(Ped ped, string action)
		{
			if (string.IsNullOrEmpty(action) || action == "None") return;

			switch (action)
			{
				case "Any":
				case "Any - Walk":
					Function.Call(Hash.TASK_USE_NEAREST_SCENARIO_TO_COORD, ped.Handle, ped.Position.X, ped.Position.Y,
						ped.Position.Z, 100f, -1);
					return;
				case "Any - Warp":
					Function.Call(Hash.TASK_USE_NEAREST_SCENARIO_TO_COORD_WARP, ped.Handle, ped.Position.X,
						ped.Position.Y, ped.Position.Z, 100f, -1);
					return;
				case "Wander":
					ped.Task.WanderAround();
					return;
			}

			string scenario;
			if (ObjectDatabase.ScrenarioDatabase.TryGetValue(action, out scenario))
				Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE, ped.Handle, scenario, 0, 0);
		}

		/// <summary>Every marker still standing, from whichever maps are still loaded.</summary>
		private static IEnumerable<Marker> AllMarkers => Maps.SelectMany(m => m.Markers);

		public static void Tick()
		{
			if (Maps.Count == 0) return;

			StreamEntities();

			foreach (var map in Maps)
			{
				foreach (var o in map.RemovedObjects)
				{
					var prop = World.GetClosestProp(o.Position, 1f, new Model(o.Hash));
					// A prop another autoloaded map spawned here is not the world prop this one wants gone.
					if (prop == null || !prop.Exists() || IsOurs(prop)) continue;
					prop.Delete();
				}
			}

			foreach (var marker in AllMarkers)
			{
				if (marker.OnlyVisibleInEditor && !MapEditor.IsInFreecam) continue;

				World.DrawMarker(marker.Type, marker.Position, Vector3.Zero, marker.Rotation, marker.Scale,
					System.Drawing.Color.FromArgb(marker.Alpha, marker.Red, marker.Green, marker.Blue),
					marker.BobUpAndDown, marker.RotateToCamera);
			}

			TickTeleports();
		}

		/// <summary>Whether an entity is one of ours, from any of the loaded maps.</summary>
		private static bool IsOurs(Entity entity)
		{
			return Maps.Any(m => m.Entities.Any(e => e.Entity != null && e.Entity.Handle == entity.Handle));
		}

		/// <summary>How far through the loaded maps the rolling scan has got. See <see cref="StreamEntities"/>.</summary>
		private static int _scanCursor;

		/// <summary>
		/// Keeps the loaded maps down to the parts of them the player is anywhere near, the same way the editor
		/// does it for the map being edited. See <see cref="SmartStreaming"/>.
		///
		/// Autoloaded maps are the reason the setting is worth having at all: they are loaded whole at startup
		/// and stand there for the rest of the session, wherever the player goes, and there can be any number
		/// of them. Walked a slice at a time across frames, because asking every entity of every map where it
		/// is, on every frame, is the cost this is meant to be saving.
		/// </summary>
		private static void StreamEntities()
		{
			var total = 0;
			foreach (var map in Maps)
				total += map.Entities.Count;

			if (total == 0) return;

			var toScan = Math.Min(total, SmartStreaming.ScanPerTick);
			for (var i = 0; i < toScan; i++)
			{
				if (_scanCursor >= total) _scanCursor = 0;

				var entry = EntryAt(_scanCursor++);
				if (entry == null || entry.Gone) continue;

				// With the setting off there is nothing to ask about something that is already standing, and
				// asking anyway would be spending every frame on exactly the cost the player turned off.
				if (entry.Entity != null && !SmartStreaming.Enabled) continue;

				if (entry.Entity == null)
				{
					if (!SmartStreaming.HasReturned(entry.Object.Position)) continue;
					if (!SmartStreaming.TakeSpawn()) return;

					// A spawn that comes back empty-handed has asked for its model and will be tried again on
					// a later pass, once the game has it.
					entry.Entity = Spawn(entry.Object, streaming: true);
					continue;
				}

				if (!entry.Entity.Exists())
				{
					// Deleted by the player or by another mod: these are persistent, so the game did not do it.
					entry.Entity = null;
					entry.Gone = true;
					continue;
				}

				if (!SmartStreaming.IsTooFar(entry.Entity.Position)) continue;

				// Only reachable while the freecam is down, since distance is measured from the camera while
				// it is up and the player is dragged along behind it.
				var vehicle = Game.Player.Character.CurrentVehicle;
				if (vehicle != null && vehicle.Handle == entry.Entity.Handle) continue;

				if (!SmartStreaming.TakeDespawn()) return;

				entry.Entity.Delete();
				entry.Entity = null;
			}
		}

		/// <summary>The entities of every loaded map as one list, for the rolling scan to walk.</summary>
		private static LoadedEntity EntryAt(int index)
		{
			foreach (var map in Maps)
			{
				if (index < map.Entities.Count) return map.Entities[index];
				index -= map.Entities.Count;
			}

			return null;
		}

		private static void TickTeleports()
		{
			foreach (var marker in AllMarkers)
			{
				if (!marker.TeleportTarget.HasValue || _justTeleported) continue;
				if (!Game.Player.Character.IsInRange(marker.Position, Math.Max(2f, marker.Scale.X))) continue;

				if (Game.Player.Character.IsInVehicle())
					Game.Player.Character.CurrentVehicle.Position = marker.TeleportTarget.Value;
				else
					Game.Player.Character.Position = marker.TeleportTarget.Value;

				_justTeleported = true;
			}

			// Held down until the player leaves the pad, or they would be bounced straight back on arrival.
			if (_justTeleported && !AllMarkers.Any(m => m.TeleportTarget.HasValue &&
			                                            Game.Player.Character.IsInRange(m.Position, Math.Max(2f, m.Scale.X))))
				_justTeleported = false;
		}

		/// <summary>
		/// Takes one autoloaded map back out of the world, by its index in <see cref="Names"/>, and leaves the
		/// others standing. Returns the name of the map that went, or null if the index was not one.
		/// </summary>
		public static string Unload(int index)
		{
			if (index < 0 || index >= Maps.Count) return null;

			var map = Maps[index];
			Maps.RemoveAt(index);
			Remove(map);

			// Tick, and with it the latch's own reset, stops running once the last map is gone.
			if (Maps.Count == 0) _justTeleported = false;

			return map.Name;
		}

		/// <summary>
		/// Takes every autoloaded map back out of the world, leaving the map being edited untouched.
		/// </summary>
		public static void UnloadAll()
		{
			foreach (var map in Maps)
				Remove(map);

			Maps.Clear();
			_justTeleported = false;
		}

		/// <summary>
		/// Everything one map put into the world goes, and the world objects it deleted come back, since nothing
		/// is holding them down any more. A map still loaded that deletes the same object takes it out again on
		/// the next tick.
		/// </summary>
		private static void Remove(LoadedMap map)
		{
			foreach (var entry in map.Entities)
			{
				// Nothing to delete for the ones streaming has already taken out of the world.
				if (entry.Entity != null && entry.Entity.Exists())
					entry.Entity.Delete();
			}

			foreach (var pickup in map.Pickups)
				Function.Call(Hash.REMOVE_PICKUP, pickup);

			foreach (var o in map.RemovedObjects)
			{
				var prop = World.CreateProp(new Model(o.Hash), o.Position, o.Rotation, true, false);
				if (prop != null) prop.PositionNoOffset = o.Position;
			}

			map.Entities.Clear();
			map.Pickups.Clear();
			map.Markers.Clear();
			map.RemovedObjects.Clear();
		}
	}
}
