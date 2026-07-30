using System;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.Native;

namespace MapEditor
{
    public partial class MapEditor
    {
        /// <summary>
        /// Where smart streaming measures from this frame: the freecam while it is up, the player otherwise.
        ///
        /// The two are never the same spot. Freelook parks the player eight metres behind the camera and drags
        /// them along frozen, and it is the camera the map is being looked at from — measuring from the player
        /// would be right by accident while the freecam is up and wrong the moment they are not the same thing,
        /// which is every frame of the object picker and every frame the player is put down somewhere.
        /// </summary>
        private Vector3 CurrentStreamingOrigin => IsInFreecam && _mainCamera != null && _mainCamera.Exists()
            ? _mainCamera.Position
            : Game.Player.Character.Position;

        /// <summary>How far through the map the rolling out-of-range scan has got. See <see cref="StreamOutDistantEntities"/>.</summary>
        private int _streamScanCursor;

        /// <summary>
        /// Keeps the map being edited down to the part of it the player is anywhere near: what has been left
        /// behind goes out of the world, what has been come back to comes back. See <see cref="SmartStreaming"/>
        /// for why, and <see cref="AutoloadedMaps.Tick"/> for the same thing done to the maps standing alongside
        /// this one.
        /// </summary>
        private void ProcessSmartStreaming()
        {
            // Putting back first, and unconditionally: with streaming switched off everything has returned, so
            // this is also what puts a map back together when the player turns the setting off.
            StreamInReturnedEntities();

            if (!SmartStreaming.Enabled) return;
            StreamOutDistantEntities();
        }

        /// <summary>
        /// Takes whatever the player has left far enough behind back out of the world.
        ///
        /// Asking an entity where it stands is a native call each and a large map is thousands of them, so the
        /// map is walked a slice at a time across frames rather than whole every frame. The cursor is an index
        /// into the three lists laid end to end, and those lists shift as entities are placed, deleted and
        /// streamed out under it: an entity stepped over that way is simply looked at on the next pass, a
        /// fraction of a second later, which is soon enough for something the player is hundreds of metres from.
        /// </summary>
        private void StreamOutDistantEntities()
        {
            var toScan = Math.Min(EditorEntityCount, SmartStreaming.ScanPerTick);

            for (var i = 0; i < toScan; i++)
            {
                var total = EditorEntityCount;
                if (total == 0)
                {
                    _streamScanCursor = 0;
                    return;
                }
                if (_streamScanCursor >= total) _streamScanCursor = 0;

                var handle = EditorEntityAt(_streamScanCursor);
                var entity = handle == 0 ? null : Compat.Ent(handle);

                if (entity == null || !SmartStreaming.IsTooFar(entity.Position) || IsEntityBusy(handle))
                {
                    _streamScanCursor++;
                    continue;
                }

                if (!SmartStreaming.TakeDespawn()) return;

                var record = PropStreamer.StreamOut(entity);
                if (record == null)
                {
                    _streamScanCursor++;
                    continue;
                }

                // Its row stays in the entity menu — the map is no shorter for this — but the handle it was
                // pointing at has just been handed back to the game, and the game gives handles out again.
                RetagEntityMenuItem(EntityMenuKind.Entity, handle, EntityMenuKind.Streamed, record.Uid);

                // The lists closed up over the gap, so the cursor is already pointing at the next entity.
            }
        }

        /// <summary>
        /// Puts back whatever the player has come back to. A spawn that finds its model not in memory yet is
        /// not a failure: the model has been asked for and the next pass over this record puts it in.
        /// </summary>
        private void StreamInReturnedEntities()
        {
            var streamed = PropStreamer.StreamedOut;

            for (var i = streamed.Count - 1; i >= 0; i--)
            {
                var record = streamed[i];
                if (!SmartStreaming.HasReturned(record.Object.Position)) continue;

                // A map with more props than the world will hold is workable with streaming on, as long as no
                // more than the limit are in range at once. Over that, the rest waits rather than telling the
                // player about the prop limit on every frame.
                if (record.Object.Type == ObjectTypes.Prop && PropStreamer.PropSlotsFull) continue;

                if (!SmartStreaming.TakeSpawn()) return;

                var entity = SpawnMapObject(record.Object, streaming: true);
                if (entity == null) continue;

                PropStreamer.StreamedIn(record, entity);
                RetagEntityMenuItem(EntityMenuKind.Streamed, record.Uid, EntityMenuKind.Entity, entity.Handle);
            }
        }

        /// <summary>
        /// Puts one streamed-out entity back where it belongs there and then, for a player who has asked for it
        /// by name in the entity menu rather than by flying to it. Blocks for the model the way loading a map
        /// does: the player is waiting on this one.
        /// </summary>
        private Entity StreamInNow(PropStreamer.StreamedObject record)
        {
            var entity = SpawnMapObject(record.Object);
            if (entity == null) return null;

            PropStreamer.StreamedIn(record, entity);
            RetagEntityMenuItem(EntityMenuKind.Streamed, record.Uid, EntityMenuKind.Entity, entity.Handle);
            return entity;
        }

        private static int EditorEntityCount =>
            PropStreamer.StreamedInHandles.Count + PropStreamer.Vehicles.Count + PropStreamer.Peds.Count;

        /// <summary>The map's props, vehicles and peds as one list, for the rolling scan to walk.</summary>
        private static int EditorEntityAt(int index)
        {
            var props = PropStreamer.StreamedInHandles;
            if (index < props.Count) return props[index];
            index -= props.Count;

            var vehicles = PropStreamer.Vehicles;
            if (index < vehicles.Count) return vehicles[index];
            index -= vehicles.Count;

            var peds = PropStreamer.Peds;
            return index < peds.Count ? peds[index] : 0;
        }

        /// <summary>
        /// Whether an entity is one streaming has to leave alone. Everything here is something that is holding
        /// the entity itself rather than a description of it, and would be left holding a deleted one.
        /// </summary>
        private bool IsEntityBusy(int handle)
        {
            if (handle == 0) return true;

            // Whatever the player has in their hands, in every sense the editor has of that.
            if (_selectedProp != null && _selectedProp.Handle == handle) return true;
            if (_snappedProp != null && _snappedProp.Handle == handle) return true;
            if (_previewProp != null && _previewProp.Handle == handle) return true;
            if (_stackingBase != null && _stackingBase.Handle == handle) return true;
            if (_loopingBase != null && _loopingBase.Handle == handle) return true;
            if (_multiSelection.Any(e => e != null && e.Handle == handle)) return true;

            // An entity with a name was handed to the map's own script as an object it can move, task and
            // delete (see <see cref="JavascriptHook.StartScript"/>). Deleting it out from under the script
            // would leave it holding nothing, and there is no way to hand it the replacement.
            if (PropStreamer.Identifications.ContainsKey(handle)) return true;

            // Only reachable with the freecam down, since the camera is what distance is measured from while
            // it is up and the player is dragged along behind it — but a car deleted out from under whoever is
            // driving it is worth one comparison a frame to rule out.
            var vehicle = Game.Player.Character.CurrentVehicle;
            if (vehicle != null && vehicle.Handle == handle) return true;

            return false;
        }

        /// <summary>
        /// Puts one map object into the world as part of the map being edited, and registers everything the
        /// editor keeps beside the entity itself — its label, its door flag, its scenario, its relationship,
        /// its weapon, its siren — under the handle it was given.
        ///
        /// Shared by <see cref="LoadMap"/> and by streaming, which is the point: an entity that goes out of the
        /// world and comes back has to come back as exactly the thing loading the map would have produced, or
        /// the map drifts a little further from itself on every trip.
        ///
        /// <paramref name="streaming"/> says nobody is waiting for this one. A map the player asked to load
        /// blocks for its models and takes a breather every couple of hundred props; scenery filling in behind
        /// them does neither, and comes back empty-handed for the caller to try again on a later frame.
        /// </summary>
        private Entity SpawnMapObject(MapObject o, bool streaming = false)
        {
            if (o == null) return null;

            var model = streaming ? SmartStreaming.RequestModel(o.Hash) : ObjectPreview.LoadObject(o.Hash);
            if (model == null) return null;

            switch (o.Type)
            {
                case ObjectTypes.Prop:
                {
                    // A door is spawned static so the game does not drop it, then unfrozen so it can swing.
                    var prop = PropStreamer.CreateProp(model, o.Position, o.Rotation, o.Dynamic && !o.Door,
                        IsUnsetQuaternion(o.Quaternion) ? null : o.Quaternion,
                        drawDistance: _settings.DrawDistance, pace: !streaming);
                    if (prop == null) return null;

                    if (o.Door)
                    {
                        PropStreamer.Doors.Add(prop.Handle);
                        prop.IsPositionFrozen = false;
                    }

                    RegisterIdentification(o, prop.Handle);
                    return prop;
                }
                case ObjectTypes.Vehicle:
                {
                    // Unlike a ped, a vehicle is quite happy sitting at an angle — on a ramp, on its roof — and
                    // that angle is in the map file, so it is put back on.
                    var vehicle = PropStreamer.CreateVehicle(model, o.Position, o.Rotation.Z, o.Dynamic,
                        IsUnsetQuaternion(o.Quaternion) ? null : o.Quaternion, _settings.DrawDistance);
                    if (vehicle == null) return null;

                    vehicle.Mods.PrimaryColor = (VehicleColor) o.PrimaryColor;
                    vehicle.Mods.SecondaryColor = (VehicleColor) o.SecondaryColor;
                    if (o.Livery >= 0)
                    {
                        Function.Call(Hash.SET_VEHICLE_MOD_KIT, vehicle.Handle, 0);
                        vehicle.Mods.Livery = o.Livery;
                    }

                    if (o.SirensActive)
                    {
                        PropStreamer.ActiveSirens.Add(vehicle.Handle);
                        vehicle.IsSirenActive = true;
                    }

                    RegisterIdentification(o, vehicle.Handle);
                    return vehicle;
                }
                case ObjectTypes.Ped:
                {
                    // Peds stand on their feet where props hang from their centre, and the editor stores the
                    // centre. A ped is only ever placed standing, so the heading is all of its rotation that
                    // means anything and the quaternion is left alone.
                    var ped = PropStreamer.CreatePed(model, o.Position - new Vector3(0f, 0f, 1f), o.Rotation.Z,
                        o.Dynamic, drawDistance: _settings.DrawDistance);
                    if (ped == null) return null;

                    PedComponents.Apply(ped, o.Drawables, o.Textures);

                    var action = string.IsNullOrEmpty(o.Action) ? "None" : o.Action;
                    PropStreamer.ActiveScenarios[ped.Handle] = action;
                    StartPedScenario(ped, action);

                    var relationship = o.Relationship ?? DefaultRelationship.ToString();
                    PropStreamer.ActiveRelationships[ped.Handle] = relationship;
                    if (relationship != DefaultRelationship.ToString())
                        ObjectDatabase.SetPedRelationshipGroup(ped, relationship);

                    var weapon = o.Weapon ?? WeaponHash.Unarmed;
                    PropStreamer.ActiveWeapons[ped.Handle] = weapon;
                    if (weapon != WeaponHash.Unarmed)
                        ped.Weapons.Give(weapon, 999, true, true);

                    RegisterIdentification(o, ped.Handle);
                    return ped;
                }
            }

            return null;
        }

        private static void RegisterIdentification(MapObject o, int handle)
        {
            if (string.IsNullOrWhiteSpace(o.Id)) return;
            PropStreamer.Identifications[handle] = o.Id;
        }

        private static void StartPedScenario(Ped ped, string action)
        {
            if (string.IsNullOrEmpty(action) || action == "None") return;

            switch (action)
            {
                case "Any":
                case "Any - Walk":
                    Function.Call(Hash.TASK_USE_NEAREST_SCENARIO_TO_COORD, ped.Handle, ped.Position.X,
                        ped.Position.Y, ped.Position.Z, 100f, -1);
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

        /// <summary>
        /// A quaternion that was never written to the map file, as opposed to a real rotation. Handing it to
        /// the game as one collapses the entity into a corner of itself.
        /// </summary>
        private static bool IsUnsetQuaternion(Quaternion q)
        {
            return q == null || (q.X == 0 && q.Y == 0 && q.Z == 0 && q.W == 0);
        }

        /// <summary>
        /// Points a "Current Entities" row at what it now has to point at. The row itself does not move: an
        /// entity going out of the world and coming back is not the player's business, and a map they are
        /// halfway through picking through must not reshuffle under them because they walked away from it.
        /// </summary>
        private void RetagEntityMenuItem(EntityMenuKind fromKind, int fromId, EntityMenuKind toKind, int toId)
        {
            foreach (var item in _currentObjectsMenu.Items)
            {
                var tag = item.Tag as EntityMenuTag;
                if (tag == null || tag.Kind != fromKind || tag.Id != fromId) continue;

                tag.Kind = toKind;
                tag.Id = toId;
                return;
            }
        }
    }
}
