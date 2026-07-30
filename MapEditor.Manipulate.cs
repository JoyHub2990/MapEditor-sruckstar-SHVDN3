using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using Control = GTA.Control;

namespace MapEditor
{
    public partial class MapEditor
    {
        private static readonly Color MultiSelectionColor = Color.FromArgb(200, 20, 200, 20);

        private void ProcessFreelook(Entity hitEnt, float mouseX, float mouseY, float movementModifier, float modifier)
        {
            if (!_menuPool.AreAnyVisible || Game.LastInputMethod == InputMethod.GamePad)
                _mainCamera.Rotation = VectorExtensions.ClampCameraRotation(
                    new Vector3(_mainCamera.Rotation.X + mouseY, _mainCamera.Rotation.Y, _mainCamera.Rotation.Z + mouseX));

            var dir = VectorExtensions.RotationToDirection(_mainCamera.Rotation);
            var rotLeft = _mainCamera.Rotation + new Vector3(0, 0, -10);
            var rotRight = _mainCamera.Rotation + new Vector3(0, 0, 10);
            var right = VectorExtensions.RotationToDirection(rotRight) - VectorExtensions.RotationToDirection(rotLeft);

            var newPos = _mainCamera.Position;
            if (Game.IsControlPressed(Control.MoveUpOnly))
                newPos += dir * movementModifier;
            if (Game.IsControlPressed(Control.MoveDownOnly))
                newPos -= dir * movementModifier;
            if (Game.IsControlPressed(Control.MoveLeftOnly))
                newPos += right * movementModifier;
            if (Game.IsControlPressed(Control.MoveRightOnly))
                newPos -= right * movementModifier;
            _mainCamera.Position = newPos;
            Game.Player.Character.PositionNoOffset = _mainCamera.Position - dir * 8f;

            PruneMultiSelection();
            foreach (Entity selected in _multiSelection)
                DrawEntityBox(selected, MultiSelectionColor);

            if (_multiSelectionSnapped)
            {
                ProcessMultiSelectionMove(modifier);
            }
            else if (_snappedProp != null)
            {
                if (!IsProp(_snappedProp))
                    _snappedProp.Position = VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, _snappedProp);
                else
                    _snappedProp.PositionNoOffset = VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, _snappedProp);

                if (Game.IsControlPressed(Control.CursorScrollUp) || Game.IsControlPressed(Control.FrontendRb))
                {
                    _snappedProp.Rotation = _snappedProp.Rotation - new Vector3(0f, 0f, modifier);
                    if (IsPed(_snappedProp))
                        _snappedProp.Heading = _snappedProp.Rotation.Z;
                }

                if (Game.IsControlPressed(Control.CursorScrollDown) || Game.IsControlPressed(Control.FrontendLb))
                {
                    _snappedProp.Rotation = _snappedProp.Rotation + new Vector3(0f, 0f, modifier);
                    if (IsPed(_snappedProp))
                        _snappedProp.Heading = _snappedProp.Rotation.Z;
                }

                if (Game.IsControlJustPressed(Control.CreatorDelete))
                {
                    RemoveItemFromEntityMenu(_snappedProp);
                    if (PropStreamer.IsPickup(_snappedProp.Handle))
                    {
                        PropStreamer.RemovePickup(_snappedProp.Handle);
                    }
                    else
                    {
                        PropStreamer.RemoveEntity(_snappedProp.Handle);
                        if (PropStreamer.Identifications.ContainsKey(_snappedProp.Handle))
                            PropStreamer.Identifications.Remove(_snappedProp.Handle);
                    }
                    _snappedProp = null;
                    _changesMade++;
                }

                if (_snappedProp != null && Game.IsControlJustPressed(Control.Attack))
                {
                    if (PropStreamer.IsPickup(_snappedProp.Handle))
                        PropStreamer.GetPickup(_snappedProp.Handle).UpdatePos();
                    _snappedProp = null;
                    _changesMade++;
                }

                DrawButtons(_snappedButtons);
            }
            else if (_snappedMarker != null)
            {
                _snappedMarker.Position = VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, Game.Player.Character);

                if (Game.IsControlPressed(Control.CursorScrollUp) || Game.IsControlPressed(Control.FrontendRb))
                    _snappedMarker.Rotation = _snappedMarker.Rotation - new Vector3(0f, 0f, modifier);

                if (Game.IsControlPressed(Control.CursorScrollDown) || Game.IsControlPressed(Control.FrontendLb))
                    _snappedMarker.Rotation = _snappedMarker.Rotation + new Vector3(0f, 0f, modifier);

                if (Game.IsControlJustPressed(Control.CreatorDelete))
                {
                    RemoveMarkerFromEntityMenu(_snappedMarker.Id);
                    PropStreamer.Markers.Remove(_snappedMarker);
                    _snappedMarker = null;
                    _changesMade++;
                }

                if (Game.IsControlJustPressed(Control.Attack))
                {
                    _snappedMarker = null;
                    _changesMade++;
                }

                DrawButtons(_snappedButtons);
            }
            else
            {
                if (_settings.CrosshairType == CrosshairType.Orb)
                {
                    var pos = VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, Game.Player.Character);
                    var color = Color.FromArgb(255, 200, 20, 20);
                    if (hitEnt != null && hitEnt.Handle != 0 && !PropStreamer.GetAllHandles().Contains(hitEnt.Handle))
                        color = Color.FromArgb(255, 20, 20, 255);
                    else if (hitEnt != null && hitEnt.Handle != 0 && PropStreamer.GetAllHandles().Contains(hitEnt.Handle))
                        color = Color.FromArgb(255, 200, 200, 20);
                    Function.Call(Hash.DRAW_MARKER, 28, pos.X, pos.Y, pos.Z, 0f, 0f, 0f, 0f, 0f, 0f, 0.20f, 0.20f, 0.20f, color.R, color.G, color.B, color.A, false, true, 2, false, false, false, false);
                }

                if (Game.IsControlJustPressed(Control.Aim))
                {
                    if (_multiSelection.Count > 0)
                    {
                        BeginMultiSelectionMove();
                    }
                    else if (hitEnt != null && PropStreamer.GetAllHandles().Contains(hitEnt.Handle))
                    {
                        _snappedProp = WrapEntity(hitEnt);
                        _changesMade++;
                    }
                    else
                    {
                        var pos = VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, Game.Player.Character);
                        Marker mark = PropStreamer.Markers.FirstOrDefault(m => (m.Position - pos).Length() < 2f);
                        if (mark != null)
                        {
                            _snappedMarker = mark;
                            _changesMade++;
                        }
                    }
                }

                if (Game.IsControlJustPressed(Control.Attack) && IsMultiSelectKeyDown())
                {
                    if (hitEnt != null && PropStreamer.GetAllHandles().Contains(hitEnt.Handle))
                        ToggleMultiSelection(hitEnt);
                }
                else if (Game.IsControlJustPressed(Control.Attack))
                {
                    // A plain click always starts a fresh selection.
                    ClearMultiSelection();

                    if (hitEnt != null && PropStreamer.GetAllHandles().Contains(hitEnt.Handle))
                    {
                        _selectedProp = WrapEntity(hitEnt);
                        RedrawObjectInfoMenu(_selectedProp, true);
                        CloseAllMenus();
                        SetMenuVisible(_objectInfoMenu, true);
                        if (_settings.SnapCameraToSelectedObject)
                            _mainCamera.PointAt(_selectedProp, Vector3.Zero);
                        _changesMade++;
                    }
                    else
                    {
                        var pos = VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, Game.Player.Character);
                        Marker mark = PropStreamer.Markers.FirstOrDefault(m => (m.Position - pos).Length() < 2f);
                        if (mark != null)
                        {
                            _selectedMarker = mark;
                            RedrawObjectInfoMenu(_selectedMarker, true);
                            CloseAllMenus();
                            SetMenuVisible(_objectInfoMenu, true);
                            _changesMade++;
                        }
                    }
                }

                if (Game.IsControlJustReleased(Control.LookBehind))
                {
                    if (_multiSelection.Count > 0)
                    {
                        CopyMultiSelection();
                    }
                    else if (hitEnt != null)
                    {
                        _snappedProp = CopyEntity(hitEnt);
                        _changesMade++;
                    }
                    else
                    {
                        var pos = VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, Game.Player.Character);
                        Marker mark = PropStreamer.Markers.FirstOrDefault(m => (m.Position - pos).Length() < 2f);
                        if (mark != null)
                        {
                            var tmpMark = CloneMarker(mark);
                            AddItemToEntityMenu(tmpMark);
                            PropStreamer.Markers.Add(tmpMark);
                            _snappedMarker = tmpMark;
                            _changesMade++;
                        }
                    }
                }

                // Copying takes the object under the crosshair whether the editor placed it or the game did;
                // starring its model is the other half of that, for the game's objects that are only ever
                // found by looking at them.
                if (Game.IsControlJustPressed(Control.Context) && hitEnt != null)
                {
                    FavoriteAimedEntity(hitEnt);
                }

                if (Game.IsControlJustPressed(Control.CreatorDelete))
                {
                    if (_multiSelection.Count > 0)
                    {
                        DeleteMultiSelection();
                    }
                    else if (hitEnt != null && PropStreamer.GetAllHandles().Contains(hitEnt.Handle))
                    {
                        RemoveItemFromEntityMenu(hitEnt);
                        if (PropStreamer.Identifications.ContainsKey(hitEnt.Handle))
                            PropStreamer.Identifications.Remove(hitEnt.Handle);
                        if (PropStreamer.ActiveScenarios.ContainsKey(hitEnt.Handle))
                            PropStreamer.ActiveScenarios.Remove(hitEnt.Handle);
                        if (PropStreamer.ActiveRelationships.ContainsKey(hitEnt.Handle))
                            PropStreamer.ActiveRelationships.Remove(hitEnt.Handle);
                        if (PropStreamer.ActiveWeapons.ContainsKey(hitEnt.Handle))
                            PropStreamer.ActiveWeapons.Remove(hitEnt.Handle);
                        PropStreamer.RemoveEntity(hitEnt.Handle);
                        _changesMade++;
                    }
                    else if (hitEnt != null && !PropStreamer.GetAllHandles().Contains(hitEnt.Handle) && IsProp(hitEnt))
                    {
                        MapObject tmpObj = new MapObject()
                        {
                            Hash = hitEnt.Model.Hash,
                            Position = hitEnt.Position,
                            Rotation = hitEnt.Rotation,
                            Quaternion = Quaternion.GetEntityQuaternion(hitEnt),
                            Type = ObjectTypes.Prop,
                            Id = _mapObjCounter.ToString(),
                        };
                        _mapObjCounter++;
                        PropStreamer.RemovedObjects.Add(tmpObj);
                        AddItemToEntityMenu(tmpObj);
                        hitEnt.Delete();
                        _changesMade++;
                    }
                    else
                    {
                        var pos = VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, Game.Player.Character);
                        Marker mark = PropStreamer.Markers.FirstOrDefault(m => (m.Position - pos).Length() < 2f);
                        if (mark != null)
                        {
                            PropStreamer.Markers.Remove(mark);
                            RemoveMarkerFromEntityMenu(mark.Id);
                            _changesMade++;
                        }
                    }
                }

                DrawButtons(_freelookButtons);
            }
        }

        /// <summary>
        /// The raycast already hands back the concrete Prop/Vehicle/Ped subclass, so the entity
        /// can be used as-is.
        /// </summary>
        private static Entity WrapEntity(Entity hitEnt)
        {
            return hitEnt;
        }

        private Marker CloneMarker(Marker mark)
        {
            var tmpMark = new Marker()
            {
                BobUpAndDown = mark.BobUpAndDown,
                Red = mark.Red,
                Green = mark.Green,
                Blue = mark.Blue,
                Alpha = mark.Alpha,
                Position = mark.Position,
                RotateToCamera = mark.RotateToCamera,
                Rotation = mark.Rotation,
                Scale = mark.Scale,
                Type = mark.Type,
                Id = _markerCounter,
            };
            _markerCounter++;
            return tmpMark;
        }

        /// <summary>
        /// Duplicates an entity in place and returns the copy, or null if it could not be created.
        /// </summary>
        private Entity CopyEntity(Entity hitEnt)
        {
            if (PropStreamer.IsPickup(hitEnt.Handle))
            {
                var oldPickup = PropStreamer.GetPickup(hitEnt.Handle);
                var oldRotation = Compat.Ent(oldPickup.ObjectHandle)?.Rotation.Z ?? 0f;
                var newPickup = PropStreamer.CreatePickup(new Model(oldPickup.PickupHash), oldPickup.Position,
                    oldRotation, oldPickup.Amount, oldPickup.Dynamic);
                AddItemToEntityMenu(newPickup);
                return Compat.Ent(newPickup.ObjectHandle);
            }

            if (IsProp(hitEnt))
            {
                var isDoor = PropStreamer.Doors.Contains(hitEnt.Handle);
                Entity newProp;
                AddItemToEntityMenu(newProp = PropStreamer.CreateProp(hitEnt.Model, hitEnt.Position, hitEnt.Rotation,
                    (!PropStreamer.StaticProps.Contains(hitEnt.Handle) && !isDoor), q: Quaternion.GetEntityQuaternion(hitEnt),
                    force: true, drawDistance: _settings.DrawDistance));
                if (isDoor && newProp != null)
                {
                    newProp.IsPositionFrozen = false;
                    PropStreamer.Doors.Add(newProp.Handle);
                }
                return newProp;
            }

            if (IsVehicle(hitEnt))
            {
                Entity newVehicle;
                AddItemToEntityMenu(newVehicle = PropStreamer.CreateVehicle(hitEnt.Model, hitEnt.Position, hitEnt.Rotation.Z,
                    !PropStreamer.StaticProps.Contains(hitEnt.Handle), drawDistance: _settings.DrawDistance));
                return newVehicle;
            }

            if (IsPed(hitEnt))
            {
                Entity newPed;
                AddItemToEntityMenu(newPed = ((Ped)hitEnt).Clone(hitEnt.Rotation.Z));
                if (newPed == null) return null;

                PropStreamer.Peds.Add(newPed.Handle);

                if (_settings.DrawDistance != -1)
                    newPed.LodDistance = _settings.DrawDistance;

                if (PropStreamer.StaticProps.Contains(hitEnt.Handle))
                {
                    newPed.IsPositionFrozen = true;
                    PropStreamer.StaticProps.Add(newPed.Handle);
                }

                if (!PropStreamer.ActiveScenarios.ContainsKey(newPed.Handle))
                    PropStreamer.ActiveScenarios.Add(newPed.Handle, "None");

                if (PropStreamer.ActiveRelationships.ContainsKey(hitEnt.Handle))
                    PropStreamer.ActiveRelationships.Add(newPed.Handle, PropStreamer.ActiveRelationships[hitEnt.Handle]);
                else if (!PropStreamer.ActiveRelationships.ContainsKey(newPed.Handle))
                    PropStreamer.ActiveRelationships.Add(newPed.Handle, DefaultRelationship.ToString());

                if (PropStreamer.ActiveWeapons.ContainsKey(hitEnt.Handle))
                    PropStreamer.ActiveWeapons.Add(newPed.Handle, PropStreamer.ActiveWeapons[hitEnt.Handle]);
                else if (!PropStreamer.ActiveWeapons.ContainsKey(newPed.Handle))
                    PropStreamer.ActiveWeapons.Add(newPed.Handle, WeaponHash.Unarmed);

                return newPed;
            }

            return null;
        }

        private static bool IsMultiSelectKeyDown()
        {
            // WinForms reports both Ctrl keys as ControlKey, which is what the script hook tracks;
            // the L/R variants only ever show up through raw input.
            return Game.IsKeyPressed(Keys.ControlKey) ||
                   Game.IsKeyPressed(Keys.LControlKey) ||
                   Game.IsKeyPressed(Keys.RControlKey);
        }

        /// <summary>
        /// Adds the entity to the multi-selection, or drops it if it was already picked.
        /// </summary>
        private void ToggleMultiSelection(Entity ent)
        {
            int index = _multiSelection.FindIndex(e => e.Handle == ent.Handle);
            if (index != -1)
            {
                _multiSelection.RemoveAt(index);
                _multiSelectionOffsets.RemoveAt(index);
                return;
            }

            _multiSelection.Add(ent);
            _multiSelectionOffsets.Add(Vector3.Zero);
        }

        private void ClearMultiSelection()
        {
            if (_multiSelectionSnapped)
                EndMultiSelectionMove();

            _multiSelection.Clear();
            _multiSelectionOffsets.Clear();
        }

        /// <summary>
        /// Drops entities that were deleted or removed from the map behind our back, e.g. through the entity menu.
        /// </summary>
        private void PruneMultiSelection()
        {
            if (_multiSelection.Count == 0) return;

            var handles = PropStreamer.GetAllHandles();
            for (int i = _multiSelection.Count - 1; i >= 0; i--)
            {
                var ent = _multiSelection[i];
                if (ent != null && ent.Exists() && handles.Contains(ent.Handle)) continue;

                _multiSelection.RemoveAt(i);
                _multiSelectionOffsets.RemoveAt(i);
            }

            if (_multiSelection.Count == 0)
                _multiSelectionSnapped = false;
        }

        private Vector3 CrosshairPosition()
        {
            return VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, Game.Player.Character);
        }

        private void BeginMultiSelectionMove()
        {
            var anchor = CrosshairPosition();
            for (int i = 0; i < _multiSelection.Count; i++)
            {
                var ent = _multiSelection[i];
                _multiSelectionOffsets[i] = ent.Position - anchor;
                // Without this the crosshair raycast lands on the props being dragged and the group creeps
                // towards the camera instead of following the ground.
                Function.Call(Hash.SET_ENTITY_COLLISION, ent.Handle, false, true);
            }
            _multiSelectionSnapped = true;
        }

        private void EndMultiSelectionMove()
        {
            foreach (Entity ent in _multiSelection)
            {
                if (ent == null || !ent.Exists()) continue;
                RestoreCollision(ent);
                SyncPickup(ent);
            }
            _multiSelectionSnapped = false;
        }

        private static void RestoreCollision(Entity ent)
        {
            Function.Call(Hash.SET_ENTITY_COLLISION, ent.Handle, true, true);
        }

        private void ProcessMultiSelectionMove(float modifier)
        {
            if (Game.IsControlPressed(Control.CursorScrollUp) || Game.IsControlPressed(Control.FrontendRb))
                RotateMultiSelection(-modifier);

            if (Game.IsControlPressed(Control.CursorScrollDown) || Game.IsControlPressed(Control.FrontendLb))
                RotateMultiSelection(modifier);

            var anchor = CrosshairPosition();
            for (int i = 0; i < _multiSelection.Count; i++)
                SetEntityPosition(_multiSelection[i], anchor + _multiSelectionOffsets[i]);

            if (Game.IsControlJustPressed(Control.CreatorDelete))
            {
                DeleteMultiSelection();
                return;
            }

            if (Game.IsControlJustPressed(Control.Attack))
            {
                EndMultiSelectionMove();
                _changesMade++;
            }

            DrawButtons(_snappedButtons);
        }

        /// <summary>
        /// Spins the whole group around the crosshair, both the entities and the offsets they hold it by.
        /// </summary>
        private void RotateMultiSelection(float angle)
        {
            var rad = (float)VectorExtensions.DegToRad(angle);
            var cos = (float)Math.Cos(rad);
            var sin = (float)Math.Sin(rad);

            for (int i = 0; i < _multiSelection.Count; i++)
            {
                var offset = _multiSelectionOffsets[i];
                _multiSelectionOffsets[i] = new Vector3(offset.X * cos - offset.Y * sin, offset.X * sin + offset.Y * cos, offset.Z);

                var ent = _multiSelection[i];
                ent.Rotation = ent.Rotation + new Vector3(0f, 0f, angle);
                if (IsPed(ent))
                    ent.Heading = ent.Rotation.Z;
            }
        }

        /// <summary>
        /// Takes an entity off the map: its row in the entity menu, everything keyed by its handle, and
        /// the entity itself. Handles have to be cleaned up before the entity goes, and every one of them,
        /// or the next entity to be handed the same handle inherits what was left behind.
        /// </summary>
        private void DeleteEditorEntity(Entity ent)
        {
            if (ent == null || !ent.Exists()) return;

            RemoveItemFromEntityMenu(ent);
            PropStreamer.Identifications.Remove(ent.Handle);
            PropStreamer.ActiveScenarios.Remove(ent.Handle);
            PropStreamer.ActiveRelationships.Remove(ent.Handle);
            PropStreamer.ActiveWeapons.Remove(ent.Handle);
            PropStreamer.Doors.Remove(ent.Handle);

            if (PropStreamer.IsPickup(ent.Handle))
                PropStreamer.RemovePickup(ent.Handle);
            else
                PropStreamer.RemoveEntity(ent.Handle);
        }

        private void DeleteMultiSelection()
        {
            foreach (Entity ent in _multiSelection)
                DeleteEditorEntity(ent);

            _multiSelection.Clear();
            _multiSelectionOffsets.Clear();
            _multiSelectionSnapped = false;
            _changesMade++;
        }

        /// <summary>
        /// Duplicates every selected entity and hands the copies to the cursor as the new selection.
        /// </summary>
        private void CopyMultiSelection()
        {
            var copies = new List<Entity>();
            foreach (Entity ent in _multiSelection)
            {
                if (ent == null || !ent.Exists()) continue;
                var copy = CopyEntity(ent);
                if (copy != null) copies.Add(copy);
            }

            if (copies.Count == 0) return;

            ClearMultiSelection();
            foreach (Entity copy in copies)
            {
                _multiSelection.Add(copy);
                _multiSelectionOffsets.Add(Vector3.Zero);
            }
            BeginMultiSelectionMove();
            _changesMade++;
        }

        private void ProcessSelectedProp(float modifier)
        {
            var tmp = _controlsRotate ? Color.FromArgb(200, 200, 20, 20) : Color.FromArgb(200, 200, 200, 10);
            var (min, max) = _selectedProp.Model.Dimensions;
            var modelDims = max - min;
            Function.Call(Hash.DRAW_MARKER, 0, _selectedProp.Position.X, _selectedProp.Position.Y, _selectedProp.Position.Z + modelDims.Z + 2f, 0f, 0f, 0f, 0f, 0f, 0f, 2f, 2f, 2f, tmp.R, tmp.G, tmp.B, tmp.A, 1, 0, 2, 2, 0, 0, 0);

            DrawEntityBox(_selectedProp, tmp);

            if (Game.IsControlJustReleased(Control.Duck))
                _controlsRotate = !_controlsRotate;

            if (Game.IsControlPressed(Control.FrontendRb))
            {
                float pedMod = _selectedProp is Ped ? -1f : 0f;
                if (!_controlsRotate)
                    MoveSelectedProp(new Vector3(0f, 0f, (modifier / 4) + pedMod));
                else
                {
                    _selectedProp.Quaternion = new Vector3(_selectedProp.Rotation.X, _selectedProp.Rotation.Y, _selectedProp.Rotation.Z - (modifier / 4)).ToQuaternion();
                    if (IsPed(_selectedProp))
                        _selectedProp.Heading = _selectedProp.Rotation.Z;
                }
                SyncPickup(_selectedProp);
                _changesMade++;
            }

            if (Game.IsControlPressed(Control.FrontendLb))
            {
                float pedMod = _selectedProp is Ped ? 1f : 0f;
                if (!_controlsRotate)
                    MoveSelectedProp(new Vector3(0f, 0f, -((modifier / 4) + pedMod)));
                else
                {
                    _selectedProp.Quaternion = new Vector3(_selectedProp.Rotation.X, _selectedProp.Rotation.Y, _selectedProp.Rotation.Z + (modifier / 4)).ToQuaternion();
                    if (IsPed(_selectedProp))
                        _selectedProp.Heading = _selectedProp.Rotation.Z;
                }
                SyncPickup(_selectedProp);
                _changesMade++;
            }

            if (Game.IsControlPressed(Control.MoveUpOnly))
            {
                float pedMod = IsPed(_selectedProp) ? -1f : 0f;
                if (!_controlsRotate)
                {
                    var dir = VectorExtensions.RotationToDirection(_mainCamera.Rotation) * (modifier / 4);
                    MoveSelectedProp(new Vector3(dir.X, dir.Y, pedMod));
                }
                else
                    _selectedProp.Quaternion = new Vector3(_selectedProp.Rotation.X + (modifier / 4), _selectedProp.Rotation.Y, _selectedProp.Rotation.Z).ToQuaternion();
                SyncPickup(_selectedProp);
                _changesMade++;
            }

            if (Game.IsControlPressed(Control.MoveDownOnly))
            {
                float pedMod = _selectedProp is Ped ? 1f : 0f;
                if (!_controlsRotate)
                {
                    var dir = VectorExtensions.RotationToDirection(_mainCamera.Rotation) * (modifier / 4);
                    MoveSelectedProp(new Vector3(-dir.X, -dir.Y, -pedMod));
                }
                else
                    _selectedProp.Quaternion = new Vector3(_selectedProp.Rotation.X - (modifier / 4), _selectedProp.Rotation.Y, _selectedProp.Rotation.Z).ToQuaternion();
                SyncPickup(_selectedProp);
                _changesMade++;
            }

            if (Game.IsControlPressed(Control.MoveLeftOnly))
            {
                float pedMod = _selectedProp is Ped ? -1f : 0f;
                if (!_controlsRotate)
                {
                    var right = CameraRight(modifier);
                    MoveSelectedProp(new Vector3(right.X, right.Y, pedMod));
                }
                else
                    _selectedProp.Quaternion = new Vector3(_selectedProp.Rotation.X, _selectedProp.Rotation.Y + (modifier / 4), _selectedProp.Rotation.Z).ToQuaternion();
                SyncPickup(_selectedProp);
                _changesMade++;
            }

            if (Game.IsControlPressed(Control.MoveRightOnly))
            {
                float pedMod = _selectedProp is Ped ? 1f : 0f;
                if (!_controlsRotate)
                {
                    var right = CameraRight(modifier);
                    MoveSelectedProp(new Vector3(-right.X, -right.Y, -pedMod));
                }
                else
                    _selectedProp.Quaternion = new Vector3(_selectedProp.Rotation.X, _selectedProp.Rotation.Y - (modifier / 4), _selectedProp.Rotation.Z).ToQuaternion();
                SyncPickup(_selectedProp);
                _changesMade++;
            }

            if (Game.IsControlJustReleased(Control.MoveLeftOnly) ||
                Game.IsControlJustReleased(Control.MoveRightOnly) ||
                Game.IsControlJustReleased(Control.MoveUpOnly) ||
                Game.IsControlJustReleased(Control.MoveDownOnly) ||
                Game.IsControlJustReleased(Control.FrontendLb) ||
                Game.IsControlJustReleased(Control.FrontendRb))
            {
                RedrawObjectInfoMenu(_selectedProp, false);
            }

            if (Game.IsControlJustReleased(Control.LookBehind))
            {
                Entity mainProp = CopySelectedProp();
                _changesMade++;
                _selectedProp = mainProp;
                if (_settings.SnapCameraToSelectedObject && _selectedProp != null)
                    _mainCamera.PointAt(_selectedProp, Vector3.Zero);
                if (_selectedProp != null) RedrawObjectInfoMenu(_selectedProp, true);
            }

            if (_selectedProp != null && Game.IsControlJustPressed(Control.CreatorDelete))
            {
                if (PropStreamer.Identifications.ContainsKey(_selectedProp.Handle))
                    PropStreamer.Identifications.Remove(_selectedProp.Handle);
                if (PropStreamer.ActiveScenarios.ContainsKey(_selectedProp.Handle))
                    PropStreamer.ActiveScenarios.Remove(_selectedProp.Handle);
                if (PropStreamer.ActiveRelationships.ContainsKey(_selectedProp.Handle))
                    PropStreamer.ActiveRelationships.Remove(_selectedProp.Handle);
                if (PropStreamer.ActiveWeapons.ContainsKey(_selectedProp.Handle))
                    PropStreamer.ActiveWeapons.Remove(_selectedProp.Handle);
                RemoveItemFromEntityMenu(_selectedProp);
                PropStreamer.RemoveEntity(_selectedProp.Handle);
                _selectedProp = null;
                SetMenuVisible(_objectInfoMenu, false);
                _mainCamera.StopPointing();
                _changesMade++;
            }

            if (_selectedProp != null && (Game.IsControlJustPressed(Control.PhoneCancel) || Game.IsControlJustPressed(Control.Attack)))
            {
                SyncPickup(_selectedProp);
                _selectedProp = null;
                SetMenuVisible(_objectInfoMenu, false);
                _mainCamera.StopPointing();
                _changesMade++;
            }

            DrawButtons(_selectedButtons);
        }

        private Vector3 CameraRight(float modifier)
        {
            var rotLeft = _mainCamera.Rotation + new Vector3(0, 0, -10);
            var rotRight = _mainCamera.Rotation + new Vector3(0, 0, 10);
            return (VectorExtensions.RotationToDirection(rotRight) - VectorExtensions.RotationToDirection(rotLeft)) * (modifier / 2);
        }

        /// <summary>
        /// Props need PositionNoOffset so they don't get shifted by their model's bounding origin.
        /// </summary>
        private void MoveSelectedProp(Vector3 delta)
        {
            var target = _selectedProp.Position + delta;
            if (!IsProp(_selectedProp))
                _selectedProp.Position = target;
            else
                _selectedProp.PositionNoOffset = target;
        }

        private static void SyncPickup(Entity ent)
        {
            if (ent != null && PropStreamer.IsPickup(ent.Handle))
                PropStreamer.GetPickup(ent.Handle).UpdatePos();
        }

        private Entity CopySelectedProp()
        {
            if (PropStreamer.IsPickup(_selectedProp.Handle))
            {
                var oldPickup = PropStreamer.GetPickup(_selectedProp.Handle);
                var oldRotation = Compat.Ent(oldPickup.ObjectHandle)?.Rotation.Z ?? 0f;
                var newPickup = PropStreamer.CreatePickup(new Model(oldPickup.PickupHash), oldPickup.Position,
                    oldRotation, oldPickup.Amount, oldPickup.Dynamic);
                AddItemToEntityMenu(newPickup);
                return Compat.Ent(newPickup.ObjectHandle);
            }

            if (_selectedProp is Prop)
            {
                var isDoor = PropStreamer.Doors.Contains(_selectedProp.Handle);
                Entity mainProp;
                AddItemToEntityMenu(mainProp = PropStreamer.CreateProp(_selectedProp.Model, _selectedProp.Position, _selectedProp.Rotation,
                    (!PropStreamer.StaticProps.Contains(_selectedProp.Handle) && !isDoor), force: true,
                    q: Quaternion.GetEntityQuaternion(_selectedProp), drawDistance: _settings.DrawDistance));
                if (isDoor && mainProp != null)
                {
                    mainProp.IsPositionFrozen = false;
                    PropStreamer.Doors.Add(mainProp.Handle);
                }
                return mainProp;
            }

            if (_selectedProp is Vehicle)
            {
                Entity mainProp;
                AddItemToEntityMenu(mainProp = PropStreamer.CreateVehicle(_selectedProp.Model, _selectedProp.Position,
                    _selectedProp.Rotation.Z, !PropStreamer.StaticProps.Contains(_selectedProp.Handle), drawDistance: _settings.DrawDistance));
                return mainProp;
            }

            if (_selectedProp is Ped ped)
            {
                Entity mainProp;
                AddItemToEntityMenu(mainProp = ped.Clone(_selectedProp.Rotation.Z));
                if (mainProp == null) return _selectedProp;

                PropStreamer.Peds.Add(mainProp.Handle);
                if (!PropStreamer.ActiveScenarios.ContainsKey(mainProp.Handle))
                    PropStreamer.ActiveScenarios.Add(mainProp.Handle, "None");

                if (_settings.DrawDistance != -1)
                    mainProp.LodDistance = _settings.DrawDistance;

                if (PropStreamer.ActiveRelationships.ContainsKey(_selectedProp.Handle))
                    PropStreamer.ActiveRelationships.Add(mainProp.Handle, PropStreamer.ActiveRelationships[_selectedProp.Handle]);
                else if (!PropStreamer.ActiveRelationships.ContainsKey(mainProp.Handle))
                    PropStreamer.ActiveRelationships.Add(mainProp.Handle, DefaultRelationship.ToString());

                if (PropStreamer.ActiveWeapons.ContainsKey(_selectedProp.Handle))
                    PropStreamer.ActiveWeapons.Add(mainProp.Handle, PropStreamer.ActiveWeapons[_selectedProp.Handle]);
                else if (!PropStreamer.ActiveWeapons.ContainsKey(mainProp.Handle))
                    PropStreamer.ActiveWeapons.Add(mainProp.Handle, WeaponHash.Unarmed);

                return mainProp;
            }

            return _selectedProp;
        }

        private void ProcessSelectedMarker(float modifier)
        {
            if (Game.IsControlJustReleased(Control.Duck))
                _controlsRotate = !_controlsRotate;

            if (Game.IsControlPressed(Control.FrontendRb))
            {
                if (!_controlsRotate)
                    _selectedMarker.Position += new Vector3(0f, 0f, (modifier / 4));
                else
                    _selectedMarker.Rotation += new Vector3(0f, 0f, modifier);
                _changesMade++;
            }

            if (Game.IsControlPressed(Control.FrontendLb))
            {
                if (!_controlsRotate)
                    _selectedMarker.Position -= new Vector3(0f, 0f, (modifier / 4));
                else
                    _selectedMarker.Rotation -= new Vector3(0f, 0f, modifier);
                _changesMade++;
            }

            if (Game.IsControlPressed(Control.MoveUpOnly))
            {
                if (!_controlsRotate)
                {
                    var dir = VectorExtensions.RotationToDirection(_mainCamera.Rotation) * (modifier / 4);
                    _selectedMarker.Position += new Vector3(dir.X, dir.Y, 0f);
                }
                else
                    _selectedMarker.Rotation += new Vector3(modifier, 0f, 0f);
                _changesMade++;
            }

            if (Game.IsControlPressed(Control.MoveDownOnly))
            {
                if (!_controlsRotate)
                {
                    var dir = VectorExtensions.RotationToDirection(_mainCamera.Rotation) * (modifier / 4);
                    _selectedMarker.Position -= new Vector3(dir.X, dir.Y, 0f);
                }
                else
                    _selectedMarker.Rotation -= new Vector3(modifier, 0f, 0f);
                _changesMade++;
            }

            if (Game.IsControlPressed(Control.MoveLeftOnly))
            {
                if (!_controlsRotate)
                {
                    var right = CameraRight(modifier);
                    _selectedMarker.Position += new Vector3(right.X, right.Y, 0f);
                }
                else
                    _selectedMarker.Rotation += new Vector3(0f, modifier, 0f);
                _changesMade++;
            }

            if (Game.IsControlPressed(Control.MoveRightOnly))
            {
                if (!_controlsRotate)
                {
                    var right = CameraRight(modifier);
                    _selectedMarker.Position -= new Vector3(right.X, right.Y, 0f);
                }
                else
                    _selectedMarker.Rotation -= new Vector3(0f, modifier, 0f);
                _changesMade++;
            }

            if (Game.IsControlJustReleased(Control.MoveLeftOnly) ||
                Game.IsControlJustReleased(Control.MoveRightOnly) ||
                Game.IsControlJustReleased(Control.MoveUpOnly) ||
                Game.IsControlJustReleased(Control.MoveDownOnly) ||
                Game.IsControlJustReleased(Control.FrontendLb) ||
                Game.IsControlJustReleased(Control.FrontendRb))
            {
                RedrawObjectInfoMenu(_selectedMarker, false);
            }

            if (Game.IsControlJustReleased(Control.LookBehind))
            {
                var tmpMark = CloneMarker(_selectedMarker);
                PropStreamer.Markers.Add(tmpMark);
                AddItemToEntityMenu(tmpMark);
                _selectedMarker = tmpMark;
                RedrawObjectInfoMenu(_selectedMarker, true);
                _changesMade++;
            }

            if (Game.IsControlJustPressed(Control.CreatorDelete))
            {
                PropStreamer.Markers.Remove(_selectedMarker);
                RemoveMarkerFromEntityMenu(_selectedMarker.Id);
                _selectedMarker = null;
                SetMenuVisible(_objectInfoMenu, false);
                _mainCamera.StopPointing();
                _changesMade++;
            }

            if (_selectedMarker != null && (Game.IsControlJustPressed(Control.PhoneCancel) || Game.IsControlJustPressed(Control.Attack)))
            {
                _selectedMarker = null;
                SetMenuVisible(_objectInfoMenu, false);
                _mainCamera.StopPointing();
                _changesMade++;
            }

            DrawButtons(_selectedButtons);
        }
    }
}
