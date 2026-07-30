using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.Native;

namespace MapEditor
{
	/// <summary>
	/// The map being edited: everything in it, everything the editor keeps beside it, and the spawning and
	/// deleting of it.
	///
	/// The lists below hold what is standing in the world at this moment, keyed by entity handle, and
	/// <see cref="StreamedOut"/> holds the rest of the map — the part <see cref="SmartStreaming"/> has taken
	/// out of the world because the player is nowhere near it. Both halves are the map: the counts, what gets
	/// saved and the entity menu all cover the two together.
	/// </summary>
	public static class PropStreamer
	{
		public static int MAX_OBJECTS = 2048;

	    public static List<int> UsedModels = new List<int>();

		public static List<MapObject> MemoryObjects = new List<MapObject>();

		public static List<int> StreamedInHandles = new List<int>();

		public static List<int> StaticProps = new List<int>();

		public static List<int> Vehicles = new List<int>();

		public static List<int> Peds = new List<int>();

        public static List<DynamicPickup> Pickups = new List<DynamicPickup>();
        
        public static Dictionary<int, string> Identifications = new Dictionary<int, string>();

		public static List<Marker> Markers = new List<Marker>();

		public static Dictionary<int, string> ActiveScenarios = new Dictionary<int, string>();

		public static Dictionary<int, string> ActiveRelationships = new Dictionary<int, string>();

		public static Dictionary<int, WeaponHash> ActiveWeapons = new Dictionary<int, WeaponHash>();

        public static List<int> Doors = new List<int>(); 

		public static List<int> ActiveSirens = new List<int>();

		public static List<MapObject> RemovedObjects = new List<MapObject>();

	    public static MapMetadata CurrentMapMetadata = new MapMetadata();

		/// <summary>
		/// One entity of the map being edited that <see cref="SmartStreaming"/> has taken back out of the world
		/// because the player left it behind. It is still part of the map in every way that matters — it is
		/// counted, it is saved, and it still has its row in the entity menu — it is simply not standing
		/// anywhere at the moment.
		/// </summary>
		public sealed class StreamedObject
		{
			/// <summary>Everything needed to put it back, in the same shape the map file holds it in.</summary>
			public MapObject Object;

			/// <summary>
			/// What its entity menu row points at while there is no entity handle to point at. Handles are
			/// handed back to the game when the entity goes and are given out again to whatever the game
			/// spawns next, so the row cannot simply keep the old one.
			/// </summary>
			public int Uid;
		}

		public static List<StreamedObject> StreamedOut = new List<StreamedObject>();

		private static int _streamUids;

		public static int PropCount => StreamedInHandles.Count + MemoryObjects.Count + StreamedOutCount(ObjectTypes.Prop);

		public static int VehicleCount => Vehicles.Count + StreamedOutCount(ObjectTypes.Vehicle);

		public static int PedCount => Peds.Count + StreamedOutCount(ObjectTypes.Ped);

		public static int EntityCount => PropCount + VehicleCount + PedCount;

		/// <summary>
		/// Whether the world is holding as many props as it may. Streaming asks before spawning, because a map
		/// with more props than the limit is perfectly workable as long as no more than the limit are in range
		/// at once, and trying anyway would tell the player they had reached the limit on every frame.
		/// </summary>
		public static bool PropSlotsFull => StreamedInHandles.Count >= MAX_OBJECTS;

		private static int StreamedOutCount(ObjectTypes type)
		{
			var count = 0;
			foreach (var streamed in StreamedOut)
			{
				if (streamed.Object.Type == type) count++;
			}
			return count;
		}

        public static Prop CreateProp(Model model, Vector3 position, Vector3 rotation, bool dynamic, Quaternion q = null, bool force = false, int drawDistance = -1, bool pace = true)
		{
			if (StreamedInHandles.Count >= MAX_OBJECTS)
			{
				Compat.Notify("~r~~h~Map Editor~h~~w~\nYou have reached the prop limit. You cannot place any more props.");
				return null;
			}

			// A breather every so often while a whole map is being poured into the world in one go. Counted on
			// what is actually standing, not on the size of the map: with smart streaming most of a large map
			// is not in the world, and counting that would have this waiting on every single prop for as long
			// as the total sat on a multiple. Streaming itself passes pace: false — it already spreads its
			// spawning over frames, and a hundred milliseconds lost in the middle of one is a visible stutter.
			if (pace && StreamedInHandles.Count > 0 && StreamedInHandles.Count % 249 == 0)
                Script.Wait(100);

			var prop = Compat.PropFrom(Function.Call<int>(Hash.CREATE_OBJECT_NO_OFFSET, model.Hash, position.X, position.Y, position.Z, true, true, dynamic));
			if (prop == null)
			{
				Compat.Notify("~r~~h~Map Editor~h~~w~~n~The prop failed to spawn.");
				return null;
			}
            prop.Rotation = rotation;
			StreamedInHandles.Add(prop.Handle);
			if (!dynamic)
			{
				StaticProps.Add(prop.Handle);
				prop.IsPositionFrozen = true;
			}
			if (q != null)
				Quaternion.SetEntityQuaternion(prop, q);
			prop.Position = position;
		    if (drawDistance != -1)
		        prop.LodDistance = drawDistance;
            UsedModels.Add(model.Hash);
            model.MarkAsNoLongerNeeded();
			return prop;
		}

		public static Vehicle CreateVehicle(Model model, Vector3 position, float heading, bool dynamic, Quaternion q = null, int drawDistance = -1)
		{
			Vehicle veh;
			int counter = 0;
			do
			{
				veh = World.CreateVehicle(model, position, heading);
				counter++;
			} while (veh == null && counter < 2000);

			if (veh == null)
			{
				Compat.Notify("~r~~h~Map Editor~h~~w~~n~I tried very hard, but the vehicle failed to load.");
				return null;
			}

			Vehicles.Add(veh.Handle);
			if (!dynamic)
			{
				StaticProps.Add(veh.Handle);
				veh.IsPositionFrozen = true;
			}
			if(q != null)
				Quaternion.SetEntityQuaternion(veh, q);
		    if (drawDistance != -1)
		        veh.LodDistance = drawDistance;
            UsedModels.Add(model.Hash);
            model.MarkAsNoLongerNeeded();
            return veh;
		}

		public static Ped CreatePed(Model model, Vector3 position, float heading, bool dynamic, Quaternion q = null, int drawDistance = -1)
		{
			var veh = World.CreatePed(model, position, heading);
			if (veh == null)
			{
				Compat.Notify("~r~~h~Map Editor~h~~w~~n~The ped failed to spawn.");
				return null;
			}
			Peds.Add(veh.Handle);
			if (!dynamic)
			{
				StaticProps.Add(veh.Handle);
				veh.IsPositionFrozen = true;
			}
			if (q != null)
				Quaternion.SetEntityQuaternion(veh, q);
		    if (drawDistance != -1)
		        veh.LodDistance = drawDistance;
            UsedModels.Add(model.Hash);
            model.MarkAsNoLongerNeeded();
            return veh;
		}

	    private static int _pickupIds = 0;
        public static DynamicPickup CreatePickup(Model model, Vector3 position, float heading, int amount, bool dynamic, Quaternion q = null)
        {
            var v_4 = 515;
            int newPickup = -1;

            if (Game.Player.Character.IsInRange(position, 30f))
            {
                newPickup = Function.Call<int>(Hash.CREATE_PICKUP_ROTATE, model.Hash, position.X, position.Y,
                    position.Z, 0, 0, heading, v_4, amount, 0, false, 0);
            }

            var pcObj = new DynamicPickup(newPickup);
            pcObj.Flag = v_4;
            pcObj.Amount = amount;
            pcObj.RealPosition = position;
            if (newPickup != -1)
            {
                var start = 0;
                while (pcObj.ObjectHandle == -1 && start < 20)
                {
                    start++;
                    Script.Yield();
                }

                pcObj.Dynamic = false;

                var pickupObject = Compat.PropFrom(pcObj.ObjectHandle);
                if (pickupObject != null)
                {
                    pickupObject.IsPersistent = true;
                    if (q != null)
                        Quaternion.SetEntityQuaternion(pickupObject, q);
                }
                pcObj.UpdatePos();
            }
            
            Pickups.Add(pcObj);
            pcObj.PickupHash = model.Hash;
            pcObj.Timeout = 1;
            pcObj.UID = _pickupIds++;
            return pcObj;
        }

	    public static DynamicPickup GetPickup(int objectHandle)
	    {
            DynamicPickup pc = null;
            foreach (var pickup in Pickups)
            {
                if (pickup.ObjectHandle == objectHandle)
                {
                    pc = pickup;
                    break;
                }
            }

	        return pc;
	    }

        public static DynamicPickup GetPickupByUID(int uid)
        {
            DynamicPickup pc = null;
            foreach (var pickup in Pickups)
            {
                if (pickup.UID == uid)
                {
                    pc = pickup;
                    break;
                }
            }

            return pc;
        }

        public static void RemoveVehicle(int handle)
		{
		    var veh = Compat.VehicleFrom(handle);
		    if (veh != null)
		    {
		        ReleaseModel(veh.Model);
		        veh.Delete();
		    }
			if (Vehicles.Contains(handle)) Vehicles.Remove(handle);
			if (StaticProps.Contains(handle)) StaticProps.Remove(handle);
			Anchors.Remove(handle);
		}

		public static void RemovePed(int handle)
		{
		    var ped = Compat.PedFrom(handle);
		    if (ped != null)
		    {
		        ReleaseModel(ped.Model);
		        ped.Delete();
		    }
			if (Peds.Contains(handle)) Peds.Remove(handle);
			if (StaticProps.Contains(handle)) StaticProps.Remove(handle);
			Anchors.Remove(handle);
        }

		/// <summary>
		/// Drops one usage of a model and releases it back to the streamer when nothing else needs it.
		/// </summary>
		private static void ReleaseModel(Model model)
		{
		    UsedModels.Remove(model.Hash);
		    if (!UsedModels.Contains(model.Hash))
		        model.MarkAsNoLongerNeeded();
		}

	    public static void RemovePickup(int objectHandle)
	    {
	        DynamicPickup pc = null;
	        foreach (var pickup in Pickups)
	        {
	            if (pickup.ObjectHandle == objectHandle)
	            {
	                pc = pickup;
                    pc.Remove();
	                break;
	            }
	        }

	        if (pc != null) Pickups.Remove(pc);   
	    }

	    public static bool IsPickup(int entity)
	    {
	        return Pickups.Any(pickup => pickup.ObjectHandle == entity);
	    }

	    public static void RemoveEntity(int handle)
		{
		    var entity = handle != 0 ? Compat.Ent(handle) : null;
		    if (entity != null)
		        ReleaseModel(entity.Model);

	        if (IsPickup(handle))
	        {
	            var ourPickup = GetPickup(handle);
	            if (Pickups.Contains(ourPickup)) Pickups.Remove(ourPickup);
                ourPickup.Remove();
	        }
	        else
	        {
	            entity?.Delete();
	        }
	        if (Peds.Contains(handle)) Peds.Remove(handle);
			if (Vehicles.Contains(handle)) Vehicles.Remove(handle);
			if (StreamedInHandles.Contains(handle)) StreamedInHandles.Remove(handle);
			Anchors.Remove(handle);
		}

		internal static void AddProp(Prop prop, bool dynamic)
		{
			if (StreamedInHandles.Count > MAX_OBJECTS)
			{
				MemoryObjects.Add(new MapObject() {Dynamic = dynamic, Hash = prop.Model.Hash, Position = prop.Position, Quaternion = Quaternion.GetEntityQuaternion(prop), Rotation = prop.Rotation, Type = ObjectTypes.Prop});
				prop.Delete();
				return;
			}
			StreamedInHandles.Add(prop.Handle);
			if(!dynamic)
				StaticProps.Add(prop.Handle);
		}

		internal static void RemoveProp(Prop prop, bool dynamic)
		{
			if(StreamedInHandles.Contains(prop.Handle)) StreamedInHandles.Remove(prop.Handle);
			if(StaticProps.Contains(prop.Handle)) StaticProps.Remove(prop.Handle);
			if(MemoryObjects.Contains(new MapObject() { Dynamic = dynamic, Hash = prop.Model.Hash, Position = prop.Position, Quaternion = Quaternion.GetEntityQuaternion(prop), Rotation = prop.Rotation, Type = ObjectTypes.Prop })) 
				MemoryObjects.Remove(new MapObject() { Dynamic = dynamic, Hash = prop.Model.Hash, Position = prop.Position, Quaternion = Quaternion.GetEntityQuaternion(prop), Rotation = prop.Rotation, Type = ObjectTypes.Prop });
		}

		public static void RemoveAll()
		{
			StreamedInHandles.ForEach(i => Compat.Ent(i)?.Delete());
			StreamedInHandles.Clear();
			MemoryObjects.Clear();
			// Nothing to delete for these: they are the part of the map that is not in the world.
			StreamedOut.Clear();
			Anchors.Clear();
			StaticProps.Clear();
			Vehicles.ForEach(v => Compat.Ent(v)?.Delete());
			Peds.ForEach(v => Compat.Ent(v)?.Delete());
            Pickups.ForEach(p => p.Remove());
			Vehicles.Clear();
			Peds.Clear();
            Pickups.Clear();
		}

		/// <summary>
		/// The difference between the spot an entity was spawned to stand at and the spot it then says it is
		/// standing at.
		///
		/// The two are not always the same. A ped is spawned by its feet and answers about its middle, and the
		/// metre between the two is only exactly a metre for a grown human — a child, a dog or a bird answers
		/// from somewhere slightly else. Write down what it answers and spawn it from that again and it moves
		/// by that difference every time, so an afternoon of flying out to a map and back would slowly bury it
		/// in the ground or leave it hovering. The same goes, more coarsely, for anything spawned in the air
		/// that comes to rest a little lower than it was put.
		///
		/// So the difference is measured once, when the entity is put into the world, and taken back off
		/// whenever the map is read out of the world again. It is a property of the model rather than of the
		/// placement, which is why it can be taken off wherever the entity has got to since: an entity the
		/// player has moved is written down at its new spot, corrected the same way.
		/// </summary>
		private static readonly Dictionary<int, Vector3> Anchors = new Dictionary<int, Vector3>();

		/// <summary>
		/// How far the game may be seen to move something on the way in before the measurement is written off
		/// as something other than the offset this is here to undo, and ignored. Nothing legitimate is out by
		/// more than the metre a ped is spawned by.
		/// </summary>
		private const float MaxAnchorCorrection = 2f;

		/// <summary>
		/// Takes an object into the map without putting it into the world, for a map loaded from a file with
		/// most of itself somewhere the player is not. It is part of the map from this moment — counted, saved,
		/// and listed in the entity menu — and streaming spawns it when the player goes to it.
		/// </summary>
		public static StreamedObject AddStreamedOut(MapObject o)
		{
			var record = new StreamedObject { Object = o, Uid = _streamUids++ };
			StreamedOut.Add(record);
			return record;
		}

		/// <summary>
		/// Takes one entity of the map out of the world, keeping everything needed to put it back. The map is
		/// no shorter for it: the snapshot stands in for the entity in the count, in what gets saved, and in
		/// the entity menu, until <see cref="StreamedIn"/> hands the map back a real one.
		/// </summary>
		public static StreamedObject StreamOut(Entity entity)
		{
			if (entity == null) return null;

			var handle = entity.Handle;
			var snapshot = Snapshot(handle);
			if (snapshot == null) return null;

			var record = AddStreamedOut(snapshot);

			// Everything these were holding for this entity has just been written into the snapshot, and the
			// handle they are keyed by is about to be handed back to the game and given out to something else.
			Identifications.Remove(handle);
			ActiveScenarios.Remove(handle);
			ActiveRelationships.Remove(handle);
			ActiveWeapons.Remove(handle);
			Doors.Remove(handle);
			ActiveSirens.Remove(handle);
			StaticProps.Remove(handle);
			StreamedInHandles.Remove(handle);
			Vehicles.Remove(handle);
			Peds.Remove(handle);
			Anchors.Remove(handle);

			ReleaseModel(entity.Model);
			entity.Delete();
			return record;
		}

		/// <summary>
		/// Hands the map back the entity <paramref name="record"/> stood in for. The caller has already spawned
		/// it and registered whatever the snapshot said about it; this is the record being retired.
		/// </summary>
		public static void StreamedIn(StreamedObject record, Entity entity)
		{
			StreamedOut.Remove(record);
			if (entity == null) return;

			Anchor(entity, record.Object.Position);
		}

		/// <summary>
		/// Writes down what the game did to <paramref name="asked"/> on the way in, so that reading the entity
		/// back out gives the spot it was asked to stand at rather than the spot it answers from. See
		/// <see cref="Anchors"/>.
		/// </summary>
		public static void Anchor(Entity entity, Vector3 asked)
		{
			if (entity == null) return;

			var correction = entity.Position - asked;
			if (correction.Length() > MaxAnchorCorrection)
			{
				Anchors.Remove(entity.Handle);
				return;
			}

			Anchors[entity.Handle] = correction;
		}

		/// <summary>The map object one entity of the map would be saved as, or null if it is no longer there.</summary>
		public static MapObject Snapshot(int handle)
		{
			if (Vehicles.Contains(handle)) return SnapshotVehicle(handle);
			if (Peds.Contains(handle)) return SnapshotPed(handle);
			return SnapshotProp(handle);
		}

		private static MapObject SnapshotProp(int handle)
		{
			var prop = Compat.Ent(handle);
			if (prop == null) return null;

			var snapshot = new MapObject()
			{
				Dynamic = !StaticProps.Contains(handle),
				Hash = prop.Model.Hash,
				Position = prop.Position,
				Quaternion = Quaternion.GetEntityQuaternion(prop),
				Rotation = prop.Rotation,
				Type = ObjectTypes.Prop,
				Door = Doors.Contains(handle),
				Id = (Identifications.ContainsKey(handle) && !string.IsNullOrWhiteSpace(Identifications[handle])) ? Identifications[handle] : null,
			};

			ApplyAnchor(handle, snapshot);
			return snapshot;
		}

		private static MapObject SnapshotVehicle(int handle)
		{
			var veh = Compat.VehicleFrom(handle);
			if (veh == null) return null;

			var snapshot = new MapObject()
			{
				Dynamic = !StaticProps.Contains(handle),
				Hash = veh.Model.Hash,
				Position = veh.Position,
				Quaternion = Quaternion.GetEntityQuaternion(veh),
				Rotation = veh.Rotation,
				Type = ObjectTypes.Vehicle,
				Id = (Identifications.ContainsKey(handle) && !string.IsNullOrWhiteSpace(Identifications[handle])) ? Identifications[handle] : null,
				SirensActive = ActiveSirens.Contains(handle),
				PrimaryColor = (int)veh.Mods.PrimaryColor,
				SecondaryColor = (int)veh.Mods.SecondaryColor,
				Livery = veh.Mods.Livery,
			};

			ApplyAnchor(handle, snapshot);
			return snapshot;
		}

		private static MapObject SnapshotPed(int handle)
		{
			var ped = Compat.PedFrom(handle);
			if (ped == null) return null;

			var snapshot = new MapObject()
			{
				Dynamic = !StaticProps.Contains(handle),
				Hash = ped.Model.Hash,
				Position = ped.Position,
				Quaternion = Quaternion.GetEntityQuaternion(ped),
				Rotation = ped.Rotation,
				Type = ObjectTypes.Ped,
				Action = ActiveScenarios.ContainsKey(handle) ? ActiveScenarios[handle] : "None",
				Id = (Identifications.ContainsKey(handle) && !string.IsNullOrWhiteSpace(Identifications[handle])) ? Identifications[handle] : null,
				Relationship = ActiveRelationships.ContainsKey(handle) ? ActiveRelationships[handle] : null,
				Weapon = ActiveWeapons.ContainsKey(handle) ? ActiveWeapons[handle] : (WeaponHash?)null,
				Drawables = PedComponents.ReadDrawables(ped),
				Textures = PedComponents.ReadTextures(ped),
			};

			ApplyAnchor(handle, snapshot);
			return snapshot;
		}

		/// <summary>Takes the offset measured on the way in back off the position. See <see cref="Anchors"/>.</summary>
		private static void ApplyAnchor(int handle, MapObject snapshot)
		{
			Vector3 correction;
			if (!Anchors.TryGetValue(handle, out correction)) return;

			snapshot.Position -= correction;
		}

		public static MapObject[] GetAllEntities()
		{
			var outList = new List<MapObject>();

			foreach (int handle in StreamedInHandles)
			{
				var snapshot = SnapshotProp(handle);
				if (snapshot != null) outList.Add(snapshot);
			}

			outList.AddRange(MemoryObjects);

			// The map is saved whole whether or not all of it happens to be standing at the moment. What was
			// streamed out is written from the snapshot taken on the way out, which is the same snapshot the
			// entity itself would have been written from.
			foreach (var streamed in StreamedOut)
				outList.Add(streamed.Object);

			foreach (int v in Vehicles)
			{
				var snapshot = SnapshotVehicle(v);
				if (snapshot != null) outList.Add(snapshot);
			}

			foreach (int v in Peds)
			{
				var snapshot = SnapshotPed(v);
				if (snapshot != null) outList.Add(snapshot);
			}

			foreach (DynamicPickup p in Pickups)
			{
				var pickupObject = Compat.Ent(p.ObjectHandle);
				outList.Add(new MapObject()
				{
					Dynamic = p.Dynamic,
					Hash = p.PickupHash,
					Position = p.RealPosition,
					Quaternion = pickupObject != null ? Quaternion.GetEntityQuaternion(pickupObject) : new Quaternion(),
					Rotation = pickupObject?.Rotation ?? new Vector3(),
					Type = ObjectTypes.Pickup,
					Amount = p.Amount,
					RespawnTimer = p.Timeout,
					Flag = p.Flag,
				});
			}

			return outList.ToArray();
		}

		public static int[] GetAllHandles()
		{
			List<int> outHandles = new List<int>();
			outHandles.AddRange(StreamedInHandles);
			outHandles.AddRange(Vehicles);
			outHandles.AddRange(Peds);
            outHandles.AddRange(Pickups.Select(p => p.ObjectHandle));
			return outHandles.ToArray();
		}

		[Obsolete("Prop streaming has been disabled since the object limit is 2048.")]
		public static void MoveToMemory(Entity i)
		{
			var obj = new MapObject()
			{
				Dynamic = !StaticProps.Contains(i.Handle),
				Hash = i.Model.Hash,
				Position = i.Position,
				Quaternion = Quaternion.GetEntityQuaternion(i),
				Rotation = i.Rotation,
				Type = ObjectTypes.Prop,
			};
            MemoryObjects.Add(obj);
			StreamedInHandles.Remove(i.Handle);
			StaticProps.Remove(i.Handle);
			i.Delete();
		}

		[Obsolete("Prop streaming has been disabled since the object limit is 2048.")]
		public static void MoveFromMemory(MapObject obj)
		{
			var prop = obj;
			Prop newProp = World.CreateProp(new Model(prop.Hash), prop.Position, prop.Rotation, false, false);
			newProp.IsPositionFrozen = !prop.Dynamic;
			StreamedInHandles.Add(newProp.Handle);
			if (!prop.Dynamic)
			{
				StaticProps.Add(newProp.Handle);
				newProp.IsPositionFrozen = true;
			}
			if (prop.Quaternion != null)
				Quaternion.SetEntityQuaternion(newProp, prop.Quaternion);
			newProp.Position = prop.Position;
			MemoryObjects.Remove(prop);
		}


	    private static bool _justTeleported;
		public static void Tick()
		{
			foreach (MapObject o in RemovedObjects)
			{
				Prop returnedProp = Function.Call<Prop>(Hash.GET_CLOSEST_OBJECT_OF_TYPE, o.Position.X, o.Position.Y, o.Position.Z, 1f, o.Hash, 0);
				if (returnedProp == null || returnedProp.Handle == 0 || StreamedInHandles.Contains(returnedProp.Handle)) continue;
				returnedProp.Delete();
			}
            
            foreach (Marker marker in Markers)
			{
                if (!marker.OnlyVisibleInEditor || marker.OnlyVisibleInEditor && MapEditor.IsInFreecam)
				Function.Call(Hash.DRAW_MARKER, (int) marker.Type, marker.Position.X, marker.Position.Y, marker.Position.Z, 0f, 0f, 0f,
				 marker.Rotation.X, marker.Rotation.Y, marker.Rotation.Z, marker.Scale.X, marker.Scale.Y, marker.Scale.Z,
				 marker.Red, marker.Green, marker.Blue, marker.Alpha, marker.BobUpAndDown, marker.RotateToCamera, 2, false, false, false);

			    if (marker.TeleportTarget.HasValue && Game.Player.Character.IsInRange(marker.Position, Math.Max(2f, marker.Scale.X)) && !_justTeleported)
			    {
			        if (!Game.Player.Character.IsInVehicle())
			            Game.Player.Character.Position = marker.TeleportTarget.Value;
			        else
			            Game.Player.Character.CurrentVehicle.Position = marker.TeleportTarget.Value;
			        _justTeleported = true;
			    }
			}

		    if (_justTeleported)
		    {
		        var isInRangeOfAny = Markers.Any(m =>
		        {
		            if (!m.TeleportTarget.HasValue) return false;
		            return Game.Player.Character.IsInRange(m.Position, Math.Max(2f, m.Scale.X));
		        });

		        if (!isInRangeOfAny) _justTeleported = false;
		    }

		    foreach (DynamicPickup pickup in Pickups)
		    {
		        pickup.Update();
		    }

			/*
			if(_lastPos == Game.Player.Character.Position)
				return;
			_lastPos = Game.Player.Character.Position;

			if (PropCount < MAX_OBJECTS)
			{
				if (MemoryObjects.Count != 0)
				{
					for (int i = MemoryObjects.Count - 1; i >= 0; i--)
					{
						var prop = MemoryObjects[i];
						Prop newProp = World.CreateProp(ObjectPreview.LoadObject(prop.Hash), prop.Position, prop.Rotation, false, false);
						newProp.IsPositionFrozen = !prop.Dynamic;
						StreamedInHandles.Add(newProp.Handle);
						if (!prop.Dynamic)
						{
							StaticProps.Add(newProp.Handle);
							newProp.FreezePosition = true;
						}
						if (prop.Quaternion != null)
							Quaternion.SetEntityQuaternion(newProp, prop.Quaternion);
						MemoryObjects.Remove(prop);
					}
				}
				return;
			}
			
			MapObject[] propsToRemove = StreamedInHandles.Select(i => new MapObject()
			{
				Dynamic = !StaticProps.Contains(i), Hash = new Prop(i).Model.Hash, Position = new Prop(i).Position, Quaternion = Quaternion.GetEntityQuaternion(new Prop(i)), Rotation = new Prop(i).Rotation, Type = ObjectTypes.Prop, Id = i
			}).OrderBy(obj => (obj.Position - Game.Player.Character.Position).Length()).ToArray();

			MapObject[] propsToReAdd = MemoryObjects.OrderBy(obj => (obj.Position - Game.Player.Character.Position).Length()).ToArray();


			int lastPropToRemove = 0;
			int lastPropToReAdd = 0;
			for (int i = 0; i < MAX_OBJECTS; i++)
			{
				if (propsToReAdd.Length <= lastPropToReAdd)
				{
					lastPropToRemove = MAX_OBJECTS - lastPropToReAdd;
					break;
				}
				if (propsToRemove.Length <= lastPropToRemove)
				{
					lastPropToReAdd = MAX_OBJECTS - lastPropToRemove;
					break;
				}
				float readdLen = (propsToReAdd[lastPropToReAdd].Position - Game.Player.Character.Position).Length();
				float removeLen = (propsToRemove[lastPropToRemove].Position - Game.Player.Character.Position).Length();
				if (readdLen < removeLen)
					lastPropToReAdd++;
				else
					lastPropToRemove++;
			}

			for (var i = lastPropToRemove; i < propsToRemove.Length; i++)
			{
				MoveToMemory(new Prop(propsToRemove[i].Id));
			}
			
			for (int i = 0; i < lastPropToReAdd; i++) // Have to spawn it in
			{
				var prop = propsToReAdd[i];
				MoveFromMemory(prop);
			}
			// */
		}
	}
}