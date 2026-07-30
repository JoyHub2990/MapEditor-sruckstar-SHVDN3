using System;
using System.Drawing;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using LemonUI.Elements;
using LemonUI.Menus;
using LemonUI.Scaleform;
using LemonUI.Tools;
using MapEditor.API;
using Control = GTA.Control;
using Font = GTA.UI.Font;

namespace MapEditor
{
    public partial class MapEditor
    {
        private InstructionalButtons _freelookButtons;
        private InstructionalButtons _selectedButtons;
        private InstructionalButtons _snappedButtons;
        private InstructionalButtons _stackingButtons;
        private InstructionalButtons _loopingButtons;

        private CustomSprite _crosshairSprite;
        private CustomSprite _crosshairBlueSprite;
        private CustomSprite _crosshairYellowSprite;

        private void BuildInstructionalButtons()
        {
            // LemonUI's scaleforms are Visible = false by default and Draw() is a no-op until it's set.
            _freelookButtons = new InstructionalButtons(
                new InstructionalButton(Translation.Translate("Spawn Prop"), Control.Enter),
                new InstructionalButton(Translation.Translate("Spawn Ped"), Control.FrontendPause),
                new InstructionalButton(Translation.Translate("Spawn Vehicle"), Control.NextCamera),
                new InstructionalButton(Translation.Translate("Spawn Marker"), Control.Phone),
                new InstructionalButton(Translation.Translate("Spawn Pickup"), Control.ThrowGrenade),
                new InstructionalButton(Translation.Translate("Move Entity"), Control.Aim),
                new InstructionalButton(Translation.Translate("Select Entity"), Control.Attack),
                // Control.Duck is the LCTRL binding on keyboard, which is the key IsMultiSelectKeyDown() looks for.
                new InstructionalButton(Translation.Translate("Add to Selection"), Control.Duck),
                new InstructionalButton(Translation.Translate("Copy Entity"), Control.LookBehind),
                // The same key stars the highlighted model in the object picker.
                new InstructionalButton(Translation.Translate("Favorite Entity"), Control.Context),
                new InstructionalButton(Translation.Translate("Delete Entity"), Control.CreatorDelete))
            {
                Visible = true,
            };
            _freelookButtons.Update();

            _selectedButtons = new InstructionalButtons(
                new InstructionalButton("", Control.MoveLeftRight),
                new InstructionalButton("", Control.MoveUpDown),
                new InstructionalButton("", Control.FrontendRb),
                new InstructionalButton(Translation.Translate("Move Entity"), Control.FrontendLb),
                new InstructionalButton(Translation.Translate("Switch to Rotation"), Control.Duck),
                new InstructionalButton(Translation.Translate("Copy Entity"), Control.LookBehind),
                new InstructionalButton(Translation.Translate("Delete Entity"), Control.CreatorDelete),
                new InstructionalButton(Translation.Translate("Accept"), Control.Attack))
            {
                Visible = true,
            };
            _selectedButtons.Update();

            _snappedButtons = new InstructionalButtons(
                new InstructionalButton("", Control.FrontendRb),
                new InstructionalButton(Translation.Translate("Rotate Entity"), Control.FrontendLb),
                new InstructionalButton(Translation.Translate("Delete Entity"), Control.CreatorDelete),
                new InstructionalButton(Translation.Translate("Accept"), Control.Attack))
            {
                Visible = true,
            };
            _snappedButtons.Update();

            _stackingButtons = new InstructionalButtons(
                new InstructionalButton(Translation.Translate("Exit Tool"), Control.PhoneCancel),
                new InstructionalButton(Translation.Translate("Abort Current Stacking"), Control.CreatorDelete),
                new InstructionalButton(Translation.Translate("Multiplier"), Control.Sprint))
            {
                Visible = true,
            };
            _stackingButtons.Update();

            _loopingButtons = new InstructionalButtons(
                new InstructionalButton(Translation.Translate("Exit Tool"), Control.PhoneCancel),
                new InstructionalButton(Translation.Translate("Abort Current Looping"), Control.CreatorDelete),
                new InstructionalButton(Translation.Translate("Multiplier"), Control.Sprint))
            {
                Visible = true,
            };
            _loopingButtons.Update();
        }

        /// <summary>
        /// The stacking and looping tools each own the object they were opened on and drive their own
        /// controls, so whatever else the editor would do with those controls has to stand down.
        /// </summary>
        private bool IsGeneratorToolActive
        {
            get { return _stackingBase != null || _loopingBase != null; }
        }

        private void DrawButtons(InstructionalButtons buttons)
        {
            if (!_settings.InstructionalButtons) return;
            buttons.Draw();
        }

		public void OnTick(object sender, EventArgs e)
		{
			// Before anything that spawns or places anything, including the first-tick load below: every map
			// that goes into the world this frame is streamed against the same spot.
			SmartStreaming.BeginTick(CurrentStreamingOrigin);

			// The maps flagged for autoloading, plus anything left in the legacy "AutoloadMaps" folder, and then
			// the map the player was editing when the script last went down, if it went down with one open.
			if (!_hasLoaded)
			{
				AutoloadedMaps.LoadAll();
				RestoreSession();
				_hasLoaded = true;
			}
			_menuPool.Process();
			PropStreamer.Tick();
			AutoloadedMaps.Tick();
			ProcessSmartStreaming();
			WarmObjectRows();

			if (PropStreamer.EntityCount > 0 || PropStreamer.RemovedObjects.Count > 0 || PropStreamer.Markers.Count > 0 || PropStreamer.Pickups.Count > 0)
			{
				_currentEntitiesItem.Enabled = true;
				_currentEntitiesItem.Description = "";
			}
			else
			{
				_currentEntitiesItem.Enabled = false;
				_currentEntitiesItem.Description = Translation.Translate("There are no current entities.");
			}

			if (AutoloadedMaps.Any)
			{
				_unloadAutoloadedItem.Enabled = true;
				_unloadAutoloadedItem.AltTitle = AutoloadedMaps.MapCount.ToString();
			}
			else
			{
				_unloadAutoloadedItem.Enabled = false;
				_unloadAutoloadedItem.AltTitle = "";
			}

			if (ModManager.HasMods)
			{
				_externalModItem.Enabled = true;
				_externalModItem.Description = Translation.Translate("Hand the map over to another mod instead of saving it to a file.");
			}
			else
			{
				_externalModItem.Enabled = false;
				_externalModItem.Description = Translation.Translate("No external mods are connected to Map Editor.");
			}

			if (Game.IsControlPressed(Control.LookBehind) && Game.IsControlJustPressed(Control.FrontendLb) && !_menuPool.AreAnyVisible && _settings.Gamepad)
			{
				_mainMenu.Visible = !_mainMenu.Visible;
			}

		    if (_settings.AutosaveInterval != -1 && DateTime.Now.Subtract(_lastAutosave).Minutes >= _settings.AutosaveInterval && PropStreamer.EntityCount > 0 && _changesMade > 0 && PropStreamer.EntityCount != _loadedEntities)
		    {
                SaveMap("Autosave.xml", MapSerializer.Format.NormalXml);
		        _lastAutosave = DateTime.Now;
		    }

		    if (_currentObjectsMenu.Visible)
		    {
                if (Game.IsControlJustPressed(Control.PhoneLeft))
                {
                    if (_currentObjectsMenu.SelectedIndex <= 100)
                        _currentObjectsMenu.SelectedIndex = 0;
                    else
                        _currentObjectsMenu.SelectedIndex -= 100;
                }

                if (Game.IsControlJustPressed(Control.PhoneRight))
                {
                    if (_currentObjectsMenu.SelectedIndex >= _currentObjectsMenu.Items.Count - 101)
                        _currentObjectsMenu.SelectedIndex = _currentObjectsMenu.Items.Count - 1;
                    else
                        _currentObjectsMenu.SelectedIndex += 100;
                }
            }

            //
            // BELOW ONLY WHEN MAP EDITOR IS ACTIVE
            //

            if (!IsInFreecam)
            {
                // A tick late on purpose: the freecam hands the player back down into the interior it was
                // holding (see ReturnPlayerToGround), and letting go of them in the same frame would be letting
                // go before the game has had a chance to notice anyone is standing in one.
                ReleaseHeldInteriors();
                return;
            }

            // Before anything that can end the tick early, so that the interiors around the camera are held
            // open just the same while the object picker is up over them.
            HoldInteriorsAroundCamera();

            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)Control.CharacterWheel);
			Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)Control.SelectWeapon);
			Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)Control.FrontendPause);
			Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)Control.NextCamera);
			Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)Control.Phone);
			Function.Call(Hash.HIDE_HUD_AND_RADAR_THIS_FRAME);

			// The object picker has to stay out until the player has left the tool that owns the object.
			// The two below it are already held off by AreAnyVisible.
			if (Game.IsControlJustPressed(Control.Enter) && !_isChoosingObject && !IsGeneratorToolActive)
			{
			    BeginChoosingObject(ObjectTypes.Prop);
			}

			if (Game.IsControlJustPressed(Control.NextCamera) && !_isChoosingObject && !IsGeneratorToolActive)
			{
			    BeginChoosingObject(ObjectTypes.Vehicle);
			}

            if (Game.IsControlJustPressed(Control.FrontendPause) && !_isChoosingObject && !IsGeneratorToolActive)
			{
			    BeginChoosingObject(ObjectTypes.Ped);
			}

			if (Game.IsControlJustPressed(Control.Phone) && !_isChoosingObject && !_menuPool.AreAnyVisible)
			{
				ClearMultiSelection();
				_snappedProp = null;
				_selectedProp = null;
				_snappedMarker = null;
				_selectedMarker = null;

				var tmpMark = new Marker()
				{
					Red = Color.Yellow.R,
					Green = Color.Yellow.G,
					Blue = Color.Yellow.B,
					Alpha = Color.Yellow.A,
					Scale = new Vector3(0.75f, 0.75f, 0.75f),
					Type =  MarkerType.UpsideDownCone,
					Position = VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, Game.Player.Character),
					Id = _markerCounter,
				};
				PropStreamer.Markers.Add(tmpMark);
				_snappedMarker = tmpMark;
				_markerCounter++;
			    _changesMade++;
				AddItemToEntityMenu(_snappedMarker);
			}

            if (Game.IsControlJustPressed(Control.ThrowGrenade) && !_isChoosingObject && !_menuPool.AreAnyVisible)
            {
                ClearMultiSelection();
                _snappedProp = null;
                _selectedProp = null;
                _snappedMarker = null;
                _selectedMarker = null;

                var pickup = PropStreamer.CreatePickup(new Model((int) ObjectDatabase.PickupHash.Parachute),
                    VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation,
                        Game.Player.Character), 0f, 100, false);

                _changesMade++;
                AddItemToEntityMenu(pickup);
                _snappedProp = Compat.Ent(pickup.ObjectHandle);
            }

            if (_isChoosingObject)
            {
                // Every way out of the picker runs from a menu's Closed handler, so a picker with no menu on
                // screen is a state nothing can take the player out of: the freecam controls are swallowed by
                // the return below and BeginChoosingObject refuses to reopen while this is set. Whatever hid
                // the menu, hand the player back their camera rather than stranding them in it.
                if (!_categoriesMenu.Visible && !_objectsMenu.Visible && !_searchMenu.Visible)
                {
                    LeaveObjectPicker();
                }
                else
                {
                    ProcessObjectPreview();
                    return;
                }
            }

            World.RenderingCamera = _mainCamera;

			if (_settings.PropCounterDisplay)
			    DrawEntityCounter();

			Entity hitEnt = VectorExtensions.RaycastEntity(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation);

			if (_settings.CrosshairType == CrosshairType.Crosshair)
			{
			    DrawCrosshair(hitEnt);
			}

			if (_settings.WorldObjectNames)
			    DrawWorldObjectNames(hitEnt);

            Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 0);
            Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, (int)Control.LookLeftRight);
            Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, (int)Control.LookUpDown);
            Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, (int)Control.CursorX);
            Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, (int)Control.CursorY);
            Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, (int)Control.FrontendPauseAlternate);

            var mouseX = Function.Call<float>(Hash.GET_CONTROL_NORMAL, 0, (int)Control.LookLeftRight);
			var mouseY = Function.Call<float>(Hash.GET_CONTROL_NORMAL, 0, (int)Control.LookUpDown);

			mouseX *= -1;
			mouseY *= -1;

		    switch (Game.LastInputMethod)
		    {
		        case InputMethod.MouseAndKeyboard:
		            mouseX *= _settings.CameraSensivity;
		            mouseY *= _settings.CameraSensivity;
		            break;
		        case InputMethod.GamePad:
		            mouseX *= _settings.GamepadCameraSensitivity;
		            mouseY *= _settings.GamepadCameraSensitivity;
		            break;
		    }

            float movementModifier = 1f;
            if (Game.IsControlPressed(Control.Sprint))
                movementModifier = 5f;
            else if (Game.IsControlPressed(Control.CharacterWheel))
                movementModifier = 0.3f;

		    switch (Game.LastInputMethod)
		    {
                case InputMethod.MouseAndKeyboard:
		            float baseSpeed = _settings.KeyboardMovementSensitivity / 30f; // 1 - 60, baseSpeed = 0.03 - 2
		            movementModifier *= baseSpeed;
		            break;
                case InputMethod.GamePad:
                    float gamepadSpeed = _settings.GamepadMovementSensitivity / 30f; // 1 - 60, baseSpeed = 0.03 - 2
                    movementModifier *= gamepadSpeed;
                    break;
		    }

            float modifier = 1f;
            if (Game.IsControlPressed(Control.Sprint))
                modifier = 5f;
            else if (Game.IsControlPressed(Control.CharacterWheel))
                modifier = 0.3f;

			// The base object stays put while its stack or its loop is being configured, so the tool takes
			// the controls that would otherwise move the selected object.
			if (_stackingBase != null)
			{
			    ProcessStacking();
			}
			else if (_loopingBase != null)
			{
			    ProcessLooping();
			}
			else if (_selectedProp == null && _selectedMarker == null)
			{
			    ProcessFreelook(hitEnt, mouseX, mouseY, movementModifier, modifier);
			}
            else if(_selectedProp != null)
            {
                ProcessSelectedProp(modifier);
            }
			else if (_selectedMarker != null)
			{
			    ProcessSelectedMarker(modifier);
			}
		}

        private void BeginChoosingObject(ObjectTypes type)
        {
            var oldType = _currentObjectType;
            _currentObjectType = type;
            bool sameType = oldType == _currentObjectType;

            if (!sameType)
            {
                // Categories are per-type, so switching type invalidates both the list and the choice.
                RedrawCategoriesMenu(_currentObjectType);
                _currentCategory = null;
                _dlcFilterItem = null;
                _objectsMenu.Clear();
            }

            _isChoosingObject = true;
            ClearMultiSelection();
            _snappedProp = null;
            _selectedProp = null;
            CloseAllMenus();

            if (_quitWithSearchVisible && sameType)
            {
                SetMenuVisible(_searchMenu, true);
                OnIndexChange(_searchMenu, new SelectedEventArgs(_searchMenu.SelectedIndex, 0));
            }
            else if (_currentCategory != null && sameType)
            {
                // Placing several objects from one category is the common case, so drop straight back
                // into it instead of making the player pick it again every time.
                SetMenuVisible(_objectsMenu, true);
                OnIndexChange(_objectsMenu, new SelectedEventArgs(_objectsMenu.SelectedIndex, 0));
            }
            else
            {
                SetMenuVisible(_categoriesMenu, true);
            }
        }

        private void ProcessObjectPreview()
        {
            if (_previewProp != null)
            {
                _previewProp.Rotation = _previewProp.Rotation + (_zAxis ? new Vector3(0f, 0f, 2.5f) : new Vector3(2.5f, 0f, 0f));
                if (_zAxis && IsPed(_previewProp))
                    _previewProp.Heading = _previewProp.Rotation.Z;
                DrawEntityBox(_previewProp, Color.White);
            }

            if (Game.IsControlJustPressed(Control.SelectWeapon))
                _zAxis = !_zAxis;

            if (_objectPreviewCamera == null)
            {
                _objectPreviewCamera = World.CreateCamera(new Vector3(1200.016f, 4000.998f, 86.05062f), new Vector3(0f, 0f, 0f), 60f);
                _objectPreviewCamera.PointAt(_objectPreviewPos);
            }

            if (Game.IsControlPressed(Control.MoveDownOnly))
                _objectPreviewCamera.Position -= new Vector3(0f, 0.5f, 0f);

            if (Game.IsControlPressed(Control.MoveUpOnly))
                _objectPreviewCamera.Position += new Vector3(0f, 0.5f, 0f);

            // LemonUI scrolls a list item's value with these same two controls, so paging has to stand
            // down while the cursor sits on the DLC filter, or one press would do both.
            bool onDlcFilter = _dlcFilterItem != null && _objectsMenu.SelectedIndex == 0;

            // Paging stops at the first model rather than at the filter above it, so that it always lands
            // on something to preview.
            int firstObject = _dlcFilterItem != null ? 1 : 0;

            // Paging by 100 only makes sense on the object list; the category list is short.
            if (_objectsMenu.Visible && !onDlcFilter && Game.IsControlJustPressed(Control.PhoneLeft))
            {
                if (_objectsMenu.SelectedIndex - 100 <= firstObject)
                    _objectsMenu.SelectedIndex = firstObject;
                else
                    _objectsMenu.SelectedIndex -= 100;
                OnIndexChange(_objectsMenu, new SelectedEventArgs(_objectsMenu.SelectedIndex, 0));
            }

            if (_objectsMenu.Visible && !onDlcFilter && Game.IsControlJustPressed(Control.PhoneRight))
            {
                if (_objectsMenu.SelectedIndex >= _objectsMenu.Items.Count - 101)
                    _objectsMenu.SelectedIndex = _objectsMenu.Items.Count - 1;
                else
                    _objectsMenu.SelectedIndex += 100;
                OnIndexChange(_objectsMenu, new SelectedEventArgs(_objectsMenu.SelectedIndex, 0));
            }

            World.RenderingCamera = _objectPreviewCamera;

            if (Game.IsControlJustPressed(Control.Context))
                ToggleFavorite();

            if (Game.IsControlJustPressed(Control.Jump))
            {
                string query = Compat.GetUserInput(255);
                if (String.IsNullOrWhiteSpace(query)) return;
                if (query[0] == ' ')
                    query = query.Remove(0, 1);
                SetMenuVisible(_objectsMenu, false);
                SetMenuVisible(_categoriesMenu, false);
                RedrawSearchMenu(query, _currentObjectType);
                if (_searchMenu.Items.Count != 0)
                    OnIndexChange(_searchMenu, new SelectedEventArgs(0, 0));
                _searchMenu.Name = "~b~" + Translation.Translate("SEARCH RESULTS FOR") + " \"" + query.ToUpper() + "\"";
                SetMenuVisible(_searchMenu, true);
            }
        }

        private void DrawCrosshair(Entity hitEnt)
        {
            // CustomSprite.ScaledDraw() maps onto a Screen.ScaledWidth x Screen.Height (720) space,
            // not onto the real resolution.
            var size = new SizeF(30, 30);
            var pos = new PointF(Screen.ScaledWidth * 0.5f, Screen.Height * 0.5f);

            if (_crosshairSprite == null)
            {
                _crosshairSprite = new CustomSprite(_crosshairPath, size, pos) { Centered = true };
                _crosshairBlueSprite = new CustomSprite(_crosshairBluePath, size, pos) { Centered = true };
                _crosshairYellowSprite = new CustomSprite(_crosshairYellowPath, size, pos) { Centered = true };
            }

            var cross = _crosshairSprite;
            if (hitEnt != null && hitEnt.Handle != 0 && !PropStreamer.GetAllHandles().Contains(hitEnt.Handle))
                cross = _crosshairBlueSprite;
            else if (hitEnt != null && hitEnt.Handle != 0 && PropStreamer.GetAllHandles().Contains(hitEnt.Handle))
                cross = _crosshairYellowSprite;

            cross.Position = pos;
            cross.ScaledDraw();
        }

        private void DrawEntityCounter()
        {
            const int interval = 45;
            var bottomRight = SafeZone.BottomRight;
            var background = Color.FromArgb(180, 255, 255, 255);

            void Row(int slot, string label, string value)
            {
                float y = bottomRight.Y - (90 + (slot * interval));
                float valueY = bottomRight.Y - (102 + (slot * interval));
                float bgY = bottomRight.Y - (100 + (slot * interval));

                new ScaledTexture(new PointF(bottomRight.X - 248, bgY), new SizeF(250, 37), "timerbars", "all_black_bg")
                {
                    Color = background,
                }.Draw();

                new ScaledText(new PointF(bottomRight.X - 90, y), label, 0.3f, Font.ChaletLondon)
                {
                    Alignment = Alignment.Right,
                    Color = Color.White,
                }.Draw();

                new ScaledText(new PointF(bottomRight.X - 20, valueY), value, 0.5f, Font.ChaletLondon)
                {
                    Alignment = Alignment.Right,
                    Color = Color.White,
                }.Draw();
            }

            Row(5, Translation.Translate("PICKUPS"), PropStreamer.Pickups.Count.ToString());
            Row(4, Translation.Translate("MARKERS"), PropStreamer.Markers.Count.ToString());
            Row(3, Translation.Translate("WORLD"), PropStreamer.RemovedObjects.Count.ToString());
            // The map's own count, not the world's: what smart streaming has taken out of the world is still
            // part of the map, and a counter that fell as the player flew away would be reporting the wrong
            // thing entirely.
            Row(2, Translation.Translate("PROPS"), PropStreamer.PropCount.ToString());
            Row(1, Translation.Translate("VEHICLES"), PropStreamer.VehicleCount.ToString());
            Row(0, Translation.Translate("PEDS"), PropStreamer.PedCount.ToString());
        }

        private void DrawEntityBox(Entity ent, Color color)
        {
            if(ent == null || (_settings.BoundingBox.HasValue && !_settings.BoundingBox.Value)) return;

            var (min, max) = ent.Model.Dimensions;
            var modelSize = max - min;
            modelSize = new Vector3(modelSize.X/2, modelSize.Y/2, modelSize.Z/2);

            var b1 = GetEntityOffset(ent, new Vector3(-modelSize.X, -modelSize.Y, -modelSize.Z * 0));
            var b2 = GetEntityOffset(ent, new Vector3(-modelSize.X, modelSize.Y, -modelSize.Z * 0));
            var b3 = GetEntityOffset(ent, new Vector3(modelSize.X, -modelSize.Y, -modelSize.Z * 0));
            var b4 = GetEntityOffset(ent, new Vector3(modelSize.X, modelSize.Y, -modelSize.Z * 0));

            var a1 = GetEntityOffset(ent, new Vector3(-modelSize.X, -modelSize.Y, modelSize.Z * 2));
            var a2 = GetEntityOffset(ent, new Vector3(-modelSize.X, modelSize.Y, modelSize.Z * 2));
            var a3 = GetEntityOffset(ent, new Vector3(modelSize.X, -modelSize.Y, modelSize.Z * 2));
            var a4 = GetEntityOffset(ent, new Vector3(modelSize.X, modelSize.Y, modelSize.Z * 2));

            World.DrawLine(a1, a2, color);
            World.DrawLine(a2, a4, color);
            World.DrawLine(a4, a3, color);
            World.DrawLine(a3, a1, color);

            World.DrawLine(b1, b2, color);
            World.DrawLine(b2, b4, color);
            World.DrawLine(b4, b3, color);
            World.DrawLine(b3, b1, color);

            World.DrawLine(a1, b1, color);
            World.DrawLine(a2, b2, color);
            World.DrawLine(a3, b3, color);
            World.DrawLine(a4, b4, color);
        }

        private Vector3 GetEntityOffset(Entity ent, Vector3 offset)
        {
            return Function.Call<Vector3>(Hash.GET_OFFSET_FROM_ENTITY_IN_WORLD_COORDS, ent.Handle, offset.X, offset.Y, offset.Z);
        }
    }
}
